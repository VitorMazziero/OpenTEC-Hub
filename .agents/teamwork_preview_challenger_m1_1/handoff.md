# Relatório de Handoff — Challenger 1 (Milestone 1)

**Data:** 2026-09-13T16:40:00Z  
**Agente:** Challenger 1 (M1)  
**Destinatário:** Orchestrator / Parent Agent (`82f26027-eaef-4f56-bf65-2cbcdf3dab0a`)  
**Status do Veredito:** APROVAÇÃO CONDICIONAL COM RESSALVAS CRÍTICAS (PLANO ROBUSTO COM 7 VULNERABILIDADES DE BORDA IDENTIFICADAS)

---

## 1. Observation

### 1.1 Verificação Adversarial de `verify_plan_biomassa.py`
Foi criada e executada uma suíte automatizada de testes de estresse em `External-Devices/sensor-biomassa/tests/test_verify_plan_biomassa_adversarial.py`.
- **Comando executado:**
  `python External-Devices/sensor-biomassa/tests/test_verify_plan_biomassa_adversarial.py`
- **Resultado:**
  `Ran 15 tests in 0.282s - OK`
- **Comportamento observado em omissão e corrupção:**
  1. *Omissão de itens individuais (B04, B13, B01):* `verify_plan_biomassa.py` detecta a ausência, exibe `Bxx | FAIL | NÃO | NÃO | NÃO` e retorna `Exit Code 1` (`False`).
  2. *Corrupção de sintaxe de cabeçalho:* Transformar `### B04 —` em `#### B04` ou `### B13_CORRUPTED` falha na captura da regex `rf"^###\s+{item_id}\b.*"`, marcando `FAIL` e retornando `Exit Code 1`.
  3. *Omissão de dimensões estruturais (Causa Raiz, Plano de Ação, Segurança):* A remoção de palavras-chave de ação (`pwmSetDutyPercent`, `Plano de Ação`), causa raiz ou segurança em B04 ou B13 aciona reprovação pontual (`Falta plano de ação/justificativa`, `Falta causa raiz/localização`, `Falta classificação de segurança/prioridade`).
  4. *Omissão de seções macro:* Todas as 6 seções canônicas foram testadas individualmente via injeção sintética. A ausência de qualquer uma resulta em `[FAIL] Seção '...' AUSENTE!` e encerramento com falha.
  5. *Vulnerabilidade do Verificador:* Na checagem da Seção 6 (`verify_plan_biomassa.py:263-287`), se o checklist de homologação em bancada (§4.11) for removido, o script emite `[WARN] Checklist de homologação não localizado explicitamente na Seção 6.`, porém **retorna `True` (`Exit Code 0`)** porque a variável `bench_match` não foi inserida na condicional final `if all_items_passed and macro_errors == 0:`.

### 1.2 Análise de Código e Casos de Borda Ocultos (B01, B03, B04)

#### Observação 1 (B01 — Supressão de Ecos no Hub por Janela Incompleta):
No arquivo `ESP32S3-HUB/ESP32S3-HUB/src/sensor/Telemetry.h:94–96`:
```cpp
if (biomassEchoSeen && (millis() - biomassLastUpdate > BIOMASS_TIMEOUT)) {
  biomassEchoSeen = false;
}
snapBiomassEchoSeen = biomassEchoSeen;
```
E em `Telemetry.h:311–315`:
```cpp
if (biomassOnline && snapBiomassEchoSeen) {
  if (snapBiomassGear >= 0) jsonResponse += ",\"BiomassGear\":" + String(snapBiomassGear);
  if (!isnan(snapBiomassEma)) jsonResponse += ",\"BiomassEma\":" + String(snapBiomassEma, 3);
  if (snapBiomassProbePeriodMs > 0) jsonResponse += ",\"BiomassProbePeriodMs\":" + String(snapBiomassProbePeriodMs);
}
```
O plano `IMPLEMENTATION_PLAN_BIOMASSA.md` (linhas 167–181) propõe atualizar apenas as linhas 171–177 para usar `bioWin`, omitindo a linha 94.
Como contraste, o sensor de distância em `Telemetry.h:84` adota:
```cpp
if (distanceEchoSeen && (millis() - distanceSensorLastUpdate > distancePresenceWindowMs(distanceSendPeriodMs))) {
  distanceEchoSeen = false;
}
```

