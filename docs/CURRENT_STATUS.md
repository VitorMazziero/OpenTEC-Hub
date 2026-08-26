# TECNAL-Hub current status and stabilization audit

> **Audit date:** 2026-08-26  
> **Current version:** 0.24.0  
> **Next release target:** 0.25.0 — safety stabilization and UI polish  
> **Implementation base:** `5152b69`; current verified changes await their dedicated Git commit

This document is the current release-status source. The detailed build sequence remains in
[ROADMAP.md](ROADMAP.md), historical implementation evidence remains in
[PHASE_LOG.md](PHASE_LOG.md), and released changes remain in [CHANGELOG.md](CHANGELOG.md).

## Executive status

The branch integration is correct: `main` contains both histories that diverged at
`codex/v6-parity-roadmap`, including Receitas/cascade and biomass/external-pump work, and the
working tree was clean at the start of this audit. The application core is substantially built,
but **0.24.0 is not yet a field-release candidate**.

The remaining work is no longer a broad rebuild. It is concentrated in:

1. closing two command-ownership/safe-stop safety defects;
2. making every manual surface report accepted versus refused commands honestly;
3. stabilizing startup, chart dependencies and the code-quality gate;
4. completing visual/operator review and real-hardware receipts; and
5. packaging the application for field use.

The authoritative version should remain **0.24.0** while the P0 findings below are open. Use
**0.25.0** as the next milestone and bump `Directory.Build.props` only after the 0.25.0 release
gates in this document pass.

## Verified baseline

| Check | Result on 2026-08-26 | Interpretation |
|---|---|---|
| Git integration | Implementation based on `5152b69`; verified changes are currently in the working tree | Create the dedicated branch and commit as soon as `.git` is writable |
| Authoritative version | `Directory.Build.props` = `0.24.0` | Older version text in README/roadmap was documentation drift |
| Release tests | **510 passed, 1 skipped, 0 failed** | Includes Hub v7/flowmeter v05 synchronization and UI contract coverage; the skip is the hosted-WPF theme-cycle test |
| Package vulnerability scan | No known vulnerable direct or transitive packages | Does not waive compatibility warnings |
| Runtime startup smoke test | Two Release launches reached the first frame; no binding failure, fatal exception or unhandled exception was logged | No current startup XAML build failure was reproduced |
| First-frame time | **1.788 s and 6.011 s**, target `< 2 s` | The startup target is not repeatably met |
| Build compatibility | Release solution build: **0 warnings** after targeting `net10.0-windows10.0.19041.0` | `NU1701` is resolved; published chart/theme verification remains a release receipt |
| Formatting gate | `dotnet format --verify-no-changes --no-restore` fails repository-wide | Formatting/analyzer debt is not CI-ready |

The skipped theme test depends on a hosted WPF `Application`; token parity is tested headlessly,
but a packaged light/dark/light runtime test remains part of the UI acceptance work.

## Capability status

| Area | Status | Remaining gate |
|---|---|---|
| Protocol, connection shell and simulator | Software-complete | Full captured v.6 parity, live sensors and acknowledgement timing on hardware |
| Flowmeter v05 through ESP32 Hub v7 | Software-complete | Real Hub/flowmeter receipt for pending, ACK, internal-link loss and recovery |
| Manual process control and cultivation auxiliaries | Software-complete | Ownership-aware command feedback and full cultivation |
| Operational alarm kernel | Core complete | Per-variable alarms page and control-room audio/operator validation |
| kLa mapping, oxygen cascade, conditional OUR and gain scheduling | Software-complete | Real-bioreactor validation and performance receipt |
| Biomass sensor and guided procedure | Software-complete | Explicit threshold-apply correction and hardware receipt |
| External pump and proportional gas | Software-complete | Rejected-dispatch retry correction and hardware receipt |
| Receitas authoring and execution | Feature-complete | P0 safe-stop/manual-lock corrections, minor canvas polish and hardware confirmation |
| Synoptic and main UI | Broad inventory present | Placement, scaling, keyboard, theme and operator walkthrough |
| Packaging and field cutover | Not complete | Installer, first-run path, crash reporting, performance soak and operator manual |

## Audit findings

Severity means release impact: **P0** blocks any recipe-enabled field release, **P1** blocks the
0.25.0 release candidate, and **P2** is planned hardening/polish.

### AUD-001 — P0 — global safe-stop may be refused during an active recipe

`ControlViewModel.SafeStop` disengages the automatic cascade and then sends a combined frame through
`IDeviceService.Send`. In the application composition root, that service is the `CommandArbiter`,
whose plain `Send` is a **Manual** dispatch. A running recipe owns every actuator, and one ownership
conflict atomically refuses the entire frame. The `void` call hides that refusal; the view-model then
marks every control stopped and displays success even though no stop frame reached the device.

Evidence:

- [`ControlViewModel.cs`](../src/TecnalHub/ViewModels/ControlViewModel.cs) sends and commits success
  without a dispatch result.
