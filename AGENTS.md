# Development constraints

This repository implements a Terraria gameplay agent. Follow the user's challenge rules and `docs/PLAN.md`.

1. Prioritize the currently failing stage: A before B, B before C, C before main-challenge D. Never treat compilation, mock observations or command acknowledgements as in-game success.
2. Use a new Classic character and Classic world in an isolated save directory. Back up existing local and Steam Cloud save files before launching any new game workflow. Never overwrite or load the user's existing characters/worlds for experiments.
3. The main challenge must use normal gameplay. No inventory spawning, health/damage edits, teleportation, time skips, unexplored terrain or hidden ore. A separate prepared combat save must have independent results and an explicit `combat_test` label.
4. Filter observations inside the in-process C# bridge before serialization. Background communication threads must never access Terraria world/player objects. Retain only legally observed history.
5. Keep communication local. Require sequence numbers, bounded lifetimes, session identity and explicit control ownership. Stop on expiry, disconnect, death, world change and emergency/manual takeover. Reconnection cannot rearm control automatically.
6. Craft, place and equip through verified normal game mechanisms with material, reach and station checks. Never write item stacks to manufacture a success.
7. The user selected current vanilla Terraria 1.4.5.8 with a lightweight bridge. Verify real game method signatures, lifecycle and the exact loader source/dependencies before using them. Do not use tModLoader APIs in vanilla. Current A uses our fixed-purpose Host and custom bridge with pinned Harmony; it does not load upstream Injector or Core. Use an isolated game copy; exclude Vault, DebugTools and optional gameplay mods. Do not execute upstream all-mod deployment scripts. See docs/SECURITY_REVIEW.md and docs/DEPENDENCIES.md for audit boundaries.
8. Start with a rule-based planner and continuous local controller. Keep model planning optional and low frequency. Ask the user before using a paid runtime API; the ChatGPT subscription is not assumed to supply API credits.
9. Keep retry budgets finite. Every skill needs preconditions, result-based success checks, timeouts and recovery reasons.
10. Preserve source, scripts and stage evidence. Record task, actions, results, failures, deaths and manual takeovers. Screenshots are not continuous recordings. Do not publish saves, credentials or raw private logs to GitHub.
