# Calibration save/undo, automatic first-run setup, and calibration enhancements

## Context

Three issues on the `windows-background-service` branch:

1. **Mid-calibration settings can't be saved.** During a run, trial settings are applied live via `trial-update` but the only commit path is the final "Apply recommended settings" button after a confirmed recommendation. If the user likes a candidate mid-screening, their only options are cancel (restores original) or finishing the whole search. And when calibration *does* finish with a confirmed improvement, saving still requires a manual Apply click.
2. **Every new build requires an admin terminal** to run `Install-Service.ps1` by hand. The GUI already contains the exact elevated-install pattern needed (`BackgroundServiceMenu.cs:26-69`), and the installer script itself already supports update-in-place (stops service, recopies payload, restarts). The GUI currently does nothing at startup: `VersionHelper.ValidOrThrow()` at `Form1.cs:25` even crashes unhandled when the driver is missing.
3. **Calibration enhancements.** Valorant targets spawn with `Yaw = distance * cos(angle)` where wide targets reach ~25–30° almost purely horizontal (angle jitter only ±0.35 rad, `ValorantAim.cs:34-37`) — the user wants targets not to spawn far out on the x axis. Also, screening all ~12 candidates at 6 scored targets each + confirmation (~125 target presentations) frequently overruns the 6-minute deadline, which hard-aborts the whole search via `Interrupt` before any result.

User decisions: first-run setup is **fully automatic** (UAC appears on launch when a new version is detected); Valorant **Escape asks keep/discard**.

---

## Part 1 — Mid-calibration save + auto-save with undo

All in `grapher/CalibrationForm.cs` (+ `.Search.cs` / `.Valorant.cs` end-of-session changes).

**Refactor commit core.** Extract the body of `AcceptCandidate()` (`CalibrationForm.cs:803-842`) into `CommitCandidate(string expectedJson)`: pointer-settings check → `trial-commit` → verify `TrialState == "committed"` and response fingerprint matches `expectedJson` → set `accepted`, `AcceptedSettingsJson`, `ShowSavedState()`, show `restore` (the existing undo button), `cancel.Text = "&Done"`. Keep the existing lost-ack reconciliation (`trial-query`) and `RecoveryPending` fallback. `AcceptCandidate()` keeps its UI guards (`busy || running || !candidateReady || proposal == null || lease == null`) and delegates; auto/keep paths call the core directly.

**"Keep this setting" (desktop mode).** New button `keep` ("&Keep this setting") in the buttons flow panel. Visible while `running`, enabled when `lease != null && !stopping && fingerprint(active) != fingerprint(baseline)` — refresh its state in `ShowTrialState()` (called after every `SetTrial`) and at session start/finish. Handler: set `keeping = true`, `stopping = true`, `interruptReason = "Keeping the current trial settings."`, then unblock the running loops (`FinishCurrent()`; the existing `if (stopping) throw OperationCanceledException()` checks in `Block()`/`RunTarget()` unwind cleanly). In `RunSession`'s `finally`, when `keeping && lease != null` call `CommitCandidate(active)` instead of `CancelTrial()`. The host already supports this: `trial-commit` during `Phase == "preview"` commits the currently live candidate (`SettingsEngine.cs:397-444`).

**Valorant Escape keep/discard.** In the `KeyDown` handler (`CalibrationForm.cs:155`): when `valorantViewport && running && lease != null && active != baseline`, show a Yes/No MessageBox "Keep these trial settings?". Yes → the keeping flow above (with `StopValorant()` to unblock `ValorantBlock`); No → existing `CloseSafely()` behavior. During baseline/practice (active == baseline) Escape stays cancel-only.

**Auto-save on completion.** In `RunDesktopSearch` (`CalibrationForm.Search.cs:184`) and `RunValorantSession` (`CalibrationForm.Valorant.cs:287`), in the `improved` branch after `ShowProposal(...)` + `SetCandidateReady(true)`, call `await CommitCandidate(proposal)`. Inconclusive/manual-preview paths keep the explicit Apply button. Undo = the existing `restore` button → `trial-restore` → host `ApplyAndSave(baseline)`, unchanged.

## Part 2 — Fully automatic first-time setup (GUI does the installer script)

**New `grapher/Models/FirstRunSetup.cs`** (register in `grapher/grapher.csproj` near line 138):

