# Plano Mestre de Implementação e Resolução Técnica: Inconsistências do Fluxômetro (F01–F16)

**Documento:** `IMPLEMENTATION_PLAN_FLUXOMETRO.md`  
**Referência Normativa:** Seção 3.10 de `External-Devices/docs/COMANDOS_DISPOSITIVOS_EXTERNOS.md`  
**Módulos Analisados:** Firmware Fluxômetro (`External-Devices/fluxometro/firmware/flowmeter/`), Backend Hub (`ESP32S3-HUB/`), Frontend Windows App (`Windows_app/`), App Flutter (`External-Devices/fluxometro/apps/flutter/`)  
**Data:** 13 de Setembro de 2026  
**Status:** Aprovado para Execução Planejada (Documentário / Analítico)

---

## 1. Sumário Executivo

Este documento estabelece o plano técnico definitivo para resolução ou justificativa formal de todas as 16 inconsistências (**F01 a F16**) catalogadas na Seção 3.10 do manual de dispositivos externos (`COMANDOS_DISPOSITIVOS_EXTERNOS.md`), aplicáveis ao subsistema **Fluxômetro** (medidor e controlador mássico de vazão baseado no Omega FMA-5400, ADC ADS1115, DAC MCP4725 e ESP32).

A investigação exaustiva do código-fonte revelou que o sistema opera atualmente com mecanismos de defesa e compensação implementados no Hub (`ESP32S3-HUB/`) e no aplicativo Windows (`Windows_app/`), que mitigam parcialmente limitações do firmware do nó. Contudo, subsistem vulnerabilidades críticas de segurança operacional (ex.: retenção de vazão durante atualizações OTA, indefinição na faixa baixa de setpoint, ausência de intertravamentos de rota no firmware e permissividade na persistência de parâmetros corrompidos).

Para cada item de F01 a F16, este plano fornece:
1. **Identificação Canônica e Problema Declarado**.
2. **Causa Raiz no Código com Arquivos e Linhas Exatas**.
3. **Impacto Sistêmico Cruzado** (Firmware, Hub, App Windows e Flutter).
4. **Decisão Técnica de Ação**: plano concreto de implementação de código com trechos de código/diffs conceituais OU justificativa técnica de postergação/manutenção fundamentada em limitações de hardware e metrologia.
5. **Classificação de Risco, Segurança e Prioridade**.

---

## 2. Arquitetura do Sistema e Cadeia de Comunicação

O subsistema de controle de fluxo integra três camadas hierárquicas:

```
┌─────────────────────────────────────────────────────────────┐
│                 OpenTEC Windows App (WPF/.NET 8)            │
│  - GasRouting.cs (intertravamento de rotas em software)      │
│  - CalibrationMath.cs (cálculo de curvas e interpolação)    │
│  - FlowControlViewModel.cs (UI de operação e supervisão)    │
└──────────────────────────────┬──────────────────────────────┘
                               │ USB Serial / HTTP REST (/readData, /command)
┌──────────────────────────────▼──────────────────────────────┐
│                    OpenTEC-Hub (ESP32-S3)                   │
│  - Mailboxes.h (gerenciamento de estado desejado e filas)   │
│  - HttpServer.h (coordenação REST /flowCommand e /flowData) │
│  - Telemetry.h (agregação e difusão de telemetria)          │
└──────────────────────────────┬──────────────────────────────┘
                               │ Wi-Fi HTTP Polling (250 ms) / Push (500 ms)
┌──────────────────────────────▼──────────────────────────────┐
│             Nó Controlador Fluxômetro (ESP32 Node)          │
│  - TaskRuntime.h (FreeRTOS tasks: http, telemetry, wifi)    │
│  - CommandCodec.h (parser zero-copy e despacho de comandos) │
│  - Lifecycle.h (malha PI 100 ms, corte geral, rampa)        │
│  - FlowIo.h (driver I²C ADS1115 A3 e DAC MCP4725)           │
│  - CalibrationStore.h (persistência EEPROM dos polinômios)  │
│  - OtaService.h (endpoint /update e rotinas de flash)       │
└─────────────────────────────────────────────────────────────┘
```

### Regras Fundamentais de Interação
- **Sentido de Telemetria:** O nó executa `telemetryTask` (`TaskRuntime.h:63-160`), emitindo requisições HTTP GET para `http://192.168.4.1/flowData?...` no Hub a cada 500 ms. O Hub armazena os valores em cache, calcula o timeout de presença (`flowmeterLastUpdate <= 6000 ms`) e agrega os dados em `/readData`. O App faz polling de `/readData` e atualiza seus ViewModels via `TelemetryParser.cs`.
- **Sentido de Comando:** O App despacha comandos estruturados (`OpenTECCommand`) via Hub (`Commands.h` / `Mailboxes.h`). O nó efetua polling periódico a cada 250 ms em `GET /flowCommand`. O Hub responde com o payload JSON contendo `cmd_id`. O nó executa as modificações em `CommandCodec.h` e responde com `ack_cmd_id` no próximo push de `/flowData`.

---

## 3. Análise Detalhada e Plano de Implementação por Inconsistência (F01 a F16)

---

### F01 — Identidade V10 no build/OTA e v11 no protocolo/endpoints

#### Problema Declarado e Causa Raiz
- **Problema:** O banner serial de inicialização e a página web de atualização OTA informam versão `V10`, enquanto os endpoints `/nodeHello`, `/diag`, `/status` e o documento `fluxometro/docs/PROTOCOL.md` declaram `v11`. Por sua vez, `README.md` e `CURRENT_STATUS.md` mencionam `v10`. Essa discrepância impede que a aplicação supervisora e os operadores determinem univocamente o binário ativo.
- **Localização Exata no Código:**
  - Firmware: `External-Devices/fluxometro/firmware/flowmeter/src/core/FirmwareApp.cpp:16-17` (`#define FW_VERSION "V10"`) e linha 58.
  - Firmware: `External-Devices/fluxometro/firmware/flowmeter/src/core/Lifecycle.h:10` (`Serial.println("Firmware " FW_BUILD);`).
  - Firmware: `External-Devices/fluxometro/firmware/flowmeter/src/tasks/TaskRuntime.h:85` (`snprintf(..., "%s/nodeHello?dev=flowmeter&ver=v11&mac=%s", ...);`).
  - Firmware: `External-Devices/fluxometro/firmware/flowmeter/src/api/OtaService.h:12,33` (`"{\"device\":\"flowmeter\",\"version\":\"v11\",..."`).
  - App: `Windows_app/src/OpenTECHub/Services/Communication/NodeFirmwareCatalog.cs:39` (`[Flowmeter] = new(StringComparer.OrdinalIgnoreCase) { "v11" }`).
  - Hub: `ESP32S3-HUB/ESP32S3-HUB/src/network/HttpServer.h:509-540` (armazena `flowmeterNodeVer` repassado via `/nodeHello`).

#### Impacto Sistêmico Cruzado
- Se o firmware unificar a versão para `"v10"` sem sincronização no App, `NodeFirmwareCatalog.cs` disparará um aviso incorreto ao usuário: *"Firmware V10 não validado com esta versão do app (validado: v11)"*.
- O Hub apenas repassa a string `flowmeterNodeVer` em `/readData` sem validar semanticamente seu conteúdo.

#### Decisão Técnica de Ação: IMPLEMENTAÇÃO IMEDIATA
- **Ação:** Unificar a versão sob a macro canônica `"v11.0"` em todo o firmware e atualizar o catálogo do App para suportar compatibilidade retroativa com nós antigos e atualizados.

#### Plano de Modificação de Código
1. **Firmware (`FirmwareApp.cpp` / `FirmwareApp.h`):**
   ```cpp
   // Substituir linhas 16-17 em FirmwareApp.cpp por:
   #define FW_VERSION "v11.0"
   #define FW_BUILD FW_VERSION " (" __DATE__ " " __TIME__ ")"
   ```
2. **Firmware (`TaskRuntime.h` linha 85 e `OtaService.h` linhas 12, 33):**
   ```cpp
   // Em TaskRuntime.h:
   snprintf(helloUrl, sizeof(helloUrl), "%s/nodeHello?dev=flowmeter&ver=%s&mac=%s",
            sensorHubURL.c_str(), FW_VERSION, WiFi.macAddress().c_str());

   // Em OtaService.h:
   snprintf(json, sizeof(json),
            "{\"device\":\"flowmeter\",\"version\":\"%s\",\"uptime_s\":%lu,",
            FW_VERSION, millis() / 1000UL);
   ```
3. **Windows App (`NodeFirmwareCatalog.cs` linha 39):**
   ```csharp
   [Flowmeter] = new(StringComparer.OrdinalIgnoreCase) { "v11", "v11.0", "V10" },
   ```
4. **Documentação:** Atualizar `CURRENT_STATUS.md` e `PROTOCOL.md` para refletir `v11.0`.

- **Prioridade:** Média (Baixo risco de quebra, consistência de release).
- **Classificação de Segurança:** Administrativa / Rastreabilidade de Software.

---

### F02 — Curva `FACTORY_*` do firmware, `CalibrationMath.FirmwareDefault`/pontos certificados do Windows e defaults do Flutter são diferentes

#### Problema Declarado e Causa Raiz
- **Problema:** Os coeficientes polinomiais padrão gravados no firmware (`FACTORY_*`), os valores padrão do Windows App (`CalibrationMath.FirmwareDefault`) e os valores no aplicativo Flutter utilizam três conjuntos numéricos completamente distintos. O acionamento da função "Restaurar Padrões de Fábrica" em interfaces diferentes instala modelos matemáticos conflitantes, gerando leituras díspares para uma mesma tensão elétrica do sensor.
- **Localização Exata no Código:**
  - Firmware: `External-Devices/fluxometro/firmware/flowmeter/src/core/FirmwareApp.cpp:183-195` (segmento baixo com $A_1 = -1353785.3$, $B_1 = 246663.69$, $K_1 = -16473.492$; segmento alto $K_2 = -0.46260458$).
  - Windows App: `Windows_app/src/OpenTECHub/Services/Calibration/CalibrationMath.cs:78-84` (segmento baixo com $A = 321791.3459$, $B = -32589.0731$, $K = 462.8935$; segmento alto $K = -0.854551899$).
  - Flutter App: `External-Devices/fluxometro/apps/flutter/lib/main.dart:180-185` (segmento baixo puramente quadrático com $K_1 = -139.057$, $F_1 = 21.9738$, $C_1 = -0.034188$, sem termos quárticos).

#### Impacto Sistêmico Cruzado
- Para uma tensão de $V = 0.030\text{ V}$:
  - Firmware (`FACTORY_*`): $\approx 0.18\text{ L/min}$.
  - Windows App (`FirmwareDefault`): $\approx 0.44\text{ L/min}$.
  - Flutter App (Quadrático): $\approx 0.49\text{ L/min}$.
- Uma variação de mais de 100% no cálculo de fluxo em baixas vazões dependendo de qual aplicativo enviou o comando de restauração padrão.

#### Decisão Técnica de Ação: JUSTIFICATIVA TÉCNICA DE POSTERGAÇÃO DE ALTERAÇÃO NUMÉRICA + SINCRONIZAÇÃO ARQUITETURAL
- **Justificativa:** A alteração arbitrária dos coeficientes numéricos em código fonte sem uma campanha de calibração metrológica em bancada com padrão primário rastreável (ex.: medidor calibrador Alicat ou Molbloc) destruiria a referência atualmente ajustada na bancada do laboratório. Coeficientes polinomiais de grau 4 possuem altíssima sensibilidade a variações de ponto flutuante.
- **Plano de Implementação Metrológico e Arquitetural:**
  1. **Fonte Única de Verdade:** Definir o firmware do nó como o detentor definitivo dos padrões físicos de fábrica (`FACTORY_*`).
  2. **Adequação do Windows App:** Alterar `CalibrationMath.cs` para refletir exatamente as constantes `FACTORY_*` do firmware V10/V11 ativo, ou desativar o botão de restauração local caso o nó esteja conectado, requisitando que o nó aplique seu próprio padrão através de um comando explícito `{"restore_factory_cal": 1}`.
  3. **Depreciação dos Defaults no Flutter:** Remover os defaults puramente quadráticos do Flutter, unificando a estrutura de calibração para suportar a curva completa de dois segmentos com os termos quárticos.

- **Prioridade:** Alta (Impacto direto na precisão metrológica).
- **Classificação de Segurança:** Integridade de Medição e Qualidade do Processo.

---

### F03 — `FlowOutput` é equivalente L/min, mas Windows mostra `V`

