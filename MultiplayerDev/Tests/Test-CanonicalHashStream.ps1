[CmdletBinding()]
param([Parameter(Mandatory)][string]$BaselineSource,[string]$CandidateSource,[ValidateRange(1,10)][int]$BenchmarkIterations=3)
$ErrorActionPreference='Stop'
$devRoot=Split-Path -Parent $PSScriptRoot;$gameRoot=Split-Path -Parent $devRoot
if(!$CandidateSource){$CandidateSource=Join-Path $devRoot 'Source/SharedEconomyReplication.cs'}
$protocolSource=Join-Path $devRoot 'Shared/DedicatedProtocol.cs'
$inputs=@($BaselineSource,$CandidateSource,$protocolSource)|ForEach-Object{[pscustomobject]@{Path=(Resolve-Path -LiteralPath $_).Path;Sha256=(Get-FileHash -LiteralPath $_).Hash}}
$oldTree=[Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText([IO.File]::ReadAllText($BaselineSource)).GetRoot()
$newTree=[Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText([IO.File]::ReadAllText($CandidateSource)).GetRoot()
$protocolTree=[Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText([IO.File]::ReadAllText($protocolSource)).GetRoot()
$methodNames=@('ContentHash','ItemContentHash','ValidateVendor','VendorDetails','WriteCanonicalItems')
# Explicit construction avoids PowerShell array/string operator precedence.
function Make-Class($tree,[string]$name,[bool]$candidate){
 $rep=$tree.DescendantNodes()|Where-Object{$_-is[Microsoft.CodeAnalysis.CSharp.Syntax.ClassDeclarationSyntax]-and$_.Identifier.ValueText-eq'SharedEconomyReplication'}
 $methods=@($rep.Members|Where-Object{$_-is[Microsoft.CodeAnalysis.CSharp.Syntax.MethodDeclarationSyntax]-and$_.Identifier.ValueText-in$methodNames})
 if($methods.Count-ne8){throw "Expected8 selected actual methods, found$($methods.Count)"}
 $constants=@($rep.Members|Where-Object{$_-is[Microsoft.CodeAnalysis.CSharp.Syntax.FieldDeclarationSyntax]-and$_.Declaration.Variables.Identifier.ValueText-contains'MaxStacks'})
 $nested=@($rep.Members|Where-Object{$_-is[Microsoft.CodeAnalysis.CSharp.Syntax.ClassDeclarationSyntax]-and$_.Identifier.ValueText-eq'CanonicalHashStream'})
 if($candidate-and$nested.Count-ne1){throw 'Candidate stream is not ready'}
 $parts=[Collections.Generic.List[string]]::new();$parts.Add('internal static partial class '+$name+'{');foreach($node in @($constants)+@($methods)+@($nested)){$parts.Add($node.ToFullString())}
 $parts.Add('internal static HashResult Evaluate(SharedEconomyState state,bool map,CancellationToken cancellation=default){var hashes=map?new Dictionary<SharedItemStack[],string>():null;return new HashResult{Hash=ContentHash(state,hashes,cancellation),ItemHashes=hashes};} internal static string EvaluateItem(SharedItemStack[] stacks)=>ItemContentHash(stacks); }')
 $parts-join "`n"
}
$dto=@($oldTree.DescendantNodes()|Where-Object{$_-is[Microsoft.CodeAnalysis.CSharp.Syntax.ClassDeclarationSyntax]-and$_.Identifier.ValueText-in@('SharedItemStack','SharedVendorStock','SharedEconomyState')})
if($dto.Count-ne3){throw 'DTO extraction failed'}
$textMethod=$protocolTree.DescendantNodes()|Where-Object{$_-is[Microsoft.CodeAnalysis.CSharp.Syntax.MethodDeclarationSyntax]-and$_.Identifier.ValueText-eq'Text'}
$prefix='using System;using System.Collections.Generic;using System.IO;using System.Security.Cryptography;using System.Text.Json;using System.Threading;namespace CanonicalHashRegression {'
$common=$prefix+(@($dto|ForEach-Object{$_.ToFullString()})-join "`n")+'internal static class DedicatedProtocol{'+$textMethod.ToFullString()+'} internal sealed class HashResult{internal string Hash;internal Dictionary<SharedItemStack[],string> ItemHashes;} }'
$old=$prefix+(Make-Class $oldTree 'OldHash' $false)+'}'
$new=$prefix+(Make-Class $newTree 'NewHash' $true)+'}'
$out=Join-Path $PSScriptRoot ('canonical-hash-regression-'+[DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss-fff'));[void][IO.Directory]::CreateDirectory($out)
foreach($pair in @(@('Common.extracted.cs',$common),@('Old.extracted.cs',$old),@('New.extracted.cs',$new))){[IO.File]::WriteAllText((Join-Path $out $pair[0]),$pair[1])}
$refs=[Collections.Generic.List[Microsoft.CodeAnalysis.MetadataReference]]::new()
foreach($file in Get-ChildItem -LiteralPath (Join-Path $gameRoot 'dotnet') -Filter '*.dll'){try{[void][Reflection.AssemblyName]::GetAssemblyName($file.FullName)}catch{continue};$refs.Add([Microsoft.CodeAnalysis.MetadataReference]::CreateFromFile($file.FullName))}
$options=[Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions]::new([Microsoft.CodeAnalysis.OutputKind]::ConsoleApplication).WithOptimizationLevel([Microsoft.CodeAnalysis.OptimizationLevel]::Release)
$compile=[Microsoft.CodeAnalysis.CSharp.CSharpCompilation]::Create('CanonicalHashRegression').WithOptions($options).AddReferences($refs.ToArray())
foreach($source in @($common,$old,$new,[IO.File]::ReadAllText((Join-Path $PSScriptRoot 'CanonicalHashStreamRegression.cs')))){$compile=$compile.AddSyntaxTrees([Microsoft.CodeAnalysis.SyntaxTree[]]@([Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText($source)))}
$assembly=Join-Path $out 'CanonicalHashRegression.dll';$stream=[IO.File]::Create($assembly)
try{$result=$compile.Emit($stream);if(!$result.Success){$result.Diagnostics|Where-Object Severity -eq Error|ForEach-Object ToString;throw 'Canonical hash regression compile failed'}}finally{$stream.Dispose()}
'{"runtimeOptions":{"tfm":"net8.0","framework":{"name":"Microsoft.NETCore.App","version":"8.0.0"},"rollForward":"LatestPatch"}}'|Set-Content -LiteralPath (Join-Path $out 'CanonicalHashRegression.runtimeconfig.json')
$tiered=$env:DOTNET_TieredCompilation
try{$env:DOTNET_TieredCompilation='0';& dotnet $assembly (Join-Path $out 'results.json') $BenchmarkIterations;if($LASTEXITCODE-ne0){throw 'Canonical hash regression failed'}}finally{$env:DOTNET_TieredCompilation=$tiered}
foreach($input in $inputs){if((Get-FileHash -LiteralPath $input.Path).Hash-ne$input.Sha256){throw 'Source changed while test ran; results not final'}}
$evidence=[pscustomobject]@{Utc=[DateTime]::UtcNow;Inputs=$inputs;TestSourceSha256=(Get-FileHash -LiteralPath (Join-Path $PSScriptRoot 'CanonicalHashStreamRegression.cs')).Hash;AssemblySha256=(Get-FileHash -LiteralPath $assembly).Hash;ActualProductionMethodsExtracted=$true;ProductionSourceModified=$false;CompileReferences='Bundled .NET6';Runtime='Installed .NET8';TieredCompilation=$false;ResultsSha256=(Get-FileHash -LiteralPath (Join-Path $out 'results.json')).Hash}
$evidence|ConvertTo-Json -Depth 8|Set-Content -LiteralPath (Join-Path $out 'evidence.json')
[pscustomobject]@{Directory=$out;EvidenceSha256=(Get-FileHash -LiteralPath (Join-Path $out 'evidence.json')).Hash;Results=Get-Content -LiteralPath (Join-Path $out 'results.json') -Raw|ConvertFrom-Json}
