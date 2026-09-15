# Handoff Report: Explorer Hub & App

**Data**: 2026-09-13  
**Agente**: `explorer_hubapp_1`  
**Destinatário**: `parent` (`a109a27d-47d4-4806-8071-687fa2f0b968`)  
**Artefato Principal**: `d:\OneDrive\PosDoc_Fapesp\Automacao_e_Controle\ProjetoTECNAL\.agents\explorer_hubapp_1\hub_app_analysis.md`

---

## 1. Observation

1. **OpenTEC-Hub Firmware (C++):**
   - No arquivo `ESP32S3-HUB/ESP32S3-HUB/src/protocol/Commands.h:588–596`, o Hub possui a lista de chaves simples `simpleKeys` (`pump_command`, `mode`, `pump_speed`, `pump_speed_ms`, `pump_pot`, `init_t`, `final_t`, `pumpSlope`, `pumpIntercept`, `pumpPidKp`, `pumpPidKi`, `pumpPidKd`) e restringe comandos na whitelist `allowedPumpCommands[] = { "reset_volume", "start", "stop" }` (linhas 596–613).
   - No arquivo `ESP32S3-HUB/ESP32S3-HUB/src/network/HttpServer.h:352–393`, o endpoint `GET /pumpData` recebe e desserializa `mode`, `flow`, `vol`, `v_tgt`, `pwm`, `speed`, `slope`, `intercept`, `kp`, `ki`, `kd`, `pot`, `cyc_vol`, `active`, `waiting`.
   - No arquivo `ESP32S3-HUB/ESP32S3-HUB/src/sensor/Telemetry.h:325–347`, o Hub serializa no JSON agregado de `/readData` as chaves `PumpOnline`, `PumpCommEnabled`, `PumpCommandPending`, `PumpMode`, `PumpPWM`, `PumpSpeed`, `PumpFlow`, `PumpVol`, `PumpTargetVol`, `PumpActive`, `PumpWaiting`, `PumpSlope`, `PumpIntercept`, `PumpPidKp`, `PumpPidKi`, `PumpPidKd`, `PumpPotEnabled`, `PumpCycleVol`.
   - No arquivo `ESP32S3-HUB/ESP32S3-HUB/src/protocol/Mailboxes.h:6–18`, `seedReliableMailboxes()` inicializa `pumpBox.revision` com uma base aleatória elevada para prevenir colisões de `cmd_id` com o nó após reboot do Hub.

2. **Suíte de Testes de Contrato do Hub (Python):**
   - Execução de `python -m unittest discover ESP32S3-HUB/tests/contracts`: 81 testes executados e aprovados com código de saída 0.
   - Execução de `python -m unittest ESP32S3-HUB/tests/contracts/test_node_commands.py`: 30 testes aprovados, cobrindo `test_pump_command_whitelist_blocks_clear_nvs_and_config_verbs`, `test_pump_speed_ms_and_pot_are_forwarded_without_prefix`, `test_hub_echoes_pump_pid_pot_and_cycle_volume`, `test_speed_alone_without_pump_prefix_is_rejected`.

3. **Aplicativo Flutter (`Android_app`):**
   - Em `Android_app/lib/providers/device_control_provider.dart:305–310`:
     ```dart
     Future<bool> stopPump() async {
       return sendRawCommand({
         "mode": 0,
         "speed": 0, // Inconsistência com protocolo §1.10 item 2
       });
     }
     ```
   - Em `Android_app/lib/models/peristaltic_pump_state.dart:36–112`:
     `PeristalticPumpState` não possui campos nem desserialização para: `PumpCycleVol`, `PumpPotEnabled`, `PumpSlope`, `PumpIntercept`, `PumpPidKp`, `PumpPidKi`, `PumpPidKd`. Além disso, a linha 30 descreve `PumpSpeed` como "RPM", quando a unidade real são passos internos de velocidade $S \in [-1000, 1000]$.
   - Em `Android_app/lib/providers/device_control_provider.dart`:
     Não existem métodos para: `resetPumpVolume()` (`{"pump_command": "reset_volume"}`), `setPumpPotentiometers()` (`{"pump_pot": 1|0}`), `setPumpManualSpeed()` (`{"pump_speed": S, "pump_speed_ms": ms}`), `applyPumpCalibration()` (`{"pumpSlope": a, "pumpIntercept": b}`), `setPumpPid()` (`{"pumpPidKp": a, ...}`).
   - Em `Android_app/lib/screens/controls_screen.dart:1490–1910`:
     Não existem botões ou seções para: Zerar volume acumulado, habilitar/desabilitar potenciômetros físicos (`PumpPotEnabled`), sintonia de PID do nó, nem tela de calibração volumétrica. O botão "Apply Feed Profile" (linha 1504) não verifica `pumpState.isCommandPending` antes do disparo.
   - Execução de `flutter test test/peristaltic_pump_test.dart`: 20 testes executados e aprovados, incluindo o teste na linha 289 que valida incorretamente que `stopPump` emite `{"mode": 0, "speed": 0}`.

