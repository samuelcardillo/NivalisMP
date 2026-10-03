$ErrorActionPreference='Stop'
$gameRoot=(Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$source=Join-Path $gameRoot 'MultiplayerDev/Source/SharedEconomyReplication.cs'
$codec=Join-Path $gameRoot 'MultiplayerDev/Shared/BoundedSnapshotCodec.cs'
$tree=[Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText([IO.File]::ReadAllText($source)).GetRoot()
$classes=$tree.DescendantNodes()|Where-Object{$_ -is [Microsoft.CodeAnalysis.CSharp.Syntax.ClassDeclarationSyntax] -and $_.Identifier.ValueText -in @('SharedEconomyState','SharedVendorStock','SharedItemStack')}
if($classes.Count-ne3){throw 'Actual DTO extraction failed.'}
$dtos='using System;namespace NivalisTogether.Dedicated {'+($classes|ForEach-Object ToFullString)-join "`n"
$dtos+='}'
$codecTree=[Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText([IO.File]::ReadAllText($codec)).GetRoot()
$codecClass=$codecTree.DescendantNodes()|Where-Object{$_ -is [Microsoft.CodeAnalysis.CSharp.Syntax.ClassDeclarationSyntax] -and $_.Identifier.ValueText -eq 'BoundedSnapshotCodec'}
$baseline='using System;using System.IO;using System.IO.Compression;using System.Security.Cryptography;using System.Text.Json;namespace NivalisTogether.Dedicated {'+$codecClass.ToFullString().Replace('BoundedSnapshotCodec','BaselineSnapshotCodec').Replace('DedicatedProtocol.Json','ExperimentOptions.Baseline')+'}'
$refs=[Collections.Generic.List[Microsoft.CodeAnalysis.MetadataReference]]::new()
foreach($file in Get-ChildItem -LiteralPath (Join-Path $gameRoot 'dotnet') -Filter '*.dll'){
 try{[void][Reflection.AssemblyName]::GetAssemblyName($file.FullName)}catch{continue}
 $refs.Add([Microsoft.CodeAnalysis.MetadataReference]::CreateFromFile($file.FullName))
}
$options=[Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions]::new([Microsoft.CodeAnalysis.OutputKind]::ConsoleApplication).WithOptimizationLevel([Microsoft.CodeAnalysis.OptimizationLevel]::Release)
$compile=[Microsoft.CodeAnalysis.CSharp.CSharpCompilation]::Create('Int32ArrayJsonTests').WithOptions($options).AddReferences($refs.ToArray())
$sources=@(Get-ChildItem -LiteralPath (Join-Path $gameRoot 'MultiplayerDev/Shared') -Filter '*.cs'|ForEach-Object FullName)+(Join-Path $PSScriptRoot 'Int32ArrayJsonTests.cs')
foreach($path in $sources){$compile=$compile.AddSyntaxTrees([Microsoft.CodeAnalysis.SyntaxTree[]]@([Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText([IO.File]::ReadAllText($path),$null,$path)))}
foreach($text in @($dtos,$baseline)){$compile=$compile.AddSyntaxTrees([Microsoft.CodeAnalysis.SyntaxTree[]]@([Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText($text)))}
$folder=Join-Path $PSScriptRoot ('int-array-json-results-'+[DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss-fff'))
[void][IO.Directory]::CreateDirectory($folder)
$dll=Join-Path $folder 'Int32ArrayJsonTests.dll';$stream=[IO.File]::Create($dll)
try{$result=$compile.Emit($stream)}finally{$stream.Dispose()}
if(!$result.Success){$result.Diagnostics|Where-Object Severity -eq Error|ForEach-Object ToString;throw 'Int32 array tests compile failed.'}
'{"runtimeOptions":{"tfm":"net6.0","framework":{"name":"Microsoft.NETCore.App","version":"6.0.0"},"rollForward":"Major"}}'|Set-Content -LiteralPath (Join-Path $folder 'Int32ArrayJsonTests.runtimeconfig.json')
@($source)+$sources|ForEach-Object{Get-FileHash -LiteralPath $_}|Select-Object Path,Hash|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $folder 'source-hashes.json')
& dotnet $dll (Join-Path $folder 'results.json')
if($LASTEXITCODE-ne0){throw 'Int32 array tests failed.'}
Write-Output $folder
