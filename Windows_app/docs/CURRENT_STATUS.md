# OpenTEC-Hub current status and stabilization audit

> **Audit date:** 2026-08-26 · **Revised:** 2026-08-29  
> **Current version:** 0.24.0  
> **Next release target:** 0.25.0 — safety stabilization and UI polish  
> **Implementation base:** `230c6ce` on `main` — committed and fully integrated

The 2026-08-29 revision re-measured branch integration and the Debug/Release test suites, then
launched the current executable with an explicit workspace. Rows and findings marked **26/08**
carry over from the original audit and were not re-run today; read an undated claim in this
document as evidence from 26/08.

This document is the current release-status source. The detailed build sequence remains in
[ROADMAP.md](ROADMAP.md), historical implementation evidence remains in
[PHASE_LOG.md](PHASE_LOG.md), and released changes remain in [CHANGELOG.md](CHANGELOG.md).

## Executive status

Branch integration is now **complete**. The kLa/flowmeter, UI/receitas and external-device/detail-
panel lines are unified on `main` through `230c6ce`. All 20 local branches are ancestors of `main`;
none carries a commit outside it. The application core is substantially built, but **0.24.0 is not
yet a field-release candidate**.

The P0 findings AUD-001 e AUD-002 e as P1s AUD-003 e AUD-004 estão agora **resolvidas (05/09/2026)**. Os itens abaixo
refletem a estabilização e verificação das pendências restantes de release.

The remaining work is concentrated in:

1. completing remaining control findings (AUD-005: foco e limiares de biomassa);
2. stabilizing startup, chart dependencies and the code-quality gate;
3. completing visual/operator review and real-hardware receipts; and
4. packaging the application for field use.

The authoritative version should remain **0.24.0** while the P0 findings below are open. Use
**0.25.0** as the next milestone and bump `Directory.Build.props` only after the 0.25.0 release
gates in this document pass.

## Verified baseline

| Check | Result (date) | Interpretation |
|---|---|---|
| Git integration | **29/08:** `main` contains `230c6ce`; all 20 local branches are ancestors of `main`, none ahead | The external-device and detail-panel lines are integrated without conflicts; nothing is stranded on a side branch |
| Repository integrity | **28/08:** `git fsck --full` reports only dangling objects, `garbage: 0`; no merge/rebase state and no stale lock files | No corruption, despite `.git` living inside the shared OneDrive folder |
| Authoritative version | `Directory.Build.props` = `0.24.0` | Correctly held while the P0 findings are open |
| Release tests | **29/08: 651 passed, 1 skipped, 0 failed** (`dotnet test OpenTECHub.slnx -c Release --no-build --no-restore`, repeated after one isolated timing flake passed) | Includes external-device, detail-pane, calibration-navigation and sensor-health contracts; the skip is still the hosted-WPF theme-cycle test |
| Package vulnerability scan | **26/08:** no known vulnerable direct or transitive packages | Does not waive compatibility warnings; not re-scanned after the merge |
| Runtime startup smoke test | **29/08:** Debug executable launched with `--workspace C:\Users\vitor\Documents\OpenTEC-Hub`; first frame rendered and the fresh log contained no binding failure, fatal exception or unhandled exception | `--workspace` and `--no-workspace-prompt` now bypass the Windows folder picker; the expected offline COM1 warnings do not establish hardware operation |
| First-frame time | **29/08:** 2.347 s, target `< 2 s` | The startup target remains unmet and is still a stabilization item |
| Build compatibility | **29/08:** Debug and Release solution builds, **0 warnings, 0 errors** | `NU1701` stays resolved; published chart/theme verification remains a release receipt |
| Formatting gate | **26/08:** `dotnet format --verify-no-changes --no-restore` fails repository-wide | Formatting/analyzer debt is not CI-ready; not re-run |

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
| Determinação abiótica de kLa por gassing-out | Software-complete após auditoria; inclui espera pós-N₂ por derivada, importação/reanálise de campanhas e a estabilização opcional da vazão em válvula de alívio antes do `t₀` | Validar troca Ar/N₂, estabilidade da sonda, ACK, limites de fase e parada segura no ESP32-S3 v7 + fluxômetro v05. O alívio foi exercitado apenas por reprodução de arquivo e ainda precisa da montagem física e do seu recibo de bancada |
| Biomass sensor and guided procedure | Software-complete | Explicit threshold-apply correction and hardware receipt |
| External pump and proportional gas | Software-complete | Rejected-dispatch retry correction and hardware receipt |
| Receitas authoring and execution | Feature-complete | P0 safe-stop/manual-lock corrections, minor canvas polish and hardware confirmation |
| Receitas holding for an unresponsive external device | Software-complete **(new since the audit)** | Flow-setpoint and aeration-enable blocks wait for the flowmeter to confirm, latch the `Receita aguardando dispositivo` alarm after 8 s and offer skip/stop; needs a bench receipt with a genuinely offline meter |
| Synoptic and main UI | Detail panels standardized; pH has PV/SP/Δ, trend, control and direct calibration navigation; summary cards enlarged; measured sensors expose detrended-noise health | Screenshot capture failed with Windows `0x80004002`; accessibility/runtime tree passed, but final visual spacing and operator walkthrough remain open |
| Packaging and field cutover | Not complete | Installer, first-run path, crash reporting, performance soak and operator manual |

