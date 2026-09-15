# Relatório de Remediação Técnica: Inconsistências F07 e F08 do Fluxômetro

**Data:** 2026-09-13  
**Autor:** `teamwork_preview_explorer` (Explorer Remedy 3 — Iteration 2)  
**Diretório de Trabalho:** `.agents/explorer_remedy_3_it2/`  
**Artefatos Gerados:**  
- Relatório de Remediação: `.agents/explorer_remedy_3_it2/handoff.md`  
- Patch Unificado de Código: `.agents/explorer_remedy_3_it2/f07_f08_remedy.patch`  
- Suíte de Testes Automatizada (68 vetores): `.agents/explorer_remedy_3_it2/test_f07_f08_remedy.py`  

---

## 1. Observation (Observações Diretas do Código e Auditoria)

### 1.1 Apontamentos do Relatório Adversarial Challenger 2
Na auditoria realizada pelo agente `challenger_2` (`.agents/challenger_2/handoff.md:87-99, 173-175`), foram destacados os seguintes defeitos de concepção no plano anterior:
> **Obs C - F07 / F08: Limites Numéricos de Calibração e Sintaxe JSON**  
> - No plano (`IMPLEMENTATION_PLAN_FLUXOMETRO.md:384`):  
>   Sugere aplicar limites estreitos: `// Repetir para ki_flow (0..100), ff_gain (0..10), ff_offset (-2..5), ramp_rate (0..100)`.  
> - No código real (`FirmwareApp.cpp:183-187`):  
>   Os coeficientes de calibração reais da fábrica são:  
>   ```cpp
>   const float FACTORY_A1 = -1353785.3f;
>   const float FACTORY_B1 = 246663.69f;
>   const float FACTORY_K1 = -16473.492f;
>   ```  
>   Se uma validação limitada for aplicada a coeficientes de calibração sem a devida escala, os coeficientes válidos de calibração laboratorial serão rejeitados.  
> - No plano (`IMPLEMENTATION_PLAN_FLUXOMETRO.md:425-435`):  
>   A função `parseJsonBool` utiliza `strcmp(str, "true") == 0`, falhando na conversão de variações legítimas de clientes REST/Python como `"True"`, `"False"` ou `"TRUE"`.  
>  
> **Recomendação (§4, item 3):**  
> - Especificar faixas de validação para os coeficientes de calibração que acomodem a magnitude real dos termos quárticos ($\pm 10^7$ para $a_1, b_1, k_1$).  
> - Utilizar comparação case-insensitive (`strcasecmp`) ou parser estrito JSON para valores booleanos (`true`, `false`, `1`, `0`).

---

### 1.2 Código Ativo no Firmware do Fluxômetro

#### [Obs 1.2.1 — Coeficientes de Calibração Reais de Fábrica]
Arquivo: `External-Devices/fluxometro/firmware/flowmeter/src/core/FirmwareApp.cpp:183-190`
```cpp
const float FACTORY_A1 = -1353785.3f;
const float FACTORY_B1 = 246663.69f;
const float FACTORY_K1 = -16473.492f;
const float FACTORY_F1 = 484.99466f;
const float FACTORY_C1 = -4.6159464f;
const float FACTORY_K2 = -0.46260458f;
const float FACTORY_F2 = 10.797299f;
const float FACTORY_C2 = 0.28475793f;
```

#### [Obs 1.2.2 — Avaliação Polinomial Horner na Faixa de Milivolts]
Arquivo: `External-Devices/fluxometro/firmware/flowmeter/src/hardware/FlowIo.h:31-37`
```cpp
  if (readFlowVoltage <= 0.0545f) {
    // Horner form reduces floating-point cancellation at millivolt inputs.
    readFlowRate = ((((a1 * readFlowVoltage + b1) * readFlowVoltage + k1)
                    * readFlowVoltage + f1) * readFlowVoltage + c1);
  } else {
    readFlowRate = k2 * sq(readFlowVoltage) + f2 * readFlowVoltage + c2;
  }
```

#### [Obs 1.2.3 — Conversão Insegura e Inversão de Booleano via `atoi`]
Arquivo: `External-Devices/fluxometro/firmware/flowmeter/src/protocol/CommandCodec.h:139-163, 189-194`
```cpp
    if (strcmp(keyBuf, "v_Flow") == 0 || strcmp(keyBuf, "valveFlow") == 0) {
      valveFlowState = (atoi(valBuf) != 0);
      digitalWrite(VALVE_FLOW_PIN, valveFlowState);
      recognizedCommand = true;
    }
    else if (strcmp(keyBuf, "v1") == 0 || strcmp(keyBuf, "valve_1") == 0) {
      valve1State = (atoi(valBuf) != 0);
      digitalWrite(VALVE1_PIN, valve1State);
      recognizedCommand = true;
    }
    else if (strcmp(keyBuf, "v2") == 0 || strcmp(keyBuf, "valve_2") == 0) {
      valve2State = (atoi(valBuf) != 0);
      digitalWrite(VALVE2_PIN, valve2State);
      recognizedCommand = true;
    }
...
    else if (strcmp(keyBuf, "dac_hold") == 0) {
      dacHold = (atoi(valBuf) != 0);
      calParams.dac_hold = dacHold ? 1.0f : 0.0f;
      calParamsUpdated = true; recognizedCommand = true;
      Serial.printf("DAC hold across zero setpoint: %s\n", dacHold ? "ON" : "OFF");
    }
```
Na linguagem C standard, `atoi("true")` encontra a letra `'t'` e devolve `0`. Consequentemente, `(atoi("true") != 0)` avalia como `false` (`0`), desligando a válvula quando o cliente solicitou explicitamente ligá-la.

