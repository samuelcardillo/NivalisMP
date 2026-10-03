$ErrorActionPreference='Stop'
$devRoot=Split-Path -Parent $PSScriptRoot
$gameRoot=Split-Path -Parent $devRoot
$path=Join-Path $devRoot 'Source/SharedEconomyReplication.cs'
$tree=[Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText([IO.File]::ReadAllText($path)).GetRoot()
$layout=$tree.DescendantNodes()|Where-Object {$_ -is [Microsoft.CodeAnalysis.CSharp.Syntax.ClassDeclarationSyntax] -and $_.Identifier.ValueText -eq 'ItemReadLayout'}
$old=$layout.ToFullString()
$method=$layout.Members|Where-Object {$_ -is [Microsoft.CodeAnalysis.CSharp.Syntax.MethodDeclarationSyntax] -and $_.Identifier.ValueText -eq 'TryMatches'}
$baselineBody=@'
            internal bool TryMatches(IntPtr item, IntPtr expectedType, int[] expected)
            {
                if (expected == null || expected.Length != 4 || item == IntPtr.Zero || expectedType == IntPtr.Zero ||
                    Marshal.ReadIntPtr(item) != itemClass || Marshal.ReadIntPtr(item, typeOffset) != expectedType) return false;
                return Marshal.ReadInt32(item, valueOffset) == expected[0] &&
                    Marshal.ReadInt32(item, valueOffset + 4) == expected[1] &&
                    Marshal.ReadInt32(item, valueOffset + 8) == expected[2] &&
                    Marshal.ReadInt32(item, valueOffset + 12) == expected[3];
            }
'@
$candidateBody=@'
            internal bool TryMatches(IntPtr item, IntPtr expectedType, int[] expected)
            {
                if (expected == null || expected.Length != 4 || item == IntPtr.Zero || expectedType == IntPtr.Zero ||
                    Marshal.ReadIntPtr(item) != itemClass || Marshal.ReadIntPtr(item, typeOffset) != expectedType) return false;
                long first = unchecked((long)((ulong)(uint)expected[0] | ((ulong)(uint)expected[1] << 32)));
                long second = unchecked((long)((ulong)(uint)expected[2] | ((ulong)(uint)expected[3] << 32)));
                return Marshal.ReadInt64(item, valueOffset) == first && Marshal.ReadInt64(item, valueOffset + 8) == second;
            }
'@
$candidate=$old.Replace($method.ToFullString(),$candidateBody).Replace('ItemReadLayout','PairReadLayout')
if(!$candidate.Contains('!BitConverter.IsLittleEndian')){$candidate=$candidate.Replace('IntPtr.Size != 8','IntPtr.Size != 8 || !BitConverter.IsLittleEndian')}
$baseline=$old.Replace($method.ToFullString(),$baselineBody)
$code='using System;using System.Runtime.InteropServices;public static partial class EconomyNativeMatchBenchmark{'+$baseline+$candidate+'}'
$refs=[Collections.Generic.List[Microsoft.CodeAnalysis.MetadataReference]]::new()
foreach($file in Get-ChildItem -LiteralPath (Join-Path $gameRoot 'dotnet') -Filter '*.dll'){
 try{[void][Reflection.AssemblyName]::GetAssemblyName($file.FullName)}catch{continue}
 $refs.Add([Microsoft.CodeAnalysis.MetadataReference]::CreateFromFile($file.FullName))
}
$options=[Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions]::new([Microsoft.CodeAnalysis.OutputKind]::ConsoleApplication).WithOptimizationLevel([Microsoft.CodeAnalysis.OptimizationLevel]::Release)
$compile=[Microsoft.CodeAnalysis.CSharp.CSharpCompilation]::Create('EconomyNativeMatchBenchmark').WithOptions($options).AddReferences($refs.ToArray())
foreach($source in @($code,[IO.File]::ReadAllText((Join-Path $PSScriptRoot 'Benchmark-EconomyNativeMatch.cs')))){$compile=$compile.AddSyntaxTrees([Microsoft.CodeAnalysis.SyntaxTree[]]@([Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText($source)))}
$output=Join-Path $PSScriptRoot ('economy-match-benchmark-'+[DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss-fff'))
[void][IO.Directory]::CreateDirectory($output)
$assembly=Join-Path $output 'EconomyNativeMatchBenchmark.dll';$stream=[IO.File]::Create($assembly)
try{$result=$compile.Emit($stream);if(!$result.Success){$result.Diagnostics|ForEach-Object ToString;throw 'Native comparison benchmark compilation failed.'}}finally{$stream.Dispose()}
'{"runtimeOptions":{"tfm":"net6.0","framework":{"name":"Microsoft.NETCore.App","version":"6.0.0"},"rollForward":"Major"}}'|Set-Content -LiteralPath (Join-Path $output 'EconomyNativeMatchBenchmark.runtimeconfig.json')
$oldTiered=$env:DOTNET_TieredCompilation
try{$env:DOTNET_TieredCompilation='0';& dotnet $assembly (Join-Path $output 'benchmark.json');if($LASTEXITCODE-ne0){throw 'Native item matcher benchmark failed.'}}finally{$env:DOTNET_TieredCompilation=$oldTiered}
@{SourceSha256=(Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash;SourceUnmodified=$true;TieredCompilation=$false;BenchmarkOutput=$output}|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $output 'source.json')