4. **Referência Windows App (`Windows_app`):**
   - Em `Windows_app/src/OpenTECHub.Protocol/CommandBuilders.cs:410–414`, `PumpStopProfile()` emite exclusivamente `{"mode": 0}`, omitindo propositalmente a chave `speed`.
   - Em `Windows_app/src/OpenTECHub/ViewModels/PumpControlViewModel.cs:379–405`, `TogglePotentiometers()` gerencia `PumpPotEnabled` e `CommandBuilders.PumpPotentiometers()`.
   - Em `PumpControlViewModel.cs:595–615`, `ResetVolume()` emite `PumpResetVolume()` e realiza confirmação não-otimista aguardando `PumpVolume < 0.05`.
   - Em `Windows_app/src/OpenTECHub/ViewModels/PumpCalibrationViewModel.cs:1–140`, implementa calibração volumétrica assistida multiponto ($S = 250, 500, 1000 \times 60\text{ s}$) com `pump_speed_ms = \Delta t + 3\text{ s}$, regressão linear $R^2$ e resíduos.

---

## 2. Logic Chain

1. A Seção 1.10 de `COMANDOS_DISPOSITIVOS_EXTERNOS.md` estabelece as decisões de projeto para o firmware v3.10 da bomba peristáltica, destacando a não-reinicialização do volume ao parar perfil (Item 2), liberação dos knobs via `pot:1/0` (Item 3), parada por tempo via `speed_ms` (Item 4), filtragem de `clear_nvs` no Hub (Item 5), eco e sintonia de PID (Item 8) e serialização por `PumpCommandPending` (Item 9).
2. As observações no código do Hub (`ESP32S3-HUB`) comprovam que o firmware do Hub já implementou 100% dessas decisões:
   - Filtro em `Commands.h:596` rejeita verbos que não sejam `reset_volume`, `start`, `stop`.
   - Encaminhamento e tradução de chaves em `Commands.h:617–620` cobrem `pump_pot`, `pump_speed_ms`, `pumpPidK*`.
   - Agregação em `Telemetry.h:325–347` publica todas as métricas v3.10 no JSON de `/readData`.
   - Os 81 testes de contrato em Python confirmam estaticamente a integridade dessa camada.
3. No entanto, o código do aplicativo Flutter (`Android_app`) diverge das especificações:
   - `DeviceControlProvider.stopPump()` emite `{"mode": 0, "speed": 0}`, contrariando a regra expressa no Item 1.10 #2 (que proíbe emissão de `speed` para preservar o volume e não acionar o modo USB pegajoso em nós legados).
   - O operador não dispõe de comando de zeramento de volume (`reset_volume`), sendo incapaz de cumprir o procedimento do Item 1.10 #2 e Checklist 1.11 #6.
   - O operador não dispõe de controle de potenciômetros (`pump_pot`), contrariando o Item 1.10 #3 e Checklist 1.11 #4.
   - A telemetria de PID e volume de ciclo (`PumpCycleVol`, `PumpPidKp/Ki/Kd`) é descartada no parser Dart (`PeristalticPumpState.fromJson`), contrariando o Item 1.10 #8.
   - Não há módulo de calibração volumétrica multiponto no Flutter, contrariando o Item 1.10 #1 e Checklist 1.11 #3.
4. Logo, para atingir conformidade plena de ponta a ponta com a documentação oficial, é mandatório corrigir o aplicativo Flutter (`Android_app`), ajustando o modelo de estado, os métodos do provider, as telas de controle e seus respectivos testes unitários.

---

## 3. Caveats

- **Testes de Bancada Física (§1.11):** Os itens do checklist 1.11 (#1 sentido físico, #3 pesagem de líquido, #5 corte de cabo, #9 corte de energia, #11 teste de molhamento do sensor) dependem de bancada instrumentalizada real (proveta, líquido, motor físico, cronômetro) e não podem ser finalizados em ambiente puramente virtual/documental.
- **Porta do Sensor de Líquido (§1.10 #7 e §1.11 #11):** Permanece intencionalmente restrita à bancada física e à porta 80 local do nó por diretriz de segurança de engenharia fechada, não devendo ser repassada pelo Hub.
- **Gás Proporcional:** A automação de gás proporcional ao volume dosado ($Q_g = f(V)$) existe no `Windows_app`, mas não foi portada para o `Android_app`. Trata-se de uma funcionalidade de coordenação de processo entre dois atuadores (bomba e fluxômetro).

---

## 4. Conclusion

1. O **OpenTEC-Hub (C++ no ESP32-S3 e testes Python)** está **aprovado e 100% pronto**, sem necessidade de modificação em código de produção.
2. O **Aplicativo Flutter (`Android_app`)** requer 4 conjuntos de correções pontuais:
   - **Correção de Protocolo:** Remover `"speed": 0` de `stopPump()` e atualizar `test/peristaltic_pump_test.dart`.
   - **Comandos Faltantes:** Adicionar `resetPumpVolume()`, `setPumpPotentiometers()`, `setPumpManualSpeed()`, `applyPumpCalibration()` e `setPumpPid()` em `DeviceControlProvider.dart`.
   - **Telemetria e Modelo:** Expandir `PeristalticPumpState` com `cycleVolume`, `potEnabled`, `slope`, `intercept`, `pidKp`, `pidKi`, `pidKd`, e corrigir unidade de velocidade de "RPM" para "S".
   - **Interface com Usuário:** Inserir em `controls_screen.dart` os botões para zeramento de volume, comutação de potenciômetros e expansor de PID, além da guarda de `isCommandPending` no botão de envio de perfil.
3. O relatório completo com todas as justificativas e códigos detalhados encontra-se em `hub_app_analysis.md`.

---

## 5. Verification Method

Para verificar independentemente todas as afirmações deste relatório:

1. **Executar Testes de Contrato Python do Hub:**
   ```powershell
   python -m unittest discover ESP32S3-HUB/tests/contracts
   python -m unittest ESP32S3-HUB/tests/contracts/test_node_commands.py
   ```
   *Resultado esperado:* 81 testes passam sem erro.

2. **Executar Testes Unitários do Flutter:**
   ```powershell
   cd Android_app
   flutter test test/peristaltic_pump_test.dart
   ```
   *Resultado esperado:* 20 testes passam; inspecionar as linhas 284 e 292 para verificar o desvio do envio de `{"mode": 0, "speed": 0}`.

3. **Inspecionar Arquivos de Origem:**
   - `ESP32S3-HUB/ESP32S3-HUB/src/protocol/Commands.h:588–635` (whitelist e pass-through da bomba).
   - `ESP32S3-HUB/ESP32S3-HUB/src/sensor/Telemetry.h:325–347` (serialização da telemetria da bomba).
   - `Android_app/lib/models/peristaltic_pump_state.dart:36–112` (ausência de `PumpCycleVol`, `PumpPotEnabled`, etc.).
   - `Android_app/lib/providers/device_control_provider.dart:305–310` (`stopPump`).
   - `Android_app/lib/screens/controls_screen.dart:1490–1910` (seção de controle da bomba).
