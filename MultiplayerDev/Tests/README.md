# Tests

Run the portable managed suite from the repository root with PowerShell 7:

```powershell
pwsh -NoProfile -File ./MultiplayerDev/test-managed.ps1
```

This entry point uses an explicit list of tests and the current `Shared` and `Server` source. It does not launch the game, connect to an existing game server, install a plugin, or use your account/save folders. The fixture credentials and IDs in these sources are synthetic test data, not accounts supplied with the mod.

## What the portable suite covers

- Account authentication and bounded account-state transfer.
- Chat protocol validation.
- Boat trigger geometry and apartment protocol validation.
- Independent player scene membership and map catch-up progress.
- Group sleep consensus.
- Per-peer event queues, delivery backpressure, and world-flow accounting.

The test runner is the authoritative list of included tests. Managed test success does not verify game visuals, native movement, live multiplayer, or game-version compatibility.

## Additional regression sources

The other `Test-*.ps1` scripts and C# files preserve the mod's broader regression work. They are developer tools and are **not** automatically run by CI. Most older scripts assume that `MultiplayerDev` sits directly inside a prepared game installation, with matching local `dotnet`, `BepInEx/core`, and generated `BepInEx/interop` directories. Some extract selected current production methods with Roslyn and test them against small native-object facades. Others inspect the installed game's generated interop metadata. None of those dependencies are distributed in this repository.

Review a runner's inputs before invoking it. Use a separate development installation and disposable output directories for native or service integration work. Some integration fixtures start loopback test services and create test worlds, identities and logs under their designated output folders. Do not supply a live server's data directory as a fixture path. Keep generated output, private saves and credentials out of commits.

`Test-CanonicalHashStream.ps1` is a two-version comparison tool and requires an explicit `-BaselineSource` pointing to the older `SharedEconomyReplication.cs` you want to compare; no historical private source archive is included.

Historical capture reviewers, restoration checks tied to a developer's private world, obsolete simulated-peer launchers, and process-debugger helpers are not included. Live acceptance evidence is described in the project documentation rather than shipped as account data, logs, dumps, or game binaries.