- [`CommandArbiter.cs`](../src/TecnalHub/Services/Communication/CommandArbiter.cs) atomically rejects a
  mixed frame and its `IDeviceService.Send` adapter discards `CommandDispatchResult`.
- Existing tests cover manual and cascade safe-stop, but not the global red stop while Recipe owns the
  wire.

Required correction: create one owner-aware safety coordinator. The global stop must stop/abort the
recipe through its owning engine (or use a narrowly defined privileged safety path), deliver the safe
frame under valid ownership, release ownership, and update UI state only after accepted dispatch.

Exit test: start a recipe, press the global safe stop, assert one accepted safe frame, recipe state
stopped, all required actuator owners returned to Manual, and no false success on refusal/transport
failure.

### AUD-002 — P0 — manual controls do not visibly become inert under Recipe ownership

The arbiter correctly refuses Manual writes while a recipe runs, but `ControlViewModel.CanActuate`
checks connection only. The page has cascade-specific locks and badges, not general Recipe ownership
bindings. Therefore controls can remain editable and appear to send even while the wire rejects them.
This does not satisfy the documented promise that starting a recipe deactivates manual control.

Required correction: expose ownership/read-only state to each control row/card, disable all conflicting
edit/apply actions, display `receita` provenance and a clear reason, and keep the global safety action
available. Add UI/view-model tests for acquire, release, abort and reconnect transitions.

### AUD-003 — P1 — manual command acceptance is not observable by view-models

`ICommandArbiter.Dispatch` already returns `CommandDispatchResult`, but most UI code receives only the
legacy `IDeviceService.Send(TecnalCommand)` `void` method. Biomass, pump and other manual surfaces can
persist staged state and announce "sent" after an ownership rejection.

Required correction: introduce a manual command-dispatch abstraction that returns the result (and,
where needed, later transport acknowledgement). Commit/persist fields only after acceptance; show the
conflicting owner and retain staged input after refusal. Migrate all actuator view-models, not only the
two newly merged cards.

### AUD-004 — P1 — proportional-gas retry is suppressed after a refused dispatch

`PumpControlViewModel.MaybeSendProportionalGas` records `_lastGasFlowSentLpm` immediately after the
`void` send. If the cascade owns aeration, the arbiter refuses the frame, but the pump remembers the
target as sent and will not retry until the calculated flow changes by the resend threshold.

Required correction: advance the last-accepted value only when dispatch succeeds and retry when
aeration ownership returns to Manual. Tests must pin refusal, unchanged target, ownership release and
successful retry.

### AUD-005 — P1 — biomass thresholds send on focus loss despite an explicit apply action

The design says low/high/optimal thresholds are staged and applied atomically. `ControlView` routes
every text box `LostKeyboardFocus` through `ApplyFor`, whose biomass case invokes
`ApplyThresholdsCommand`; the page also presents an explicit **Enviar limiares** action. Moving focus
can therefore send calibration thresholds unexpectedly.

Required correction: exclude biomass threshold fields from the generic focus-loss apply behavior and
retain the explicit atomic action. Add an interaction test proving focus loss stages only, Enter/button
applies once, Escape reverts, and invalid thresholds never dispatch.

### AUD-006 — P1 — startup performance gate is unstable

Both startup smoke runs succeeded, but first-frame time ranged from 1.788 s to 6.011 s against the
roadmap's `< 2 s` acceptance target. A single fast warm launch is not sufficient evidence.

Required correction: add repeatable cold/warm measurements, instrument startup stages, and defer heavy
recipe/chart/resource initialization until after first frame where safe. Record median and worst-case
results on the target lab PC; keep auto-connect outside the first-frame critical path.

### AUD-007 — P1 — compatibility warning resolved; published chart receipt remains

The app and WPF test project now declare their actual Windows 10 2004 minimum
(`net10.0-windows10.0.19041.0`). NuGet consequently selects the supported
`SkiaSharp.Views.WPF 3.119.0` Windows asset instead of falling back to `net462`; the complete Release
solution build reports zero warnings and zero errors.

Remaining release receipt: open every chart surface in both themes from a self-contained `win-x64`
publish. Keep this gate open until that packaged-runtime check is recorded, and then promote
`NU1701` to an error so an incompatible fallback cannot return silently.

### AUD-008 — P2 — formatting and analyzer debt has no enforceable baseline

`dotnet format --verify-no-changes --no-restore` fails across production and test files, including
whitespace/line-ending/charset differences and naming/style diagnostics. The normal build also reports
missing-brace, obsolete API and unused-event warnings.

Required correction: align `.editorconfig`, normalize mechanically in an isolated change, resolve the
remaining semantic warnings, and add a no-new-debt CI ratchet. Do not mix a repository-wide formatting
rewrite with safety changes.

### UI/build conclusion

There is **no reproduced compiler error or startup XAML resource error** at this commit. The UI-related
release blockers are instead ownership honesty, an accidental focus-loss action, unstable first-frame
performance, chart-package compatibility, and incomplete visual/operator coverage. A successful build
does not validate responsive layout, clipping, focus order, DPI scaling or every deferred page.

