# Plano de implementação: sincronização do Fluxômetro v05 via ESP32 Hub v7

## Objetivo

Adaptar o `OpenTECHub` para tratar o ESP32 Hub v7 como intermediário obrigatório do
`flowmeter_OpenTECHUB_V05`, usando `FlowCommandPending` como confirmação assíncrona e
`FlowmeterOnline` como estado do enlace interno. A interface deve impedir comandos concorrentes,
distinguir a perda do Hub da perda do fluxômetro e nunca anunciar aplicação antes da confirmação.

## Premissas congeladas

1. Os firmwares `OpenTEC_ESP32_v7` e `flowmeter_OpenTECHUB_V05` não serão modificados.
2. O app se comunica somente com o Hub, por USB serial ou HTTP em `192.168.4.1`.
3. `cmd_id`, repetição, idempotência e ACK entre Hub e fluxômetro pertencem aos firmwares.
4. O app não expõe `kp_flow`, `ki_flow` nem `reconnect_wifi`.
5. O lote será registrado no branch dedicado `codex/flowmeter-v05-hub-sync` após o commit do
   ajuste visual que o antecede.

## Evidência do contrato v7

Uma inspeção somente de leitura do firmware congelado confirmou que:

- `queueReliableFlowCommandFromJson` reconhece `flowSetpoint`, `valve_1`, `valve_2`, `v_Flow`,
  `maxFlow`, `k1`, `f1`, `c1`, `k2`, `f2` e `c2`;
- o Hub conserva o estado desejado até receber o ACK do `cmd_id` correspondente;
- `/readData` publica `FlowCommandPending`, `FlowmeterOnline`, `FlowCommandId` e
  `FlowCommandAck`;
- `flowmeterComm` não integra o comando encaminhado ao fluxômetro v05 e por isso ficou fora
  dos quadros de setpoint e de parada segura. **Correção posterior:** ele não é legado — o
  firmware do Hub v7 o interpreta, persiste em `flowComm` e o republica como
  `FlowControlEnabled`, único registro de malha ativa. Passou a ser enviado em quadro próprio,
  onde a malha é ligada ou desligada (ver [PROTOCOL §3.1](PROTOCOL.md)).

## Alterações

### 1. Estado reativo de fluxo

- `FlowControlViewModel` passa a expor `IsFlowCommandPending`, `IsAwaitingAck`,
  `IsFlowmeterOnline`, textos de estado e a guarda única `CanSendFlowCommands`.
- Um envio local marca imediatamente a espera; a telemetria do Hub encerra a espera somente quando
  `FlowCommandPending` voltar a `false`.
- O estado solicitado permanece staged até a confirmação; perda do enlace não produz falso sucesso.

### 2. Controle e calibração

- `ControlViewModel` bloqueia `ApplyFlowState` e a parte de fluxo de `ApplyAll` durante pendência ou
  quando o fluxômetro estiver offline, atualizando o status do operador em cada transição.
- `FlowCalibrationViewModel` aplica a mesma guarda a preparar, ajustar e enviar a curva.
- A interface da linha/cartão de vazão mostra chips de “aguardando confirmação” e “desconectado”.

### 3. Wire format

- Comandos operacionais usam apenas as chaves reconhecidas pelo roteamento confiável v7:
  `flowSetpoint`, `maxFlow`, `valve_1`, `valve_2` e `v_Flow`.
- Curvas usam `maxFlow`, `k1`, `f1`, `c1`, `k2`, `f2` e `c2`.
- Os firmwares permanecem intocados.

### 4. Verificação

- Testes de transição `pending → confirmado` e `online → offline`.
- Testes de bloqueio em Controle e Calibração.
- Testes do JSON exato produzido por `FlowSetpoint` e `FlowSafeStop`.
- Build Release sem avisos e suíte completa de testes.
- Resultado automatizado em 2026-08-26: build com 0 avisos/0 erros e 510 testes aprovados,
  1 teste WPF hospedado ignorado e 0 falhas.
- Verificação manual com Hub/fluxômetro: enviar um comando, observar bloqueio durante
  `FlowCommandPending=true` e liberação após o ACK. Esta última etapa exige o hardware real.
