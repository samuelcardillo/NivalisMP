$ErrorActionPreference='Stop'
$gameRoot=Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
Add-Type -Path (Join-Path $gameRoot 'BepInEx/core/Mono.Cecil.dll')
$assembly=[Mono.Cecil.AssemblyDefinition]::ReadAssembly((Join-Path $gameRoot 'BepInEx/interop/Assembly-CSharp.dll'))
$checks=0
function Check([bool]$condition,[string]$message) { $script:checks++; if(!$condition){ throw ('Boat ABI mismatch: '+$message) } }
function Get-NativeBoatType([string]$name) { return $assembly.MainModule.Types | Where-Object FullName -eq $name }
function Method([string]$type,[string]$name,[string[]]$parameters) {
    $method=@((Get-NativeBoatType $type).Methods | Where-Object { $_.Name -ceq $name -and $_.Parameters.Count -eq $parameters.Count })
    Check ($method.Count -eq 1) ($type+'.'+$name+' unique overload')
    for($i=0;$i -lt $parameters.Count;$i++) {
        Check ($method[0].Parameters[$i].ParameterType.FullName -eq $parameters[$i]) ($name+' argument '+$i)
        Check (!$method[0].Parameters[$i].ParameterType.IsByReference) ($name+' avoids unsafe native byref argument')
    }
}
Method 'Nivalis.BoatTeleportSequence' 'Start' @('Nivalis.Boat.BoatController','Nivalis.PortalKey')
Method 'Nivalis.Boat.BoatDock' 'Dock' @('Nivalis.Boat.BoatController')
Method 'Nivalis.Boat.BoatDock' 'Recall' @('Nivalis.Boat.BoatGhost')
Method 'Nivalis.Boat.BoatDock' 'Undock' @()
Method 'Nivalis.Boat.BoatCockpit' 'Enter' @('Nivalis.PlayerCharacter')
Method 'Nivalis.Boat.BoatController' 'OnDeckEnter' @('Nivalis.PlayerCharacterController')
Method 'Nivalis.Boat.BoatController' 'OnDeckExit' @('Nivalis.PlayerCharacterController')
Method 'Nivalis.PlayerNavMesh' 'OnTriggerEnter' @('UnityEngine.Collider')
Method 'Nivalis.PlayerNavMesh' 'OnTriggerExit' @('UnityEngine.Collider')
Method 'Nivalis.PlayerCharacterController' 'set_NavMesh' @('Nivalis.PlayerNavMesh')
Method 'Nivalis.Boat.BoatGhost' 'FindBoat' @()
Method 'Nivalis.Boat.BoatController' 'UpdateUnlocking' @()
Method 'Nivalis.Boat.BoatController' 'Update' @()
Method 'Nivalis.Boat.BoatController' 'FixedUpdate' @()
Method 'Nivalis.Boat.BoatController' 'Awake' @()
Method 'Nivalis.Boat.BoatController' 'OnDestroy' @()
Method 'Nivalis.BoatFuelDistributor' 'TryStartFueling' @()
Method 'Nivalis.BoatFuelDistributor' 'UpdateFueling' @()
Method 'Nivalis.GhostSystem.GhostManager' 'RegisterGhostImmediate' @('Nivalis.GhostSystem.Ghost')
Method 'Nivalis.GhostSystem.GhostManager' 'DeregisterGhostImmediate' @('Nivalis.GhostSystem.Ghost')
Method 'Nivalis.GhostSystem.GhostManager' 'InitializeInternal' @('Nivalis.ManagerConfiguration','Nivalis.ISavePacket')
Method 'Nivalis.GhostSystem.GhostManager' 'OnDestroy' @()
foreach($name in @('RegisterGhostImmediate','DeregisterGhostImmediate','InitializeInternal','OnDestroy')) {
    $method=@((Get-NativeBoatType 'Nivalis.GhostSystem.GhostManager').Methods | Where-Object Name -ceq $name)
    Check ($method.Count -eq 1 -and !$method[0].IsStatic) ('boat lifecycle hook '+$name+' has an unambiguous manager instance')
}
Check ($null -ne ((Get-NativeBoatType 'Nivalis.GhostSystem.GhostManager').Properties | Where-Object Name -ceq '_ghostsDictionary').GetMethod) 'boat cleanup can identify a rebuilt native ghost registry'
foreach($typeName in @('Nivalis.Boat.BoatController','Nivalis.Boat.BoatGhost','Nivalis.Boat.BoatDock','Nivalis.PlayerCharacter','Nivalis.PortalKey')) {
    $t=Get-NativeBoatType $typeName; Check ($null -ne $t -and !$t.IsValueType) ($typeName+' remains native reference wrapper')
}
$ghost=Get-NativeBoatType 'Nivalis.Boat.BoatGhost'
foreach($name in @('unlocked','boarded','driven','fuel','dock')) { Check ($null -ne ($ghost.Properties | Where-Object Name -eq $name).SetMethod) ('raw ghost state '+$name) }
Check (((Get-NativeBoatType 'Nivalis.Boat.BoatController').Properties | Where-Object Name -eq 'Instance').SetMethod.IsStatic) 'local native singleton can be scoped'
Check (((Get-NativeBoatType 'Nivalis.Boat.BoatController').Properties | Where-Object Name -eq 'navMesh').PropertyType.FullName -eq 'Nivalis.PlayerNavMesh') 'boat navmesh ownership uses its exact serialized component'
Check (((Get-NativeBoatType 'Nivalis.PlayerCharacterController').Properties | Where-Object Name -ceq 'NavMesh').SetMethod.IsPublic) 'stale worker attachment can use native NavMesh setter without raw field writes'
Check (((Get-NativeBoatType 'Nivalis.PlayerCharacterController').Properties | Where-Object Name -eq 'CharacterController').PropertyType.FullName -eq 'UnityEngine.CharacterController') 'native trigger exit receives the real character collider'
Check (((Get-NativeBoatType 'Nivalis.Boat.BoatDockGhost').Properties | Where-Object Name -eq 'DockedGhost').PropertyType.FullName -eq 'Nivalis.GhostSystem.GhostReference`1<Nivalis.Boat.BoatGhost>') 'single native berth needs explicit owner reservation'
Check (((Get-NativeBoatType 'Nivalis.Boat.BoatDock').Properties | Where-Object Name -eq 'IsRecalling').PropertyType.FullName -eq 'System.Boolean') 'native recall completion uses the dock coroutine state'
Check ($null -ne ((Get-NativeBoatType 'Nivalis.Boat.BoatDock').Properties | Where-Object Name -eq 'IsRecalling').GetMethod) 'native recall completion getter is available without field mutation'
Check (((Get-NativeBoatType 'Nivalis.BoatTeleportSequence').Properties | Where-Object Name -eq 'boatGhost').PropertyType.FullName -eq 'Nivalis.Boat.BoatGhost') 'native transition retains ghost reference identity'
Check (((Get-NativeBoatType 'Nivalis.Boat.BoatController').Properties | Where-Object Name -eq 'FuelCapacity').PropertyType.FullName -eq 'System.Single') 'native fuel capacity scalar'
Check (((Get-NativeBoatType 'Nivalis.BoatFuelDistributor').Properties | Where-Object Name -eq 'fuelPrice').PropertyType.FullName -eq 'System.Single') 'native refuel unit price scalar'
$assembly.Dispose()
'PASS: '+$checks+' boat native ABI checks; no native game calls.'
