# Relatório de Análise Técnica: OpenTEC-Hub e Aplicativo (Flutter/UI)
## Integração do Protocolo da Bomba Peristáltica Externa (Firmware v3.10)
### Auditoria de Conformidade com `COMANDOS_DISPOSITIVOS_EXTERNOS.md` (§1.10 e §1.11)

**Data de Auditoria:** 2026-09-13  
**Agente Investigador:** Explorer Hub & App (`explorer_hubapp_1`)  
**Escopo:** OpenTEC-Hub (Firmware C++ do Hub ESP32-S3, Suíte de Contratos Python), Aplicativo Flutter Multiplataforma (`Android_app`), Aplicativo Dedicado Flutter da Bomba (`External-Devices/bomba-peristaltica/apps/flutter`) e Referência Desktop C# (`Windows_app`).

---

## 1. Sumário Executivo e Panorama Arquitetural

### 1.1 Topologia de Comunicação e Responsabilidades

A integração da Bomba Peristáltica Externa envolve quatro camadas principais:
1. **Nó Atuador (ESP32 Nó Peristáltico v3.10):**
   - Roda firmware em C++ (`External-Devices/bomba-peristaltica/firmware/peristaltic-pump/`).
   - Controla motor DC escovado via ponte H (LEDC 7,5 kHz, 10 bits, pinos GPIO 14/27/25/26), integrando vazão e volume a cada 2 ms no Core 0.
   - Faz *push* periódico HTTP `GET /pumpData?...` a 1 Hz e *poll* HTTP `GET /pumpCommand` a 2 s em direção ao Hub.
2. **OpenTEC-Hub (Firmware C++ ESP32-S3 e Testes de Contrato Python):**
   - Firmware em C++ no diretório `ESP32S3-HUB/ESP32S3-HUB/src/`.
   - Gerencia filas e caixas de correio confiáveis (`Mailboxes.h`, `Commands.h`, `HttpServer.h`, `Telemetry.h`).
   - Fornece endpoints REST HTTP (`/command`, `/readData`, `/pumpData`, `/pumpCommand`, `/nodeDiag`, `/nodeHello`).
   - Suíte de validação e garantia de contrato em Python (`ESP32S3-HUB/tests/contracts/`).
3. **Aplicativo Flutter Multiplataforma (`Android_app`):**
   - Implementado em Flutter/Dart no diretório `Android_app/lib/`.
   - Controla e monitora os múltiplos subsistemas do bioprocesso (Servos, Biomassa, Distância, Agitador, Fluxômetro e Bomba Peristáltica).
   - Comunica-se com o Hub via HTTP REST (`HubApiService.dart`), consumindo telemetria por polling com ETag (`/readData`) e despachando comandos JSON para `/command`.
4. **Implementação de Referência e Aplicativos Auxiliares:**
   - **Windows App C# WPF (`Windows_app`):** Implementação de referência mencionada em `COMANDOS_DISPOSITIVOS_EXTERNOS.md` que alcançou 100% de integração de ponta a ponta com o firmware 3.10.
   - **Pump Flutter App (`External-Devices/bomba-peristaltica/apps/flutter`):** Aplicativo Flutter autônomo projetado para conexão direta via SoftAP local (`192.168.6.1`) do nó da bomba, contendo telas de calibração volumétrica e sintonia.

---

### 1.2 Matriz Geral de Prontidão da Bomba Peristáltica (Firmware v3.10)