#### Problema Declarado e Causa Raiz
- **Problema:** A variável de telemetria `flow_output` (repassada pelo Hub como `FlowOutput`) representa o esforço de controle calculado pela malha PI em unidades de vazão equivalente ($0 \dots \text{maxFlowRate}$ L/min). Contudo, a interface do Windows App adiciona o sufixo `" V"`, exibindo o valor como se fosse a tensão elétrica analógica do DAC.
- **Localização Exata no Código:**
  - Firmware: `External-Devices/fluxometro/firmware/flowmeter/src/core/Lifecycle.h:177,217,230` (`float newFlowSetpoint = constrain(...); snapOutput = flowSetpoint; snprintf(..., "\"flow_output\":%.6f", snapOutput);`).
  - Firmware: `External-Devices/fluxometro/firmware/flowmeter/src/hardware/FlowIo.h:1-3` (`uint16_t dacValue = (flowSetpointVal / maxFlowRate) * 4095;`).
  - Hub: `ESP32S3-HUB/ESP32S3-HUB/src/network/HttpServer.h:202-203` e `Telemetry.h:280` (`"FlowOutput":String(snapFlowmeterOutput, 4)`).
  - Windows App: `Windows_app/src/OpenTECHub/ViewModels/FlowControlViewModel.cs:690` (`FlowOutputText = snapshot.FlowOutput is { } vo ? vo.ToString("F2", CultureInfo.CurrentCulture) + " V" : "—";`).
  - Documentação do App: `Windows_app/docs/PROTOCOL.md:281` (`| FlowOutput | float | V | Flowmeter controller analog output voltage |`).

#### Impacto Sistêmico Cruzado
- Um setpoint de 25 L/min gera um `FlowOutput` de $\approx 25.0$. A interface gráfica do Windows exibe `"25.00 V"`, o que é fisicamente absurdo para um circuito alimentado a 5V/3.3V com DAC MCP4725 de 12 bits ($0 \dots 5.0\text{ V}$). Isso induz operadores a acreditarem que o DAC está saturado ou avariado.

#### Decisão Técnica de Ação: IMPLEMENTAÇÃO NO WINDOWS APP & DOCUMENTAÇÃO (MANTER FIRMWARE)
- **Ação:** O firmware opera corretamente em unidades de engenharia (L/min). Modificar o firmware para emitir volts quebraria os testes contratuais do Hub e a semântica da malha PI. A correção deve ocorrer exclusivamente na camada de apresentação do Windows App e na especificação documental.

#### Plano de Modificação de Código
1. **Windows App (`FlowControlViewModel.cs` linha 690):**
   ```csharp
   // Substituir a formatação de FlowOutputText por L/min:
   FlowOutputText = snapshot.FlowOutput is { } vo
       ? vo.ToString("F2", CultureInfo.CurrentCulture) + " L/min"
       : "—";
   ```
2. **Windows App XAML (`ControlView.xaml` linha 1446):**
   - Atualizar o rótulo descritivo do campo na interface de *"Tensão de Saída do DAC"* para *"Saída do Controlador (L/min)"*.
   - Caso os engenheiros desejem monitorar a tensão elétrica calculada do DAC na interface, adicionar uma propriedade computada derivada no ViewModel:
     ```csharp
     public string FlowOutputVoltageText => snapshot.FlowOutput is { } vo && snapshot.MaxFlow is { } mf && mf > 0
         ? (vo / mf * 5.0).ToString("F2", CultureInfo.CurrentCulture) + " V"
         : "—";
     ```
3. **Protocolo (`Windows_app/docs/PROTOCOL.md` linha 281):**
   - Corrigir a tabela de telemetria: mudar unidade de `V` para `L/min` e descrição para *"Flowmeter controller PI output (equivalent flow setpoint in L/min)"*.

- **Prioridade:** Média (Correção de interface e documentação, sem risco eletromecânico).
- **Classificação de Segurança:** Interface Homem-Máquina (IHM) / Clareza Operacional.

---

### F04 — 0 < alvo ≤ 0,1 não fecha a linha nem roda PI

#### Problema Declarado e Causa Raiz
- **Problema:** Quando um setpoint na faixa $0 < \text{alvo} \le 0.1\text{ L/min}$ é comandado, o firmware do nó não fecha a válvula de corte geral (`v_Flow`), não zera o DAC e não executa a malha PI. O DAC do MCP4725 permanece congelado no valor de tensão anterior, mantendo a vazão física correspondente indefinidamente.
- **Localização Exata no Código:**
  - Firmware: `External-Devices/fluxometro/firmware/flowmeter/src/protocol/CommandCodec.h:170` (o fechamento da válvula `digitalWrite(VALVE_FLOW_PIN, HIGH)` e corte só ocorrem com `targetFlowSetpoint == 0.0f`).
  - Firmware: `External-Devices/fluxometro/firmware/flowmeter/src/core/FirmwareApp.cpp:125-127` (acumulador persistente é `integralError`, não `integral_term`).
  - Firmware: `External-Devices/fluxometro/firmware/flowmeter/src/core/Lifecycle.h:155, 161, 190, 192`:
    ```cpp
    if (targetFlowSetpoint > 0.1f && valveFlowState == 0) {
      // Executa malha PI e atualiza DAC
    } else if (targetFlowSetpoint > 0.1f) {
      // Congelado aguardando válvula
    } else if (!dacHold) {
      // Só zera o DAC se dacHold for explicitamente falso!
    }
    ```
  - Como `dacHold` possui valor padrão `true` (`DAC_HOLD_DEFAULT = 1.0f`), para qualquer valor $0 < \text{targetFlowSetpoint} \le 0.1$, nenhum bloco é executado, e o hardware mantém o DAC ativo na última tensão aplicada.

#### Impacto Sistêmico Cruzado
- **Cenário Crítico:** O sistema operava a 35 L/min. O operador reduz o setpoint para 0.05 L/min acreditando estar operando em vazão mínima controlada. O firmware aceita o comando, porém não aciona a malha PI e não fecha a válvula. O MFC continua admitindo 35 L/min de gás para o reator de forma desgovernada.
- **Risco de Corrupção por Não-Finitude:** Valores `NaN` ou `±Inf` passados como setpoint avaliam falso em comparações numéricas ordinais C++, contornando limitações e desestabilizando o integrador e o driver DAC.

#### Decisão Técnica de Ação: IMPLEMENTAÇÃO IMEDIATA (CORREÇÃO DE SEGURANÇA CRÍTICA E PRESERVAÇÃO DE dacHold)
- **Ação:**
  1. Padronizar a constante canônica de corte operacional: `#define MIN_FLOW_CUTOFF_THRESHOLD 0.10f` em `FirmwareApp.cpp`.
  2. Implementar validação estrita de finitude numérica (`!isnan(parsedVal) && !isinf(parsedVal)`) antes de qualquer processamento de setpoint.
  3. Para setpoints $\le \text{MIN\_FLOW\_CUTOFF\_THRESHOLD}$ (incluindo $0.0\text{ L/min}$), acionar incondicionalmente o corte mecânico (`valveFlowState = 1; digitalWrite(VALVE_FLOW_PIN, HIGH);`).
  4. Preservar com rigor o contrato de engenharia de `dacHold` (manual §3.2 e §3.4; Omega FMA-5400 §5.5):
     - Se `dacHold == false`: zerar a saída do DAC (`writeFlowSetpointToDAC(0.0f)`), resetar o integrador real (`integralError = 0.0f;` — corrigindo o identificador errôneo `integral_term`), zerar `rampedTarget` e `flowFeedforward`.
     - Se `dacHold == true`: manter a tensão analógica do DAC e o valor de `rampedTarget` inalterados, assegurando que o ponto de operação pré-polarizado do MFC seja restaurado sem sobressalto ao reabrir a válvula.
  5. Na interface do Windows App, rejeitar entradas manuais na faixa $(0.00, 0.10)$ L/min com mensagem instrutiva clara.

#### Plano de Modificação de Código
1. **Firmware (`FirmwareApp.cpp` ~linha 110):**
   ```cpp
   // Limiar operacional mínimo controlável do sensor FMA-5400
   #define MIN_FLOW_CUTOFF_THRESHOLD 0.10f
   ```

2. **Firmware (`CommandCodec.h` linhas 164-183):**
   ```cpp
   else if (strcmp(keyBuf, "flow_setpoint") == 0 || strcmp(keyBuf, "flowSetpoint") == 0) {
     char *endPtr = nullptr;
     float parsedVal = strtof(valBuf, &endPtr);
     if (endPtr == valBuf || isnan(parsedVal) || isinf(parsedVal)) {
       Serial.printf("[CMD] Rejeitado setpoint não-finito ou inválido: '%s'\n", valBuf);
     } else {
       float newTarget = constrain(parsedVal, 0.0f, maxFlowRate);
       recognizedCommand = true;

       // Bloqueio se hardware em falha ou se OTA safe latch estiver ativo
       if ((hardwareFaultLatched || !adsHealthy || !dacHealthy || otaSafeLatch) && newTarget > 0.0f) {
         Serial.println("[SAFETY] Setpoint rejeitado: falha de hardware ou OTA safe latch ativo!");
         newTarget = 0.0f;
       }

       if (newTarget <= MIN_FLOW_CUTOFF_THRESHOLD) {
         targetFlowSetpoint = 0.0f;
         stagedVFlow = 1; // 1 = Corte mecânico acionado (GPIO 5 HIGH)
         valveCommandPresent = true;
         if (!dacHold) {
           flowSetpoint = 0.0f;
           rampedTarget = 0.0f;
           integralError = 0.0f; // Identificador real corrigido
           flowFeedforward = 0.0f;
           writeFlowSetpointToDAC(0.0f);
         }
         Serial.printf("Setpoint <= %.2f: Corte acionado (dacHold=%s)\n",
                       MIN_FLOW_CUTOFF_THRESHOLD, dacHold ? "ON" : "OFF");
       } else {
         targetFlowSetpoint = newTarget;
         Serial.printf("Novo Setpoint Aceito: %.3f L/min\n", targetFlowSetpoint);
       }
     }
   }
   ```

3. **Firmware (`Lifecycle.h` linhas 145-199):**
   ```cpp
   // Tratamento de rampa e slew rate (F04)
   if (targetFlowSetpoint <= MIN_FLOW_CUTOFF_THRESHOLD) {
     if (!dacHold) rampedTarget = 0.0f;
   } else if (rampRate > 0.0f) {
     float step = rampRate * controlInterval / 1000.0f;
     if (rampedTarget < targetFlowSetpoint) rampedTarget = min(rampedTarget + step, targetFlowSetpoint);
     else if (rampedTarget > targetFlowSetpoint) rampedTarget = max(rampedTarget - step, targetFlowSetpoint);
   } else {
     rampedTarget = targetFlowSetpoint;
   }

   if (targetFlowSetpoint > MIN_FLOW_CUTOFF_THRESHOLD && valveFlowState == 0) {
     float error = rampedTarget - readFlowRate;
     flowFeedforward = feedforwardSetpoint(rampedTarget);

     if (abs(error) > 0.01f) {
       integralError += error * integralIntervalScale;

       if (Ki_flow > 0.0f) {
         float iMin = -flowFeedforward / Ki_flow;
         float iMax = (maxFlowRate - flowFeedforward) / Ki_flow;
         integralError = constrain(integralError, iMin, iMax);
       } else {
         integralError = 0.0f;
       }

       float P_term = error * Kp_flow;
       float I_term = integralError * Ki_flow;
       float newFlowSetpoint = constrain(flowFeedforward + P_term + I_term, 0.0f, maxFlowRate);

       if (abs(newFlowSetpoint - flowSetpoint) > dacUpdateThreshold) {
         if (writeFlowSetpointToDAC(newFlowSetpoint)) flowSetpoint = newFlowSetpoint;
       }
       if (debugPI) {
         Serial.printf("[PI] t=%lu Tgt:%.3f Ref:%.3f FF:%.3f Act:%.3f Err:%.3f P:%.3f I:%.3f Out:%.3f\n",
                       now, targetFlowSetpoint, rampedTarget, flowFeedforward, readFlowRate,
                       error, P_term, I_term, flowSetpoint);
       }
     }
   } else if (targetFlowSetpoint > MIN_FLOW_CUTOFF_THRESHOLD) {
     // Alvo ativo com válvula fechada: congela integrador e DAC sem descarregar
   } else if (!dacHold) {
     // Setpoint sub-limiar com dacHold desligado: zera atuador e limpa acumulador
     flowFeedforward = 0.0f;
     integralError = 0.0f; // <-- Identificador real de acumulação em Lifecycle.h
     rampedTarget = 0.0f;
     if (flowSetpoint > 0.0f) {
       if (writeFlowSetpointToDAC(0.0f)) flowSetpoint = 0.0f;
     }
   } else {
     // Setpoint sub-limiar com dacHold ativo:
     // Corte geral mecânico assegurado, tensão do DAC e rampedTarget preservados.
   }
   ```

