# NivalisMP

An unofficial multiplayer sandbox mod for **Nivalis Nights**. Play separate characters, explore different districts, and keep your own account, inventory, money, property and boat in a shared world.

**[Download the alpha](https://github.com/samuelcardillo/NivalisMP/releases) · [Installation and hosting](docs/INSTALL.md) · [Build from source](docs/DEVELOPMENT.md) · [Architecture](docs/ARCHITECTURE.md)**

## ALPHA — work in progress

**This is purely an ALPHA.** Known bugs:

- Can't rent shops (yet)
- NPCs are acting a bit weird

The first public release is **0.5.2-alpha, protocol 14**. It includes the latest NPC movement, replication performance and boat arrival corrections. These corrections passed focused code checks, but the final combined two-client/server-restart test and final performance capture were **not run**. Expect bugs; this is not a claim that every NPC, map or interaction works without glitches.

Use the same game and mod builds on the server and every client. Protocol 13 and earlier cannot join this version. Windows x64 is the supported setup; other platforms and game updates have not been validated.

## Quick start

1. Install your own copy of Nivalis Nights and launch it once.
2. Install **BepInEx 6 for Unity IL2CPP, Windows x64** in the game folder, then launch and close the game so its interop files are generated. The alpha was built with BepInEx bleeding-edge build **788**. See the [dependency instructions](docs/INSTALL.md#requirements).
3. Download the mod ZIP from [Releases](https://github.com/samuelcardillo/NivalisMP/releases). With the game and any server stopped, extract it into the folder containing `Nivalis Nights.exe`, merging its `BepInEx` and `MultiplayerDev` folders.
4. Start a server using the command below, or get an address and port from a friend.
5. Launch the game, select **MULTIPLAYER**, connect, verify the server identity, and create an account with a password of at least 12 characters. Returning players select **LOG IN**.

For a server on your own PC, open **PowerShell 7.4 or newer** in the game folder. Hosting also requires a [.NET 8 or newer x64 runtime](https://learn.microsoft.com/en-us/dotnet/core/install/windows).

```powershell
./MultiplayerDev/Start-DedicatedServer.ps1 -ServerRoot ./MultiplayerDev/server-data/my-world -Name 'My Nivalis World' -Bind 127.0.0.1 -Port 47778
```

Wait for `Ready=True`, then join **127.0.0.1:47778**. For another PC to join, use `-Bind 0.0.0.0` and give it the host's LAN address. See [hosting, shutdown and backups](docs/INSTALL.md#host-a-world) before opening a world to friends or the internet.

The host runs both a network service and a native game simulation worker. It needs the game installed; this is not a standalone server that runs with only .NET. The download contains the mod, server and helpers, without the game, loader, accounts or saved worlds.

## What you can do

- Control separate characters and travel independently across all **17 public destinations**, available from the beginning.
- Start each new account with **5,000 limes**, fresh starting items and one basic boat. Accounts keep their assets across reconnects.
- Recall, drive, dock and refuel your own boat, including supported waterway travel.
- Share NPCs, time, weather and business activity, with account ownership rules for private properties. Shop rental is currently broken.
- Play a sandbox without the opening campaign, story debt or campaign quests.
- Use the shared starter apartment's bed to **Sleep together** between 18:00 and 08:00, when every connected player is ready at an allowed bed.
- Chat locally with plain text or `/s`, globally with `/g`, or privately with `/w username message`. Press Enter to open/send and Escape to cancel.

The starter apartment is rent-free. Its other furniture interactions are blocked. Vendor opening hours and normal purchase costs still apply. Skills, knowledge and native personal receipts remain shared. Private-apartment invitations, furniture editing, boat trading and shared passenger crews are not included.

## Modify it

The mod's C# source and PowerShell helpers are available under the [MIT license](LICENSE). Existing assembly and namespace names remain `NivalisTogether` for compatibility. Start with the [development guide](docs/DEVELOPMENT.md), [architecture](docs/ARCHITECTURE.md) and [contributing guide](CONTRIBUTING.md).

Report reproducible problems through [Issues](https://github.com/samuelcardillo/NivalisMP/issues). Include your game/mod/loader versions and relevant redacted log lines, never account databases, passwords, server keys or full saved worlds. See [SECURITY.md](SECURITY.md) for security reports.

This project is an unofficial community mod and is not affiliated with or endorsed by the game's creators. Game assets and third-party dependencies retain their own licenses.
