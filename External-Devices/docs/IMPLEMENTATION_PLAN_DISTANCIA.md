# Plano de Implementação — Correção de Inconsistências do Firmware do Sensor de Distância (v11)

**Data:** 2026-09-12  
**Autor:** Teamwork Implementer / Auditoria de Firmware  
**Alvo:** `External-Devices/sensor-distancia/firmware/distance-sensor`  
**Documentos Relacionados:** `COMANDOS_DISPOSITIVOS_EXTERNOS.md` (§2), `sensor-distancia/docs/PROTOCOL.md`, `ESP32S3-HUB/docs/WIRE_CONTRACT_V9.md`

---

## 1. Sumário Executivo e Objetivos

Durante a auditoria técnica do firmware v11 do nó Sensor de Distância (arquivos `FirmwareApp.cpp`, `ConfigCodec.cpp`, `LocalHttpApi.cpp`, `DistanceSensor.cpp`, `NvsConfig.cpp` e contrato `PROTOCOL.md`), foram identificadas 9 inconsistências/desvios arquiteturais entre a implementação no código C++, o contrato de comunicação de fio e a integração no Hub 10.2 / Windows App.

Este plano detalha as mudanças necessárias no código-fonte para eliminar esses desvios, garantir robustez operacional em bateladas longas e alinhar estritamente o comportamento do firmware às especificações de protocolo.

---

## 2. Catálogo de Inconsistências e Desvios Auditados

| ID | Arquivo Afetado | Linhas | Desvio Identificado | Severidade |
|---|---|:---:|---|:---:|
| **D01** | `src/core/FirmwareApp.cpp` | 118, 151 | **Acoplamento temporal de envio:** O envio do push HTTP está aninhado dentro da condicional do período de amostragem (`SAMPLE_PERIOD_MS`). Se `sample_period > send_period`, o nó não transmite na cadência de envio configurada. | 🟡 Média (Arquitetura) |
| **D02** | `src/protocol/ConfigCodec.cpp` | 88–95 | **Avanço prematuro de `g_lastCmdId`:** O ID de comando é registrado antes de validar os parâmetros. Comandos com chaves desconhecidas ou fora de faixa geram ACK espúrio sem alteração física. | 🟡 Média (Integridade de Protocolo) |
| **D03** | `src/config/BoardConfig.h` / Hub | — | **Assimetria de teto de `send_period`:** O nó e o Hub aceitam `send_period` até 60.000 ms, mas o timeout de presença do Hub é de 3.000 ms (`DISTANCE_PRESENCE_TIMEOUT`), causando oscilação offline com períodos $> 2500\text{ ms}$. | 🟡 Média (Integração) |
| **D04** | `src/api/LocalHttpApi.cpp` | 17 | **Versão legada na interface OTA:** HTML de `/update` declara fixo `DistanceClient r10`, contrariando o banner `DistanceClient r11` e o `/nodeHello` (`ver=v11`). | 🟢 Baixa (Identidade Visual) |
| **D05** | `src/protocol/ConfigCodec.cpp` | 70–78 | **Falta de limites superiores em `applyInt`:** Parâmetros `l1_reinit`, `l2_clear`, `l3_xshut` validam apenas `value <= 0 return false;`. Aceitam qualquer valor positivo sem teto superior seguro. | 🟢 Baixa (Robustez) |
| **D06** | `src/api/LocalHttpApi.cpp` | 51–53 | **HTTP 200 incondicional em POST local:** `handleConfig()` responde 200 "Config Updated" mesmo quando `processConfigUpdate()` retorna `false`. | 🟢 Baixa (API REST Local) |
| **D07** | `docs/PROTOCOL.md` | 133 | **Discrepância de tipo do campo `ota`:** O firmware gera booleano `{"ota":false}` em `/diag`, enquanto `PROTOCOL.md` §5.3 exemplifica string `"ota":"false"`. | 🟢 Baixa (Documentação) |
| **D08** | `src/core/AppContext.cpp` | 24, 28 | **Variável residual `lastGoodRawMm`:** Atualizada no laço de leitura, mas sem consumidores no firmware ou na telemetria. | 🟢 Baixa (Código Morto) |
| **D09** | `src/sensor/DistanceSensor.cpp` | 57–93 | **Sombreamento da escada de recuperação I²C:** `maybeRecover()` avalia `failStreak >= L1` primeiro com `return;`. Sob cooldown compartilhado, L1 intercepta sempre, tornando L2 (Bus Clear) e L3 (XSHUT Power-Cycle) código morto/inalcançável. | 🔴 Alta (Confiabilidade de Hardware) |

