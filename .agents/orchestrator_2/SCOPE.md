# Scope: Peristaltic Pump (§1.10 & §1.11) Implementation Plan & Verification

## Architecture
- **Firmware Subsystem**: `External-Devices/bomba-peristaltica/firmware/peristaltic-pump/` (v3.10 active, C++, ESP32 Core 0 PWM, Core 1 Comm, NVS checkpointing, local safety interlocks).
- **Hub Gateway Subsystem**: `ESP32S3-HUB/ESP32S3-HUB/` (C++, whitelist `allowedPumpCommands`, mailbox latest-wins with high-entropy revision seed, `/pumpData` and `/readData` aggregation, Python contract tests).
- **App Frontend Subsystem**: `Android_app/` (Flutter/Dart UI, `PeristalticPumpState`, `DeviceControlProvider`, `controls_screen.dart`, unit tests).
- **Bench / Physical Hardware**: External peristaltic pump head, BTS7960 H-Bridge, DC motor, graduated cylinder, scale, liquid contact switch, potentiometer knobs.

## Feature Inventory & Audit Mapping (§1.10 and §1.11)

### Section 1.10: Limitações, Riscos e Decisões
| # | Feature / Item | Documentation Description | Subsystems | Current Status in Code | Action Plan / Justification |
|---|----------------|---------------------------|------------|------------------------|-----------------------------|
| 1 | Volume sensorless | Estimativa volumétrica por curva linear $Q = a \cdot S + b$ sem encoder de quadratura | FW, App | Implementado no FW (`PwmRuntime.h`, `SensorAndConversion.h`). Ausente na UI do App Flutter. | App: Criar módulo de calibração volumétrica assistida multiponto ($S=250,500,1000$). FW/Hub: Sem alterações. |
| 2 | Retenção de volume e `mode: 0` | Parada de perfil não deve zerar volume acumulado (`g_cumulativeVolumeMl`). `stop` não deve emitir `speed: 0`. Reset apenas via `reset_volume`. | FW, Hub, App | FW/Hub corretos (`startCycle()` preserva acumulado; `reset_volume` zera). App Flutter tem bug (`stopPump()` emite `speed: 0`) e falta `resetPumpVolume()`. | App: Corrigir `stopPump()` para enviar apenas `{"mode": 0}`; adicionar `resetPumpVolume()`. |
| 3 | Arbitragem de potenciômetros | Liberação dos potenciômetros físicos após comando remoto via chave `pot: 1` (`disablePot = false`). | FW, Hub, App | FW/Hub implementam `pot: 1`. App Flutter não possui método nem botão para alternar `PumpPotEnabled`. | App: Adicionar `setPumpPotentiometers(bool enable)` no provider e botão na UI. |
| 4 | Parada autônoma por timeout | Parada de segurança caso o rádio/USB caia durante dosagem manual (`speed_ms` com deadline). | FW, Hub, App | FW/Hub implementam `speed_ms` (`Lifecycle.h:138`). App Flutter não expõe `speed_ms` na UI. | App: Implementar dosagem manual temporizada com `pump_speed_ms`. |
| 5 | Bloqueio de `clear_nvs` no Hub | Hub bloqueia `clear_nvs` e `save_config` para prevenir perda remota acidental de calibração. | Hub, FW | 100% implementado em `Commands.h:596` com whitelist `allowedPumpCommands`. | Justificativa de Não-Implementação: Já 100% implementado e testado (81 testes de contrato). |
| 6 | Checkpoint NVS de 60 s | Salvamento de checkpoint em bateladas longas (`s_cvol`) para suportar quedas de energia. | FW | 100% implementado no FW (`RuntimeStateStore.h:41-56`). | Justificativa de Não-Implementação: Já implementado no FW v3.10. |
| 7 | Sensor de líquido no pino 15 | Intertravamento de hardware local mantido fora da telemetria remota por segurança do operador. | FW, HW | Implementado no FW (`SensorAndConversion.h`, `Lifecycle.h:174-202`). | Justificativa de Não-Implementação: Decisão de segurança fechada (D-SEC-02); não deve ser exposto ao Hub/App. |
| 8 | Telemetria de ganhos PID e saturação | Eco de $K_p, K_i, K_d$ no push a 1 Hz para evitar conflitos de digitação na UI. | FW, Hub, App | FW e Hub transmitem e agregam. App Flutter descarta no modelo `PeristalticPumpState`. | App: Incluir campos `pidKp`, `pidKi`, `pidKd`, `cycleVolume` no modelo e expansor de sintonia na UI. |
| 9 | Latência de polling e latest-wins | Polling de 2 s do nó e substituição atômica no Hub sem enfileiramento infinito. | Hub, FW | 100% implementado no Hub (`Mailboxes.h`) e FW (`Lifecycle.h`). | Justificativa de Não-Implementação: Comportamento arquitetural correto e validado. |
| 10 | Envio de `start` com `mode=0` | Envio de `start` com `mode=0` coloca o nó em `OP_RUNNING` com vazão nula. | FW, App | Inócuo; App usa apenas `mode: 1..5` e `stop`. | Justificativa de Não-Implementação: Inócuo; App não gera `mode=0` com comando `start`. |
| 11 | Motor DC com ponte H vs stepper | Motor DC escovado em ponte BTS7960, PWM com zona morta 155..1023 (unidade interna $S$). | FW, App | App rotula indevidamente a unidade como "RPM". | App: Corrigir rótulo de unidade para passos de velocidade $S \in [-1000, 1000]$ ou vazão em mL/min. |
| 12 | Potenciômetro "gain" bipolar | Potenciômetro "gain" atua como escala bipolar de velocidade (-1000..1000 centrado em 2047). | FW, HW | Implementado no FW (`OperationController.h:184-204`). | Justificativa de Não-Implementação: Comportamento de hardware documentado e operante no FW. |