4. **Windows App (`FlowControlViewModel.cs` linhas 540-555):**
   - Na validação de comando manual de vazão (`TryBuildRequested`), rejeitar valores entre $0.00$ e $0.10\text{ L/min}$ exibindo mensagem explicativa: *"Setpoints entre 0,01 e 0,10 L/min estão abaixo do limiar operacional do FMA-5400. Use 0 para corte ou valores ≥ 0,10 L/min."*

- **Prioridade:** Crítica (Risco iminente de sobrealimentação de gás e vazão descontrolada).
- **Classificação de Segurança:** Intertravamento Primário de Processo (Safety-Critical).

---

### F05 — Setpoint direto positivo não abre `v_Flow`; toggle Flutter “ON” fecha

#### Problema Declarado e Causa Raiz
- **Problema:** Enviar um setpoint de vazão positivo diretamente ao nó (via WebSocket ou Serial) não abre a válvula de corte geral (`v_Flow`), que inicia fechada (`valveFlowState = 1`). Além disso, no aplicativo Flutter, o botão alternador (switch) "Flow Valve ON" envia `v_Flow: 1`, que aciona o corte geral elétrico (GPIO 5 HIGH), cortando o gás enquanto a tela exibe "ON" na cor verde.
- **Localização Exata no Código:**
  - Firmware: `External-Devices/fluxometro/firmware/flowmeter/src/protocol/CommandCodec.h:139-143, 168-179` (receber setpoint positivo nunca altera `valveFlowState`).
  - Firmware: `External-Devices/fluxometro/firmware/flowmeter/src/core/Lifecycle.h:2-4` (inicializa GPIO 5 como OUTPUT, HIGH e `valveFlowState = 1`).
  - Hub: `ESP32S3-HUB/ESP32S3-HUB/src/protocol/Mailboxes.h:242-244` (o Hub já possui um contorno onde infere `desiredFlowValveFlow = (desiredFlowSetpoint > 0.0f) ? 0 : 1` quando `v_Flow` não é especificado).
  - Flutter App: `External-Devices/fluxometro/apps/flutter/lib/main.dart:570-573, 606-613`:
    ```dart
    _buildDataRow('Flow Valve State', valveFlowState == 1 ? 'ON' : 'OFF',
                  valueColor: valveFlowState == 1 ? Colors.green : Colors.red)
    ...
    SwitchListTile(
      title: const Text('Flow Valve'),
      value: valveFlowState == 1,
      onChanged: (val) => _updateValveState('v_Flow', val),
    )
    ```

#### Impacto Sistêmico Cruzado
- Clientes diretos que enviam apenas `{"flow_setpoint": 10.0}` não obtêm fluxo de gás porque o nó não abre a válvula física.
- Usuários do aplicativo Flutter acionam o botão para "ligar a válvula", mas fisicamente cortam o fluxo de gás no MFC, enquanto a interface exibe confirmação visual em verde.

#### Decisão Técnica de Ação: IMPLEMENTAÇÃO EM FIRMWARE E FLUTTER (HUB JÁ PROTEGIDO)
- **Ação:** O Hub e o Windows App já estão protegidos contra essa falha porque o Hub sempre preenche e envia `v_Flow` explicitamente em `buildFlowCommandLocked()`. A correção deve ser implementada no firmware (para comandos diretos) e no app Flutter.

#### Plano de Modificação de Código
1. **Firmware (`CommandCodec.h` após linha 179):**
   ```cpp
   // Se um setpoint positivo válido for recebido e o frame NÃO especificou v_Flow explicitamente:
   if (newTarget > MIN_FLOW_CUTOFF_THRESHOLD && !explicitVFlowInCurrentFrame) {
     valveFlowState = 0; // 0 = Corte desativado (Válvula Aberta)
     digitalWrite(VALVE_FLOW_PIN, LOW);
   }
   ```
2. **Flutter App (`main.dart` linhas 570-573 e 606-613):**
   ```dart
   // Corrigir semântica: valveFlowState == 0 significa Válvula Aberta / Liberada
   _buildDataRow('Corte Geral (Valve Off)', valveFlowState == 1 ? 'ACIONADO (Fechado)' : 'LIBERADO (Aberto)',
                 valueColor: valveFlowState == 1 ? Colors.red : Colors.green)

   // Inverter a lógica do SwitchListTile:
   SwitchListTile(
     title: const Text('Válvula de Fluxo (Aberta)'),
     value: valveFlowState == 0,
     onChanged: _isConnected ? (val) => _updateValveState('v_Flow', val ? 0 : 1) : null,
   )
   ```

- **Prioridade:** Alta (Segurança operacional e eliminação de semântica invertida).
- **Classificação de Segurança:** Usabilidade Crítica e Segurança de Atuação.

---

### F06 — Sem intertravamento de rota no nó

#### Problema Declarado e Causa Raiz
- **Problema:** O firmware do nó aplica os estados das solenoides `v1` (GPIO 17, Rota B+C: Nitrogênio/Descarga) e `v2` (GPIO 16, Rota A: Ar para o Reator) de maneira totalmente independente. O nó aceita comandos que abrem ambas as rotas simultaneamente ($v_1=1, v_2=1$) ou comandos que liberam fluxo de gás com ambas as rotas fechadas ($v_1=0, v_2=0, \text{setpoint} > 0$), gerando linha morta com sobrepressão pneumática. Além disso, comandos parciais (ex.: `{"v1": 1}`) podem mascarar o estado da outra rota se as variáveis de estágio não forem inicializadas com o estado real do hardware.
- **Localização Exata no Código:**
  - Firmware: `External-Devices/fluxometro/firmware/flowmeter/src/protocol/CommandCodec.h:139-153` (`digitalWrite(VALVE1_PIN, valve1State); digitalWrite(VALVE2_PIN, valve2State);`).
  - Hub: `ESP32S3-HUB/ESP32S3-HUB/src/protocol/Mailboxes.h:246-248` (armazena `desiredFlowValve1` e `desiredFlowValve2` sem validar exclusividade mútua).
  - Windows App: `Windows_app/src/OpenTECHub.Protocol/GasRouting.cs:40-105` (possui máquina de estados rigorosa em software que impede comandos conflitantes).

#### Impacto Sistêmico Cruzado
- Um cliente direto (Flutter, script Python ou injeção acidental no Hub) pode enviar `{"v1": 1, "v2": 1}`, misturando a linha de purga/descarga com a entrada do reator biológico.
- Enviar `{"v_Flow": 0, "v1": 0, "v2": 0}` (mesmo com setpoint zero) mantém o corte aberto enquanto as rotas de alívio estão vedadas, aprisionando gás sob alta pressão na linha de montante e arriscando desacoplamento violento de mangueiras.
- Em comandos parciais que omitem uma das válvulas, inicializar estágios com `0` em vez do estado vigente de hardware falseia a avaliação de rotas ativas.

#### Decisão Técnica de Ação: LIBERAÇÃO DE TODAS AS COMBINAÇÕES LÓGICAS NO NÓ E HUB (DECISÃO DE PROJETO)
- **Decisão:** Conforme determinação do projeto, o problema descrito de linha morta ou colisão não existe fisicamente no hardware. Portanto, **nenhuma guarda, restrição ou intertravamento é imposto no firmware do fluxômetro nem no Hub**.
- Qualquer combinação lógica de atuadores ($V_1$, $V_2$, $V_\text{Flow}$) é permitida e executada fielmente pelo firmware e repassada pelo Hub.
- **A responsabilidade de governança de rotas e segurança operacional reside exclusivamente no Windows App** durante os ensaios de potência e $k_L a$ (onde o app já possui diagnósticos visuais como `Gás sem destino` e `A e B/C abertas` sem recusa de envio).
- No firmware, preservou-se o staging transacional seguro (F08) e a inferência de auto-abertura de $V_\text{Flow}$ ao receber setpoint positivo sem especificação explícita de $V_\text{Flow}$ (F05).

- **Prioridade:** Crítica (Integridade física do equipamento e prevenção de sobrepressão).
- **Classificação de Segurança:** Intertravamento de Segurança Pneumática (Safety Interlock).

---

### F07 — Kp/Ki/FF/curva sem validação de faixa/finitude

#### Problema Declarado e Causa Raiz
- **Problema:** Ganhos de controle (`kp_flow`, `ki_flow`), parâmetros de feedforward (`ff_gain`, `ff_offset`), taxa de rampa (`ramp_rate`), fundo de escala (`max_flow`) e coeficientes polinomiais de calibração (`a1..c2`) são convertidos diretamente via `strtof(valBuf, nullptr)` sem validação de finitude (`isnan`, `isinf`) ou de limites operacionais. Qualquer valor malformado, `NaN` ou extremo é aceito e gravado imediatamente na EEPROM, corrompendo a malha de controle em caráter permanente através dos reboots. Além disso, propostas ingênuas de impor limites estreitos (ex.: $0..100$) aos coeficientes de calibração quebram o sistema, pois os termos de calibração reais de fábrica operam na escala de $\pm 10^6$ para compensar a faixa de milivolts na forma de Horner ($A_1 = -1353785.3, B_1 = 246663.69, K_1 = -16473.492$).
- **Localização Exata no Código:**
  - Firmware: `External-Devices/fluxometro/firmware/flowmeter/src/protocol/CommandCodec.h:184-210, 235` (`calParamsUpdated = true; if (calParamsUpdated) saveParameters();`).
  - Firmware: `External-Devices/fluxometro/firmware/flowmeter/src/core/FirmwareApp.cpp:183-190` (coeficientes reais de fábrica: $A_1 \approx -1.35 \times 10^6, B_1 \approx 2.47 \times 10^5, K_1 \approx -1.65 \times 10^4, F_1 \approx 485.0, C_1 \approx -4.62$).
  - Firmware: `External-Devices/fluxometro/firmware/flowmeter/src/hardware/FlowIo.h:31-37` (avaliação polinomial quártica de Horner para $V_{in} \le 0.0545\text{ V}$).
  - Firmware: `External-Devices/fluxometro/firmware/flowmeter/src/storage/CalibrationStore.h:60-64` (`EEPROM.put(CALIBRATION_EEPROM_ADDR, calParams); EEPROM.commit();`).
  - Hub: `ESP32S3-HUB/ESP32S3-HUB/src/protocol/Mailboxes.h:255-265` (`.toFloat()` direto de strings JSON).
  - Windows App: `Windows_app/src/OpenTECHub/ViewModels/FlowControlViewModel.cs:765-799` (já valida limites em software: $0 < K_p \le 100$, $0 \le K_i \le 100$, $0 \le \text{ramp} \le 100$).

#### Impacto Sistêmico Cruzado
- Uma injeção acidental de `{"kp_flow": "NaN"}` ou payload truncado faz com que `calParams.kp` armazene `NaN`. O cálculo de controle em `Lifecycle.h:177` propaga `NaN` para `newFlowSetpoint`, paralisando o cálculo do DAC. Como a EEPROM possui `magic` válido, a cada inicialização o microcontrolador recarrega `NaN`, inutilizando o equipamento até que a EEPROM seja regravada por comando especial.
- Se uma faixa estreita (ex.: $0..100$) for inadvertidamente imposta a todos os floats, qualquer envio de calibração padrão de fábrica ou calculada pelo Windows App (`CalibrationMath.cs`) será sumariamente rejeitado.

#### Decisão Técnica de Ação: IMPLEMENTAÇÃO IMEDIATA (DEFESA EM PROFUNDIDADE COM LIMITES DIFERENCIADOS)
- **Ação:**
  1. Implementar validador numérico rigoroso `parseBoundedFloat(str, minVal, maxVal, outVal)` com checagem de caracteres residuais e rejeição estrita de `NaN` e `±Inf`.
  2. Estabelecer a faixa de validação dos coeficientes polinomiais quárticos e quadráticos (`a1..c2`) em `[-1.0e7f, 1.0e7f]`. Essa escala acomoda confortavelmente os coeficientes reais de fábrica ($|A_1| \approx 1.35 \times 10^6 \ll 10^7$) e curvas laboratoriais íngremes, enquanto rejeita anomalias astronômicas e lixo de memória.
  3. Estabelecer faixas seguras para parâmetros dinâmicos:
     - Ganhos PI: $K_p \in [0.0\text{ f}, 100.0\text{ f}]$, $K_i \in [0.0\text{ f}, 100.0\text{ f}]$.
     - Feedforward: $ff\_gain \in [0.0\text{ f}, 10.0\text{ f}]$; $ff\_offset \in [-5.0\text{ f}, 5.0\text{ f}]$ (permitindo valores negativos indispensáveis para compensar a pré-tensão de abertura da válvula proporcional, cujo padrão de fábrica é $-0.05\text{ f}$).
     - Rampa: $ramp\_rate \in [0.0\text{ f}, 100.0\text{ f}]$ L/min/s.
     - Fundo de escala: $max\_flow \in [0.1\text{ f}, 500.0\text{ f}]$ L/min.
  4. Bloquear qualquer escrita na EEPROM ou mutação na RAM caso qualquer campo do frame contenha valor fora de faixa ou sintaxe inválida.

