# Terraria Agent

A local, rule-based agent for vanilla Terraria **1.4.5.8** on Windows. A C# bridge filters observations inside the game, a continuous controller executes bounded actions, and state-machine skills decide what to do next. No runtime model API is required.

## Current status

- **A verified in-game:** observation, movement, stopping and jumping, plus disconnect, lease-expiry, emergency-stop and manual-takeover checks.
- **B verified in-game:** discover a visible tree, approach, chop with a normal axe, collect wood, craft a workbench with a normal recipe and place it.
- **C partially verified in-game:** selected normal crafting and placement, soil mining and natural health recovery. Stone acquisition, arrow combat and Boss progression remain unverified.

Compilation and synthetic checks are reported separately from game acceptance. The long-term goal is normal progression toward the Moon Lord; it is not a completed capability.

## Boundaries

Use a new Classic character and Classic world with isolated local saves. The main challenge permits normal materials, recipes, reach, tools and damage. It does not permit item grants, health or damage edits, teleportation, time skips or hidden terrain access. A separate combat-test profile has separate acceptance.

The bridge exposes own state, currently visible filtered targets and limited legally observed history. Communication binds only to **127.0.0.1**, requires authentication and explicit ownership, and uses bounded frames and short action leases. Expiry, disconnect, death, unsafe UI, world changes and takeover release input. Reconnection cannot authorize control.

Human authorization and emergency controls remain available. The optional initial controller start requires an explicit launch option and is consumed once. A stop cannot be cleared by automatic rearming.

## Development

The fixed-purpose Host loads an isolated verified game copy and our bridge with pinned Harmony **2.3.3**. Game APIs are checked against the installed assembly; the runtime does not depend on tModLoader. Normal crafting uses `CraftingRequests.CraftItem`, and ordinary melee facing uses guarded `Player.ChangeDir`. Item use and movement remain normal game controls. Host and Bridge target .NET Framework **4.8**, while the controller and standalone checks target .NET **8**.

See [running instructions](docs/RUNNING.md). Source, launch scripts and offline checks are included. Credentials, saves, recordings and detailed local run evidence are excluded from public publication.