## Extended 0.25.0 stabilization and UI-polish plan

### Stage 0 — freeze and reproduce the baseline

1. Keep `0.24.0` and tag no release while AUD-001/AUD-002 are open.
2. Add focused failing tests for the active-recipe safe stop, visible ownership lock, rejected manual
   command feedback, proportional-gas retry and biomass focus loss.
3. Capture one current self-contained publish size and a five-run cold/warm startup baseline.

**Exit:** every defect has a deterministic reproduction; the existing 499 passing tests remain green.

### Stage 1 — repair the safety and ownership boundary

1. Introduce the owner-aware safety coordinator and route shell/global stop, recipe stop, cascade abort
   and link-loss stop through explicit ownership transitions.
2. Replace fire-and-forget manual sends with result-returning dispatch for all actuator view-models.
3. Add ownership state/badges and command refusal messages to Controle; disable conflicting editors and
   commands while Recipe/Automatic owns an actuator.
4. Audit state commits so displayed/applied/persisted values change only after accepted dispatch.

**Exit:** active-recipe global stop is proven end-to-end; no control reports success for a refused
frame; ownership acquire/release is visible and tested.

### Stage 2 — close merged biomass and pump semantics

1. Make biomass thresholds explicit-apply only and test keyboard/focus behavior.
2. Make proportional gas retry acceptance-aware and ownership-event driven.
3. Verify external-pump profile payload limits, safe disabled frame, volume preview and vvm coupling on
   the bench; verify biomass enable/blank/start/stop/threshold/readout sequence on the bench.
4. Capture protocol/event receipts and update the WP3 parity record.

**Exit:** both merged features have accepted-command UI state plus live-hardware receipts.

### Stage 3 — make the build and runtime gate clean

1. Resolve `NU1701` and verify all chart surfaces from the published executable.
2. Isolate formatting normalization, then enable `dotnet format --verify-no-changes` in CI.
3. Remove normal-build warnings (missing braces, obsolete cascade setting and unused test event).
4. Run vulnerability, restore, Release build/test and self-contained publish on a clean machine.

**Exit:** zero build warnings, zero known vulnerable packages, format gate green, and publish starts.

### Stage 4 — systematic UI polish and accessibility pass

Review all nine workspaces, modal dialogs, detail panes and recipe states using a fixed matrix:

- 1280×720 minimum and the normal lab display;
- 100%, 125% and 150% Windows scaling;
- light/dark/light theme cycle;
- keyboard-only navigation, Enter/Escape semantics, focus visibility and logical tab order;
- disconnected, connecting, live, stale, alarmed, Automatic-owned and Recipe-owned states;
- long pt-BR labels, validation messages, empty data and maximum-value formatting.

Specific work: validate biomass/pump synoptic anchors, prevent clipping/overlap, finish recipe
drag-from-library/minimap/editable JSON/live PID readout only after safety, and capture approved
screenshots plus a zero-binding-error log.

**Exit:** operator walkthrough signed off; no clipping or inaccessible action in the matrix; hosted
theme cycle and startup smoke pass from the published app.

### Stage 5 — hardware and cultivation gate

1. Execute every v.6 operator action over USB and Wi-Fi and compare captured bytes.
2. Validate acknowledgements, reconnect, stale telemetry, alarm/audio behavior and the global stop under
   Manual, Automatic and Recipe ownership.
3. Run the cascade/kLa/OUR path and one complete cultivation with v.6 available as fallback.
4. Record deviations, operator decisions, timings and receipts in `PHASE_LOG.md`.

**Exit:** the roadmap's real-hardware parity and full-cultivation gates are closed.

### Stage 6 — package and release 0.25.0

1. Deliver the Inno Setup installer, first-run/no-hardware path, persistent crash reporting and pt-BR
   operator documentation.
2. Prove `< 2 s` first frame, `< 5 s` remembered-port telemetry, `< 200 MB` publish and flat memory in a
   24 h run, or explicitly revise a target with recorded evidence and a decision.
3. Bump `Directory.Build.props` to `0.25.0`, move `[Unreleased]` entries into the release, create the
   release tag, and archive the evidence bundle.

**Exit:** clean clone/build/test/publish/install/uninstall succeeds; P0/P1 findings are closed; the
release checklist and rollback instructions are complete.

## 0.25.0 release gate

Do not bump/release until all of the following are true:

- [ ] AUD-001 and AUD-002 closed with active-recipe tests
- [ ] AUD-003 through AUD-007 closed; no false command-success state
- [ ] `dotnet test TecnalHub.slnx -c Release --no-restore` passes with no unexpected skip
- [ ] Release build and self-contained publish have zero warnings, including `NU1701`
- [ ] `dotnet format TecnalHub.slnx --verify-no-changes --no-restore` passes
- [ ] package vulnerability scan reports no known vulnerabilities
- [ ] published UI passes the resolution/DPI/theme/keyboard/state matrix with zero binding errors
- [ ] startup and hardware receipts meet the roadmap targets
- [ ] installer, operator documentation and rollback path are verified
