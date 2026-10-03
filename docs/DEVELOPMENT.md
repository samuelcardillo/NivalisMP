# Development

NivalisMP is a C# BepInEx IL2CPP plugin plus a managed dedicated network service. Its assembly names and C# namespaces remain `NivalisTogether`.

## Set up

Clone this repository anywhere on a Windows machine with **PowerShell 7.4 or newer**. For a full plugin build, prepare a separate local game installation using [INSTALL.md](INSTALL.md), including the first BepInEx launch. Building into a separate checkout does not require closing the game. Close it and stop its worker before installing the plugin; stop a service before replacing outputs that it uses.

The build uses your local game's `dotnet/*.dll`, loader assemblies under `BepInEx/core`, and generated wrappers under `BepInEx/interop`. These dependencies are intentionally absent from the repository and release. Do not commit copies of them. The published alpha used BepInEx 6 bleeding-edge build 788.

## Build

From the repository root, substitute your game installation path:

```powershell
pwsh -File ./MultiplayerDev/build-dedicated.ps1 -GamePath 'C:\Games\Nivalis Nights' -Target All
```

`-Target` accepts `Plugin`, `Server` or `All`. Outputs go to this checkout's `MultiplayerDev/build-v2`:

```text
NivalisTogether.dll
NivalisTogether.Server.dll
NivalisTogether.Server.runtimeconfig.json
```

To also install the plugin into the selected game installation:

```powershell
pwsh -File ./MultiplayerDev/build-dedicated.ps1 -GamePath 'C:\Games\Nivalis Nights' -Target All -Install
```

`-Install` copies the plugin to `BepInEx/plugins/NivalisTogether`. To run the server from a separate checkout build, copy the three generated files into the game's `MultiplayerDev/build-v2`, and copy `Start-DedicatedServer.ps1`, `Get-DedicatedServerStatus.ps1` and `Stop-DedicatedServer.ps1` into the game's `MultiplayerDev`. Run the hosting helpers there: they locate the game executable relative to that folder. Use an empty test `-ServerRoot`, not a live world's directory.

To package a completed build into an installable alpha ZIP:

```powershell
pwsh -File ./scripts/New-Release.ps1
```

The packager includes only the mod's release files and documentation. Install the resulting ZIP using [INSTALL.md](INSTALL.md).

## Test

The portable managed suite does not require the game, generated wrappers or a separate .NET SDK:

```powershell
pwsh -File ./MultiplayerDev/test-managed.ps1
```

It uses PowerShell's bundled compiler/runtime to run selected protocol, account, map and queue checks. The initial public source build passed all 11 suites with 7,496 assertions. GitHub Actions runs this suite on Windows. Passing it does not verify native Unity behavior.

`MultiplayerDev/Tests` also contains focused regression and native-interface tests from development. Some original test helpers assume `MultiplayerDev` is inside a prepared game installation and depend on local wrappers or compiled outputs. Use the portable suite as the default entry point; read an individual helper before running it.

Native changes need a separate game test. Use disposable accounts and an isolated server root, then check the affected interaction, independent peers, same-process reconnect and a clean service/worker restart. NPC and boat work also needs visual movement/attachment checks. Keep long performance captures separate from instrumented functional tests.

## Find the code

| Directory | Responsibility |
| --- | --- |
| `MultiplayerDev/Source` | Plugin, Unity/native hooks, client replication and authoritative game worker. |
| `MultiplayerDev/Server` | Managed listener, peer sessions, world delivery and chat routing. |
| `MultiplayerDev/Shared` | Wire data, authentication, validation, state machines and reusable policies. |
| `MultiplayerDev/Tests` | Managed regressions, boundary doubles and native metadata checks. |

Start at `Source/Plugin.cs`, `Source/DedicatedSession.cs` and `Server/ServerProgram.cs`. The [architecture guide](ARCHITECTURE.md) explains the authority and persistence boundaries.

Starting currency is defined by `PlayerAccountRuntime.StartingLimes`; it is 5,000 in this release. Native currency uses hundredths, so `StartingMoney` is 500,000. The initial grant only affects accounts being initialized, not existing balances.

## Compatibility and changes

Treat native pointers, callbacks and generated method signatures as game-build-specific. Managed boundary doubles and compilation cannot prove that a hook is safe in the running game. Keep native mutation on the Unity thread, preserve account/context checks, and retain rollback or explicit failure when an applied update cannot be verified.

Wire changes must account for `Shared/DedicatedProtocol.cs`. Protocol 14 uses ordered delivery acknowledgements and a fresh catch-up barrier; do not release loading/input locks merely because a client's local queue is empty. Changing protocol semantics may require a version bump and matching clients, service and worker.

Follow [CONTRIBUTING.md](../CONTRIBUTING.md), include the tests relevant to the change, and state what native behavior remains untested. Keep account stores, keys, snapshots, logs and generated game assemblies out of commits and release archives.

The current alpha has known gameplay bugs: shop rental is not working yet, and NPC behavior remains irregular. Treat ownership and replication code as implementation under development, not proof that those user flows are complete.