#### [Obs 1.2.4 — Falta de Sanitização e Mutação sem Estágio]
Arquivo: `External-Devices/fluxometro/firmware/flowmeter/src/protocol/CommandCodec.h:184-210`
```cpp
    else if (strcmp(keyBuf, "kp_flow") == 0) { Kp_flow = calParams.kp = strtof(valBuf, nullptr); calParamsUpdated = true; recognizedCommand = true; }
    else if (strcmp(keyBuf, "ki_flow") == 0) { Ki_flow = calParams.ki = strtof(valBuf, nullptr); calParamsUpdated = true; recognizedCommand = true; }
    else if (strcmp(keyBuf, "ff_gain") == 0) { ffGain = calParams.ff_gain = strtof(valBuf, nullptr); calParamsUpdated = true; recognizedCommand = true; }
    else if (strcmp(keyBuf, "ff_offset") == 0) { ffOffset = calParams.ff_offset = strtof(valBuf, nullptr); calParamsUpdated = true; recognizedCommand = true; }
    else if (strcmp(keyBuf, "ramp_rate") == 0) { rampRate = calParams.ramp_rate = max(0.0f, strtof(valBuf, nullptr)); calParamsUpdated = true; recognizedCommand = true; }
```
Não há checagem de `isnan()` nem `isinf()`, nem validação de limites físicos. Valores malformados corrompem as variáveis ativas em RAM e persistem em EEPROM via `saveParameters()`.

---

## 2. Logic Chain (Cadeia Lógica de Dedução e Engenharia)

### 2.1 Dedução da Escala Numérica Quártica de Calibração (F07)
1. **Origem Física:** Na faixa de baixa vazão ($V_{in} \le 0.0545\text{ V} = 54.5\text{ mV}$), a resposta do sensor Omega FMA-5400 é fortemente não-linear. O modelo matemático utiliza um polinômio quártico ($4^\text{a}$ ordem):
   $$Q(V) = a_1 V^4 + b_1 V^3 + k_1 V^2 + f_1 V + c_1$$
2. **Ordem de Grandeza dos Termos:**
   - Para $V \approx 0.02\text{ V}$ ($20\text{ mV}$), temos $V^4 \approx 1.6 \times 10^{-7}\text{ V}^4$.
   - Para que o termo $a_1 V^4$ produza uma contribuição mensurável em litros por minuto (ex.: $0.2$ a $2\text{ L/min}$), o coeficiente $a_1$ obrigatoriamente deve ter magnitude na ordem de $10^6$:
     $$a_1 = -1.3537853 \times 10^6 \implies a_1 \times (0.02)^4 \approx -0.2166\text{ L/min}$$
   - Coeficiente cúbico: $V^3 \approx 8 \times 10^{-6} \implies b_1 = +2.4666369 \times 10^5 \implies b_1 \times (0.02)^3 \approx +1.973\text{ L/min}$.
   - Coeficiente quadrático: $V^2 \approx 4 \times 10^{-4} \implies k_1 = -1.6473492 \times 10^4 \implies k_1 \times (0.02)^2 \approx -6.589\text{ L/min}$.
3. **Consequência para Limites de Validação:**
   - Qualquer limite estreito (como $0..100$) rejeita imediatamente tanto os coeficientes de calibração padrão de fábrica quanto os ajustes calculados pelo aplicativo Windows (`CalibrationMath.cs`).
   - Um intervalo simétrico de $\pm 1.0 \times 10^7$ (`[-1e7f, 1e7f]`):
     - Acomoda confortavelmente os valores de fábrica ($|a_1| \approx 1.35 \times 10^6 \ll 10^7$).
     - Fornece margem para sensores com curvas mais íngremes ($|a_1|$ até $5 \times 10^6$).
     - Rejeita valores astronômicos (ex.: $10^{15}$), valores corrompidos por memória não-inicializada e `NaN`/`Inf`.

### 2.2 Dedução das Faixas Seguras de Controle PI, Feedforward e Rampa (F07)
1. **Ganho Proporcional (`kp_flow`):**
   - O sinal de controle ajusta a vazão desejada ($0$ a $50\text{ L/min}$). Se $K_p > 100$, um erro residual minúsculo de $0.5\text{ L/min}$ gera saturação instantânea de $+50\text{ L/min}$ no DAC, desestabilizando o loop de controle de 100 ms. Faixa segura: $[0.0\text{ f}, 100.0\text{ f}]$.