#### Plano de Modificação de Código

##### Tabela Normativa de Faixas e Tipos
| Parâmetro / Chave | Tipo | Faixa Permitida | Padrão Fábrica | Ação em Caso de Violação |
|---|---|---|---|---|
| `a1` (quártico) | float | `[-1.0e7f, 1.0e7f]` | `-1353785.3f` | Rejeita frame (`frameHasErrors = true`) |
| `b1` (cúbico) | float | `[-1.0e7f, 1.0e7f]` | `+246663.69f` | Rejeita frame (`frameHasErrors = true`) |
| `k1` (quadrático) | float | `[-1.0e7f, 1.0e7f]` | `-16473.492f` | Rejeita frame (`frameHasErrors = true`) |
| `f1` (linear) | float | `[-1.0e7f, 1.0e7f]` | `+484.99466f` | Rejeita frame (`frameHasErrors = true`) |
| `c1` (offset) | float | `[-1.0e7f, 1.0e7f]` | `-4.6159464f` | Rejeita frame (`frameHasErrors = true`) |
| `k2` (quadrático alto) | float | `[-1.0e7f, 1.0e7f]` | `-0.46260458f` | Rejeita frame (`frameHasErrors = true`) |
| `f2` (linear alto) | float | `[-1.0e7f, 1.0e7f]` | `+10.797299f` | Rejeita frame (`frameHasErrors = true`) |
| `c2` (offset alto) | float | `[-1.0e7f, 1.0e7f]` | `+0.28475793f` | Rejeita frame (`frameHasErrors = true`) |
| `kp_flow` | float | `[0.0f, 100.0f]` | `0.4f` | Rejeita frame (`frameHasErrors = true`) |
| `ki_flow` | float | `[0.0f, 100.0f]` | `2.0f` | Rejeita frame (`frameHasErrors = true`) |
| `ff_gain` | float | `[0.0f, 10.0f]` | `0.85f` | Rejeita frame (`frameHasErrors = true`) |
| `ff_offset` | float | `[-5.0f, 5.0f]` | `-0.05f` | Rejeita frame (`frameHasErrors = true`) |
| `ramp_rate` | float | `[0.0f, 100.0f]` | `3.0f` | Rejeita frame (`frameHasErrors = true`) |
| `max_flow` | float | `[0.1f, 500.0f]` | `50.0f` | Rejeita frame (`frameHasErrors = true`) |
| `flow_setpoint` | float | `[0.0f, maxFlowRate]` | `0.0f` | Rejeita frame (`frameHasErrors = true`) |

1. **Firmware (`CommandCodec.h` - Helper de Validação Numérica):**
   ```cpp
   // F07: Parser e validador numérico estrito (rejeição de NaN, Inf, caracteres espúrios e fora de faixa)
   static inline bool parseBoundedFloat(const char* str, float minVal, float maxVal, float* outVal) {
     if (!str || !outVal) return false;
     while (*str && isspace(static_cast<unsigned char>(*str))) str++;
     if (*str == '\0') return false;
     char* endPtr = nullptr;
     float val = strtof(str, &endPtr);
     if (endPtr == str) return false;
     while (*endPtr && isspace(static_cast<unsigned char>(*endPtr))) endPtr++;
     if (*endPtr != '\0') return false; // Rejeita trailing lixo
     if (isnan(val) || isinf(val)) return false;
     if (val < minVal || val > maxVal) return false;
     *outVal = val;
     return true;
   }
   ```

2. **Firmware (`CommandCodec.h` - Aplicação no Parser):**
   ```cpp
   // Exemplo de despacho com faixas validadas:
   else if (strcmp(keyBuf, "kp_flow") == 0) {
     if (parseBoundedFloat(valBuf, 0.0f, 100.0f, &fVal)) {
       staged.hasKp = true; staged.stagedKp = fVal; staged.recognizedAny = true;
     } else {
       Serial.printf("[REJECT] kp_flow fora de faixa (0..100): %s\n", valBuf);
       staged.frameHasErrors = true;
     }
   }
   else if (strcmp(keyBuf, "ff_offset") == 0) {
     if (parseBoundedFloat(valBuf, -5.0f, 5.0f, &fVal)) {
       staged.hasFfOffset = true; staged.stagedFfOffset = fVal; staged.recognizedAny = true;
     } else {
       Serial.printf("[REJECT] ff_offset fora de faixa (-5..5): %s\n", valBuf);
       staged.frameHasErrors = true;
     }
   }
   else if (strcmp(keyBuf, "a1") == 0) {
     if (parseBoundedFloat(valBuf, -1e7f, 1e7f, &fVal)) {
       staged.hasA1 = true; staged.stagedA1 = fVal; staged.recognizedAny = true;
     } else {
       Serial.printf("[REJECT] a1 fora de faixa (+/-1e7): %s\n", valBuf);
       staged.frameHasErrors = true;
     }
   }
   // (Idêntico para b1, k1, f1, c1, k2, f2, c2 na faixa [-1e7f, 1e7f])
   ```

- **Prioridade:** Alta (Prevenção de corrupção de memória não volátil e rejeição de curvas de fábrica).
- **Classificação de Segurança:** Integridade de Dados e Estabilidade da Malha.

---

### F08 — Parser permissivo aplica parcialmente JSON malformado e true vira 0 via atoi

#### Problema Declarado e Causa Raiz
- **Problema:** O parser proprietário em `CommandCodec.h` analisa os pares chave-valor sequencialmente em um laço de busca de caracteres. Se um payload tiver erro de sintaxe no final, as chaves do início já terão atuado diretamente nos pinos GPIO e na RAM. Além disso, chaves booleanas são convertidas via `atoi(valBuf) != 0`. No padrão JSON formal, valores booleanos são representados como `true` ou `false` (ou variações de clientes como `"True"`, `"False"`, `"TRUE"`, `"FALSE"`). Como `atoi("true")` encontra a letra 't', a função C padrão devolve `0`, invertendo o comando e desligando a válvula solicitada.
- **Localização Exata no Código:**
  - Firmware: `External-Devices/fluxometro/firmware/flowmeter/src/protocol/CommandCodec.h:99-138` (laço de varredura manual sem rollback) e linhas 140, 145, 150, 155, 160, 190:
    ```cpp
    valveFlowState = (atoi(valBuf) != 0);
    valve1State = (atoi(valBuf) != 0);
    valve2State = (atoi(valBuf) != 0);
    reconnect_Wifi = (atoi(valBuf) == 1);
    dacHold = (atoi(valBuf) != 0);
    ```
  - App: `Windows_app/src/OpenTECHub.Protocol/OpenTECCommand.cs:74` (contorno defensivo: o App serializa explicitamente booleans como `"1"` ou `"0"`: `public OpenTECCommand Set(string key, bool value) => SetRaw(key, value ? "1" : "0");`).
  - Hub: `ESP32S3-HUB/ESP32S3-HUB/src/protocol/Mailboxes.h:119` (também formata numeric `"1"` e `"0"`).

#### Impacto Sistêmico Cruzado
- Qualquer integração externa moderna (Postman, scripts Python usando `json.dumps({"v1": True})`, dashboards REST, etc.) que envie booleanos nativos ou capitalizados tem a lógica invertida: a válvula é desligada quando se pretendia ligá-la.
- Se um pacote JSON for corrompido no meio da transmissão (ex.: perda de conexão ou truncamento serial), as instruções iniciais já acionaram o hardware antes do erro ser detectado, deixando a bancada em estado inconsistente e potencialmente inseguro.

#### Decisão Técnica de Ação: IMPLEMENTAÇÃO DE PARSER TRANSACIONAL EM 2 FASES COM SUPORTE A LITERAIS BOOLEANOS CASE-INSENSITIVE
- **Ação:**
  1. Implementar função `parseJsonBool(const char* str, bool* outVal)` utilizando `strcasecmp` para reconhecer de forma case-insensitive `"true"`, `"false"`, `"True"`, `"False"`, `"TRUE"`, `"FALSE"`, assim como `"1"` e `"0"`, rejeitando quaisquer strings inválidas.
  2. Implementar arquitetura transacional em duas fases para `processReceivedData`:
     - **Fase 1 (Parsing & Validação em Staging):** Extração de todas as chaves para uma estrutura local na pilha (`StagedCommands`). Nenhum pino GPIO e nenhuma variável global em RAM é tocada. Caso ocorra erro de sintaxe ou qualquer parâmetro viole as faixas permitidas (F07), sinalizar `staged.frameHasErrors = true`.
     - **Fase 2 (Commit Atômico sob Proteção de Mutex):** Se e somente se `!staged.frameHasErrors && staged.recognizedAny`, adquirir o `commandMutex` e aplicar todas as alterações de válvulas, setpoints, parâmetros de sintonia e persistência EEPROM de forma atômica.

#### Plano de Modificação de Código
1. **Firmware (`CommandCodec.h` - Parser Booleano Robusto Case-Insensitive):**
   ```cpp
   #include <strings.h>
   #if defined(_MSC_VER) && !defined(strcasecmp)
   #define strcasecmp _stricmp
   #endif

   // F08: Parser booleano robusto case-insensitive compatível com JSON ("true", "false", "True", "False", "1", "0")
   static inline bool parseJsonBool(const char* str, bool* outVal) {
     if (!str || !outVal) return false;
     while (*str && isspace(static_cast<unsigned char>(*str))) str++;
     if (*str == '\0') return false;

     if (strcasecmp(str, "true") == 0 || strcmp(str, "1") == 0) {
       *outVal = true;
       return true;
     }
     if (strcasecmp(str, "false") == 0 || strcmp(str, "0") == 0) {
       *outVal = false;
       return true;
     }
     return false;
   }
   ```

2. **Firmware (`CommandCodec.h` - Estrutura de Staging e Transação Atômica):**
   ```cpp
   // =========================================================================
   // FASE 1: Parsing e Validação em Staging Area (Sem efeitos colaterais em RAM)
   // =========================================================================
   struct StagedCommands {
     bool hasVFlow = false; bool stagedVFlow = false;
     bool hasV1 = false;    bool stagedV1 = false;
     bool hasV2 = false;    bool stagedV2 = false;
     bool hasReconnectWifi = false; bool stagedReconnectWifi = false;
     bool hasDebugPi = false;       bool stagedDebugPi = false;
     bool hasDacHold = false;       bool stagedDacHold = false;
     bool hasFlowSetpoint = false;  float stagedFlowSetpoint = 0.0f;
     bool hasMaxFlow = false;       float stagedMaxFlow = 0.0f;
     bool hasKp = false;            float stagedKp = 0.0f;
     bool hasKi = false;            float stagedKi = 0.0f;
     bool hasFfGain = false;        float stagedFfGain = 0.0f;
     bool hasFfOffset = false;      float stagedFfOffset = 0.0f;
     bool hasRampRate = false;      float stagedRampRate = 0.0f;
     bool hasA1 = false; float stagedA1 = 0.0f;
     bool hasB1 = false; float stagedB1 = 0.0f;
     bool hasK1 = false; float stagedK1 = 0.0f;
     bool hasF1 = false; float stagedF1 = 0.0f;
     bool hasC1 = false; float stagedC1 = 0.0f;
     bool hasK2 = false; float stagedK2 = 0.0f;
     bool hasF2 = false; float stagedF2 = 0.0f;
     bool hasC2 = false; float stagedC2 = 0.0f;

     bool frameHasErrors = false;
     bool recognizedAny = false;
   } staged;

   // Durante o parsing do JSON, campos preenchem exclusivamente a struct staged.
   // Exemplo:
   if (strcmp(keyBuf, "v1") == 0 || strcmp(keyBuf, "valve_1") == 0) {
     if (parseJsonBool(valBuf, &bVal)) {
       staged.hasV1 = true; staged.stagedV1 = bVal; staged.recognizedAny = true;
     } else {
       Serial.printf("[REJECT] v1 booleano invalido: %s\n", valBuf);
       staged.frameHasErrors = true;
     }
   }

   // =========================================================================
   // FASE 2: Commit Atômico Transacional (Sob Proteção de Mutex)
   // =========================================================================
   if (staged.frameHasErrors || !staged.recognizedAny) {
     if (staged.frameHasErrors) {
       Serial.println("[REJECT] Quadro rejeitado por parametros invalidos; nenhuma alteracao aplicada.");
     }
     return false;
   }

   xSemaphoreTake(commandMutex, portMAX_DELAY);
   // Aplica atômica e exclusivamente parâmetros validados nos pinos e na memória
   if (staged.hasV1) {
     valve1State = staged.stagedV1;
     digitalWrite(VALVE1_PIN, valve1State);
   }
   // (... Aplicação das demais variáveis validadas ...)
   if (calParamsUpdated) saveParameters();
   xSemaphoreGive(commandMutex);
   return true;
   ```

