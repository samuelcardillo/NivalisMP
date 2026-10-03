$ErrorActionPreference='Stop'
$gameRoot=(Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$path=Join-Path $gameRoot 'MultiplayerDev/Source/SharedEconomyReplication.cs'
$tree=[Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText([IO.File]::ReadAllText($path)).GetRoot()
$names=@('PreparedEconomyUpdate','IncrementalEconomyPlan','EconomyProfile','ItemApplyTarget','InventoryComparison')
$classes=@($tree.DescendantNodes()|Where-Object{$_ -is [Microsoft.CodeAnalysis.CSharp.Syntax.ClassDeclarationSyntax] -and $_.Identifier.ValueText -in $names})
if($classes.Count-ne$names.Count){throw 'Actual economy cursor extraction failed.'}
$helper=$tree.DescendantNodes()|Where-Object{$_ -is [Microsoft.CodeAnalysis.CSharp.Syntax.MethodDeclarationSyntax] -and $_.Identifier.ValueText-eq'AddItemTarget'}
$inside='using System;using System.Collections.Generic;using System.Diagnostics;namespace NivalisTogether.Dedicated {public static partial class SharedEconomyReplication {'+($classes|ForEach-Object ToFullString)-join "`n"
$inside+=$helper.ToFullString()+'}}'
$other=$tree.DescendantNodes()|Where-Object{$_ -is [Microsoft.CodeAnalysis.CSharp.Syntax.ClassDeclarationSyntax] -and $_.Identifier.ValueText -in @('SharedItemStack','SharedVendorStock','SharedEconomyState','BoundedPreparationFence')}
$outside='using System;using System.IO;using System.Collections.Generic;using System.Threading;using System.Threading.Tasks;namespace NivalisTogether.Dedicated {'+($other|ForEach-Object ToFullString)-join "`n"
$outside+='}'
$refs=[Collections.Generic.List[Microsoft.CodeAnalysis.MetadataReference]]::new()
foreach($file in Get-ChildItem -LiteralPath (Join-Path $gameRoot 'dotnet') -Filter '*.dll'){
 try{[void][Reflection.AssemblyName]::GetAssemblyName($file.FullName)}catch{continue};$refs.Add([Microsoft.CodeAnalysis.MetadataReference]::CreateFromFile($file.FullName))
}
$options=[Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions]::new([Microsoft.CodeAnalysis.OutputKind]::ConsoleApplication).WithOptimizationLevel([Microsoft.CodeAnalysis.OptimizationLevel]::Release)
$compile=[Microsoft.CodeAnalysis.CSharp.CSharpCompilation]::Create('EconomyIncrementalTests').WithOptions($options).AddReferences($refs.ToArray())
foreach($source in @($inside,$outside,[IO.File]::ReadAllText((Join-Path $gameRoot 'MultiplayerDev/Shared/ClientMutationBudget.cs')),[IO.File]::ReadAllText((Join-Path $PSScriptRoot 'EconomyIncrementalTests.cs')))){
 $compile=$compile.AddSyntaxTrees([Microsoft.CodeAnalysis.SyntaxTree[]]@([Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText($source)))
}
$output=Join-Path $PSScriptRoot ('economy-incremental-results-'+[DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss-fff'));[void][IO.Directory]::CreateDirectory($output)
[IO.File]::WriteAllText((Join-Path $output 'EconomyIncremental.generated.cs'),$inside+"`n"+$outside)
$dll=Join-Path $output 'EconomyIncrementalTests.dll';$stream=[IO.File]::Create($dll)
try{$result=$compile.Emit($stream)}finally{$stream.Dispose()}
if(!$result.Success){$result.Diagnostics|Where-Object Severity -eq Error|ForEach-Object ToString;throw 'Economy incremental test compilation failed.'}
'{"runtimeOptions":{"tfm":"net6.0","framework":{"name":"Microsoft.NETCore.App","version":"6.0.0"},"rollForward":"Major"}}'|Set-Content -LiteralPath (Join-Path $output 'EconomyIncrementalTests.runtimeconfig.json')
& dotnet $dll
if($LASTEXITCODE-ne0){throw 'Economy incremental tests failed.'}
Get-FileHash -LiteralPath $path|Select-Object Path,Hash|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $output 'source-hash.json')
Write-Output $output
