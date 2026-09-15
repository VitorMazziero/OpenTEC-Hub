# Relatório de Remediação Técnica — F09: Migração de EEPROM e Persistência de `max_flow`

**Data:** 2026-09-13  
**Agente:** teamwork_preview_explorer (Remedy Explorer 2 — Iteração 2)  
**Destinatário:** Orchestrator / Parent Agent (`b0df3e0f-75ec-45d0-8782-6776cbcdc8ff`)  
**Status:** Concluído — Proposta Técnica Detalhada com Diffs de Produção Prontos para Incorporação  

---

## 1. Observation (Observações Diretas)

### 1.1 Código Atual do Firmware e Layout da Memória EEPROM
- **Arquivo:** `External-Devices/fluxometro/firmware/flowmeter/src/core/FirmwareApp.cpp:108, 164-182`
  - Linha 108: `float maxFlowRate = 50.0;` é declarada como variável global volátil inicializada em RAM.
  - Linhas 164-170:
    ```cpp
    struct CalibrationParams {
      uint32_t magic;
      float a1, b1, k1, f1, c1, k2, f2, c2;
      float kp, ki;
      float ff_gain, ff_offset;   // V08 (schema v3), appended so v2 records migrate in place
      float ramp_rate, dac_hold;  // V10 (schema v5); dac_hold stored as 0/1 in a float
    };
    ```
    Tamanho total da estrutura V5 em memória: `4 + 32 + 8 + 8 + 8 = 60` bytes. O campo `max_flow` não existe na estrutura persistida.
  - Linhas 172-175:
    ```cpp
    const uint32_t CALIBRATION_MAGIC_V2 = 0xCAFEBAC0;
    const uint32_t CALIBRATION_MAGIC_V3 = 0xCAFEBAC1;
    const uint32_t CALIBRATION_MAGIC_V4 = 0xCAFEBAC2;
    const uint32_t CALIBRATION_MAGIC = 0xCAFEBAC3;
    ```
    A versão ativa em campo é V5 (`CALIBRATION_MAGIC = 0xCAFEBAC3`).

### 1.2 Mecanismo de Carga e Migração em `CalibrationStore.h`
- **Arquivo:** `External-Devices/fluxometro/firmware/flowmeter/src/storage/CalibrationStore.h:1-53`
  - Linhas 3-6:
    ```cpp
    bool oldRecord = calParams.magic == CALIBRATION_MAGIC_V2 ||
                     calParams.magic == CALIBRATION_MAGIC_V3 ||
                     calParams.magic == CALIBRATION_MAGIC_V4;
    ```
  - Se `CALIBRATION_MAGIC` for alterado para uma nova versão (ex: `0xCAFEBAC4` ou `0x464C5706`) sem incluir `CALIBRATION_MAGIC_V5` na rotina de migração, um dispositivo em campo gravado com V5 (`0xCAFEBAC3`) entrará diretamente na linha 23:
    ```cpp
    } else if (calParams.magic != CALIBRATION_MAGIC) {
      calParams.magic = CALIBRATION_MAGIC;
      applyFactoryCurve(calParams);
      calParams.kp = KP_DEFAULT;
      calParams.ki = KI_DEFAULT;
      calParams.ff_gain = FF_GAIN_DEFAULT;
      calParams.ff_offset = FF_OFFSET_DEFAULT;
      calParams.ramp_rate = RAMP_RATE_DEFAULT;
      calParams.dac_hold = DAC_HOLD_DEFAULT;
      EEPROM.put(0, calParams);
      EEPROM.commit();
      Serial.println("Calibration parameters reset to defaults.");
    }
    ```
    Isso executa `applyFactoryCurve(calParams)`, **destruindo permanentemente** as curvas de calibração laboratorial (`a1..c2`) e a sintonia do PI gravadas no nó.
  - Além disso, no bloco de fallback/reset acima (linhas 23-35), `calParams.max_flow` **não é inicializado**, ficando com lixo de memória da flash não inicializada.
  - Linhas 39-52: Ao final de `loadParameters()`, as variáveis globais de trabalho são copiadas de `calParams`:
    `a1`, `b1`, `k1`, `f1`, `c1`, `k2`, `f2`, `c2`, `Kp_flow`, `Ki_flow`, `ffGain`, `ffOffset`, `rampRate`, `dacHold`.
    **`maxFlowRate` não é atribuído!** Permanece com o valor padrão de boot `50.0`, ignorando qualquer leitura de EEPROM.