---

## 3. Especificação das Mudanças no Código-Fonte

### 3.1 Correção D01: Desacoplamento Temporal dos Laços de Amostragem e Envio HTTP

#### Diagnóstico
No código atual (`FirmwareApp.cpp`):
```cpp
// Código Atual (com defeito de aninhamento):
if (now - lastSampleMs >= SAMPLE_PERIOD_MS) {
    lastSampleMs = now;
    // ... leitura óptica ...
    
    if (WiFi.status() == WL_CONNECTED && now - lastSendMs >= currentSendInterval) {
        lastSendMs = now;
        // ... push HTTP ...
    }
}
```
Se `SAMPLE_PERIOD_MS = 5000` e `SEND_PERIOD_MS = 1000`, a cada segundo o laço de envio não é executado porque o laço pai só roda a cada 5 segundos.

#### Solução Proposta
Tornar os dois laços blocos irmãos independentes no `firmwareLoop()`. A variável `g_lastValidDistance` (ou `distance`) é lida e persistida no laço de amostragem e transmitida de forma desacoplada no laço de envio:

```cpp
// Trecho Proposto para FirmwareApp.cpp:
void firmwareLoop() {
  esp_task_wdt_reset();
  const unsigned long now = millis();
  serviceLocalHttpApi();

  if (g_otaRebootAtMs > 0 && now >= g_otaRebootAtMs) {
    Serial.println("[OTA] Reiniciando no novo firmware...");
    delay(100);
    ESP.restart();
  }

  if (g_otaInProgress) {
    if (now - g_otaLastChunkMs > OTA_STALL_TIMEOUT_MS) {
      Serial.println("[OTA] Watchdog disparado: upload estagnou.");
      Update.abort();
      g_otaInProgress = false;
    }
    delay(1);
    return;
  }

  if (Serial.available() > 0) {
    String command = Serial.readStringUntil('\n');
    command.trim();
    if (!command.isEmpty() && command.startsWith("{")) {
      Serial.println("[CMD] Received: " + command);
      processConfigUpdate(command);
    }
  }

  checkWifi();

  static unsigned long lastHelloCheckMs = 0;
  if (WiFi.status() == WL_CONNECTED && (!g_hubAnnounced || now - lastHelloCheckMs >= 30000)) {
    lastHelloCheckMs = now;
    sendHubHello();
  }

  // --- LAÇO 1: Amostragem Óptica Independente ---
  if (now - lastSampleMs >= SAMPLE_PERIOD_MS) {
    lastSampleMs = now;

    int mm = -1;
    if (readSingleShot(mm)) {
      failStreak = 0;
      if (mm > 0 && mm < 4000) {
        lastGoodRawMm = mm;
      }
    } else {
      ++failStreak;
      maybeRecover();
    }

    float distance = -1.0f;
    if (mm > 0) {
      distance = static_cast<float>(mm) - g_offsetMm;
      if (distance < 0) {
        distance = 0;
      }
    }

    const float seconds = now / 1000.0f;
    Serial.printf("{\"time\":%.1f,\"distance\":%.0f}\n", seconds, distance);
    g_lastValidDistance = distance;
    g_lastSampleTimeSec = seconds;
  }

  // --- LAÇO 2: Envio Periódico de Telemetria (Push HTTP) Desacoplado ---
  unsigned long currentSendInterval = SEND_PERIOD_MS;
  if (g_hubFailStreak > 0) {
    uint8_t shift = (g_hubFailStreak > 4) ? 4 : g_hubFailStreak;
    currentSendInterval = min(SEND_PERIOD_MS * (1UL << shift), MAX_HUB_BACKOFF_MS);
  }

  if (WiFi.status() == WL_CONNECTED && now - lastSendMs >= currentSendInterval) {
    lastSendMs = now;
    const float sendSeconds = now / 1000.0f;
    char url[192];
    snprintf(url, sizeof(url), "%s?distance=%d&time=%.1f&offset=%.2f&sample_ms=%lu&send_ms=%lu&ack_cmd_id=%lu",
             sensorHubURL.c_str(), static_cast<int>(g_lastValidDistance), sendSeconds,
             g_offsetMm, SAMPLE_PERIOD_MS, SEND_PERIOD_MS, static_cast<unsigned long>(g_lastCmdId));
    Serial.print("HTTP GET: ");
    Serial.println(url);
    int code;
    String body;
    if (httpGet(url, code, body)) {
      if (g_hubFailStreak > 0) {
        Serial.printf("[Hub] Conexao restabelecida apos %u falha(s).\n", g_hubFailStreak);
      }
      g_hubFailStreak = 0;
      Serial.printf("Response: %d\n", code);
      if (code == 200 && body.length() > 1 && body[0] == '{') {
        processConfigUpdate(body.c_str());
      }
    } else {
      if (g_hubFailStreak < 255) g_hubFailStreak++;
      Serial.printf("HTTP error: %d \"%s\" (streak=%u, backoff=%lu ms)\n",
                    code, body.c_str(), g_hubFailStreak, currentSendInterval);
    }
  }

  delay(1);
}
```

