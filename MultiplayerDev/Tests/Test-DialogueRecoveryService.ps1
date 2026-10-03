param()
$ErrorActionPreference='Stop'
$gameRoot=Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$refs=[Collections.Generic.List[Microsoft.CodeAnalysis.MetadataReference]]::new()
foreach($file in Get-ChildItem (Join-Path $gameRoot 'dotnet') -Filter '*.dll'){try{[void][Reflection.AssemblyName]::GetAssemblyName($file.FullName)}catch{continue};$refs.Add([Microsoft.CodeAnalysis.MetadataReference]::CreateFromFile($file.FullName))}
$options=[Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions]::new([Microsoft.CodeAnalysis.OutputKind]::ConsoleApplication).WithMainTypeName('DialogueRecoveryServiceTests')
$compilation=[Microsoft.CodeAnalysis.CSharp.CSharpCompilation]::Create('DialogueRecoveryServiceTests').WithOptions($options).AddReferences($refs.ToArray())
$sources=@(Get-ChildItem (Join-Path $gameRoot 'MultiplayerDev/Shared') -Filter '*.cs'|ForEach-Object FullName)
$sources+=@(Get-ChildItem (Join-Path $gameRoot 'MultiplayerDev/Server') -Filter '*.cs'|ForEach-Object FullName)
$sources+=@(Join-Path $PSScriptRoot 'DialogueRecoveryServiceFixture.cs'),@(Join-Path $PSScriptRoot 'DialogueRecoveryServiceTests.cs')
foreach($source in $sources){$compilation=$compilation.AddSyntaxTrees([Microsoft.CodeAnalysis.SyntaxTree[]]@([Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText([IO.File]::ReadAllText($source),$null,$source)))}
$outDir=Join-Path $PSScriptRoot ('dialogue-recovery-results-'+[DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss-fff'));[void][IO.Directory]::CreateDirectory($outDir)
$path=Join-Path $outDir 'DialogueRecoveryServiceTests.dll';$stream=[IO.File]::Create($path);try{$result=$compilation.Emit($stream)}finally{$stream.Dispose()}
if(!$result.Success){$result.Diagnostics|ForEach-Object ToString;throw 'Account service compile failed'}
'{"runtimeOptions":{"tfm":"net6.0","framework":{"name":"Microsoft.NETCore.App","version":"6.0.0"},"rollForward":"Major"}}'|Set-Content -LiteralPath (Join-Path $outDir 'DialogueRecoveryServiceTests.runtimeconfig.json')
& dotnet $path (Join-Path $outDir 'world')
if($LASTEXITCODE -ne 0){throw 'Account service regression failed'}
"Evidence: $outDir"