### 1.3 Vulnerabilidade Crítica de Divisão por Zero e Cast em `FlowIo.h`
- **Arquivo:** `External-Devices/fluxometro/firmware/flowmeter/src/hardware/FlowIo.h:1-11`
  ```cpp
  bool writeFlowSetpointToDAC(float flowSetpointVal) {
    uint16_t dacValue = (flowSetpointVal / maxFlowRate) * 4095;
    dacValue = constrain(dacValue, 0, 4095);
    if (i2cMutex != NULL && xSemaphoreTake(i2cMutex, pdMS_TO_TICKS(250)) == pdTRUE) {
      mcp.setVoltage(dacValue, false);
      xSemaphoreGive(i2cMutex);
      return true;
    }
    Serial.println("[DAC] I2C busy, write deferred");
    return false;
  }
  ```
  - Se `maxFlowRate <= 0.01f`, `0.0f` ou `NaN` (oriundo de EEPROM não migrada ou comando corrompido), `(flowSetpointVal / maxFlowRate)` resulta em `+inf`, `-inf` ou `NaN`.
  - Em C++ e na arquitetura Xtensa do ESP32, o cast direto de float infinito ou fora de escala para `uint16_t` acarreta comportamento indefinido ou wrap para `UINT16_MAX` (65535).
  - Em seguida, `constrain((uint16_t)65535, 0, 4095)` resulta em `4095` (tensão máxima do DAC, válvula MFC 100% aberta), o que representa risco catastrófico de sobrepressurização e escape de gases.
  - Da mesma forma, se `flowSetpointVal < 0.0f`, a conversão direta para `uint16_t` antes do `constrain` produz underflow numérico (65535), travando o atuador no valor máximo em vez de zero.

### 1.4 Comando `max_flow` em `CommandCodec.h` Não Persistia em EEPROM
- **Arquivo:** `External-Devices/fluxometro/firmware/flowmeter/src/protocol/CommandCodec.h:195-202, 235`
  ```cpp
  else if (strcmp(keyBuf, "max_flow") == 0 || strcmp(keyBuf, "maxFlow") == 0) {
    float requestedMax = strtof(valBuf, nullptr);
    if (requestedMax > 0.01f) {
      maxFlowRate = requestedMax;
      targetFlowSetpoint = constrain(targetFlowSetpoint, 0.0f, maxFlowRate);
      recognizedCommand = true;
    }
  }
  ```
  O bloco apenas altera a variável volátil `maxFlowRate` na RAM; não atribui `calParams.max_flow` nem sinaliza `calParamsUpdated = true`. Na linha 235 (`if (calParamsUpdated) saveParameters();`), a EEPROM não é gravada.

### 1.5 Hub (`HttpServer.h`) Não Reimpunha `max_flow` Pós-Reboot
- **Arquivo:** `ESP32S3-HUB/ESP32S3-HUB/src/network/HttpServer.h:253, 264-275`
  Ao receber o primeiro ACK, a linha 253 zera `pendingMaxFlow = false`. Na detecção de reboot do nó (linhas 264-275), `pendingMaxFlow` permanecia `false`, de modo que `buildFlowCommandLocked()` montava o frame de recuperação apenas com setpoints e válvulas, sem reenviar `max_flow`.

---

## 2. Logic Chain (Cadeia Lógica de Inferência)

1. **Premissa de Preservação de Calibração em Campo:** Dispositivos instalados em biorreatores e bancadas possuem curvas laboratoriais calibradas contra padrões gravadas no schema V5 (`0xCAFEBAC3`).
   - *Dedução:* A introdução do schema V6 deve conter um caminho de migração explícito para `CALIBRATION_MAGIC_V5`. Esse caminho **não pode** invocar `applyFactoryCurve` nem sobrescrever ganhos PI (`kp`, `ki`), parâmetros de feedforward (`ff_gain`, `ff_offset`) ou configurações de rampa e dac hold (`ramp_rate`, `dac_hold`). Deve apenas estender o registro com `calParams.max_flow = 50.0f;` e atualizar o magic para V6.
