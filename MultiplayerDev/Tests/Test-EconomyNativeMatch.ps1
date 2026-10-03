$ErrorActionPreference='Stop'
$devRoot=Split-Path -Parent $PSScriptRoot
$gameRoot=Split-Path -Parent $devRoot
$root=[Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText([IO.File]::ReadAllText((Join-Path $devRoot 'Source/SharedEconomyReplication.cs'))).GetRoot()
$layout=$root.DescendantNodes()|Where-Object {$_ -is [Microsoft.CodeAnalysis.CSharp.Syntax.ClassDeclarationSyntax] -and $_.Identifier.ValueText -eq 'ItemReadLayout'}
$listLayout=$root.DescendantNodes()|Where-Object {$_ -is [Microsoft.CodeAnalysis.CSharp.Syntax.ClassDeclarationSyntax] -and $_.Identifier.ValueText -eq 'ItemListReadLayout'}
$top=$root.DescendantNodes()|Where-Object {$_ -is [Microsoft.CodeAnalysis.CSharp.Syntax.MethodDeclarationSyntax] -and $_.Identifier.ValueText -eq 'TryMatchNativeItems'}
$native=$root.DescendantNodes()|Where-Object {$_ -is [Microsoft.CodeAnalysis.CSharp.Syntax.ClassDeclarationSyntax] -and $_.Identifier.ValueText -eq 'NativeItemReader'}
$array=$native.Members|Where-Object {$_ -is [Microsoft.CodeAnalysis.CSharp.Syntax.MethodDeclarationSyntax] -and $_.Identifier.ValueText -in @('TryMatches','TryObserveList','ArrayStart','ArrayLength','CopyPointers')}
if(!$layout -or !$listLayout -or !$top -or $array.Count -ne 5){throw 'Required production native comparison boundary missing.'}
$code='using System;using System.Runtime.InteropServices;using NativeItems=EconomyNativeMatchProbe.NativeList;public static partial class EconomyNativeMatchProbe{'+$layout.ToFullString()+$listLayout.ToFullString()+$top.ToFullString()+'private static partial class NativeItemReader{'+($array.ToFullString()-join "`n")+'}}'
$refs=[Collections.Generic.List[Microsoft.CodeAnalysis.MetadataReference]]::new()
foreach($file in Get-ChildItem -LiteralPath (Join-Path $gameRoot 'dotnet') -Filter '*.dll'){
 try{[void][Reflection.AssemblyName]::GetAssemblyName($file.FullName)}catch{continue}
 $refs.Add([Microsoft.CodeAnalysis.MetadataReference]::CreateFromFile($file.FullName))
}
$comp=[Microsoft.CodeAnalysis.CSharp.CSharpCompilation]::Create('EconomyNativeMatchProbe'+[Guid]::NewGuid().ToString('N')).WithOptions([Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions]::new([Microsoft.CodeAnalysis.OutputKind]::DynamicallyLinkedLibrary)).AddReferences($refs.ToArray())
foreach($source in @($code,[IO.File]::ReadAllText((Join-Path $PSScriptRoot 'EconomyNativeMatchTests.cs')))){$comp=$comp.AddSyntaxTrees([Microsoft.CodeAnalysis.SyntaxTree[]]@([Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText($source)))}
$stream=[IO.MemoryStream]::new()
try{$result=$comp.Emit($stream);if(!$result.Success){$result.Diagnostics|ForEach-Object ToString;throw 'Native exact comparison test compilation failed.'};$assembly=[Reflection.Assembly]::Load($stream.ToArray());$count=$assembly.GetType('EconomyNativeMatchProbe').GetMethod('Run').Invoke($null,$null)}finally{$stream.Dispose()}
"PASS: $count production allocation-free native equality/fallback checks with owned unmanaged records."
