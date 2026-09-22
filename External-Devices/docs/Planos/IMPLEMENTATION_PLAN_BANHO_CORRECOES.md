# PLANO DE CORREÇÕES: INTEGRAÇÃO DO BANHO EXTERNO (C404) VIA WI-FI

**Data:** 2026-09-22 (decisões do responsável incorporadas no mesmo dia)
**Base auditada:** `main` @ `7847489` — Hub `10.5.1-dev`, nó `r3.1`, Windows App (W01–W08),
`bath_app` 1.1.0+2, `Android_app` 1.0.0+1
**Status:** auditoria concluída, decisões tomadas e **correções K00–K11 implementadas em software** (seção 7); bancada pendente
**Classificação atual:** `NOT_READY_FOR_CULTURE` (inalterada — H08/H09/H10 físicos pendentes)

## 1. Resumo

A integração está bem estruturada: uma referência de processo (`tempSetpoint`), PI residente
no Hub, troca break-before-make com `100B`, caixa confiável com `cmd_id`, validação atômica do
`/bathData`, enlace do nó em tarefa própria e testes automatizados verdes. Os problemas encontrados
não estão no PI em si, mas nas **bordas de coordenação**: dois tipos de comando dividindo a
mesma caixa, falha que não sai com o reset, clientes que comandam o nó por fora do Hub e
aplicativos cujo estado exibido pode divergir do estado real do Hub.

**Enquadramento de risco (decisão do responsável):** o banho não pode ser desligado por software
e não é um atuador perigoso; os riscos reais do módulo estão no motor e no fluxômetro. Por isso
as correções do banho são tratadas como **funcionais** (a cascata não pode travar, a interface
não pode mentir sobre a via e a receita precisa da temperatura do reator), não como
intertravamento de segurança.

### Evidência executada nesta auditoria

| Verificação | Resultado |
|---|---|
| `python -m unittest discover ESP32S3-HUB/tests/contracts` | 120 testes OK (são testes estáticos sobre o fonte) |
| `ESP32S3-HUB/tests/host-bath-cascade/build.cmd` | `ALL PASSED` (somente o PI puro) |
| `banho-termostatico/tests/host-sim/build.cmd` | `ALL PASSED` (inclui U/V: reentrega e recusa) |
| `dotnet test Windows_app/OpenTECHub.slnx` | 1743/1743 aprovados |

**Lacuna de teste central:** a orquestração do Hub (`serviceExternalBathCascade` + handler
`/bathData` + `bathBox`) não tem teste comportamental; os contratos Python só verificam texto.
Os defeitos C1–C3 abaixo passariam por qualquer suíte atual.

## 2. Decisões tomadas (2026-09-22)

| # | Tema | Decisão | Consequência no plano |
|---|---|---|---|
| D-1 | Posse do banho | **O Hub é dono do nó** enquanto a cascata estiver ativa. Mudanças no painel do C404 continuam sendo revertidas pela guarda em modo automático (comportamento já existente). | Nó recusa comandos de atuação pela API local enquanto houver posse (K01); a posse expira sozinha sem push do Hub; divergência de alvo vira re-comando, não falha (K03). |
| D-2 | Parada de emergência | **Somente parar de enviar setpoints e tirar o nó do modo automático.** Não trocar a via para UART. O banho não é desligado por software. | Uma única semântica de **parada do banho** (K02): aborta sequência em curso, coloca o nó em `manual` (a guarda deixa de acionar relés), desliga a cascata e libera a posse; via e `bathComm` permanecem. |
| D-3 | Receitas | O bloco de mudança de temperatura **só avança quando a temperatura do reator chegar à desejada**. | A espera pelo reator é mantida e corrigida (K08): mensagens, validade de `Tempval`, banda e permanência configuráveis, avaliação por bloco. |
| D-4 | Prioridade do abort | Abort substitui qualquer comando pendente e encerra a conclusão pendente imediatamente. Abort e parada de emergência raramente serão usados e têm a mesma finalidade: encerrar tudo. | `bathAbort` ≡ parada do banho de D-2 (K02). |

**Interpretação adotada em D-2 (confirmar):** "troca do modo controle automático" = colocar o
nó em `mode=manual`, de modo que nem a cascata nem a guarda do painel acionem mais os relés; o
C404 permanece no último SP e o operador fica livre para usar o painel. Se a intenção era outra
(p.ex. manter a guarda em `auto` defendendo o último SP), só o payload de parada muda em K01/K02.

