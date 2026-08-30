## Hideout Shootout

A mod for SPT 4.0.13 that lets you keep your weapon out in the Hideout outside of the shooting range, and spawn scav targets to shoot at.

---

### Compatibility
- Target: SPT 4.0.13 (backported from the upstream SPT 4.1 release)

### Installation
- Place the folders from the release zip into your SPT root folder. It contains both a client plugin (`BepInEx/plugins/`) and a server mod (`SPT/user/mods/`) — both are required.
- Works out of the box. Settings are optional, via the BepInEx configuration manager.

### 4.0.13 Backport Notes
- Server mod (`Server/`): retargeted `net10.0` → `net9.0`, switched from raw DLL
  references to the `SPTarkov.*` 4.0.13 NuGet packages, converted the metadata record
  from 4.1's `IModMetadata` to 4.0.13's `AbstractModMetadata`, and moved the loader
  priority from `OnLoadOrder.Preload` to `OnLoadOrder.PreSptModLoader` with
  `IOnLoad.OnLoad()` (no `CancellationToken` parameter). Builds clean against the real
  4.0.13 packages.
- Client mod: hardened patch loading so one patch failing to find its target no longer
  blocks the rest (`Plugin.cs`), replaced a hardcoded obfuscated method name
  (`BotCreatorClient.method_3`) with a signature-based lookup that survives obfuscation
  changes, and renamed `ObjectsFactory` (4.1's deobfuscated name) to `PoolManagerClass`
  (4.0.13's client-internal name) per SPT's official migration notes.
- **Not verified against a real 4.0.13 client**: the nested `PoolManagerClass.PoolsCategory`/
  `AssemblyType` enum members, and whether SPT 4.0.13 still ships the
  `DisableDevMaskCheckPatch` transpiler that the old `LocalPlayer.Create` code path was
  written to avoid (see the porting notes in `Patches.cs`). If a hideout scav spawn
  fails to compile or the bot never appears, check the BepInEx log with
  "Enable Spawn Diagnostics" turned on — the failure will point at the exact member or
  step that still needs a 4.0.13-specific fix.

### How to Use
- Enter the Hideout and step into the shooting range.
- Normally your weapon holsters when you turn too far or walk away. With this mod you keep it out until you press ESC.
- Press **F11** to spawn a scav target in front of you, or to replace the current one.

### Settings
- **Spawn Scav Hotkey** — rebind the spawn key (default F11).
- **Bot Spawn Distance** — how far ahead the scav is placed.
- **Face Scav Toward Player** — have the scav spawn facing you.
- **Diagnostics** — verbose logging for the spawn pipeline and bot rendering. Off by default; turn on only when reporting an issue.

---

###### This project is distributed under the MIT License — see `LICENSE` for details.
