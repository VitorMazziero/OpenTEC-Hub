# PLANO DE IMPLEMENTAÇÃO DO WINDOWS APP: BANHO EXTERNO

**Data:** 2026-09-21
**Status:** planejamento detalhado — **nenhuma alteração no `Windows_app` foi implementada**
**Base:** alterações W1–W17 do plano geral, alinhadas ao contrato final do plano do Hub
**Alvo previsto:** OpenTECHub 0.26.4 → 0.27.0

## 1. Objetivo e responsabilidade do aplicativo

Adicionar ao Windows App a operação e supervisão do banho externo como segunda via térmica,
sem executar a malha de controle no PC.

O aplicativo deve:

- selecionar a via e enviar a referência de temperatura do reator;
- mostrar separadamente reator, saída da cascata e estado do C404;
- operar modo/sync/abort e habilitação de comunicação;
- permitir sintonia avançada do controlador residente no Hub;
- integrar alarmes, receitas, sessão, gráficos, catálogo de nós e simulador;
- continuar funcional com Hubs antigos, desabilitando a superfície não suportada.

O aplicativo não deve:

- calcular a saída PI;
- enviar `tempSetpoint` como SP direto do C404;
- inventar presença, PV ou confirmação quando campos estiverem ausentes;
- restaurar via, `bathComm` ou sintonia por envio automático ao abrir o programa;
- afirmar que liberar/parar desliga fisicamente o C404.

## 2. Dependências e estado inicial

- O código atual do Windows App não contém `ExternalBathViewModel`, `Bath*`,
  `tempControlMode` ou `BathCascade*`.
- O desenvolvimento pode começar contra o simulador, mas a integração real depende do contrato
  fechado no `IMPLEMENTATION_PLAN_BANHO_HUB.md`.
- O nó r3 e o app Android próprio são independentes deste plano.
- Usar workspace explícito em todos os smokes; não abrir seletor de pasta.

## 3. Semântica que a interface deve preservar

| Conceito | Rótulo recomendado | Fonte |
|---|---|---|
| `tempSetpoint` | Setpoint do reator | operador/receita |
| `Tempval` | Temperatura do reator | sensor do módulo |
| `BathCascadePvFiltered` | Temperatura filtrada do reator | Hub |
| `BathCommandSetpoint` | Setpoint calculado do banho | PI no Hub |
| `BathCommandConfirmed`/`BathSp` | SP confirmado no C404 | nó/display |
| `BathPv` | Temperatura do banho | C404 |
| `BathCascadeError` | Erro do reator | Hub |
| `BathDeviation` | Desvio interno do C404 | nó |

“Automático” no nó é a guarda do painel; “cascata controlando” é um estado do Hub. Nunca usar
o mesmo indicador para ambos.

## 4. Contrato de comandos

Adicionar chaves e builders invariantes:

```text
{"tempControlMode":1}
{"tempControlMode":0}
{"tempSetpoint":30.0}
{"bathComm":1} / {"bathComm":0}
{"bathMode":"auto"} / {"bathMode":"manual"}
{"bathSync":30.0}
{"bathAbort":1}
{"bathCascadeReset":1}
```

Builders avançados para todos os parâmetros `bathCascade*` do plano do Hub. Todo comando da
via, operação do nó ou sintonia pertence ao `ActuatorId.Temperature` no `CommandArbiter`.

Troca de via e setpoint não devem ser agrupados no mesmo frame. A UI confirma primeiro a via,
aguarda telemetria correspondente e somente depois habilita novo envio de setpoint.

## 5. Modelo e parser de telemetria

### 5.1 Presença e transporte

- `HasBathTelemetry`;
- `BathOnline`, `BathCommEnabled`;
- `BathCommandPending`, `BathCommandId`, `BathCommandAck`;
- `BathIP`, `BathNodeVer`, `BathNodeMac`, `BathLastSeenAt`;
- `TempControlViaBath`.

### 5.2 Estado do nó

- `BathSp`, `BathKnown`, `BathTarget`;
- `BathState`, `BathPhase`, `BathError`;
- `BathPv?`, `BathMode`, `BathGuard`, `BathDeviation?`, `BathSpSource`.

### 5.3 Estado da cascata

