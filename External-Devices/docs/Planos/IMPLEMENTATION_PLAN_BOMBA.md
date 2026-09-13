# PLANO DE IMPLEMENTAÇÃO E HOMOLOGAÇÃO: BOMBA PERISTÁLTICA EXTERNA (FIRMWARE v3.10)
## Resolução de Inconsistências, Limitações (§1.10) e Protocolos de Homologação em Bancada (§1.11)

**Data de Emissão:** 2026-09-13  
**Status:** Oficial / Autoritativo para Engenharia  
**Referência Principal:** `External-Devices/docs/COMANDOS_DISPOSITIVOS_EXTERNOS.md` (§1.0 a §1.11)  
**Subsistemas Cobertos:**
- Firmware do Nó Peristáltico v3.10 (`External-Devices/bomba-peristaltica/firmware/peristaltic-pump/`)
- Gateway OpenTEC-Hub v10.2 (`ESP32S3-HUB/ESP32S3-HUB/`)
- Aplicativo Flutter Multiplataforma (`Android_app/`)
- Referência Desktop C# WPF (`Windows_app/`)
- Bancada de Ensaios Físicos e Eletromecânicos

---

## SUMÁRIO EXECUTIVO & VISÃO GERAL DA ARQUITETURA

### 1. Topologia Distribuída do Ecossistema OpenTEC-Hub
O subsistema da Bomba Peristáltica Externa opera em topologia distribuída com comunicação cliente-servidor assíncrona sobre Wi-Fi e barramento REST HTTP, particionada em quatro camadas fundamentais:

1. **Nó Atuador Peristáltico (ESP32 Dual-Core, Firmware v3.10):**
   - **Core 0 (Tempo Real Estrito - `Pump-PWM-Vol`):** Executa tarefa cíclica a cada 2 ms (`PwmRuntime.h`), aplicando duty cycle de 10 bits (0 ou 155..1023) a 7,5 kHz no periférico LEDC e realizando a integração trapezoidal contínua do volume dosado ($V = \int Q\,dt$) a partir da reta de calibração ativa.
   - **Core 1 (Laço de Comunicação e Controle - `loopTask`):** Processa servidor HTTP assíncrono na porta 80, parsing JSON (Serial USB e Wi-Fi), cliente HTTP para o Hub (push a 1 Hz em `/pumpData`, poll a 2 s em `/pumpCommand`), máquina de estados (`OP_IDLE`, `OP_WAITING`, `OP_RUNNING`), algoritmo PID de volume e persistência não-volátil NVS via ESP-IDF `Preferences`. Protegido por Watchdog TWDT de 15 s.
2. **Gateway Central OpenTEC-Hub (ESP32-S3, Firmware v10.2):**
   - Atua como ponto de agregação e arbitração entre o aplicativo supervisor e os dispositivos de campo.
   - Gerencia caixa de correio confiável com semântica de elemento único e substituição atômica (*latest-wins*) em `Mailboxes.h` (`pumpBox`), com semeadura pseudoaleatória de `cmd_id` na inicialização (`seedReliableMailboxes`).
   - Implementa filtragem perimetral estrita de comandos (`allowedPumpCommands[]` em `Commands.h`), descartando verbos destrutivos da NVS e traduzindo chaves de calibração e sintonia de PID.
   - Agrega telemetria em snapshot JSON servido em `/readData` e armazena diagnósticos de saúde em `/nodeDiag`.
3. **Aplicativo Supervisor Multiplataforma (Flutter / Dart - `Android_app`):**
   - Responsável pelo monitoramento contínuo em tempo real (dashboard) e controle operacional dos atuadores de processo.
   - Comunica-se exclusivamente via REST com o Hub (`/readData` com suporte a ETag e `/command` via POST).