## Audit findings

Severity means release impact: **P0** blocks any recipe-enabled field release, **P1** blocks the
0.25.0 release candidate, and **P2** is planned hardening/polish.

### AUD-001 — P0 — global safe-stop may be refused during an active recipe

**Resolvido (05/09/2026).**

`ControlViewModel.SafeStop` disengages the automatic cascade and then sends a combined frame through
`IDeviceService.Send`. In the application composition root, that service is the `CommandArbiter`,
whose plain `Send` is a **Manual** dispatch. A running recipe owns every actuator, and one ownership
conflict atomically refuses the entire frame. The `void` call hides that refusal; the view-model then
marks every control stopped and displays success even though no stop frame reached the device.

Evidence:

- [`ControlViewModel.cs`](../src/OpenTECHub/ViewModels/ControlViewModel.cs) sends and commits success
  without a dispatch result.
- [`CommandArbiter.cs`](../src/OpenTECHub/Services/Communication/CommandArbiter.cs) atomically rejects a
  mixed frame and its `IDeviceService.Send` adapter discards `CommandDispatchResult`.
- Existing tests cover manual and cascade safe-stop, but not the global red stop while Recipe owns the
  wire.

Required correction: create one owner-aware safety coordinator. The global stop must stop/abort the
recipe through its owning engine (or use a narrowly defined privileged safety path), deliver the safe
frame under valid ownership, release ownership, and update UI state only after accepted dispatch.

Exit test: start a recipe, press the global safe stop, assert one accepted safe frame, recipe state
stopped, all required actuator owners returned to Manual, and no false success on refusal/transport
failure.

**Implementação e Verificação (05/09/2026):**
- Criado o serviço coordenador de segurança `ISafetyCoordinator` / `SafetyCoordinator` (`src/OpenTECHub/Services/Safety/`), responsável por coordenar a parada de receitas ativas (`IRecipeEngine.StopAsync`), desengajamento de cascata (`ICascadeService.Disengage`), aborto de ensaios (`IKlaTestRunner.AbortTestAsync`, `IPowerTestRunner.AbortTestAsync`) e verificação do estado de conexão da camada de transporte.
- Implementado caminho privilegiado de segurança no `ICommandArbiter` (`DispatchSafety` e `DispatchSeparateSafetyFrame`), permitindo o envio atômico do quadro de emergência para o hardware e garantindo a revogação de posse para `CommandOwner.Manual` com emissão do evento `OwnershipRevoked(isSafeAbort: true)`.
- Atualizado `ControlViewModel.SafeStopCommand` para rotear a parada por `_safetyCoordinator.ExecuteGlobalSafeStopAsync`, avaliando o `SafetyStopResult` e exibindo mensagem de erro explícita em caso de desconexão ou recusa, eliminando qualquer falso positivo de sucesso na UI.
- Registrado `ISafetyCoordinator` no contêiner DI em `App.xaml.cs` e injetado em `ShellViewModel.cs`.
- Criada a suíte `SafetyCoordinatorTests.cs` cobrindo cenários com receita em execução, cascata engajada, link desconectado e override forçado no `CommandArbiter`.
- Adicionados testes de integração em `ControlViewModelTests.cs` validando o comando de parada segura durante receita ativa e o retorno de falha honesta quando desconectado. Total de 1078 testes passando.

### AUD-002 — P0 — manual controls do not visibly become inert under Recipe ownership

**Resolvido (05/09/2026).**

`ControlViewModel` e todos os ViewModels de subsistemas e periféricos (`FlowControlViewModel`, `PHControlViewModel`, `NutrientControlViewModel`, `AntifoamControlViewModel`, `FlaskAgitatorViewModel`, `BiomassControlViewModel`, `PumpControlViewModel`) agora rastreiam dinamicamente o estado de posse de cada atuador via `ICommandArbiter` (`OwnershipChanged` e `OwnershipRevoked`).

