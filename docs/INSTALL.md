# Installation and hosting

**Purely an ALPHA:** you can't rent shops yet, and NPCs are acting a bit weird. Expect incomplete or buggy behavior even where a feature is present.

## Requirements

Every player, and the PC hosting the simulation, needs a compatible **Windows x64** copy of Nivalis Nights. Keep game and mod versions identical across the group. Game updates may change the native interfaces used by the mod.

Install **BepInEx 6 Unity IL2CPP for Windows x64** separately. This alpha was built against bleeding-edge build **788**; other loader builds have not been validated. Choose the `Unity.IL2CPP-win-x64` archive from the [official BepInEx builds](https://builds.bepinex.dev/projects/bepinex_be), following the [official IL2CPP installation guide](https://docs.bepinex.dev/master/articles/user_guide/installation/unity_il2cpp.html). The first loader run generates interop files and can take longer than an ordinary launch.

For hosting, install [PowerShell 7.4 or newer](https://learn.microsoft.com/en-us/powershell/scripting/install/install-powershell-on-windows) and a [.NET 8 or newer x64 runtime](https://learn.microsoft.com/en-us/dotnet/core/install/windows). Use `pwsh`, not Windows PowerShell 5.1. `dotnet --list-runtimes` should list `Microsoft.NETCore.App`.

The dedicated host launches a network service **and the game's Unity worker**. It needs a complete game installation with the mod and loader. Linux-only .NET hosting, containers and cross-platform clients are not supported by this alpha.

## Install the release

1. Launch the unmodified game once, close it, then install and initialize BepInEx as described above.
2. Download the mod ZIP from [Releases](https://github.com/samuelcardillo/NivalisMP/releases). The GitHub source-code ZIP is for development and does not contain the compiled mod.
3. Stop the dedicated server and close every game/worker process before updating files.
4. Extract the mod ZIP into the directory containing `Nivalis Nights.exe`. Merge folders and replace the mod's previous files when upgrading.

The resulting paths include:

```text
Nivalis Nights.exe
BepInEx/
  plugins/NivalisTogether/NivalisTogether.dll
MultiplayerDev/
  build-v2/NivalisTogether.dll
  build-v2/NivalisTogether.Server.dll
  build-v2/NivalisTogether.Server.runtimeconfig.json
  Start-DedicatedServer.ps1
  Get-DedicatedServerStatus.ps1
  Stop-DedicatedServer.ps1
```

Keep both copies of `NivalisTogether.dll` from the same release: the launcher checks that the installed plugin matches the server's build directory. The release does not include the game, BepInEx, .NET, accounts or a pre-existing world.

## Host a world

Open PowerShell 7.4+ in the game directory. Use an explicit world directory so start, status and stop always address the same world:

```powershell
./MultiplayerDev/Start-DedicatedServer.ps1 -ServerRoot ./MultiplayerDev/server-data/my-world -Name 'My Nivalis World' -Bind 127.0.0.1 -Port 47778 -MaxPlayers 4
./MultiplayerDev/Get-DedicatedServerStatus.ps1 -ServerRoot ./MultiplayerDev/server-data/my-world
```

Wait for `Ready=True`. First startup creates a fresh multiplayer world and server identity. Later starts restore that directory's checkpoint. The host can then launch a normal game client and join `127.0.0.1:47778`.

For LAN play, start with `-Bind 0.0.0.0`, allow the server through your Windows firewall for the intended network, and have other players join the host's LAN IP with the same TCP port. Local discovery uses the following UDP port: **47779** with the default TCP **47778**. Changing `-Port` also changes discovery to `Port + 1`.

Without arguments the launcher uses `server-data/default`, bind address `0.0.0.0`, TCP 47778 and four player slots. The configurable slot limit is not a performance guarantee; the full 32-player capacity has not been validated.

Internet play requires suitable routing/firewall configuration or a VPN. The launcher does not configure a router, expose ports automatically or provide a relay. LAN discovery does not find servers across the internet. Gameplay traffic is authenticated but is not encrypted; see [SECURITY.md](https://github.com/samuelcardillo/NivalisMP/blob/main/SECURITY.md).

## Join and leave

Select **MULTIPLAYER** beneath LOAD on the title screen. Select a discovered server or enter its address and port. Verify its fingerprint with the host before trusting its identity.

Choose account creation for a new account and use a password of at least 12 characters. Returning players use **LOG IN**; confirmation is only needed for creation. Leave the Room Password field empty unless the host supplied one. Choose an appearance and join. Only one connection can use an account at a time.

New accounts receive 5,000 limes and fresh starting items once. Each account receives one basic boat; existing accounts without a boat receive one on login. Recall it at an available dock. An update to the starting-money setting does not reset existing balances.

Use **Esc → DISCONNECT** to return to the title screen, and let that transition finish before reconnecting. Multiplayer uses isolated storage and restores personal save paths on leaving. The server does not import your solo campaign.

## Save, stop and back up

Use the same world directory when stopping:

```powershell
./MultiplayerDev/Stop-DedicatedServer.ps1 -ServerRoot ./MultiplayerDev/server-data/my-world -WorkerTimeoutSeconds 120
```

After a clean stop, back up the **entire server directory**. Its native world snapshot and account ledger form a paired checkpoint; restore them together, with the credential store and server identity. Keep those backups private. Do not post the directory to GitHub or attach it to an issue.

Before an upgrade, disconnect players, stop the server, and make that backup. Install matching release files on every PC, then start with the same `-ServerRoot`. Choosing a new empty directory creates a different world with different accounts and identity.

## Troubleshooting

| Symptom | Check |
| --- | --- |
| No MULTIPLAYER button | Check `BepInEx/LogOutput.txt` for loader/plugin errors and confirm the DLL is under `plugins/NivalisTogether`. |
| Plugin/build mismatch at startup | Re-extract both DLL copies from the same mod release after stopping the game and server. |
| Version or protocol rejected | Match game and mod versions on clients, service and worker. This release uses protocol 14. |
| Server not ready | Run the status helper for the correct world root; inspect its service and worker logs under `server-data/.../runtime`. |
| Changed server identity | Verify the change with the host. A new or restored identity may mean you reached a different world. |
| Long synchronizing screen | Let loading and ordered world catch-up finish. Slow or stalled catch-up can disconnect the client; reconnect after checking server health. |
| Shop closed / sleep unavailable | Shop schedules still apply. Shared sleep is available from 18:00 until 08:00 and requires all connected players to be ready. |
| Cannot rent a shop | Shop rental is a known alpha bug and is not working yet. |

When reporting a bug, describe the steps, maps, player count and whether it happens after reconnecting. Share only relevant redacted log excerpts. The final combined gameplay/performance pass was not run for this experimental release; NPC animation, travel and performance may still have defects.