### 2.1 Semântica resultante da parada do banho

Aplicada, na via externa, a `bathAbort`, `tempSetpoint=0` (inclui `CoreSafeStop` dos apps) e
`resetVariables`:

1. descartar setpoint pendente da cascata e qualquer operação pendente;
2. entregar ao nó uma única revisão `{"cmd_id":N,"stop":1}` (novo no r3.2: `abort` + `mode=manual`
   persistido), com prioridade máxima;
3. cascata → `off`; `tempReferenceCommanded=false`; conclusão pendente encerrada com motivo
   `stopped` (sem esperar 300 s); posse liberada;
4. via (`tempControlMode`) e `bathComm` **inalterados**; nada é enviado à UART (`100B` só na
   troca de via);
5. **retomada:** um novo `tempSetpoint > 0` na via externa readquire a posse, enfileira
   `mode=auto` e só então libera o primeiro setpoint calculado (re-semeado sem degrau a partir
   do SP lido no display).

`resetVariables` continua zerando os demais subsistemas como hoje; para o banho, deixa de trocar
a via e deixa de desligar `bathComm` antes de entregar o `stop`.

## 3. Achados

Severidade: **Crítico** = trava a malha ou faz a interface afirmar algo falso sobre o controle;
**Alto** = comportamento incorreto em operação normal; **Médio** = robustez/UX com contorno;
**Baixo** = consistência/documentação. Coluna "Etapa" indica onde é corrigido.

### 3.1 Hub (`ESP32S3-HUB`)

| ID | Sev. | Achado | Evidência | Efeito | Etapa |
|---|---|---|---|---|---|
| C1 | Crítico | `bathMode`/`bathSync`/`bathAbort` usam a mesma `bathBox` da cascata. `queueReliable` troca payload e revisão; `bathCommandLastSentId` continua apontando para o setpoint. | `Commands.h:511-565`, `Mailboxes.h:22-38`, `HttpServer.h:481-486` | Qualquer operação durante uma sequência (ou antes do ACK do setpoint) faz o `done` nunca casar com o ID → após 300 s `bath_completion_timeout` → **fault travado**. Setpoint ainda sem ACK é descartado silenciosamente. | K02 |
| C2 | Crítico | `bathCascadeReset` só chama `bathCascade.reset()`; não limpa `bathCommandCompletionPending`, e o fault é avaliado **por nível** (`bathState=="error"/"aborted"`, `guard=="suspended"`). | `Commands.h:221-233`, `Runtime.h:299-304` | Após timeout, erro de sequência ou abort, o reset re-trava a falha no ciclo seguinte. Não há caminho de recuperação. | K02 |
| C3 | Alto | Comando recusado pelo nó (`range`, `busy`, `display_invalid`…) não gera ACK nem motivo visível; o Hub reentrega a cada push (≈2 s) até o timeout de 300 s. O Hub não conhece `sp_min/sp_max` do nó. | `ConfigCodec.cpp:236-238`, `HubLink.cpp:131-136` | ~150 reentregas e fault genérico em vez de erro imediato com causa. | K01, K03 |
| C4 | Médio | A cascata compara a saída com o **próprio** último comando, não com `BathTarget`/`BathDisplaySp`. Com D-1 (API local bloqueada) e a guarda revertendo o painel, a divergência só sobra em casos residuais (guarda em `manual`/`suspended`, posse expirada). | `ExternalBathCascade.cpp` (`update`, bloco de `commandReady`) | Alvo diferente do calculado pode persistir até a saída andar mais que a banda. | K03 |
| C5 | Alto | `resetVariables` troca a via para UART e desliga `bathComm` limpando a caixa, sem entregar nada ao nó. `tempSetpoint=0` na via externa só desliga a cascata. | `Commands.h:235-267` | Contraria D-2; clientes passam a exibir via errada; guarda continua em `auto`. | K02 |
| C6 | Médio | Ao sair de `waiting_inputs` a PV filtrada não é reinicializada (`dt≈10 ms` no retorno). | `ExternalBathCascade.cpp` (`update`) | PV filtrada velha por ~1 min após longa ausência do nó. | K04 |
| C7 | Médio | `configure()` durante `controlling` não recalcula a integral. | `ExternalBathCascade.cpp` (`configure`) | Mudança de `Kp`/`bias` desloca a saída de uma vez (limitada só pelo slew). | K04 |
| C8 | Médio | Versão aceita por igualdade exata `"r3.1"`. | `HttpServer.h:420-424` | r3.2 seria recusado com 403; o Windows aceita `r3`, que o Hub recusa (`NodeFirmwareCatalog.cs:42`). | K03 |
| C9 | Médio | `setTemperature` **satura** 0–100 em vez de recusar. | `SensorUart.h:20-28` | Referência plausível inventada a partir de entrada inválida. | K04 |
| C10 | Médio | A configuração `bathCascade*` vigente não é publicada; rejeição só vira log serial. | `Commands.h:202-219`, `Telemetry.h` | App não consegue mostrar/confirmar a sintonia real. | K05 |
| C11 | Baixo | Não há evidência publicada do atuador original; `TempControlMode` e `TempControlViaBath` saem da mesma variável. | `Telemetry.h:497-501` | Alarme `ExternalBathDualActuation` do app é tautológico. | K05 |
| C12 | Baixo | Valores do nó continuam publicados após o nó ficar offline; `TempSetpoint` publica o valor persistido mesmo sem comando na sessão; `BathCascadeEnabled` = via∧comm; `tempRouteTransitionPending` nunca é lido. | `Telemetry.h:484-560`, `Settings.h:137` | Leitura ambígua para qualquer cliente. | K05 |