2. **Premissa de Estabilidade de Memória Não-Volátil (EEPROM Emulada no ESP32):** O ESP32 emula EEPROM na flash via partição NVS/flash com tamanho alocado de 256 bytes (`EEPROM.begin(256)`).
   - *Dedução:* O acréscimo de um `float` (4 bytes) eleva a estrutura de 60 bytes para 64 bytes, perfeitamente compatível com o espaço alocado. Como o novo campo é posicionado ao final da estrutura, a leitura sequencial `EEPROM.get(0, calParams)` mantém intactos todos os 60 bytes anteriores dos registros V5.
3. **Premissa de Inicialização Determinística em Fallback:** Quando uma placa nova ou recém-gravada inicia com flash apagada (`0xFFFFFFFF`), o magic não bate com nenhuma versão.
   - *Dedução:* O bloco de inicialização padrão (reset para defaults) deve obrigatoriamente inicializar `calParams.max_flow = MAX_FLOW_DEFAULT` (50.0f) antes de gravar em EEPROM. Caso contrário, valores espúrios ou `NaN` seriam gravados e carregados.
4. **Premissa de Acoplamento Correto entre EEPROM e Estado Operacional:** A função `loadParameters()` é responsável por transferir o estado persistido na EEPROM para as variáveis globais do algoritmo de controle.
   - *Dedução:* A variável operacional `maxFlowRate` deve ser explicitamente atribuída a partir de `calParams.max_flow` logo após a leitura, garantindo que reinicializações preservem o fundo de escala configurado pelo usuário.
5. **Premissa de Segurança Defensiva Pneumática (Fail-Safe no DAC):** Em sistemas de controle de vazão de gases, nenhuma anomalia numérica (divisão por zero, overflow, NaN, float negativo) pode induzir saturação máxima no atuador analógico (DAC MCP4725).
   - *Dedução:* Em `FlowIo.h:writeFlowSetpointToDAC`, o divisor `maxFlowRate` deve ser sanitizado contra valores <= 0.01f ou `NaN` com fallback imediato para 50.0f. O cálculo da fração deve ser confinado em ponto flutuante no intervalo [0.0, 1.0] antes de qualquer multiplicação por 4095 e conversão para inteiro, impedindo estritamente overflows e underflows acidentais.

---

## 3. Caveats (Ressalvas e Limitações)

- **Faixa Operacional do MFC:** O MFC suporta fundos de escala entre 0.1 L/min e 200.0 L/min. A validação defensiva estabelece limite inferior de 0.01 L/min e superior de 200.0 L/min.
- **Constante Magic V6:** O valor adotado para `CALIBRATION_MAGIC` (V6) é `0xCAFEBAC4`, mantendo a continuidade lógica do padrão `0xCAFEBAC0` (V2), `0xCAFEBAC1` (V3), `0xCAFEBAC2` (V4), `0xCAFEBAC3` (V5). Caso se opte pelo identificador mnemônico `0x464C5706` ("FLW\x06"), a lógica permanece rigorosamente idêntica.
- **Não-invasão do Código de Produção Durante a Análise:** Em conformidade com o mandato de exploração (read-only), este relatório apresenta os diffs e especificações técnicas sem aplicar alterações diretas aos arquivos de produção do repositório Git.

---

## 4. Conclusion e Recomendações Técnicas de Código (Diffs Exatos)

A auditoria confirmou a procedência total dos apontamentos do Challenger 2 sobre o item F09. A seguir, apresentam-se as modificações exatas recomendadas para o plano de implementação e para a aplicação em código.

### 4.1 Modificação 1 — `FirmwareApp.cpp`
**Localização:** `External-Devices/fluxometro/firmware/flowmeter/src/core/FirmwareApp.cpp:164-182`

```diff
--- a/External-Devices/fluxometro/firmware/flowmeter/src/core/FirmwareApp.cpp
+++ b/External-Devices/fluxometro/firmware/flowmeter/src/core/FirmwareApp.cpp
@@ -164,19 +164,22 @@ uint32_t bootSessionId = 0;
 struct CalibrationParams {
   uint32_t magic;
   float a1, b1, k1, f1, c1, k2, f2, c2;
   float kp, ki;
   float ff_gain, ff_offset;   // V08 (schema v3), appended so v2 records migrate in place
   float ramp_rate, dac_hold;  // V10 (schema v5); dac_hold stored as 0/1 in a float
+  float max_flow;             // V11 (schema v6); full-scale flow rate in L/min
 };
 CalibrationParams calParams;
 const uint32_t CALIBRATION_MAGIC_V2 = 0xCAFEBAC0;
 const uint32_t CALIBRATION_MAGIC_V3 = 0xCAFEBAC1;
 const uint32_t CALIBRATION_MAGIC_V4 = 0xCAFEBAC2;
-const uint32_t CALIBRATION_MAGIC = 0xCAFEBAC3;
+const uint32_t CALIBRATION_MAGIC_V5 = 0xCAFEBAC3;
+const uint32_t CALIBRATION_MAGIC = 0xCAFEBAC4; // Schema v6 (ou 0x464C5706)
+const float MAX_FLOW_DEFAULT = 50.0f;
 const float FF_GAIN_DEFAULT = 0.85f;
 const float FF_OFFSET_DEFAULT = -0.05f;
 const float KP_DEFAULT = 0.4f;
 const float KI_DEFAULT = 2.0f;
 const float RAMP_RATE_DEFAULT = 3.0f;
 const float DAC_HOLD_DEFAULT = 1.0f;
```