- `EnsureDriver()` — called from `Form1` constructor replacing the bare `VersionHelper.ValidOrThrow()` (line 25). On driver missing/incompatible: if `installer.exe` exists beside the GUI exe, explain and run it elevated (`Verb = "runas"`, wait), then show "Restart Windows to finish driver installation" and exit; otherwise show instructions (install matching Raw Accel driver, restart) and exit.
- `EnsureServiceAsync(owner)` — fired from `Form1.Shown` (after the window is visible so UAC has context). Skip silently when `Install-Service.ps1` or the payload files aren't beside the exe. Detect work needed:
  - Service registry key missing (`HKLM\...\Services\RawAccelService`) → never installed → install.
  - `ServiceClient.IsEnabled` (reuse `service-shared/ServiceClient.cs:16-24`) → compare SHA-256 of the 4 payload files (`RawAccelService.exe`, `RawAccelService.exe.config`, `wrapper.dll`, `Newtonsoft.Json.dll`) in `%ProgramFiles%\RawAccel\Service` vs beside the GUI; any mismatch → update.
  - Key exists but `Start == 4` (user explicitly disabled via the menu) → respect it, do nothing.
- Install/update: snapshot live settings to a temp file (`DriverConfig.GetActive().ToJSON()`, same as `BackgroundServiceMenu.cs:41-43`), run elevated hidden PowerShell `-File "<startup>\Install-Service.ps1" -SettingsPath <snapshot>` — extract the elevated-run code from `BackgroundServiceMenu` into a shared helper both call. Exit code 0 → verify by polling `ServiceClient.SendAsync("status")` (~15 s). Success is silent; failures (or a cancelled UAC → catch `Win32Exception` 1223) show a one-line message and continue startup in standalone mode.

## Part 3 — Calibration enhancements

**Valorant spawn distribution** (`grapher/Models/Calibration/ValorantAim.cs:22-45`): widen the direction jitter from ±0.35 to ±0.7 rad (real vertical/diagonal variety) and clamp the horizontal component to per-category caps — Micro 3.6°, Medium 12°, Wide 18° — so no target spawns far out on the x axis while approach distance stays a genuine flick. Seed reproducibility, category round-robin, and shuffle are preserved (matched-geometry confirmation still works).

**Finish within the deadline:**
- Screening: 4 scored targets per candidate instead of 6 (`Block(6, "Screen ...", 100)` → 4 in `CalibrationForm.Search.cs:146`; `ValorantMeasure(..., 6)` → 4 in `CalibrationForm.Valorant.cs:249`). Update `DesktopScreenRank`'s `Count != 6` guard and the `searchTotal` / `valorantShotsTotal` formulas (7 → 5 per candidate).
- Soft deadline: the 6-minute timer no longer hard-`Interrupt`s once screening has started — it sets `outOfTime = true`; the candidate loop breaks, and confirmation runs with the best screened so far (confirmation time is bounded). If the timer fires during practice/baseline (no candidates yet), keep the current hard interrupt. Report which candidates were skipped.

## Tests & docs

- `calibration-tests/ValorantAimTests.cs`: assert `|Yaw| <= cap` for every condition.
- `calibration-tests/WorkflowTests.cs`: rework the acceptance assertions — with auto-save, a confirmed session commits automatically (saved file == candidate, accept shows "Applied && saved", no explicit `AcceptCandidate` call needed). Keep the "must not commit before confirmation" test (line 196-198 — still valid). Add: mid-run keep-commit (screening interrupted via keep → saved file == current candidate, restore visible, `trial-restore` returns baseline).
- `service-tests/Program.cs`: add a `SettingsEngine` test that `trial-update` followed by `trial-commit` mid-preview commits the updated candidate.
- Docs: update `doc/Calibration.md` (Keep button, Escape keep/discard, auto-save + undo, spawn behavior) and `doc/BackgroundService.md` (automatic first-run setup).

## Verification

1. `powershell -File tools/Build-Calibration.ps1` (full build incl. wrapper) or `tools/Build-UserMode.ps1` against the existing wrapper — build must pass and run calibration-tests, service-tests, Test-Installer.
2. Launch the new build without touching a terminal: confirm the service auto-installs/updates (UAC appears, then Background > Service status shows running + settings applied).
3. Desktop calibration: click "Keep this setting" mid-screening → verify settings saved + "Restore previous settings" undo works and restores.
4. Full desktop + Valorant runs: confirmed improvement auto-saves; Valorant targets stay within the yaw caps; Escape mid-Valorant offers keep/discard.
