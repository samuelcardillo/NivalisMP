[CmdletBinding()]
param()
$ErrorActionPreference='Stop'
$devRoot=Split-Path -Parent $PSScriptRoot
$gameRoot=Split-Path -Parent $devRoot
$source=Join-Path $devRoot 'Source/BusinessCraftStateReplication.cs'
$fixture=Join-Path $PSScriptRoot 'CraftMealGhostIndexTests.cs'
$syntax=[Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText([IO.File]::ReadAllText($source))
$classes=@($syntax.GetRoot().DescendantNodes() | Where-Object { $_ -is [Microsoft.CodeAnalysis.CSharp.Syntax.ClassDeclarationSyntax] -and $_.Identifier.ValueText -eq 'CraftMealGhostIndex' })
if($classes.Count -ne 1 -or $classes[0].Parent -isnot [Microsoft.CodeAnalysis.CSharp.Syntax.NamespaceDeclarationSyntax]) {
    throw 'Expected exactly one namespace-level production CraftMealGhostIndex.'
}
$production='using System; using System.Collections.Generic; namespace NivalisTogether.Dedicated {'+[Environment]::NewLine+$classes[0].ToFullString()+[Environment]::NewLine+'}'
$refs=[Collections.Generic.List[Microsoft.CodeAnalysis.MetadataReference]]::new()
foreach($file in Get-ChildItem -LiteralPath (Join-Path $gameRoot 'dotnet') -Filter '*.dll') {
    try { [void][Reflection.AssemblyName]::GetAssemblyName($file.FullName) } catch { continue }
    $refs.Add([Microsoft.CodeAnalysis.MetadataReference]::CreateFromFile($file.FullName))
}
$comp=[Microsoft.CodeAnalysis.CSharp.CSharpCompilation]::Create('CraftMealGhostIndexTests'+[Guid]::NewGuid().ToString('N')).WithOptions([Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions]::new([Microsoft.CodeAnalysis.OutputKind]::DynamicallyLinkedLibrary)).AddReferences($refs.ToArray())
foreach($code in @($production,[IO.File]::ReadAllText($fixture))) {
    $comp=$comp.AddSyntaxTrees([Microsoft.CodeAnalysis.SyntaxTree[]]@([Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText($code)))
}
$stream=[IO.MemoryStream]::new()
try {
    $result=$comp.Emit($stream)
    $result.Diagnostics | Where-Object Severity -ne Hidden | ForEach-Object { $_.ToString() }
    if(!$result.Success) { throw 'Craft meal lifecycle index regression compilation failed.' }
    $testAssembly=[Reflection.Assembly]::Load($stream.ToArray())
    $assertions=$testAssembly.GetType('CraftMealGhostIndexTests').GetMethod('Run').Invoke($null,$null)
} finally { $stream.Dispose() }
$abiChecks=0
function Assert-MealAbi([bool]$condition,[string]$message) {
    $script:abiChecks++;if(!$condition){throw ('Craft meal native ABI mismatch: '+$message)}
}
Add-Type -Path (Join-Path $gameRoot 'BepInEx/core/Mono.Cecil.dll')
$native=[Mono.Cecil.AssemblyDefinition]::ReadAssembly((Join-Path $gameRoot 'BepInEx/interop/Assembly-CSharp.dll'))
try {
    $manager=$native.MainModule.Types | Where-Object FullName -ceq 'Nivalis.GhostSystem.GhostManager'
    foreach($expected in @(
        @{Name='RegisterGhostImmediate';Parameters=@('Nivalis.GhostSystem.Ghost')}
        @{Name='DeregisterGhostImmediate';Parameters=@('Nivalis.GhostSystem.Ghost')}
        @{Name='InitializeInternal';Parameters=@('Nivalis.ManagerConfiguration','Nivalis.ISavePacket')}
        @{Name='OnDestroy';Parameters=@()}
    )) {
        $methods=@($manager.Methods | Where-Object Name -ceq $expected.Name)
        Assert-MealAbi ($methods.Count -eq 1 -and !$methods[0].IsStatic) ($expected.Name+' resolves to exactly one instance method')
        Assert-MealAbi ($methods[0].Parameters.Count -eq $expected.Parameters.Count) ($expected.Name+' parameter count')
        for($i=0;$i -lt $expected.Parameters.Count;$i++) {
            Assert-MealAbi ($methods[0].Parameters[$i].ParameterType.FullName -ceq $expected.Parameters[$i] -and !$methods[0].Parameters[$i].ParameterType.IsByReference) ($expected.Name+' exact safe parameter '+$i)
        }
    }
    Assert-MealAbi ($null -ne ($manager.Properties | Where-Object Name -ceq '_ghostsDictionary').GetMethod) 'native registry identity can be read'
} finally { $native.Dispose() }
$structuralChecks=0
function Assert-MealStructure([bool]$condition,[string]$message) {
    $script:structuralChecks++;if(!$condition){throw ('Craft meal pooled callback regression: '+$message)}
}
$postfix=@($syntax.GetRoot().DescendantNodes() | Where-Object { $_ -is [Microsoft.CodeAnalysis.CSharp.Syntax.MethodDeclarationSyntax] -and $_.Identifier.ValueText -ceq 'MealDeregisteredPostfix' })
Assert-MealStructure ($postfix.Count -eq 1) 'one deregistration postfix'
$postfixArgumentReads=@($postfix[0].DescendantTokens() | Where-Object ValueText -ceq '__0')
Assert-MealStructure ($postfixArgumentReads.Count -eq 0) 'postfix never reads the pooled native argument'
Assert-MealStructure (@($postfix[0].ParameterList.Parameters | Where-Object { $_.Identifier.ValueText -ceq '__state' -and $_.Type.ToString() -ceq 'MealDeregisterState' }).Count -eq 1) 'postfix receives managed prefix-captured state'
$bindings=@($syntax.GetRoot().DescendantNodes() | Where-Object { $_ -is [Microsoft.CodeAnalysis.CSharp.Syntax.InvocationExpressionSyntax] -and $_.Expression.ToString() -ceq 'PatchLifecycle' } | ForEach-Object { $_.ToString() -replace '\s','' })
Assert-MealStructure ($bindings -ccontains 'PatchLifecycle("DeregisterGhostImmediate",nameof(MealDeregisteringPrefix),nameof(MealDeregisteredPostfix))') 'deregistration binds both prefix capture and postfix verification'
Assert-MealStructure ($bindings -ccontains 'PatchLifecycle("RegisterGhostImmediate",null,nameof(MealRegisteredPostfix))') 'registration binds canonical observation postfix'
[pscustomobject]@{
    Utc=[DateTime]::UtcNow.ToString('O');Passed=$true;Assertions=$assertions
    NativeAbiAssertions=$abiChecks;PooledCallbackStructuralAssertions=$structuralChecks
    SourceSHA256=(Get-FileHash -LiteralPath $source).Hash
    FixtureSHA256=(Get-FileHash -LiteralPath $fixture).Hash
    Scope='Actual production lifecycle index with pure native-world facade. Verifies boundary bootstrap, replacement and stale callbacks, copied retained snapshots, and canonical re-resolution consumer behavior. Native callback wiring and deletion require separate ABI/live acceptance.'
} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $devRoot 'inspection/craft-meal-ghost-index-tests.json')
Write-Output "PASS: $assertions production craft meal lifecycle index assertions."
Write-Output "PASS: $abiChecks native ABI checks and $structuralChecks pooled callback wiring checks; no native game calls."
