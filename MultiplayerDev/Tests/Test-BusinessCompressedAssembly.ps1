$ErrorActionPreference='Stop'
$devRoot=Split-Path -Parent $PSScriptRoot
$gameRoot=Split-Path -Parent $devRoot
$generated="using System;using System.IO;using System.Diagnostics;using System.Text.Json;using System.Threading;using System.Collections.Generic;`nnamespace NivalisTogether.Dedicated {`n"
foreach($family in @(@('BusinessStateReplication','BusinessStateChunk'),@('BusinessCraftStateReplication','BusinessCraftChunk'))){
    $tree=[Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText([IO.File]::ReadAllText((Join-Path $devRoot ('Source/'+$family[0]+'.cs')))).GetRoot()
    $method=@($tree.DescendantNodes()|Where-Object {$_ -is [Microsoft.CodeAnalysis.CSharp.Syntax.MethodDeclarationSyntax] -and (($_.Identifier.ValueText -eq 'Apply' -and $_.ParameterList.Parameters.Count -eq 1 -and $_.ParameterList.Parameters[0].Type.ToString() -eq 'WorldEvent') -or $_.Identifier.ValueText -in @('AcceptChunk','PrepareManaged'))})
    $dto=@($tree.DescendantNodes()|Where-Object {$_ -is [Microsoft.CodeAnalysis.CSharp.Syntax.ClassDeclarationSyntax] -and $_.Identifier.ValueText -eq $family[1]})
    $nested=@($tree.DescendantNodes()|Where-Object {$_ -is [Microsoft.CodeAnalysis.CSharp.Syntax.ClassDeclarationSyntax] -and $_.Identifier.ValueText -in @('EncodedBusinessUpdate','PreparedBusinessUpdate','EncodedCraftUpdate','PreparedCraftUpdate')})
    if($method.Count -ne 3 -or $dto.Count -ne 1 -or $nested.Count -ne 2){throw 'Exact production snapshot assembler, preparation, commit wrapper and chunk DTO required.'}
    $nestedSource=($nested|ForEach-Object ToFullString)-join"`n"
    # Framing-only fixture keeps actual synchronous Commit/Prepare/Accept. Native
    # incremental cursors are covered by their dedicated production extraction tests.
    foreach($member in $nested.DescendantNodes()|Where-Object{$_ -is [Microsoft.CodeAnalysis.CSharp.Syntax.MethodDeclarationSyntax] -and $_.Identifier.ValueText -in @('Advance','Abort')}){$nestedSource=$nestedSource.Replace($member.ToFullString(),'')}
    $nestedSource=$nestedSource.Replace(':IIncrementalPreparedWorldUpdate',':IPreparedWorldUpdate').Replace('private BusinessClientCursor cursor;','private object cursor;').Replace('private ClientCraftApply incremental;','private object incremental;')
    $generated+=$dto[0].ToFullString()+"`npublic static partial class "+$family[0]+" {`n"+$nestedSource+"`n"+(($method|ForEach-Object ToFullString)-join"`n")+"`n}`n"
}
$generated+="}`n"
$refs=[Collections.Generic.List[Microsoft.CodeAnalysis.MetadataReference]]::new()
foreach($file in Get-ChildItem -LiteralPath (Join-Path $gameRoot 'dotnet') -Filter '*.dll'){
    try{[void][Reflection.AssemblyName]::GetAssemblyName($file.FullName)}catch{continue}
    $refs.Add([Microsoft.CodeAnalysis.MetadataReference]::CreateFromFile($file.FullName))
}
$comp=[Microsoft.CodeAnalysis.CSharp.CSharpCompilation]::Create('BusinessCompressedAssembly'+[Guid]::NewGuid().ToString('N')).WithOptions([Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions]::new([Microsoft.CodeAnalysis.OutputKind]::DynamicallyLinkedLibrary)).AddReferences($refs.ToArray())
foreach($source in @($generated,[IO.File]::ReadAllText((Join-Path $PSScriptRoot 'BusinessCompressedAssemblyTests.cs')),[IO.File]::ReadAllText((Join-Path $devRoot 'Shared/ClientMutationBudget.cs')),[IO.File]::ReadAllText((Join-Path $devRoot 'Shared/BoundedSnapshotCodec.cs')),[IO.File]::ReadAllText((Join-Path $devRoot 'Source/PreparedWorldUpdate.cs')))){
    $comp=$comp.AddSyntaxTrees([Microsoft.CodeAnalysis.SyntaxTree[]]@([Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText($source)))
}
$stream=[IO.MemoryStream]::new()
try{
    $result=$comp.Emit($stream)
    if(!$result.Success){$result.Diagnostics|ForEach-Object ToString;throw 'Production compressed assembler fixture failed compilation.'}
    $assembly=[Reflection.Assembly]::Load($stream.ToArray())
    $checks=$assembly.GetType('NivalisTogether.Dedicated.BusinessCompressedAssemblyTests').GetMethod('Run').Invoke($null,$null)
}finally{$stream.Dispose()}
"PASS: $checks production business/craft compressed assembler checks; no Unity/native execution."