| Funcionalidade / Requisito | Firmware Nó (v3.10) | Hub 10.2 (C++) | Hub Tests (Python) | App Flutter (`Android_app`) | App Windows (C# Ref) |
|---|:---:|:---:|:---:|:---:|:---:|
| **Roteamento e Presença (`pumpComm`, `PumpOnline`)** | 🟢 Total | 🟢 Total | 🟢 Total | 🟢 Total | 🟢 Total |
| **Parada de Perfil sem Zerar Volume (`mode:0`)** | 🟢 Total | 🟢 Total | 🟢 Total | 🔴 Desvio (Envia `speed:0`) | 🟢 Total |
| **Zerar Volume Acumulado (`reset_volume`)** | 🟢 Total | 🟢 Total | 🟢 Total | 🔴 Não Implementado | 🟢 Total |
| **Controle de Potenciômetros (`pump_pot`, `PumpPotEnabled`)** | 🟢 Total | 🟢 Total | 🟢 Total | 🔴 Ausente na UI/Model | 🟢 Total |
| **Parada Autônoma por Prazo (`pump_speed_ms`)** | 🟢 Total | 🟢 Total | 🟢 Total | 🔴 Não Implementado | 🟢 Total |
| **Sintonia e Eco de PID (`pumpPidKp/Ki/Kd`)** | 🟢 Total | 🟢 Total | 🟢 Total | 🔴 Ausente na UI/Model | 🟢 Total |
| **Telemetria de Volume de Ciclo (`PumpCycleVol`)** | 🟢 Total | 🟢 Total | 🟢 Total | 🔴 Ausente no Model | 🟢 Total |
| **Calibração Volumétrica Assistida (Multiponto $S=250..1000$)** | 🟢 Total | 🟢 Total | 🟢 Total | 🔴 Não Implementado | 🟢 Total |
| **Calibração por Coeficientes (`pumpSlope`, `pumpIntercept`)** | 🟢 Total | 🟢 Total | 🟢 Total | 🔴 Não Implementado | 🟢 Total |
| **Filtro de Segurança NVS (`clear_nvs`, `save_config`)** | 🟢 Total | 🟢 Total | 🟢 Total | 🟢 Seguro por Omissão | 🟢 Total |
| **Gás Proporcional ao Volume Doador ($Q_g = f(V)$)** | N/A | N/A | N/A | 🔴 Não Implementado | 🟢 Total |

---

## 2. Auditoria Detalhada dos Itens da Seção 1.10 (Limitações, Riscos e Decisões)

Abaixo é apresentada a auditoria minuciosa de cada um dos itens de 1 a 11 e complementares descritos na Seção 1.10 de `COMANDOS_DISPOSITIVOS_EXTERNOS.md`.

---

### Item 1.10 #1: Volume e vazão são estimados pela curva de calibração

- **Achado Original:** No firmware v3.9, o volume e a vazão eram calculados puramente pela conversão empírica do duty cycle aplicado através da reta $Q = \text{slope} \cdot S + \text{intercept}$, sem haver medidor de vazão real no cabeçote.
- **Decisão Homologada:** Manter a estimativa matemática no firmware e implementar no aplicativo a calibração volumétrica rigorosa com recipiente graduado para fechar a malha física.
- **Status no OpenTEC-Hub:**
  - **Arquivo:** `ESP32S3-HUB/ESP32S3-HUB/src/sensor/Telemetry.h`, linhas 332–333.
  - **Comportamento:** O Hub recebe `flow` e `vol` do nó e os publica em `/readData` como `PumpFlow` e `PumpVol`. O Hub é neutro quanto à física do sensor.
- **Status no Aplicativo Flutter (`Android_app`):**
  - **Arquivo:** `Android_app/lib/models/peristaltic_pump_state.dart`, linhas 43–44 e 151–158; `Android_app/lib/widgets/peristaltic_pump_card.dart`, linhas 128–133, 213–217.
  - **Lacuna / Inconsistência:** O aplicativo exibe `flow` e `volume` como se fossem medidas absolutas e não disponibiliza tela de calibração volumétrica. O operador no Flutter não é avisado de que estes valores dependem estritamente da calibração gravada.
- **Mudança Técnica Necessária:**
  1. No card `PeristalticPumpCard` (`peristaltic_pump_card.dart`), adicionar indicação textual ou tooltip esclarecendo que a vazão e o volume são valores estimados baseados na curva de calibração ativa.
  2. Implementar fluxo de calibração no Flutter (ver Item 1.10 #4 e §1.11 Item 3).

---

### Item 1.10 #2: `mode:0` zerava o volume acumulado

- **Achado Original:** No firmware v3.9, enviar `mode: 0` disparava o zeramento de `g_cumulativeVolumeMl`. O operador perdia o totalizador da sessão toda vez que pausava ou parava um perfil.
- **Decisão Homologada:** Parar o perfil sem zerar o volume. O acumulador de volume só deve ser zerado mediante comando explícito `reset_volume`. O firmware v3.10 implementou `startCycle()`, isolando `g_cumulativeVolumeMl` (sessão) e calculando `cyc_vol = vol - g_cycleStartVolumeMl` (ciclo).
- **Status no OpenTEC-Hub:**
  - **Arquivo:** `ESP32S3-HUB/ESP32S3-HUB/src/protocol/Commands.h`, linhas 589 e 596; `ESP32S3-HUB/ESP32S3-HUB/src/sensor/Telemetry.h`, linha 345.
  - **Comportamento:** O Hub aceita `mode: 0` em `simpleKeys` e repassa ao nó sem alterar o acumulador. Aceita `pump_command: "reset_volume"` na whitelist `allowedPumpCommands`. Coleta e expõe `PumpCycleVol` no payload de `/readData`.
- **Status no Aplicativo Flutter (`Android_app`):**
  - **Arquivos:**
    - `Android_app/lib/providers/device_control_provider.dart`, linhas 296–310 (`stopPump()` e `setPumpComm()`).
    - `Android_app/lib/models/peristaltic_pump_state.dart`, linhas 36–112.
    - `Android_app/test/peristaltic_pump_test.dart`, linhas 284 e 290–295.
  - **Desvio Crítico Encontrado:**
    1. Em `DeviceControlProvider.dart:306-310`, `stopPump()` envia:
       ```dart
       Future<bool> stopPump() async {
         return sendRawCommand({
           "mode": 0,
           "speed": 0, // BUG DE PROTOCOLO
         });
       }
       ```
       No contrato de fio (`COMANDOS_DISPOSITIVOS_EXTERNOS.md` §1.0 linha 34 e `CommandBuilders.cs` linha 410), a parada de perfil deve conter **estritamente `{"mode": 0}`**. A inclusão de `"speed": 0` viola a diretriz de parada limpa. Além disso, `"speed"` sem o prefixo `"pump_"` é silenciosamente descartado pelo Hub (`Commands.h:589`). Caso fosse repassado a um nó v3.9, acionaria o modo manual USB com velocidade zero, travando os potenciômetros físicos.
    2. O modelo `PeristalticPumpState` **não mapeia nem consome a chave `PumpCycleVol`**.
    3. `DeviceControlProvider` e `ControlsScreen` **não possuem o comando `reset_volume`**. O operador não consegue zerar o volume acumulado pela interface do Flutter!
- **Mudança Técnica Necessária:**
  1. Em `Android_app/lib/providers/device_control_provider.dart`:
     - Corrigir `stopPump()` para enviar apenas `{"mode": 0}`:
       ```dart
       Future<bool> stopPump() async {
         return sendRawCommand({"mode": 0});
       }
       ```
     - Implementar o método `resetPumpVolume()`:
       ```dart
       Future<bool> resetPumpVolume() async {
         return sendRawCommand({"pump_command": "reset_volume"});
       }
       ```
  2. Em `Android_app/lib/models/peristaltic_pump_state.dart`:
     - Adicionar o campo `final double cycleVolume;` e mapear `json['PumpCycleVol']`.
  3. Em `Android_app/lib/screens/controls_screen.dart`:
     - Adicionar botão de ação "Zerar Volume Acumulado" com diálogo de confirmação, que aguarda `volume < 0.05 mL`.
  4. Em `Android_app/test/peristaltic_pump_test.dart`:
     - Atualizar o teste unitário linha 290 para validar que `stopPump()` emite apenas `{"mode": 0}`.

---

### Item 1.10 #3: `speed` pegajoso: knobs físicos mortos até reboot

- **Achado Original:** No firmware v3.9, uma vez recebido qualquer comando `"speed"`, o flag `hasUsbSpeed` tornava-se permanente. Os potenciômetros de bancada ficavam inoperantes até o ESP32 ser reiniciado fisicamente.
- **Decisão Homologada:** Criar o comando `"pot": 1` (para devolver o controle aos potenciômetros físicos) e `"pot": 0` (para travá-los via software), e ecoar o estado no parâmetro `pot` da telemetria (`PumpPotEnabled`).
- **Status no OpenTEC-Hub:**
  - **Arquivos:**
    - `ESP32S3-HUB/ESP32S3-HUB/src/protocol/Commands.h`, linhas 589 e 620: traduz `pump_pot` para `pot`.
    - `ESP32S3-HUB/ESP32S3-HUB/src/network/HttpServer.h`, linha 379: lê parâmetro `pot` do push HTTP.
    - `ESP32S3-HUB/ESP32S3-HUB/src/sensor/Telemetry.h`, linha 344: serializa `"PumpPotEnabled": true/false`.
  - **Suíte de Testes Python:**
    - `ESP32S3-HUB/tests/contracts/test_node_commands.py`, linhas 428–432 (`test_pump_speed_ms_and_pot_are_forwarded_without_prefix`) e 434–440 (`test_hub_echoes_pump_pid_pot_and_cycle_volume`).
- **Status no Aplicativo Flutter (`Android_app`):**
  - **Arquivos:** `Android_app/lib/models/peristaltic_pump_state.dart`, `Android_app/lib/providers/device_control_provider.dart`, `Android_app/lib/screens/controls_screen.dart`.
  - **Lacuna Crítica Encontrada:**
    - `PeristalticPumpState` **não possui o campo `potEnabled`** e ignora `PumpPotEnabled`.
    - `DeviceControlProvider` **não implementa `setPumpPotentiometers(bool enabled)`** (envio de `{"pump_pot": 1|0}`).
    - A interface em `controls_screen.dart` não possui botão ou indicador para devolver o controle aos botões físicos da bomba. O operador em bancada fica impossibilitado de destravar os knobs pelo app Flutter!
- **Mudança Técnica Necessária:**
  1. Em `Android_app/lib/models/peristaltic_pump_state.dart`:
     - Adicionar `final bool potEnabled;` (default `false`), populando com `json['PumpPotEnabled'] == true`.
  2. Em `Android_app/lib/providers/device_control_provider.dart`:
     - Implementar:
       ```dart
       Future<bool> setPumpPotentiometers(bool enabled) async {
         return sendRawCommand({"pump_pot": enabled ? 1 : 0});
       }
       ```
  3. Em `Android_app/lib/screens/controls_screen.dart`:
     - Adicionar botão alternador "Potenciômetros de Bancada" que reflete o estado de `pumpState.potEnabled` e despacha `setPumpPotentiometers(!pumpState.potEnabled)`.

---

### Item 1.10 #4: `speed` sem temporizador (risco de transbordamento)

- **Achado Original:** No firmware v3.9, enviar uma velocidade de teste manual mantinha o motor acionado indefinidamente. Se a comunicação caísse durante um teste de calibração, o motor continuava dosando até transbordar o vaso.
- **Decisão Homologada:** Adicionar a chave opcional `speed_ms` no nó v3.10. Ao vencer o prazo, o laço de controle zera `usbSpeedSteps`. O app deve enviar a duração prevista mais uma margem de segurança de 3 segundos ($\Delta t + 3\text{ s}$).
- **Status no OpenTEC-Hub:**
  - **Arquivo:** `ESP32S3-HUB/ESP32S3-HUB/src/protocol/Commands.h`, linhas 589 e 620.
  - **Comportamento:** O Hub aceita `pump_speed` e `pump_speed_ms`, remove o prefixo e encaminha `{"speed": S, "speed_ms": ms}` para a `pumpBox`. Testado em `test_node_commands.py:425`.
- **Status no Aplicativo Flutter (`Android_app`):**
  - **Arquivos:** `Android_app/lib/providers/device_control_provider.dart`.
  - **Lacuna Encontrada:** O aplicativo Flutter geral não possui comando para velocidade manual ou calibração assistida (`pump_speed` e `pump_speed_ms` nunca são emitidos).
- **Mudança Técnica Necessária:**
  1. Em `Android_app/lib/providers/device_control_provider.dart`:
     - Implementar o método para acionamento volumétrico com temporizador:
       ```dart
       Future<bool> setPumpManualSpeed(int speedUnits, {int? durationMs}) async {
         final Map<String, dynamic> cmd = {
           "pump_speed": speedUnits.clamp(0, 1000),
         };
         if (durationMs != null && durationMs > 0) {
           cmd["pump_speed_ms"] = durationMs;
         }
         return sendRawCommand(cmd);
       }
       ```

---

### Item 1.10 #5: `clear_nvs` passava pelo Hub (vulnerabilidade de segurança)

- **Achado Original:** No firmware v3.9 e Hub inicial, comandos perigosos como `clear_nvs`, `save_config` e `load_config` não eram filtrados pelo Hub. Se enviados acidentalmente ou por bug, a partição NVS do nó era apagada e o nó reiniciava perdendo calibração e perfis.
- **Decisão Homologada:** O Hub deve filtrar estritamente a chave `pump_command`, permitindo apenas os verbos de processo: `"reset_volume"`, `"start"`, `"stop"`. Qualquer outro verbo é rejeitado com aviso no log (`ESP32_AVISO`).
- **Status no OpenTEC-Hub:**
  - **Arquivo:** `ESP32S3-HUB/ESP32S3-HUB/src/protocol/Commands.h`, linhas 596–614:
    ```cpp
    const char* allowedPumpCommands[] = { "reset_volume", "start", "stop" };
    if (strcmp(key, "pump_command") == 0) {
      bool allowed = false;
      for (const char* ok : allowedPumpCommands) {
        if (val == ok) { allowed = true; break; }
      }
      if (!allowed) {
        ESP32_AVISO(String("pump_command recusado pelo Hub: ") + val);
        continue;
      }
    }
    ```
  - **Suíte de Testes Python:** `ESP32S3-HUB/tests/contracts/test_node_commands.py`, linhas 410–423 (`test_pump_command_whitelist_blocks_clear_nvs_and_config_verbs`).
- **Status no Aplicativo Flutter (`Android_app`):**
  - **Comportamento:** O app nunca emite verbos NVS destrutivos. Totalmente conforme.
- **Mudança Técnica Necessária:** Nenhuma (Já 100% implementado e protegido no Hub).

---

### Item 1.10 #6: Retomada automática após queda de energia ("Robust Recovery")

- **Achado Original:** Se houver interrupção de energia durante um perfil em `OP_RUNNING`, o firmware v3.10 salva periodicamente checkpoints na NVS (`s_active`, `s_vol`, `s_time`, `s_mode`, `s_cvol`). Ao religar, o nó retoma autonomamente a rotação do motor com os valores do último checkpoint (até 60 s antes).
- **Decisão Homologada:** Manter a retomada automática (vital para bateladas longas de bioprocessos que não podem ser interrompidas por microcortes de energia). Registrar o comportamento operacionalmente.
- **Status no OpenTEC-Hub:**
  - **Comportamento:** Transparente. Ao reconectar na rede Wi-Fi, o nó volta a enviar `/pumpData` com `active=1` e o Hub restabelece `PumpOnline: true` e `PumpActive: true`.
- **Status no Aplicativo Flutter (`Android_app`):**
  - **Arquivos:** `Android_app/lib/screens/dashboard_screen.dart`, `Android_app/lib/widgets/peristaltic_pump_card.dart:33–37`.
  - **Comportamento:** Ao religar a energia, o card do Flutter detecta `isActive == true` e exibe o estado "DOSING" automaticamente. Contudo, não há nenhum indicador histórico de que houve um reboot com retomada de ciclo (como um aviso de "Ciclo retomado pós-queda").
- **Mudança Técnica Necessária / Justificativa:**
  - *Justificativa de Não Implementação no Código-Fonte Atual:* A retomada autônoma é tratada pelo hardware/firmware do nó. No app, a interface já reflete com precisão o estado real do nó (`PumpActive`, `PumpMode`, `PumpVol`). Sugere-se apenas adicionar documentação operacional no manual do usuário.

---

### Item 1.10 #7: Porta do sensor de presença de líquido invisível ao app

- **Achado Original:** O nó possui entrada física para sensor de presença de líquido (GPIO 15) e botão de habilitação (GPIO 32). Quando ativado, a bomba só opera na presença de líquido. Esse estado não sai na telemetria HTTP.
- **Decisão Homologada:** Adiar exposição no app. O modo do sensor de líquido é um dispositivo de bancada físico/local. As chaves `sensorEnable` e `sensorBypass` não são encaminhadas pelo Hub.
- **Status no OpenTEC-Hub:**
  - **Arquivo:** `ESP32S3-HUB/ESP32S3-HUB/src/protocol/Commands.h:588–592`.
  - **Comportamento:** `sensorEnable`, `sensorBypass` e `sensorButtonOverride` **não constam** em `simpleKeys`. O Hub bloqueia seu tráfego deliberadamente.
- **Status no Aplicativo Flutter (`Android_app`):**
  - O app não possui nem tenta emitir essas chaves.
- **Justificativa Técnica de Não Implementação:**
  - Diretriz de arquitetura fechada (§1.0 e §1.10 #7). O acionamento por líquido é uma trava física de bancada acoplada aos potenciômetros manuais locais. Permitir controle remoto sobreporia a segurança física do operador de bancada.

---

### Item 1.10 #8: PID sem eco na telemetria (UI ficava travada)

- **Achado Original:** No firmware v3.9, o comando `pid_kp/ki/kd` era aceito mas seus valores atuais nunca eram ecoados na telemetria do nó nem repassados pelo Hub. O app não sabia se os ganhos estavam ativos.
- **Decisão Homologada:** Firmware v3.10 ecoa `kp`, `ki`, `kd` em `/pumpData` e `/readData`. Hub repassa `PumpPidKp`, `PumpPidKi`, `PumpPidKd`. O app deve carregar os valores padrão (0,5 / 0,05 / 0,001), aguardar o eco para habilitar a edição e confirmar a gravação.
- **Status no OpenTEC-Hub:**
  - **Arquivos:**
    - `ESP32S3-HUB/ESP32S3-HUB/src/protocol/Commands.h`, linhas 591 e 617–619: aceita `pumpPidKp/Ki/Kd` e traduz para `pid_kp/ki/kd`.
    - `ESP32S3-HUB/ESP32S3-HUB/src/network/HttpServer.h`, linhas 376–378: extrai `kp`, `ki`, `kd` do nó.
    - `ESP32S3-HUB/ESP32S3-HUB/src/sensor/Telemetry.h`, linhas 341–343: expõe `PumpPidKp`, `PumpPidKi`, `PumpPidKd` no JSON.
  - **Suíte de Testes Python:** `test_node_commands.py:434–440`.
- **Status no Aplicativo Flutter (`Android_app`):**
  - **Arquivos:** `Android_app/lib/models/peristaltic_pump_state.dart`, `Android_app/lib/providers/device_control_provider.dart`, `Android_app/lib/screens/controls_screen.dart`.
  - **Lacuna Crítica Encontrada:**
    - `PeristalticPumpState` **não possui os campos `pidKp`, `pidKi`, `pidKd`**.
    - `DeviceControlProvider` **não possui método para envio de PID**.
    - `controls_screen.dart` **não possui nenhum controle para sintonia de PID**.
- **Mudança Técnica Necessária:**
  1. Em `Android_app/lib/models/peristaltic_pump_state.dart`:
     - Adicionar `final double? pidKp;`, `final double? pidKi;`, `final double? pidKd;` e o getter `bool get hasPidEcho => pidKp != null;`.
  2. Em `Android_app/lib/providers/device_control_provider.dart`:
     - Implementar o despacho de PID:
       ```dart
       Future<bool> setPumpPid({
         required double kp,
         required double ki,
         required double kd,
       }) async {
         return sendRawCommand({
           "pumpPidKp": kp,
           "pumpPidKi": ki,
           "pumpPidKd": kd,
         });
       }
       ```
  3. Em `Android_app/lib/screens/controls_screen.dart`:
     - Adicionar seção retrátil (ExpansionTile) "Sintonia de PID do Nó (Volume)", com campos para Kp, Ki, Kd, validando faixa $\ge 0$ e exibindo os valores ecoados pelo nó.

---

### Item 1.10 #9: Latência de comando 0–2 s e política latest-wins na `pumpBox`

- **Achado Original:** O nó da bomba opera como cliente HTTP, fazendo *poll* em `/pumpCommand` a cada 2 s. Portanto, há uma latência inerente de 0 a 2 segundos entre o app enviar um comando e o nó buscá-lo. Se dois comandos forem emitidos rapidamente, o segundo sobrescreve o primeiro na caixa do Hub (`latest-wins`).
- **Decisão Homologada:** Manter a arquitetura de caixa única confiável (`pumpBox`) com `cmd_id` e semear revisão no reboot. O aplicativo deve bloquear novos envios enquanto `PumpCommandPending` for verdadeiro.
- **Status no OpenTEC-Hub:**
  - **Arquivos:** `ESP32S3-HUB/ESP32S3-HUB/src/protocol/Mailboxes.h`, linhas 22–38 (`queueReliable`), 42–52 (`takeReliable`), 56–71 (`ackReliable`), 6–18 (`seedReliableMailboxes`).
  - **Comportamento:** O Hub gerencia a `pumpBox` perfeitamente, garantindo que o comando fique retido até o nó enviar o `ack_cmd_id` no push seguinte.
- **Status no Aplicativo Flutter (`Android_app`):**
  - **Arquivos:** `Android_app/lib/models/peristaltic_pump_state.dart:85`, `Android_app/lib/screens/controls_screen.dart:1504–1520`.
  - **Lacuna Encontrada:** Embora `PeristalticPumpState` leia `PumpCommandPending` e o card exiba o badge "Cmd Pending", o botão **"Apply Feed Profile"** em `controls_screen.dart:1504` **não verifica `pumpState.isCommandPending`** nem `control.isBusy` no `onApply`! O operador pode clicar repetidas vezes e gerar comandos em cascata que colidem na `pumpBox`.
- **Mudança Técnica Necessária:**
  - Em `Android_app/lib/screens/controls_screen.dart`, atualizar a guarda do botão de aplicação:
    ```dart
    onApply: (_pumpValidationError == null && !pumpState.isCommandPending && !control.isBusy)
        ? () async { ... }
        : null,
    ```

---

### Item 1.10 #10: `start` com `mode = 0` marca estado ativo sem bombear

- **Achado Original:** Enviar `"command": "start"` com `mode = 0` no firmware coloca o nó em `OP_RUNNING`, mas com $Q = 0$, ficando "ativo" sem que o motor gire.
- **Decisão Homologada:** Manter no firmware; o aplicativo nunca deve emitir `"command": "start"`. Para iniciar perfis, o app envia o bloco com `mode: 1..5`, que aciona `startCycle()` automaticamente.
- **Status no OpenTEC-Hub:** Hub aceita `start` na whitelist de `pump_command`, mas não sintetiza.
- **Status no Aplicativo Flutter (`Android_app`):**
  - **Arquivo:** `Android_app/lib/providers/device_control_provider.dart`, linhas 312–350 (`applyPumpProfile`).
  - **Comportamento:** O Flutter App emite diretamente `mode: spec.mode.code` junto com os parâmetros, nunca emitindo a string `"start"`. Totalmente conforme.
- **Mudança Técnica Necessária:** Nenhuma.

---

### Item 1.10 #11 e "—": Correção da natureza do motor e potenciômetro "gain"

- **Achado Original:** Arquivos legados de CAD e documentação mencionavam motor de passo (stepper NEMA 17HS4401) e denominavam o segundo potenciômetro como "gain".
- **Decisão Homologada:** Confirmado em bancada física em 2026-09-12 que o motor montado é **DC escovado** acionado por ponte H dupla BTS7960/IBT-2. O segundo potenciômetro é **sentido e ganho** $\in [-1, +1]$ sobre o primeiro knob.
- **Status no OpenTEC-Hub:** Hub e contratos Python utilizam `pumpSpeed` com sinal.
- **Status no Aplicativo Flutter (`Android_app`):**
  - **Arquivo:** `Android_app/lib/models/peristaltic_pump_state.dart`, linhas 30 e 165.
  - **Inconsistência Identificada:** O docstring e a formatação no Flutter rotulam `PumpSpeed` como `"RPM"`:
    ```dart
    String get formattedSpeed =>
        isConnectedAndActive ? "${speed.toStringAsFixed(1)} RPM" : "-- RPM";
    ```
    Como o motor é DC sem encoder óptico de feedback, o valor `PumpSpeed` é o valor comandado em **unidades de velocidade S (-1000 a +1000)**, e **não RPM**. Exibir "RPM" induz o operador ao erro de acreditar que há medição tacométrica de rotação.
- **Mudança Técnica Necessária:**
  - Em `Android_app/lib/models/peristaltic_pump_state.dart`:
    Alterar `formattedSpeed` para `"S"` ou `"unid."`:
    ```dart
    String get formattedSpeed =>
        isConnectedAndActive ? "${speed.toStringAsFixed(0)} S" : "--";
    ```

---

## 3. Auditoria Detalhada dos Itens da Seção 1.11 (Checklist de Bancada)

A Seção 1.11 elenca 11 ensaios práticos a serem executados em bancada física. Abaixo detalha-se o papel do software (Hub e App) em cada item, as evidências no código e as lacunas a sanar.

---

### Item 1.11 #1: Sentido positivo de rotação

- **Objetivo:** Conferir se $S > 0$ impulsiona o líquido no sentido correto em direção ao vaso (motor DC escovado em ponte H).
- **Papel do Hub e App:** O Hub transmite `pump_speed` positivo sem alteração de sinal. O nó aciona `R_PWM` se `s >= 0` e `L_PWM` se `s < 0`.
- **Status no Código:**
  - Firmware Nó: `FirmwareApp.cpp:224` (`applyDutyFromSpeed`) e `PwmRuntime.h`.
  - Hub: `Commands.h:622`.
  - Flutter App: `applyPumpProfile` gera perfis positivos.
- **Classificação:** Ensaio físico de bancada. O software está apto.

---

### Item 1.11 #2: Detecção de queda de link e tempo de resposta ($\le 4\text{ s}$)

- **Objetivo:** Validar se ao desligar o nó a telemetria marca `PumpOnline = false` em $\le 4\text{ s}$ e o app dispara o alarme "Bomba externa offline".
- **Papel do Hub:**
  - **Arquivo:** `ESP32S3-HUB/ESP32S3-HUB/src/core/AppContext.h:366` (`const unsigned long PUMP_TIMEOUT = 4000;`).
  - **Arquivo:** `ESP32S3-HUB/ESP32S3-HUB/src/sensor/Telemetry.h:151–152`.
  - Se `millis() - snapPumpUpdate > PUMP_TIMEOUT`, o Hub altera `PumpOnline` para `false`. Em 108–110, limpa `pumpEchoSeen`.
- **Papel do App Flutter:**
  - **Arquivo:** `Android_app/lib/models/peristaltic_pump_state.dart:120–121`:
    `bool get isDisconnected => commEnabled && !online;`
  - **Arquivo:** `Android_app/lib/widgets/peristaltic_pump_card.dart:29–32`:
    Muda status para `"PUMP DISCONNECTED"` na cor âmbar.
  - **Lacuna no Flutter:** Não há um despachador de alarmes audíveis ou banner de alerta global no Flutter (`AlarmService`), diferentemente do `Windows_app` (`AlarmService.cs:72`).
- **Plano de Ação:** Adicionar listener de estado em `TelemetryProvider` que emita notificação de sistema caso `pumpState.isDisconnected` permaneça verdadeiro por mais de 4 s.

---

### Item 1.11 #3: Calibração volumétrica ($S = 250, 500, 1000 \times 60\text{ s}$)

- **Objetivo:** Executar acionamentos volumétricos reais com proveta graduada, calcular reta de calibração ($Q = a \cdot S + b$), $R^2$ e resíduos, aplicar coeficientes no nó e validar perfil contínuo de 10 min.
- **Papel do Hub:**
  - Repassa `pump_speed`, `pump_speed_ms`, `pumpSlope` e `pumpIntercept`.
  - Ecoa `PumpSlope` e `PumpIntercept` após confirmação do nó.
- **Papel do App:**
  - **No `Windows_app`:** 100% implementado em `PumpCalibrationViewModel.cs` (execução com temporizador, cálculo de regressão por mínimos quadrados, resíduos, geração de recibo JSON).
  - **No `Android_app`:** **Completamente inexistente.** Não há tela de calibração no aplicativo Flutter principal.
  - **No `External-Devices/bomba-peristaltica/apps/flutter`:** Possui `calibration_page.dart` (614 linhas), mas projetado para comunicação direta via SoftAP na porta 80 (`http://192.168.6.1/command`), sem passar pelo Hub.
- **Plano de Ação:** Portar a lógica de calibração volumétrica do `Windows_app` / `calibration_page.dart` para o `Android_app`, permitindo que o operador execute as corridas através do Hub (`/command` com `pump_speed` e `pump_speed_ms`) e aplique `pumpSlope`/`pumpIntercept`.

---

### Item 1.11 #4: Liberação de knobs de bancada após calibração

- **Objetivo:** Após acionamento via app, clicar em "Potenciômetros" deve devolver o comando aos knobs físicos (`PumpPotEnabled = true`), esquecendo a velocidade manual USB.
- **Papel do Hub:** Repassa `pump_pot: 1` e ecoa `PumpPotEnabled: true`.
- **Papel do App:**
  - No `Windows_app`: Botão no `ControlView.xaml` ligado a `TogglePotentiometersCommand`.
  - No `Android_app`: **Ausente.** (Ver Item 1.10 #3).
- **Plano de Ação:** Implementar o botão e o método `setPumpPotentiometers` no Flutter.

---

### Item 1.11 #5: Corte autônomo de emergência com `pump_speed_ms`

- **Objetivo:** Desconectar cabo de rede/USB durante acionamento com `speed_ms`; confirmar que o motor desliga sozinho ao expirar o prazo.
- **Papel do Hub e App:** O app deve preencher `pump_speed_ms = duration + 3000 ms`. O nó v3.10 implementa a checagem no laço (`FirmwareApp.cpp:177` e `OperationController.h`).
- **Status:** Suportado no Hub. Falta implementar o envio em `Android_app` no método de acionamento manual.

---

### Item 1.11 #6: Preservação de volume após perfil de 5 min (`final_t = 5`)

- **Objetivo:** Parada autônoma ao final do tempo, retorno a `mode: 0`, preservação de `PumpVol`, cálculo de `PumpCycleVol` e zeramento de ambos via `reset_volume`.
- **Papel do Hub:** Repassa `PumpVol` e `PumpCycleVol`.
- **Papel do App:**
  - O Flutter App precisa expor `PumpCycleVol` e disponibilizar o botão `reset_volume`.
- **Status:** Hub pronto; Flutter necessita das adições detalhadas no Item 1.10 #2.

---

### Item 1.11 #7: Acúmulo de volume em perfis sequenciais sem `reset_volume`

- **Objetivo:** Rodar dois ciclos seguidos; `PumpVol` acumula continuamente; o PID do segundo ciclo utiliza apenas `cyc_vol`.
- **Papel do Firmware e Hub:** Implementado no firmware v3.10 via `startCycle()` que reseta o integrador do PID e marca `g_cycleStartVolumeMl = g_cumulativeVolumeMl`. O Hub transmite os campos.
- **Papel do App:** Mostrar graficamente o volume acumulado da sessão (`PumpVol`) e o volume do ciclo corrente (`PumpCycleVol`).

---

### Item 1.11 #8: Envio e persistência NVS do PID pelo aplicativo

- **Objetivo:** Enviar `pumpPidKp/Ki/Kd` pelo app, receber o eco em $\le 3\text{ s}$, persistir em NVS (`feed_pump/config`) e confirmar que após reboot os ganhos permanecem salvos.
- **Papel do Hub:** Repassa em `Commands.h:617–619` e ecoa em `Telemetry.h:341–343`.
- **Papel do App:** Falta implementar a interface de PID no Flutter (Item 1.10 #8).

---

### Item 1.11 #9: Retomada de emergência pós-queda no meio de um perfil

- **Objetivo:** Desconectar a fonte de alimentação com a bomba rodando em perfil; religar a fonte e confirmar retomada autônoma do motor via checkpoint NVS `s_cvol`.
- **Papel do Software:** Ensaio estritamente físico de bancada. O firmware v3.10 contém toda a lógica de checkpoint NVS e auto-retomada (`RuntimeStateStore.h:checkAndRecoverState`).

---

### Item 1.11 #10: Comando após reboot do Hub (Semeadura de `cmd_id`)

- **Objetivo:** Reiniciar o Hub mantendo o nó ligado; enviar `reset_volume` e confirmar que o nó aceita o comando sem descartar por colisão de ID.
- **Papel do Hub:**
  - **Arquivo:** `ESP32S3-HUB/ESP32S3-HUB/src/protocol/Mailboxes.h:6–18` (`seedReliableMailboxes`).
  - O Hub gera um número pseudo-aleatório elevado (`base = ((esp_random() % 900000UL) + 100000UL) * 1000UL`), garantindo que a nova sequência de `cmd_id` nunca coincida com o `lastAppliedId` retido na RAM do nó.
- **Papel do App:** O app emite o comando normalmente; o Hub garante a entrega. Totalmente pronto no Hub.

---

### Item 1.11 #11: Ensaio do modo físico de presença de líquido

- **Objetivo:** Com `sensorEnable: 1` enviado via `POST /command` local no AP da bomba e tubo seco, motor deve permanecer parado. Ao molhar o sensor (GPIO 15 em nível LOW), motor deve partir em $\le 50\text{ ms} + 500\text{ ms}$ de trava.
- **Papel do Hub e App:** O Hub deliberadamente **não encaminha** `sensorEnable`. O ensaio é realizado exclusivamente via conexão local de bancada no AP `192.168.6.1`. Totalmente conforme a diretriz de segurança fechada.

---

## 4. Plano Técnico de Ação Concreto (Arquivo por Arquivo)

Abaixo estão especificadas as alterações exatas a serem implementadas no código do Hub e do Aplicativo Flutter.

---

### 4.1 Modificações no Aplicativo Flutter (`Android_app`)

#### Arquivo 1: `Android_app/lib/models/peristaltic_pump_state.dart`

**Objetivo:** Adicionar os campos da telemetria v3.10 (`PumpCycleVol`, `PumpPotEnabled`, `PumpSlope`, `PumpIntercept`, `PumpPidKp`, `PumpPidKi`, `PumpPidKd`) e corrigir descrições técnicas de unidades.

**Alterações Específicas:**
- Linha 30: Alterar a documentação de `PumpSpeed: Actual motor speed (RPM)` para `PumpSpeed: Commanded internal speed units S (-1000..+1000)`.
- Linha 29: Alterar `PumpPWM: Actuation PWM (0..255)` para `PumpPWM: 10-bit LEDC duty cycle (0 ou 155..1023)`.
- Adicionar os campos na classe `PeristalticPumpState`:
  ```dart
  final double cycleVolume;
  final bool potEnabled;
  final double? slope;
  final double? intercept;
  final double? pidKp;
  final double? pidKi;
  final double? pidKd;
  ```
- No construtor `empty()`: inicializar `cycleVolume: 0.0`, `potEnabled: false`, demais nulos.
- No factory `fromJson(Map<String, dynamic> json)`:
  ```dart
  final double rawCycleVol = (json['PumpCycleVol'] is num) ? (json['PumpCycleVol'] as num).toDouble() : 0.0;
  final bool rawPotEnabled = json['PumpPotEnabled'] == true;
  final double? rawSlope = (json['PumpSlope'] is num) ? (json['PumpSlope'] as num).toDouble() : null;
  final double? rawIntercept = (json['PumpIntercept'] is num) ? (json['PumpIntercept'] as num).toDouble() : null;
  final double? rawKp = (json['PumpPidKp'] is num) ? (json['PumpPidKp'] as num).toDouble() : null;
  final double? rawKi = (json['PumpPidKi'] is num) ? (json['PumpPidKi'] as num).toDouble() : null;
  final double? rawKd = (json['PumpPidKd'] is num) ? (json['PumpPidKd'] as num).toDouble() : null;
  ```
- Linha 164–166: Atualizar `formattedSpeed`:
  ```dart
  String get formattedSpeed =>
      isConnectedAndActive ? "${speed.toStringAsFixed(0)} S" : "--";
  ```
- Adicionar getters auxiliares:
  ```dart
  bool get hasPidEcho => pidKp != null && pidKi != null && pidKd != null;
  bool get hasCalibrationEcho => slope != null && intercept != null;
  String get formattedCycleVolume =>
      isConnectedAndActive ? "${cycleVolume.toStringAsFixed(1)} mL" : "-- mL";
  ```

---

#### Arquivo 2: `Android_app/lib/providers/device_control_provider.dart`

**Objetivo:** Eliminar o envio indevido de `"speed": 0` no `stopPump()`, adicionar comandos de processo (`reset_volume`, `pump_pot`, `pump_speed`, `pumpSlope/pumpIntercept`, `pumpPidKp/Ki/Kd`).

**Alterações Específicas:**
- **Linhas 305–310 (`stopPump`):**
  *Antes:*
  ```dart
  Future<bool> stopPump() async {
    return sendRawCommand({
      "mode": 0,
      "speed": 0,
    });
  }
  ```
  *Depois:*
  ```dart
  Future<bool> stopPump() async {
    return sendRawCommand({
      "mode": 0,
    });
  }
  ```
- **Adicionar novos métodos em `device_control_provider.dart`:**
  ```dart
  /// Envia comando de zeramento do volume acumulado da sessão (reset_volume)
  Future<bool> resetPumpVolume() async {
    return sendRawCommand({"pump_command": "reset_volume"});
  }

  /// Comuta o controle para os potenciômetros físicos de bancada (true) ou trava-os (false)
  Future<bool> setPumpPotentiometers(bool enabled) async {
    return sendRawCommand({"pump_pot": enabled ? 1 : 0});
  }

  /// Acionamento de velocidade manual com prazo autônomo de segurança opcional
  Future<bool> setPumpManualSpeed(int speedUnits, {int? speedDeadlineMs}) async {
    final Map<String, dynamic> cmd = {
      "pump_speed": speedUnits.clamp(-1000, 1000),
    };
    if (speedDeadlineMs != null && speedDeadlineMs > 0) {
      cmd["pump_speed_ms"] = speedDeadlineMs;
    }
    return sendRawCommand(cmd);
  }

  /// Aplica coeficientes da reta linear de calibração (slope e intercept)
  Future<bool> applyPumpCalibration(double slope, double intercept) async {
    if (slope <= 0.0 || slope.isNaN || intercept.isNaN) return false;
    return sendRawCommand({
      "pumpSlope": slope,
      "pumpIntercept": intercept,
    });
  }

  /// Aplica novos ganhos de sintonia do PID de volume do nó
  Future<bool> setPumpPid({
    required double kp,
    required double ki,
    required double kd,
  }) async {
    if (kp < 0 || ki < 0 || kd < 0) return false;
    return sendRawCommand({
      "pumpPidKp": kp,
      "pumpPidKi": ki,
      "pumpPidKd": kd,
    });
  }
  ```

---

#### Arquivo 3: `Android_app/lib/screens/controls_screen.dart`

**Objetivo:** Expor os novos controles operacionais na interface do usuário (Zerar Volume, Botão Potenciômetros, Sintonia de PID, trava de botão quando comando pendente).

**Alterações Específicas:**
- Adicionar controladores de texto para PID no `_ControlsScreenState`:
  ```dart
  late TextEditingController _pumpKpController;
  late TextEditingController _pumpKiController;
  late TextEditingController _pumpKdController;
  ```
- No `initState()`: Inicializar com os padrões homologados:
  ```dart
  _pumpKpController = TextEditingController(text: "0.500");
  _pumpKiController = TextEditingController(text: "0.050");
  _pumpKdController = TextEditingController(text: "0.001");
  ```
- Linhas 1504–1520: Bloquear botão "Apply Feed Profile" se `pumpState.isCommandPending` ou `control.isBusy`:
  ```dart
  onApply: (_pumpValidationError == null && !pumpState.isCommandPending && !control.isBusy)
      ? () async { ... }
      : null,
  ```
- Abaixo da linha 1602: Inserir barra de botões de utilidades do operador:
  1. **Botão Zerar Volume Acumulado:**
     Dispara diálogo de confirmação ("Deseja zerar o volume acumulado da sessão?"). Ao confirmar, invoca `control.resetPumpVolume()`.
  2. **Botão Alternador de Potenciômetros:**
     Exibe estado atual (`pumpState.potEnabled ? "Knobs Liberados" : "Knobs Bloqueados"`). Ao clicar, invoca `control.setPumpPotentiometers(!pumpState.potEnabled)`.
- Abaixo da linha 1890: Inserir `ExpansionTile` para Sintonia de PID:
  Exibe os valores ecoados pelo nó (`pumpState.pidKp`, etc.), libera edição caso o nó tenha ecoado e permite enviar novos ganhos via `control.setPumpPid()`.

---

#### Arquivo 4: `Android_app/test/peristaltic_pump_test.dart`

**Objetivo:** Atualizar os testes unitários para a nova especificação sem `speed: 0` e cobrir os novos comandos.

**Alterações Específicas:**
- Linhas 280–295: Atualizar testes `Disabling pumpComm performs ordered two-step safe shutdown` e `stopPump sends {"mode": 0}` para esperar apenas `{"mode": 0}`.
- Adicionar novos casos de teste unitário:
  - `resetPumpVolume sends {"pump_command": "reset_volume"}`.
  - `setPumpPotentiometers sends {"pump_pot": 1}` e `{"pump_pot": 0}`.
  - `setPumpManualSpeed sends {"pump_speed": 500, "pump_speed_ms": 63000}`.
  - `applyPumpCalibration sends {"pumpSlope": 0.028, "pumpIntercept": 1.76}`.
  - `setPumpPid sends {"pumpPidKp": 0.5, "pumpPidKi": 0.05, "pumpPidKd": 0.001}`.
  - `fromJson parses 3.10 telemetry fields (PumpCycleVol, PumpPotEnabled, PumpPidKp, etc.)`.

---

### 4.2 Verificação no OpenTEC-Hub (ESP32-S3 e Testes Python)

- **Firmware C++ (`Commands.h`, `HttpServer.h`, `Mailboxes.h`, `Telemetry.h`):**
  O Hub já se encontra **100% completo e conforme** com o protocolo do firmware v3.10.
  - Whitelist restrita a `reset_volume`, `start`, `stop` (`Commands.h:596`).
  - Tradução correta de prefixos (`pump_speed` $\rightarrow$ `speed`, `pump_speed_ms` $\rightarrow$ `speed_ms`, `pump_pot` $\rightarrow$ `pot`, `pumpPidK*` $\rightarrow$ `pid_k*`).
  - Serialização completa de ecos em `Telemetry.h:325–347`.
  - Semeadura aleatória de `cmd_id` na inicialização (`Mailboxes.h:6–18`).
- **Testes Python (`ESP32S3-HUB/tests/contracts/`):**
  Todos os 81 testes de contrato executados via `python -m unittest discover ESP32S3-HUB/tests/contracts` foram aprovados com êxito (código de saída 0). Recomenda-se apenas adicionar um teste específico em `test_node_commands.py` para garantir que o Hub rejeite qualquer comando com chave `"speed"` desprovida do prefixo `"pump_"`, assegurando que o app não consiga enviar frames mal-formados.

---

## 5. Tabela Resumo de Rastreabilidade e Decisões Técnicas

| Item | Origem | Componente | Status Atual | Ação Técnica Proposta / Justificativa |
|---|---|---|---|---|
| **1.10 #1** | Curva estimativa | Flutter App | Sem aviso ao usuário | Adicionar tooltip indicativo no card de telemetria; planejar módulo de calibração |
| **1.10 #2** | `mode:0` / Volume | Flutter App | `stopPump` envia `speed:0`; sem `reset_volume` | **Corrigir `stopPump` para `{"mode":0}`**; implementar `resetPumpVolume()` no Provider e UI |
| **1.10 #3** | Knobs pegajosos | Flutter App | `pump_pot` e `PumpPotEnabled` ausentes | Implementar `setPumpPotentiometers` no Provider, ler eco no State e criar botão na UI |
| **1.10 #4** | Temporizador `speed_ms` | Flutter App | Ausente no Provider | Implementar `setPumpManualSpeed(S, speedDeadlineMs)` com $\Delta t + 3\text{ s}$ |
| **1.10 #5** | Bloqueio `clear_nvs` | Hub 10.2 | Totalmente protegido | Manter whitelist em `Commands.h`; nenhuma alteração necessária |
| **1.10 #6** | Retomada autônoma | Firmware Nó | Funcional via NVS | Justificativa: Tratamento autônomo no nó; documentar para o operador |
| **1.10 #7** | Porta do sensor | Hub 10.2 | Bloqueada por projeto | Justificativa: Segurança de bancada; operação física local exclusiva |
| **1.10 #8** | Eco e sintonia PID | Flutter App | Ausente na UI e Model | Mapear `PumpPidKp/Ki/Kd` no State, método no Provider e ExpansionTile na UI |
| **1.10 #9** | Latest-wins / Pending | Flutter App | Botão Apply não checa pending | Adicionar guarda `!pumpState.isCommandPending` no `onApply` de `controls_screen.dart` |
| **1.10 #10** | Verbo `start` | Flutter App | Conforme (App não usa) | Manter; app continua emitindo apenas modos 1..5 e 0 |
| **1.10 #11** | Motor DC / Ganho | Flutter App | Docstring exibe "RPM" | Corrigir docstring e label de unidade de velocidade para "S" (passos internos) |
| **1.11 #1** | Sentido rotação | Bancada | Pronto no software | Ensaio físico com motor e líquido na bancada |
| **1.11 #2** | Queda de link | Hub & App | Hub pronto (4 s); App sem alarme sonoro | Adicionar notificação de alarme no Flutter caso desconectado $> 4\text{ s}$ |
| **1.11 #3** | Calibração volumétrica | Flutter App | Inexistente no `Android_app` | Portar fluxo de calibração volumétrica assistida multiponto para o Flutter |
| **1.11 #4** | Knobs pós-calibração | Flutter App | Ausente | Resolvido com a implementação do Item 1.10 #3 |
| **1.11 #5** | Corte por prazo | Hub & Firmware | Pronto no Hub e Nó | Ensaio físico desconectando cabo durante `speed_ms` |
| **1.11 #6** | Perfil 5 min e `PumpVol` | Flutter App | `PumpCycleVol` ausente | Resolvido com a implementação do Item 1.10 #2 |
| **1.11 #7** | Perfis contínuos | Firmware & Hub | Pronto no Nó e Hub | Ensaio físico de dois ciclos consecutivos |
| **1.11 #8** | Persistência PID | Flutter App | Ausente na UI | Resolvido com a implementação do Item 1.10 #8 |
| **1.11 #9** | Teste queda energia | Bancada | Pronto no Nó | Ensaio físico cortando a alimentação durante perfil |
| **1.11 #10** | Reboot do Hub | Hub 10.2 | Pronto (`Mailboxes.h`) | Ensaio físico reiniciando Hub e enviando `reset_volume` |
| **1.11 #11** | Ensaio sensor líquido | Bancada / Nó | Local na porta 80 do nó | Ensaio físico no AP `192.168.6.1` via `POST /command` |

---

## 6. Conclusão da Investigação

A investigação detalhada revelou que o **OpenTEC-Hub (backend C++ no ESP32-S3)** já está **completamente maduro e em conformidade estrita** com todas as decisões homologadas na revisão v3.10 da Bomba Peristáltica Externa, com cobertura integral em seus testes de contrato Python.

Por outro lado, o aplicativo **Flutter (`Android_app`) apresenta desvios e lacunas pontuais de alta relevância**, destacando-se:
1. O envio incorreto de `"speed": 0` dentro de `stopPump()`, violando a especificação de parada limpa de perfil;
2. A ausência de suporte para o comando `reset_volume`, impedindo o operador de zerar o totalizador de volume;
3. A ausência de controles e telemetria para os potenciômetros de bancada (`pump_pot`, `PumpPotEnabled`) e ganhos de PID (`pumpPidKp/Ki/Kd`);
4. A ausência de uma tela de calibração volumétrica assistida integrada ao Hub.

O plano técnico formulado nesta análise fornece o mapeamento exato de linhas de código e trechos de implementação prontos para serem aplicados pela equipe de implementação.