- `TempSetpoint?`;
- `BathCascadeState`, `BathCascadeEnabled`;
- `BathCascadeError?`, `BathCascadePvFiltered?`;
- `BathCommandSetpoint?`, `BathCommandConfirmed?`;
- `BathCascadeP?`, `BathCascadeI?`;
- `BathCascadeSaturated`;
- `BathCascadePausedReason`;
- `BathCascadeLastUpdateMs?`.

`SensorReadings.Clone`/snapshots/comparações devem preservar todos os campos. Campos ausentes
ou JSON `null` permanecem nullable. Hub antigo resulta em `HasBathTelemetry=false`, não em
`BathOnline=false` inventado.

## 6. View-model

Criar `ExternalBathViewModel` com:

- `Status : ExternalDeviceStatus` (presença × via);
- `IsTempControlViaBath`, reversível quando envio for recusado;
- `IsTempControlRouteAvailable` e texto de compatibilidade;
- PV/SP/alvo/estado/fase/erro/modo/guarda/desvio formatados;
- referência, PV filtrada, erro, saída, P/I, saturação e motivo de pausa da cascata;
- `IsCommEnabled`, `IsCommandPending`;
- comandos de modo, sync, abort, reset de falha e aplicação explícita da sintonia;
- validação de finitude/faixa/coerência sem substituição silenciosa;
- journal nas transições de presença, via, fault, saturação e `sp_mismatch`.

Em `ControlViewModel`:

- propriedade `ExternalBath` e expansor da linha de temperatura;
- `CanApplyNow` bloqueia a via externa sem Hub compatível, comunicação, nó pronto ou cascata
  habilitável;
- faixa de entrada acompanha limites publicados/configurados da via externa;
- mensagem específica explica o bloqueio;
- o setpoint continua no `SubsystemViewModel` de temperatura, não num segundo controle.

## 7. Interface

Na linha `1. Temperatura`:

1. rótulo **Setpoint do reator**;
2. PV principal = temperatura do reator;
3. expansor “Banho externo — Contemp C404”;
4. seletor `Módulo (UART)` ↔ `Banho externo C404`;
5. estado de presença/comunicação e comando pendente;
6. cartão do C404: BathPv, BathSp, alvo, estado/fase/erro, modo/guarda/desvio;
7. cartão da cascata: PV filtrada, erro, saída calculada, confirmação, P/I, saturação e pausa;
8. controles `Banho no Hub`, Manual/Automático, Sincronizar, Abortar e Reset de falha;
9. painel avançado recolhido para ganhos, bias, limites e temporizações;
10. aviso permanente: **liberar a cascata não desliga o C404**.

O seletor fica desabilitado com Hub antigo. Preferências visuais podem persistir; comandos não
são enviados automaticamente ao abrir o app.

## 8. Alarmes

Adicionar identidades separadas, com on-delay/latch/ack seguindo o serviço atual:

| Alarme | Condição inicial |
|---|---|
| `ExternalBathOffline` | via externa + comm + nó offline por 10 s |
| `ExternalBathReactorPvInvalid` | PV do reator inválida/stale |
| `ExternalBathCommandTimeout` | revisão pendente além do limite |
| `ExternalBathSequenceFault` | `error`, `aborted` ou guarda `suspended` |
| `ExternalBathSetpointMismatch` | `sp_mismatch` ou confirmação desconhecida |
| `ExternalBathCascadeSaturated` | saturação persistente fora da tolerância |
| `ExternalBathReactorDeviation` | erro do reator persistente |
| `ExternalBathPvLimit` | BathPv fora dos absolutos |
| `ExternalBathImplausibleDelta` | diferença banho–reator implausível |
| `ExternalBathDualActuation` | via externa e placa original ainda ativa, se observável |

`BathDeviation` em modo manual pode ser aviso operacional, não necessariamente alarme crítico.

## 9. Receitas e segurança

- Passo de temperatura envia referência do reator.
- Na via externa, aguarda cascata ativa, revisão confirmada e `BathState=done` quando houver
  novo comando de atuador.
- `error`, `aborted`, fault, PV inválida ou nó offline colocam a receita em hold pelo mecanismo
  existente.
- Timeout depende da distância térmica e dos tempos identificados; não usar uma constante
  silenciosa baseada em número de toques.
- Parada de emergência envia `tempSetpoint=0`; a interface e o journal registram que isso
  libera a cascata, mas não desliga o C404.