### 4.2 Modificação 2 — `CalibrationStore.h`
**Localização:** `External-Devices/fluxometro/firmware/flowmeter/src/storage/CalibrationStore.h:1-53`

```diff
--- a/External-Devices/fluxometro/firmware/flowmeter/src/storage/CalibrationStore.h
+++ b/External-Devices/fluxometro/firmware/flowmeter/src/storage/CalibrationStore.h
@@ -1,42 +1,66 @@
 void loadParameters() {
   EEPROM.get(0, calParams);
-  bool oldRecord = calParams.magic == CALIBRATION_MAGIC_V2 ||
-                   calParams.magic == CALIBRATION_MAGIC_V3 ||
-                   calParams.magic == CALIBRATION_MAGIC_V4;
-  if (oldRecord) {
-    // Same layout prefix. ff_* is valid from v3 on; the curve is deliberately
-    // replaced by the factory one and the PI gains are re-seeded for the V07
-    // measurement chain (the old Ki=0.1 needed a minute to remove 3 L/min).
+  if (calParams.magic == CALIBRATION_MAGIC_V5) {
+    // Migração de V5 para V6:
+    // Preserva integralmente curvas de calibração laboratoriais (a1..c2),
+    // sintonia PI (kp, ki), feedforward (ff_gain, ff_offset), rampa e dac_hold.
+    // Apenas inicializa o novo campo max_flow e atualiza o magic.
+    calParams.max_flow = MAX_FLOW_DEFAULT;
+    calParams.magic = CALIBRATION_MAGIC;
+    EEPROM.put(0, calParams);
+    EEPROM.commit();
+    Serial.println("[EEPROM] Migrated record from v5 to v6: user curves, PI, FF, ramp and hold preserved; max_flow initialized to 50.0 L/min.");
+  } else if (calParams.magic == CALIBRATION_MAGIC_V2 ||
+             calParams.magic == CALIBRATION_MAGIC_V3 ||
+             calParams.magic == CALIBRATION_MAGIC_V4) {
+    // Migração de esquemas legados (v2-v4) para v6:
     if (calParams.magic == CALIBRATION_MAGIC_V2) {
       calParams.ff_gain = FF_GAIN_DEFAULT;
       calParams.ff_offset = FF_OFFSET_DEFAULT;
     }
     applyFactoryCurve(calParams);
     calParams.kp = KP_DEFAULT;
     calParams.ki = KI_DEFAULT;
     calParams.ramp_rate = RAMP_RATE_DEFAULT;
     calParams.dac_hold = DAC_HOLD_DEFAULT;
+    calParams.max_flow = MAX_FLOW_DEFAULT;
     calParams.magic = CALIBRATION_MAGIC;
     EEPROM.put(0, calParams);
     EEPROM.commit();
-    Serial.println("Calibration record migrated to v5: factory curve, Kp/Ki, ramp and hold defaults applied; ff_* kept.");
+    Serial.println("[EEPROM] Legacy record (v2-v4) migrated to v6: factory defaults applied, max_flow=50.0 L/min.");
   } else if (calParams.magic != CALIBRATION_MAGIC) {
+    // Fallback: flash virgem ou magic corrompido -> carrega padrões de fábrica
     calParams.magic = CALIBRATION_MAGIC;
     applyFactoryCurve(calParams);
     calParams.kp = KP_DEFAULT;
     calParams.ki = KI_DEFAULT;
     calParams.ff_gain = FF_GAIN_DEFAULT;
     calParams.ff_offset = FF_OFFSET_DEFAULT;
     calParams.ramp_rate = RAMP_RATE_DEFAULT;
     calParams.dac_hold = DAC_HOLD_DEFAULT;
+    calParams.max_flow = MAX_FLOW_DEFAULT; // Inicialização explícita indispensável
 
     EEPROM.put(0, calParams);
     EEPROM.commit();
-    Serial.println("Calibration parameters reset to defaults.");
+    Serial.println("[EEPROM] Calibration parameters reset to defaults (v6 schema).");
   } else {
-    Serial.println("Calibration parameters loaded.");
+    Serial.println("[EEPROM] Calibration parameters loaded (v6 schema).");
+  }
+
+  // Validação defensiva pós-carga: previne valor nulo, negativo ou espúrio na flash
+  if (isnan(calParams.max_flow) || calParams.max_flow <= 0.01f || calParams.max_flow > 200.0f) {
+    Serial.printf("[EEPROM] Warning: Corrupted max_flow (%.4f) detected, restoring default %.1f L/min\n",
+                  calParams.max_flow, MAX_FLOW_DEFAULT);
+    calParams.max_flow = MAX_FLOW_DEFAULT;
+    EEPROM.put(0, calParams);
+    EEPROM.commit();
   }
+
   a1 = calParams.a1; b1 = calParams.b1;
   k1 = calParams.k1; f1 = calParams.f1; c1 = calParams.c1;
   k2 = calParams.k2; f2 = calParams.f2; c2 = calParams.c2;
 
   Kp_flow = calParams.kp;
   Ki_flow = calParams.ki;
   ffGain = calParams.ff_gain;
   ffOffset = calParams.ff_offset;
   rampRate = calParams.ramp_rate;
   dacHold = calParams.dac_hold != 0.0f;
+  maxFlowRate = calParams.max_flow; // Propagação obrigatória para variável de trabalho
+
   Serial.printf("Feedforward: corrected = %.4f * real + %.4f\n", ffGain, ffOffset);
-  Serial.printf("PI: Kp=%.3f Ki=%.3f  ramp_rate=%.2f L/min/s  dac_hold=%s\n",
-                Kp_flow, Ki_flow, rampRate, dacHold ? "ON" : "OFF");
+  Serial.printf("PI: Kp=%.3f Ki=%.3f  ramp_rate=%.2f L/min/s  dac_hold=%s  max_flow=%.2f L/min\n",
+                Kp_flow, Ki_flow, rampRate, dacHold ? "ON" : "OFF", maxFlowRate);
 }
```