- **Prioridade:** Alta (Robustez de protocolo e compatibilidade com clientes REST/WebSocket padrão).
- **Classificação de Segurança:** Integridade de Protocolo e Prevenção de Estados Parciais.

---

### F09 — `max_flow` não persiste

#### Problema Declarado e Causa Raiz
- **Problema:** A configuração de fim de escala do instrumento (`max_flow`), essencial para a função de transferência do DAC ($V_{out} = \frac{\text{flow}}{\text{maxFlow}} \times 4095$), é mantida apenas na memória RAM volátil (`float maxFlowRate = 50.0;`). Após qualquer reinicialização do ESP32 (queda de energia, watchdog ou atualização OTA), o valor reverte para 50.0 L/min. Embora o Hub detecte o reboot (`rebootDetected`), o Hub limpa a flag `pendingMaxFlow = false` logo no primeiro ACK, de modo que o Hub reimpõe o setpoint, mas não reimpõe `max_flow`.
- **Localização Exata no Código:**
  - Firmware: `External-Devices/fluxometro/firmware/flowmeter/src/core/FirmwareApp.cpp:108` (`float maxFlowRate = 50.0;`) e linhas 164-175 (`struct CalibrationParams` V5 de 60 bytes não possui `max_flow`).
  - Firmware: `External-Devices/fluxometro/firmware/flowmeter/src/storage/CalibrationStore.h:1-53` (ausência de migração do schema V5 para V6 apagaria calibrações de campo via `applyFactoryCurve`, e `loadParameters()` não atribui `maxFlowRate`).
  - Firmware: `External-Devices/fluxometro/firmware/flowmeter/src/hardware/FlowIo.h:1-11` (divisão cega por `maxFlowRate` sem salvaguarda numérica contra 0.0f, NaN ou negativo; sob overflow numérico o cast direto converte para 65535, saturando o DAC no máximo 4095).
  - Firmware: `External-Devices/fluxometro/firmware/flowmeter/src/protocol/CommandCodec.h:195-202` (receber `max_flow` altera RAM, mas não marca `calParamsUpdated = true`).
  - Hub: `ESP32S3-HUB/ESP32S3-HUB/src/network/HttpServer.h:253, 264-275` (ao detectar reboot, não reativa `pendingMaxFlow`).

#### Impacto Sistêmico Cruzado
- Se uma bancada utiliza um MFC de 10 L/min e configurou `max_flow: 10`, após um reboot do nó, `maxFlowRate` volta silenciosamente para 50.0. Quando o Hub reimpõe um setpoint de 10 L/min, o DAC é acionado com $(10 / 50) \times 4095 = 819$ (apenas 20% do fundo de escala), entregando meros 2 L/min em vez dos 10 L/min exigidos pelo bioprocesso.
- **Risco Catastrófico Pneumático:** Se `maxFlowRate` for corrompido com zero ou valor negativo, o cálculo `(flowSetpointVal / maxFlowRate) * 4095` gera `+Inf` ou valor negativo que, ao sofrer cast para `uint16_t` antes do `constrain()`, wrapa para `65535`, saturando o DAC em 4095 (válvula 100% aberta sem controle).

#### Decisão Técnica de Ação: IMPLEMENTAÇÃO EM DUAS FRENTES (PERSISTÊNCIA COM MIGRAÇÃO V5 NO NÓ + REIMPOSIÇÃO NO HUB)
- **Ação:**
  1. No firmware, estender `struct CalibrationParams` com o campo `float max_flow;` (elevando o registro de 60 para 64 bytes) e definir `CALIBRATION_MAGIC_V5 = 0xCAFEBAC3;` e `CALIBRATION_MAGIC = 0xCAFEBAC4;` (Schema v6).
  2. Implementar caminho de migração explícito para `CALIBRATION_MAGIC_V5` em `CalibrationStore.h` que **preserve integralmente** as curvas de calibração laboratoriais (`a1..c2`), sintonia PI, feedforward e rampa existentes no nó, inicializando apenas `calParams.max_flow = 50.0f;`.
  3. Inicializar explicitamente `calParams.max_flow = 50.0f;` no bloco de fallback/reset para padrões de fábrica.
  4. Atribuir obrigatoriamente `maxFlowRate = calParams.max_flow;` ao final de `loadParameters()`.
  5. Proteger `writeFlowSetpointToDAC` em `FlowIo.h` contra divisão por zero, `NaN` e valores negativos, normalizando a fração estritamente em ponto flutuante $[0.0, 1.0]$ antes da quantização para 12 bits.
  6. Corrigir a detecção de reboot no Hub (`HttpServer.h`) para reativar `pendingMaxFlow = true;`.

#### Plano de Modificação de Código
1. **Firmware (`FirmwareApp.cpp` linhas 164-182):**
   ```cpp
   struct CalibrationParams {
     uint32_t magic;
     float a1, b1, k1, f1, c1, k2, f2, c2;
     float kp, ki;
     float ff_gain, ff_offset;
     float ramp_rate, dac_hold;
     float max_flow; // V11 (Schema v6); fundo de escala em L/min (offset 60..63)
   };
   const uint32_t CALIBRATION_MAGIC_V2 = 0xCAFEBAC0;
   const uint32_t CALIBRATION_MAGIC_V3 = 0xCAFEBAC1;
   const uint32_t CALIBRATION_MAGIC_V4 = 0xCAFEBAC2;
   const uint32_t CALIBRATION_MAGIC_V5 = 0xCAFEBAC3;
   const uint32_t CALIBRATION_MAGIC    = 0xCAFEBAC4; // Schema v6
   const float MAX_FLOW_DEFAULT = 50.0f;
   ```

2. **Firmware (`CalibrationStore.h` linhas 1-53):**
   ```cpp
   void loadParameters() {
     EEPROM.get(0, calParams);
     if (calParams.magic == CALIBRATION_MAGIC_V5) {
       // Migração limpa de V5 para V6:
       // Preserva coeficientes de calibração laboratorial (a1..c2), sintonia PI e feedforward.
       calParams.max_flow = MAX_FLOW_DEFAULT;
       calParams.magic = CALIBRATION_MAGIC;
       EEPROM.put(0, calParams);
       EEPROM.commit();
       Serial.println("[EEPROM] Migrado de V5 para V6: calibração preservada; max_flow=50.0 L/min.");
     } else if (calParams.magic == CALIBRATION_MAGIC_V2 ||
                calParams.magic == CALIBRATION_MAGIC_V3 ||
                calParams.magic == CALIBRATION_MAGIC_V4) {
       if (calParams.magic == CALIBRATION_MAGIC_V2) {
         calParams.ff_gain = FF_GAIN_DEFAULT;
         calParams.ff_offset = FF_OFFSET_DEFAULT;
       }
       applyFactoryCurve(calParams);
       calParams.kp = KP_DEFAULT;
       calParams.ki = KI_DEFAULT;
       calParams.ramp_rate = RAMP_RATE_DEFAULT;
       calParams.dac_hold = DAC_HOLD_DEFAULT;
       calParams.max_flow = MAX_FLOW_DEFAULT;
       calParams.magic = CALIBRATION_MAGIC;
       EEPROM.put(0, calParams);
       EEPROM.commit();
       Serial.println("[EEPROM] Registro legado migrado para V6 com defaults.");
     } else if (calParams.magic != CALIBRATION_MAGIC) {
       // Fallback flash virgem: inicialização explícita obrigatória
       calParams.magic = CALIBRATION_MAGIC;
       applyFactoryCurve(calParams);
       calParams.kp = KP_DEFAULT;
       calParams.ki = KI_DEFAULT;
       calParams.ff_gain = FF_GAIN_DEFAULT;
       calParams.ff_offset = FF_OFFSET_DEFAULT;
       calParams.ramp_rate = RAMP_RATE_DEFAULT;
       calParams.dac_hold = DAC_HOLD_DEFAULT;
       calParams.max_flow = MAX_FLOW_DEFAULT; // Inicialização explícita indispensável

       EEPROM.put(0, calParams);
       EEPROM.commit();
       Serial.println("[EEPROM] Parâmetros reinicializados para padrão de fábrica (Schema V6).");
     }

     // Validação defensiva pós-carga contra flash degradada
     if (isnan(calParams.max_flow) || calParams.max_flow <= 0.01f || calParams.max_flow > 200.0f) {
       calParams.max_flow = MAX_FLOW_DEFAULT;
       EEPROM.put(0, calParams);
       EEPROM.commit();
     }

     // Propagação obrigatória para as variáveis ativas em RAM
     a1 = calParams.a1; b1 = calParams.b1; k1 = calParams.k1; f1 = calParams.f1; c1 = calParams.c1;
     k2 = calParams.k2; f2 = calParams.f2; c2 = calParams.c2;
     Kp_flow = calParams.kp; Ki_flow = calParams.ki;
     ffGain = calParams.ff_gain; ffOffset = calParams.ff_offset;
     rampRate = calParams.ramp_rate; dacHold = calParams.dac_hold != 0.0f;
     maxFlowRate = calParams.max_flow; // <-- ATRIBUIÇÃO AO ESTADO ATIVO DO FIRMWARE
   }
   ```

3. **Firmware (`FlowIo.h` linhas 1-11 — Fail-Safe Pneumático no DAC):**
   ```cpp
   bool writeFlowSetpointToDAC(float flowSetpointVal) {
     if (isnan(flowSetpointVal) || flowSetpointVal <= 0.0f) {
       flowSetpointVal = 0.0f;
     }
     float effectiveMax = maxFlowRate;
     if (isnan(effectiveMax) || effectiveMax <= 0.01f) {
       effectiveMax = 50.0f; // Safe fallback para evitar divisão por zero
       Serial.println("[DAC] Aviso: maxFlowRate inválido <= 0.01, adotado fallback 50.0 L/min");
     }

     // Normalização estrita no domínio float [0.0, 1.0] antes de conversão para DAC 12 bits
     float fraction = flowSetpointVal / effectiveMax;
     fraction = constrain(fraction, 0.0f, 1.0f);
     uint16_t dacValue = (uint16_t)constrain((fraction * 4095.0f) + 0.5f, 0.0f, 4095.0f);

     if (i2cMutex != NULL && xSemaphoreTake(i2cMutex, pdMS_TO_TICKS(250)) == pdTRUE) {
       mcp.setVoltage(dacValue, false);
       xSemaphoreGive(i2cMutex);
       return true;
     }
     return false;
   }
   ```

4. **Firmware (`CommandCodec.h` linhas 195-202):**
   ```cpp
   else if (strcmp(keyBuf, "max_flow") == 0 || strcmp(keyBuf, "maxFlow") == 0) {
     float requestedMax = strtof(valBuf, nullptr);
     if (!isnan(requestedMax) && requestedMax > 0.01f && requestedMax <= 200.0f) {
       maxFlowRate = calParams.max_flow = requestedMax;
       targetFlowSetpoint = constrain(targetFlowSetpoint, 0.0f, maxFlowRate);
       calParamsUpdated = true; // Garante gravação imediata na EEPROM
       recognizedCommand = true;
       Serial.printf("[CMD] Atualizado e persistido max_flow: %.2f L/min\n", maxFlowRate);
     }
   }
   ```

5. **Hub (`HttpServer.h` linhas 264-275):**
   ```cpp
   if (rebootDetected) {
     flowCommandRevision++;
     flowmeterLastAck = 0;
     flowCommandAwaitingAck = true;
     pendingMaxFlow = true; // Forçar retransmissão de max_flow na recuperação de reboot!
     pendingFlowmeterCommand = buildFlowCommandLocked();
     rebootRevision = flowCommandRevision;
   }
   ```

- **Prioridade:** Alta (Prevenção de erro crônico de escala de 500% após microquedas de energia).
- **Classificação de Segurança:** Integridade de Atuação e Continuidade Operacional.

---

### F10 — Curva parcial é gravada imediatamente; `k1/f1/c1` sem `a1/b1` zera termos quartic

#### Problema Declarado e Causa Raiz
- **Problema:** Se um cliente envia apenas os parâmetros quadráticos da faixa baixa (`k1`, `f1`, `c1`) sem reenviar explicitamente `a1` e `b1` no mesmo frame, o firmware executa uma heurística de compatibilidade legada que zera incondicionalmente `a1 = 0.0f` e `b1 = 0.0f` e grava a EEPROM. Isso destrói o modelo quártico ancorado de alta precisão que fora calibrado em laboratório.
- **Localização Exata no Código:**
  - Firmware: `External-Devices/fluxometro/firmware/flowmeter/src/protocol/CommandCodec.h:217-220, 235`:
    ```cpp
    if (lowQuadraticUpdated && !lowHigherOrderUpdated) {
      a1 = calParams.a1 = 0.0f;
      b1 = calParams.b1 = 0.0f;
    }
    ...
    if (calParamsUpdated) saveParameters();
    ```
  - Hub: `ESP32S3-HUB/ESP32S3-HUB/src/protocol/Mailboxes.h:124-132` (o Hub mitiga isso serializando `a1` e `b1` antes de `k1..c1`).
  - App: `Windows_app/src/OpenTECHub/ViewModels/FlowCalibrationViewModel.cs:380-410` (permite enviar segmento isolado se o usuário clicar no botão correspondente).
  - Flutter: `External-Devices/fluxometro/apps/flutter/lib/main.dart:375-382` (não suporta `a1`/`b1`; ao salvar calibração pelo Flutter, apaga instantaneamente os termos quárticos no nó).