---

### 3.2 Correção D02 e D05: Validação Robusta e Atualização Condicional de `cmd_id`

#### Diagnóstico
No código atual (`ConfigCodec.cpp:88-95`):
```cpp
long cmdId = getJsonValue(payload, "cmd_id");
if (cmdId > 0 && static_cast<uint32_t>(cmdId) == g_lastCmdId) {
    return true;
}
if (cmdId > 0) {
    g_lastCmdId = static_cast<uint32_t>(cmdId); // ATUALIZADO PREMATURAMENTE!
}
```
Se nenhuma chave for reconhecida no JSON (`seen == false`), `g_lastCmdId` já foi atualizado, gerando ACK enganoso no push subsequente. Além disso, `applyInt` não possui teto superior.

#### Solução Proposta
1. Armazenar `candidateCmdId`.
2. Validar inteiros com limites seguros (`minVal` e `maxVal`).
3. Atualizar `g_lastCmdId` apenas se `seen == true` ou `reset_nvs == 1`:

```cpp
// Trecho Proposto para ConfigCodec.cpp:
namespace {
constexpr long  PERIOD_MIN_MS = 100;
constexpr long  PERIOD_MAX_MS = 60000;
constexpr float OFFSET_MIN_MM = -50.0f;
constexpr float OFFSET_MAX_MM = 200.0f;

// Limites seguros para parâmetros de recuperação I2C
constexpr int   L1_MAX_TRIES  = 50;
constexpr int   L2_MAX_TRIES  = 100;
constexpr int   L3_MAX_TRIES  = 200;

bool applyUlong(const char* payload, const char* key, unsigned long& target,
                long minVal, long maxVal, bool& seen) {
  const long value = getJsonValue(payload, key);
  if (value < minVal || value > maxVal) return false;
  seen = true;
  if (static_cast<unsigned long>(value) == target) return false;
  target = static_cast<unsigned long>(value);
  Serial.printf("Set %s = %lu\n", key, target);
  return true;
}

bool applyInt(const char* payload, const char* key, int& target,
              int minVal, int maxVal, bool& seen) {
  const long value = getJsonValue(payload, key);
  if (value < minVal || value > maxVal) return false;
  seen = true;
  if (static_cast<int>(value) == target) return false;
  target = static_cast<int>(value);
  Serial.printf("Set %s = %d\n", key, target);
  return true;
}
}  // namespace

bool processConfigUpdate(const char* payload) {
  if (!payload) return false;

  long cmdId = getJsonValue(payload, "cmd_id");
  if (cmdId > 0 && static_cast<uint32_t>(cmdId) == g_lastCmdId) {
    Serial.printf("[CMD] cmd_id=%ld ja aplicado; ignorando reentrega.\n", cmdId);
    return true;
  }

  if (getJsonValue(payload, "reset_nvs") == 1) {
    resetNvsConfig();
    SAMPLE_PERIOD_MS = 1000;
    SEND_PERIOD_MS = 1000;
    COOLDOWN_SOFT_MS = 15000;
    COOLDOWN_BUS_MS = 15000;
    COOLDOWN_XSHUT_MS = 30000;
    L1_SOFT_REINIT = 5;
    L2_BUS_CLEAR = 10;
    L3_XSHUT = 20;
    g_offsetMm = BoardConfig::OffsetMm;
    if (cmdId > 0) {
      g_lastCmdId = static_cast<uint32_t>(cmdId);
    }
    Serial.println("[CMD] Reset NVS e restaurou parametros padroes.");
    return true;
  }

  bool seen = false;
  bool changed = false;
  changed |= applyUlong(payload, "sample_period", SAMPLE_PERIOD_MS, PERIOD_MIN_MS, PERIOD_MAX_MS, seen);
  changed |= applyUlong(payload, "send_period",   SEND_PERIOD_MS,   PERIOD_MIN_MS, PERIOD_MAX_MS, seen);
  changed |= applyUlong(payload, "cooldown_soft",  COOLDOWN_SOFT_MS,  1, LONG_MAX, seen);
  changed |= applyUlong(payload, "cooldown_bus",   COOLDOWN_BUS_MS,   1, LONG_MAX, seen);
  changed |= applyUlong(payload, "cooldown_xshut", COOLDOWN_XSHUT_MS, 1, LONG_MAX, seen);
  changed |= applyInt(payload, "l1_reinit", L1_SOFT_REINIT, 1, L1_MAX_TRIES, seen);
  changed |= applyInt(payload, "l2_clear",  L2_BUS_CLEAR,   1, L2_MAX_TRIES, seen);
  changed |= applyInt(payload, "l3_xshut",  L3_XSHUT,       1, L3_MAX_TRIES, seen);

  float offsetVal = 0.0f;
  if (getJsonFloat(payload, "offset_mm", offsetVal)) {
    if (offsetVal >= OFFSET_MIN_MM && offsetVal <= OFFSET_MAX_MM) {
      seen = true;
      if (offsetVal != g_offsetMm) {
        g_offsetMm = offsetVal;
        changed = true;
        Serial.printf("Set g_offsetMm = %.2f\n", offsetVal);
      }
    } else {
      Serial.printf("offset_mm=%.2f fora da faixa [%.0f, %.0f]; ignorado.\n",
                    offsetVal, OFFSET_MIN_MM, OFFSET_MAX_MM);
    }
  }

  // Avança o ACK somente se ao menos uma chave válida foi reconhecida
  if (seen && cmdId > 0) {
    g_lastCmdId = static_cast<uint32_t>(cmdId);
  }

  if (changed) {
    saveNvsConfig();
  } else if (seen) {
    Serial.println("[CMD] Parametros ja vigentes; nada persistido.");
  } else {
    Serial.println("Failed to parse any valid keys from payload.");
  }
  return seen;
}
```

