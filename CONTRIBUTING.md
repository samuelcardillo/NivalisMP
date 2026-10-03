# Contributing

Start with [development setup](docs/DEVELOPMENT.md) and the
[architecture notes](docs/ARCHITECTURE.md). Fork the repository, make a focused
change, and open a pull request describing the observed problem, the resulting
behaviour, and the checks you ran.

Keep account assets authoritative on the server. Preserve the ordering of world
updates and the atomic pairing of native snapshots with account ledgers. Keep
Unity and IL2CPP calls on the appropriate game thread; managed background work
must not access native Unity objects.

Use a disposable server world for testing. Do not commit saves, account stores,
passwords, server keys, generated bindings, third-party binaries, or game assets.
Describe native playtesting separately from mocked, protocol, and compile checks.
If you did not test a feature in the game, say so.

For bug reports, include the mod version, game build, map, number of players, and
steps to reproduce. Attach a short, redacted log excerpt if helpful. Do not
upload an entire server-data, client-worlds, or BepInEx folder.

Contributions are made under the repository's MIT license.