#### Observação 2 (B01 — Discrepância de Heartbeat em MEASURING):
No plano `IMPLEMENTATION_PLAN_BIOMASSA.md` (linha 157), consta:
*"Permitir o envio de heartbeats leves (idle=1) em Lifecycle.h mesmo em MEASURING"*.
Porém, o trecho de código proposto para `Lifecycle.h` (linha 185) chama `sendDataToHub()`.
Em `External-Devices/sensor-biomassa/firmware/biomass-sensor/src/protocol/TelemetryAndHub.h:84`:
```cpp
(g_state == IDLE) ? 1 : 0,
```
Quando o nó está em `MEASURING`, essa expressão avalia para `0` (`idle=0`).
No Hub (`HttpServer.h:352`):
```cpp
if (!idleBeat) biomassSampleLastUpdate = millis();
```
Como `idleBeat` chega como `0`, o Hub interpreta cada heartbeat de repouso como uma amostra óptica fresca.

#### Observação 3 (B03 — `setManualGear` Não Desativa Auto-Range):
Na Tabela 4 do plano (linha 740), consta:
*"Ao travar marcha manual, o auto-range é desativado no nó (`g_autoRange = false`), evitando substituição involuntária de marcha."*
Porém, no código atual de `CommandCodec.h:73–87` e no snippet de substituição de B04 (linhas 376–400), `setManualGear()` não atribui `g_autoRange = false` nem chama `setAutoRange(false)`.
Em `CommandCodec.h:221` e no snippet proposto em B03 (linha 328):
```cpp
if (g_autoRange || !blankIsValid(startIt, startPwm)) {
  findOptimalBlankGear(startIt, startPwm);
}
```
Se o operador selecionou a marcha pelo aplicativo (`biomassGear`) ou serial sem enviar `auto: 0`, `g_autoRange` permanece `true`, fazendo com que `start` execute `findOptimalBlankGear` e destrua a marcha manual selecionada.

#### Observação 4 (B03 — Acionamento Prematuro do LED no Comando `start`):
Em `CommandCodec.h:220–226`:
```cpp
int startIt, startPwm;
findOptimalBlankGear(startIt, startPwm);
g_state = MEASURING;
g_nextReadTime = millis();
vemlSetConfig(startIt);
pwmSetLevel(startPwm); // <--- Liga o LED imediatamente a plena carga
```
A rotina `pwmSetLevel` (`Veml7700Driver.h:249`) chama `pwmSetDutyPercent(g_config.pwmSettings[pwmIndex])`.
O LED permanece aceso durante todo o intervalo até que o Core 0/Core 1 despache `runMeasurementLoop()`.
No início de `takePulsedReading()` (`Veml7700Driver.h:108`), é exigido repouso escuro:
```cpp
const uint32_t darkFor = millis() - g_ledOffSinceMs;
if (darkFor < guardMs) delayServiced(guardMs - darkFor);
if (pwmIndex >= 0) pwmSetLevel(pwmIndex);
```
O acionamento prévio em `start` viola o baseline escuro e dissipa calor desnecessariamente.

#### Observação 5 (B04 — Omissão de `enforceRefreshFloor` em `setManualGear`):
Em `BlankingAndRange.h:103–113`:
```cpp
uint32_t minSafeRefreshMs() {
  uint32_t itMs = 0;
  if (g_autoRange) { ... }
  else { itMs = g_config.itDelays[g_currentItIndex]; }
  return (uint32_t)(ledOnMsFor(itMs) / LED_DUTY_LIMIT);
}
```
Em `IT = 100 ms`, o piso seguro é $\sim 3\text{ s}$; em `IT = 800 ms`, é $24.325\text{ ms}$.
`setAutoRange(true)` invoca `enforceRefreshFloor(true)` (`CommandCodec.h:70`), mas `setManualGear()` não o faz.
Se o operador definiu o intervalo de amostragem em 5.000 ms operando em marcha com $IT = 100\text{ ms}$ (seguro), e depois seleciona uma marcha manual com $IT = 800\text{ ms}$, o intervalo permanece em 5.000 ms.
O ciclo de trabalho térmico resultante é:
$$D = \frac{1.946\text{ ms}}{5.000\text{ ms}} = 38{,}92\%$$
Isso ultrapassa em quase 5 vezes o limite de segurança de $8{,}0\%$.

