# Running Terraria Agent

The current runtime uses vanilla Terraria **1.4.5.8**, a verified isolated game copy, pinned Harmony **2.3.3**, local XNA references and .NET Framework **4.8**. The controller requires .NET **8**. Install and prepare these local dependencies before building; the build script does not download game files or dependencies.

## Build and check

Run `scripts/Build.ps1` with the isolated game closed. `-CodeOnly` uses separate build output without replacing the active bridge or normal controller. Run `scripts/Test-All.ps1` for standalone offline checks; these do not establish game acceptance.

## Start and authorize

1. Keep the desktop available and use `scripts/Start-Challenge.cmd` to open the isolated main profile with B/C features enabled. Select your new Classic character and Classic world. The launcher refuses duplicate game runs. Entering a world does not automatically pause; immediately press **Ctrl+Shift+Home** and confirm the pause menu before starting the controller.
2. Start an action mode through `scripts/Run-Controller.ps1` with `-Arm`, then focus Terraria and press **Ctrl+Shift+Insert** once. Release all keys. This grants one explicit request; the controller does not repeat authorization automatically.
3. An explicitly selected `-InitialStart` flow also requires the launcher's `-AllowInitialControllerStart` option. This is a one-time initial authorization; stop, death or disconnect prevents automatic reuse. Ordinary observation does not grant control.

Use `-Mode observe` to inspect filtered observations without control. Available action modes include `stage-a`, `stage-b`, `recover-health`, `dig-test`, `seek-stone` and `forage-stone`. Newer resource modes are bounded development trials; they do not guarantee progression. `scripts/Recover-Health.cmd` reuses an existing isolated session and waits for fresh human permission.

## Stop, pause and resume

- **Ctrl+Shift+Backspace:** emergency stop.
- **Ctrl+Shift+Home:** request normal in-game pause when the current game/UI state permits it.
- **Ctrl+Shift+End:** close the agent-owned pause UI without granting control.
- Physical keyboard, pointer or gamepad input triggers manual takeover. Control remains stopped until fresh explicit authorization.
- `scripts/Stop-Agent.ps1` provides an additional local emergency-stop route.

After each short trial, confirm that the game is actually paused before leaving it unattended. Pause is a normal game UI operation, not an invulnerability mechanism. Use the game's **Save & Exit** before closing the program; closing a window alone does not prove that both character and world progress persisted.

The controller communicates only over authenticated localhost. Never publish connection credentials, saves or detailed local recordings and run evidence. Current A/B acceptance is in-game; C is partial. Stone acquisition, arrow combat and Boss progression remain unverified.
