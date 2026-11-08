# Mouse calibration

Open **Advanced > Calibrate**, move the mouse you intend to use, and choose the mode. Enable **Valorant mode** and enter your in-game hip-fire sensitivity for first-shot aiming. Desktop pointing uses a cursor and a short hold inside each target. Keep hardware DPI fixed throughout the session.

## A finite search across curve families

After three practice targets and twelve baseline targets, calibration screens a catalog of up to twelve settings profiles. It covers Off, Linear, Classic, Natural, Jump, Synchronous, Power and LUT, plus sensitivity and gain variants. The current profile represents its own unchanged candidate. Measured overshoot or undershoot can shift the base sensitivity of the curve candidates by 10%. Curves have distinct offsets, caps and shapes; candidates that exceed the native-model response bounds are discarded.

Each profile gets one unscored acclimation target and four scored targets. The strongest screened candidate gets one fresh comparison against the original profile with twelve scored targets on each side. Misses and timeouts count. Progress shows the current profile block, target count and overall total. The session stops after six minutes: once screening has begun, running out of time skips the remaining candidates and still runs the fresh comparison for the best screened so far, so the session always ends with a usable result.

The native model checks finite output, direction and bounded response across speeds and stateful sequences before a proposal can reach the driver. The family search bounds relative output to 0.8–1.2 times the original profile where the native model can validate the comparison. When the fixed Natural preset does not fit an existing curve (including Linear), calibration fits a small deterministic grid of Natural parameters to the baseline through the native wrapper, then applies the same safety checks. Candidate fitting runs away from the UI/input thread. The catalog copies device assignments, physical DPI configuration and other profiles from the session snapshot. It searches a finite set of curve and sensitivity settings rather than every combination of profile fields.

## Valorant hip-fire

The crosshair stays centered in a fullscreen 16:9 view. Aim at each target and click once. The first click scores the shot, and each target has a three-second timeout. Press **Escape** to stop: if a trial setting is currently live, a dialog asks whether to keep it (saving it to the profile immediately, with an undo option afterwards) or discard it and restore the original settings. The view uses post-driver relative mouse counts, a 0.07-degree hip-fire scale at sensitivity 1 and a 103-degree horizontal field of view.

