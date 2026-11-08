# Personalized calibration implementation plan

Status: full workflow implemented after the user requested completion of the original scope. Service-backed and standalone automatic trials, existing curve families, measured refinement, non-first profile editing, explicit recovery and hermetic end-to-end checks are included. The C# pipeline passes against the existing wrapper; source wrapper rebuilding and the real hardware release matrix require a capable Visual Studio/Windows environment. See [Pointing calibration](Calibration.md) for current behavior.

This plan was based on the current working tree, including existing background-service changes. Three concurrent `gpt-6.1-terra` agents, each at medium reasoning effort, researched GUI/workflow, input/measurement, and optimization/service integration. The initial research was read-only; the same agents subsequently implemented separate owned modules and reviewed each other's work after user approval.

## Recommended scope

Add a native WinForms **Calibrate** command that presents ordinary targets, measures pointing performance, proposes conservative settings, and compares them with the user's original settings. The result is a starting point that remains editable through the existing controls.

Keep the kernel driver and its input-processing formulas unchanged. Use .NET Framework 4.7.2, C# 7.3, the current C++/CLI wrapper, existing JSON serialization, and the current service pipe. No game engine, online training, ML dependency, global input hook, new always-running component, or driver telemetry protocol is needed.

**Revised scope, implemented:** both standalone and service-backed sessions support automatic live comparisons and refinement. Standalone uses the existing service executable as an on-demand, private user-session host with the same recovery engine; it never installs a service. Existing curve families and non-first profiles are supported, with unchanged results when evidence or candidate bounds are inadequate. Earlier first-release-only restrictions below are historical design notes superseded by the current implementation and user guide.

An unchanged result is valid. Insufficient evidence, unsupported configurations, or failed safety checks must never force a new curve.

## Existing architecture and reuse

| Component | Existing behavior and proposed reuse |
| --- | --- |
| `grapher/Form1.cs:264` | Menu and profile workflows provide the entry point; use one modal calibration form and reuse theme application. Refresh user/active state and mouse device handles using the profile-switch pattern near lines 325–340. |
| `grapher/Form1.cs:362` | Existing `WM_INPUT` dispatch owns mouse input registration. Fan out parsed capture data here instead of registering another raw-input window. |
| `grapher/Models/Mouse/MouseWatcher.cs:688` | Raw-input registration and device filtering are reusable. Its latest chart delta and clamped elapsed time near lines 774–803 are not a trajectory recorder. Add an optional session sink without changing chart behavior. |
| `grapher/Models/Serialized/SettingsManager.cs:177` | Existing Apply validates, activates and persists. Do not call it for temporary candidates. Snapshot full serialized configurations because user/active configuration objects can alias. |
| `grapher/Models/Calculations/AccelCalculator.cs` and charts | Reuse curve display for the proposed result. Chart samples are explanatory, not a behavioral prediction or a replacement for stateful native simulation. |
| `wrapper/wrapper.cpp:570, 727, 821` | `ManagedAccel.Accelerate`, `SetProfileAt`, and `DriverConfig.Convert` provide native model evaluation, consistent profile/native-state updates, and parsing/validation. Round-trip clones through full configuration JSON. |
| `service/SettingsEngine.cs:44, 66` | One lock coordinates startup, resume and permanent Apply. `ApplyAndSave` stages/flushed data, applies and atomically saves, with rollback on persistence failure. Extend this authority for temporary trials. |
| `service/RawAccelService.cs:96` and `service-shared/ServiceProtocol.cs` | Existing local, bounded named-pipe protocol supports `status` and permanent `apply`; add capability-negotiated trial commands. Reuse ACLs and message-size checks. |
| `service-tests/Program.cs` | Console harness and fake `IAccelerationDriver` already exercise persistence, rollback, ordering and pipes. Extend it for trials and recovery. |
| `tools/Build-UserMode.ps1` | Builds C# components against a matching wrapper and runs service/installer checks. GUI source inclusion comes from explicit project Compile entries. MSTest wrapper tests are separate. |

