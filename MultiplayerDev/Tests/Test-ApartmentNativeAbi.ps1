# Metadata inspection only; no native assembly invocation or game process needed.
$ErrorActionPreference='Stop'
$root=Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
Add-Type -Path (Join-Path $root 'BepInEx/core/Mono.Cecil.dll')
$asm=[Mono.Cecil.AssemblyDefinition]::ReadAssembly((Join-Path $root 'BepInEx/interop/Assembly-CSharp.dll'))
$checks=0
function Check([bool]$condition,[string]$message){$script:checks++;if(!$condition){throw ('Apartment ABI mismatch: '+$message)}}
try {
    $door=$asm.MainModule.Types|Where-Object FullName -eq 'Nivalis.Apartment.ApartmentDoor'
    $controller=$asm.MainModule.Types|Where-Object FullName -eq 'Nivalis.Apartment.ApartmentController'
    $bus=$asm.MainModule.Types|Where-Object FullName -eq 'Nivalis.ActionNonAlloc`2'
    Check ($door.BaseType.FullName -eq 'UnityEngine.MonoBehaviour') 'door is native reference component'
    foreach($name in 'Enter','DoInteraction'){
        $method=$door.Methods|Where-Object Name -eq $name
        Check ($method.Parameters.Count -eq 0 -and $method.ReturnType.FullName -eq 'System.Void' -and !$method.IsStatic) ('safe void native door method '+$name)
    }
    Check (($door.Properties|Where-Object Name -eq 'teleportPoint').PropertyType.FullName -eq 'UnityEngine.Transform') 'native target is Transform reference'
    Check (($door.Properties|Where-Object Name -eq 'onEnter').PropertyType.FullName -eq 'Il2CppSystem.Action') 'existing configured callback is Action reference'
    Check ($controller.BaseType.FullName.StartsWith('Nivalis.Locale.PropertyArea`1')) 'controller reference type'
    foreach($name in 'Entered','enterAndExitWithDistance'){
        Check (($controller.Properties|Where-Object Name -eq $name).PropertyType.FullName -eq 'System.Boolean') ('native primitive property '+$name)
    }
    foreach($name in 'entrance','exit'){
        Check (($controller.Properties|Where-Object Name -eq $name).PropertyType.FullName -eq 'Nivalis.Apartment.ApartmentDoor') ('canonical door reference '+$name)
    }
    Check (($controller.Properties|Where-Object Name -eq 'OnPlayerEnterExit').PropertyType.FullName -eq 'Nivalis.ActionNonAlloc`2<Nivalis.Apartment.ApartmentController,System.Boolean>') 'typed reference + bool context event'
    Check ($bus.BaseType.FullName -eq 'Il2CppSystem.Object') 'event bus is native reference object'
    foreach($name in 'Add','Remove'){
        $method=$bus.Methods|Where-Object Name -eq $name
        Check ($method.Parameters.Count -eq 1 -and $method.Parameters[0].ParameterType.FullName -eq 'Il2CppSystem.Action`2<T1,T2>') ('rooted reference listener '+$name)
    }
    $player=($asm.MainModule.Types|Where-Object FullName -eq 'Nivalis.PlayerManager').NestedTypes|Where-Object Name -eq 'Player'
    Check (($player.Properties|Where-Object Name -ceq 'CurrentProperty').PropertyType.FullName -eq 'Nivalis.GhostSystem.CustomerLoop.BaseProperty') 'native property context is reference object'
    'PASS: '+$checks+' apartment ABI metadata assertions; no game calls.'
} finally { $asm.Dispose() }