#### Impacto Sistêmico Cruzado
- Um usuário que ajuste apenas o ponto zero (`c1`) ou que utilize o aplicativo Flutter destrói permanentemente os coeficientes de grau 4 e 3 da memória EEPROM do instrumento, transformando a curva de calibração em quadrática simples com grande descontinuidade no ponto de transição de 0.0545 V.

#### Decisão Técnica de Ação: IMPLEMENTAÇÃO DE MODELO ATÔMICO E ELIMINAÇÃO DO ZERAMENTO AUTOMÁTICO
- **Ação:** Eliminar as linhas 217-220 do firmware. A atualização parcial de coeficientes não deve apagar outros parâmetros existentes. A conversão de modelo de quártico para quadrático deve exigir comando explícito (`"curve_model": "quadratic"`). No Windows App, forçar envio atômico de curva completa.

#### Plano de Modificação de Código
1. **Firmware (`CommandCodec.h`):**
   ```cpp
   // REMOVER AS LINHAS 217-220:
   // - if (lowQuadraticUpdated && !lowHigherOrderUpdated) {
   // -   a1 = calParams.a1 = 0.0f;
   // -   b1 = calParams.b1 = 0.0f;
   // - }
   // Preservar valores prévios de calParams.a1 e calParams.b1 a menos que valor seja explicitamente enviado.
   ```
2. **Windows App (`FlowCalibrationViewModel.cs` linha 380):**
   - Garantir que `SendCurveCommand` só seja executado quando ambos os segmentos estiverem validados (`Curve.IsComplete`), despachando todos os 8 coeficientes simultaneamente.

- **Prioridade:** Alta (Preservação da integridade de calibração).
- **Classificação de Segurança:** Metrologia e Não-Volatilidade.

---

### F11 — ACK de calibração sem readback dos coeficientes

#### Problema Declarado e Causa Raiz
- **Problema:** Quando um comando de calibração é enviado, o ACK retornado via WebSocket ou o `ack_cmd_id` retornado ao Hub confirmam meramente que a string JSON foi recebida pelo parser de texto. Nenhum coeficiente gravado em EEPROM é devolvido no ACK ou na telemetria periódica `/flowData`. O aplicativo cliente exibe "Calibração salva com sucesso" sem qualquer confirmação de que os dados gravados na memória Flash/EEPROM coincidem com os calculados.
- **Localização Exata no Código:**
  - Firmware: `External-Devices/fluxometro/firmware/flowmeter/src/api/WebSocketApi.h:28-36` (ACK só ecoa IDs, setpoint e estados de válvulas).
  - Firmware: `External-Devices/fluxometro/firmware/flowmeter/src/tasks/TaskRuntime.h:124-151` (telemetria `/flowData` repassa ganhos PI, mas omite completamente `a1..c2`).
  - Windows App: `Windows_app/src/OpenTECHub/ViewModels/FlowCalibrationViewModel.cs:474, 505-509` (assume sucesso assim que `snapshot.FlowCommandPending == false`).

#### Impacto Sistêmico Cruzado
- Falhas de gravação em EEPROM (setor desgastado, brownout momentâneo ou corrupção de float) passam despercebidas pelo operador e pelo software supervisório.

#### Decisão Técnica de Ação: IMPLEMENTAÇÃO DE HASH CRC32 EM TELEMETRIA E ENDPOINT DE LEITURA
- **Ação:**
  1. Adicionar um campo `cal_crc` (CRC32 do bloco `CalibrationParams`) na telemetria rápida (/flowData e WebSocket). Não inflar a telemetria periódica de 500 ms com 8 floats de 64 bits para não estourar o orçamento de rede.
  2. Implementar endpoint HTTP dedicado `GET /calibration` no nó para auditoria sob demanda.
  3. No Windows App, comparar o CRC calculado localmente com o `cal_crc` antes de confirmar a calibração ao usuário.

#### Plano de Modificação de Código
1. **Firmware (`CalibrationStore.h`):**
   ```cpp
   uint32_t calculateCalibrationCrc(const CalibrationParams& p) {
     return crc32_le(0, (const uint8_t*)&p + sizeof(p.magic), sizeof(p) - sizeof(p.magic));
   }
   ```
2. **Firmware (`TaskRuntime.h` linha 130 e `WebSocketApi.h`):**
   - Anexar `&cal_crc=%08X` na URL `/flowData`.
3. **Firmware (`OtaService.h` ou `FirmwareApp.cpp`):**
   ```cpp
   server.on("/calibration", HTTP_GET, [](AsyncWebServerRequest *request) {
     char buf[384];
     snprintf(buf, sizeof(buf),
       "{\"a1\":%.6e,\"b1\":%.6e,\"k1\":%.6e,\"f1\":%.6e,\"c1\":%.6e,"
       "\"k2\":%.6e,\"f2\":%.6e,\"c2\":%.6e,\"max_flow\":%.2f,\"crc\":\"%08X\"}",
       calParams.a1, calParams.b1, calParams.k1, calParams.f1, calParams.c1,
       calParams.k2, calParams.f2, calParams.c2, maxFlowRate, currentCalCrc);
     request->send(200, "application/json", buf);
   });
   ```

- **Prioridade:** Média (Confiabilidade e auditabilidade de processo).
- **Classificação de Segurança:** Garantia de Qualidade de Dados (Data Integrity).

---

### F12 — ADS/DAC podem falhar no boot sem bloquear operação ou gerar telemetria de falha

#### Problema Declarado e Causa Raiz
- **Problema:** Caso o conversor ADC ADS1115 (`0x48`) ou o DAC MCP4725 (`0x60`) falhem na inicialização I²C no boot (ou desconectem a quente), o firmware apenas imprime uma mensagem de erro na porta serial (`FAILED (Check wiring!)`) e prossegue com a execução de todas as tarefas FreeRTOS. O tratamento anterior era pontual no boot e não possuía intertravamento contínuo (latch), permitindo que comandos subsequentes reabrissem `valveFlowState = 0` e executassem a malha PI às cegas. Além disso, o driver em `FlowIo.h` não validava o retorno da transmissão I²C do MCP4725, e nenhum bit de falha de hardware era exposto na telemetria. A telemetria continua publicando 0.00 V e 0.00 L/min, dando ao supervisório a falsa impressão de que o nó está saudável e em repouso.
- **Localização Exata no Código:**
  - Firmware: `External-Devices/fluxometro/firmware/flowmeter/src/core/FirmwareApp.cpp:74` (ausência das flags globais de supervisão contínua `adsHealthy`, `dacHealthy`, `hardwareFaultLatched`).
  - Firmware: `External-Devices/fluxometro/firmware/flowmeter/src/core/Lifecycle.h:67-83` (boot não trava estado seguro nem arma latch persistente).
  - Firmware: `External-Devices/fluxometro/firmware/flowmeter/src/core/Lifecycle.h:142` (malha PI executa sem verificar saúde dos periféricos I²C).
  - Firmware: `External-Devices/fluxometro/firmware/flowmeter/src/hardware/FlowIo.h:1-26` (`writeFlowSetpointToDAC` não valida código de erro I²C do MCP4725 via `Wire.endTransmission()`).
  - Hub: `ESP32S3-HUB/ESP32S3-HUB/src/sensor/Telemetry.h:268-270` (não expõe campos de diagnóstico de hardware do fluxômetro).
  - Windows App: `Windows_app/src/OpenTECHub/ViewModels/FlowControlViewModel.cs:663-665` (não possui indicador de falha de hardware para o fluxômetro).

#### Impacto Sistêmico Cruzado
- Se o barramento I²C for desconectado fisicamente ou sofrer queima por ESD, o Hub continuará reportando o nó como online (`FlowmeterOnline = true`). O operador programa a injeção de gás, mas nenhuma atuação física ocorre e nenhuma leitura de vazão é coletada, operando em malha cega com potencial risco de sobrepressão ou contaminação biológica por ausência de aeração.

#### Decisão Técnica de Ação: IMPLEMENTAÇÃO DE INTERTRAVAMENTO CONTÍNUO DE HARDWARE (FAULT LATCH) E PARADA SEGURA
- **Ação:**
  1. Declarar flags globais de supervisão contínua: `bool adsHealthy;`, `bool dacHealthy;`, `bool hardwareFaultLatched;`.
  2. No boot (`Lifecycle.h` em `firmwareSetup`), validar o retorno de `ads.begin(0x48)` e `mcp.begin(0x60)`. Havendo falha em qualquer dispositivo, ativar `hardwareFaultLatched = true;` e forçar corte geral fechado (`valveFlowState = 1; digitalWrite(VALVE_FLOW_PIN, HIGH);`).
  3. No loop de controle (`Lifecycle.h` em `firmwareLoop`), implementar checagem contínua de latch: se `hardwareFaultLatched || !adsHealthy || !dacHealthy`, forçar corte fechado, zerar setpoints e o integrador (`integralError = 0.0f`), e abortar imediatamente o ciclo do PI (`return;`).
  4. Em `FlowIo.h`, checar o código de retorno de `Wire.endTransmission()`. Se houver falha de comunicação com o DAC em tempo de execução, armar imediatamente `dacHealthy = false; hardwareFaultLatched = true;`.
  5. Em `CommandCodec.h`, rejeitar qualquer comando de setpoint positivo se o latch de falha estiver ativo.
  6. Transmitir `&hw_status=%d` na telemetria periódica `/flowData` e sensibilizar alarmes no Hub e no Windows App.

#### Plano de Modificação de Código
1. **Firmware (`FirmwareApp.cpp` linha ~74):**
   ```cpp
   // Flags globais de supervisão contínua e intertravamento de hardware
   bool adsHealthy = false;
   bool dacHealthy = false;
   bool hardwareFaultLatched = false;
   ```

2. **Firmware (`Lifecycle.h` em `firmwareSetup`):**
   ```cpp
   Serial.print("6. Init ADS1115... ");
   adsHealthy = ads.begin(0x48);
   if (adsHealthy) {
     ads.setGain(GAIN_TWOTHIRDS);
     ads.setDataRate(RATE_ADS1115_128SPS);
     Serial.println("OK");
   } else {
     Serial.println("FAILED (Check wiring!)");
   }

   Serial.print("7. Init MCP4725... ");
   dacHealthy = mcp.begin(0x60);
   if (dacHealthy) {
     Serial.println("OK");
   } else {
     Serial.println("FAILED (Check wiring!)");
   }

   // Intertravamento Mandatório de Falha de Hardware no Boot
   if (!adsHealthy || !dacHealthy) {
     hardwareFaultLatched = true;
     valveFlowState = 1; // Garante corte mecânico fechado (GPIO 5 HIGH)
     digitalWrite(VALVE_FLOW_PIN, HIGH);
     Serial.println("[HARDWARE FAULT] Latch ativado no boot: corte fechado e malha PI desativada!");
   }
   ```

3. **Firmware (`Lifecycle.h` em `firmwareLoop` — Latch Contínuo no Ciclo de Controle):**
   ```cpp
   // Intertravamento contínuo de hardware: bloqueia PI e força corte
   if (hardwareFaultLatched || !adsHealthy || !dacHealthy) {
     if (valveFlowState == 0) {
       valveFlowState = 1;
       digitalWrite(VALVE_FLOW_PIN, HIGH);
     }
     targetFlowSetpoint = 0.0f;
     rampedTarget = 0.0f;
     flowSetpoint = 0.0f;
     integralError = 0.0f;
     flowFeedforward = 0.0f;
     xSemaphoreGive(commandMutex);
     return; // Pula integralmente a execução da malha PI
   }
   ```

4. **Firmware (`FlowIo.h` em `writeFlowSetpointToDAC` — Detecção em Tempo de Execução):**
   ```cpp
   bool writeFlowSetpointToDAC(float flowSetpointVal) {
     if (hardwareFaultLatched || !dacHealthy) return false;

     // Sanitização defensiva contra divisão por zero
     float effectiveMax = maxFlowRate;
     if (isnan(effectiveMax) || effectiveMax <= 0.01f) effectiveMax = 50.0f;
     float fraction = constrain(flowSetpointVal / effectiveMax, 0.0f, 1.0f);
     uint16_t dacValue = (uint16_t)constrain((fraction * 4095.0f) + 0.5f, 0.0f, 4095.0f);

     if (i2cMutex != NULL && xSemaphoreTake(i2cMutex, pdMS_TO_TICKS(250)) == pdTRUE) {
       Wire.beginTransmission(0x60);
       byte err = Wire.endTransmission();
       if (err != 0) {
         Serial.printf("[DAC] Falha de comunicacao I2C (%d). Latched!\n", err);
         dacHealthy = false;
         hardwareFaultLatched = true;
         xSemaphoreGive(i2cMutex);
         return false;
       }
       mcp.setVoltage(dacValue, false);
       xSemaphoreGive(i2cMutex);
       return true;
     }
     return false;
   }
   ```