#### Observação 6 (B04 / B05 — Snippet Proposto Bypassa Clamping Térmico):
No código existente de `CommandCodec.h:438–446`:
```cpp
long floorMs = (long)minSafeRefreshMs();
if (val < floorMs) val = floorMs;
for (int i = 0; i < g_config.IT_COUNT; i++) g_config.itRefreshTimes[i] = (uint32_t)val;
```
No snippet proposto para B05 em `IMPLEMENTATION_PLAN_BIOMASSA.md` (linhas 453–457):
```cpp
long refreshVal = getJsonValue(json, "refresh_ms");
if (refreshVal != -999999 && refreshVal > 0 && refreshVal != g_config.itRefreshTimes[g_currentItIndex]) {
  for (int i = 0; i < g_config.IT_COUNT; i++) g_config.itRefreshTimes[i] = (uint32_t)refreshVal;
  configModified = true;
}
```
A verificação `if (refreshVal < floorMs) refreshVal = floorMs;` foi omitida, permitindo gravar intervalos perigosos na NVS.

#### Observação 7 (B04 — Ausência de Intertravamento em `case MEASURING` no Laço Principal):
Em `Lifecycle.h:204–220`:
O estado `IDLE` possui salvaguarda térmica no laço:
`if (!g_ledTestEnable && !g_manualLedOn && g_targetPct > 0.0f) pwmSetDutyPercent(0.0f);`
Porém, o estado `MEASURING` não possui nenhuma salvaguarda correspondente entre leituras (`now < g_nextReadTime`). Se uma leitura falhar ou qualquer rotina esquecer o LED aceso, ele permanecerá ligado até o próximo ciclo.

---

## 2. Logic Chain

1. **Rigor do Verificador:**
   Os testes adversariais provaram que `verify_plan_biomassa.py` é sensível e eficaz para detectar omissões de itens, corrupções de cabeçalho e ausência de seções macro. Contudo, constatou-se que o checklist de bancada (§4.11) gera apenas `[WARN]` sem bloquear a aprovação, constituindo um ponto cego no script.

2. **Cadeia de Falha do Efeito B01 no Hub:**
   A proposta de B01 corrige `biomassOnline` nas linhas 171–177 de `Telemetry.h`, mas mantém `BIOMASS_TIMEOUT` fixo em 10 s na linha 94. Aos 10 segundos de cada ciclo de 25 s, `biomassEchoSeen` é forçado a `false`. Em decorrência disso, as chaves `BiomassGear`, `BiomassEma` e `BiomassProbePeriodMs` são suprimidas do payload `/readData` (linhas 311–315), gerando dados oscilantes no supervisor.

3. **Cadeia de Falha do Heartbeat de Repouso em B01:**
   A telemetria do nó envia `idle = (g_state == IDLE) ? 1 : 0`. Em `MEASURING`, `idle` é 0. O Hub (`HttpServer.h:352`) atualiza `biomassSampleLastUpdate = millis()`. Se o hardware óptico congelar, o Hub continua interpretando os heartbeats como amostras frescas a cada 5 s, mascarando a perda de medição óptica.

4. **Cadeia de Falha do Auto-Range e Smart Start em B03:**
   O usuário seleciona marcha manual na interface (`biomassGear`), mas o firmware não reseta `g_autoRange` em `setManualGear()`. Ao enviar `start`, `g_autoRange` é avaliado como `true`, disparando `findOptimalBlankGear` e substituindo a marcha pretendida pela marcha do Smart Start.

5. **Cadeia de Sobrecarga Térmica em B04 e B05:**
   Ao trocar de marcha manual para um tempo de integração maior ($IT = 800\text{ ms}$), a ausência de chamada a `enforceRefreshFloor(true)` permite que períodos curtos (ex: 5.000 ms) permaneçam ativos, elevando o ciclo de trabalho para quase 39%. Adicionalmente, o snippet sugerido em B05 remove a clampagem contra `minSafeRefreshMs()`, abrindo brecha para que comandos remotos violem o limite de dissipação térmica do emissor LED.