### 4.3 Modificação 3 — `FlowIo.h`
**Localização:** `External-Devices/fluxometro/firmware/flowmeter/src/hardware/FlowIo.h:1-11`

```diff
--- a/External-Devices/fluxometro/firmware/flowmeter/src/hardware/FlowIo.h
+++ b/External-Devices/fluxometro/firmware/flowmeter/src/hardware/FlowIo.h
@@ -1,6 +1,22 @@
 bool writeFlowSetpointToDAC(float flowSetpointVal) {
-  uint16_t dacValue = (flowSetpointVal / maxFlowRate) * 4095;
-  dacValue = constrain(dacValue, 0, 4095);
+  // Sanitização defensiva do setpoint solicitado
+  if (isnan(flowSetpointVal) || flowSetpointVal <= 0.0f) {
+    flowSetpointVal = 0.0f;
+  }
+
+  // Sanitização defensiva contra divisão por zero, NaN ou divisor negativo
+  float effectiveMax = maxFlowRate;
+  if (isnan(effectiveMax) || effectiveMax <= 0.01f) {
+    effectiveMax = 50.0f; // Safe fallback para fundo de escala padrão
+    Serial.println("[DAC] Warning: Invalid maxFlowRate <= 0.01, clamped to 50.0 L/min fallback");
+  }
+
+  // Normalização e confinamento estrito no domínio float [0.0, 1.0]
+  float fraction = flowSetpointVal / effectiveMax;
+  fraction = constrain(fraction, 0.0f, 1.0f);
+
+  // Conversão para DAC de 12 bits (0..4095) com arredondamento seguro
+  uint16_t dacValue = (uint16_t)constrain((fraction * 4095.0f) + 0.5f, 0.0f, 4095.0f);
+
   if (i2cMutex != NULL && xSemaphoreTake(i2cMutex, pdMS_TO_TICKS(250)) == pdTRUE) {
     mcp.setVoltage(dacValue, false);
     xSemaphoreGive(i2cMutex);
```

