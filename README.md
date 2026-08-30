## Hideout Shootout

A mod for SPT 4.0.13 that lets you keep your weapon out in the Hideout outside of the shooting range, and spawn a real, fully-functional scav bot to shoot at.

---

### Compatibility
- Target: SPT 4.0.13 (backported from the upstream SPT 4.1 release)

### Installation
- Place the folders from the release zip into your SPT root folder. It contains both a client plugin (`BepInEx/plugins/`) and a server mod (`SPT/user/mods/`) — both are required.
- Works out of the box. Settings are optional, via the BepInEx configuration manager.

### How to Use
- Enter the Hideout and step into the shooting range.
- Normally your weapon holsters when you turn too far or walk away. With this mod you keep it out until you press ESC.
- Press **F11** to spawn a scav target in front of you, or to replace the current one.
- The scav is a genuine `BotOwner`/`LocalPlayer` — it takes hits, ragdolls, and dies like a real bot — not a static prop. It spawns frozen in place so it stays put as a target.

### Known Limitation
- The scav spawns with no hand-rig model (the game's own hand-rig bundle for its outfit isn't reachable outside of a real raid's loading screen, and every loading API we could reach in `4.0.13` hits the same wall). Everything else — head, body, gear, weapon, hit reactions, death — renders and behaves normally.
- Third-party visual-effects mods (e.g. HollywoodFX) that explicitly detect and skip the Hideout will not produce blood/impact FX on the scav, since that's a deliberate choice made in that mod, not something this mod can change on its own.

### 4.0.13 Backport Notes
- **Server mod** (`Server/`): retargeted `net10.0` → `net9.0`, switched from raw DLL references
  to the `SPTarkov.*` 4.0.13 NuGet packages, converted the metadata record from 4.1's
  `IModMetadata` to 4.0.13's `AbstractModMetadata`, and moved the loader priority to
  `OnLoadOrder.PreSptModLoader` with `IOnLoad.OnLoad()` (no `CancellationToken` parameter).
- **Client mod — weapon-out-of-holster feature**: unchanged in behavior; hardened patch loading
  so one patch failing to find its target no longer blocks the rest (`Plugin.cs`).
- **Client mod — scav spawning feature**: this is the bulk of the backport, since 4.0.13's client
  is far less deobfuscated than 4.1's and the game normally never constructs a full player
  character outside of a real raid's own loading/bundle-preload machinery. Highlights, all
  confirmed against a real 4.0.13 client:
  - Bootstraps a working `BotSpawner`/`BotsController`/`ISpawnSystem`/`ISession` from scratch in
    the Hideout, where the game never initializes any of them.
  - Preloads the scav profile's bundles through `IAssetsManager.LoadBundlesAsync`, trying several
    `ResourceKey → string` conversions in order at runtime (`ToAssetName()`, `rcid`, `path`) and
    committing to whichever one actually succeeds, since which one works isn't consistent across
    every asset category.
  - Strips the `Hands` body part from the scav's customization before creation, since the game's
    own hand-rig bundle for a given outfit is unreachable outside of a raid's own preload step no
    matter which loading API is used — see Known Limitation above.
  - Recovers the spawned bot by scanning `GameWorld`'s player list when another already-installed
    mod's own `BotSpawner.OnBotCreated` handler throws first (a plain multicast event — one bad
    subscriber blocks every handler after it, ours included).
  - Works around a real bug in this client's `LocalPlayer.Create` (SPT's `DisableDevMaskCheckPatch`
    transpiler double-completes the async state machine outside of a raid) by temporarily
    unpatching it for the duration of the call.
  - Disables offline player culling on the freshly spawned bot so its body actually renders instead
    of being invisible until it takes a hit.
- If something still doesn't work on a given install, turn on **Enable Spawn Diagnostics** in the
  BepInEx configuration manager and check the BepInEx log — the failure will point at the exact
  step and member involved.

### Settings
- **Spawn Scav Hotkey** — rebind the spawn key (default F11).
- **Bot Spawn Distance** — how far ahead the scav is placed.
- **Face Scav Toward Player** — have the scav spawn facing you.
- **Enable Spawn Diagnostics** — log the hideout bot spawn pipeline: bootstrap candidates, bundle preloads, LocalPlayer creation, BotOwner creation and bot activation.
- **Enable Renderer Diagnostics** — dump the spawned scav's renderer state at activation and again 2 seconds later. Use if the bot ever spawns invisible.
- **Enable Harmony Patch Diagnostics** — log which mods have patched `LocalPlayer.Create`. Use if bot creation fails and another mod is suspected.

---

###### This project is distributed under the MIT License — see `LICENSE` for details.