### 3.2 Nó `banho-termostatico` (r3.1)

| ID | Sev. | Achado | Evidência | Efeito | Etapa |
|---|---|---|---|---|---|
| N1 | Alto | A API local (`/command`, `/ui`) aceita comandos de atuação sem saber que o Hub é dono da malha. | `LocalHttpApi.cpp`, `ConfigCodec.cpp:207+` | Contraria D-1: celular/PC muda o alvo durante a cascata; `hub_enabled=0` pelo `bath_app` tira o nó da malha sem aviso. | K01 |
| N2 | Alto | Recusa de comando do Hub não é publicada (a resposta só vai ao log). | `HubLink.cpp:229-237` | Base de C3. | K01 |
| N3 | Médio | Após reboot do Hub, o `/bathData` recebe 403 e o nó só refaz `nodeHello` após 30 s. | `HubLink.cpp:79-93, 110-142, 164` | Até 30 s de nó "offline" após cada reboot do Hub. | K01 |
| N4 | Baixo | `state/phase/err/guard` vão na URL sem codificação. | `HubLink.cpp:120-129` | Erro futuro com espaço/`&` quebraria o push. | K01 |
| N5 | Médio | Não existe ação única "abortar + manual"; `abort` e `mode` no mesmo payload não são garantidos (a primeira ação reconhecida retorna). | `ConfigCodec.cpp:131-159` | Necessário para D-2 em uma única revisão confiável. | K01 |

### 3.3 Windows App