### 4.4 Modificação 4 — `CommandCodec.h`
**Localização:** `External-Devices/fluxometro/firmware/flowmeter/src/protocol/CommandCodec.h:195-202`

```diff
--- a/External-Devices/fluxometro/firmware/flowmeter/src/protocol/CommandCodec.h
+++ b/External-Devices/fluxometro/firmware/flowmeter/src/protocol/CommandCodec.h
@@ -195,8 +195,12 @@
     else if (strcmp(keyBuf, "max_flow") == 0 || strcmp(keyBuf, "maxFlow") == 0) {
       float requestedMax = strtof(valBuf, nullptr);
-      if (requestedMax > 0.01f) {
-        maxFlowRate = requestedMax;
+      if (!isnan(requestedMax) && requestedMax > 0.01f && requestedMax <= 200.0f) {
+        maxFlowRate = calParams.max_flow = requestedMax;
         targetFlowSetpoint = constrain(targetFlowSetpoint, 0.0f, maxFlowRate);
+        calParamsUpdated = true; // Garante gravação imediata na EEPROM
         recognizedCommand = true;
+        Serial.printf("[Command] Updated and persisted max_flow: %.2f L/min\n", maxFlowRate);
       } else {
         Serial.printf("[Command] Rejected out-of-range max_flow: %s\n", valBuf);
       }
     }
```

### 4.5 Modificação 5 — Hub `HttpServer.h`
**Localização:** `ESP32S3-HUB/ESP32S3-HUB/src/network/HttpServer.h:264-275`

```diff
--- a/ESP32S3-HUB/ESP32S3-HUB/src/network/HttpServer.h
+++ b/ESP32S3-HUB/ESP32S3-HUB/src/network/HttpServer.h
@@ -268,6 +268,7 @@
             flowCommandRevision++;
             if (flowCommandRevision == 0) flowCommandRevision = 1;
             flowCommandAwaitingAck = true;
             flowCommandDeliveryCount = 0;
             flowCommandQueuedAt = millis();
+            pendingMaxFlow = true; // Forçar retransmissão de max_flow na recuperação de reboot!
             pendingFlowmeterCommand = buildFlowCommandLocked();
             rebootRevision = flowCommandRevision;
           }
```

---

## 5. Verification Method (Método de Verificação Independente)

Para auditar e verificar matematicamente e funcionalmente a integridade desta solução:

1. **Auditoria de Offset e Tamanho de Struct (C++ / GCC):**
   - Calcular offsets em 32-bit:
     - `magic`: 0..3 (4B)
     - `a1..c2`: 4..35 (32B)
     - `kp..ki`: 36..43 (8B)
     - `ff_gain..ff_offset`: 44..51 (8B)
     - `ramp_rate..dac_hold`: 52..59 (8B)
     - `max_flow`: 60..63 (4B)
   - Confirmar que os primeiros 60 bytes de um registro V5 correspondem exatamente aos primeiros 60 bytes da struct V6, garantindo que `calParams.a1` a `calParams.dac_hold` mantenham seus valores intactos na migração.

2. **Simulação de Estados Críticos de Entrada no DAC:**
   - **Caso A:** `maxFlowRate = 0.0f`, `flowSetpointVal = 5.0f`:
     - *Comportamento anterior:* Divisão por zero -> `inf` -> `(uint16_t)` -> `constrain(65535, 0, 4095)` -> DAC 4095 (100% aberto).
     - *Comportamento remediado:* `effectiveMax` detecta <= 0.01f, adota 50.0f, fração = 0.1, DAC = 410 (10% aberto, seguro).
   - **Caso B:** `maxFlowRate = 50.0f`, `flowSetpointVal = -2.0f`:
     - *Comportamento anterior:* Divisão negativa -> underflow no cast para `uint16_t` -> DAC 4095.
     - *Comportamento remediado:* `flowSetpointVal <= 0.0f` sanitizado para 0.0f, fração = 0.0, DAC = 0 (atuador fechado).

3. **Validação Estrutural do Documento do Plano:**
   Após a incorporação das seções em `IMPLEMENTATION_PLAN_FLUXOMETRO.md`, reexecutar:
   ```powershell
   python verify_plan.py
   ```
   *Critério de Sucesso:* Exit code 0, confirmando que todos os 16 itens continuam íntegros e verificáveis.