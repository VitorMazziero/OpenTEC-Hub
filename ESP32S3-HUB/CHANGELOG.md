# Changelog do Hub

## 10.7.0-dev — PI do banho só no ajuste fino

- **Portão do PI:** a cascata só atua perto da referência com o reator assentado. Entra com
  |erro| < 5 °C **e** |dPV/dt| < 0,1 °C/min (PV filtrado, janela de 120 s); sai só com
  |erro| > 6 °C. Fora disso fica no novo estado `approaching`: banho em `ref + bias + I`, um
  comando direto, sem slew e sem integrar.
- **Partida:** o integrador não é mais semeado com o erro da partida (com reator a 20 °C e
  referência 37 °C começava em −14 °C) e o primeiro comando sai logo, em vez de só ser marcado
  como enviado e depender do reenvio C4 após 30 s. Em simulação (banho τ = 5 min, reator
  τ = 15 min, 22 → 37 °C, 6 h): 50 → 19 comandos ao C404, sem os ~40 degraus de 0,2 °C da subida.
- O reenvio C4 (alvo divergente) também vale em `approaching`.
- Telemetria (aditiva): `BathCascadeFine`, `BathCascadeSlopeCMin` e os limiares
  `BathCascadeFineEnterBandC`, `BathCascadeFineExitBandC`, `BathCascadeFineSlopeCMin`,
  `BathCascadeSlopeWindowMs`. Os limiares são padrões do firmware (sem comando nem NVS ainda).
- Testes: `host-bath-cascade` com partida longe, entrada por banda e derivada, histerese,
  integrador preservado e degrau de referência.

## 10.6.0-dev — correções da integração do banho (plano K00–K05)

- **Caminho de comando do banho:** `bathBox` substituída por `BathCommandCoordinator`
  (`src/control`), com três posições e prioridade `stop` > `mode/sync` > `setpoint`. A conclusão
  fica vinculada ao `cmd_id` do setpoint: um ACK de modo/sync durante a sequência não gera mais
  timeout de 300 s (C1).
- **Parada do banho (D-2/D-4):** `bathAbort`, `tempSetpoint=0` na via externa e `resetVariables`
  entregam `{"stop":1}` (abort + modo manual), desligam a cascata e liberam a posse. A via e
  `bathComm` não mudam; `resetVariables` deixou de trocar a via para UART e de apagar a sintonia.
  Novo `tempSetpoint` religa a cascata e pede `mode=auto` ao nó uma vez por ativação.
- **Falhas por borda (C2):** erro/abort/guarda suspensa só falham quando o nó *entra* nesses
  estados; `bathCascadeReset` limpa a conclusão pendente, esquece o estado já visto e rearma a
  guarda. Motivo publicado em `BathCascadeFaultReason`.
- **Recusa do nó (C3):** `rej_cmd_id/rej_err` do nó r3.2 geram falha imediata
  `node_rejected:<motivo>` (exceto `busy`, reentregue); a saída é limitada a `sp_min/sp_max`.
- **Posse (D-1):** resposta do `/bathData` leva `X-Hub-Owner: 1` enquanto a cascata está ativa;
  alvo divergente no nó é reenviado até 3 vezes antes de `target_override`.
- **PI:** retomada após pausa e ressintonia sem degrau, filtro reiniciado após espera, `dt`
  limitado a 3 períodos, motivo de espera específico (`node_offline`, `bath_manual`…).
- `tempSetpoint` fora de 0–100 °C é recusado (antes: saturado).
- `/bathData` exige nó `r3.2`+ (`bathNodeVersionSupported`) e os campos `rej_cmd_id`, `rej_err`,
  `sp_min`, `sp_max`.
- Telemetria: `BathOwned`, `BathCascadeActive`, `BathCascadeFaultReason`, `BathStopPending`,
  `BathCommandCompletion`, `BathOperationError`, `BathNodeReject*`, `BathNodeSpMin/Max`,
  `TempSetpointCommanded`, `TempModuleActuatorOn`, sintonia vigente `BathCascade*` e
  `BathCascadeConfigError`; campos do nó ficam `null`/vazios com o nó offline. Reserva JSON
  3584 → 4608 bytes.
- Testes: novo `tests/host-bath-orchestration`; `host-bath-cascade` ampliado; contratos 125.
- Compilação esp32s3: 1 159 072 B de flash (88%), 52 216 B de RAM global (15%).

## 10.5.1-dev — auditoria adicional banho/Hub

- falhas do nó e timeout de execução de 300 s ficam travados até reset explícito;
- conclusão usa o par `ack_cmd_id`/`done` uma única vez, sem renovar cooldown em pushes repetidos;
- `/bathData` exige nó r3.1 previamente registrado no mesmo IP e enums conhecidos;
- novo comando de mesmo valor rearma a UART após troca de via e `resetVariables` envia `100B`;
- telemetria expõe IDs, pendência e idade da conclusão; reserva JSON elevada para 3584 bytes;
- aritmética de idade de `/nodes` permanece correta no rollover de `millis()`.

## 10.5.0-dev — cascata térmica externa

- adiciona `bath`/r3, `bathBox`, `/bathData` e `/bathCommand`;
- torna `Tempval` uma amostra real, válida e temporal do reator;
- adiciona PI puro, limites, slew, anti-windup, troca break-before-make e cooldown após `done`;
- publica diagnóstico completo da cascata com `null` para valores inválidos;
- persiste somente configuração/rota/comunicação e nunca retoma atuação após reboot;
- mantém a versão do protocolo HTTP em 10 por compatibilidade aditiva.

Compilação e contratos não equivalem à aprovação de bancada, sintonia ou liberação para cultivo.