| ID | Sev. | Achado | Evidência | Efeito | Etapa |
|---|---|---|---|---|---|
| W1 | Crítico | `IsTempControlViaBath` e `IsCommEnabled` vêm da **preferência salva** e nunca são sincronizados com `TempControlViaBath`/`BathCommEnabled`. Alarmes e roteamento usam essa preferência (`BathRouted()` exige `RoutingRequested`). | `ExternalBathViewModel.cs:49-52, 339-367`, `ControlViewModel.cs:1127-1132`, `AlarmService.cs:768-770` | Hub em via externa com preferência "UART" ⇒ nenhum alarme do banho e interface mostrando a via errada. | K06 |
| W2 | Médio | `Abortar`, `Reset falha`, modo e sync exigem `CanApplyNow`, que exige `!IsAwaitingAck` (= `BathCommandPending`). | `ExternalBathViewModel.cs:141-152, 252-256`, `ExternalDeviceStatus.cs:305-311` | Parar/reset indisponíveis enquanto há comando do banho em curso. | K06 |
| W3 | Alto | Dois controles para o mesmo `tempSetpoint`: linha "1. Temperatura" (15–60 °C) e cartão do banho (0–100 °C). | `ShellViewModel.cs:288-292`, `ControlView.xaml:625-627`, `ExternalBathViewModel.cs:151, 230-239` | Contraria o plano (§6); faixas divergentes; "desligar" não explica que o C404 fica no último SP. | K07 |
| W4 | Alto | Sintonia: validação do app mais fraca que a do Hub, "Sintonia aplicada" mesmo quando o Hub rejeita, campos exibem a preferência. | `CommandBuilders.cs:1020`, `ExternalBathViewModel.cs:258-267` | Depende de C10. | K07 |
| W5 | Alto | `ExternalBathCommandTimeout` dispara com `CompletionAgeMs ≥ 10 s`; hold legítimo leva dezenas de segundos. | `AlarmService.cs:634-637` | Alarme falso a cada degrau grande. | K08 |
| W6 | Médio | `ExternalBathSetpointMismatch` compara `BathTarget` com `BathDisplaySp`; ramo `Contains("mismatch")` inalcançável; não compara com `BathCommandSetpoint`. | `AlarmService.cs:648-656` | Não detecta alvo divergente do calculado. | K08 |
| W7 | Médio | `ExternalBathReactorPvInvalid` usa `Temperature ≤ 10 °C` e ignora `TempvalValid`/`TempvalAgeMs`. | `AlarmService.cs:629-632` | Critério diferente do usado pelo PI. | K08 |
| W8 | Médio | `ExternalBathDualActuation` compara dois campos derivados da mesma variável. | `AlarmService.cs:679-683` | Alarme morto (C11). | K08 |
| W9 | Alto | Bloco de temperatura (via banho) já espera `|Tempval − alvo| ≤ 0,5 °C` — **semântica correta segundo D-3** —, mas: mensagem após o *grace* diz "não foi confirmada pela cascata" (soa como falha durante aquecimento normal); não exige `TempvalValid`; banda fixa, sem permanência (um cruzamento ruidoso libera o bloco); exigência capturada no início da execução; falha da cascata durante a espera não é mostrada. | `RecipeEngine.Devices.cs:75-115`, `RecipeEngine.cs:137` | Receita pode avançar num pico, ficar presa após troca de via ou esperar sem dizer por quê. | K08 |
| W10 | Médio | `StatusText` é sobrescrito a cada quadro de telemetria. | `ExternalBathViewModel.cs:364-365` | Mensagens de recusa desaparecem em ~1 s. | K06 |
| W11 | Baixo | Texto "Cascata automática solicitada" para `bathMode=auto` (guarda do nó). | `ExternalBathViewModel.cs:218` | Confunde guarda com cascata. | K06 |
| W12 | Baixo | Parser zera `BathSp/Target/Pv/Deviation` offline, mas mantém `BathState/Guard/Error/DisplaySp`. | `TelemetryParser.cs` (`ParseBath`) | Estado velho exibido. | K08 |

### 3.4 `Android_app` (cliente geral do Hub)

| ID | Sev. | Achado | Evidência | Efeito | Etapa |
|---|---|---|---|---|---|
| A1 | Médio | Nenhuma consciência da via térmica: "Temperature Control" OFF, `coreSafeStop` e `emergencyStopAll` são apresentados como desligamento; na via externa o C404 permanece no último SP. Com K02 o comportamento do Hub passa a ser correto (parada do banho); falta o app dizer o que aconteceu. | `device_control_provider.dart:70-73, 357-375`, `controls_screen.dart:521-545` | Mensagem enganosa ao operador. | K09 |
| A2 | Médio | Entrada inválida vira 25 °C (`double.tryParse(...) ?? 25.0`). | `controls_screen.dart:531` | Setpoint plausível inventado. | K09 |
| A3 | Baixo | Não exibe estado do banho/cascata. | — | Supervisão só no Windows. | K09 |

### 3.5 `bath_app` (Flutter direto ao nó)

| ID | Sev. | Achado | Evidência | Efeito | Etapa |
|---|---|---|---|---|---|
| B1 | Alto | Opera o nó sem saber se o Hub está no controle; `hub_enabled` editável sem confirmação. | `lib/models/bath_config.dart:37`, `pages/*` | Face cliente de N1. | K10 |

### 3.6 Documentação

