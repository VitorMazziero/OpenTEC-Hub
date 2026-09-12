# OpenTEC-Hub current status and stabilization audit

> **Audit date:** 2026-08-26 · **Revised:** 2026-09-10  
> **Current version:** 0.26.2-dev (derived from git tags by MinVer)  
> **Next release target:** 0.27.0 — responsive UI, motor routing and power WP  
> **Implementation base:** `main` — every feature branch was merged on 2026-09-10 and `main` is now the only branch

The 2026-09-05 revision audited the Etapa 4.1 delivery (serial hygiene, WMI ranking, RTT), added
the per-shaft tare profile library, and re-ran the repository, build, format and test gates. Rows
and findings marked **26/08** or **29/08** carry over from earlier audits and were not re-run
today; read an undated claim in this document as evidence from 26/08.

This document is the current release-status source. The detailed build sequence remains in
[ROADMAP.md](ROADMAP.md), historical implementation evidence remains in
[PHASE_LOG.md](PHASE_LOG.md), and released changes remain in [CHANGELOG.md](CHANGELOG.md).

## Executive status

Branch integration is **done**. On 2026-09-10 the outstanding feature work — AUD-001…AUD-008,
Etapa 1.9, Etapa 1.10, Etapa 4.1, the motor/Modbus routing, the power tare library and the
responsive-UI adaptation — was consolidated into `main`, which is now the repository's only branch.
The application core is substantially built, but this is **not yet a field-release candidate**: the
UI adaptation still owes the visual matrix by scale and theme (step 5 of the responsive plan), and
nothing in this cycle was exercised against connected hardware.

The dated rows below were **not re-run on 2026-09-10** and still describe the 0.24.0 audit; read an
undated claim in this document as evidence from 26/08 until this file is reconciled for the release.

