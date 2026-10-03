# Changelog

## 0.5.2-alpha — 2026-10-03

First public source and binary alpha of NivalisMP. The plugin retains its `NivalisTogether` assembly identity and uses **protocol 14**.

**Purely an ALPHA. Known bugs:**

- Can't rent shops (yet)
- NPCs are acting a bit weird

### Included

- Dedicated network service with a native game simulation worker and persistent player accounts.
- Independent characters, map travel and apartment contexts; all 17 public destinations available immediately.
- Fresh starting items and **5,000 limes** for newly initialized accounts.
- One persistent basic boat per account, with ownership checks, recall, driving, docking, refueling and supported waterway travel.
- Shared NPCs, clock, weather and business activity, with private account assets and property ownership infrastructure. Shop rental is currently broken.
- A questless multiplayer sandbox without the opening narrative debt.
- Shared starter apartment and unanimous overnight sleep; other starter-furniture interactions remain blocked.
- Local, global and whisper chat.

### Corrections and performance work

- Replaced repeated full-scene menu discovery with registered UI tracking.
- Distributed snapshot preparation and native application work across bounded stages.
- Added independent per-client world queues and a fresh ordered catch-up barrier before releasing loading controls.
- Preserved craft reference identities when the referenced native object is absent locally.
- Added selected public NPC walking guards and corrected stale movement origins when an NPC view resumes an existing walk.
- Waited for canonical boat recall completion, published destination state before travel completion, and repaired/checked native boat attachment on arrival.

### Validation and limits

Focused code and native-interface checks cover these corrections. The final combined two-client/server-restart test and final one-client performance capture were **not run** for the release candidate. No universal NPC, animation, interaction or hitch-free claim is made.

Use matching game/mod builds; older wire protocols cannot connect. Private furniture editing, apartment invitations, shared boat passenger crews, boat trading and customizable fleets are outside this alpha. Skills, knowledge and native personal receipts remain shared.