2. **Ganho Integral (`ki_flow`):**
   - A integral acumula a cada ciclo ($dt = 0.1\text{ s}$). Um $K_i > 100$ causa windup violento em menos de 1 segundo. Faixa segura: $[0.0\text{ f}, 100.0\text{ f}]$.
3. **Ganho Feedforward (`ff_gain`):**
   - Multiplicador direto do setpoint. Em condições ideais de projeto, $ff\_gain \approx 0.85$ a $1.0$. Um limite superior de $10.0\text{ f}$ protege contra comandos espúrios de alta tensão no DAC. Faixa segura: $[0.0\text{ f}, 10.0\text{ f}]$.
4. **Offset Feedforward (`ff_offset`):**
   - O valor de fábrica é negativo (`FF_OFFSET_DEFAULT = -0.05f`) para compensar o ponto de abertura da válvula proporcional. Portanto, a faixa **não pode** ser restrita a valores positivos. Limite seguro: $[-5.0\text{ f}, 5.0\text{ f}]$.
5. **Taxa de Rampa (`ramp_rate`):**
   - Variação máxima de setpoint em L/min por segundo. $0.0\text{ f}$ desativa a rampa (degrau imediato); $100.0\text{ f}$ atinge o fim de escala (50 L/min) em 0.5 s. Faixa segura: $[0.0\text{ f}, 100.0\text{ f}]$.
6. **Capacidade Máxima (`max_flow`):**
   - O atuador DAC calcula: `(flowSetpointVal / maxFlowRate) * 4095`. Se `maxFlowRate <= 0`, ocorre divisão por zero resultando em `NaN`/`Inf`. Faixa segura: $[0.1\text{ f}, 500.0\text{ f}]$.

### 2.3 Dedução do Parser Booleano Case-Insensitive (F08)
1. **Diagnóstico do Defeito:**
   - Comandos originados de scripts Python (`json.dumps({"v1": True})`) produzem literais com maiúscula inicial (`"True"`, `"False"`).
   - Clientes HTTP REST ou utilitários de bancada frequentemente emitem `"TRUE"`, `"FALSE"`, `"1"` ou `"0"`.
   - O código original utilizava `atoi(valBuf) != 0`, onde `atoi("true") == 0` invertia a lógica da válvula solicitada.
   - O plano anterior propôs `strcmp(str, "true") == 0`, o que falhava para `"True"` e `"TRUE"`.
2. **Solução Definitiva:**
   - A função `strcasecmp()` (POSIX standard, presente no GCC Xtensa / newlib do ESP32) avalia igualdade de strings sem distinção entre maiúsculas e minúsculas.
   - Implementando:
     ```cpp
     if (strcasecmp(str, "true") == 0 || strcmp(str, "1") == 0) { *outVal = true; return true; }
     if (strcasecmp(str, "false") == 0 || strcmp(str, "0") == 0) { *outVal = false; return true; }
     ```
   - O parser atende de forma unificada e inequívoca a todas as representações padrão de JSON e REST, rejeitando tokens desconhecidos (`"2"`, `"on"`, `"inválido"`).

### 2.4 Dedução da Execução Transacional em 2 Fases (Staging $\rightarrow$ Commit)
1. **Problema de Mutação Parcial:**
   - Se o parser proprietário aplicar alterações diretamente nos pinos GPIO ou nas variáveis globais conforme encontra cada chave, um erro de sintaxe no final do frame JSON deixará o nó em estado inconsistente e potencialmente perigoso (ex.: válvula aberta sem setpoint definido).
2. **Abordagem Transacional:**
   - **Fase 1 (Parsing & Validação em Staging):** Todas as chaves do payload são extraídas para uma estrutura local na pilha (`StagedCommands`). Nenhuma variável global nem pino de hardware é alterado. Qualquer falha de sintaxe ou violação de faixa marca `frameHasErrors = true`.
   - **Fase 2 (Commit Atômico com Mutex):** Se e somente se `!frameHasErrors && recognizedAny`, o semáforo `commandMutex` é adquirido e todas as variáveis, pinos de GPIO e registros de persistência são atualizados atomicamente.

---

## 3. Caveats (Ressalvas e Limitações)

1. **Tratamento de Chaves Desconhecidas:**
   - Para manter compatibilidade com versões futuras do protocolo e campos auxiliares do Hub (ex.: `direct_cmd_id`, `timestamp`), chaves não reconhecidas pelo firmware do nó são ignoradas com segurança, desde que a sintaxe JSON do par seja válida. Apenas chaves reconhecidas com valores fora de faixa ou sintaxes inválidas acionam a rejeição do quadro.