- Parada não muda a via automaticamente.

## 10. Persistência, sessão e gráficos

Persistir como preferência, sem autoenvio:

- via de temperatura preferida;
- `bathComm` preferido;
- expansão do painel e preferências de séries;
- valores editados de sintonia somente como rascunho até aplicação explícita.

CSV/sessão:

- `TempSetpoint`, `Tempval`, PV filtrada, erro, P, I;
- `BathCommandSetpoint`, `BathCommandConfirmed`;
- `BathPv`, `BathSp`, `BathTarget`, modo/guarda;
- saturação, estado, motivo de pausa e via.

Gráfico de temperatura: séries opcionais para BathPv, PV filtrada e saída calculada. Não usar
escala que faça setpoint do C404 parecer PV do reator.

## 11. Catálogo de nós e firmware

- `NodeFirmwareCatalog.Bath = "bath"`;
- incluir `bath` nos dispositivos e validar versão `r3`;
- `HubNodesViewModel`/painel devem suportar seis nós sem truncar colunas;
- versão incompatível gera estado visível, sem bloquear diagnóstico.

## 12. Simulador

Estender `OpenTECHub.Simulator` com duas massas térmicas:

```text
dT_banho/dt  = malha interna do C404 até BathCommandSetpoint
dT_reator/dt = troca banho↔reator + perda ambiente + calor metabólico
```

Simular:

- nó r3, mailbox/ACK/done e tempos de sequência;
- C404 em 30,0 °C com reator em 28,7 °C até o PI remover o offset;
- entrada posterior de calor metabólico;
- nó offline, PV inválida, saturação, error, abort e guarda suspenso;
- via original preservando o modelo antigo;
- relógio determinístico, sem sleeps nem rede real.

O simulador reproduz o contrato; não é validação física do controlador.

## 13. Arquivos previstos

| Área | Arquivos principais |
|---|---|
| protocolo | `OpenTECHub.Protocol/CommandKeys.cs`, `CommandActuators.cs`, `CommandBuilders.cs`, `SensorReadings.cs`, `TelemetryParser.cs` |
| simulador | `OpenTECHub.Simulator/DeviceModel.cs`, `WireCodec.cs`, `HttpEndpoint.cs` |
| VM/UI | novo `ExternalBathViewModel.cs`, `ControlViewModel.cs`, `SubsystemViewModel.cs`, `ControlView.xaml`, `App.xaml.cs` |
| alarmes | `Services/Alarms/AlarmModels.cs`, `AlarmService.cs` |
| receitas | `RecipeEngine.Actuation.cs`, `RecipeEngine.Devices.cs`, `RecipeValidator.cs` |
| persistência | `AppSettings.cs`, `SettingsViewModel.cs` |
| dados | `SessionLogger.cs`, `SessionFiles.cs`, `TelemetryHistory.cs`, `ChartsViewModel.cs` |
| nós | `NodeFirmwareCatalog.cs`, `HubNodesViewModel` e testes |
| testes | novo `ExternalBathTests.cs` e suites específicas por serviço |
| docs | `PROTOCOL.md`, `UI_DESIGN.md`, `DECISIONS.md`, `MANUAL_DO_OPERADOR.md`, `CHANGELOG.md`, `CURRENT_STATUS.md` |

## 14. Etapas detalhadas de execução

Cada etapa termina testada e em commit próprio. Não editar firmware do Hub neste roteiro.

### W00 — Baseline e fixtures congeladas

**Ações:** executar suíte atual; compilar Release; lançar com workspace temporário explícito;
capturar fixtures completas, parciais, nulas e de Hub antigo; registrar logs/crashes existentes.

**Conclui quando:** baseline e critérios visuais documentados.
**Commit:** documentação/fixtures apenas, se necessário.

### W01 — Adicionar contrato tipado

**Ações:** chaves, builders, actuator map, campos em `SensorReadings`, clone/snapshots e parser
com presença/nulabilidade corretas.

**Testes:** golden strings, finitude/faixa, parser completo/parcial/nulo/antigo e arbiter.
**Commit:** `feat(windows): adicionar contrato do banho externo e cascata`

### W02 — Implementar simulador térmico e do nó

**Ações:** duas massas, mailbox/ACK/done, falhas injetáveis, rotas e serialização idêntica ao
Hub; preservar cenários anteriores.