5. **Firmware (`TaskRuntime.h` linha 128):**
   - Transmitir `&hw_status=%d` onde bit 0 = `adsHealthy`, bit 1 = `dacHealthy`, bit 2 = `!hardwareFaultLatched`. Caso haja falha latente, reportar `flow_rate = -1.0f` para acionar alarmes de supervisório.

6. **Hub & Windows App:**
   - No Hub, mapear `hw_status` em `FlowmeterHwFault`. No App, acionar alerta no `AlarmService` bloqueando o envio de setpoints de fluxo enquanto o latch estiver armado.

- **Prioridade:** Crítica (Operação cega de atuador químico).
- **Classificação de Segurança:** Confiabilidade de Hardware e Autodiagnóstico.

---

### F13 — AP/controle/OTA sem autenticação

#### Problema Declarado e Causa Raiz
- **Problema:** O ponto de acesso Wi-Fi gerado pelo microcontrolador (`Floxometro_AP`) opera sem qualquer senha WPA2 (`ap_password = NULL`). Além disso, os serviços HTTP e WebSocket nos endpoints `/ws`, `/update`, `/diag` e `/status` aceitam conexões e ordens de controle sem qualquer cabeçalho de autenticação ou token compartilhado.
- **Localização Exata no Código:**
  - Firmware: `External-Devices/fluxometro/firmware/flowmeter/src/core/FirmwareApp.cpp:19-20` (`const char* ap_ssid = "Floxometro_AP"; const char* ap_password = NULL; // Rede aberta (sem senha)`).
  - Firmware: `External-Devices/fluxometro/firmware/flowmeter/src/api/OtaService.h:4-110` (`server.on("/update", HTTP_POST, ...)` sem autenticação).
  - Firmware: `External-Devices/fluxometro/firmware/flowmeter/src/api/WebSocketApi.h:1-41` (aceita conexões WebSocket na raiz sem handshake autenticado).

#### Impacto Sistêmico Cruzado
- Qualquer pessoa nas proximidades físicas do laboratório com um smartphone ou laptop pode se associar à rede `Floxometro_AP`, abrir `http://192.168.10.1/update`, carregar um firmware malicioso, ou conectar via WebSocket e abrir solenoides de gás tóxico/asfixiante ($N_2$).

#### Decisão Técnica de Ação: NÃO IMPLEMENTAR (DECISÃO DE PROJETO)
- **Decisão:** Por decisão e especificação explícita do projeto, **o ponto de acesso SoftAP e os endpoints HTTP/OTA NÃO devem ter senhas WPA2 nem autenticação básica**. A rede SoftAP e os serviços permanecem abertos para facilidade de comissionamento em bancada e integração de campo sem atrito.
- Nenhuma alteração foi realizada em `FirmwareApp.cpp`, `OtaService.h` ou `WebSocketApi.h` relativa a senhas ou credenciais restritivas.

- **Prioridade:** Dispensada por Decisão de Projeto.
- **Classificação de Segurança:** Aberto por Projeto.

---

### F14 — OTA pausa rede, mas pode manter última saída ativa

#### Problema Declarado e Causa Raiz
- **Problema:** Quando uma atualização de firmware Over-The-Air (OTA) é iniciada pelo endpoint `/update`, o firmware sinaliza `otaInProgress = true`, o que suspende a execução das tarefas de comunicação e controle (`httpTask`, `telemetryTask`, `wifiTask`). No entanto, o manipulador de upload de arquivo (`OtaService.h`) roda concorrentemente na task `AsyncTCP` sem adquirir `commandMutex`, gerando condição de corrida com a `firmwareLoop()` no Core 1. Além disso, se o upload sofrer stall (interrupção de rede) e o watchdog de 90 segundos expirar (`Lifecycle.h:136`), a flag `otaInProgress` é desmarcada, permitindo que a `httpTask` reassuma o polling com o Hub e reaplique silenciosamente o último comando de alta vazão sem supervisão ou intervenção humana.
- **Localização Exata no Código:**
  - Firmware: `External-Devices/fluxometro/firmware/flowmeter/src/core/FirmwareApp.cpp:44` (ausência da flag `volatile bool otaSafeLatch`).
  - Firmware: `External-Devices/fluxometro/firmware/flowmeter/src/api/OtaService.h:74-92` (no bloco `if (index == 0)`, chama `Update.begin(...)` sem adquirir `commandMutex` e sem desenergizar as saídas).
  - Firmware: `External-Devices/fluxometro/firmware/flowmeter/src/core/Lifecycle.h:132-137` (ao detectar stall de OTA por timeout de 90s, zera `otaInProgress = false` liberando a rede do Hub sem travar o estado seguro).
  - Firmware: `External-Devices/fluxometro/firmware/flowmeter/src/tasks/TaskRuntime.h:16, 82, 104` (`if (!otaInProgress && WiFi.status() == WL_CONNECTED)` bloqueia telemetria e polling).

#### Impacto Sistêmico Cruzado
- Se um operador disparar o upload de uma nova versão de firmware enquanto um experimento estiver em andamento a 40 L/min:
  - O Hub perde a comunicação após 6 segundos e marca o nó como offline (`FlowmeterOnline = false`).
  - O operador no Windows App vê o dispositivo como desconectado e não consegue enviar comando de parada de emergência.
  - O gás continua fluindo a 40 L/min no reator desassistido durante toda a transferência e queima do binário na Flash.
  - Se a rede oscilar e o OTA estagnar por 90 segundos, a liberação desgovernada de `otaInProgress` faz o nó retomar imediatamente comandos de vazão do Hub sem confirmação de que o firmware foi ou não alterado.

#### Decisão Técnica de Ação: IMPLEMENTAÇÃO DE PARADA SEGURA SINCRONIZADA POR MUTEX E TRAVA DE STALL (OTA SAFE LATCH)
- **Ação:**
  1. Declarar a flag `volatile bool otaSafeLatch = false;` em `FirmwareApp.cpp`.
  2. No manipulador de upload OTA (`OtaService.h` em `index == 0`), adquirir `commandMutex` com timeout finito (1000 ms) antes de mutar qualquer estado, forçando corte geral mecânico fechado (`valveFlowState = 1; digitalWrite(VALVE_FLOW_PIN, HIGH);`), solenoides de rota desenergizadas (`valve1State = 0; valve2State = 0;`), DAC em 0.0V, e integrador resetado (`integralError = 0.0f;`).
  3. Em caso de timeout de aquisição do mutex, aplicar atuação direta de emergência sobre os pinos de hardware.
  4. No watchdog de stall do OTA (`Lifecycle.h`), ao detectar ausência de chunks por mais de 90 segundos, armar a trava persistente `otaSafeLatch = true;`. Sob essa trava, mesmo que a rede restabeleça o link, comandos periódicos de vazão positiva do Hub são bloqueados até que o operador humano reconheça a falha e resete o alarme.

#### Plano de Modificação de Código
1. **Firmware (`FirmwareApp.cpp` linha ~44):**
   ```cpp
   // Trava persistente de parada segura pós-stall de OTA
   volatile bool otaSafeLatch = false;
   ```

2. **Firmware (`OtaService.h` linhas 74-90 — Safe Stop Atômico Protegido por Mutex):**
   ```cpp
   [](AsyncWebServerRequest *request, String filename, size_t index, uint8_t *data, size_t len, bool final) {
     if (index == 0) {
       otaRejectReason = "";
       if (filename.indexOf("merged") >= 0 || filename.indexOf("bootloader") >= 0 || filename.indexOf("partitions") >= 0) {
         otaRejectReason = "'" + filename + "' is not the app image, send the plain .ino.bin";
         Serial.println("[OTA] " + otaRejectReason);
         return;
       }
       Serial.printf("[OTA] Upload start: %s\n", filename.c_str());

       // --- F14: Safe Stop Atômico Protegido por commandMutex ---
       if (commandMutex != NULL && xSemaphoreTake(commandMutex, pdMS_TO_TICKS(1000)) == pdTRUE) {
         valveFlowState = 1;
         digitalWrite(VALVE_FLOW_PIN, HIGH); // Corte geral fechado
         valve1State = 0;
         digitalWrite(VALVE1_PIN, LOW);       // Rota 1 fechada
         valve2State = 0;
         digitalWrite(VALVE2_PIN, LOW);       // Rota 2 fechada

         targetFlowSetpoint = 0.0f;
         rampedTarget = 0.0f;
         flowSetpoint = 0.0f;
         integralError = 0.0f;
         flowFeedforward = 0.0f;

         writeFlowSetpointToDAC(0.0f);        // Zero V no DAC

         xSemaphoreGive(commandMutex);
         Serial.println("[OTA SAFETY] Safe Stop executado com sucesso sob commandMutex!");
       } else {
         // Fallback de emergência caso haja timeout no mutex
         digitalWrite(VALVE_FLOW_PIN, HIGH);
         digitalWrite(VALVE1_PIN, LOW);
         digitalWrite(VALVE2_PIN, LOW);
         writeFlowSetpointToDAC(0.0f);
         Serial.println("[OTA SAFETY] Timeout de mutex: pinos de hardware forçados para estado seguro!");
       }

       if (Update.isRunning()) Update.abort();
       otaStalled = false;
       otaSafeLatch = false;
       otaNextProgressLog = 0;
       if (!Update.begin(UPDATE_SIZE_UNKNOWN)) Update.printError(Serial);
     }
   ```

3. **Firmware (`Lifecycle.h` linhas 132-137 — Watchdog de Stall com Trava Persistente):**
   ```cpp
   if (otaInProgress && now - otaLastChunkMs > otaStallTimeoutMs) {
     Serial.printf("[OTA] Sem dados por %lus após %u bytes. STALL DETECTADO: travando em SAFE LATCH!\n",
                   otaStallTimeoutMs / 1000, (unsigned)Update.progress());
     otaStalled = true;
     otaSafeLatch = true; // Trava persistente impedindo retomada desgovernada de fluxo
     otaInProgress = false;

     // Garante parada segura mecânica e elétrica sob mutex
     if (commandMutex != NULL && xSemaphoreTake(commandMutex, pdMS_TO_TICKS(200)) == pdTRUE) {
       valveFlowState = 1;
       digitalWrite(VALVE_FLOW_PIN, HIGH);
       valve1State = 0;
       digitalWrite(VALVE1_PIN, LOW);
       valve2State = 0;
       digitalWrite(VALVE2_PIN, LOW);
       targetFlowSetpoint = 0.0f;
       rampedTarget = 0.0f;
       flowSetpoint = 0.0f;
       integralError = 0.0f;
       flowFeedforward = 0.0f;
       writeFlowSetpointToDAC(0.0f);
       xSemaphoreGive(commandMutex);
     }
   }
   ```

- **Prioridade:** Crítica (Segurança física e prevenção de fluxo sem supervisão).
- **Classificação de Segurança:** Intertravamento de Segurança de Manutenção (Maintenance Safety).

---

### F15 — `reconnect_wifi` pode ser desligado, mas Windows só o monitora

#### Problema Declarado e Causa Raiz
- **Problema:** O firmware aceita o comando `reconnect_wifi: 0`, que desliga a máquina de estados de reconexão do Wi-Fi (`wifiTask`). Caso o nó perca o sinal com o Hub, ele nunca mais tentará se associar à rede da estação. Embora a telemetria do Hub repasse `FlowmeterReconnectWifi`, o aplicativo Windows apenas faz o parse desse valor em `SensorReadings.cs` e `TelemetryParser.cs`, sem oferecer qualquer comando no `CommandBuilders.cs` ou elemento gráfico na interface para reativar o serviço.
- **Localização Exata no Código:**
  - Firmware: `External-Devices/fluxometro/firmware/flowmeter/src/protocol/CommandCodec.h:154-158` (`reconnect_Wifi = (atoi(valBuf) == 1);`).
  - Firmware: `External-Devices/fluxometro/firmware/flowmeter/src/tasks/TaskRuntime.h:211-215` (`if (!reconnect_Wifi || otaInProgress) { wifiReconnectState = WF_IDLE; ... continue; }`).
  - Hub: `ESP32S3-HUB/ESP32S3-HUB/src/protocol/Mailboxes.h:123, 216, 251-254` (o Hub possui o suporte para enfileirar `reconnectWifi`).
  - Windows App: `Windows_app/src/OpenTECHub.Protocol/CommandKeys.cs:257` e `TelemetryParser.cs:355-357` (lê e expõe `snapshot.FlowmeterReconnectWifi`, mas `CommandBuilders.cs` não possui método para criá-lo).

