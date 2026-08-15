# HideoutShootout Server Companion (Scaffold)

This folder contains the initial scaffold for a companion SPT server-side mod.

## Goal
Provide server-side prerequisites for hideout bot spawning so the client mod can reliably spawn a hideout scav target.

## Current status
- Scaffold only (safe placeholders)
- No behavior changes yet
- Not wired into the current client solution

## Planned responsibilities
1. Prepare/override hideout-relevant bot spawn prerequisites.
2. Expose minimal config flags for hideout-only behavior.
3. Keep changes isolated from regular raid spawn behavior unless explicitly enabled.

## Suggested next implementation phase
- Wire this project against your local server-csharp references.
- Implement a concrete patch path that guarantees bot-system readiness in hideout sessions.
- Add clear diagnostics for when prerequisites are still missing.