2. **Interação com F04 (`dacHold`) e F06 (Segurança Pneumática):**
   - O parser transacional implementado respeita estritamente o contrato de F04: ao receber setpoint zero, a válvula de corte geral é acionada (`valveFlowState = 1`), mas a tensão analógica do DAC MCP4725 só é zerada se `!dacHold`.
   - As condições de segurança de corte geral sem rota (F06) integram-se harmonicamente na Fase 2 de Commit.

---

## 4. Conclusion e Recomendações de Código (Conclusão e Diffs Técnicos)

### 4.1 Tabela Normativa de Faixas e Tipos (F07 e F08)

| Parâmetro / Chave | Tipo | Faixa Permitida | Padrão Fábrica | Ação em Caso de Violação |
|---|---|---|---|---|
| `a1` | float (quártico) | `[-1.0e7f, 1.0e7f]` | `-1353785.3f` | Rejeita frame (`frameHasErrors = true`) |
| `b1` | float (cúbico) | `[-1.0e7f, 1.0e7f]` | `+246663.69f` | Rejeita frame (`frameHasErrors = true`) |
| `k1` | float (quadrático) | `[-1.0e7f, 1.0e7f]` | `-16473.492f` | Rejeita frame (`frameHasErrors = true`) |
| `f1` | float (linear) | `[-1.0e7f, 1.0e7f]` | `+484.99466f` | Rejeita frame (`frameHasErrors = true`) |
| `c1` | float (offset) | `[-1.0e7f, 1.0e7f]` | `-4.6159464f` | Rejeita frame (`frameHasErrors = true`) |
| `k2` | float (quadrático alto) | `[-1.0e7f, 1.0e7f]` | `-0.46260458f` | Rejeita frame (`frameHasErrors = true`) |
| `f2` | float (linear alto) | `[-1.0e7f, 1.0e7f]` | `+10.797299f` | Rejeita frame (`frameHasErrors = true`) |
| `c2` | float (offset alto) | `[-1.0e7f, 1.0e7f]` | `+0.28475793f` | Rejeita frame (`frameHasErrors = true`) |
| `kp_flow` | float (PI ganho P) | `[0.0f, 100.0f]` | `0.4f` | Rejeita frame (`frameHasErrors = true`) |
| `ki_flow` | float (PI ganho I) | `[0.0f, 100.0f]` | `2.0f` | Rejeita frame (`frameHasErrors = true`) |
| `ff_gain` | float (Feedforward) | `[0.0f, 10.0f]` | `0.85f` | Rejeita frame (`frameHasErrors = true`) |
| `ff_offset` | float (Bias feedforward)| `[-5.0f, 5.0f]` | `-0.05f` | Rejeita frame (`frameHasErrors = true`) |
| `ramp_rate` | float (L/min/s) | `[0.0f, 100.0f]` | `3.0f` | Rejeita frame (`frameHasErrors = true`) |
| `max_flow` | float (Fim de escala) | `[0.1f, 500.0f]` | `50.0f` | Rejeita frame (`frameHasErrors = true`) |
| `flow_setpoint` | float (Alvo de vazão) | `[0.0f, maxFlowRate]` | `0.0f` | Rejeita frame (`frameHasErrors = true`) |
| `v1`, `v2`, `v_Flow`, `dac_hold`, `reconnect_wifi`, `debug_pi` | booleano | `"true"`, `"false"`, `"True"`, `"False"`, `"1"`, `"0"` | N/A | Rejeita frame (`frameHasErrors = true`) |

---

### 4.2 Código C++ Recomendado para `CommandCodec.h`

#### 4.2.1 Helpers de Validação e Parser Booleano
Inserir no topo de `External-Devices/fluxometro/firmware/flowmeter/src/protocol/CommandCodec.h`:

```cpp
#include <strings.h>
#include <cmath>
#include <cstdlib>

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

// F07: Parser e validador numérico com rejeição estrita de NaN, Inf e verificação de faixa
static inline bool parseBoundedFloat(const char* str, float minVal, float maxVal, float* outVal) {
  if (!str || !outVal) return false;
  while (*str && isspace(static_cast<unsigned char>(*str))) str++;
  if (*str == '\0') return false;
  char* endPtr = nullptr;
  float val = strtof(str, &endPtr);
  if (endPtr == str) return false;
  while (*endPtr && isspace(static_cast<unsigned char>(*endPtr))) endPtr++;
  if (*endPtr != '\0') return false; // Rejeita caracteres inválidos residuais
  if (isnan(val) || isinf(val)) return false;
  if (val < minVal || val > maxVal) return false;
  *outVal = val;
  return true;
}
```

#### 4.2.2 Implementação Transacional em Duas Fases para `processReceivedData`
Substituir o laço de execução em `CommandCodec.h:93-239`:

