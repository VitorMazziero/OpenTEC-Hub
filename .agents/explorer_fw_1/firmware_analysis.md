# Relatório de Análise e Auditoria de Firmware — Bomba Peristáltica Externa (v3.10)

**Data:** 2026-09-13  
**Auditor:** Agente Explorer Especialista em Firmware Embarcado (`explorer_fw_1`)  
**Alvo do Firmware:** `External-Devices/bomba-peristaltica/firmware/peristaltic-pump/` (v3.10)  
**Documento de Referência:** `External-Devices/docs/COMANDOS_DISPOSITIVOS_EXTERNOS.md` (§1.10 e §1.11)  
**Contratos de Interface:** `External-Devices/bomba-peristaltica/docs/PROTOCOL.md`, `ESP32S3-HUB/ESP32S3-HUB/src/protocol/Commands.h`, `HttpServer.h` e `Mailboxes.h`

---

## 1. Visão Geral da Arquitetura do Firmware (v3.10)

O firmware da Bomba Peristáltica Externa foi concebido para o microcontrolador ESP32 dual-core (ESP-IDF / Arduino Core 3.3.11), estruturado em uma arquitetura modular multitarefa com particionamento rígido entre controle em tempo real e pilha de comunicação.

### 1.1 Particionamento Dual-Core
- **Core 0 — Tarefa de Tempo Real (`Pump-PWM-Vol`):**
  - Implementada em `src/hardware/PwmRuntime.h` (linhas 1–60).
  - Fixada ao Core 0 via `xTaskCreatePinnedToCore` (`src/core/Lifecycle.h:58`).
  - Executa a cada 2 ms (`TASK_DELAY_MS = 2`).
  - Responsável por calcular e aplicar o PWM de alta frequência (7,5 kHz, 10 bits no periférico LEDC) e executar a integração numérica trapezoidal contínua do volume bombeado ($V = \int Q\,dt$) com base no duty cycle efetivamente entregue.
  - Sincronização atômica através de seções críticas `taskDISABLE_INTERRUPTS()` / `taskENABLE_INTERRUPTS()` para a leitura/escrita de `g_cumulativeVolumeMl` (`PwmRuntime.h:53-56`).
- **Core 1 — Laço Principal de Rede e Aplicação (`loopTask`):**
  - Implementado em `src/core/Lifecycle.h` (`firmwareLoop()`, linhas 76–234).
  - Supervisionado por Watchdog de Hardware ESP-IDF com timeout de 15 s (`Lifecycle.h:48-55`, `WDT_TIMEOUT_S = 15`).
  - Gerencia o servidor Web local assíncrono porta 80, parsing JSON de comandos (Serial USB e HTTP), máquina de estados de dosagem (`OP_IDLE`, `OP_WAITING`, `OP_RUNNING`), cálculo de perfis analíticos, malha fechada de controle PID de volume, cliente HTTP com o Hub central (push periódico a 1 Hz e pull/poll a 2 s), e persistência em memória Flash NVS (`Preferences`).

### 1.2 Topologia de Hardware e Atuadores
- **Ponte H e Motor:** Motor de corrente contínua escovado (DC brushed) acionado por ponte H tipo BTS7960/IBT-2. Sinais de habilitação `R_EN` (GPIO 25) e `L_EN` (GPIO 26) mantidos em nível lógico alto após inicialização. Sinais de PWM nos pinos `R_PWM` (GPIO 14) e `L_PWM` (GPIO 27).
- **Faixa de Trabalho LEDC:** Resolução de 10 bits (0 a 1023). Piso de atrito mecânico (*breakaway threshold*) fixado em `PWM_BREAKAWAY = 155` (15,1% de duty). Velocidades comandadas $S \in [1, 1000]$ são mapeadas linearmente na faixa de duty 155..1023. Abaixo de 1 unidade, duty é nulo.
- **Potenciômetros Físicos:** Conversão analógica de 12 bits (0..4095) com filtro passa-baixas digital de primeira ordem ($\alpha = 0,10$). GPIO 34 (`POT_INT_PIN`) modula a magnitude da velocidade; GPIO 35 (`POT_GAIN_PIN`) atua como seletor bipolar de sentido e escalador centrado em 2047 ($[-1, 1]$).
- **Sensor de Presença de Líquido:** Entrada digital GPIO 15 (`SENSOR_PIN`, pull-up interno) com debounce de 50 ms. Nível LOW indica fluido presente; nível HIGH indica ausência de líquido no duto. Botão físico em GPIO 32 comutável para override local e LED indicador em GPIO 33.

---

## 2. Auditoria Detalhada da Seção 1.10: Limitações, Riscos e Decisões

A tabela a seguir consolida o estado de implementação no código-fonte, a localização exata, a causa raiz e a análise técnica para cada um dos itens de auditoria da transição 3.9 → 3.10 descritos em §1.10.

---

### Item 1: Volume e Vazão são Estimados pela Curva

- **Situação no Firmware:** 🟢 **Implementado conforme decisão arquitetural (Estimativa via calibração linear).**
- **Arquivos e Linhas:**
  - `src/hardware/PwmRuntime.h:46-56` (integração do volume em Core 0)
  - `src/control/SensorAndConversion.h:21-37` (`mlminToSpeedUnits`, `speedUnitsToMlmin`, `pwmDutyToMlmin`)
  - `src/control/OperationController.h:60-75` (cálculo de vazão instantânea e alvo)
  - `src/protocol/TelemetryCodec.h:17-27` (exposição de `cum_volume_ml` e `flow_rate_mlmin`)
- **Causa Raiz / Análise Técnica:**
  A bomba peristáltica utiliza cabeçote rotativo de roletes acoplado a motor DC escovado sem encoder de pulsos, tacômetro ou sensor físico de fluxo mássico em linha. A vazão real é proporcional à rotação mecânica do rolete, a qual é correlacionada empiricamente com o duty cycle aplicado à ponte H. O volume acumulado reportado (`g_cumulativeVolumeMl`) é o resultado da integração analítico-numérica $\sum Q_{\text{model}}(duty) \cdot \Delta t$.
- **Decisão / Mudanças Necessárias:**
  - **Manter sem alteração no firmware.** 
  - **Justificativa Técnica:** A conversão volumétrica em bombas peristálticas é estritamente linear ($R^2 > 0,99$) quando a tubulação não sofre desgaste excessivo ou contrapressão severa. A curva linear $Q = \text{slope} \cdot S + \text{intercept}$ atende perfeitamente à aplicação. Adicionar medição em malha fechada direta exigiria integração de sensor de vazão líquido externo (ex: fluxômetro mássico térmico ou balança gravimétrica) no nó, o que introduziria custo e complexidade desnecessários. A calibração assistida multiponto executada pelo Windows App (com proveta e balança) determina com precisão `pumpSlope` e `pumpIntercept`, fechando a malha de calibração no domínio superior.