| ID | Sev. | Achado | Etapa |
|---|---|---|---|
| D1 | Baixo | `IMPLEMENTATION_PLAN_BANHO.md` §1/§3.3 ainda diz Hub e Windows "planejado; não implementado"; `IMPLEMENTATION_PLAN_BANHO_HUB.md` diz "nenhuma alteração implementada"; decisão 7 do plano geral ("`tempSetpoint=0` não mexe no C404") muda com D-2. | K11 |
| D2 | Baixo | Não há procedimento de recuperação de `fault` nem descrição da parada do banho para o operador. | K11 |

## 4. Plano de implementação

Ordem: contrato do nó → Hub → Windows → Android_app → bath_app → documentação/bancada. Cada
etapa termina testada e em commit próprio em `main` (padrão das passagens de bancada), com
verificação de branch antes de cada commit (`.git` no OneDrive).

### K00 — Arnês de teste da orquestração do Hub (pré-requisito)

**Ações:** criar `ESP32S3-HUB/tests/host-bath-orchestration/` que compile
`ExternalBathCascade.cpp` e uma extração pura da orquestração — mover a lógica de
`serviceExternalBathCascade`, a regra de conclusão (`done`+ID) e a caixa do banho para
`src/control/BathCommandCoordinator.{h,cpp}`, sem Arduino/HTTP —, com relógio simulado e um nó
falso (ACK, `running→done`, recusa com motivo, `stop`, `error`, guarda).
**Testes (devem falhar antes das correções):** C1 (mode durante sequência → sem fault),
C2 (reset após timeout/erro sai de fault), C3 (recusa `range` → fault imediato com causa),
C5/D-2 (`tempSetpoint=0` e `resetVariables` → `stop` entregue, via inalterada).
**Commit:** `test(hub): arnes host da orquestracao do banho`

### K01 — Nó r3.2: posse do Hub, parada única e recusa observável

**Ações:**
- **Posse (D-1, N1):** o Hub declara posse em toda resposta do `/bathData` (cabeçalho
  `X-Hub-Owner: 0|1`); o nó mantém `hub_owned` com expiração de 10 s sem push aceito.
  Enquanto `hub_owned=1`, a API local recusa com `409 hub_owned` `setpoint`, `delta`, `home`,
  `key`/`hold_ms`, `sync_sp`, `mode`, `reset_nvs` e toda chave de configuração (inclusive
  `hub_enabled`); `abort`/`stop` e leituras continuam livres. `/status` expõe `hub_owned` e
  `hub_last_push_ms`. O painel físico segue coberto pela guarda em `auto`, como hoje.
- **Parada (D-2, N5):** nova ação `{"stop":1}` = `setpointAbort()` + `guardSetMode(MANUAL)`
  persistido, idempotente por `cmd_id`, aceita mesmo sem sequência em curso (ACK sempre).
- **Recusa observável (N2):** publicar no `/bathData` `rej_cmd_id` e `rej_err` (último comando
  do Hub recusado e motivo) e `sp_min`/`sp_max`.
- **Robustez (N3, N4):** 403/404 do Hub zeram `g_hubAnnounced` (hello no ciclo seguinte);
  codificar `state/phase/err/guard` na URL.
- `FirmwareVersion = "r3.2"`; `PROTOCOL.md` atualizado.
**Testes:** novos cenários no `tests/host-sim` (comando local recusado com posse; posse expira
sem push; `stop` durante hold libera relés e deixa `manual`; recusa publicada); compilar com
arduino-cli (`--fqbn esp32:esp32:esp32s3`) e registrar flash/RAM.
**Commit:** `feat(bath-firmware): r3.2 com posse do Hub, parada e recusa publicada`

### K02 — Hub: filas separadas, parada do banho e recuperação (C1, C2, C5, D-2, D-4)

**Ações:**
- `BathCommandCoordinator` com três slots e prioridade **stop > operação (mode/sync) >
  setpoint**; um payload por push. A conclusão pendente fica vinculada ao ID do *setpoint*;
  ACK de uma operação não a invalida.
- **Parada** (seção 2.1) disparada por `bathAbort`, `tempSetpoint=0` na via externa e
  `resetVariables`: preempta os slots, entrega `stop`, cascata `off`, conclusão encerrada com
  motivo `stopped`, posse liberada; via e `bathComm` mantidos; `resetVariables` deixa de trocar
  a via e de limpar a caixa antes da entrega.