4. **Implementação de Referência e Suporte (Windows C# WPF e SoftAP Flutter):**
   - `Windows_app`: Implementação desktop de alta maturidade com suporte completo a calibração volumétrica multiponto com proveta/balança, persistência de recibos JSON e arbitragem por posse de atuador (`ActuatorId.ExternalPump`).
   - `External-Devices/bomba-peristaltica/apps/flutter`: Aplicativo de serviço de bancada para conexão direta via ponto de acesso local (`FeedPump`, IP `192.168.6.1`).

```
┌─────────────────────────────────────────────────────────────────────────┐
│                    APLICATIVO FLUTTER (Android_app)                     │
│  - DeviceControlProvider  - PeristalticPumpState  - ControlsScreen      │
└────────────────────────────────────┬────────────────────────────────────┘
                                     │ HTTP REST (/command, /readData)
                                     ▼
┌─────────────────────────────────────────────────────────────────────────┐
│                       OPENTEC-HUB (ESP32-S3 v10.2)                      │
│  - Mailboxes.h (pumpBox, latest-wins, seed cmd_id)                      │
│  - Commands.h (allowedPumpCommands: reset_volume, start, stop)          │
│  - Telemetry.h (Agregador /readData com ETag, PumpOnline timeout 4 s)   │
└────────────────────────────────────┬────────────────────────────────────┘
                                     │ Wi-Fi STA / HTTP (Poll 2s / Push 1Hz)
                                     ▼
┌─────────────────────────────────────────────────────────────────────────┐
│                 NÓ BOMBA PERISTÁLTICA (ESP32 v3.10)                     │
│  - Core 1: HTTP Client, NVS Checkpoint (60s), PID de Volume             │
│  - Core 0: Tarefa PWM 2ms (LEDC 7.5 kHz, BTS7960, Integrador Trapezoid) │
│  - Hardware: Motor DC Escovado, Knobs (Pinos 34/35), Eletrodo Pino 15   │
└─────────────────────────────────────────────────────────────────────────┘
```

### 2. Diagnóstico Geral de Maturidade e Lacunas entre Camadas
- **Firmware do Nó (v3.10):** Encontra-se **100% implementado e em conformidade estrita**. Todas as limitações identificadas no legado v3.9 foram sanadas (retenção de volume com contador de sessão `g_cumulativeVolumeMl`, isolamento de ciclo `g_cycleStartVolumeMl`, devolução de knobs via `pot: 1`, parada autônoma com `speed_ms`, eco de PID `kp, ki, kd` e recuperação robusta pós-queda via checkpoint `s_cvol`). Nenhuma alteração de código é necessária no firmware.
- **Gateway OpenTEC-Hub (v10.2):** Encontra-se **100% implementado e coberto por 81 testes automatizados de contrato Python**. A filtragem de comandos destrutivos, semeadura de `cmd_id` e agregação de telemetria operam conforme o especificado. Nenhuma alteração de código é necessária no Hub.
- **Aplicativo Flutter (`Android_app`):** Apresenta **desvios e omissões críticas** em relação ao contrato v3.10:
  1. *Bug Crítico em `stopPump()`:* Envia simultaneamente `{"mode": 0, "speed": 0}`, violando o padrão de parada limpa e induzindo efeitos colaterais caso a chave `speed` atinja o nó.
  2. *Ausência de `reset_volume`:* Não há método no provider nem botão na interface para zerar o totalizador de volume acumulado.
  3. *Ausência de Controle de Potenciômetros (`pump_pot`):* O operador de bancada não tem como destravar os botões manuais pelo aplicativo Flutter após acionamentos remotos.
  4. *Ausência de Acionamento Temporizado (`pump_speed_ms`):* Não há suporte para envio de velocidade com prazo de desligamento autônomo.
  5. *Ausência de Campos de Telemetria v3.10 e Sintonia de PID:* O modelo `PeristalticPumpState` ignora `PumpCycleVol`, `PumpPotEnabled`, `PumpSlope`, `PumpIntercept` e `PumpPidKp/Ki/Kd`. A interface não expõe expansor de sintonia.
  6. *Rótulo Incorreto de Velocidade:* O aplicativo exibe "RPM" em vez de passos internos de velocidade $S \in [-1000, 1000]$.
  7. *Inexistência de Assistente de Calibração Volumétrica:* O Flutter não possui fluxo guiado para ensaio de calibração multiponto com proveta e cálculo de $R^2$.
- **Checklist de Bancada (§1.11):** Os 11 itens constituem protocolos físicos de aceitação e qualificação de montagem com líquido, cronômetro, balança e ensaios de corte elétrico, prontos para execução experimental.

---

## SEÇÃO 1: AUDITORIA E PLANO TÉCNICO DA SEÇÃO 1.10 (LIMITAÇÕES, RISCOS E DECISÕES)

Esta seção analisa detalhadamente cada um dos 12 itens de §1.10 de `COMANDOS_DISPOSITIVOS_EXTERNOS.md` e as 4 decisões de segurança arquiteturais fechadas (D-SEC-01 a D-SEC-04), fornecendo para cada item o status cruzado nas 3 camadas de código e um **Plano de Ação Técnico Concreto** (com arquivos, métodos e contratos exatos) ou uma **Justificativa Técnica de Não-Implementação**.

---

### Item 1.10.1: Estimativa de Volume e Vazão por Curva de Calibração (Volume Sensorless)
- **Referência na Especificação:** §1.10 Item 1 / F-110-01
- **Citação Textual Autoritativa:**
  > *"Volume e vazão são estimados pela curva | Manter; calibrar por volume | Documentado (§1.3); calibração volumétrica no app"*
- **Status Atual no Código-Fonte:**
  - **Firmware Nó:** Implementado em `External-Devices/bomba-peristaltica/firmware/peristaltic-pump/src/hardware/PwmRuntime.h:46-56` (integração analítica a cada 2 ms no Core 0), `src/control/SensorAndConversion.h:21-37` (`mlminToSpeedUnits`, `speedUnitsToMlmin`, `pwmDutyToMlmin`) e `src/control/OperationController.h:60-75`.
  - **Gateway Hub:** Implementado em `ESP32S3-HUB/ESP32S3-HUB/src/sensor/Telemetry.h:332-333` (repassa `PumpFlow` e `PumpVol`).
  - **Aplicativo Flutter (`Android_app`):** Lacuna em `Android_app/lib/models/peristaltic_pump_state.dart:43-44` e `Android_app/lib/widgets/peristaltic_pump_card.dart:128-133`. O app exibe volume e vazão sem informar que são estimativas decorrentes da reta de calibração e não possui assistente de calibração.
- **Plano de Ação Técnico:**
  - **Componente:** Aplicativo Flutter (`Android_app`).
  - **Arquivo 1:** `Android_app/lib/widgets/peristaltic_pump_card.dart`
    - Adicionar ícone de informação (`Icons.info_outline`) com `Tooltip` nos rótulos de vazão e volume: *"Vazão e volume inferidos numericamente da calibração volumétrica ativa ($Q = a \cdot S + b$)."*
  - **Arquivo 2:** `Android_app/lib/screens/pump_calibration_screen.dart` (Novo Módulo)
    - Criar tela de calibração assistida multiponto executando pulsos em $S=250, 500, 1000$ com temporizador de segurança, cálculo de regressão linear e envio de `pumpSlope` e `pumpIntercept`.
  - **Firmware e Hub:** Nenhuma alteração necessária.

---

### Item 1.10.2: Preservação de Volume Acumulado no Comando de Parada e `mode: 0`
- **Referência na Especificação:** §1.10 Item 2 / F-110-02
- **Citação Textual Autoritativa:**
  > *"`mode:0` zerava o volume | Parar sem zerar; zerar só com `reset_volume` | `startCycle()` substitui o zeramento em `stop`, `mode`, troca de parâmetro, `WAITING→RUNNING` e fim de `final_t`; `vol` é contador de sessão, `cyc_vol` é o do ciclo; checkpoint guarda `s_cvol`"*
- **Status Atual no Código-Fonte:**
  - **Firmware Nó:** Implementado em `src/control/OperationController.h:251-257` (`stop` invoca `startCycle()` preservando `g_cumulativeVolumeMl`), `src/control/OperationController.h:258-261` (`reset_volume` é o único que zera a sessão sob `taskDISABLE_INTERRUPTS()`), `src/control/OperationController.h:41-54` (`startCycle()` isola o ciclo capturando `g_cycleStartVolumeMl`) e `src/protocol/TelemetryCodec.h:26-27` (emite `vol` e `cyc_vol`).
  - **Gateway Hub:** Implementado em `Commands.h:589, 596` (whitelist aceita `reset_volume`) e `Telemetry.h:345` (expõe `PumpCycleVol`).
  - **Aplicativo Flutter (`Android_app`):** **Desvio Crítico e Omissão:**
    - Em `Android_app/lib/providers/device_control_provider.dart:306-310`: `stopPump()` envia `{"mode": 0, "speed": 0}`. A inclusão de `speed: 0` viola a diretriz de parada limpa de perfil.
    - O comando `reset_volume` não existe no provider nem na interface.
    - O modelo `PeristalticPumpState` não lê a chave `PumpCycleVol`.
- **Plano de Ação Técnico:**
  - **Componente:** Aplicativo Flutter (`Android_app`).
  - **Arquivo 1:** `Android_app/lib/providers/device_control_provider.dart`
    - Corrigir `stopPump()` para enviar estritamente `{"mode": 0}`:
      ```dart
      Future<bool> stopPump() async {
        return sendRawCommand({"mode": 0});
      }
      ```
    - Implementar `resetPumpVolume()`:
      ```dart
      Future<bool> resetPumpVolume() async {
        return sendRawCommand({"pump_command": "reset_volume"});
      }
      ```
  - **Arquivo 2:** `Android_app/lib/models/peristaltic_pump_state.dart`
    - Adicionar propriedade `final double cycleVolume;` lendo `json['PumpCycleVol']`.
  - **Arquivo 3:** `Android_app/lib/screens/controls_screen.dart`
    - Adicionar botão **"Zerar Volume Acumulado"** com diálogo modal de confirmação.
  - **Arquivo 4:** `Android_app/test/peristaltic_pump_test.dart`
    - Atualizar testes para validar que `stopPump()` despacha apenas `{"mode": 0}` e adicionar teste unitário para `resetPumpVolume()`.

---

### Item 1.10.3: Arbitragem e Desbloqueio dos Potenciômetros Físicos (`pot: 1/0`, `pump_pot`)
- **Referência na Especificação:** §1.10 Item 3 / F-110-03
- **Citação Textual Autoritativa:**
  > *"`speed` pegajoso: knobs mortos até reboot | Comando para habilitar/desabilitar os potenciômetros | `pot:1/0` no nó (`pump_pot` no Hub); eco `pot` → `PumpPotEnabled`; botão Potenciômetros no app"*
- **Status Atual no Código-Fonte:**
  - **Firmware Nó:** Implementado em `src/control/OperationController.h:314-328` (trata `"pot": 1`, restaurando `disablePot = false`, `hasUsbSpeed = false`, `usbSpeedSteps = 0.0f` e limpando prazo) e `src/network/HubClient.h:188` (transmite eco `pot = (disablePot || hasUsbSpeed) ? 0 : 1`).
  - **Gateway Hub:** Implementado em `Commands.h:620` (traduz `pump_pot` para `pot`) e `Telemetry.h:344` (expõe `PumpPotEnabled: true/false`).
  - **Aplicativo Flutter (`Android_app`):** **Omissão:** `PeristalticPumpState` ignora `PumpPotEnabled`, `DeviceControlProvider` não possui método `setPumpPotentiometers` e não há botão de devolução na tela `controls_screen.dart`.
- **Plano de Ação Técnico:**
  - **Componente:** Aplicativo Flutter (`Android_app`).
  - **Arquivo 1:** `Android_app/lib/models/peristaltic_pump_state.dart`
    - Adicionar campo `final bool potEnabled;`, inicializado em `empty()` como `false` e mapeado no `fromJson` via `json['PumpPotEnabled'] == true`.
  - **Arquivo 2:** `Android_app/lib/providers/device_control_provider.dart`
    - Implementar:
      ```dart
      Future<bool> setPumpPotentiometers(bool enabled) async {
        return sendRawCommand({"pump_pot": enabled ? 1 : 0});
      }
      ```
  - **Arquivo 3:** `Android_app/lib/screens/controls_screen.dart`
    - Inserir botão de alternância **"Potenciômetros de Bancada"**, com cor condicional ao estado `pumpState.potEnabled` (Verde = Liberados, Âmbar = Travados) invocando `setPumpPotentiometers(!pumpState.potEnabled)`.

---

### Item 1.10.4: Temporização de Velocidade Manual e Parada Autônoma (`speed_ms`, `pump_speed_ms`)
- **Referência na Especificação:** §1.10 Item 4 / F-110-04
- **Citação Textual Autoritativa:**
  > *"`speed` sem temporizador | `speed_ms` no firmware (parada autônoma) | `speed_ms` opcional; laço zera a velocidade ao vencer; a calibração envia Δt + 3 s (a parada primária continua no app, para o Δt medido ser o do app)"*
- **Status Atual no Código-Fonte:**
  - **Firmware Nó:** Implementado em `src/control/OperationController.h:305-312` (lê `"speed_ms"` e calcula `g_usbSpeedUntilMs = millis() + msVal`) e `src/core/Lifecycle.h:138-143` (corte não bloqueante imune a overflow: `(long)(now - g_usbSpeedUntilMs) >= 0` zera `usbSpeedSteps = 0.0f`).
  - **Gateway Hub:** Implementado em `Commands.h:620` (remove prefixo `pump_` e repassa `speed` e `speed_ms`). Testado em `test_node_commands.py:428`.
  - **Aplicativo Flutter (`Android_app`):** **Omissão:** `DeviceControlProvider` não expõe método para dosagem manual ou calibração com deadline de segurança.
- **Plano de Ação Técnico:**
  - **Componente:** Aplicativo Flutter (`Android_app`).
  - **Arquivo 1:** `Android_app/lib/providers/device_control_provider.dart`
    - Adicionar método com temporização de segurança:
      ```dart
      Future<bool> setPumpManualSpeed(int speedUnits, {int? durationMs}) async {
        final Map<String, dynamic> cmd = {
          "pump_speed": speedUnits.clamp(-1000, 1000),
        };
        if (durationMs != null && durationMs > 0) {
          cmd["pump_speed_ms"] = durationMs;
        }
        return sendRawCommand(cmd);
      }
      ```
  - **Arquivo 2:** Módulo de calibração volumétrica do Flutter utilizará `setPumpManualSpeed(S, durationMs: pulseDurationMs + 3000)` para garantir desligamento autônomo em caso de perda de comunicação durante ensaios de calibração.

---

### Item 1.10.5: Proteção de Memória Flash NVS no Hub e Filtragem de Comandos Críticos (`clear_nvs`)
- **Referência na Especificação:** §1.10 Item 5 / F-110-05
- **Citação Textual Autoritativa:**
  > *"`clear_nvs` passava pelo Hub | Filtrar no Hub: só `reset_volume`, `start`, `stop` | `allowedPumpCommands[]` em `Commands.h`; recusa com `ESP32_AVISO`; teste de contrato"*
- **Status Atual no Código-Fonte:**
  - **Firmware Nó:** `OperationController.h:269-274` mantém o handler de `clear_nvs` acessível apenas via Serial USB ou POST direto no IP local `192.168.6.1`.
  - **Gateway Hub:** Implementado em `Commands.h:596, 605-614` com a whitelist `allowedPumpCommands[] = { "reset_volume", "start", "stop" }`. Qualquer outro verbo disparado via `pump_command` gera aviso `ESP32_AVISO` e é descartado.
  - **Suíte de Testes Hub:** `ESP32S3-HUB/tests/contracts/test_node_commands.py:410-423` (`test_pump_command_whitelist_blocks_clear_nvs_and_config_verbs`).
  - **Aplicativo Flutter (`Android_app`):** O aplicativo não emite nem expõe verbos NVS destrutivos.
- **Justificativa Técnica de Não-Implementação de Mudanças:**
  - Já 100% implementado e validado em nível de gateway. O Hub atua como barreira perimetral contra comandos destrutivos. Manter o handler no firmware é necessário exclusivamente para manutenção de bancada por cabo serial sem regravação do chip. Nenhuma alteração é requerida.

---

### Item 1.10.6: Retomada Autônoma de Perfil Pós-Queda de Energia (Checkpoint NVS a cada 60 s)
- **Referência na Especificação:** §1.10 Item 6 / F-110-06
- **Citação Textual Autoritativa:**
  > *"Retomada automática após queda de energia | Mantida (batelada longa); registrar no procedimento de energização | `s_cvol` entra no checkpoint para a retomada não quebrar o ciclo"*
- **Status Atual no Código-Fonte:**
  - **Firmware Nó:** Implementado em `src/storage/RuntimeStateStore.h:1-56` (`saveRuntimeState()` salva a cada 60 s em `OP_RUNNING`: `s_active`, `s_vol`, `s_time`, `s_mode`, `s_cvol`; `checkAndRecoverState()` no boot reconstitui relógio retroativo, volume de sessão e marco do ciclo).
  - **Gateway Hub:** Transparente; restabelece `PumpOnline = true` e `PumpActive = true` assim que o nó restabelece Wi-Fi e envia `/pumpData`.
  - **Aplicativo Flutter (`Android_app`):** Atualiza telemetria automaticamente via polling em `/readData`.
- **Justificativa Técnica de Não-Implementação de Mudanças:**
  - A funcionalidade é nativa e autônoma do firmware do nó, essencial para a integridade de bioprocessos e bateladas alimentadas de dias de duração. Alterações no software de supervisão são desnecessárias, bastando a devida instrução operacional no manual de bancada.

---

### Item 1.10.7: Porta do Sensor de Presença de Líquido no Pino 15 Invisível ao App
- **Referência na Especificação:** §1.10 Item 7 / F-110-07
- **Citação Textual Autoritativa:**
  > *"Porta do sensor invisível ao app | Adiado | Sem mudança; `sensorEnable`/`sensorBypass` continuam só locais"*
- **Status Atual no Código-Fonte:**
  - **Firmware Nó:** Entrada digital GPIO 15 (`SENSOR_PIN`) com debounce de 50 ms (`SensorAndConversion.h:1-19`), botão físico em GPIO 32 e LED de status em GPIO 33. Variáveis `sensorEnable` e `sensorWetState` omitidas propositalmente da query string em `HubClient.h`.
  - **Gateway Hub:** Chaves `sensorEnable` e `sensorBypass` não constam na lista de comandos aceitos em `Commands.h:588-592`.
  - **Aplicativo Flutter (`Android_app`):** Não possui suporte nem referências ao sensor de líquido.
- **Justificativa Técnica de Não-Implementação (Decisão Arquitetural Fechada D-SEC-02):**
  - O sensor de líquido no pino 15 foi concebido como **intertravamento físico local de bancada**. Seu acionamento transfere a autoridade de dosagem aos potenciômetros analógicos e impede o acionamento remoto de perfis. Expor seu controle remoto ao Hub e ao aplicativo criaria um bypass de segurança que anularia a proteção física local do operador. A não-exposição é uma diretriz de segurança fechada.

---

### Item 1.10.8: Eco Bidirecional e Ajuste Dinâmico de PID de Volume
- **Referência na Especificação:** §1.10 Item 8 / F-110-08
- **Citação Textual Autoritativa:**
  > *"PID sem eco, UI travada | Ecoar e receber PID; padrões no app | `kp/ki/kd` no push e no `/readData`; Hub `PumpPidKp/Ki/Kd`; app libera edição com eco, persiste ao eco, padrões 0,5/0,05/0,001"*
- **Status Atual no Código-Fonte:**
  - **Firmware Nó:** `OperationController.h:345-350` lê `pid_kp`, `pid_ki`, `pid_kd`, aciona `g_configDirty = true` e grava na NVS (`ConfigStore.h:50`). `HubClient.h:185-187` transmite `&kp=...&ki=...&kd=...` no push a 1 Hz.
  - **Gateway Hub:** `HttpServer.h:376-378` lê parâmetros e armazena em `pumpPidKp/Ki/Kd`. `Telemetry.h:341-343` expõe no snapshot JSON. `Commands.h:617-619` traduz `pumpPidK*` para `pid_k*`. Testado em `test_node_commands.py:434`.
  - **Aplicativo Flutter (`Android_app`):** **Omissão:** `PeristalticPumpState` não possui campos de PID, `DeviceControlProvider` não implementa envio e `controls_screen.dart` não possui interface de sintonia.
- **Plano de Ação Técnico:**
  - **Componente:** Aplicativo Flutter (`Android_app`).
  - **Arquivo 1:** `Android_app/lib/models/peristaltic_pump_state.dart`
    - Adicionar campos: `final double? pidKp;`, `final double? pidKi;`, `final double? pidKd;` e getter `bool get hasPidEcho => pidKp != null && pidKi != null && pidKd != null;`.
  - **Arquivo 2:** `Android_app/lib/providers/device_control_provider.dart`
    - Implementar:
      ```dart
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
  - **Arquivo 3:** `Android_app/lib/screens/controls_screen.dart`
    - Adicionar `ExpansionTile` "Sintonia de PID do Nó (Volume)" contendo 3 campos numéricos (Kp, Ki, Kd), pré-carregados com os padrões nominais 0.500 / 0.050 / 0.001, habilitados apenas quando `pumpState.hasPidEcho` for verdadeiro e com botão de envio disparando `setPumpPid`.

---

### Item 1.10.9: Latência de Comunicação (0–2 s) e Semântica Latest-Wins na `pumpBox`
- **Referência na Especificação:** §1.10 Item 9 / F-110-09
- **Citação Textual Autoritativa:**
  > *"Latência 0–2 s + latest-wins na `pumpBox` | Manter; app já serializa por `PumpCommandPending` | —"*
- **Status Atual no Código-Fonte:**
  - **Firmware Nó:** Polling HTTP em `/pumpCommand` a cada 2000 ms (`FirmwareApp.cpp:53`, `Lifecycle.h:111-119`).
  - **Gateway Hub:** Gerencia `pumpBox` com profundidade 1 em `Mailboxes.h:22-52` (`queueReliable` sobrescreve comandos não consumidos; `takeReliable` entrega ao nó; `ackReliable` fecha a caixa).
  - **Aplicativo Flutter (`Android_app`):** **Brecha de Interface:** Embora `PeristalticPumpState:85` leia `PumpCommandPending`, o botão "Apply Feed Profile" em `controls_screen.dart:1504` não checa `pumpState.isCommandPending` antes do envio, permitindo disparos sobrepostos.
- **Plano de Ação Técnico:**
  - **Componente:** Aplicativo Flutter (`Android_app`).
  - **Arquivo 1:** `Android_app/lib/screens/controls_screen.dart:1504-1520`
    - Atualizar a guarda do botão de aplicação de perfil:
      ```dart
      onApply: (_pumpValidationError == null && !pumpState.isCommandPending && !control.isBusy)
          ? () async { ... }
          : null,
      ```
  - **Firmware e Hub:** Comportamento e arquitetura mantidos integralmente.

---

### Item 1.10.10: Comportamento de Ativação Ociosa com Comando `start` e `mode=0`
- **Referência na Especificação:** §1.10 Item 10 / F-110-10
- **Citação Textual Autoritativa:**
  > *"`start` com `mode = 0` marca `active` sem bombear | Manter (app não usa `start`) | —"*
- **Status Atual no Código-Fonte:**
  - **Firmware Nó:** `OperationController.h:242-250` transiciona para `OP_RUNNING` ao receber `{"command":"start"}`. Se `mode == 0`, a vazão calculada é 0 mL/min, gerando `active = 1` sem rotação física do motor.
  - **Gateway Hub:** Whitelist aceita `start`, repassando para a `pumpBox`.
  - **Aplicativo Flutter (`Android_app`):** Conforme. `applyPumpProfile` em `DeviceControlProvider.dart:312-350` despacha perfis diretamente com `mode: 1..5`, e a parada é feita com `{"mode": 0}`. A string `"start"` nunca é utilizada.
- **Justificativa Técnica de Não-Implementação de Mudanças:**
  - O aplicativo não faz uso do comando `"start"`, interagindo exclusivamente através de definição explícita de modos de dosagem. O comportamento do firmware é mantido por retrocompatibilidade com consoles seriais de bancada sem qualquer impacto operacional.

---

### Item 1.10.11: Tipologia do Atuador Mecânico: Motor DC Escovado em Ponte H vs Stepper
- **Referência na Especificação:** §1.10 Item 11 / F-110-11
- **Citação Textual Autoritativa:**
  > *"Motor: CAD com stepper | É DC (confirmado) | Doc corrigida (§1.1)"*
- **Status Atual no Código-Fonte:**
  - **Firmware Nó:** Utiliza saídas direcionadas para ponte H BTS7960 nos pinos GPIO 14 (`R_PWM`) e GPIO 27 (`L_PWM`), habilitados por GPIO 25/26 (`PwmRuntime.h:1-60`). Sem bibliotecas de passo.
  - **Documentação:** Corrigida em §1.1 e §1.10.
  - **Aplicativo Flutter (`Android_app`):** **Inconsistência de Rotulagem:** `peristaltic_pump_state.dart:30, 165` rotula a velocidade como `"RPM"`:
    ```dart
    String get formattedSpeed => isConnectedAndActive ? "${speed.toStringAsFixed(1)} RPM" : "-- RPM";
    ```
    Como o motor é DC sem sensor de feedback de rotação, o valor exibido é a velocidade comandada em **unidades de controle $S \in [-1000, 1000]$**, e não uma contagem tacométrica em RPM.
- **Plano de Ação Técnico:**
  - **Componente:** Aplicativo Flutter (`Android_app`).
  - **Arquivo 1:** `Android_app/lib/models/peristaltic_pump_state.dart`
    - Corrigir o getter `formattedSpeed`:
      ```dart
      String get formattedSpeed =>
          isConnectedAndActive ? "${speed.toStringAsFixed(0)} S" : "--";
      ```
    - Corrigir a documentação do campo `speed` nos comentários da classe.

---

### Item 1.10.12: Potenciômetro "Gain" como Sentido e Escalonamento Bipolar Centrado
- **Referência na Especificação:** §1.10 Item 12 / F-110-12
- **Citação Textual Autoritativa:**
  > *"Potenciômetro 'gain' | É sentido e ganho sobre a velocidade do outro knob | Doc corrigida (§1.1)"*
- **Status Atual no Código-Fonte:**
  - **Firmware Nó:** Implementado em `src/control/OperationController.h:184-204` (`calcPotSpeed()` calcula fator bipolar $\text{dir} = \frac{\text{ADC} - 2047}{2047} \in [-1, 1]$ escalando o valor lido no knob de magnitude).
- **Justificativa Técnica de Não-Implementação de Mudanças:**
  - O comportamento eletromecânico e a equação do divisor analógico estão verificados e consolidados no código-fonte. Trata-se de operação exclusiva do modo manual local de bancada física.

---

### Decisões Arquiteturais Fechadas e Riscos Sistêmicos Adicionais

#### D-SEC-01: Diretriz de Calibração: Curva Linear ($R^2 \ge 0,98$) vs Lookup Table
- **Citação Textual Autoritativa (§1.0 e §1.3):**
  > *"A bomba peristáltica tem resposta predominantemente linear ($R^2 > 0,99$). A calibração assistida do app calcula a reta e resíduos sobre múltiplos pontos ($S = 250, 500, 1000$). Pequenos desvios dinâmicos são corrigidos pelo PID de volume do nó. Diretriz: manter calibração linear atual; só evoluir para tabela de lookup se a bancada física demonstrar $R^2 < 0,98$."*
- **Justificativa Técnica de Não-Implementação:** Decisão arquitetural congelada. O firmware v3.10 mantém estritamente o modelo linear $Q = a \cdot S + b$.

#### D-SEC-02: Intertravamento de Segurança Local por Presença de Líquido e Prioridade Manual
- **Citação Textual Autoritativa (§1.0 e §1.1):**
  > *"Modo de segurança e operação física ativado por botão no hardware. Com o botão acionado, a bomba opera exclusivamente em modo manual local (liga/desliga por contato com líquido, na velocidade e sentido dos potenciômetros) e não deve receber comandos externos de perfil."*
- **Justificativa Técnica de Não-Implementação:** Decisão de segurança homologada. A porta do sensor permanece puramente local, sem exposição ao Hub ou App.

#### D-SEC-03: Semeadura Pseudoaleatória de `cmd_id` Pós-Reboot no Hub
- **Citação Textual Autoritativa (§1.11 #10):**
  > *"Semeadura de `cmd_id`, Hub 2026-09-12: impede que o primeiro comando após reboot do Hub colida com o último ID recebido pelo nó."*
- **Justificativa Técnica de Não-Implementação:** 100% implementado no Hub (`Mailboxes.h:6-18`).

#### D-SEC-04: Resiliência Numérica e Proteção contra Divisão por Zero com Slope Nulo
- **Citação Textual Autoritativa (§1.2):**
  > *"Com `|slope| < 1e-6` o firmware devolve S = 0 (não divide por zero) — uma calibração com slope zero para a bomba em qualquer perfil."*
- **Justificativa Técnica de Não-Implementação:** 100% implementado em `SensorAndConversion.h:23`. O app deve validar previamente coeficientes nulos antes do envio.

---

## SEÇÃO 2: PROTOCOLOS E CHECKLIST DE BANCADA FÍSICA DA SEÇÃO 1.11

Esta seção detalha os 11 procedimentos de ensaio físico descritos em §1.11 de `COMANDOS_DISPOSITIVOS_EXTERNOS.md`, fornecendo para cada um o arranjo físico, precauções de segurança, protocolo passo a passo, critérios de aceitação e dependências de software.

---

### Item 1.11.1: Conferência do Sentido Físico de Rotação (Fluxo para o Vaso)
- **Título & Objetivo:** Comprovar experimentalmente que a velocidade com sinal positivo ($S > 0$) e comandos de perfis de dosagem acionam o cabeçote no sentido que conduz o fluido do reservatório de alimentação em direção ao vaso do biorreator.
- **Pré-requisitos, Arranjo Físico e Segurança:**
  - Nó da bomba energizado com fonte DC 12V/24V conectada à ponte H BTS7960.
  - Mangueira de silicone montada no rotor de 5 roletes do cabeçote peristáltico Watson-Marlow.
  - Reservatório com água destilada na sucção e proveta graduada na descarga.
  - Luvas de proteção e óculos de segurança. Cuidado com partes móveis do rotor giratório.
- **Protocolo de Ensaio Passo a Passo:**
  1. Habilitar a comunicação da bomba (`pumpComm: 1`).
  2. Enviar pulso manual via app ou terminal: `{"pump_speed": 500, "pump_speed_ms": 10000}`.
  3. Observar visualmente o deslocamento da coluna líquida na mangueira.
- **Critérios de Aceitação (Pass/Fail):**
  - **Aprovado (Pass):** O rotor gira no sentido horário/anti-horário mecânico que aspira líquido do frasco de alimentação e o expele em direção ao vaso. Sinal elétrico de PWM ativo no pino `R_PWM` (GPIO 14) e nível zero em `L_PWM` (GPIO 27).
  - **Reprovado (Fail):** O fluido é aspirado do vaso em direção ao frasco de reagentes.
- **Resolução de Inconsistência:**
  - Se invertido, realizar a inversão física dos cabos do motor nos bornes OUT1 e OUT2 da ponte H BTS7960. Caso a fiação seja soldada e inacessível, aplicar flag lógica `MOTOR_DIR_INVERTED` em `PwmRuntime.h:14`.

---

### Item 1.11.2: Tempo de Resposta e Detecção de Queda de Presença (≤ 4 s)
- **Título & Objetivo:** Validar o mecanismo de *heartbeat* e o limiar de alarme de desconexão no Hub e aplicativo supervisor.
- **Pré-requisitos, Arranjo Físico e Segurança:**
  - Central Hub ligada e sincronizada com o aplicativo.
  - Nó da bomba ligado na fonte de alimentação e conectado à rede Wi-Fi do Hub.
- **Protocolo de Ensaio Passo a Passo:**
  1. No aplicativo, ativar o interruptor mestre da bomba (`pumpComm: 1`).
  2. Cronometrar o tempo decorrido até o chip `PumpOnline` transicionar para ativo (verde).
  3. Desconectar o plugue P4 de alimentação da bomba, simulando desligamento abrupto.
  4. Cronometrar o tempo até o chip `PumpOnline` transicionar para inativo (vermelho/âmbar).
- **Critérios de Aceitação (Pass/Fail):**
  - **Aprovado (Pass):**
    - `PumpOnline = true` ativado em tempo $\le 4,0\text{ s}$ após o acionamento de `pumpComm`.
    - Ao desconectar a alimentação, `PumpOnline = false` publicado pelo Hub em $\le 4,0\text{ s}$.
    - Aplicativo exibe aviso de desconexão da bomba.
  - **Reprovado (Fail):** A presença demora mais de 4 s para ser reconhecida ou o estado online persiste por mais de 4 s após o corte elétrico.
- **Dependências de Software:** Totalmente atendido pelo Hub (`AppContext.h:366`, `PUMP_TIMEOUT = 4000`).

---

### Item 1.11.3: Calibração Volumétrica Assistida Multiponto e Verificação Contínua de 10 min
- **Título & Objetivo:** Executar ensaio metrológico com água para determinação da reta linear de conversão ($Q = a \cdot S + b$), cálculo de resíduos e coeficiente de determinação ($R^2 \ge 0,99$), e validação de precisão em perfil contínuo de 10 minutos.
- **Pré-requisitos, Arranjo Físico e Segurança:**
  - Proveta de vidro classe A de 100 mL (resolução de 0,5 mL) ou balança analítica com resolução de 0,01 g.
  - Mangueira escorvada (sem bolhas de ar).
- **Protocolo de Ensaio Passo a Passo:**
  1. Executar corrida de 60 s em $S = 250$; registrar volume medido $V_1$.
  2. Executar corrida de 60 s em $S = 500$; registrar volume medido $V_2$.
  3. Executar réplica de 60 s em $S = 500$; registrar volume medido $V_3$ (calcular média em 500).
  4. Executar corrida de 60 s em $S = 1000$; registrar volume medido $V_4$.
  5. Calcular a regressão linear por mínimos quadrados, determinando `slope`, `intercept` e $R^2$.
  6. Aplicar os coeficientes no nó (`pumpSlope`, `pumpIntercept`) e aguardar o eco na telemetria.
  7. Iniciar perfil de teste contínuo com vazão constante de 10 mL/min durante 10 minutos (`final_t = 10`).
  8. Ao término, medir o volume acumulado na proveta e comparar com `PumpVol`.
- **Critérios de Aceitação (Pass/Fail):**
  - **Aprovado (Pass):** Coeficiente de determinação $R^2 \ge 0,990$. Erro relativo entre o volume real da proveta e o reportado por `PumpVol` após 10 minutos inferior a $\pm 3\%$.
  - **Reprovado (Fail):** $R^2 < 0,980$ ou discrepância superior a $3\%$ no volume final integrado.
- **Dependências de Software:** Exige implementação da tela de calibração volumétrica no aplicativo Flutter (`Android_app`).

---

### Item 1.11.4: Liberação e Comutação dos Potenciômetros de Bancada (`pump_pot: 1/0`)
- **Título & Objetivo:** Comprovar a capacidade de devolver a autoridade de controle aos knobs analógicos de velocidade e sentido após acionamentos remotos via software.
- **Pré-requisitos, Arranjo Físico e Segurança:**
  - Nó da bomba energizado, operador posicionado junto aos botões manuais na bancada.
- **Protocolo de Ensaio Passo a Passo:**
  1. Enviar um comando de acionamento manual remoto (`pump_speed: 400`).
  2. Verificar que o motor gira na velocidade comandada e que os giros nos botões físicos são ignorados.
  3. No aplicativo, acionar o comando de devolução de potenciômetros (`pump_pot: 1`).
  4. Confirmar o eco de `PumpPotEnabled: true` na telemetria.
  5. Girar o botão `POT_INT` (pino 34) e verificar se o motor responde imediatamente à rotação manual.
  6. No aplicativo, enviar `pump_pot: 0` e certificar que os knobs voltam a ficar travados.
- **Critérios de Aceitação (Pass/Fail):**
  - **Aprovado (Pass):** Com `pump_pot: 1`, `PumpPotEnabled` atualiza para `true` em $\le 3\text{ s}$ e a rotação obedece instantaneamente aos potenciômetros analógicos. Com `pump_pot: 0`, a rotação congela.
  - **Reprovado (Fail):** Os botões manuais continuam sem resposta após `pump_pot: 1`.
- **Dependências de Software:** Exige adição do método `setPumpPotentiometers` e botão na interface do Flutter (`Android_app`).

---

### Item 1.11.5: Corte Autônomo de Emergência por Timeout (`pump_speed_ms`)
- **Título & Objetivo:** Validar que o microcontrolador do nó cessa a rotação do motor por decisão autônoma caso ocorra queda de comunicação durante uma dosagem manual.
- **Pré-requisitos, Arranjo Físico e Segurança:**
  - Bomba dosando água em circuito fechado.
- **Protocolo de Ensaio Passo a Passo:**
  1. Despachar comando manual com prazo fixo: `{"pump_speed": 600, "pump_speed_ms": 6000}` (6 segundos).
  2. No instante $t = 2\text{ s}$, desconectar bruscamente o cabo de rede do Hub ou desligar o Wi-Fi do computador de comando.
  3. Observar visualmente o motor no instante $t = 6\text{ s}$.
- **Critérios de Aceitação (Pass/Fail):**
  - **Aprovado (Pass):** O motor desliga rigorosamente ao transcorrerem os 6000 ms no relógio local do ESP32, mesmo na ausência completa de pacotes na rede. Log serial exibe: `[CMD] speed_ms elapsed; motor stopped.`.
  - **Reprovado (Fail):** O motor permanece girando indefinidamente após os 6 segundos.
- **Dependências de Software:** Firmware v3.10 e Hub 10.2 já atendem integralmente.

---

### Item 1.11.6: Parada Autônoma por `final_t`, Retenção de `PumpVol` e Apuração de `PumpCycleVol`
- **Título & Objetivo:** Comprovar a finalização autônoma de perfis de dosagem por término de tempo programado, a preservação do volume total da sessão e a correta segregação do volume do ciclo.
- **Pré-requisitos, Arranjo Físico e Segurança:**
  - Bomba pronta para dosagem de 5 minutos.
- **Protocolo de Ensaio Passo a Passo:**
  1. Configurar e enviar perfil constante com vazão de 10 mL/min, `init_t = 0` e `final_t = 5` minutos.
  2. Aguardar a execução completa dos 5 minutos.
  3. Constatar a parada do motor e inspecionar a telemetria reportada.
  4. Disparar o comando `{"pump_command": "reset_volume"}` e registrar os valores de volume.
- **Critérios de Aceitação (Pass/Fail):**
  - **Aprovado (Pass):**
    - Aos 5 min exatos, o motor cessa o giro; `PumpActive` transiciona para `false` e `PumpMode` retorna para `0`.
    - `PumpVol` mantém retido o valor total da sessão ($\approx 50\text{ mL}$).
    - `PumpCycleVol` reporta estritamente o volume dosado naquele ciclo ($\approx 50\text{ mL}$).
    - Após o envio de `reset_volume`, tanto `PumpVol` quanto `PumpCycleVol` retornam a zero ($< 0,05\text{ mL}$).
  - **Reprovado (Fail):** O volume é zerado prematuramente na parada de 5 min ou não zera após `reset_volume`.
- **Dependências de Software:** Exige mapeamento de `PumpCycleVol` e botão de `reset_volume` no Flutter (`Android_app`).

---

### Item 1.11.7: Execução Sequencial de Múltiplos Perfis sem Zeramento de Volume
- **Título & Objetivo:** Assegurar que múltiplos perfis disparados em sequência acumulem monotonicamente o volume da sessão e comprovem que a malha fechada do PID opera isoladamente sobre o ciclo ativo.
- **Pré-requisitos, Arranjo Físico e Segurança:**
  - Tubulação montada e escorvada.
- **Protocolo de Ensaio Passo a Passo:**
  1. Enviar Perfil 1 (ex: 2 min a 10 mL/min $\rightarrow$ volume previsto de 20 mL).
  2. Aguardar o término do Perfil 1; verificar `PumpVol` ($\approx 20\text{ mL}$) e `PumpCycleVol` ($\approx 20\text{ mL}$).
  3. **Sem disparar `reset_volume`**, enviar Perfil 2 (ex: 2 min a 10 mL/min $\rightarrow$ volume previsto de 20 mL).
  4. Observar o log serial do nó (`[DEBUG] TgtV: ... ActV: ...`) e a telemetria do segundo ciclo.
- **Critérios de Aceitação (Pass/Fail):**
  - **Aprovado (Pass):**
    - `PumpVol` parte de 20 mL e atinge $\approx 40\text{ mL}$ ao término do segundo ciclo.
    - `PumpCycleVol` reinicia em 0 mL no início do Perfil 2 e finaliza em $\approx 20\text{ mL}$.
    - O termo integrador do PID do Perfil 2 não sofre saturação decorrente do volume entregue no Perfil 1.
  - **Reprovado (Fail):** `PumpVol` é truncado ou o PID satura no início do segundo perfil.
- **Dependências de Software:** Totalmente atendido pelo firmware v3.10 (`OperationController.h:63-69`).

---

### Item 1.11.8: Transmissão, Eco e Retenção NVS dos Ganhos de PID
- **Título & Objetivo:** Comprovar a integridade da parametrização de ganhos PID pela rede, validação de eco e persistência não-volátil entre ciclos de desligamento.
- **Pré-requisitos, Arranjo Físico e Segurança:**
  - Bomba conectada ao Hub e supervisor.
- **Protocolo de Ensaio Passo a Passo:**
  1. No aplicativo, enviar ganhos distintos dos padrões de fábrica: $K_p = 0,85$, $K_i = 0,080$, $K_d = 0,005$.
  2. Cronometrar o tempo até o retorno dos ecos na telemetria.
  3. Desligar a alimentação da placa ESP32 da bomba, aguardar 10 segundos e religar.
  4. Ler a telemetria do primeiro push após a inicialização.
- **Critérios de Aceitação (Pass/Fail):**
  - **Aprovado (Pass):** Ecos `PumpPidKp/Ki/Kd` atualizam na interface em tempo $\le 3\text{ s}$. Após o reinício por corte elétrico, a bomba carrega da NVS e reporta os mesmos valores sintonizados.
  - **Reprovado (Fail):** Ecos não retornam ou o nó restaura padrões de fábrica no boot.
- **Dependências de Software:** Exige adição dos campos de PID no Flutter (`Android_app`).

---

### Item 1.11.9: Ensaio de Retomada Autônoma Pós-Queda de Alimentação (Robust Recovery)
- **Título & Objetivo:** Validar a funcionalidade de recuperação autônoma de perfil ativo pós-reset elétrico inesperado e colher a homologação formal da equipe de bioprocesso.
- **Pré-requisitos, Arranjo Físico e Segurança:**
  - Bomba dosando líquido em bancada sob perfil contínuo longo.
  - Atenção: o motor poderá religar sozinho ao restaurar a energia. Não colocar os dedos no rotor!
- **Protocolo de Ensaio Passo a Passo:**
  1. Iniciar perfil de dosagem de 30 minutos a 10 mL/min.
  2. Aguardar 90 segundos de dosagem contínua (garantindo a gravação de pelo menos um checkpoint NVS aos 60 s).
  3. Desligar bruscamente o disjuntor de alimentação da bancada no instante $t \approx 3\text{ min}$.
  4. Aguardar 15 segundos e religar a alimentação.
  5. Observar o log serial de inicialização e o comportamento físico do rotor.
- **Critérios de Aceitação (Pass/Fail):**
  - **Aprovado (Pass):** Log de boot exibe: `>>> DETECTED UNEXPECTED RESET! RECOVERING STATE <<<`. O motor volta a girar imediatamente sem intervenção do operador, reconstituindo o perfil ativo, o tempo decorrido e o volume acumulado da sessão com defasagem temporal inferior a 60 segundos.
  - **Reprovado (Fail):** O nó acorda em `OP_IDLE` com motor parado ou perde o volume de sessão acumulado.
- **Dependências de Software:** Nenhuma (suportado de forma autônoma pelo firmware v3.10 em `RuntimeStateStore.h`).

---

### Item 1.11.10: Injeção de Comando Pós-Reboot do Hub (Semeadura de `cmd_id`)
- **Título & Objetivo:** Comprovar que o mecanismo de semeadura pseudoaleatória da base de `cmd_id` no Hub elimina colisões de comandos confiáveis e garante a entrega imediata de instruções à bomba após o reinício da central.
- **Pré-requisitos, Arranjo Físico e Segurança:**
  - Central Hub e nó da bomba ligados e sincronizados.
- **Protocolo de Ensaio Passo a Passo:**
  1. Enviar comandos para a bomba garantindo que `ack_cmd_id` na telemetria seja maior que zero.
  2. Pressionar o botão físico `EN`/Reset no ESP32-S3 do Hub ou disparar comando HTTP `/reboot`.
  3. Manter o nó da bomba continuamente ligado na tomada durante todo o procedimento.
  4. Assim que o Hub restabelecer o rádio Wi-Fi e a telemetria, despachar imediatamente o comando **Zerar Volume Acumulado** (`reset_volume`).
- **Critérios de Aceitação (Pass/Fail):**
  - **Aprovado (Pass):** O comando é executado com sucesso no nó da bomba na primeira tentativa, zerando o volume acumulado em $\le 3\text{ s}$ e atualizando `ack_cmd_id` para um novo valor na faixa aleatória ($> 100.000.000$).
  - **Reprovado (Fail):** O comando é silenciosamente descartado pelo nó da bomba por duplicidade de ID.
- **Dependências de Software:** Totalmente atendido pelo Hub (`Mailboxes.h:6-18`).

---

### Item 1.11.11: Resposta do Sensor de Presença de Líquido e Travamento de Bancada
- **Título & Objetivo:** Validar o circuito elétrico, lógica de debounce (50 ms) e trava mecânica mínima (500 ms) do sensor de presença de líquido conectado ao pino 15.
- **Pré-requisitos, Arranjo Físico e Segurança:**
  - Terminal de comando de bancada acessando a rede direta `FeedPump` (`http://192.168.6.1/command`) ou console serial USB a 115200 bps.
  - Eletrodo sensor conectado ao pino GPIO 15. Copo com água destilada.
- **Protocolo de Ensaio Passo a Passo:**
  1. Enviar comando local: `{"sensorEnable": 1, "sensorBypass": 0, "speed": 500}` com o eletrodo seco no ar.
  2. Verificar se o motor permanece completamente parado e se o LED indicador (GPIO 33) está aceso.
  3. Mergulhar a ponta do eletrodo na água.
  4. Cronometrar o tempo de partida e observar a estabilidade de rotação do motor.
  5. Retirar o eletrodo da água e certificar a parada do motor.
- **Critérios de Aceitação (Pass/Fail):**
  - **Aprovado (Pass):** Com o eletrodo seco, o motor não gira sob nenhuma hipótese. Ao submergir na água, o motor parte suavemente em $\le 50\text{ ms}$ e mantém a rotação por pelo menos 500 ms (trava anti-chattering `MIN_MOTOR_ON_TIME_MS`). Ao secar o sensor, o motor para imediatamente.
  - **Reprovado (Fail):** O motor gira com o sensor seco ou apresenta trepidação (*chattering*) na transição de contato.
- **Dependências de Software:** Suportado nativamente pelo firmware v3.10 (`SensorAndConversion.h:1-19`, `Lifecycle.h:174-202`).

---

## SEÇÃO 3: MATRIZ DE RASTREABILIDADE, CONTRATOS DE FIO E ROADMAP DE EXECUÇÃO

### 1. Matriz Cruzada Global de Rastreabilidade Técnica

A tabela a seguir estabelece o mapeamento completo e o status individual de cada um dos 12 itens de auditoria (§1.10) e 11 itens de bancada (§1.11), consolidando a situação em todas as camadas de software e hardware.

| Item | Descrição / Tópico | Firmware Nó (v3.10) | Gateway Hub (v10.2) | App Flutter (`Android_app`) | Bancada Física | Resolução / Ação Requerida |
|---|---|:---:|:---:|:---:|:---:|---|
| **§1.10 #1** | Volume e vazão estimados | 🟢 Conforme | 🟢 Conforme | 🔴 Lacuna | 🟢 Metrologia | Adicionar aviso no card e criar módulo de calibração no Flutter |
| **§1.10 #2** | `mode:0` / Retenção de volume | 🟢 Conforme | 🟢 Conforme | 🔴 Desvio | 🟢 Ensaio | **Corrigir `stopPump()` para `{"mode": 0}`**; criar `resetPumpVolume()` |
| **§1.10 #3** | Desbloqueio dos potenciômetros | 🟢 Conforme | 🟢 Conforme | 🔴 Lacuna | 🟢 Knobs | Implementar `setPumpPotentiometers` e botão alternador na UI |
| **§1.10 #4** | Parada autônoma `speed_ms` | 🟢 Conforme | 🟢 Conforme | 🔴 Lacuna | 🟢 Ensaio | Implementar `setPumpManualSpeed(S, durationMs)` no provider |
| **§1.10 #5** | Bloqueio `clear_nvs` no Hub | 🟢 Seguro | 🟢 Conforme | 🟢 Seguro | 🟢 Serial | Manter whitelist em `Commands.h`. Nenhuma alteração necessária |
| **§1.10 #6** | Retomada autônoma pós-queda | 🟢 Conforme | 🟢 Conforme | 🟢 Conforme | 🟢 Ensaio | Checkpoint NVS `s_cvol` funcional. Registrar no manual |
| **§1.10 #7** | Sensor no pino 15 invisível | 🟢 Conforme | 🟢 Conforme | 🟢 Conforme | 🟢 Eletrodo | Decisão fechada D-SEC-02. Manter estritamente local |
| **§1.10 #8** | Eco e sintonia de PID | 🟢 Conforme | 🟢 Conforme | 🔴 Lacuna | 🟢 Ensaio | Mapear `pidKp/Ki/Kd` no modelo, criar método e expansor na UI |
| **§1.10 #9** | Latência e latest-wins | 🟢 Conforme | 🟢 Conforme | 🟡 Ajuste | 🟢 Ensaio | Adicionar trava `!pumpState.isCommandPending` no botão Apply |
| **§1.10 #10**| `start` com `mode=0` | 🟢 Mantido | 🟢 Conforme | 🟢 Conforme | 🟢 Ensaio | Manter; app não emite `start`. Proposta de guarda documentada |
| **§1.10 #11**| Motor DC escovado em ponte H | 🟢 Conforme | 🟢 Conforme | 🔴 Desvio | 🟢 Hardware | Corrigir docstring e label de unidade de velocidade para "S" |
| **§1.10 #12**| Potenciômetro bipolar "Gain" | 🟢 Conforme | 🟢 Conforme | 🟢 Conforme | 🟢 Knobs | Funcionalidade matemática documentada e homologada |
| **D-SEC-01** | Diretriz linear vs lookup | 🟢 Conforme | 🟢 Conforme | 🟢 Conforme | 🟢 Ensaio | Manter modelo linear ($R^2 \ge 0,98$) |
| **D-SEC-02** | Prioridade do sensor local | 🟢 Conforme | 🟢 Conforme | 🟢 Conforme | 🟢 Eletrodo | Intertravamento puramente físico local de bancada |
| **D-SEC-03** | Semeadura pós-reboot de Hub | 🟢 Conforme | 🟢 Conforme | 🟢 Conforme | 🟢 Ensaio | Semeadura aleatória de `cmd_id` implementada em `Mailboxes.h` |
| **D-SEC-04** | Resiliência numérica divisor | 🟢 Conforme | 🟢 Conforme | 🟢 Conforme | 🟢 N/A | Guarda de divisão por zero ativa em `SensorAndConversion.h` |
| **§1.11 #1** | Sentido físico de rotação | 🟢 Conforme | 🟢 Conforme | 🟢 Conforme | 🟡 Ensaio | Ensaio com água. Se invertido, inverter bornes da ponte H |
| **§1.11 #2** | Queda de link em $\le 4\text{ s}$ | 🟢 Conforme | 🟢 Conforme | 🟢 Conforme | 🟡 Ensaio | Cronometrar desconexão de fonte DC com alarme no Hub/App |
| **§1.11 #3** | Calibração volumétrica 10 min| 🟢 Conforme | 🟢 Conforme | 🔴 Lacuna | 🟡 Ensaio | Executar ensaio multiponto com balança e proveta |
| **§1.11 #4** | Liberação dos knobs | 🟢 Conforme | 🟢 Conforme | 🔴 Lacuna | 🟡 Ensaio | Testar devolução com `pump_pot: 1` e rotação física |
| **§1.11 #5** | Corte de emergência `speed_ms`| 🟢 Conforme | 🟢 Conforme | 🔴 Lacuna | 🟡 Ensaio | Cortar cabo/Wi-Fi durante acionamento com prazo |
| **§1.11 #6** | Perfil 5 min e retenção `vol` | 🟢 Conforme | 🟢 Conforme | 🔴 Lacuna | 🟡 Ensaio | Verificar parada por `final_t` e zeramento com `reset_volume` |
| **§1.11 #7** | Múltiplos perfis contínuos | 🟢 Conforme | 🟢 Conforme | 🟢 Conforme | 🟡 Ensaio | Validar acúmulo de `PumpVol` e erro do PID isolado no ciclo |
| **§1.11 #8** | Persistência de PID em NVS | 🟢 Conforme | 🟢 Conforme | 🔴 Lacuna | 🟡 Ensaio | Enviar PID, reiniciar o ESP32 e conferir eco no boot |
| **§1.11 #9** | Retomada autônoma pós-queda | 🟢 Conforme | 🟢 Conforme | 🟢 Conforme | 🟡 Ensaio | Cortar energia aos 3 min de perfil e conferir retorno |
| **§1.11 #10**| Comando pós-reboot do Hub | 🟢 Conforme | 🟢 Conforme | 🟢 Conforme | 🟡 Ensaio | Reiniciar o Hub e enviar `reset_volume` imediato |
| **§1.11 #11**| Ensaio do sensor de líquido | 🟢 Conforme | 🟢 Conforme | 🟢 Conforme | 🟡 Ensaio | Testar chave no pino 15 com debounce de 50 ms e trava 500 ms |

---

### 2. Catálogo Consolidado de Contratos de Fio e Chaves JSON

#### A. Envelope de Comandos Despachados ao Hub (`POST /command`)
| Chave no Envelope | Tipo | Faixa Válida | Destino no Nó | Função e Efeito Operacional |
|---|---|---|---|---|
| `"pumpComm"` | Inteiro | `0` ou `1` | Roteamento Hub | Habilita (`1`) ou desliga (`0`) o tráfego de dados e comandos para a bomba |
| `"mode"` | Inteiro | `0..5` | `mode` | Define perfil (0 = Parada/IDLE, 1 = Constante, 2 = Linear, 3 = Exp, 4 = Poli, 5 = Partes) |
| `"pump_command"` | String | `"reset_volume"`, `"start"`, `"stop"` | `"command"` | Verbos de controle de processo autorizados pela whitelist do Hub |
| `"pump_pot"` | Inteiro | `0` ou `1` | `"pot"` | Libera (`1`) ou trava (`0`) o controle dos potenciômetros físicos de bancada |
| `"pump_speed"` | Inteiro | `-1000..1000` | `"speed"` | Velocidade manual imediata em passos internos $S$ (negativo = reverso) |
| `"pump_speed_ms"` | Inteiro | $\ge 1$ | `"speed_ms"` | Prazo limite em ms para o comando manual; ao expirar, nó desliga o motor |
| `"pumpSlope"` | Double | $> 0.0$ | `"pumpSlope"` | Coeficiente angular da reta volumétrica; grava imediatamente na NVS |
| `"pumpIntercept"`| Double | Qualquer | `"pumpIntercept"` | Coeficiente linear da reta volumétrica; grava imediatamente na NVS |
| `"pumpPidKp"` | Double | $\ge 0.0$ | `"pid_kp"` | Ganho proporcional do controlador de volume; ecoa no push a 1 Hz |
| `"pumpPidKi"` | Double | $\ge 0.0$ | `"pid_ki"` | Ganho integrador do controlador de volume; ecoa no push a 1 Hz |
| `"pumpPidKd"` | Double | $\ge 0.0$ | `"pid_kd"` | Ganho derivativo do controlador de volume; ecoa no push a 1 Hz |

#### B. Telemetria Publicada pelo Hub no Snapshot Agregado (`GET /readData`)
| Chave no JSON | Tipo | Unidade | Descrição e Interpretação |
|---|---|---|---|
| `"PumpOnline"` | Booleano | Flag | `true` se nó estiver conectado e emitir push em $\le 4\text{ s}$ |
| `"PumpCommEnabled"` | Booleano | Flag | Espelha o interruptor mestre `pumpComm` no Hub |
| `"PumpCommandPending"`| Booleano | Flag | `true` enquanto comando estiver aguardando confirmação `ack_cmd_id` do nó |
| `"PumpMode"` | Inteiro | Código | Modo de perfil ativo corrente no nó ($0..5$) |
| `"PumpPWM"` | Inteiro | $0..1023$ | Duty cycle efetivo de 10 bits aplicado à ponte H ($0$ ou $155..1023$) |
| `"PumpSpeed"` | Double | Passos $S$ | Velocidade comandada ao motor ($S \in [-1000, 1000]$) |
| `"PumpFlow"` | Double | mL/min | Vazão volumétrica estimada pela curva de calibração |
| `"PumpVol"` | Double | mL | **Volume total acumulado na sessão** (só zera via `reset_volume`) |
| `"PumpCycleVol"` | Double | mL | **Volume dosado exclusivamente no ciclo de perfil ativo** |
| `"PumpTargetVol"` | Double | mL | Volume analítico teórico esperado para o instante $t$ corrente |
| `"PumpActive"` | Booleano | Flag | `true` quando motor estiver em execução ativa de perfil (`OP_RUNNING`) |
| `"PumpWaiting"` | Booleano | Flag | `true` quando perfil estiver aguardando expirar tempo inicial `init_t` |
| `"PumpPotEnabled"` | Booleano | Flag | `true` quando potenciômetros manuais de bancada estiverem liberados |
| `"PumpSlope"` | Double | mL/(min·S) | Coeficiente angular gravado na NVS do nó |
| `"PumpIntercept"` | Double | mL/min | Coeficiente linear gravado na NVS do nó |
| `"PumpPidKp/Ki/Kd"`| Double | Ganhos | Ecos dos ganhos nominais de fábrica ou sintonizados pelo operador |

---

### 3. Roadmap Faseado de Implementação

```
┌───────────────────────────────────────────────────────────────────────────────────┐
│                           CRONOGRAMA DE EXECUÇÃO (ROADMAP)                        │
├───────────────────────────────────────────────────────────────────────────────────┤
│ FASE 1: CORREÇÃO E ADEQUAÇÃO DO APLICATIVO FLUTTER (Android_app)                  │
│ 1.1 Eliminar envio de "speed": 0 em stopPump() (correção de parada limpa).        │
│ 1.2 Implementar método resetPumpVolume() e botão com diálogo na UI.               │
│ 1.3 Implementar setPumpPotentiometers() e botão alternador de knobs físicos.      │
│ 1.4 Adicionar campos de telemetria v3.10 ao modelo PeristalticPumpState.          │
│ 1.5 Adicionar expansor de sintonia de PID com validação de eco na tela de controle│
│ 1.6 Corrigir rotulagem de unidade de velocidade de "RPM" para "S".                │
│ 1.7 Atualizar e expandir suíte de testes unitários peristaltic_pump_test.dart.    │
├───────────────────────────────────────────────────────────────────────────────────┤
│ FASE 2: DESENVOLVIMENTO DO ASSISTENTE DE CALIBRAÇÃO VOLUMÉTRICA                   │
│ 2.1 Criar tela dedicada PumpCalibrationScreen no aplicativo Flutter.              │
│ 2.2 Implementar rotina assistida em 3 pontos (S = 250, 500, 1000) com temporizador│
│ 2.3 Calcular regressão linear (slope, intercept, R²) e exibir resíduos.           │
│ 2.4 Despachar calibração via applyPumpCalibration e registrar recibo JSON.        │
├───────────────────────────────────────────────────────────────────────────────────┤
│ FASE 3: HOMOLOGAÇÃO FÍSICA E COMISSIONAMENTO EM BANCADA (§1.11)                   │
│ 3.1 Execução dos ensaios elétricos e de sentido de rotação (Item 1 e 11).         │
│ 3.2 Execução dos ensaios de comunicação e watchdog de presença (Itens 2, 5 e 10). │
│ 3.3 Ensaio metrológico de calibração volumétrica e verificação de 10 min (Item 3).│
│ 3.4 Ensaio de retenção de volume e perfis sequenciais (Itens 6 e 7).              │
│ 3.5 Ensaio de sintonia de PID e resiliência pós-queda de energia (Itens 8 e 9).   │
│ 3.6 Emissão do Laudo Técnico de Comissionamento e Homologação Final da Bomba.     │
└───────────────────────────────────────────────────────────────────────────────────┘
```

---

## 4. CONCLUSÃO E ATESTAÇÃO TÉCNICA

A auditoria de engenharia comprova que o subsistema da Bomba Peristáltica Externa (Firmware v3.10 e OpenTEC-Hub v10.2) alcançou um patamar robusto de projeto arquitetural. Todas as vulnerabilidades históricas foram sanadas no firmware embarcado e no gateway. 

A aplicação das correções pontuais especificadas para o aplicativo Flutter (`Android_app`) restabelecerá a total harmonia com os contratos de interface e dotará o operador dos instrumentos de controle, metrologia e segurança preconizados pelas Seções 1.10 e 1.11 de `COMANDOS_DISPOSITIVOS_EXTERNOS.md`.