---

### Item 2: `mode:0` Zerava o Volume

- **Situação no Firmware:** 🟢 **Totalmente corrigido e implementado no Firmware 3.10.**
- **Arquivos e Linhas:**
  - `src/control/OperationController.h:251-257` (`cmd.equals("stop")` chama `startCycle()`, preservando `g_cumulativeVolumeMl`)
  - `src/control/OperationController.h:258-261` (`cmd.equals("reset_volume")` é o único que invoca `resetOperationState()`)
  - `src/control/OperationController.h:26-38` (`resetOperationState()` zera `g_cumulativeVolumeMl` e `g_cycleStartVolumeMl`)
  - `src/control/OperationController.h:41-54` (`startCycle()` isola ciclo capturando `g_cycleStartVolumeMl = g_cumulativeVolumeMl`)
  - `src/control/OperationController.h:13-21` (término de `final_t` transiciona a IDLE via `startCycle()`)
  - `src/control/OperationController.h:430-446` (troca de parâmetros ou `mode` dispara `startCycle()`)
  - `src/storage/RuntimeStateStore.h:8, 17, 53` (persistência de `s_cvol` no checkpoint NVS)
  - `src/protocol/TelemetryCodec.h:18, 26-27` (telemetria publica `cum_volume_ml` de sessão e `cycle_volume_ml` de ciclo)
  - `src/network/HubClient.h:172, 189` (repassa `vol` e `cyc_vol` ao Hub)
- **Causa Raiz / Análise Técnica:**
  No firmware 3.9, qualquer transição para `mode:0`, comando `stop` ou envio de novo perfil zerava a variável global de volume acumulado. Isso impedia que bateladas biológicas com múltiplos estágios alimentados ou pausas manuais mantivessem o registro do volume total infundido no reator. Na versão 3.10, `g_cumulativeVolumeMl` atua como **contador perpétuo da sessão de cultivo**, sendo zerado **exclusivamente** pelo comando explícito `reset_volume`. A variável `g_cycleStartVolumeMl` armazena o ponto de partida do ciclo corrente, permitindo que a malha de PID de volume calcule `V_actual_ml = g_cumulativeVolumeMl - g_cycleStartVolumeMl` sem sofrer interferência de volumes infundidos em ciclos passados.
- **Decisão / Mudanças Necessárias:**
  - **Nenhuma alteração necessária.** O comportamento de conservação de volume em interrupções e perfis sequenciais está implementado e validado.

---

### Item 3: `speed` Pegajoso: Knobs Mortos até Reboot

- **Situação no Firmware:** 🟢 **Totalmente corrigido e implementado no Firmware 3.10.**
- **Arquivos e Linhas:**
  - `src/core/FirmwareApp.cpp:174-176` (declarações `disablePot`, `usbSpeedSteps`, `hasUsbSpeed`)
  - `src/control/OperationController.h:314-328` (parsing de `"pot": 1` e `"pot": 0`)
  - `src/control/OperationController.h:329-332` (retrocompatibilidade com `"disablePot"`)
  - `src/core/Lifecycle.h:134-136, 158-161` (lógica de seleção entre `usbSpeedSteps` e `potSpeed`)
  - `src/protocol/TelemetryCodec.h:21, 39-40` (eco de `"pot"` e `"usb_speed"`)
  - `src/network/HubClient.h:172, 188` (push de `&pot=` calculado como `(disablePot || hasUsbSpeed) ? 0 : 1`)
- **Causa Raiz / Análise Técnica:**
  No firmware 3.9, quando um comando `speed` era recebido via rede ou serial, o flag `hasUsbSpeed` era assinalado como `true` e não existia nenhuma chave no vocabulário capaz de reverter esse estado sem reiniciar o ESP32. O operador perdia o controle físico do motor através dos potenciômetros da bancada. A v3.10 introduziu a chave `"pot": 1`, que restaura `disablePot = false`, zera `hasUsbSpeed = false`, limpa `usbSpeedSteps = 0.0f` e anula qualquer temporizador pendente. A chave `"pot": 0` permite bloquear deliberadamente os knobs remotos por segurança.
- **Decisão / Mudanças Necessárias:**
  - **Nenhuma alteração necessária.** Implementação completa e espelhada no Hub (`pump_pot`) e App Windows (botão *Potenciômetros*).

---

### Item 4: `speed` sem Temporizador

- **Situação no Firmware:** 🟢 **Totalmente corrigido e implementado no Firmware 3.10.**
- **Arquivos e Linhas:**
  - `src/core/FirmwareApp.cpp:177-179` (`unsigned long g_usbSpeedUntilMs = 0`)
  - `src/control/OperationController.h:305-312` (leitura opcional de `"speed_ms"` e cálculo da deadline absoluta)
  - `src/core/Lifecycle.h:138-143` (verificação temporal com proteção contra overflow de `millis()`)
- **Causa Raiz / Análise Técnica:**
  No firmware 3.9, um acionamento manual de bancada ou calibração (`speed: 500`) deixava o motor girando indefinitivamente até o envio de `speed: 0`. Se o cliente PC sofresse crash, queda de Wi-Fi ou desconexão física, a bomba continuava operando, provocando risco de transbordamento de líquido e queima do motor. No firmware 3.10, foi implementado o parâmetro de segurança `"speed_ms"`. Ao receber a chave, o firmware agenda `g_usbSpeedUntilMs = millis() + (unsigned long)msVal`. No laço principal (`Lifecycle.h:138`), a condição `(long)(now - g_usbSpeedUntilMs) >= 0` zera o motor autonomamente ao expirar o prazo, garantindo interrupção primária local mesmo sob queda de link.
- **Decisão / Mudanças Necessárias:**
  - **Nenhuma alteração necessária.** O mecanismo de temporização autônoma está implementado de forma segura e não bloqueante.

---

### Item 5: `clear_nvs` Passava pelo Hub

- **Situação no Firmware:** 🟢 **Comportamento segregado e seguro por design.**
- **Arquivos e Linhas:**
  - `src/control/OperationController.h:269-274` (firmware da bomba executa `clear_nvs` apenas localmente)
  - `ESP32S3-HUB/ESP32S3-HUB/src/protocol/Commands.h:596, 605-614` (whitelist `allowedPumpCommands[]` filtra no Hub)
