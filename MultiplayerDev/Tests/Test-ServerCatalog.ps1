$ErrorActionPreference='Stop'
$gameRoot=(Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$refs=[Collections.Generic.List[Microsoft.CodeAnalysis.MetadataReference]]::new()
foreach($file in Get-ChildItem -LiteralPath (Join-Path $gameRoot 'dotnet') -Filter '*.dll'){
 try{[void][Reflection.AssemblyName]::GetAssemblyName($file.FullName)}catch{continue}
 $refs.Add([Microsoft.CodeAnalysis.MetadataReference]::CreateFromFile($file.FullName))
}
$options=[Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions]::new([Microsoft.CodeAnalysis.OutputKind]::ConsoleApplication)
$compilation=[Microsoft.CodeAnalysis.CSharp.CSharpCompilation]::Create('ServerCatalogTests').WithOptions($options).AddReferences($refs.ToArray())
$paths=@(Get-ChildItem -LiteralPath (Join-Path $gameRoot 'MultiplayerDev/Shared') -Filter '*.cs' | ForEach-Object FullName)
$paths+=Join-Path $gameRoot 'MultiplayerDev/Source/ServerCatalog.cs'
$paths+=Join-Path $PSScriptRoot 'ServerCatalogTests.cs'
foreach($path in $paths){$tree=[Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText([IO.File]::ReadAllText($path),$null,$path);$compilation=$compilation.AddSyntaxTrees([Microsoft.CodeAnalysis.SyntaxTree[]]@($tree))}
# Extract the actual production DTO syntax; the test never substitutes a copy.
$menuPath=Join-Path $gameRoot 'MultiplayerDev/Source/MultiplayerMenu.cs'
$menuTree=[Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText([IO.File]::ReadAllText($menuPath))
$dto=$menuTree.GetRoot().DescendantNodes() | Where-Object {$_ -is [Microsoft.CodeAnalysis.CSharp.Syntax.ClassDeclarationSyntax] -and $_.Identifier.ValueText -eq 'MultiplayerServerEntry'}
$dtoTree=[Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText(('using System; namespace NivalisTogether {'+$dto.ToFullString()+'}'))
$compilation=$compilation.AddSyntaxTrees([Microsoft.CodeAnalysis.SyntaxTree[]]@($dtoTree))
$outDir=Join-Path $gameRoot ('MultiplayerDev/inspection/catalog-tests/'+[DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss-fff'))
[void][IO.Directory]::CreateDirectory($outDir)
$stream=[IO.MemoryStream]::new()
$result=$compilation.Emit($stream)
if(!$result.Success){$result.Diagnostics|ForEach-Object ToString;throw 'Catalog test compilation failed'}
$assembly=[Reflection.Assembly]::Load($stream.ToArray());$stream.Dispose()
$result=$assembly.EntryPoint.Invoke($null,[object[]]@(,[string[]]@((Join-Path $outDir 'fixtures'))))
if($result -ne 0){throw 'Catalog regression failed'}
"Evidence folder: $outDir"