```cpp
bool processReceivedData(const String& rawData, CommandSource source) {
  if (rawData.length() < 2) return false;
  const char* str = rawData.c_str();
  while (*str && isspace(static_cast<unsigned char>(*str))) str++;
  const char* firstBrace = strchr(str, '{');
  const char* lastBrace = strrchr(str, '}');
  if (!firstBrace || !lastBrace || lastBrace <= firstBrace) return false;

  uint32_t hubCommandId = 0;
  uint32_t directSessionId = 0;
  uint32_t directCommandId = 0;
  bool hasHubCommandId = (source == COMMAND_HUB) &&
                         extractJsonUint32(str, "cmd_id", hubCommandId);
  bool hasDirectCommandId = (source == COMMAND_DIRECT) &&
                            extractJsonUint32(str, "direct_cmd_id", directCommandId);
  bool hasDirectSessionId = (source == COMMAND_DIRECT) &&
                            extractJsonUint32(str, "direct_session_id", directSessionId);

  if (hasHubCommandId) {
    xSemaphoreTake(commandMutex, portMAX_DELAY);
    bool duplicate = (hubCommandId == lastAppliedHubCommandId);
    xSemaphoreGive(commandMutex);
    if (duplicate) return true;
  }

  if (hasDirectCommandId && hasDirectSessionId) {
    xSemaphoreTake(commandMutex, portMAX_DELAY);
    bool sameSession = (directSessionId == lastAppliedDirectSessionId);
    bool duplicateOrStale = sameSession &&
                            ((int32_t)(directCommandId - lastAppliedDirectCommandId) <= 0);
    xSemaphoreGive(commandMutex);
    if (duplicateOrStale) return true;
  }

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

    bool hasKp = false;        float stagedKp = 0.0f;
    bool hasKi = false;        float stagedKi = 0.0f;
    bool hasFfGain = false;    float stagedFfGain = 0.0f;
    bool hasFfOffset = false;  float stagedFfOffset = 0.0f;
    bool hasRampRate = false;  float stagedRampRate = 0.0f;

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

  const char* cursor = firstBrace + 1;
  while (cursor < lastBrace) {
    while (cursor < lastBrace && (isspace(static_cast<unsigned char>(*cursor)) || *cursor == ',')) cursor++;
    if (cursor >= lastBrace) break;

    char keyBuf[32];
    size_t kLen = 0;
    if (*cursor == '"') {
      cursor++;
      while (cursor < lastBrace && *cursor != '"' && kLen < sizeof(keyBuf) - 1) {
        keyBuf[kLen++] = *cursor++;
      }
      if (cursor < lastBrace && *cursor == '"') cursor++;
    } else {
      while (cursor < lastBrace && *cursor != ':' && !isspace(static_cast<unsigned char>(*cursor)) && kLen < sizeof(keyBuf) - 1) {
        keyBuf[kLen++] = *cursor++;
      }
    }
    keyBuf[kLen] = '\0';

    while (cursor < lastBrace && *cursor != ':') cursor++;
    if (cursor >= lastBrace) { staged.frameHasErrors = true; break; }
    cursor++; // pular ':'
    while (cursor < lastBrace && isspace(static_cast<unsigned char>(*cursor))) cursor++;

    char valBuf[32];
    size_t vLen = 0;
    if (*cursor == '"') {
      cursor++;
      while (cursor < lastBrace && *cursor != '"' && vLen < sizeof(valBuf) - 1) {
        valBuf[vLen++] = *cursor++;
      }
      if (cursor < lastBrace && *cursor == '"') cursor++;
    } else {
      while (cursor < lastBrace && *cursor != ',' && *cursor != '}' && !isspace(static_cast<unsigned char>(*cursor)) && vLen < sizeof(valBuf) - 1) {
        valBuf[vLen++] = *cursor++;
      }
    }
    valBuf[vLen] = '\0';

    bool bVal = false;
    float fVal = 0.0f;

    // F08: Tratamento robusto de booleanos
    if (strcmp(keyBuf, "v_Flow") == 0 || strcmp(keyBuf, "valveFlow") == 0) {
      if (parseJsonBool(valBuf, &bVal)) {
        staged.hasVFlow = true; staged.stagedVFlow = bVal; staged.recognizedAny = true;
      } else {
        Serial.printf("[REJECT] v_Flow booleano invalido: %s\n", valBuf);
        staged.frameHasErrors = true;
      }
    }
    else if (strcmp(keyBuf, "v1") == 0 || strcmp(keyBuf, "valve_1") == 0) {
      if (parseJsonBool(valBuf, &bVal)) {
        staged.hasV1 = true; staged.stagedV1 = bVal; staged.recognizedAny = true;
      } else {
        Serial.printf("[REJECT] v1 booleano invalido: %s\n", valBuf);
        staged.frameHasErrors = true;
      }
    }
    else if (strcmp(keyBuf, "v2") == 0 || strcmp(keyBuf, "valve_2") == 0) {
      if (parseJsonBool(valBuf, &bVal)) {
        staged.hasV2 = true; staged.stagedV2 = bVal; staged.recognizedAny = true;
      } else {
        Serial.printf("[REJECT] v2 booleano invalido: %s\n", valBuf);
        staged.frameHasErrors = true;
      }
    }
    else if (strcmp(keyBuf, "reconnect_wifi") == 0) {
      if (parseJsonBool(valBuf, &bVal)) {
        staged.hasReconnectWifi = true; staged.stagedReconnectWifi = bVal; staged.recognizedAny = true;
      } else {
        Serial.printf("[REJECT] reconnect_wifi booleano invalido: %s\n", valBuf);
        staged.frameHasErrors = true;
      }
    }
    else if (strcmp(keyBuf, "debug_pi") == 0) {
      if (parseJsonBool(valBuf, &bVal)) {
        staged.hasDebugPi = true; staged.stagedDebugPi = bVal; staged.recognizedAny = true;
      } else {
        Serial.printf("[REJECT] debug_pi booleano invalido: %s\n", valBuf);
        staged.frameHasErrors = true;
      }
    }
    else if (strcmp(keyBuf, "dac_hold") == 0) {
      if (parseJsonBool(valBuf, &bVal)) {
        staged.hasDacHold = true; staged.stagedDacHold = bVal; staged.recognizedAny = true;
      } else {
        Serial.printf("[REJECT] dac_hold booleano invalido: %s\n", valBuf);
        staged.frameHasErrors = true;
      }
    }
    // F07: Tratamento robusto de ponto flutuante
    else if (strcmp(keyBuf, "max_flow") == 0 || strcmp(keyBuf, "maxFlow") == 0) {
      if (parseBoundedFloat(valBuf, 0.1f, 500.0f, &fVal)) {
        staged.hasMaxFlow = true; staged.stagedMaxFlow = fVal; staged.recognizedAny = true;
      } else {
        Serial.printf("[REJECT] max_flow fora de faixa (0.1..500): %s\n", valBuf);
        staged.frameHasErrors = true;
      }
    }
    else if (strcmp(keyBuf, "flow_setpoint") == 0 || strcmp(keyBuf, "flowSetpoint") == 0) {
      if (parseBoundedFloat(valBuf, 0.0f, 500.0f, &fVal)) {
        staged.hasFlowSetpoint = true; staged.stagedFlowSetpoint = fVal; staged.recognizedAny = true;
      } else {
        Serial.printf("[REJECT] flow_setpoint invalido: %s\n", valBuf);
        staged.frameHasErrors = true;
      }
    }
    else if (strcmp(keyBuf, "kp_flow") == 0) {
      if (parseBoundedFloat(valBuf, 0.0f, 100.0f, &fVal)) {
        staged.hasKp = true; staged.stagedKp = fVal; staged.recognizedAny = true;
      } else {
        Serial.printf("[REJECT] kp_flow fora de faixa (0..100): %s\n", valBuf);
        staged.frameHasErrors = true;
      }
    }
    else if (strcmp(keyBuf, "ki_flow") == 0) {
      if (parseBoundedFloat(valBuf, 0.0f, 100.0f, &fVal)) {
        staged.hasKi = true; staged.stagedKi = fVal; staged.recognizedAny = true;
      } else {
        Serial.printf("[REJECT] ki_flow fora de faixa (0..100): %s\n", valBuf);
        staged.frameHasErrors = true;
      }
    }
    else if (strcmp(keyBuf, "ff_gain") == 0) {
      if (parseBoundedFloat(valBuf, 0.0f, 10.0f, &fVal)) {
        staged.hasFfGain = true; staged.stagedFfGain = fVal; staged.recognizedAny = true;
      } else {
        Serial.printf("[REJECT] ff_gain fora de faixa (0..10): %s\n", valBuf);
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
    else if (strcmp(keyBuf, "ramp_rate") == 0) {
      if (parseBoundedFloat(valBuf, 0.0f, 100.0f, &fVal)) {
        staged.hasRampRate = true; staged.stagedRampRate = fVal; staged.recognizedAny = true;
      } else {
        Serial.printf("[REJECT] ramp_rate fora de faixa (0..100): %s\n", valBuf);
        staged.frameHasErrors = true;
      }
    }
    // F07: Polinômios quárticos e quadráticos com faixa ampla (+/- 1e7)
    else if (strcmp(keyBuf, "a1") == 0) {
      if (parseBoundedFloat(valBuf, -1e7f, 1e7f, &fVal)) {
        staged.hasA1 = true; staged.stagedA1 = fVal; staged.recognizedAny = true;
      } else {
        Serial.printf("[REJECT] a1 fora de faixa (+/-1e7): %s\n", valBuf);
        staged.frameHasErrors = true;
      }
    }
    else if (strcmp(keyBuf, "b1") == 0) {
      if (parseBoundedFloat(valBuf, -1e7f, 1e7f, &fVal)) {
        staged.hasB1 = true; staged.stagedB1 = fVal; staged.recognizedAny = true;
      } else {
        Serial.printf("[REJECT] b1 fora de faixa (+/-1e7): %s\n", valBuf);
        staged.frameHasErrors = true;
      }
    }
    else if (strcmp(keyBuf, "k1") == 0) {
      if (parseBoundedFloat(valBuf, -1e7f, 1e7f, &fVal)) {
        staged.hasK1 = true; staged.stagedK1 = fVal; staged.recognizedAny = true;
      } else {
        Serial.printf("[REJECT] k1 fora de faixa (+/-1e7): %s\n", valBuf);
        staged.frameHasErrors = true;
      }
    }
    else if (strcmp(keyBuf, "f1") == 0) {
      if (parseBoundedFloat(valBuf, -1e7f, 1e7f, &fVal)) {
        staged.hasF1 = true; staged.stagedF1 = fVal; staged.recognizedAny = true;
      } else {
        Serial.printf("[REJECT] f1 fora de faixa (+/-1e7): %s\n", valBuf);
        staged.frameHasErrors = true;
      }
    }
    else if (strcmp(keyBuf, "c1") == 0) {
      if (parseBoundedFloat(valBuf, -1e7f, 1e7f, &fVal)) {
        staged.hasC1 = true; staged.stagedC1 = fVal; staged.recognizedAny = true;
      } else {
        Serial.printf("[REJECT] c1 fora de faixa (+/-1e7): %s\n", valBuf);
        staged.frameHasErrors = true;
      }
    }
    else if (strcmp(keyBuf, "k2") == 0) {
      if (parseBoundedFloat(valBuf, -1e7f, 1e7f, &fVal)) {
        staged.hasK2 = true; staged.stagedK2 = fVal; staged.recognizedAny = true;
      } else {
        Serial.printf("[REJECT] k2 fora de faixa (+/-1e7): %s\n", valBuf);
        staged.frameHasErrors = true;
      }
    }
    else if (strcmp(keyBuf, "f2") == 0) {
      if (parseBoundedFloat(valBuf, -1e7f, 1e7f, &fVal)) {
        staged.hasF2 = true; staged.stagedF2 = fVal; staged.recognizedAny = true;
      } else {
        Serial.printf("[REJECT] f2 fora de faixa (+/-1e7): %s\n", valBuf);
        staged.frameHasErrors = true;
      }
    }
    else if (strcmp(keyBuf, "c2") == 0) {
      if (parseBoundedFloat(valBuf, -1e7f, 1e7f, &fVal)) {
        staged.hasC2 = true; staged.stagedC2 = fVal; staged.recognizedAny = true;
      } else {
        Serial.printf("[REJECT] c2 fora de faixa (+/-1e7): %s\n", valBuf);
        staged.frameHasErrors = true;
      }
    }

    while (cursor < lastBrace && *cursor != ',') cursor++;
    if (cursor < lastBrace && *cursor == ',') cursor++;
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
  bool calParamsUpdated = false;

  if (staged.hasMaxFlow) {
    maxFlowRate = staged.stagedMaxFlow;
    targetFlowSetpoint = constrain(targetFlowSetpoint, 0.0f, maxFlowRate);
  }

  if (staged.hasVFlow) {
    valveFlowState = staged.stagedVFlow;
    digitalWrite(VALVE_FLOW_PIN, valveFlowState);
  }
  if (staged.hasV1) {
    valve1State = staged.stagedV1;
    digitalWrite(VALVE1_PIN, valve1State);
  }
  if (staged.hasV2) {
    valve2State = staged.stagedV2;
    digitalWrite(VALVE2_PIN, valve2State);
  }
  if (staged.hasReconnectWifi) {
    reconnect_Wifi = staged.stagedReconnectWifi;
    Serial.printf("Wifi Reconnect Logic set to: %s\n", reconnect_Wifi ? "TRUE" : "FALSE");
  }
  if (staged.hasDebugPi) {
    debugPI = staged.stagedDebugPi;
    Serial.printf("PI debug trace %s\n", debugPI ? "ON" : "OFF");
  }
  if (staged.hasDacHold) {
    dacHold = staged.stagedDacHold;
    calParams.dac_hold = dacHold ? 1.0f : 0.0f;
    calParamsUpdated = true;
    Serial.printf("DAC hold across zero setpoint: %s\n", dacHold ? "ON" : "OFF");
  }

  if (staged.hasFlowSetpoint) {
    float newTarget = constrain(staged.stagedFlowSetpoint, 0.0f, maxFlowRate);
    if (fabs(newTarget - targetFlowSetpoint) > 0.001f || newTarget == 0.0f || targetFlowSetpoint == 0.0f) {
      targetFlowSetpoint = newTarget;
      if (targetFlowSetpoint == 0.0f) {
        valveFlowState = 1;
        digitalWrite(VALVE_FLOW_PIN, HIGH);
        if (!dacHold) {
          flowSetpoint = 0.0f;
          rampedTarget = 0.0f;
          writeFlowSetpointToDAC(0.0f);
        }
      }
      Serial.printf("New Target Accepted: %.3f\n", targetFlowSetpoint);
    } else {
      Serial.printf("Target Update Ignored (Delta < 0.05): %.3f\n", newTarget);
    }
  }

  if (staged.hasKp) { Kp_flow = calParams.kp = staged.stagedKp; calParamsUpdated = true; }
  if (staged.hasKi) { Ki_flow = calParams.ki = staged.stagedKi; calParamsUpdated = true; }
  if (staged.hasFfGain) { ffGain = calParams.ff_gain = staged.stagedFfGain; calParamsUpdated = true; }
  if (staged.hasFfOffset) { ffOffset = calParams.ff_offset = staged.stagedFfOffset; calParamsUpdated = true; }
  if (staged.hasRampRate) { rampRate = calParams.ramp_rate = staged.stagedRampRate; calParamsUpdated = true; }

  bool lowQuadraticUpdated = (staged.hasK1 || staged.hasF1 || staged.hasC1);
  bool lowHigherOrderUpdated = (staged.hasA1 || staged.hasB1);
  if (staged.hasA1) { a1 = calParams.a1 = staged.stagedA1; calParamsUpdated = true; }
  if (staged.hasB1) { b1 = calParams.b1 = staged.stagedB1; calParamsUpdated = true; }
  if (staged.hasK1) { k1 = calParams.k1 = staged.stagedK1; calParamsUpdated = true; }
  if (staged.hasF1) { f1 = calParams.f1 = staged.stagedF1; calParamsUpdated = true; }
  if (staged.hasC1) { c1 = calParams.c1 = staged.stagedC1; calParamsUpdated = true; }
  if (staged.hasK2) { k2 = calParams.k2 = staged.stagedK2; calParamsUpdated = true; }
  if (staged.hasF2) { f2 = calParams.f2 = staged.stagedF2; calParamsUpdated = true; }
  if (staged.hasC2) { c2 = calParams.c2 = staged.stagedC2; calParamsUpdated = true; }

  if (lowQuadraticUpdated && !lowHigherOrderUpdated) {
    a1 = calParams.a1 = 0.0f;
    b1 = calParams.b1 = 0.0f;
  }

  lastCommandApplyMs = millis();
  lastCommandSource = (source == COMMAND_HUB) ? "hub" : "direct";
  if (hasHubCommandId) {
    lastAppliedHubCommandId = hubCommandId;
    Serial.printf("[HubCmd] Applied cmd_id=%lu\n", (unsigned long)hubCommandId);
  }
  if (hasDirectCommandId && hasDirectSessionId) {
    lastAppliedDirectSessionId = directSessionId;
    lastAppliedDirectCommandId = directCommandId;
    Serial.printf("[DirectCmd] Applied session=%lu direct_cmd_id=%lu\n",
                  (unsigned long)directSessionId, (unsigned long)directCommandId);
  }

  if (calParamsUpdated) saveParameters();
  startLEDBlinking();
  xSemaphoreGive(commandMutex);
  return true;
}
```

