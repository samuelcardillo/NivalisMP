$ErrorActionPreference='Stop'
$gameRoot=(Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$outDir=Join-Path $PSScriptRoot ('map-catchup-results-'+[DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss-fff'))
[void][IO.Directory]::CreateDirectory($outDir)
$refs=[Collections.Generic.List[Microsoft.CodeAnalysis.MetadataReference]]::new()
foreach($file in Get-ChildItem -LiteralPath (Join-Path $gameRoot 'dotnet') -Filter '*.dll'){
 try{[void][Reflection.AssemblyName]::GetAssemblyName($file.FullName)}catch{continue}
 $refs.Add([Microsoft.CodeAnalysis.MetadataReference]::CreateFromFile($file.FullName))
}
$options=[Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions]::new([Microsoft.CodeAnalysis.OutputKind]::DynamicallyLinkedLibrary)
$compile=[Microsoft.CodeAnalysis.CSharp.CSharpCompilation]::Create('MapCatchupProgressTests_'+[Guid]::NewGuid().ToString('N')).WithOptions($options).AddReferences($refs.ToArray())
$sources=@(Get-ChildItem -LiteralPath (Join-Path $gameRoot 'MultiplayerDev/Shared') -Filter '*.cs'|ForEach-Object FullName)+@(Get-ChildItem -LiteralPath (Join-Path $gameRoot 'MultiplayerDev/Server') -Filter '*.cs'|ForEach-Object FullName)+(Join-Path $PSScriptRoot 'MapCatchupProgressTests.cs')
foreach($source in $sources){$compile=$compile.AddSyntaxTrees([Microsoft.CodeAnalysis.SyntaxTree[]]@([Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText([IO.File]::ReadAllText($source),$null,$source)))}
$stream=[IO.MemoryStream]::new()
try{
 $result=$compile.Emit($stream)
 if(!$result.Success){$result.Diagnostics|Where-Object Severity -eq Error|ForEach-Object ToString;throw 'Map catch-up service regression compilation failed.'}
 $assembly=[Reflection.Assembly]::Load($stream.ToArray())
 $count=$assembly.GetType('MapCatchupProgressTests').GetMethod('Run').Invoke($null,@())
 [pscustomobject]@{Passed=$true;Assertions=$count;NativeRuntimeStarted=$false}|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $outDir 'result.json')
}finally{$stream.Dispose()}
$sources|ForEach-Object{Get-FileHash -LiteralPath $_ -Algorithm SHA256|Select-Object Path,Hash}|ConvertTo-Json -Depth 3|Set-Content -LiteralPath (Join-Path $outDir 'source-hashes.json')
"Evidence: $outDir"