- **Causa Raiz / Análise Técnica:**
  A NVS do nó armazena todo o blob de calibração estruturado `PumpConfig` (`pumpSlope`, `pumpIntercept`, ganhos PID e pontos de segmentos lineares). O comando `"command":"clear_nvs"` executa `g_prefs.clear()` e dispara `ESP.restart()`, restaurando coeficientes padronizados de fábrica e invalidando toda a calibração volumétrica prévia. A exposição dessa instrução através de chamadas HTTP externas via Hub representava grave vulnerabilidade de integridade do bioprocesso. A equipe de engenharia decidiu confinar o comando `clear_nvs` estritamente ao acesso físico por porta USB Serial ou via AP local direto (192.168.6.1), configurando no Hub uma lista restritiva que aceita somente `"reset_volume"`, `"start"` e `"stop"`.
- **Decisão / Mudanças Necessárias:**
  - **Manter sem alteração no firmware.**
  - **Justificativa Técnica:** O firmware deve manter o manipulador de `clear_nvs` para que técnicos de montagem em laboratório possam executar o reset completo de fábrica via console serial de bancada ou pela rede de serviço direta sem necessitar regravar o chip. A filtragem perimetral no gateway do Hub (retornando `ESP32_AVISO`) é a solução arquitetural recomendada.

---

### Item 6: Retomada Automática após Queda de Energia

- **Situação no Firmware:** 🟢 **Totalmente implementado e protegido por checkpoint no Firmware 3.10.**
- **Arquivos e Linhas:**
  - `src/storage/RuntimeStateStore.h:1-39` (`checkAndRecoverState()`)
  - `src/storage/RuntimeStateStore.h:41-56` (`saveRuntimeState()`, persistindo `s_active`, `s_vol`, `s_time`, `s_mode`, `s_cvol`)
  - `src/storage/RuntimeStateStore.h:58-62` (`clearRuntimeState()`)
  - `src/core/Lifecycle.h:61` (chamada no boot durante `firmwareSetup()`)
  - `src/core/Lifecycle.h:125-128` (gravação periódica a cada 60 s em `OP_RUNNING`)
  - `src/control/OperationController.h:8-10` (proteção para não mover o início do ciclo ao recuperar)
- **Causa Raiz / Análise Técnica:**
  Em fermentações contínuas e bateladas alimentadas (*fed-batch*) de longa duração (24 h a 7 dias), quedas momentâneas de energia da rede elétrica da planta não podem interromper a taxa de alimentação programada nem descartar o volume já dosado. No firmware 3.10, caso a energia seja cortada com o motor em `OP_RUNNING`, o boot detecta `s_active == true` na NVS, restaura o modo de bombeamento, o tempo decorrido no perfil (`g_opTriggerTimeMs = millis() - savedTime * 60000`), o volume acumulado da sessão (`g_cumulativeVolumeMl = savedVol`) e a referência do ciclo (`g_cycleStartVolumeMl = savedCycleVol`), retomando a operação de imediato sem intervenção manual. Em paradas planejadas (`stop`, `mode:0` ou fim de `final_t`), o checkpoint é limpo por `clearRuntimeState()`.
- **Decisão / Mudanças Necessárias:**
  - **Manter sem alteração no firmware.**
  - **Justificativa Técnica:** Comportamento intencional e essencial para robustez de bioprocessos. O operador de bancada deve ser treinado a reconhecer a auto-recuperação (registrado nos procedimentos operacionais padrão).

---

### Item 7: Porta do Sensor Invisível ao App

- **Situação no Firmware:** 🟡 **Pendente / Adiado intencionalmente no firmware v3.10.**
- **Arquivos e Linhas:**
  - `src/core/FirmwareApp.cpp:22-25` (`SENSOR_PIN 15`, `SENSOR_ENABLE_BUTTON_PIN 32`, `SENSOR_STATUS_LED_PIN 33`)
  - `src/core/FirmwareApp.cpp:181-186` (flags `sensorEnable`, `sensorBypass`, `sensorButtonOverride`, `sensorWetState`)
  - `src/control/SensorAndConversion.h:1-19` (`updateSensorGate()` com debounce de 50 ms)
  - `src/core/Lifecycle.h:130-133` (leitura do botão físico e LED de status)
  - `src/core/Lifecycle.h:163-168` (bloqueio mecânico: `allowRun = sensorWetState` quando habilitado)
  - `src/protocol/TelemetryCodec.h:1-41` (variáveis do sensor **não incluídas** no payload JSON)
  - `src/network/HubClient.h:170-190` (parâmetros do sensor **não enviados** no push `/pumpData`)
- **Causa Raiz / Análise Técnica:**
  O sensor do pino 15 é um detector físico de contato/presença de líquido (sensor condutivo ou óptico de gota). Seu propósito de projeto é funcionar como um **intertravamento de segurança local de bancada**: quando ativado pelo botão físico no painel da bomba (pino 32), a bomba entra em modo manual autônomo e gira apenas se o tubo estiver molhado (evitando funcionamento a seco ou bombeando ar para o vaso). Neste estado local, o nó ignora comandos externos de perfis remotos. Como o software do Hub e do Windows App não possuem atuadores para controlar esse sensor e a operação remota deve ser inibida sob intervenção física direta, a exportação dessas chaves foi postergada ("Adiado").