---

### 3.3 Correção D04 e D06: Alinhamento da Interface HTTP Local e Página OTA

#### Diagnóstico
1. `LocalHttpApi.cpp:17` hardcodeia `DistanceClient r10`.
2. `handleConfig()` sempre responde `HTTP 200 "Config Updated"`, mesmo quando nenhuma chave é válida.

#### Solução Proposta
1. Gerar o cabeçalho da página OTA dinamicamente utilizando `BoardConfig::FirmwareTag`.
2. Retornar `HTTP 400 Bad Request` caso `processConfigUpdate()` retorne `false`:

```cpp
// Trecho Proposto para LocalHttpApi.cpp:
void handleConfig() {
  Serial.println("[HTTP] POST /config received");
  if (!server.hasArg("plain")) {
    server.send(400, "text/plain", "Bad Request - No Body");
    return;
  }

  const String body = server.arg("plain");
  Serial.printf("[HTTP] Body: %s\n", body.c_str());
  const bool ok = processConfigUpdate(body.c_str());
  if (ok) {
    server.send(200, "text/plain", "Config Updated");
  } else {
    server.send(400, "text/plain", "Bad Request - Invalid Keys or Range");
  }
}

void handleOtaPage() {
  server.sendHeader("Connection", "close");
  String page = otaPage;
  page.replace("DistanceClient r10", BoardConfig::FirmwareTag);
  server.send(200, "text/html", page);
}
```

---

### 3.4 Correção D03: Diretriz de Integração para `distanceSendPeriodMs`