The P0 findings AUD-001 e AUD-002 e as P1s AUD-003 e AUD-004 estão **resolvidas (05/09/2026)**. A
auditoria da Etapa 4.1, no mesmo dia, corrigiu cinco defeitos da entrega de enlace serial — causa de
falha obsoleta entre tentativas, RTT de Wi-Fi sobrescrito, confirmação atrasada publicada como
latência, publicação não atômica e consulta WMI bloqueando a thread de UI no caminho do primeiro
quadro — e registrou três pendências P3 em
[IMPLEMENTATION_STEPS.md · Etapa 4.1-A](IMPLEMENTATION_STEPS.md#etapa-41-a--pendências-abertas-pela-auditoria-da-etapa-41).
Os itens abaixo refletem a estabilização e verificação das pendências restantes de release.

**11–12/09/2026 — plano de correção dos engasgos e achados de bancada, executado.** As quinze seções
do plano `docs/plans/2026-09-11-plano-correcao-engasgos-ui-ensaios.md` estão na `main`
([PHASE_LOG P3-08](history/PHASE_LOG.md)): grades sem `Reset`, I/O dos ensaios em fila fora da
thread da UI (D-048), gráficos só visíveis e só quando mudaram, telemetria em `Background`, critérios
persistidos, corrida sem captura recusada (D-050), política autônoma e saída por estabilidade do
alívio, Duplicar/Reabrir, faixa de alarmes por página, tabela navegável, tensão editável, hub
`10.0.1-dev`. Suíte: **1363 aprovados**. Medido com o `UiHitchMonitor` (DEBUG): 3 min a 1 Hz na
página de Potência, 3 atrasos > 30 ms só na subida do enlace, depois nenhum. **Pendente de
bancada:** gravar o hub e validar o envio da curva; a corrida de 10 min com ensaio em captura; o
controlador de vazão que não regula para baixo na primeira abertura do alívio.

**12/09/2026 — identidade e configuração dos nós pelo Hub, executados.** Dois planos do mesmo dia
estão na `main`: identidade de rede dos nós (Hub `10.1`, [D-051](DECISIONS.md), [PHASE_LOG
P3-09](history/PHASE_LOG.md)) e configuração dos nós pelo Hub (Hub `10.2.0-dev`, nós
`v11`/`3.9`/`v11`/`v11`, [D-052](DECISIONS.md), [P3-10](history/PHASE_LOG.md)): offset e períodos
do sensor de distância, sintonia do controlador de vazão, zerar volume/calibração/PID da bomba,
aquisição da biomassa, e a saúde dos nós (RSSI, heap, uptime, falhas, OTA) por `/nodeDiag` em Wi-Fi
e por serial em USB. Suíte: **1497 aprovados**; contratos do Hub: 72; firmware do Hub compila
(85 % do flash). **Pendente de bancada:** regravar a frota inteira (sem compatibilidade com
`v10`/`3.8`), medir o quadro com todos os ecos e o heap do Hub com a tarefa `NodeDiag`
(`ROADMAP.md` › Field readiness).

**12/09/2026 — arranjo de válvulas A/B/C nos ensaios, executado** ([D-053](DECISIONS.md),
[P3-11](history/PHASE_LOG.md), plano `docs/plans/2026-09-12-plano-valvulas-abc-ensaios.md`, nove
etapas, um commit cada). Válvulas com papel fixo (A ar ao reator, B N₂, C purga; B e C no mesmo
MOSFET), roteador único `GasRouting`, ligação em Configurações › Gás e válvulas (com o fluxograma) e
como proveniência nos manifestos; kLa com pré-estabilização do ar por C durante o N₂ e `t = 0` na
comutação em uma frame; potência com pré-estabilização por C obrigatória; Controle por entrada
acionada (1/2) sem intertravamento em operação livre; alarmes *Gás sem destino* e *A e B/C abertas*;
simulador como arranjo físico. Suíte: **1597 aprovados**. **Pendente de bancada:** §7.3 do plano
(`ROADMAP.md` › Field readiness).

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
| Git integration | **05/09:** `feature/etapa-4.1-serial-hygiene-and-rtt` is 11 ahead / 0 behind `main`; the 7 other unmerged branches are all ancestors of this branch's HEAD | A single fast-forward of `main` integrates the whole AUD-001…AUD-008 + Etapa 1.9/1.10/4.1 chain; nothing is stranded on a side branch |
| Repository integrity | **05/09:** `git fsck --no-progress` reports no errors and **0 dangling objects**; no merge/rebase state and no stale lock files | No corruption, despite `.git` living inside the shared OneDrive folder |
| Authoritative version | `Directory.Build.props` = `0.24.0` | Correctly held while the P0 findings are open |
| Release tests | **05/09: 1144 passed, 0 skipped, 0 failed** (`dotnet test --nologo --logger "console;verbosity=normal"`) | Includes safety coordinator, manual dispatcher, dynamic ownership locking, proportional gas retry, serial hygiene, WMI ranking, audited RTT correlation, per-shaft tare profile library, connection-popover command bindings, unskipped hosted-WPF theme-cycle test and complete 30-image screenshot capture suite. The shell-render test was host-dependent until `WpfRenderingHost` forced `WindowState.Normal`; the earlier "1133 passed" baseline was recorded on a host where the shell did not self-maximize. **Read the printed `Total de testes` line** — with `-v q` plus a quiet logger this suite returned exit 0 while hiding 5 failures |
| Port enumeration cost | **05/09:** the `Win32_PnPEntity` WMI query measures **~1090 ms cold / 256–364 ms warm** with no COM device attached, and is now off the UI thread and off the first-frame path | `ConnectionViewModel` fills the popover from the WMI-free `SerialTransport.ListPortNames()` and folds in the ranking from `ListCandidatePortsAsync()`. The first-frame figure below predates this change and was measured with the cold query still on that path, so it is a ceiling, not the current cost |
| Package vulnerability scan | **26/08:** no known vulnerable direct or transitive packages | Does not waive compatibility warnings; not re-scanned after the merge |
| Runtime startup smoke test | **29/08:** Debug executable launched with `--workspace C:\Users\vitor\Documents\OpenTEC-Hub`; first frame rendered and the fresh log contained no binding failure, fatal exception or unhandled exception | `--workspace` and `--no-workspace-prompt` now bypass the Windows folder picker; the expected offline COM1 warnings do not establish hardware operation |
| First-frame time | **05/09:** 968–1280 ms, meta `< 2 s` cumprida de forma determinística | Otimização via `DeferredPageHost` (ADR D-033) com inicialização diferida de páginas pesadas em `ApplicationIdle` (AUD-006 resolvido) |
| Build compatibility | **05/09:** Debug e Release, **0 warnings, 0 errors** | `NU1701` resolvido; temas claro/escuro validados em executável publicado pelo operador (AUD-007 resolvido) |
| Formatting gate | **05/09:** `dotnet format --verify-no-changes --no-restore` **0 erros, 0 avisos** | Código 100% formatado e analisadores em conformidade estrita com o `.editorconfig` (AUD-008 resolvido) |

A suíte hosted-WPF agora executa sobre thread STA dedicada (`WpfRenderingHost`), permitindo que o ciclo de
temas claro/escuro (`ThemeServiceTests`) execute sem depender de sessão interativa do Windows.

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
| Synoptic and main UI | Detail panels standardized; pH has PV/SP/Δ, trend, control and direct calibration navigation; summary cards enlarged; measured sensors expose detrended-noise health | Resolução da captura automatizada via `RenderTargetBitmap` (ADR D-040); 30 capturas geradas em 100%, 125% e 150% de DPI sem truncamentos; homologação final de bancada com operador aberta |
| Integridade do dado bruto dos ensaios (kLa + potência) | Software-complete (10/09/2026, ADR D-046) | As quatro lacunas foram fechadas: colunas `TemperatureC`/`RpmMeasured` no kLa (esquema 2, leitor tolerante ao esquema 1), varredura de tara gravada ao vivo em `Taras-Brutas/` e conferência de ponto único gravada em `Pontos-Unicos/` com manifesto. Falta o recibo de bancada: confirmar que a temperatura e a rotação medida chegam preenchidas com sonda e servo reais, e que uma tara interrompida deixa o arquivo legível |
| Documentação no aplicativo (Configurações → Documentação) | Painel, Controle e os quatro assuntos de Potência entregues (10/09/2026, ADR D-047) | As demais páginas seguem sem tópico e sem botão “?”, na ordem registrada em [IMPLEMENTATION_STEPS · Etapa 2.4](IMPLEMENTATION_STEPS.md); revisão de texto com o operador em aberto |
| Packaging and field cutover | Software-complete | Instalador Inno Setup (`installer/OpenTECHub_Setup.iss`), script de build (`build_installer.ps1`), serviço `CrashReporter` e Manual do Operador concluídos (ADR D-041); soak test de campo aguardando bancada física |

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

**Resolvido e Reforçado com Exclusão Mútua Estrita (05/09/2026).**

1. **Formulação Matemática e Despacho:**
   No `PumpControlViewModel`, o acoplamento de gás proporcional calcula a vazão $Q_g = \min\left(\left(V_0 + \frac{V_{\text{bomba}}}{1000}\right) \cdot \text{vvm},\; \text{maxFlow}\right)$, onde $V_0$ é o volume inicial em L, $V_{\text{bomba}}$ é o volume acumulado da bomba em mL, $\text{vvm}$ é a taxa específica de aeração ($\text{min}^{-1}$) e $\text{maxFlow}$ é o limite do fluxômetro. O envio ocorre via `_dispatcher.Dispatch(CommandBuilders.FlowSetpoint(qg, maxFlow, valve1: false, valve2: false))` sobre `ActuatorId.Aeration`.
2. **Fundamentação de Bioprocesso e Retentativa Reativa:**
   Em bateladas alimentadas (*fed-batch*), a adição de líquido altera a relação estequiométrica/hidrodinâmica de $vvm$. O acoplamento ajusta dinamicamente $Q_g$ para manter $vvm$ fixo. `PumpControlViewModel` assina os eventos do `ICommandArbiter` (`OwnershipChanged` e `OwnershipRevoked`), invalidando o setpoint entregue sob sobreposição e restaurando a vazão via `MaybeSendProportionalGas(force: true)` quando a aeração é liberada.
3. **Exclusão Mútua Bidirecional Estrita (Gás Proporcional vs Controle de Oxigênio Dissolvido):**
   - **Gás Proporcional em Execução Bloqueia Cascata/Mapa:** Enquanto a bomba peristáltica estiver ativa e o gás proporcional habilitado (`IsGasProportionalActive == true`), `ICascadeService.CanEngage` recusa a ativação da malha de oxigênio com o motivo explícito: *"O acoplamento de gás proporcional ao volume dosado está ativo na Bomba Externa. Desative-o para iniciar o controle de oxigênio (cascata/mapa)."*
   - **Cascata/Mapa em Execução Bloqueia Gás Proporcional:** Enquanto a cascata de oxigênio estiver engajada (`_cascade.IsEngaged == true`), o toggle de `GasProportionalEnabled` e a ativação da bomba com gás proporcional são imediatamente revertidos para desligado com notificação no `StatusText` (*"Gás proporcional indisponível: controle de oxigênio (cascata/mapa) em execução."*), e nenhum frame é disparado por `MaybeSendProportionalGas`.
   - Cobertura de testes automatizados completa em `CascadeServiceTests.cs` e `ExternalDeviceTests.cs` (1124 testes aprovados, 0 falhas).

### AUD-005 — P1 — biomass thresholds send on focus loss despite an explicit apply action

**Retido por Decisão de UX do Operador (05/09/2026) — Waived / By Design.**

Após alinhamento e decisão explícita de produto, o comportamento de envio de valores de processo e limiares de calibração via `LostKeyboardFocus` (ao clicar fora do campo ou alternar com `Tab`) e via tecla `Enter` foi **retido deliberadamente**.
No contexto operacional real de bancada de bioprocessos, os operadores necessitam enviar setpoints de forma ágil sem a fricção de cliques adicionais obrigatórios em botões específicos de envio após a digitação.
Os envios disparados por perda de foco continuam integralmente protegidos pelas validações de limites numéricos do protocolo e pelo árbitro de comandos (`IManualDispatcher`), impedindo despachos inválidos ou sob posse externa de outro processo (conforme ADRs D-035 e D-036).
Decisão arquitetural formalizada no [ADR D-038](DECISIONS.md#d-038--retenção-deliberada-de-envio-de-setpoints-via-lostkeyboardfocus-e-enter-aud-005-waived-por-decisão-de-ux).

### AUD-006 — P1 — startup performance gate is unstable

**Resolvido / Concluído (05/09/2026).**

O tempo de inicialização (First-Frame) foi determinística e estruturalmente estabilizado pela introdução do `DeferredPageHost` ([ADR D-033](DECISIONS.md#d-033--abertura-rápida-do-app-com-hospedagem-diferida-de-páginas-deferredpagehost)).
Todas as rotas secundárias e controles pesados de renderização (gráficos OxyPlot/ScottPlot e páginas de ensaio) têm sua materialização diferida para momentos de ociosidade do dispatcher (`ApplicationIdle`), assegurando que apenas a casca principal (`ShellView`), barra de status e sinótico inicial sejam instanciados na primeira pintura.
Medições instrumentadas comprovam First-Frame entre **968 ms e 1280 ms** em todas as 11 rotas do shell, cumprindo com folga a meta de aceitação do roadmap (`< 2 s`). Item concluído sem pendências de código adicionais.

### AUD-007 — P1 — compatibility warning resolved; published chart receipt remains

**Resolvido / Concluído (05/09/2026).**

O aviso de compatibilidade `NU1701` foi definitivamente eliminado pela padronização do TFM mínimo Windows 10 2004 (`net10.0-windows10.0.19041.0`).
A verificação dos gráficos em executável empacotado publicado (`win-x64` self-contained) foi confirmada diretamente pelo operador: as superfícies de gráficos (Sinótico, Histórico/Tendências, Ensaios de kLa e Ensaios de Potência) operam com estabilidade, nitidez e renderização correta das séries e eixos tanto no tema Claro quanto no tema Escuro. A exigência de recibos formais de captura de tela foi dispensada pelo operador, considerando o item plenamente atendido.

### AUD-008 — P2 — formatting and analyzer debt has no enforceable baseline

**Resolvido (05/09/2026).**

Toda a dívida técnica de formatação, codificação de caracteres (UTF-8 sem BOM) e avisos de analisadores estáticos de código foi resolvida:
- Executado `dotnet format` em toda a solução, corrigindo identações, quebras de linha e estilo de código C#;
- Atualizado `.editorconfig` para harmonizar com `CONVENTIONS.md`, adicionando regras explícitas para constantes privadas em `PascalCase` (`dotnet_naming_rule.constants_are_pascal`) e campos estáticos somente-leitura em `PascalCase` (`dotnet_naming_rule.static_fields_are_pascal`), eliminando falsos positivos de `IDE1006`;
- Configurado `<NoWarn>$(NoWarn);CS0067</NoWarn>` em `OpenTECHub.Tests.csproj` para suprimir advertências de eventos de interface não invocados em stubs de teste;
- Gate de verificação `dotnet format OpenTECHub.slnx --verify-no-changes --no-restore` validado com **0 erros e 0 avisos**;
- Suíte completa de testes executada com 100% de sucesso (1092 aprovados, 0 falhas, 1 ignorado).
Decisão formalizada no [ADR D-039](DECISIONS.md#d-039--higiene-de-formatação-com-dotnet-format-regras-de-nomenclatura-no-editorconfig-e-barreira-de-ci-aud-008).

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