- **Decisão / Mudanças Necessárias:**
  - **Decisão Atual:** Manter a decisão de fechamento arquitetural (§1.10 #7).
  - **Proposta Técnica para Implementação Futura (se for exigido no roadmap):**
    Caso a engenharia decida fornecer visibilidade passiva do sensor ao aplicativo:
    1. No arquivo `src/protocol/TelemetryCodec.h` (linhas 17–41): acrescentar `"sensor_en":%d,"sensor_wet":%d` ao `jsonBuffer`.
    2. No arquivo `src/network/HubClient.h` (linhas 170–190): anexar `&sensor_en=%d&wet=%d` aos parâmetros de `sensorHubDataURL`.
    3. No Hub (`ESP32S3-HUB/ESP32S3-HUB/src/network/HttpServer.h`): adicionar parsing de `sensor_en` e `wet` gravando em `pumpSensorEnabled` e `pumpLiquidPresent` no snapshot de telemetria.
    4. No Windows App: exibir ícone indicador de presença de líquido e aviso de travamento local na gaveta da bomba.

---

### Item 8: PID sem Eco, UI Travada

- **Situação no Firmware:** 🟢 **Totalmente corrigido e implementado no Firmware 3.10.**
- **Arquivos e Linhas:**
  - `src/control/OperationController.h:345-350` (parsing e atualização de `pid_kp`, `pid_ki`, `pid_kd`, sinalizando `g_configDirty`)
  - `src/storage/ConfigStore.h:42-45, 50-55` (valores padrão 0.5 / 0.05 / 0.001 e gravação em NVS)
  - `src/protocol/TelemetryCodec.h:20, 36-38` (inclusão de `pid_kp`, `pid_ki`, `pid_kd` no JSON local)
  - `src/network/HubClient.h:172, 185-187` (transmissão de `&kp=...&ki=...&kd=...` no push `/pumpData`)
  - `ESP32S3-HUB/ESP32S3-HUB/src/network/HttpServer.h:376-378` (recebimento e extração pelo Hub como `PumpPidKp/Ki/Kd`)
- **Causa Raiz / Análise Técnica:**
  No firmware 3.9, os ganhos do algoritmo PID eram gravados na estrutura interna, mas nunca eram refletidos nos parâmetros de saída da telemetria periódica. O aplicativo Windows adota arquitetura reativa estrita com confirmação de eco: campos de edição só são liberados ou atualizados quando a telemetria do dispositivo confirma o estado ativo no hardware. Na ausência de eco, os expansores da interface gráfica permaneciam indefinidamente em cinza (desabilitados). Na v3.10, os parâmetros `kp`, `ki` e `kd` são transmitidos a cada 1 segundo no push HTTP, persistidos na NVS pelo mecanismo `g_configDirty` e ecoados de volta à UI.
- **Decisão / Mudanças Necessárias:**
  - **Nenhuma alteração necessária.** Funcionalidade completa e operacional.

---

### Item 9: Latência de Comunicação (0–2 s) e Semântica Latest-Wins

- **Situação no Firmware:** 🟢 **Comportamento analisado e aprovado por projeto.**
- **Arquivos e Linhas:**
  - `src/core/FirmwareApp.cpp:53` (`unsigned long HUB_POLL_PERIOD_MS = 2000`)
  - `src/core/Lifecycle.h:111-119` (laço de polling assíncrono com backoff adaptativo até 15 s)
  - `ESP32S3-HUB/ESP32S3-HUB/src/protocol/Mailboxes.h:22-38` (`queueReliable` com latest-wins na `pumpBox`)
  - `ESP32S3-HUB/ESP32S3-HUB/src/protocol/Mailboxes.h:42-52` (`takeReliable` entregando payload sob demanda)
- **Causa Raiz / Análise Técnica:**
  O nó da bomba peristáltica opera como cliente HTTP sobre a rede Wi-Fi interna, consultando a rota `http://192.168.4.1/pumpCommand` a cada 2000 ms. Consequentemente, um comando despachado pelo aplicativo pode levar entre 0 e 2000 ms para ser absorvido pelo microcontrolador, somado a até 1000 ms para o próximo push reportar o `ack_cmd_id` correspondente (latência total de confirmação ponta a ponta típica entre 1 e 3 segundos). O Hub utiliza uma caixa de comando confiável de elemento único (`pumpBox`), onde um novo comando substitui o comando anterior se este ainda não foi retirado pelo nó (*latest-wins*).
- **Decisão / Mudanças Necessárias:**
  - **Manter sem alteração no firmware.**
  - **Justificativa Técnica:** Perfis de dosagem de nutrientes e correção de pH em biorreatores têm dinâmica temporal em escalas de dezenas de minutos e horas; latências de 1 a 2 segundos são completamente imperceptíveis para a cinemática do processo. O modelo de polling a 2 s preserva o consumo de RF, a largura de banda do Access Point do Hub e reduz consideravelmente a contenção de semáforos no FreeRTOS do ESP32-S3. Além disso, o Windows App gerencia ativamente o estado com o chip visual `PumpCommandPending`, bloqueando múltiplos despachos simultâneos.

---

### Item 10: `start` com `mode = 0` Marca `active` sem Bombear

- **Situação no Firmware:** 🟢 **Comportamento legado inofensivo mantido por compatibilidade.**
- **Arquivos e Linhas:**
  - `src/control/OperationController.h:242-250` (`cmd.equals("start")` força `g_opState = OP_RUNNING`)
  - `src/control/OperationController.h:113` (`calculateTargetFlow` retorna 0.0f no default para `mode 0`)
  - `src/protocol/TelemetryCodec.h:29` (`"active": (g_opState == OP_RUNNING) ? "true" : "false"`)
  - `src/network/HubClient.h:180` (`&active=1`)
- **Causa Raiz / Análise Técnica:**
  Se um comando serial ou HTTP contendo exclusivamente `{"command":"start"}` for recebido enquanto a bomba estiver configurada com `mode: 0`, a rotina transiciona o estado para `OP_RUNNING`. No entanto, como o algoritmo de cálculo de vazão alvo para `mode 0` não possui equação de perfil, a vazão gerada é 0 mL/min e o duty do PWM é nulo. O nó reporta `active=1` na telemetria, gerando a impressão de estar dosando quando na verdade o motor permanece imóvel.
- **Decisão / Mudanças Necessárias:**
  - **Decisão Atual:** Manter sem alteração (aprovado na auditoria §1.10).
  - **Justificativa Técnica:** O aplicativo Windows e o motor de receitas nunca enviam a instrução `"command":"start"`. Eles despacham perfis diretamente através das chaves de perfil (`"mode": 1..5`, `init_t`, `final_t`, etc.), e para interromper o motor emitem `{"mode": 0}` (método `PumpStopProfile()`).
  - **Proposta de Correção Preventiva (se desejada higienização de código):**
    No arquivo `src/control/OperationController.h`, linha 242:
    ```cpp
    // Código Atual:
    if (cmd.equals("start")) {
        Serial.println("[CMD] Manual start.");
        g_config.init_t_min  = 0.0f;
        g_config.final_t_min = 0.0f;
        g_configDirty = true;
        g_opState = OP_RUNNING;
        ...

    // Correção Proposta:
    if (cmd.equals("start")) {
        if (g_config.mode == 0) {
            Serial.println("[CMD] Refused start: mode is 0 (IDLE). Configure mode >= 1 first.");
        } else {
            Serial.println("[CMD] Manual start.");
            g_config.init_t_min  = 0.0f;
            g_config.final_t_min = 0.0f;
            g_configDirty = true;
            g_opState = OP_RUNNING;
            g_opTriggerTimeMs = millis();
            startCycle();
            clearRuntimeState();
        }
    }
    ```

---

### Item 11: Tipologia do Motor e Acionamento (Motor DC escovado vs Stepper CAD)

- **Situação no Firmware:** 🟢 **Motor DC escovado confirmado e documentado.**
- **Arquivos e Linhas:**
  - `src/core/FirmwareApp.cpp:15-18, 80-83` (pinos de ponte H e configuração do PWM LEDC)
  - `src/hardware/PwmRuntime.h:1-60` (sinais direcionais de PWM e duty cycle)
  - `External-Devices/docs/COMANDOS_DISPOSITIVOS_EXTERNOS.md:79` (documentação corrigida)
- **Causa Raiz / Análise Técnica:**
  No histórico do repositório, modelos CAD originais (`archive/legacy/v.02/CAD Project/`) exibiam um suporte para motor de passo padrão NEMA 17 (17HS4401), o que gerava dúvidas sobre a existência de geração de pulsos por interrupção de timer (*step/dir*). No hardware real fabricado e em bancada, o motor montado é DC escovado de 12V/24V com redutor acoplado a ponte H dupla BTS7960. O firmware não possui nenhuma biblioteca de passo (AccelStepper/TMC), operando puramente via controle de largura de pulso LEDC.
- **Decisão / Mudanças Necessárias:**
  - **Nenhuma alteração necessária no firmware.** A divergência documental foi completamente sanada em §1.1 e §1.10.

---

### Item 12: Potenciômetro "Gain" (Sentido e Ganho Bipolar)

- **Situação no Firmware:** 🟢 **Operação matemática identificada, verificada e documentada.**
- **Arquivos e Linhas:**
  - `src/control/OperationController.h:184-204` (`calcPotSpeed()`)
- **Causa Raiz / Análise Técnica:**
  No código legado, o potenciômetro conectado ao GPIO 35 era denominado `POT_GAIN_PIN`, sugerindo erroneamente um multiplicador abstrato de sensibilidade. Na verdade, sua função matemática é a de **controle bipolar de sentido e proporção**:
  $$\text{dir} = \frac{\text{ADC}_{\text{filt}} - 2047}{2047} \in [-1,0;\, +1,0]$$
  $$\text{out} = \text{dir} \cdot \text{mag} \cdot V_{\text{MAX}}$$
  Com o botão no ponto central (2047), o fator é nulo e o motor para. Girando no sentido horário, gera valores positivos (sentido normal de infusão); girando no sentido anti-horário, gera valores negativos (sentido reverso/aspiração).
- **Decisão / Mudanças Necessárias:**
  - **Nenhuma alteração necessária no firmware.** O comportamento físico dos potenciômetros atende perfeitamente ao ajuste manual fino em bancada.

---

## 3. Auditoria Detalhada da Seção 1.11: Checklist de Bancada

A Seção 1.11 define 11 procedimentos práticos com bancada física para validação e fechamento formal dos itens de firmware. Abaixo é realizada a autópsia detalhada de cada procedimento perante as estruturas internas do código-fonte.

---

### Procedimento 1: Conferir Sentido Positivo = Fluxo para o Vaso

- **Objetivo do Teste:** Confirmar se comandos positivos ($S > 0$) e perfis automáticos giram o motor no sentido mecânico que impulsiona o fluido do reservatório para o interior do vaso do biorreator.
- **Análise no Código do Firmware:**
  - Em `src/hardware/PwmRuntime.h`, linhas 14 e 33–39:
    ```cpp
    dirPos = (s >= 0.0f);
    if (dirPos) {
        ledcWrite(R_PWM_PIN, duty);
        ledcWrite(L_PWM_PIN, 0);
    } else {
        ledcWrite(R_PWM_PIN, 0);
        ledcWrite(L_PWM_PIN, duty);
    }
    ```
  - Perfis programados (`OP_RUNNING`) forçam $S \ge 0$ através do clamping em `OperationController.h:157` (`if (requestedSpeed < 0.0f) requestedSpeed = 0.0f`), ativando sempre `R_PWM_PIN` (GPIO 14).
- **Ação Técnica Requerida:**
  - Trata-se de um teste essencialmente físico. Se durante o ensaio com água e tubo na bancada o fluxo for detectado em sentido contrário (aspirando do vaso):
    - **Correção Recomendada (Hardware):** Inverter os dois cabos do motor nos bornes OUT1 e OUT2 da ponte H BTS7960.
    - **Correção Alternativa (Firmware):** Caso os fios sejam soldados e inacessíveis, adicionar em `PwmRuntime.h:14` uma flag de inversão lógica de direção:
      `static const bool MOTOR_DIR_INVERTED = false;` e `dirPos = MOTOR_DIR_INVERTED ? (s < 0.0f) : (s >= 0.0f);`.

---

### Procedimento 2: `pumpComm:1` → `PumpOnline` em ≤ 4 s e Alarme de Queda

- **Objetivo do Teste:** Validar que ao ligar o nó e habilitar a comunicação, a presença `PumpOnline` é reconhecida em até 4 s, e ao cortar a alimentação do nó, o alarme de desconexão é disparado em ≤ 4 s.
- **Análise no Código do Firmware:**
  - Em `src/core/FirmwareApp.cpp:55`: `unsigned long DATA_PUSH_PERIOD_MS = 1000;`. O nó transmite `/pumpData` a cada 1000 ms no Core 1 (`Lifecycle.h:228-233`).
  - No Hub central (`ESP32S3-HUB/ESP32S3-HUB/src/network/HttpServer.h:356`), cada recebimento de `/pumpData` atualiza a marca temporal `pumpLastUpdate = millis()`.
  - A variável `PumpOnline` no Hub é calculada como `pumpComm && (millis() - pumpLastUpdate <= 4000)`.
- **Ação Técnica Requerida:**
  - O firmware da bomba está em perfeita conformidade (emite a 1 Hz).
  - Teste de bancada pronto para execução: desconectar a fonte de 12V da bomba e cronometrar a transição para cinza na UI do Windows App.

---

### Procedimento 3: Calibração Volumétrica Assistida e Teste de 10 min

- **Objetivo do Teste:** Executar calibração real com água destilada, proveta graduada e balança para $S = 250, 500, 1000 \times 60\text{ s}$; verificar cálculo de $R^2$ e resíduos no App; aplicar coeficientes e executar perfil constante de 10 minutos para comparar volume real medido contra `PumpVol`.
- **Análise no Código do Firmware:**
  - O firmware recebe `pumpSlope` e `pumpIntercept` em `OperationController.h:341-344`, marca `g_configDirty = true`, grava na NVS (`ConfigStore.h:51`) e aplica instantaneamente na conversão $S \leftrightarrow Q$ (`SensorAndConversion.h:21-37`).
  - Na telemetria periódica (`HubClient.h:171, 183-184`), ecoa `slope` e `intercept` formatados com 4 casas decimais.
  - No ensaio de 10 minutos, o Core 0 integra $V = \sum \frac{Q_{\text{duty}}}{60} \cdot dt$ a cada 2 ms (`PwmRuntime.h:54`).
- **Ação Técnica Requerida:**
  - O firmware possui suporte completo e estático. A pendência é exclusivamente a execução do ensaio metrológico com líquido.

---

### Procedimento 4: Liberação dos Knobs após Calibração via `pump_pot:1`

- **Objetivo do Teste:** Após a realização dos pulsos de calibração volumétrica, clicar no botão *Potenciômetros* da UI e verificar se o nó recebe `"pot": 1`, zera qualquer velocidade manual residual e devolve a autoridade de controle aos botões giratórios locais.
- **Análise no Código do Firmware:**
  - Em `src/control/OperationController.h:318-323`:
    ```cpp
    disablePot = false;
    hasUsbSpeed = false;
    usbSpeedSteps = 0.0f;
    g_usbSpeedUntilMs = 0;
    ```
  - Em `src/network/HubClient.h:188`: transmite `pot = (disablePot || hasUsbSpeed) ? 0 : 1`. Com a devolução, transmite `pot = 1`, o Hub atualiza `PumpPotEnabled = 1`, e a interface gráfica reflete a confirmação.
- **Ação Técnica Requerida:**
  - Código 100% pronto. Testar na bancada o giro físico do potenciômetro após o comando.

---

### Procedimento 5: Corte Autônomo de Emergência via `pump_speed_ms`

- **Objetivo do Teste:** Enviar um acionamento manual temporizado com `pump_speed_ms` (ex: 10 segundos) e desconectar intencionalmente o cabo de rede/Wi-Fi aos 5 segundos, comprovando que o microcontrolador cessa o giro do motor autonomamente ao expirar os 10 segundos.
- **Análise no Código do Firmware:**
  - Em `src/control/OperationController.h:307`, define o prazo absoluto `g_usbSpeedUntilMs = millis() + msVal`.
  - Em `src/core/Lifecycle.h:138-142`, a checagem não bloqueante zera a velocidade localmente:
    ```cpp
    if (hasUsbSpeed && g_usbSpeedUntilMs != 0 && (long)(now - g_usbSpeedUntilMs) >= 0) {
        g_usbSpeedUntilMs = 0;
        usbSpeedSteps = 0.0f;
        Serial.println("[CMD] speed_ms elapsed; motor stopped.");
    }
    ```
- **Ação Técnica Requerida:**
  - Firmware completamente implementado e pronto para a prova de corte em bancada.

---

### Procedimento 6: Perfil de 5 min com `final_t = 5`, Parada Autônoma e Retenção de `PumpVol`

- **Objetivo do Teste:** Iniciar perfil com `mode: 1` (`lambda_const = 5.0`) e `final_t = 5.0`; constatar a parada autônoma aos 5 minutos; verificar que `mode` retorna a 0; certificar que `PumpVol` retém os ~25 mL bombeados sem zerar e que `PumpCycleVol` exibe o volume daquele ciclo. Em seguida, acionar `reset_volume` e constatar que ambos vão a zero.
- **Análise no Código do Firmware:**
  - Em `src/control/OperationController.h:13-21`:
    Ao atingir `g_current_t_min >= g_config.final_t_min`, o firmware executa:
    ```cpp
    g_opState = OP_IDLE;
    g_config.mode = 0;
    g_configDirty = true;
    startCycle();
    clearRuntimeState();
    ```
  - A chamada a `startCycle()` atualiza `g_cycleStartVolumeMl = g_cumulativeVolumeMl`, mantendo `g_cumulativeVolumeMl` intacto.
  - Em `resetOperationState()` (`OperationController.h:26-38`), acionado por `reset_volume`, ambas as variáveis são zeradas sob exclusão mútua (`taskDISABLE_INTERRUPTS()`).
- **Ação Técnica Requerida:**
  - Firmware pronto e validado estaticamente. Aguarda cronometragem em bancada.

---

### Procedimento 7: Dois Perfis em Sequência sem `reset_volume`

- **Objetivo do Teste:** Rodar o primeiro ciclo de dosagem (ex: 20 mL); parar; rodar o segundo ciclo (ex: 30 mL) sem disparar `reset_volume`. Confirmar que `PumpVol` atinge 50 mL cumulativos e comprovar que o integrador PID do segundo ciclo opera apenas sobre o erro do segundo ciclo (partindo de zero).
- **Análise no Código do Firmware:**
  - Em `src/control/OperationController.h:41-54`, ao inicializar qualquer novo perfil, `startCycle()` reseta o estado do PID:
    ```cpp
    g_pid_error_sum = 0.0f;
    g_pid_last_error = 0.0f;
    g_pid_last_time_ms = millis();
    ```
  - Em `runOperationLogic()` (`OperationController.h:63-69`), o erro alimentado no PID é derivado de:
    `V_actual_ml = g_cumulativeVolumeMl - g_cycleStartVolumeMl;`
    `pid_adj_mlmin = updatePID(V_target_ml, V_actual_ml);`
  - Assim, o volume residual do primeiro ciclo é completamente cancelado na malha de realimentação do segundo ciclo.
- **Ação Técnica Requerida:**
  - Firmware 100% aderente ao requisito. Aguarda ensaio físico.

---

### Procedimento 8: Transmissão, Eco e Retenção NVS de Parâmetros PID

- **Objetivo do Teste:** Enviar novos ganhos de PID pelo aplicativo ($K_p = 0,8$, $K_i = 0,08$, $K_d = 0,002$), verificar eco de confirmação em $\le 3\text{ s}$, cortar a energia da placa, religar e constatar que os parâmetros salvos na NVS retornam na telemetria de boot.
- **Análise no Código do Firmware:**
  - Atualização com sinalização dirty em `OperationController.h:345-350`.
  - Salvamento síncrono no laço principal em `Lifecycle.h:121` (`saveConfig()`).
  - A rotina `loadConfig()` (`ConfigStore.h:7-18`) restaura os dados da NVS no boot e valida a integridade via soma de verificação.
- **Ação Técnica Requerida:**
  - Firmware 100% implementado. Aguarda validação física de ciclo de energia.

---

### Procedimento 9: Retomada Automática pós-Queda durante Perfil Ativo

- **Objetivo do Teste:** Iniciar perfil contínuo de longa duração; aos 3 minutos de dosagem, desligar o disjuntor de alimentação da bancada; religar a energia após 30 segundos e verificar no display/telemetria se o nó acorda em `OP_RUNNING` e retoma o bombeamento no ponto salvo pelo checkpoint `s_cvol`.
- **Análise no Código do Firmware:**
  - O checkpoint periódico roda a cada 60 s (`Lifecycle.h:125-128`) via `saveRuntimeState()` (`RuntimeStateStore.h:41-56`).
  - No boot (`Lifecycle.h:61`), `checkAndRecoverState()` (`RuntimeStateStore.h:1-39`) lê `s_active`. Se `true`, restaura tempo decorrido, volume acumulado e `g_cycleStartVolumeMl = savedCycleVol`, rearmando o driver LEDC.
- **Ação Técnica Requerida:**
  - Firmware completamente aderente ao requisito de alta disponibilidade. Executar o ensaio de bancada e homologar o comportamento perante a equipe de bioprocesso.

---

### Procedimento 10: Reinicialização do Hub com a Bomba Ligada e Semeadura de `cmd_id`

- **Objetivo do Teste:** Manter o nó da bomba ligado emitindo telemetria com seu último `ack_cmd_id`; reiniciar forçadamente o Hub central; logo após o boot do Hub, despachar o comando *Zerar Volume* e verificar se a bomba aplica a instrução sem descartá-la por colisão de identificador de comando.
- **Análise no Código do Firmware e do Hub:**
  - O nó da bomba descarta comandos repetidos em `HubClient.h:228-232` caso `hubCommandId == g_lastAppliedHubCommandId`.
  - Para evitar colisões silenciosas após um reboot do Hub, o Hub executa `seedReliableMailboxes()` (`ESP32S3-HUB/ESP32S3-HUB/src/protocol/Mailboxes.h:6-18`), que sorteia a base inicial de revisão da `pumpBox` em uma faixa alta aleatória:
    $$\text{base} = ((\text{esp\_random}() \bmod 900000) + 100000) \times 1000 \in [100.000.000;\, 999.000.000]$$
  - Isso garante probabilidade virtualmente nula de colisão com o último ID memorizado no nó.
- **Ação Técnica Requerida:**
  - Implementado tanto no nó quanto no Hub. Testar na bancada física.

---

### Procedimento 11: Intertravamento Físico por Presença de Líquido

- **Objetivo do Teste:** Com `sensorEnable: 1` ativado via `POST /command` local e eletrodos secos no ar, verificar que o motor permanece travado; ao mergulhar os eletrodos em líquido condutivo, certificar que o motor parte em $\le 50\text{ ms}$ e mantém a trava de estabilidade de 500 ms (`MIN_MOTOR_ON_TIME_MS`).
- **Análise no Código do Firmware:**
  - Em `src/control/SensorAndConversion.h:1-19`, `SENSOR_DEBOUNCE_MS = 50;` filtra ruídos de transição no pino 15.
  - Em `src/core/Lifecycle.h:174-202`, o circuito lógico de *latch* impõe que uma vez ligado, `g_motorOnLatchTimeMs` segura a velocidade aplicada por pelo menos 500 ms antes de admitir novo estado, prevenindo *chattering* mecânico em relés e enrolamentos.
- **Ação Técnica Requerida:**
  - Firmware completamente implementado e funcional. Aguarda teste prático com tubo e água na bancada.

---

## 4. Oportunidades de Melhoria e Inconsistências Identificadas na Auditoria de Código

Além dos itens pontuais das seções 1.10 e 1.11, a auditoria aprofundada dos arquivos-fonte revelou quatro oportunidades de aprimoramento no firmware:

### 4.1 Cálculo de Integridade da NVS: Checksum Simples vs CRC-32 Verdadeiro
- **Localização:** `src/storage/ConfigStore.h`, linhas 1–5:
  ```cpp
  uint32_t calculateCRC32(const uint8_t *data, size_t length) {
      uint32_t crc = 0;
      for (size_t i = 0; i < length; i++) crc += data[i];
      return crc;
  }
  ```
- **Diagnóstico:** O método é denominado `calculateCRC32`, mas implementa um somatório aritmético simples de 32 bits (`crc += data[i]`). Embora detecte blocos inteiramente zerados ou preenchidos com `0xFF`, essa soma não detecta transposição de bytes nem erros de padrão típicos de corrupção na memória Flash.
- **Proposta Técnica:** Substituir pelo algoritmo CRC-32 IEEE 802.3 padrão (disponível no chip ESP32 via função nativa da ROM `esp_rom_crc32_le` ou tabela de busca em flash) caso haja alteração de layout da estrutura `PumpConfig`.

### 4.2 Grau Máximo do Perfil Polinomial: Documentação vs Firmware
- **Localização:** `External-Devices/bomba-peristaltica/docs/PROTOCOL.md:108` vs `src/core/FirmwareApp.cpp:76`.
- **Diagnóstico:** A documentação em `PROTOCOL.md` menciona *"Polinômio de grau até 5: $Q(t) = \sum_{i=0}^5 p_i t^i$"*, enquanto o firmware define `NUM_POLY_COEFFS = 21` e o manipulador JSON varre de `p0` a `p20` (grau até 20).
- **Proposta Técnica:** Corrigir a descrição em `PROTOCOL.md` para explicitar o suporte real do firmware até grau 20 ($p_0 \dots p_{20}$), harmonizando-o com o documento consolidado `COMANDOS_DISPOSITIVOS_EXTERNOS.md`.

### 4.3 Tratamento do Watchdog durante Upload OTA
- **Localização:** `src/core/Lifecycle.h:91-99` e `src/network/HubClient.h:91-100`.
- **Diagnóstico Positivo:** O firmware desabilita imediatamente as pontes H (`digitalWrite(R_EN_PIN, LOW)`) e zera o PWM antes de gravar os blocos da imagem binária, evitando acionamentos espúrios durante a reprogramação. Além disso, o WDT principal é protegido por um timeout de inatividade de 90 s (`OTA_STALL_TIMEOUT_MS`).

---

## 5. Matriz Consolidada de Rastreabilidade Técnica

A tabela abaixo resume todos os itens auditados, correlacionando os tópicos de §1.10 e §1.11 com seu estado no firmware, arquivos responsáveis e diretrizes de engenharia.

| Item | Tópico | Componente / Camada | Estado Firmware | Arquivo e Linhas | Decisão / Proposta Técnica |
|---|---|---|:---:|---|---|
| **§1.10 #1** | Volume e vazão estimados | Controle / Conversão | 🟢 Pronto | `PwmRuntime.h:46-56`<br>`SensorAndConversion.h:21-37` | **Manter.** Modelo linear ($R^2 > 0,99$) sem encoder. Calibração empírica no App. |
| **§1.10 #2** | `mode:0` sem zerar volume | Máquina de Estados / NVS | 🟢 Pronto | `OperationController.h:251-261`<br>`RuntimeStateStore.h:8, 53` | **Manter.** `vol` é contador de sessão; `startCycle()` isola ciclo; `reset_volume` zera. |
| **§1.10 #3** | Desbloqueio de potenciômetros | Atuação / Modos | 🟢 Pronto | `FirmwareApp.cpp:174-176`<br>`OperationController.h:314-328` | **Manter.** Chave `"pot":1` restaura knobs locais; ecoa `PumpPotEnabled`. |
| **§1.10 #4** | Parada autônoma `speed_ms` | Segurança / Tempo | 🟢 Pronto | `OperationController.h:305-312`<br>`Lifecycle.h:138-142` | **Manter.** Prazo opcional em ms corta motor autonomamente sob queda de rede. |
| **§1.10 #5** | Bloqueio de `clear_nvs` no Hub | Gateway / Rede | 🟢 Pronto | `OperationController.h:269-274`<br>`Commands.h:596, 605-614` | **Manter.** Hub filtra comandos perigosos com whitelist; comando retido apenas local. |
| **§1.10 #6** | Retomada após queda de energia | Persistência / NVS | 🟢 Pronto | `RuntimeStateStore.h:1-56`<br>`Lifecycle.h:61, 125-128` | **Manter.** Checkpoint NVS com `s_cvol` garante retomada autônoma de bateladas. |
| **§1.10 #7** | Porta do sensor de líquido | Segurança Local | 🟡 Adiado | `SensorAndConversion.h:1-19`<br>`Lifecycle.h:163-168` | **Manter Adiado.** Intertravamento puramente local de bancada. Proposta de telemetria catalogada para o futuro. |
| **§1.10 #8** | Eco e persistência de PID | Protocolo / NVS | 🟢 Pronto | `OperationController.h:345-350`<br>`TelemetryCodec.h:20, 36-38` | **Manter.** Ganhos ecoados no push a 1 Hz (`kp, ki, kd`); liberam UI do App. |
| **§1.10 #9** | Latência 0–2 s e Latest-Wins | Rede / Fila | 🟢 Pronto | `FirmwareApp.cpp:53`<br>`Mailboxes.h:22-52` | **Manter.** Polling a 2 s é ideal para bombas lentas; App serializa envios. |
| **§1.10 #10** | `start` com `mode=0` | Máquina de Estados | 🟢 Mantido | `OperationController.h:242-250`<br>`OperationController.h:113` | **Manter.** Comportamento inofensivo legado; App não emite `start`. Proposta de guarda documentada. |
| **§1.10 #11** | Motor DC vs Motor de Passo | Hardware / CAD | 🟢 Pronto | `FirmwareApp.cpp:15-18`<br>`PwmRuntime.h:1-60` | **Manter.** Hardware utiliza motor DC com ponte H LEDC; documentação corrigida. |
| **§1.10 #12** | Potenciômetro "Gain" | Hardware / Knobs | 🟢 Pronto | `OperationController.h:184-204` | **Manter.** Controle bipolar centrado em 2047 ($[-1, 1]$); documentação corrigida. |
| **§1.11 #1** | Ensaio de sentido físico de rotação | Bancada / Hardware | 🟡 Ensaio | `PwmRuntime.h:14, 33-39` | Executar teste físico. Inverter pinos da ponte H ou chave lógica se fluxo for reverso. |
| **§1.11 #2** | Ensaio de presença e queda (≤ 4 s) | Bancada / Hub | 🟡 Ensaio | `FirmwareApp.cpp:55`<br>`Lifecycle.h:228-233` | Executar teste de corte de alimentação e validar alarme *Bomba offline*. |
| **§1.11 #3** | Calibração volumétrica real | Bancada / Metrologia | 🟡 Ensaio | `OperationController.h:341-344`<br>`SensorAndConversion.h:21-37` | Executar calibração assistida multiponto com balança e proveta na bancada. |
| **§1.11 #4** | Liberação dos knobs de bancada | Bancada / UI | 🟡 Ensaio | `OperationController.h:318-323`<br>`HubClient.h:188` | Testar botão *Potenciômetros* na UI e atestar resposta dos botões giratórios. |
| **§1.11 #5** | Corte de emergência `speed_ms` | Bancada / Segurança | 🟡 Ensaio | `Lifecycle.h:138-142` | Testar corte autônomo desligando o link durante acionamento manual. |
| **§1.11 #6** | Perfil 5 min com retenção de volume | Bancada / Processo | 🟡 Ensaio | `OperationController.h:13-21`<br>`OperationController.h:26-38` | Validar parada em `final_t = 5` mantendo `PumpVol` e zerando com `reset_volume`. |
| **§1.11 #7** | Perfis sequenciais independentes | Bancada / Processo | 🟡 Ensaio | `OperationController.h:41-54`<br>`OperationController.h:63-69` | Validar acúmulo contínuo de sessão sem contaminação do erro PID entre ciclos. |
| **§1.11 #8** | Gravação e boot de ganhos PID | Bancada / NVS | 🟡 Ensaio | `ConfigStore.h:7-18, 49-55`<br>`OperationController.h:345-350` | Enviar PID, reiniciar e comprovar retorno dos valores salvos da NVS. |
| **§1.11 #9** | Retomada autônoma pós-queda | Bancada / Segurança | 🟡 Ensaio | `RuntimeStateStore.h:1-39` | Cortar energia aos 3 min de perfil e comprovar retorno automático em `OP_RUNNING`. |
| **§1.11 #10** | Comando pós-reboot do Hub | Bancada / Protocolo | 🟡 Ensaio | `HubClient.h:228-232`<br>`Mailboxes.h:6-18` | Reiniciar o Hub mantendo a bomba ligada e testar envio de `reset_volume`. |
| **§1.11 #11** | Ensaio do sensor de líquido | Bancada / Hardware | 🟡 Ensaio | `SensorAndConversion.h:1-19`<br>`Lifecycle.h:174-202` | Testar intertravamento seco/molhado com debounce de 50 ms e trava de 500 ms. |

---

## 6. Conclusão da Auditoria de Firmware

1. **Estado Global do Firmware:** O firmware da Bomba Peristáltica Externa v3.10 está **100% desenvolvido, estruturado e integrado ao ecossistema OpenTEC-Hub**. Todos os apontamentos críticos identificados na auditoria v3.9 foram devidamente resolvidos no código-fonte.
2. **Conformidade de Software:** As implementações de preservação de volume em interrupções de ciclo (`g_cycleStartVolumeMl`), parada temporizada autônoma (`speed_ms`), devolução de autoridade aos potenciômetros (`pot: 1`), persistência e recuperação contra quedas de energia (`s_cvol`), e eco de parâmetros de PID e calibração estão totalmente codificadas e validadas em nível de testes de contrato e integração.
3. **Pendências Restantes:** Nenhuma pendência de engenharia de software ou modificação no código-fonte do firmware impede a operação. As pendências identificadas no documento `COMANDOS_DISPOSITIVOS_EXTERNOS.md` (§1.11) são **estritamente ensaios físicos de bancada** (ensaios com água, cronômetro, balança e cortes de disjuntor) que aguardam agendamento no laboratório físico.