**Testes:** seis cenários térmicos obrigatórios com relógio determinístico.
**Commit:** `feat(simulator): modelar banho externo e cascata termica`

### W03 — Implementar view-model e integração de DI

**Ações:** `ExternalBathViewModel`, comandos, validação, reversão de toggle, journal,
`CanApplyNow`, faixa por via e registros no container.

**Testes:** estados de presença/via, recusa, ranges, sintonia inválida, Hub antigo e threading
de atualizações de telemetria.
**Commit:** `feat(windows): adicionar view-model do banho externo`

### W04 — Implementar a interface operacional

**Ações:** expansor na linha de temperatura, cartões do C404/cascata, seletor, ações, painel
avançado e avisos de segurança; responsividade em telas pequena e grande.

**Testes:** contratos XAML, instanciação real da view e revisão visual contra o simulador.
**Commit:** `feat(windows): adicionar interface do banho externo`

### W05 — Integrar alarmes e segurança

**Ações:** alarmes da seção 8, on-delay, latch/ack, journal e comportamento de stop/liberação.

**Testes:** falha, recuperação, reconhecimento, reentrada, sem falso alarme em Hub antigo.
**Commit:** `feat(windows): supervisionar falhas da cascata do banho`

### W06 — Integrar receitas

**Ações:** hold por estado da cascata/ACK/done, timeout baseado no processo, validação de
receitas e mensagens operacionais.

**Testes:** sucesso, timeout, offline, PV inválida, error/abort e retomada controlada.
**Commit:** `feat(windows): integrar banho externo às receitas`

### W07 — Persistência, sessão, gráficos e catálogo

**Ações:** preferências sem autoenvio, colunas/séries, nó r3 e compatibilidade de arquivos.

**Testes:** round-trip de settings, CSV antigo/novo, séries opcionais e catálogo com seis nós.
**Commit:** `feat(windows): registrar e visualizar cascata do banho`

### W08 — Documentação, versão e validação de runtime

**Ações:** protocolo, ADRs, UI/manual/changelog/status; versão 0.27.0 somente após suíte verde;
build Release e execução real com `--workspace`, navegação para Controle e logs frescos.

**Conclui quando:** zero crash/XAML/DI/binding relevante; capturas aprovadas; `git diff --check`
limpo.
**Commit:** `docs(windows): fechar integracao do banho externo`

## 15. Matriz mínima de testes

| Superfície | Casos obrigatórios |
|---|---|
| parser | completo, parcial, null, Hub antigo, strings máximas |
| builders | golden strings, NaN/inf, faixa e cultura invariável |
| VM | toggle aceito/recusado, online/offline, busy/fault/saturado |
| UI | sem suporte, esperando entradas, controlando, pausado, falha |
| alarmes | on-delay, latch, ack, recuperação e repetição |
| receitas | ACK sem done, done, error, abort, timeout e hold |
| sessão | colunas novas, abertura retrocompatível e null |
| simulador | offset, calor, rede, PV, saturação e degrau |
| runtime | criação da view, navegação, resize, tema e logs |

## 16. Comandos de verificação previstos

```powershell
dotnet test Windows_app/OpenTECHub.slnx --no-restore
dotnet build Windows_app/OpenTECHub.slnx -c Release --no-restore
Windows_app/src/OpenTECHub/bin/Release/net10.0-windows/win-x64/OpenTECHub.exe `
  --workspace <diretorio-temporario> --nav control --exit-after-ms 5000
git diff --check
```

Usar o caminho real produzido pelo build. Após alterações de XAML ou DI, build verde sozinho
não encerra a etapa: o executável precisa abrir e os logs novos precisam ser revisados.

## 17. Critérios de aceite

- O operador distingue referência/PV do reator de setpoint/PV do banho.
- Não existe campo operacional que envie SP direto ao C404 pelo Hub.
- Hub antigo desabilita a integração sem crash nem falsa presença.
- Recusa de via reverte o toggle e explica o motivo.
- Alarmes e receitas respondem a PV inválida, offline, timeout, error e abort.
- Stop/liberação nunca é descrito como desligamento físico do C404.
- Suíte, Release, smoke e revisão visual passam com evidência nova.
- Nenhuma conclusão do simulador é apresentada como validação de bancada.