The service manages configurations; mouse processing remains in the driver. Keep high-frequency recording and analysis out of the service.

### Validation prerequisite

`DriverConfig.Convert` calls `Errors()`, but the device-error branch at `wrapper/wrapper.cpp:750` appends profile errors instead of device errors. Device-only invalidity can consequently be missed. Fix this narrowly and add a regression test before trusting the validation boundary for new trial requests. Also implement finite-value rejection at the new candidate/trial boundary; native numeric comparisons are not a complete NaN/infinity guard. Keep unrelated wrapper hardening outside this feature.

## User workflow

1. **Open calibration.** One short instruction: move to each point quickly and accurately, then briefly hold inside it. Show Start, Cancel and progress. Explain that settings are a desktop pointing starting point. Preserve pending manual edits separately; canceling calibration restores those edits as well as the live baseline.
2. **Preflight and snapshot.** Check driver/wrapper compatibility, selected relative mouse and effective device/profile mapping, usable screen area, service capability/readiness, and supported optimizer configuration. Capture complete active driver JSON, committed-settings identity, pending GUI state, device/DPI/timing settings, display geometry and Windows pointer settings. Identify a shared profile that affects other mice; disclose this or decline isolated calibration rather than silently claiming per-device isolation.
3. **Practice.** Show 3–4 unscored targets. Use a single monitor and the same coordinate system for painting, recording and hit testing. Never warp, lock or hide the cursor.
4. **Baseline.** Start with approximately 24 valid trials covering three relative distances and eight directions, with targets inset from edges. Vary positions and use balanced seeded ordering. Define S as the observed cursor position at target presentation, not the prior target center or movement onset; construct/reject target placement using S and record actual distance. A target completes after roughly 150 ms observed inside its visible radius; provide a brief visual pause before the next target. Timeouts and failures remain in the results.
5. **Generate a proposal.** Show a short explanation and the ordinary Raw Accel curve/fields. In service-backed mode, try at most two conservative challengers, with matched/interleaved baseline trials and brief unscored acclimation after settings changes. Keep the one-second driver write delay and readback outside scored movement time.
6. **Validate/refine.** Use a fresh balanced target sequence to confirm the best challenger against baseline. At most one local refinement round; offer another calibration session instead of an unbounded search. If quality is poor, repeat invalid measurements within a fixed retry limit or report insufficient data.
7. **Results.** Show movement/acquisition time, failures, overshoot/corrections and a simple quality indication. Offer **Use settings**, **Recalibrate**, and **Restore previous settings**. Use settings commits once, replaces suspended pending edits with the accepted values, and shows a saved state with **Done** and **Restore previous settings**. Done returns to ordinary editable controls and ends the undo window. Before acceptance, recalibrate/close/cancel restores baseline and pending edits. After acceptance, closing/Done keeps the committed result; Restore is an explicit new permanent Apply of the original live baseline. Explain beforehand that this makes the original live settings the saved startup settings if those originally differed. If comparison is inconclusive, retain the original settings.

Use a few minutes as a pilot UX target, not a promised duration. Counts, dwell, target sizes and thresholds above are initial engineering defaults requiring user testing. Keep navigation keyboard accessible, include Escape, accessible progress labels, readable contrast and non-color status cues. Larger text must not change coordinate semantics. Pause/invalidate on focus, display/DPI, geometry or device changes; never silently score interrupted trials.

## Capture and measurement design

### What is actually observable

The driver modifies `LastX/LastY` before forwarding input (`driver/driver.cpp:96–139`). For enabled, handled relative input, `WM_INPUT` deltas are **post-Raw-Accel counts**, not original sensor motion. Record bypassed/disabled/unsupported input provenance explicitly. Raw input and the visible Windows cursor also differ because Windows pointer processing affects the latter. Receipt-time QPC timestamps are not hardware timestamps, and independently sampled cursor positions cannot be paired exactly with raw packets.