### Section 1.11: Checklist de Bancada Física
| # | Checklist Item | Description | Subsystems | Status & Nature | Technical Resolution / Protocol |
|---|----------------|-------------|------------|-----------------|---------------------------------|
| 1 | Sentido de rotação | $S > 0$ impulsiona fluido para dentro do vaso do reator | HW, FW | Procedimento de bancada | Teste físico com água. Se invertido, inverter fios OUT1/OUT2 no borne do BTS7960. |
| 2 | Detecção de presença / offline | Telemetria no Hub em $\le 4\text{ s}$ e detecção de nó offline em $4\text{ s}$ | FW, Hub, App | Procedimento de bancada | FW envia a 1 Hz; Hub monitora heartbeat de 4 s. Validado em contrato; ensaio físico de desconexão. |
| 3 | Calibração volumétrica | Ensaio em 3 pontos ($S=250, 500, 1000$ por 60 s) com proveta, $R^2 \ge 0,99$ | HW, App, FW | Procedimento de bancada + App | Implementar tela de calibração volumétrica no Flutter (`Android_app`). Executar ensaio físico na bancada. |
| 4 | Liberação de potenciômetros | Envio de `pot: 1` devolve controle aos knobs analógicos | FW, Hub, App, HW | Procedimento de bancada + App | Adicionar botão no App Flutter. Girar knobs fisicamente e aferir PWM no osciloscópio/multímetro. |
| 5 | Parada autônoma por timeout | Desconexão do rádio/USB faz o motor parar após tempo `speed_ms` | FW, HW | Procedimento de bancada | Enviar `pump_speed=500` com `pump_speed_ms=5000`, cortar rádio/cabo e cronometrar parada em 5 s. |
| 6 | Retenção de volume acumulado | Parada de perfil de 5 min mantém volume acumulado de sessão | FW, Hub, App | Procedimento de bancada + App | Corrigir `stopPump()` no Flutter para não enviar `speed: 0`. Validar conservação de `PumpVol`. |
| 7 | Acumulação em perfis consecutivos | Execução de dois perfis consecutivos acumula volume total e reseta volume de ciclo | FW, Hub, App | Procedimento de bancada + App | Rodar perfil 1, parar, rodar perfil 2. Conferir `PumpVol` somado e `PumpCycleVol` reiniciado. |
| 8 | Persistência de ganhos PID | Ajustar PID, reiniciar nó e verificar se ganhos foram mantidos na NVS | FW, Hub, App | Procedimento de bancada | Enviar `pumpPidKp/Ki/Kd`, desligar alimentação do nó, ligar e conferir telemetria de 1 Hz. |
| 9 | Resiliência a corte de energia | Cortar alimentação durante perfil longo (>1 min) e religar; nó retoma operação | FW, HW | Procedimento de bancada | Iniciar perfil contínuo, esperar 65 s (checkpoint NVS gravado), desenergizar, energizar e verificar retorno a `OP_RUNNING`. |
| 10 | Semeadura de `cmd_id` pós-reboot | Reiniciar Hub e enviar comando imediato à bomba; comando não deve ser ignorado | Hub, FW | Procedimento de bancada | Validado no Hub (`seedReliableMailboxes()`). Ensaio físico: reiniciar Hub ESP32-S3 e enviar `start`. |
| 11 | Intertravamento do sensor de líquido | Sensor no pino 15 com debounce de 50 ms para motor em <500 ms | FW, HW | Procedimento de bancada | Conectar chave/contato no pino 15, acionar motor, abrir contato com água e medir corte em osciloscópio. |

## Milestones
| # | Name | Scope | Dependencies | Status |
|---|------|-------|-------------|--------|
| M1 | Survey & Codebase Analysis | §1.10 and §1.11 cross-reference with FW, Hub, App | None | DONE |
| M2 | Plan & Verification Generation | Worker writes `IMPLEMENTATION_PLAN_BOMBA.md` and `verify_plan_bomba.py`, executes verification | M1 | DONE |
| M3 | Quality Gate & Forensic Audit | Reviewers, Challenger, and Auditor verify technical correctness and complete coverage | M2 | DONE |
| M4 | Final Handoff & Completion | Orchestrator synthesizes results and reports to parent | M3 | DONE |