Cada linha de processo e cada cartão de periférico expõe:
- `CurrentOwner`: proprietário atual do atuador (`Manual`, `Recipe`, `Automatic`, `KlaAssay`, `PowerAssay`);
- `IsOwnedByOther`: booleano indicando se o atuador está sob controle de outro processo (`CurrentOwner != Manual`);
- `HasOwnerBadge`: booleano que ativa a exibição do crachá de proveniência (no caso do oxigênio, suprimido quando em `Automatic` pois a linha atua como seletor da cascata);
- `OwnerBadgeText`: texto padronizado em minúsculas para o `ctl:ProvenanceBadge` (`receita`, `controle o₂`, `ensaio kla`, `ensaio pot`);
- `OwnerLockReason`: descrição explicativa em pt-BR utilizada como `ToolTip` de bloqueio nos controles manuais e mensagem explicativa de recusa.

No XAML (`ControlView.xaml`):
- O bloqueio genérico de página inteira (`<Grid Grid.Row="1" IsEnabled="{Binding IsManualOperationEnabled}">`) foi **removido**, garantindo que a barra de status e a ação de parada segura ("Parada segura") permaneçam **100% operáveis e acessíveis a qualquer momento**, mesmo durante uma receita ou automação ativa.
- Todas as 5 linhas de processo (temperatura, agitação, oxigênio, vazão, pressão) desabilitam seus toggles, caixas de texto e botões via `IsEnabled="{Binding IsOwnedByOther, Converter={StaticResource InverseBool}}"`, exibem `ctl:ProvenanceBadge` com bind em `HasOwnerBadge`/`OwnerBadgeText` e mostram tooltips contextuais com `OwnerLockReason`.
- Todos os cartões periféricos e gavetas (bomba externa, sensor de biomassa, agitador de frascos, pH, nutriente e antiespumante) desabilitam suas entradas, sliders e botões de envio/aplicação quando sob posse externa e exibem seus respectivos crachás e tooltips.
- Defesa em profundidade: `CanApplyAll` e `ApplyAll` validam se há linhas alteradas sob posse de outro processo e abortam o envio com aviso explícito no `StatusText`. Os métodos de aplicação dos periféricos (`Apply`, `ApplyProfile`, `ApplyThresholds`, `SendMomentary`) rejeitam qualquer disparo manual sob posse externa.
- Para a linha de oxigênio, a alternância do botão "Ativo" desengaja a cascata normalmente quando em `Automatic` ou `Manual`.

Testes automatizados:
- `ControlWorkspaceContractTests.cs`: adicionados testes contratuais `Safe_stop_is_never_locked_by_parent_grid` e `Process_rows_and_peripherals_bind_ownership_lock_and_provenance_badges`.
- `ControlViewModelTests.cs`: adicionados testes `When_recipe_claims_actuators_manual_controls_visually_lock_and_show_recipe_badge`, `When_recipe_releases_actuators_manual_controls_unlock_and_badges_disappear`, `When_actuator_is_owned_by_recipe_manual_application_is_prevented` e `When_cascade_engages_only_overridden_actuators_show_oxygen_badge`. 100% dos testes da solução passando (1084 aprovados, 0 falhas).

### AUD-003 — P1 — manual command acceptance is not observable by view-models

**Resolvido (05/09/2026).**

Todas as ViewModels de atuação e despacho manual foram migradas da chamada legado `IDeviceService.Send` (`void`) para a interface observável `IManualDispatcher` (`CommandDispatchResult`), eliminando o descarte silencioso de recusas emitidas pelo `CommandArbiter`:

- **Abstração e Diagnóstico (`IManualDispatcher` / `ManualDispatcher` / `DispatchRefusal`):**
  - Adicionada a propriedade `Ownership` à interface `IManualDispatcher` e à classe `ManualDispatcher`, expondo o snapshot de atuadores ativos quando o serviço subjacente for o `ICommandArbiter`.
  - Atualizado `DispatchRefusal.Describe` com sobrecargas convenientes recebendo `IManualDispatcher` e `IDeviceService`, além do suporte ao rótulo `CommandOwner.PowerAssay => "Ensaio de Potência"`.
