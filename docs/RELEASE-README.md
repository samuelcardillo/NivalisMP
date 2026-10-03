# NivalisMP 0.5.2-alpha

**This is purely an ALPHA.** Known bugs:

- Can't rent shops (yet)
- NPCs are acting a bit weird

An unofficial multiplayer sandbox mod for Nivalis Nights, using protocol 14.
Use the same game and mod builds on the host and every player's PC.

## Install

1. Install your own Windows x64 copy of Nivalis Nights.
2. Install **BepInEx 6 Unity IL2CPP, Windows x64** separately, then launch and
   close the game once to generate its interop files. This alpha was built with
   bleeding-edge build 788. See the
   [installation guide](https://github.com/samuelcardillo/NivalisMP/blob/main/docs/INSTALL.md).
3. With the game and dedicated server stopped, extract this ZIP into the folder
   containing `Nivalis Nights.exe`, merging `BepInEx` and `MultiplayerDev`.
4. Launch the game and use **MULTIPLAYER** to join a server. Create an account
   with a unique password of at least 12 characters, or select **LOG IN**.

The ZIP includes only the mod and its server helpers. It does not include the
game, BepInEx, .NET, private accounts, or saved worlds.

## Host on this PC

Install PowerShell 7.4+ and a .NET 8+ x64 runtime. Open PowerShell 7 in the game
folder, then run:

```powershell
./MultiplayerDev/Start-DedicatedServer.ps1 -ServerRoot ./MultiplayerDev/server-data/my-world -Name 'My Nivalis World' -Bind 127.0.0.1 -Port 47778
```

Wait for `Ready=True`, then join `127.0.0.1:47778`. The host runs a game simulation
worker as well as the network service, so a complete game installation is
required. For LAN, backups, upgrades and clean shutdown, read the included
`NivalisMP-INSTALL.md` or the online installation guide.

New accounts receive **5,000 limes**, fresh starting items and one basic boat.
Players have separate characters and can travel independently in the shared
sandbox world.

## Source and validation

[Source, documentation and issues](https://github.com/samuelcardillo/NivalisMP)
are available under the MIT license. See `NivalisMP-LICENSE.txt` and
`NivalisMP-THIRD-PARTY-NOTICES.md`.

The source build and managed checks passed. The final combined native gameplay
and performance run was not completed. This alpha may contain further gameplay,
animation, travel or performance defects. `NivalisMP-release.json` records the
packaged file hashes and corresponding source hashes.