#### Diagnóstico
No Hub (`ESP32S3-HUB/src/core/AppContext.h:334`):
`const unsigned long DISTANCE_PRESENCE_TIMEOUT = 3000;`
Enquanto em `Commands.h:555`:
`if (period >= 100 && period <= 60000)`

Se o operador configurar `distanceSendPeriodMs` para $5000\text{ ms}$, o Hub marcará o nó como offline após 3000 ms a cada ciclo de 5 segundos.

#### Recomendações
1. **Documentação Operacional (Imediata):** Documentada no `COMANDOS_DISPOSITIVOS_EXTERNOS.md` (§2.6.1 e §2.10 D03) instruindo o operador a nunca configurar envio $> 2500\text{ ms}$.
2. **Harmonização no Hub (Evolução Futura):** Alterar o teto permitido em `Commands.h` para `[100, 2500]` ms, ou calcular a tolerância dinamicamente:
   ```cpp
   unsigned long dynamicTimeout = max(3000UL, (unsigned long)(distanceSendPeriodMs * 2.5f));
   ```

---

### 3.5 Correção D07: Documentação do Tipo Booleano em `PROTOCOL.md`

#### Diagnóstico
O firmware emite `"ota":false` (booleano). O exemplo do `docs/PROTOCOL.md` §5.3 mostrava `"ota": "false"` (string).

#### Solução Proposta
Atualizar `docs/PROTOCOL.md` para refletir `"ota": false` e documentar a independência dos laços de envio e amostragem.

---

### 3.6 Correção D09: Reordenação da Escada de Recuperação em `DistanceSensor.cpp`

#### Diagnóstico
No código ativo de `DistanceSensor.cpp:57-93`:
```cpp
void maybeRecover() {
  const unsigned long now = millis();

  if (failStreak >= L1_SOFT_REINIT && now - lastRecovery >= COOLDOWN_SOFT_MS) {
    Serial.println("[RECOVER] soft re-init");
    sensorInit();
    lastRecovery = now;
    return;
  }

  if (failStreak >= L2_BUS_CLEAR && now - lastRecovery >= COOLDOWN_BUS_MS) {
    Serial.println("[RECOVER] bus clear + re-init");
    ...
    return;
  }

  if (failStreak >= L3_XSHUT && now - lastRecovery >= COOLDOWN_XSHUT_MS) {
    ...
  }
}
```
Como `L1_SOFT_REINIT` (5) < `L2_BUS_CLEAR` (10) < `L3_XSHUT` (20), e `COOLDOWN_SOFT_MS` (15 s) $\le$ `COOLDOWN_BUS_MS` (15 s) < `COOLDOWN_XSHUT_MS` (30 s), qualquer sequência de falhas $\ge 10$ ou $\ge 20$ satisfaz a primeira condicional (`failStreak >= 5`), chamando `return;`.  
Consequentemente, sob a variável única de tempo `lastRecovery`, **os Níveis 2 (Bus Clear) e 3 (XSHUT Power-Cycle) são completamente inalcançáveis (código morto)**. Se a linha I²C travar ou o sensor entrar em latch-up de silício, o firmware ficará eternamente chamando `sensorInit()` a cada 15 s sem nunca tentar liberar o barramento nem reiniciar a alimentação do VL53L0X.

#### Solução Proposta
Avaliar a escada de recuperação em **ordem decrescente de severidade** (do degrau mais drástico para o mais suave), garantindo que falhas persistentes graves executem L3 ou L2 prioritariamente:

```cpp
// Trecho Proposto para DistanceSensor.cpp:
void maybeRecover() {
  const unsigned long now = millis();

  // Nível 3: Hardware Power-Cycle via XSHUT (maior severidade, >= 20 falhas)
  if (failStreak >= L3_XSHUT && now - lastRecovery >= COOLDOWN_XSHUT_MS) {
    Serial.println("[RECOVER] XSHUT power-cycle + re-init");
    if (BoardConfig::SensorXshutPin >= 0) {
      digitalWrite(BoardConfig::SensorXshutPin, LOW);
      delay(10);
      digitalWrite(BoardConfig::SensorXshutPin, HIGH);
      delay(10);
    } else {
      i2cBusClear();
    }
    Wire.end();
    delay(2);
    Wire.begin(BoardConfig::I2cSda, BoardConfig::I2cScl, BoardConfig::I2cFrequencyHz);
    Wire.setTimeOut(BoardConfig::WireTimeoutMs);
    sensorInit();
    lastRecovery = now;
    return;
  }

  // Nível 2: I2C Bus Clear + Re-init (severidade intermediária, >= 10 falhas)
  if (failStreak >= L2_BUS_CLEAR && now - lastRecovery >= COOLDOWN_BUS_MS) {
    Serial.println("[RECOVER] bus clear + re-init");
    i2cBusClear();
    Wire.end();
    delay(2);
    Wire.begin(BoardConfig::I2cSda, BoardConfig::I2cScl, BoardConfig::I2cFrequencyHz);
    Wire.setTimeOut(BoardConfig::WireTimeoutMs);
    sensorInit();
    lastRecovery = now;
    return;
  }

  // Nível 1: Soft Re-init (primeira tentativa suave, >= 5 falhas)
  if (failStreak >= L1_SOFT_REINIT && now - lastRecovery >= COOLDOWN_SOFT_MS) {
    Serial.println("[RECOVER] soft re-init");
    sensorInit();
    lastRecovery = now;
    return;
  }
}
```

---

## 4. Plano de Validação e Testes de Regressão

### 4.1 Testes de Contrato Python (`ESP32S3-HUB/tests/contracts`)
Executar suíte de contratos para garantir conformidade com o protocolo do Hub:
```bash
pytest ESP32S3-HUB/tests/contracts/test_node_commands.py
pytest ESP32S3-HUB/tests/contracts/test_json_keys.py
pytest ESP32S3-HUB/tests/contracts/test_node_registry.py
pytest ESP32S3-HUB/tests/contracts/test_node_diag.py
```
- Critério de sucesso: 81/81 testes aprovados.

### 4.2 Testes Unitários C# Windows App (`Windows_app/tests/OpenTECHub.Tests`)
Executar suíte do aplicativo Windows para verificar consistência dos comandos e desserialização de telemetria:
```bash
dotnet test Windows_app/tests/OpenTECHub.Tests/OpenTECHub.Tests.csproj --filter "FullyQualifiedName~Distance"
dotnet test Windows_app/tests/OpenTECHub.Tests/OpenTECHub.Tests.csproj --filter "FullyQualifiedName~SimulatorNodeConfig"
```
- Critério de sucesso: 10/10 testes de distância e 7/7 testes de configuração de simulador aprovados.

### 4.3 Ensaios de Bancada com Hardware Físico
Seguir rigorosamente o checklist da Seção 2.11 de `COMANDOS_DISPOSITIVOS_EXTERNOS.md`:
1. **Ensaio de Desacoplamento (D01):** Ajustar `sample_period = 200 ms` e `send_period = 2000 ms`; verificar se o push HTTP ocorre pontualmente a cada 2 s com carimbo de uptime `time` atualizado em cada push, enquanto o sensor adquire leituras a 5 Hz.
2. **Ensaio de Deduplicação e Rejeição (D02):** Enviar JSON `{"cmd_id":99,"chave_invalida":123}`; verificar que o nó não avança `g_lastCmdId` para 99 e continua emitindo o ACK anterior.
3. **Ensaio da Escada de Recuperação (D09):** Provocar curto em SDA/GND por 25 s e monitorar serial; verificar a sequência escalonada nominal L1 (5 falhas) $\rightarrow$ L2 (10 falhas) $\rightarrow$ L3 (20 falhas).

---

## 5. Cronograma de Aplicação dos Patches

1. **Fase 1 (Documentação & Auditoria):** ✅ Concluída — Seção 2 de `COMANDOS_DISPOSITIVOS_EXTERNOS.md` preenchida com catálogo completo e 9 desvios (D01 a D09) catalogados.
2. **Fase 2 (Correção de Firmware):** Aplicação dos patches D01, D02, D04, D05, D06 e D09 no repositório de firmware do nó (`src/core/FirmwareApp.cpp`, `src/protocol/ConfigCodec.cpp`, `src/api/LocalHttpApi.cpp`, `src/sensor/DistanceSensor.cpp`).
3. **Fase 3 (Compilação & Teste Local):** Compilação via Arduino CLI / ESP-IDF (verificação de tamanho de partição flash e RAM).
4. **Fase 4 (Ensaio em Bancada Física):** Execução do checklist físico (§2.11).