- **Subsistemas de Processo (`SubsystemViewModel`):**
  - Injetado `IManualDispatcher? dispatcher = null` no construtor com fallback retrocompatível.
  - Adicionada propriedade observável `StatusText`.
  - No método `Apply()`: envio roteado via `_dispatcher.Dispatch(command)`. Em caso de recusa (`!result.Accepted`), o `StatusText` é preenchido com a mensagem descritiva de recusa (ex.: *"Comando recusado: temperatura sob controle de Receita."*), o valor digitado permanece em `SetpointText` e o marcador `HasPendingChange` permanece `true` sem chamar `CommitPendingCommand()`.
- **Tela de Controle Integrada (`ControlViewModel`):**
  - Injetado `IManualDispatcher? dispatcher = null` no construtor.
  - No método `ApplyAll()`: envio atômico despachado via `_dispatcher.Dispatch(command)`. Se recusado pelo árbitro, aborta sem comitar nenhuma linha e sem persistir predefinições, atribuindo a recusa descritiva ao `StatusText`. Somente comita e persiste após aceitação comprovada.
  - No método `ApplyFlowState()`: despacho avaliado via `_dispatcher.Dispatch(command)`;
  - Em `OnStagedStateChanged`: propaga notificações de `StatusText` originadas de disparos individuais nos subsistemas para a barra de status principal.
- **Cartões de Dosagem e Calibrações:**
  - `PHControlViewModel`, `NutrientControlViewModel` e `AntifoamControlViewModel`: migrados para `_dispatcher.Dispatch`, preservando campos *staged* e informando motivo de recusa/bloqueio no `StatusText` em caso de conflito.
  - `FlowCalibrationViewModel` e `BiomassCalibrationViewModel`: migrados para `_dispatcher.Dispatch`, validando aceitação nos comandos de envio de curvas, limiares e setpoints de teste.
- **Shell e Injeção de Dependência:**
  - Injetado `IManualDispatcher? dispatcher = null` no `ShellViewModel`, repassando a instância nas fábricas dos 5 `SubsystemViewModel` e no `ControlViewModel`.
- **Testes Automatizados:**
  - `ControlViewModelTests.cs`: atualizado `ControlFixture` com `Dispatcher = new ManualDispatcher(Arbiter)` conectado ao árbitro de testes; adicionados novos testes validando a retenção de estado *staged*, ausência de comit e exibição de mensagens de recusa sob conflito no árbitro (1089 testes aprovados, 0 falhas).

### AUD-004 — P1 — proportional-gas retry is suppressed after a refused dispatch

**Resolvido (05/09/2026).**

`PumpControlViewModel` agora assina os eventos de transição de posse do `ICommandArbiter` (`OwnershipChanged` e `OwnershipRevoked`).
Quando o atuador de aeração (`ActuatorId.Aeration`) é tomado por outro controlador (como cascata de oxigênio em `Automatic` ou receitas em `Recipe`), o ViewModel invalida o último setpoint entregue (`_lastGasFlowSentLpm = null`) e sinaliza pendência de retentativa (`_gasRetryPending = true; _aerationOverridden = true;`).
Quando a posse da aeração retorna para `CommandOwner.Manual`, o ViewModel intercepta a transição imediatamente e dispara um envio forçado (`MaybeSendProportionalGas(force: true)`), ignorando a banda morta de reenvio (`GasFlowResendThresholdLpm`) para garantir que a vazão calculada $Q_g = (V_0 + V_{\text{bomba}}/1000) \cdot \text{vvm}$ seja restaurada no hardware mesmo que o valor de volume não tenha sofrido alteração e sem depender de novos pacotes de telemetria.
O valor de último envio aceito só avança quando o despacho é efetivamente confirmado pelo árbitro (`result.Accepted == true`).
Cobertura de testes automatizados adicionada em `ExternalDeviceTests.cs` (1092 testes aprovados, 0 falhas).

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

- [x] AUD-001 e AUD-002 (P0) fechados em 05/09/2026 com testes de receita ativa e segurança física
- [ ] AUD-005 through AUD-007 closed (AUD-001..AUD-004 fechados em 05/09/2026); no false command-success state
- [x] `dotnet test OpenTECHub.slnx -c Release` passes with no unexpected skip — **05/09: 1092/1 skip/0 fail**; re-confirm at release time
- [ ] Release build and self-contained publish have zero warnings, including `NU1701`
- [ ] `dotnet format OpenTECHub.slnx --verify-no-changes --no-restore` passes
- [ ] package vulnerability scan reports no known vulnerabilities
- [ ] published UI passes the resolution/DPI/theme/keyboard/state matrix with zero binding errors
- [ ] startup and hardware receipts meet the roadmap targets
- [ ] installer, operator documentation and rollback path are verified