---

## 5. Verification Method (Método de Verificação Independente)

Para auditar e verificar independentemente a exatidão matemática, a robustez do parser e a conformidade dos limites propostos:

1. **Execução da Suíte de Testes Automatizada (68 Vetores):**
   ```powershell
   python d:\OneDrive\PosDoc_Fapesp\Automacao_e_Controle\ProjetoTECNAL\.agents\explorer_remedy_3_it2\test_f07_f08_remedy.py
   ```
   *Critério de aprovação:* Retorno código 0 com saída `SUCCESS: All 68 verification cases for F07 and F08 PASSED flawlessly!`.
   - 22 testes de booleanos (true/false/True/False/TRUE/FALSE/1/0/whitespace/rejeição de literais inválidos).
   - 21 testes de polinômios de calibração ($\pm 10^7$, curvas de fábrica, Horner mV, rejeição de NaN/Inf).
   - 25 testes de parâmetros de controle e rampa ($K_p$, $K_i$, $ff\_gain$, $ff\_offset$, $ramp\_rate$, $max\_flow$).

2. **Inspeção do Patch Git:**
   Inspecionar o arquivo `.agents/explorer_remedy_3_it2/f07_f08_remedy.patch` para confirmar a aplicação limpa e não-invasiva sobre `External-Devices/fluxometro/firmware/flowmeter/src/protocol/CommandCodec.h`.

3. **Verificação de Regressão nos Testes do Windows App:**
   ```powershell
   dotnet test Windows_app/tests/OpenTECHub.Tests/OpenTECHub.Tests.csproj --filter "FullyQualifiedName~Calibration|FullyQualifiedName~WireFormat"
   ```
   *Critério de aprovação:* 102 de 102 testes de calibração e protocolo aprovados com sucesso.

4. **Verificação de Integridade Git:**
   ```powershell
   git status
   ```
   *Critério de aprovação:* Nenhum arquivo de código de produção (`.cpp`, `.h`, `.cs`) modificado. Toda a documentação e artefatos concentram-se estritamente sob `.agents/`.