- **Retomada:** novo `tempSetpoint > 0` na via externa → posse → `mode=auto` → primeiro
  setpoint re-semeado sem degrau pelo display.
- **Fault por borda:** registrar o ID/estado que causou a falha; `bathCascadeReset` limpa a
  conclusão pendente e ignora `error/aborted` herdados até o próximo comando.
- Publicar `BathCascadeFaultReason` (`bath_error:<err>`, `guard_suspended`,
  `completion_timeout`, `node_rejected:<err>`) e `BathOwned`.
**Testes:** K00 verdes; contratos atualizados (`test_bath_routing.py`, `test_bath_contract.py`).
**Commit:** `fix(hub): separar comandos do banho, parada sem troca de via e reset da falha`

### K03 — Hub: recusa, faixa do nó, alvo divergente e versão (C3, C4, C8)

**Ações:**
- consumir `rej_cmd_id/rej_err`: recusa do setpoint vigente → fault imediato
  `node_rejected:<err>` (exceto `busy`, que reentrega);
- limitar a saída à interseção `[max(outputMin, sp_min), min(outputMax, sp_max)]`;
- alvo divergente (D-1): nó ocioso, sem conclusão pendente e `|BathTarget − último comando| ≥
  banda` por mais de `guard_delay` → re-enviar o comando vigente; após 3 re-envios sem efeito →
  fault `target_override`;
- aceitar versão por mínimo (`r3.2` ≤ versão < `r4`) com parser testado; o Windows espelha a
  mesma regra em `NodeFirmwareCatalog`.
**Testes:** arnês K00 + `test_bath_node_integration.py`.
**Commit:** `fix(hub): tratar recusa e alvo divergente do no do banho`

### K04 — Hub: bordas do PI e da referência (C6, C7, C9)

**Ações:** reinicializar o filtro ao sair de `waiting_inputs`/`paused` longo; transferência
sem degrau em `configure()` (recalcular `I` para manter `outputC_`); `tempSetpoint` fora de
0–100 °C rejeitado (sem saturação).
**Testes:** `host-bath-cascade` (retorno após 1 h offline, retune em regime, referência inválida).
**Commit:** `fix(hub): retomada e ressintonia sem degrau na cascata do banho`

### K05 — Hub: telemetria (C10, C11, C12)

**Ações:** publicar a configuração `bathCascade*` vigente (ou `BathCascadeConfigRev` +
`/bathCascadeConfig`) e `BathCascadeConfigRejected` com motivo; publicar
`TempModuleActuatorOn` e `TempSetpointCommanded`; `null` para campos do nó offline;
`BathCascadeActive` separado de `BathCascadeEnabled`; remover `tempRouteTransitionPending`.
**Testes:** `test_bath_telemetry.py`, `test_json_keys.py`, marca d'água do quadro; compilar
(`tools/compile.ps1`) e registrar flash/RAM.
**Versão:** `10.6.0-dev`. **Commit:** `feat(hub): expor sintonia, posse e atuador original do banho`

### K06 — Windows: estado real do Hub (W1, W2, W10, W11)

**Ações:**
- sincronizar `IsTempControlViaBath`/`IsCommEnabled` a partir de `TempControlViaBath`/
  `BathCommEnabled` (com janela de pedido pendente); a preferência vira só rascunho, com
  indicador "preferência ≠ Hub";