#### Impacto Sistêmico Cruzado
- Se o comando for emitido acidentalmente por um cliente direto ou rotina de teste, o nó fica isolado do Hub após a primeira desconexão, exigindo intervenção presencial de técnicos para conectar no SoftAP e reverter o parâmetro.

#### Decisão Técnica de Ação: IMPLEMENTAÇÃO NO WINDOWS APP & WATCHDOG TEMPORAL NO FIRMWARE
- **Ação:**
  1. No Windows App, implementar o método construtor `CommandBuilders.FlowmeterReconnectWifi(bool enable)` e um botão protegido na interface administrativa do `FlowControlViewModel`.
  2. No firmware, implementar um watchdog temporal: caso `reconnect_Wifi` seja configurado como `false`, um timer de segurança de 15 minutos deve reativá-lo automaticamente para evitar o isolamento permanente do nó.

#### Plano de Modificação de Código
1. **Windows App (`CommandBuilders.cs`):**
   ```csharp
   public static OpenTECCommand FlowmeterReconnectWifi(bool enable) =>
       new OpenTECCommand().Set(CommandKeys.FlowmeterReconnectWifi, enable ? "1" : "0");
   ```
2. **Windows App (`FlowControlViewModel.cs`):**
   - Adicionar comando `RelayCommand EnableWifiReconnectCommand` que despacha `CommandBuilders.FlowmeterReconnectWifi(true)` para o Hub.
3. **Firmware (`TaskRuntime.h` em `wifiTask`):**
   ```cpp
   static unsigned long wifiDisabledSinceMs = 0;
   if (!reconnect_Wifi) {
     if (wifiDisabledSinceMs == 0) wifiDisabledSinceMs = millis();
     // Watchdog de 15 minutos (900.000 ms) para autorrecuperação:
     if (millis() - wifiDisabledSinceMs > 900000UL) {
       Serial.println("[WIFI SAFETY] Auto-restaurando reconnect_Wifi = true apos timeout!");
       reconnect_Wifi = true;
       wifiDisabledSinceMs = 0;
     }
   } else {
     wifiDisabledSinceMs = 0;
   }
   ```

- **Prioridade:** Média (Facilidade de recuperação e manutenção remota).
- **Classificação de Segurança:** Resiliência de Rede e Disponibilidade Operacional.

---

### F16 — Válvulas ecoam o bit comandado, sem realimentação

#### Problema Declarado e Causa Raiz
- **Problema:** Os campos de estado das válvulas na telemetria (`valve1State`, `valve2State`, `valveFlowState`) refletem exclusivamente os valores lógicos dos registradores de memória gravados nos pinos GPIO do ESP32. Não existe sensor elétrico de corrente nas bobinas das solenoides, nem chave fim-de-curso mecânica para confirmar o deslocamento físico do êmbolo da válvula. Se a alimentação de 12V/24V das válvulas for desligada, se um fusível abrir, ou se um MOSFET de acionamento falhar, o software reporta "Válvula Aberta" com sucesso enquanto o gás permanece bloqueado.
- **Localização Exata no Código:**
  - Firmware: `External-Devices/fluxometro/firmware/flowmeter/src/protocol/CommandCodec.h:140-153` (`valve1State = ...; digitalWrite(VALVE1_PIN, valve1State);`).
  - Firmware: `External-Devices/fluxometro/firmware/flowmeter/src/core/Lifecycle.h:219-221` (`snapValve1 = valve1State; snapValve2 = valve2State; snapValveFlow = valveFlowState;`).
  - Windows App: `Windows_app/src/OpenTECHub/ViewModels/FlowControlViewModel.cs:680-683` (`ActualValve1 = snapshot.FlowValve1; ActualValve2 = snapshot.FlowValve2; ActualVentValve = snapshot.FlowValveMain;`).

#### Impacto Sistêmico Cruzado
- O operador confia que o reator está recebendo ar ou que a descarga está purgando o sistema porque os indicadores na tela estão verdes, mas nenhuma molécula de gás está se movendo fisicamente por falha de suprimento elétrico dos atuadores.

#### Decisão Técnica de Ação: JUSTIFICATIVA TÉCNICA DE POSTERGAÇÃO DE HARDWARE + DIAGNÓSTICO EM SOFTWARE POR CORRELAÇÃO DE VAZÃO
- **Justificativa:** A inclusão de sensores de corrente shunt ou chaves mecânicas de retorno exige novo projeto eletrônico de PCI (Hardware Rev 2), novos chicotes e novos conectores, sendo inviável exclusivamente por modificação de software no curto prazo.
- **Plano de Implementação em Software (Diagnóstico por Correlação):**
  Implementar no Windows App (`FlowControlViewModel.cs`) e no firmware um algoritmo de supervisão cruzada (*Cross-Check Plausibility Diagnostic*):
  1. **Falha de Atuação Aberta (Válvula Travada Fechada / Sem Força Elétrica):** Se `valveFlowState == 0` (corte aberto) e pelo menos uma rota estiver aberta (`valve1State == 1 || valve2State == 1`), com `targetFlowSetpoint >= 2.0 L/min` por mais de 5 segundos, mas a leitura do sensor de fluxo `readFlowRate < 0.2 L/min`, disparar o alarme: `ALARM_FLOW_ACTUATION_FAILED_NO_FLOW` (*"Comando de vazão ativo, mas vazão nula detectada. Verifique alimentação elétrica das solenoides"*).
  2. **Falha de Atuação Fechada (Vazamento / Válvula Travada Aberta):** Se o corte geral estiver ativo (`valveFlowState == 1`) ou ambas as rotas fechadas por mais de 5 segundos, mas `readFlowRate > 0.5 L/min`, disparar o alarme: `ALARM_FLOW_UNINTENDED_LEAK` (*"Vazão detectada com válvulas comandadas fechadas. Verifique vedação mecânica das válvulas"*).

- **Prioridade:** Média (Melhoria diagnóstica de segurança de processos).
- **Classificação de Segurança:** Diagnóstico de Falha Oculta e Detecção de Anomalias.

---

## 4. Matriz de Compatibilidade e Rastreabilidade (F01 a F16)

| ID | Subsistema Principal Afetado | Ação Proposta | Risco de Quebra (Breaking Change) | Complexidade de Implementação |
|---|---|---|---|---|
| **F01** | Firmware, App, Docs | Implementar fix (unificar macro `"v11.0"`) | Baixo (se App aceitar catálogo flexível) | Baixa |
| **F02** | Metrologia, Firmware, App | Justificativa técnica (congelar nó como base, sincronizar App) | Alto (se alterado sem ensaio) | Média (Metrológica) |
| **F03** | Windows App UI, Docs | Implementar fix (exibir `" L/min"` no App, manter firmware) | Nulo (camada puramente de visualização) | Baixa |
| **F04** | Firmware, Windows App | Implementar fix (corte obrigatório em $\le 0.1$ L/min, dacHold preservado, integralError, finitude) | Baixo (elimina indefinição insegura) | Média |
| **F05** | Firmware, Flutter App | Implementar fix (auto-abertura no nó e inversão no Flutter) | Baixo (Hub e Windows já protegidos) | Baixa |
| **F06** | Firmware, Hub, App | Decisão de Projeto: Liberar qualquer combinação no nó/Hub; responsabilidade exclusiva no Windows App | Nulo | Baixa |
| **F07** | Firmware | Implementar fix (sanitização de float, escala quártica `[-1e7, 1e7]` e clamping de ganhos/rampa) | Baixo (preserva curvas de fábrica e rejeita dados corrompidos) | Média |
| **F08** | Firmware | Implementar fix (staging transacional 2 fases sob commandMutex e parseJsonBool case-insensitive) | Baixo (compatível com `"true"/"false"` e `"1"/"0"`) | Média-Alta |
| **F09** | Firmware, Hub | Implementar fix (persistência EEPROM v6 com migração segura de V5, fail-safe no DAC e retransmissão no Hub) | Médio (bump de schema EEPROM) | Média |
| **F10** | Firmware, Windows App | Implementar fix (remoção de zeramento heurístico de `a1/b1`) | Baixo (preserva modelos calibrados) | Baixa |
| **F11** | Firmware, Hub, App | Implementar fix (telemetria de CRC32 + endpoint `/calibration`) | Baixo (não altera contratos legados) | Média |
| **F12** | Firmware, Hub, App | Implementar fix (latch contínuo de hardware ADS/DAC, corte seguro e bloqueio da malha PI) | Baixo (sensibiliza alarmes existentes) | Média |
| **F13** | Firmware | Decisão de Projeto: NÃO IMPLEMENTAR (SoftAP e OTA abertos para facilidade de comissionamento) | Nulo | Baixa |
| **F14** | Firmware | Implementar fix (Safe Stop atômico sob commandMutex e trava otaSafeLatch contra stall) | Baixo (aumenta segurança do operador) | Baixa |
| **F15** | Windows App, Firmware | Implementar fix (comando no App e watchdog de 15 min no nó) | Baixo (adiciona recurso sem quebrar fluxo) | Baixa |
| **F16** | Windows App, Firmware | Justificativa técnica (Hardware Rev 2) + Diagnóstico em Software | Nulo (apenas novos alarmes de software) | Média |

---

## 5. Roteiro de Execução em Fases (Implementation Roadmap)

A execução técnica dos planos de ação deve obedecer a uma ordem estrita de fases para mitigar riscos operacionais em bancada:

```
┌────────────────────────────────────────────────────────────────────────┐
│  FASE 1: Segurança Operacional Imediata e Intertravamentos Críticos    │
│  - F04: Normalização, corte em <= 0.1 L/min, dacHold e integralError    │
│  - F14: Parada Segura atômica sob mutex e trava otaSafeLatch em stall  │
│  - F06: Liberação de todas as combinações lógicas (governança no App)  │
│  - F12: Monitoramento e latch contínuo de falha I2C (ADS/MCP4725)      │
└───────────────────────────────────┬────────────────────────────────────┘
                                    │
┌───────────────────────────────────▼────────────────────────────────────┐
│  FASE 2: Integridade de Protocolo, Persistência e Dados                │
│  - F07: Sanitização de float, faixa quártica [-1e7, 1e7] e bounds      │
│  - F08: Parser transacional em 2 fases e booleanos case-insensitive    │
│  - F09: Persistência de max_flow na EEPROM v6 (migração V5) e Hub      │
│  - F10: Eliminação do zeramento acidental de termos quárticos a1/b1    │
│  - F05: Auto-abertura de v_Flow no nó e correção semântica no Flutter  │
└───────────────────────────────────┬────────────────────────────────────┘
                                    │
┌───────────────────────────────────▼────────────────────────────────────┐
│  FASE 3: Harmonização de Versão, Interface e Diagnósticos Avançados    │
│  - F01: Unificação da constante de versão "v11.0" em todos os canais   │
│  - F03: Correção de unidade (V -> L/min) na UI e documentação do App   │
│  - F11: Transmissão de CRC de calibração e endpoint /calibration       │
│  - F13: SoftAP/OTA mantidos abertos por decisão de projeto             │
│  - F15: Construtor de comando e UI para reativação de Wi-Fi            │
│  - F16: Algoritmo de diagnóstico cruzado de fluxo vs válvulas no App   │
│  - F02: Campanha de calibração em bancada física com padrão primário   │
└────────────────────────────────────────────────────────────────────────┘
```

---

## 6. Critérios de Aceitação e Procedimento de Verificação

### 6.1 Verificação Estática Automatizada
1. A existência e integridade do arquivo `IMPLEMENTATION_PLAN_FLUXOMETRO.md` é validada pelo script automatizado `verify_plan.py`.
2. O script examina a presença de todas as 16 chaves (`F01` a `F16`), confirmando que cada uma conta com:
   - Identificação formal em cabeçalho.
   - Causa raiz e arquivos afetados detalhados.
   - Plano de modificação técnica de código ou justificativa técnica documentada.
   - Avaliação de risco e impacto.

### 6.2 Verificação de Regressão e Contratos
Após a futura implementação em código-fonte:
1. Executar os testes de contratos do Hub:
   ```powershell
   pytest ESP32S3-HUB/tests/contracts/test_json_keys.py
   pytest ESP32S3-HUB/tests/contracts/test_node_commands.py
   ```
2. Executar a suíte de testes de calibração e rotas do Windows App:
   ```powershell
   dotnet test Windows_app/tests/OpenTECHub.Tests/OpenTECHub.Tests.csproj --filter "FullyQualifiedName~GasRouting|FullyQualifiedName~Calibration"
   ```
3. Realizar os ensaios descritos no checklist de bancada da Seção 3.11 de `COMANDOS_DISPOSITIVOS_EXTERNOS.md`.

---

**Fim do Documento.**