Do not enable or rely on `setExtraInfo` for calibration: it is off by default, overwrites metadata, packs original LONG deltas into signed 16-bit values, and provides no timestamp/validity marker. Do not invert accelerated counts to infer original motion or feed measured output speed directly into an input-speed offset.

Microsoft documents that only one window per raw-input device class can be registered in a process; use the existing owner. High-rate buffered input lacks individual hardware timestamps. See [RegisterRawInputDevices](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-registerrawinputdevices), [Raw Input overview](https://learn.microsoft.com/en-us/windows/win32/inputdev/about-raw-input), and [high-DPI mouse movement](https://learn.microsoft.com/en-us/windows/win32/dxtecharts/taking-advantage-of-high-dpi-mouse-movement).

### Minimal data contracts

Freeze these contracts before parallel implementation. Use concrete internal classes/structs, not a plugin framework.

| Contract | Required contents |
| --- | --- |
| `CalibrationContext` | Schema version, session/seed, full normalized baseline JSON and fingerprint, chosen device/profile identity, configured DPI/poll/timing parameters, pointer settings, geometry/coordinate convention, QPC frequency. |
| `MotionSample` | Monotonic receipt ticks, device identity/handle, post-driver dx/dy, input flags/button state, trial identity, capture-quality flags. |
| `CursorSample` | Monotonic observation ticks, client-coordinate x/y, geometry version and validity. Keep separate from raw packets. |
| `TargetTrial` | Actual start and target center/radius, presentation/onset/entry/dwell/timeout times, configuration fingerprint, bounded sample collections, outcome and exclusion reason. |
| `TrialMetrics` | Metrics below plus valid/invalid/timeout distinctions and timing-resolution/data-quality metadata. |
| `CalibrationProposal` | Full candidate JSON, baseline fingerprint, changed profile/parameters, model bounds, support/quality status and validation summary. |

Capture appends into a bounded per-trial buffer; raw-input callbacks perform no chart painting, disk/network I/O, candidate evaluation or service calls. The session sink must see checked records before the existing chart filters discard zero-motion/buttons or other-device input, so it can detect interference without changing chart behavior. Analyze immutable completed trials off the UI thread. Sample cursor position independently on a short UI cadence, timestamp each observation, and detect scheduling gaps. Do not advertise 8 kHz cursor-path resolution merely because raw input reports at 8 kHz. Known buffer overflow/loss or cursor-sampler gaps beyond an explicit tolerance invalidate affected measurements; the ordinary absence of raw packets while stationary is not input loss. Dwell means observed containment with a maximum inter-sample gap, not proof of containment between observations.

### Metrics

Let S be the actual start, T the target center, D=|T−S|, u=(T−S)/D, r the target radius and P(t) the observed cursor path. Reject D=0 and non-monotonic timestamps. Store positions in one client-coordinate system; scale errors by radius/distance when comparing trial conditions.

| Measurement | Definition |
| --- | --- |
| Reaction and movement time | Target presentation to sustained movement onset; onset to first target entry and to successful dwell completion, reported separately. Onset thresholds use distance/time hysteresis. |
| Speed | Windowed path length / elapsed time in client units/ms; separately approximate post-driver counts/ms over coarse 10–25 ms windows. No unsupported physical cm/s or native pre-acceleration speed claims. |
| Overshoot | Maximum positive axial displacement `(P−T)·u`, with cross-track error reported alongside it so a wide lateral miss is not mistaken for a target crossing. |
| Undershoot | Remaining positive axial distance at the first meaningful deceleration/stop before arrival. Define stop by a sustained speed threshold; mark unavailable when no defensible stop exists. |
| Corrections | Significant post-initial-movement reversals/submovements using minimum distance and duration hysteresis, avoiding noise-counting. |
| Jitter/stability | RMS displacement around the mean in a fixed post-entry observation window. Include unstable exits/failures where the complete window is available, mark incomplete windows censored, and report dwell failure separately. Successful-only terminal dwell jitter is descriptive and must not select a candidate by itself. Label this observed cursor stability, not a diagnosis of physical tremor. |
| Settling | First observed entry that begins the required continuous dwell. Report dwell completion separately; unsettled/timeout is a failed or censored observation. |
| Accuracy/efficiency | Minimum/terminal radial error, failed entries/timeouts, cross-track deviation and D/path length. Keep failures in performance comparisons. |

For v1, target acquisition is automatic dwell; clicking is not required. If click trials are added later, report click accuracy, onset-to-click time and post-click stability separately.

Bind capture to the selected physical device; invalidate on other-device movement, unknown/zero identity, absolute packets, reconnect, clipping at a screen boundary, focus/geometry/configuration changes or capture overload. Keep a zero-delta interval valid as stationary time. Do not remove ordinary bad aim, overshoot or misses as outliers. Retain all failure counts; distinguish environment-invalid trials from genuine performance failures.

## Optimization approach

Use a small deterministic parameter search and real human comparisons. A trajectory does not uniquely identify the best acceleration curve, and replaying it cannot predict how the user will adapt.

1. Always include the unchanged baseline. Clone the entire configuration; modify only the selected profile and preserve device assignments, other profiles, base sensitivity, axis ratio/rotation, DPI, polling and unrelated options.
2. Initial automatic support: whole-magnitude, scalar no-acceleration profiles, plus simple existing classic profiles only where relative curve bounds can be established. Unsupported LUT, asymmetric/directional or heavily stateful configurations receive an honest no-change/unsupported result. Broaden support only with dedicated model and behavioral tests.
3. For no-acceleration profiles, use the existing classic legacy family (`gain=false`, `capMode=output`) with fixed exponent 2, positive acceleration, input offset and output cap. Start with modest cap proposals such as 1.05/1.10/1.15 relative to the unaccelerated multiplier. These are provisional search limits, not validated universal safety limits. The actual formula is `1 + acceleration * (speed-offset)^2 / speed` above offset, capped by the native implementation; do not confuse acceleration with a linear slope. Derive acceleration only with finite representative speed > offset >= 0, cap > 1, and positive finite native time/DPI factors; handle speed zero explicitly.
4. Use a small fixed grid in native input-speed units, constrained by the selected device's known normalization/timing configuration. C0 implementation defaults are offset 5, reference speed 30 and caps 1.08/1.12, with 150 ms dwell, 5-second timeout, 12 matched pairs, at least 5% median improvement and at least two-thirds pair wins. Native model checks cover zero, both axis signs and multiple DPI/time factors. Automatic generation now also adjusts existing curve families through bounded range changes and validates LUT knots and stateful behavior; non-first profiles are preserved and editable. These are pilot defaults, not empirically validated optimal values. Post-driver measured speed is descriptive only.
5. Use `DriverConfig.Convert` and independent native model instances for finite outputs, continuity/monotonic behavior, low-speed preservation and bounded relative sensitivity across a documented native-speed/direction envelope, including endpoints/asymptotes. A sampled grid is a numerical check, not proof beyond its envelope. Replay ordered synthetic sequences for stateful behavior; use configured native time/DPI factors and fresh instances. Do not rank predicted human performance from replay.
6. Compare candidate and baseline through matched short/medium/long target conditions, counterbalanced/interleaved order and repeated real movements. Score completion time with a fixed timeout penalty for failures; report accuracy, corrections, jitter and settling separately rather than burying all behavior in arbitrary weights.
7. Require a repeatable meaningful improvement with no accuracy/stability regression in any distance group, followed by an untouched confirmation set. An initial criterion could require at least 5% paired median improvement and wins on at least two-thirds of matched trials, subject to failure/stability guardrails. These are tunable pilot rules, not statistical confidence claims. Do not call the result significant or optimal. If evidence is mixed or inadequate, keep baseline.

Desktop cursor performance includes Windows pointer settings and does not establish equivalent aim improvement in every game. Do not change Windows pointer settings, hardware DPI or in-game sensitivity automatically.

## Safe trial and persistence integration

### Service-backed trials

Extend the current authority, rather than adding a second permanent-apply path:

- Advertise a trial protocol capability/version in status. An old or unreachable service must cause a clear unsupported/error result, never fallback to permanent Apply for a preview.
- Add begin/update, renew, query, cancel and commit operations with an opaque lease ID, request identity, baseline/expected fingerprints and deadlines. Bind the lease to the authenticated local account plus a high-entropy lease capability; this distinguishes sessions possessing the token but is not independent process authentication. Do not claim the account SID isolates same-user processes. A single lease owns the machine-wide driver at a time. Reuse existing message limits and validate all input.
- Under `SettingsEngine`'s existing gate, read and normalize the entire active baseline. Capture the committed-settings identity separately; `.lastgood` is not a substitute for the live baseline. Reject normal service Apply/reset while a trial is active.
- Durably write/flush an atomic recovery journal before changing the driver. Track baseline, previous expected live configuration, next candidate, original committed identity, lease and transaction phase. Update it before every candidate transition, so a crash before activation still recognizes the previous candidate as an owned state. Leave committed startup settings unchanged during preview.
- Apply through the wrapper, honor `DriverConfig.WriteDelayMs`, then read back the effective configuration. Start scored trials only after successful acknowledgement/readback and brief acclimation. Compare normalized semantic fingerprints, not JSON whitespace or raw serialization order.
- Use an engine-owned expiry timer. Proposed defaults: 30-second lease renewed every 5 seconds, with a finite overall session limit. The current pipe is request/response and closes after every request, so ordinary pipe closure is not owner death; missed renewals trigger recovery. Query/retry operations must handle lost responses and duplicate requests idempotently.
- Cancel/expiry restores the exact live baseline without rewriting committed settings. GUI cancellation, focus loss, device changes, session lock and service stop/resume terminate the trial. Restore/readback failure remains an explicit unresolved error; never display success.
- Commit verifies ownership, deadline, expected active fingerprint and persisted identity, then uses atomic persistence. Journal commit intent includes pre-commit saved identity and intended new content/identity. Define reconciliation for each acceptable live/saved combination around Apply, atomic file replacement and acknowledgement; unknown live or disk edits are conflicts, never inferred success. After a successful file replacement, recovery must recognize a completed commit rather than undo accepted settings. A failure before commit restores the original baseline, not merely the last candidate.
- Process recovery before accepting new requests or ordinary startup/resume restoration; do not let a late boot retry overwrite an active trial. Retain unresolved recovery evidence. For an interrupted trial, restore live baseline and suppress ordinary startup restore for that service start, preserving committed settings and reporting any original divergence. Failed/conflicted recovery blocks ordinary retry/resume overwrites until resolved. After successful recovery, the next deliberate restart or later resume returns to the existing saved-startup policy; report that distinction if baseline and saved settings differ.
- Before rollback, compare live state to the expected trial/baseline states. If an external direct writer changed it, stop and report conflict rather than overwrite that later choice. The existing driver has no atomic compare-and-write, so external writes can still race a check; document this limitation and advise closing other settings writers during calibration.

Service crash recovery depends on Windows restarting the service; no software can restore during power loss. Preserve committed settings, journal safely, and test the actual recovery claims. Do not promise uninterrupted recovery when the service itself is disabled or cannot access the driver.

### GUI and manual state

Temporary trial APIs must bypass `SettingsManager.TryActivate`'s permanent persistence. Keep original live settings, pending manual form edits and committed startup settings distinct. Explain before Start that acceptance replaces pending edits; preserve them until acceptance or cancellation is resolved. After accepted commit, update the GUI's user/active objects and existing controls/charts without a second permanent Apply. On pre-accept cancel, restore pending edits and reload verified live settings appropriately. If GUI reload fails, show the verified driver/persistence outcome separately from the UI error, never imply driver rollback occurred. Retain the baseline and pending-edit backup while the saved-result modal remains open. An explicit post-accept Restore is a new permanent Apply of the original live baseline, checks for intervening changes, then reinstates pending edits. It does not promise to recreate a different original startup file. Done/closing the saved-result modal releases this backup; subsequent changes use ordinary manual/profile workflows.

Keep samples in memory by default and discard them at session end; no telemetry upload or automatic detailed trace logging. An optional explicit local diagnostic export can be added only if needed for validation.

## Independent implementation tasks and ownership

The research above was delegated concurrently; the tasks below are proposed future implementation work. A coordinator owns shared integration files so parallel workers never edit the same file.

| ID | Task and exclusive file ownership | Depends on | Completion evidence |
| --- | --- | --- | --- |
| C0 | Freeze DTOs, support matrix, trial state/error semantics, native-unit bounds and standalone release policy. Coordinator owns a new `grapher/Models/Calibration/CalibrationContracts.cs` and protocol contract decisions. | None | Reviewed contracts and deterministic example sessions; no behavioral code yet. |
| C1 | Correct device validation reporting with a targeted regression. Wrapper owner: `wrapper/wrapper.cpp`, `wrapper-tests/*`; finite rejection at the new candidate/service boundary belongs to C3/C4. | C0 | Device-only invalidity fails before the reporting fix/passes after it; existing wrapper tests pass. C3/C4 demonstrate rejection of non-finite candidates separately. |
| C2 | Capture/session recording. Input owner: `grapher/Models/Mouse/MouseWatcher.cs`, new `grapher/Models/Calibration/CalibrationCapture.cs` and capture-specific tests. Expose a session sink, but leave Form1 hook-up to C6. | C0 | Timing/provenance, device filtering, bounded memory and overload tests; no change to chart behavior. |
| C3 | Pure metrics and proposal generation. Analysis owner: new `CalibrationAnalysis.cs`, `CalibrationOptimizer.cs` under `grapher/Models/Calibration/`, and analysis-specific test files. | C0; C1 before real validation sign-off | Synthetic path expectations, unsupported/no-change outcomes and native candidate-bound tests. Can develop against frozen contracts while C1/C2 proceed. |
| C4 | Trial protocol, authority, timer, journal and recovery. Service owner: `service-shared/*`, `service/SettingsEngine.cs`, `service/RawAccelService.cs`, new service trial/journal files and `service-tests/Program.cs`. | C0; C1 before accepting real candidates | Deterministic fake-driver/clock, pipe, crash-phase and rollback tests; committed file untouched during previews. |
| C5 | Calibration form and painted target workflow. UI owner: new `grapher/CalibrationForm.cs`, any form-specific designer/resource files, and `CalibrationSession.cs`; uses C0 contracts and trial client signatures. | C0; C2/C3/C4 for integrated behavior | Seeded targets, navigation/state transitions, invalid-trial handling and accessible results. Use explicit not-connected state until real integration exists. |
| C6 | Integration and build wiring. Coordinator alone owns `grapher/Form1.cs`, `grapher/Form1.Designer.cs` if necessary, `grapher/Models/Serialized/SettingsManager.cs`, GUI project files, new calibration test project/harness, `rawaccel.sln`, and `tools/Build-UserMode.ps1`. | C1–C5 | One real capture path, no duplicate registration, no preview persistence, correct pending-edit restoration, all project sources included. |
| C7 | Documentation/release validation. Validation owner: `doc/Calibration.md`, calibration-specific test scripts/evidence; coordinator updates existing `doc/Guide.md` and `doc/BackgroundService.md` to avoid overlapping current changes. | C6 | Automated gates plus real Windows recovery/accessibility matrix below. |
| C8, implemented | Standalone automatic trial host. Same service owner extends `service/Program.cs` with an on-demand user-session host reusing the trial engine, private authenticated IPC and durable user-local recovery; coordinator handles packaging/client selection. No new always-running service. | C4; must precede standalone auto-trial release | Host acknowledges baseline ownership before activation; GUI death, orphan/host failure, commit, restart and packaging tests. Host termination itself can only recover at a specified next launch/recovery trigger unless an independent survivor exists; no immediate rollback guarantee. Separate estimate/design review required; not silently folded into C4. |

Execution waves: C0 first; then C1/C2/C3/C4/C5 can proceed within available agent capacity and their stated gates; then C6; then C7. With four total active slots, run three implementation owners concurrently while the coordinator integrates completed work. C3 and C5 may prepare pure logic/UI against contracts, but cannot declare end-to-end completion before real dependencies land. Avoid a generic calibration framework or additional production project solely for sharing a few types.

## Validation and release gates

**Pure logic:** straight hit, overshoot/return, undershoot/correction, lateral miss, jitter, dwell interruption, stationary intervals, timeouts, empty/zero-length paths, duplicate/out-of-order timestamps and noisy onset/correction thresholds. Verify balanced deterministic target generation, no edge targets, radius/distance normalization, environment-invalid versus genuine failure handling, and fresh confirmation trials. Never make failed trials disappear from the score.

**Native settings/model:** actual wrapper conversion and round trips; all untouched profiles/device settings preserved; low/high-speed boundaries, finite values, output caps, continuity and supported direction/time/DPI factors. Test no-change for insufficient data and unsupported baselines. Existing chart appearance alone is not validation.

**Service/recovery:** begin/update/renew/expiry/cancel/commit; duplicate and lost responses; old clients/services; wrong owner/lease; invalid/oversized payloads; simultaneous Apply/reset; startup retry/resume/stop races; storage/driver/readback failures; failure to restore; every journal crash boundary including commit-before-ack; corrupted journal; external writers and mismatched committed/live baselines. Failed restoration must be visible and recoverable. Use deterministic time advancement rather than slow real sleeps where practical.

**GUI:** cancel/close/Escape at every stage, pending manual edits, result acceptance and later restore, recalibration state isolation, service unavailable mid-session, device reconnect, focus/session lock, resize/display/DPI changes and chart refresh. No candidate persists before acceptance; no permanent Apply is used as a preview.

**Real Windows matrix:** supported Windows 10/11 releases; x64 and ARM64 where the existing wrapper/driver are supported; service absent/disabled/old/running/unreachable; 125/1000 Hz and high-polling mice up to 8 kHz where available; two mice, multiple profiles, differing configured DPI, pointer acceleration on/off, 100/150/200% display scaling and mixed-DPI monitors. Exercise GUI termination, service crash/restart, sleep/wake and reboot with the actual signed driver. Verify active driver configuration and saved files independently before/after each case. Measure UI responsiveness, memory bounds and timing gaps under load.

**Build:** run the existing user-mode helper with a matching wrapper and compiler, new calibration harness, existing service/installer tests, and separate MSTest wrapper tests. Because C1 changes C++/CLI code, using an old release wrapper does not validate that fix: rebuild the wrapper with the normal Visual Studio toolchain for final sign-off. No driver rebuild/signing or installer change is expected for the service-backed design. The implemented C# helper and included checks pass; C++/CLI rebuilding and the real hardware matrix remain unverified in this environment.

Ship only when cancellation, abort, timeout and inconclusive outcomes restore baseline unless an explicitly reported external conflict prevents it; acceptance retains the committed candidate. Accepted settings persist exactly once, failed recovery never reports success, supported modes demonstrate their crash/restart claims, ordinary manual tuning still works, and a small human pilot confirms that reported improvements survive fresh trials. Do not require every user to receive a changed configuration.