---

## 3. Caveats

- Os testes de estresse foram executados de forma determinística em ambiente de script e análise estática profunda do código-fonte. Não foram realizados ensaios com instrumentação de osciloscópio físico na bancada (previstos para o Milestone M6 conforme protocolo §4.11).
- O comportamento do compilador gcc-esp32 sob otimização `-O2` não altera as conclusões lógicas das rotinas analisadas.
- Nenhuma outra premissa foi assumida sem confirmação textual direta no repositório.

---

## 4. Conclusion

### Veredito: APROVADO COM RESSALVAS MANDATÓRIAS DE IMPLEMENTAÇÃO

O documento `IMPLEMENTATION_PLAN_BIOMASSA.md` e o script `verify_plan_biomassa.py` cumprem integralmente os requisitos formais de completude (B01 a B15, 6 seções canônicas, arquitetura e justificativas metrológicas).

Todavia, para a fase de execução de código (Milestones M2 a M4), **as seguintes 7 correções mandatórias devem ser incorporadas às implementações**:
1. **Hub (`Telemetry.h:94`):** Atualizar `millis() - biomassLastUpdate > biomassPresenceWindowMs(biomassProbePeriodMs)` para não apagar os ecos de marcha e período após 10 segundos.
2. **Firmware (`TelemetryAndHub.h:84` e `Lifecycle.h`):** Garantir que o heartbeat emitido durante o repouso em `MEASURING` transmita `idle=1`, evitando atualizar indevidamente `biomassSampleLastUpdate` no Hub com leituras antigas.
3. **Firmware (`CommandCodec.h:80`):** Inserir `setAutoRange(false);` dentro de `setManualGear()` para assegurar que travar marcha manual desative o auto-range e evite que o `start` sobrescreva a seleção do operador.
4. **Firmware (`CommandCodec.h:226`):** Remover `pwmSetLevel(startPwm);` do bloco de `start`, garantindo que o LED permaneça desligado até o início do pulso óptico em `takePulsedReading()`.
5. **Firmware (`CommandCodec.h:80`):** Inserir `enforceRefreshFloor(true);` dentro de `setManualGear()` para elevar automaticamente o intervalo de amostragem caso uma marcha de maior $IT$ seja selecionada manualmente.
6. **Firmware (`CommandCodec.h:440`):** No snippet de gravação coalescida de B05, manter rigorosamente a clampagem térmica `if (refreshVal < floorMs) refreshVal = floorMs;` antes de atualizar `itRefreshTimes` e gravar na NVS.
7. **Firmware (`Lifecycle.h:218`):** Adicionar no `case MEASURING:` a salvaguarda de defesa em profundidade: `if (now < g_nextReadTime && g_targetPct > 0.0f) pwmSetDutyPercent(0.0f);`.
8. **Script (`verify_plan_biomassa.py:277`):** Incluir `and bench_match` na condicional de saída final para que a ausência do checklist de bancada reprove formalmente a execução.

---

## 5. Verification Method

Para reproduzir e verificar de forma independente todas as constatações deste relatório:

1. **Executar a suíte de testes adversariais:**
   ```powershell
   python External-Devices/sensor-biomassa/tests/test_verify_plan_biomassa_adversarial.py
   ```
   *Condição esperada:* 15 testes executados com 100% de sucesso (`OK`).

2. **Inspecionar as linhas de código citadas:**
   - Hub: `ESP32S3-HUB/ESP32S3-HUB/src/sensor/Telemetry.h` linhas 84–97 e 311–315.
   - Nó: `External-Devices/sensor-biomassa/firmware/biomass-sensor/src/protocol/CommandCodec.h` linhas 73–87, 220–226, e 433–453.
   - Nó: `External-Devices/sensor-biomassa/firmware/biomass-sensor/src/protocol/TelemetryAndHub.h` linhas 75–87.
   - Nó: `External-Devices/sensor-biomassa/firmware/biomass-sensor/src/core/Lifecycle.h` linhas 179–220.