- `SetRoutingRequested(ExternalBath)` e `BathRouted()` baseados no Hub;
- botão **"Parar banho"** (envia `bathAbort`; texto: "cascata desligada, C404 em manual no
  último SP") habilitado sempre que o Hub estiver conectado; `Reset falha` habilitado com
  `BathCascadeState=fault` e mostrando `BathCascadeFaultReason`;
- mostrar `BathOwned` e a retomada ("novo setpoint do reator religa a cascata e o modo
  automático");
- `StatusText` separado em estado (telemetria) e última ação; rótulos "Guarda automática do
  C404" ≠ "Cascata".
**Testes:** `ExternalBathViewModelTests` (Hub em via externa com preferência UART; parar com
`BathCommandPending=true`; mensagem de recusa persiste), `AlarmServiceTests`.
**Commit:** `fix(windows): refletir via real do Hub e parada do banho`

### K07 — Windows: um só setpoint do reator e sintonia confirmada (W3, W4)

**Ações:** remover o campo/Enviar do cartão do banho; a linha "1. Temperatura" passa a
"Setpoint do reator" com a faixa da via ativa; desligar na via externa informa "cascata
desligada; o C404 permanece em X °C em manual"; builder de sintonia replica a validação do Hub
(fonte única de faixas); painel mostra o valor do Hub e marca campos editados; sucesso só
após eco (K05).
**Testes:** golden strings, faixas, eco/rejeição, XAML (instanciação real).
**Commit:** `fix(windows): unificar referencia do reator e confirmar sintonia no Hub`

### K08 — Windows: receitas pela temperatura do reator e alarmes (W5–W9, W12)

**Receitas (D-3):** o bloco de temperatura na via externa só avança quando o reator chega ao
valor pedido:
- condição: `TempvalValid`, `TempvalAgeMs` dentro do limite, `TempSetpoint` ecoado igual ao
  alvo, cascata sem fault, e `|Tempval − alvo| ≤ banda` **continuamente por T s**;
- banda e permanência como parâmetros do bloco (padrões propostos: 0,5 °C e 30 s), gravados
  na receita e validados pelo `RecipeValidator`;
- sem timeout silencioso: enquanto espera, mostrar "Aguardando o reator: Tempval X °C → alvo
  Y °C" (não "não confirmada"); fault da cascata, nó offline ou `Tempval` inválida mostram a
  causa e mantêm o bloco em espera; o operador pode pular ou parar, como hoje;
- a exigência é avaliada **no início de cada bloco** pela via real do Hub, não no início da
  execução; na via UART o comportamento atual é mantido.

**Alarmes:** `CommandTimeout` separado em "sem ACK" (> 10 s com `BathCommandPending`) e
"conclusão lenta" (> 240 s, antes do fault de 300 s do Hub); `SetpointMismatch` com
`BathCommandSetpoint` vs `BathTarget` e `BathTarget` vs `BathDisplaySp`; `ReactorPvInvalid`
por `TempvalValid/TempvalAgeMs`; `DualActuation` por `TempControlViaBath ∧
TempModuleActuatorOn`; parser anula estado do nó offline.
**Testes:** `RecipeEngineTests` (passa só após permanência; pico isolado não passa; Tempval
inválida segura; troca de via entre blocos), `AlarmServiceTests`, `BathIntegrationTests`,
simulador atualizado para Hub 10.6/nó r3.2.
**Commit:** `fix(windows): receitas pela temperatura do reator e alarmes do banho`

### K09 — `Android_app`: consciência da via (A1–A3)

**Ações:** modelo `BathTelemetry` (via, presença, estado da cascata, posse, `BathPv/BathSp`,
motivo de fault); cartão de temperatura mostra a via e vira "Setpoint do reator" na via
externa; OFF/parada na via externa exibem "cascata desligada; C404 em manual no último SP";
remover o `?? 25.0`; cartão somente leitura do banho. Troca de via e sintonia continuam fora
do celular.
**Testes:** `telemetry_parser_test.dart` (Hub antigo/novo/nulo), `device_control_test.dart`,
`flutter analyze`.
**Commit:** `feat(android): exibir via termica e banho externo do Hub`

### K10 — `bath_app`: posse do Hub (B1)

**Ações:** ler `hub_owned`/`hub_last_push_ms` do `/status`; faixa "Controlado pelo Hub —
somente leitura" desabilitando tudo exceto Parar; tratar `409 hub_owned`; aceitar r3.2.
**Testes:** `bath_status_test.dart`, `bath_service_test.dart` (MockClient), `flutter analyze`,
APK release.
**Commit:** `feat(bath-app): respeitar posse do Hub no banho`

### K11 — Documentação e roteiro de bancada

**Ações:** atualizar status e a decisão 7 em `IMPLEMENTATION_PLAN_BANHO*.md` (D1); parada do
banho, retomada e recuperação de fault no `MANUAL_DO_OPERADOR.md` (D2); `WIRE_CONTRACT_V9.md`
(r3.2/10.6: `stop`, `X-Hub-Owner`, `rej_*`, `sp_min/max`, novos campos); risco-auditoria com
os itens corrigidos; novos ensaios no H08: P09 (mode/parar durante hold), P10 (reset após
erro/timeout), P11 (comando pelo celular com posse ativa → recusado), P12 (reboot do Hub com o
nó ligado: tempo até online), P13 (bloco de receita só avança após permanência na banda).
**Commit:** `docs(bath): registrar correcoes da integracao e novos ensaios de bancada`

### K12 — Bancada

G1–G9 do nó → H08 (P01–P13) → H09 com água → H10. Nenhuma etapa K substitui esses gates.

## 5. Dependências e caminho mínimo

```text
K00 ──► K02 ──► K03 ──► K05 ──► K06 ──► K07 ──► K08
         ▲       ▲                        │
K01 ─────┴───────┘                        ├──► K09
                 K04 (independente)       └──► K10 (depende de K01)
                                               K11 ao final; K12 após tudo
```

Caminho mínimo para voltar à bancada H08: **K00, K01, K02, K06, K08** — cascata sem travas
(C1/C2), parada coerente com D-2, posse do Hub, interface com a via real e receita que espera
o reator.

## 6. Critérios de aceite desta rodada

- `bathMode`, `bathSync` durante sequência não geram fault espúrio.
- `bathAbort`, `tempSetpoint=0` (via externa) e `resetVariables` produzem a mesma parada:
  `stop` entregue, nó em `manual`, cascata `off`, via e `bathComm` inalterados.
- Novo `tempSetpoint` após a parada religa `auto` e a cascata sem degrau.
- `bathCascadeReset` após erro ou timeout volta a controlar sem degrau, removida a causa.
- Recusa do nó vira fault imediato com a causa literal.
- Com posse ativa, `bath_app`, `/ui` e `bath_app.py` não conseguem mover o C404 (só parar).
- Windows mostra a via e a comunicação reais do Hub; alarmes do banho dependem do Hub.
- Um único controle da referência do reator por aplicativo.
- Bloco de temperatura em receita na via externa só avança com `Tempval` válida dentro da
  banda pelo tempo de permanência.
- Todas as suítes (contratos, host da cascata, arnês K00, host-sim do nó, `dotnet test`,
  `flutter test` dos dois apps) verdes e firmwares compilados dentro do orçamento.

## 7. Execução (2026-09-22)

| Etapa | Contexto | Commit | Verificação |
|---|---|---|---|
| K01 | nó `banho-termostatico` r3.2 | `7a09626` | host-sim ALL PASSED (novos W/X/Y); esp32s3 1 085 689 B (82%) / 48 344 B |
| K00, K02–K05 | Hub 10.6.0-dev | `0f6858b` | `host-bath-orchestration` e `host-bath-cascade` ALL PASSED; contratos 125 OK; esp32s3 1 159 072 B (88%) / 52 216 B |
| K06–K08 | Windows App | `33b0204` | `dotnet test` 1755/1755; Release + smoke `--workspace --nav control`; instalador `OpenTECHub_Setup_v0.26.5-dev.45.exe` |
| K09 | `Android_app` | `5ff8845` | `flutter analyze` limpo; 84 testes |
| K10 | `bath_app` 1.2.0+3 | `5f6ae73` | `flutter analyze` limpo; 24 testes |
| K11 | documentação dos planos | este commit | — |

Diferenças em relação ao texto das etapas:

- **Receitas (K08):** banda e permanência ficaram em `ExternalBathSettings.ReactorSettleBandC/HoldS`
  (padrões 0,5 °C / 30 s, valem para todos os blocos) em vez de parâmetros por bloco — evita mudar
  o formato do documento de receita; pode virar parâmetro do bloco depois.
- **Parada (K02):** implementada com o nó em **manual** (interpretação de D-2). Se a intenção for
  deixar a guarda em automático defendendo o último SP, basta trocar a ação `stop` do nó.
- **Android_app (K09):** além de leitura e Parar, recebeu Reset falha e a chave da guarda do C404
  (pergunta do responsável sobre mudar o modo pelo app). Troca de via e sintonia continuam só no
  Windows.
- **Versão mínima:** Hub aceita somente nós `r3.2`+ (`r3.1` deixa de ser aceito); nó e Hub devem
  ser gravados juntos.
- Pendências físicas inalteradas: G1–G9 do nó, H08 (P01–P13), H09 e H10.
