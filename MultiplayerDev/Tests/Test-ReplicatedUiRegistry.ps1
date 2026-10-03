$ErrorActionPreference='Stop'
$devRoot=Split-Path -Parent $PSScriptRoot
$gameRoot=Split-Path -Parent $devRoot
$refs=[Collections.Generic.List[Microsoft.CodeAnalysis.MetadataReference]]::new()
foreach($file in Get-ChildItem -LiteralPath (Join-Path $gameRoot 'dotnet') -Filter '*.dll') {
    try { [void][Reflection.AssemblyName]::GetAssemblyName($file.FullName) } catch { continue }
    $refs.Add([Microsoft.CodeAnalysis.MetadataReference]::CreateFromFile($file.FullName))
}
$comp=[Microsoft.CodeAnalysis.CSharp.CSharpCompilation]::Create('ReplicatedUiRegistryTests'+[Guid]::NewGuid().ToString('N')).WithOptions([Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions]::new([Microsoft.CodeAnalysis.OutputKind]::DynamicallyLinkedLibrary)).AddReferences($refs.ToArray())
foreach($path in @((Join-Path $devRoot 'Source/ReplicatedUiRegistry.cs'),(Join-Path $PSScriptRoot 'ReplicatedUiRegistryTests.cs'))) {
    $comp=$comp.AddSyntaxTrees([Microsoft.CodeAnalysis.SyntaxTree[]]@([Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText([IO.File]::ReadAllText($path))))
}
$stream=[IO.MemoryStream]::new()
try {
    $result=$comp.Emit($stream)
    if(!$result.Success) { $result.Diagnostics | ForEach-Object ToString; throw 'UI registry regression compilation failed.' }
    $assembly=[Reflection.Assembly]::Load($stream.ToArray())
    $assertions=$assembly.GetType('ReplicatedUiRegistryTests').GetMethod('Run').Invoke($null,$null)
} finally { $stream.Dispose() }
"PASS: $assertions production UI registry lifetime and scan-count assertions."

# Validate the exact IL2CPP lifecycle ABI without loading native game code.
[void][Reflection.Assembly]::LoadFrom((Join-Path $gameRoot 'BepInEx/core/Mono.Cecil.dll'))
$asm=[Mono.Cecil.AssemblyDefinition]::ReadAssembly((Join-Path $gameRoot 'BepInEx/interop/Assembly-CSharp.dll'))
try {
    foreach($name in @('_Work_Lukasz.Scripts.UI.TransferMenuUI','Nivalis.Locale.UI.VenueOverviewTabGeneralSection','Nivalis.Locale.UI.VenueStaffOverviewTab','Nivalis.Locale.UI.HireStaffWindow','Nivalis.Locale.UI.MenuModificationWindow','Nivalis.UI.ShopUINew','Nivalis.UI.PlayerInventoryUI','Nivalis.WorldStateDisplayUI','Nivalis.PlayerMoneyDisplayUI')) {
        $type=$asm.MainModule.Types | Where-Object FullName -CEQ $name
        $method=@($type.Methods | Where-Object { $_.Name -ceq 'Awake' -and !$_.IsStatic -and $_.Parameters.Count -eq 0 -and $_.ReturnType.FullName -eq 'System.Void' })
        if($method.Count -ne 1) { throw "Native Awake ABI mismatch: $name" }
    }
} finally { $asm.Dispose() }
'PASS: all nine native Awake methods have the expected instance, zero-argument, void ABI.'