Screening prioritizes first-shot hits, then movement time. Confirmation protects accuracy and timeouts in each shot category, checks matched target geometry and compares movement-to-shot timing separately from reaction. Stationary targets cover small corrections, short flicks and wide turns, spread across vertical and diagonal directions; the horizontal (yaw) component of every target is capped per distance category so wide turns never spawn far out on the x axis. Test transfer in Valorant using the [Range comparison protocol](ValorantCalibrationPlan.md#validate-transfer-to-valorant-with-players).

## Desktop pointing

The desktop session maximizes the window and hides the measurements table until review. Hold the small center marker, move quickly and accurately to the next circle, and hold briefly. Targets use short, medium and long fractions of the available space in each direction, including the far horizontal area on wide screens. Desktop screening prioritizes completed targets, then acquisition time with a first-movement error penalty. Confirmation compares acquisition, first-approach accuracy, corrections and stability separately at short, medium and long distances.

## Finished results and applying a profile

The result panel shows the strongest screened candidate, actual settings changes, confirmation outcome and completed trial list. When the fresh comparison confirms an improvement, the recommendation remains a temporary preview. Select **Apply recommended settings** to save it, or **Done** to restore the original settings. **Keep this setting** (available while a trial setting is live, i.e. after the baseline, during screening or confirmation) saves the currently live trial settings immediately and ends the session. **Ctrl+Enter** performs the same action, including in the fullscreen aiming view. The button is disabled while switching settings and enabled once the candidate is active. In Valorant mode, pressing **Escape** while a trial setting is live offers the same choice as a dialog.

You can also select any completed profile trial and press **Preview selected profile**. This immediately activates that trial profile. The label identifies it as a manual choice and **Apply selected profile** saves it explicitly. Preview shows the selected profile's screening measurements alongside the original baseline. **Done** closes the review and restores the original settings for an unsaved preview. After saving, Done keeps the saved profile. **Restore previous settings** applies and saves the original profile again.

Results describe this session and the catalog tested. A completed search can favor the original profile or produce mixed evidence. The tested profiles are available for an explicit manual choice in either case.

### Reading the numbers

- **First error +/- (px):** positive means the first movement ended beyond the circle, negative means it stopped short, and zero means it stopped inside. The first stop is detected from a sustained slow stop or a meaningful reversal; corrections are measured separately.
- **Before/after mean first error:** average absolute distance outside the circle. Overshoots and undershoots do not cancel each other out. Lower is better.
- **First movement / correction time:** milliseconds spent on the initial approach and the following correction. Live correction time is elapsed time; final correction time ends at the successful hold's entry. A clean approach with no correction reports zero.
- **Reaction time:** target appearance to detected cursor movement, in milliseconds. Movement onset uses a small displacement threshold (at least 2 px, or 10% of the target radius) to ignore tiny jitter. No detected movement displays a dash. The appearance timestamp is recorded immediately after drawing the target; times reflect application sampling, not a hardware display/polling measurement.
- **Movement to hit:** detected movement start to the first sampled cursor position inside the target circle, in milliseconds. It excludes reaction time, the 150 ms confirmation hold and corrections after the first hit. A miss displays a dash. These timing metrics appear as before/after averages and changes after each trial; lower is faster. These rows are reported separately and do not replace the existing acquisition/accuracy/stability checks or confirmation round.
- **Corrections, jitter, settling, speed, successful and failed targets:** measured counts, pixel distances/rates, milliseconds and percentages as labeled. Pixel values use the target panel's client coordinates.
- **Change:** after minus before in the row's units. A dash means unavailable data, not zero.

For a desktop stability decision, the app compares median jitter as a fraction of target radius separately for short, medium and long movements. Within each distance, the limit is 1.10 times the original median plus 0.01 target radii. The table's pixel average can fall while one distance exceeds its limit. A rejection identifies the distance and either its measurements or the missing data. When both exploratory adjustments fail, each has its own result line. A successful profile's score and headline use a benefit that met the comparison rules.

After acceptance, the saved-results view offers **Done** or **Restore previous settings**. Restore verifies that no other writer changed the configuration, then applies and saves the original live settings. If those originally differed from saved startup settings, this makes the original live settings the new saved configuration. Done/closing after acceptance keeps the result. Undo is bounded by the original ten-minute session limit.

After Done, the main editor selects the calibrated mouse's acceleration profile. Use **Advanced > Edit acceleration profile** to select another profile and continue manual tuning with the existing controls and Apply. Shared profiles still affect every mouse assigned to that profile; device assignments are preserved. The original Charts, Advanced, Themes, Background and Help top-bar menus remain in place; profile and calibration commands are grouped under Advanced.

## Existing curves and personalization

Calibration can start from Off, Classic/Linear, Natural, Jump, Synchronous, Power and LUT curves, including separate-axis, directional and smoothed configurations. It automatically tracks the moving mouse internally and uses its effective assigned profile, including non-first profiles. Device identifiers are not shown. Other profiles, device mappings, device DPI, polling and unrelated parameters are preserved.

The family catalog uses first-approach bias to adjust base sensitivity. Cross-family proposals can replace the selected profile's curve family and its corresponding parameters, including LUT data. Other profiles and device assignments come from the original snapshot. No candidate is derived by inverting post-driver mouse speed.

The native model probes finite output and bounded relative changes across directions, time/DPI factors, stateful sequences, a dense speed sweep, and LUT knots and neighbors. A zero-output LUT region is preserved. Profiles with unusual response ranges may admit a subset of the catalog. Physical trials and fresh confirmation determine the recommendation within that catalog.

Raw-input deltas are post-Raw-Accel counts for handled relative input. Cursor trajectories also include Windows pointer behavior. Reported timestamps measure application receipt/sampling rather than hardware events. Incomplete metrics are shown as unavailable. Capture is bounded and detects other-device motion, interruptions and overload; ordinary bad aim is not discarded as an outlier.

## Service and standalone operation

With a healthy compatible background service enabled, that service owns the temporary trials and saved startup configuration. An enabled but outdated/unreachable service is reported as an error rather than bypassed. Calibration never installs or enables the service automatically.

With the service absent or disabled, the same automatic workflow runs through an on-demand **RawAccelService.exe** helper beside the GUI. It uses a private current-user local pipe and the same trial engine. Only one standalone authority can own the machine-wide driver. It does not register a Windows service or add a startup task. Temporary trials leave portable `settings.json` untouched; acceptance persists to the GUI's existing portable settings path. Journals and completion receipts live under `%LocalAppData%\RawAccel\Calibration`, keyed by that portable path.

The helper owns a 30-second lease renewed every five seconds, with a ten-minute session limit. It survives the GUI closing/crashing long enough to restore an unaccepted trial. Closing the dialog requests helper exit only when no trial or recovery remains pending; it never kills the recovery authority. An idle helper also exits automatically. A different portable folder can acquire authority after the previous idle helper releases it.

## Interruptions and recovery

Focus loss, session lock, device changes, display/geometry changes and pointer-setting changes interrupt measurement. Missing renewals trigger restoration; transient failures are retried. Durable journals cover interrupted candidate transitions, and bounded receipts allow lost commit/recovery acknowledgements to be resolved after restart.

If restoration cannot be confirmed, the dialog reports that and offers **Close; recovery pending**. Closing does not establish successful recovery. Reopen calibration to inspect the host's recovery state. If the host reports a pending conflict, **Restore previous settings** shows an explicit confirmation and restores the pre-calibration live baseline only after checking the current live fingerprint again. The saved startup/portable file remains unchanged; a changed saved file is disclosed. Recovery also refreshes active labels without discarding pending manual edits.

Automatic recovery intentionally refuses to overwrite an unrecognized configuration. This can happen after another writer changes settings or after a reboot resets the driver to defaults. The explicit recovery choice handles that case without manually deleting journals. If no journal exists and the host is unavailable or the driver cannot be read, correct that reported problem rather than assuming settings were restored.

Close other settings writers while calibrating. The driver has no atomic compare-and-write operation, so a direct writer can still race a check. No process can restore during power loss; a terminated standalone helper resumes journal recovery on its next launch. The background service resumes it when restarted. Samples stay in memory and are discarded when the dialog closes; no trajectories are uploaded.

## Build and tests

For a complete source build, use Visual Studio 2022 with C++/CLI v143, the Windows SDK, .NET Framework 4.7.2 targeting pack, NuGet CLI and Visual Studio Test Platform:

```powershell
.\tools\Build-Calibration.ps1
```

This rebuilds the current C++/CLI wrapper, builds the GUI and both settings hosts, runs service/installer/calibration checks and the wrapper MSTest suite, including device-only validation and minimum-speed serialization regressions. The GitHub Actions calibration workflow invokes this build on Windows. It does not install the driver or change real mouse settings.

To build C# against an existing matching wrapper instead:

```powershell
.\tools\Build-UserMode.ps1 -CscPath '<C# 7.3-or-newer csc.exe>' -WrapperDirectory '<matching wrapper folder>'
```

That helper builds against the supplied C++ wrapper. Automated coverage includes service tests, separate fake-driver host processes, native-model bounds checks and GUI workflows for acceptance, restore, cancellation and inconclusive results. The Valorant workflow fixture sends relative mouse packets through the GUI's input handler and exercises first-shot improvement, failure to repeat an improvement on fresh targets, and interruption by another mouse.

For a UI render, run `calibration-tests.exe --render-ui <absolute-png-path>`. Tests use simulated mouse traces and a memory/file-backed test driver; they never start a trial against the installed driver.

Public-release validation still needs actual mouse acquisition, 125 Hz–8 kHz devices, mixed-DPI displays, sleep/wake and reboot on supported Windows/architecture combinations. Automated simulation cannot stand in for physical user calibration or establish a universal performance improvement.




