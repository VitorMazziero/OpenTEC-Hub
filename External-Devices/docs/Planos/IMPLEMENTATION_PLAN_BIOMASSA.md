# PLANO DE IMPLEMENTAÇÃO E HOMOLOGAÇÃO: SENSOR DE BIOMASSA (FIRMWARE v11)
## Resolução de Inconsistências, Limitações (§4.10 B01 a B15) e Protocolo de Homologação em Bancada (§4.11)

**Data de Emissão:** 2026-09-13  
**Status:** Executado em 2026-09-13 — nó v11.1 (B03/B04/B05/B07), Hub 10.2 (B01/B03/B12), app (B06/B09/B13; catálogo `v11.1`). B02 e B14 mantidos abertos por decisão (flash a 5,1 kB do piso). Resultado consolidado em `../COMANDOS_DISPOSITIVOS_EXTERNOS.md` §4.0/§4.10; bancada em §4.11. Nó 1 129 932 B (86 %); Hub 1 125 420 B (85 %), 86/86 contratos; app 1615/1615 testes.  
**Referência Normativa:** `External-Devices/docs/COMANDOS_DISPOSITIVOS_EXTERNOS.md` (§4.0 a §4.11)  
**Subsistemas Cobertos:**
- Firmware do Nó Turbidimétrico/Fotômetro de Biomassa (`External-Devices/sensor-biomassa/firmware/biomass-sensor/`)
- Gateway OpenTEC-Hub v10.2 (`ESP32S3-HUB/ESP32S3-HUB/`)
- Aplicativo Supervisor Windows OpenTECHub .NET 8 / C# (`Windows_app/`)
- Cliente Python de Bancada / Laboratório (`External-Devices/sensor-biomassa/apps/desktop-python/`)
- Documentação Técnica de Engenharia (`COMANDOS_DISPOSITIVOS_EXTERNOS.md` e `HUB_PROTOCOL_IMPROVEMENTS.md`)

---

## 1. Sumário Executivo

### 1.1 Contexto e Objetivo
O **Sensor de Biomassa** (Dispositivo 4 da frota TECNAL) é um instrumento analítico microprocessado baseado no SoC ESP32-S3 e no sensor digital de luz ambiente Vishay VEML7700 (comunicação I²C a 100 kHz nos pinos SDA GPIO 8 e SCL GPIO 9), operando em conjunto com um LED emissor regulado via PWM a 2 kHz (LEDC, GPIO 18, resolução de 8 bits). Seu princípio de funcionamento fundamenta-se na turbidimetria/espectrofotometria óptica e na Lei de Beer-Lambert relativa ($A = -\log_{10}(I / I_0)$), permitindo inferir a densidade celular e o crescimento de biomassa em bioprocessos em tempo real.

A presente auditoria técnica exaustiva confrontou o código-fonte ativo do firmware do nó, o firmware do gateway mestre ESP32S3-HUB (v10.2), os aplicativos supervisores (Windows C# e Python de bancada) e o manual canônico de integração `COMANDOS_DISPOSITIVOS_EXTERNOS.md`. O objetivo deste plano é detalhar a resolução das 13 inconsistências e limitações catalogadas na **Seção 4.10 (B01 a B13)** e dos itens complementares **B14 e B15**, fornecendo para cada um:
1. Um **plano de ação técnico de código** (com arquivos, linhas, assinaturas e diffs conceituais) para os itens que exigem correção;
2. Ou uma **justificativa técnica fundamentada** para os itens fechados como decisões deliberadas de projeto, rigor metrológico ou intertravamentos de segurança de hardware.

### 1.2 Diagnóstico Geral das Camadas e Principais Conclusões
- **Firmware do Nó (`sensor-biomassa`):** O nó possui um pipeline óptico sofisticado, contendo sincronização com fronteira de conversão do VEML7700 (`waitForConversionBoundary`), filtro mediano de 5 pontos, filtro passa-baixas exponencial (EMA) e proteção rígida do LED emissor ($D \le 8\%$ de *duty cycle* médio). Todavia, foram diagnosticadas falhas críticas:
  - **Vulnerabilidade Térmica Crítica (B04):** A função `setManualGear()` aciona o LED via `pwmSetLevel()` e só o desliga se o nó estiver em `IDLE`. Durante o estado `MEASURING`, o LED permanece aceso continuamente a 100% de intensidade por até 25 s, superaquecendo o emissor óptico e destruindo o *baseline* escuro.
  - **Perda de Parâmetros em Queda de Energia (B05):** Comandos de ajuste de limiares de auto-range (`low`, `high`, `opt`) e período de amostragem (`probe_period`, `refresh_ms`) atualizam apenas a struct `g_config` em RAM, omitindo `saveConfig()`. Qualquer reboot faz o sensor retornar aos padrões de fábrica gravados na NVS.
  - **Identidade Fragmentada (B07):** O nó transmite versão `v11` no protocolo serial e no `/nodeHello`, mas expõe `"Biomass Sensor Firmware v5.3"` na página HTML de atualização OTA em `LocalHttpApi.h:80` e em seus manuais locais.
- **Gateway Central (`ESP32S3-HUB`):**
  - **Oscilação de Presença e Alarmes Falsos (B01):** O sensor adota período de amostragem padrão de 25 s (piso térmico de 24,3 s para integração de 800 ms). Como o Hub impõe timeout rígido de 10 s (`BIOMASS_TIMEOUT`), o nó é declarado offline após 10 segundos de cada amostra, fazendo o aplicativo piscar o alarme de *Absorbância offline* e apagar as leituras do sinótico a cada ciclo.
  - **Omissão de Controle de Auto-range (B03):** O Hub não possui mapeamento para a chave `biomassAutoRange`, impedindo o operador de comutar entre busca automática de marcha e ganho manual fixo.
  - **Código Morto Vestigial (B12):** O Hub processa a chave `test_period`, mas não possui rota para `test_on`/`test_off`.
- **Aplicativos e Documentação:**
  - **Tratamento de Sentinelas Numéricas no App Windows (B13):** Em casos de branco inválido/saturado ($A = -99,0$) ou escuro total ($A = 9,9$), o supervisor exibe esses valores como números válidos e os plota em gráficos históricos de batelada.
  - **Supervisão contra Parada Silenciosa (B06):** Em caso de reboot pós-queda de energia, o nó nasce em `IDLE`. O aplicativo não alarma essa condição caso o roteamento esteja habilitado, deixando o cultivo desassistido.
  - **Temporização da Calibração de Branco (B09):** O motor de receitas estipulava temporizador cego de 15 s para o branco, enquanto a varredura real completa (4 IT × 8 PWM) demanda entre 20 s e 40 s (típico 30 s).

---

## 2. Arquitetura do Sistema e Topologia de Comunicação

### 2.1 Topologia de 4 Camadas do Ecossistema TECNAL
O subsistema de medição de biomassa opera em arquitetura cliente-servidor distribuída e particionada em quatro camadas operacionais:

```
┌─────────────────────────────────────────────────────────────────────────┐
│                    APLICATIVO SUPERVISOR (Windows_app)                  │
│  - BiomassControlViewModel    - BiomassCalibrationViewModel             │
│  - TelemetryParser.cs         - AlarmService (Absorbância Offline)      │
│  - CommandBuilders.Biomass*   - RecipeEngine.ExternalDevices (Branco)   │
└────────────────────────────────────┬────────────────────────────────────┘
                                     │ HTTP REST (/command, /readData, /nodes)
                                     ▼
┌─────────────────────────────────────────────────────────────────────────┐
│                        OPENTEC-HUB (ESP32-S3 v10.2)                     │
│  - Mailboxes.h (biomassBox: ReliableMailbox, semeadura aleatória cmd_id)│
│  - Commands.h (Roteamento estrito: um comando por revisão, descartes)   │
│  - Telemetry.h (Agregador /readData: BiomassOnline, BiomassAbs, Ecos)   │
│  - HttpServer.h (Endpoints: /biomassData, /biomassCommand, /nodeHello)  │
└────────────────────────────────────┬────────────────────────────────────┘
                                     │ Wi-Fi STA / HTTP (Push GET / Poll 2s)
                                     ▼
┌─────────────────────────────────────────────────────────────────────────┐
│                 NÓ SENSOR DE BIOMASSA (ESP32-S3 v11.0)                  │
│  - Core 1: HTTP Client, ServiceRuntime, CommandCodec, LocalHttpApi (AP) │
│  - Core 0 / Laço: BlankingAndRange, MeasurementPipeline, SampleFilter   │
│  - Hardware: VEML7700 (I²C 100 kHz), LED Emissor (GPIO 18, LEDC 2 kHz)  │
└─────────────────────────────────────────────────────────────────────────┘
                                     │ Canal Serial USB (115200 baud) ou AP
                                     ▼
┌─────────────────────────────────────────────────────────────────────────┐
│                 CLIENTE PYTHON DESKTOP (Bancada/Pesquisa)               │
│  - biomass_core.py (Sample, RingBuffer, Backfill /api/history)          │
│  - biomass_gui.py (Assistente de Branco Cadenciado, Auto-range Toggle)  │
└─────────────────────────────────────────────────────────────────────────┘
```

### 2.2 Cadeia de Medição Óptica e Formulação Matemática
O cálculo de absorbância segue estritamente a Lei de Beer-Lambert relativa, parametrizada pela matriz de calibração de branco prévio $I_0$:

$$A = -\log_{10}\left( \frac{\min(I_{\text{raw}}, I_0)}{I_0} \right)$$

Onde:
1. **Disparo Pulsado do LED:** Para garantir estabilidade térmica, o LED só é ligado durante o tempo de integração do sensor ($IT$). O período de repouso escuro $T_{\text{repouso}}$ obedece ao limite térmico:
   $$D = \frac{T_{\text{pulso}}}{T_{\text{amostragem}}} \le 0{,}08 \implies T_{\text{amostragem}} \ge \frac{T_{\text{pulso}}}{0{,}08}$$
   Para o tempo de integração máximo ($IT = 800\text{ ms}$), o pulso óptico total (estabilização + sincronização de fronteira + integração) atinge $1.946\text{ ms}$, impondo o piso térmico absoluto de **$24.325\text{ ms}$** ($\approx 24{,}3\text{ s}$).
2. **Matriz de Marchas Ópticas:** O sensor opera com 32 marchas discretas, calculadas pela combinação linear de 4 slots de tempo de integração ($IT \in \{100, 200, 400, 800\}\text{ ms}$) e 8 slots de potência PWM ($PWM \in \{2{,}0; 3{,}5; 6{,}0; 10{,}5; 18{,}0; 32{,}0; 57{,}0; 100{,}0\}\%$):
   $$\text{Gear} = IT_{\text{index}} \times 8 + PWM_{\text{index}} \quad (\text{Gear} \in [0, 31])$$
3. **Filtro Mediano e EMA:** A leitura de intensidade bruta $I$ passa por um filtro mediano de 5 amostras e, subsequentemente, por um filtro de média móvel exponencial (*Exponential Moving Average* — EMA):
   $$I_{\text{EMA}}(k) = \alpha \cdot I_{\text{mediana}}(k) + (1 - \alpha) \cdot I_{\text{EMA}}(k-1), \quad \alpha \in [0{,}01; 1{,}0] \ (\text{padrão } 0{,}8)$$
4. **Matriz de Referência de Branco ($I_0$):** A rotina de calibração de branco varre as 32 células ópticas armazenando $I_0(IT, PWM)$ na NVS. Uma célula é considerada com branco válido se e somente se:
   $$500 \le I_0(IT, PWM) < 65.530\text{ contagens}$$
   Células que atingem o teto de saturação ($65.535$) são marcadas como saturadas e nunca são selecionadas pelo auto-range.

### 2.3 Orçamento de Memória e Headroom de Flash do ESP32-S3
O firmware da biomassa é compilado para o microcontrolador ESP32-S3 com tabela de partição de 1,28 MB para aplicação OTA:
- **Tamanho da Partição (`app0` / `app1`):** `0x140000` = $1.310.720\text{ bytes}$.
- **Tamanho do Binário Atual (`biomass-sensor.ino.bin`):** $1.129.872\text{ bytes}$ ($86{,}2\%$ de ocupação).
- **Espaço Livre na Partição:** $180.848\text{ bytes}$ ($176{,}6\text{ kB}$).
- **Piso Mínimo de Segurança OTA Estabelecido:** $160.000\text{ bytes}$ de folga para variações de layout.
- **Headroom Livre Efetivo:** $\approx 20{,}8\text{ kB}$ (ou $\approx 5{,}3\text{ kB}$ em compilações completas de debug).
- **Diretriz Mandatória de Engenharia:** Todas as alterações no firmware devem utilizar buffers estáticos pré-alocados, manipuladores manuais de string zero-allocation (`snprintf`, `strstr`) e evitar inclusão de bibliotecas externas pesadas (como ArduinoJson), sob pena de estourar a partição de flash e inviabilizar o rollback OTA.

---

## 3. Análise Técnica Detalhada das Lacunas e Inconsistências (B01 a B13, plus B14-B15)

---

### B01 — Janela de Presença e Cadência em MEASURING

#### 1. Problema Declarado e Causa Raiz
No estado operacional `MEASURING`, o sensor de biomassa só emite telemetria (`/biomassData`) ao publicar uma amostra fresca consolidada, o que ocorre a cada `probe_ms` (padrão de fábrica de 25 000 ms; piso térmico de hardware de 24 325 ms para integração de 800 ms). No entanto, o gateway ESP32S3-HUB opera com um timeout fixo de conectividade de 10 000 ms (`BIOMASS_TIMEOUT = 10000;` em `AppContext.h:371`).
Como o intervalo entre transmissões ($25\text{ s}$) é 2,5 vezes maior que o timeout do Hub ($10\text{ s}$), o Hub declara `BiomassOnline = false` e omite `BiomassAbs` exatamente aos 10 segundos decorridos de cada ciclo. No aplicativo Windows, o sinótico limpa a leitura para `"—"`, a receita de inicialização falha por instabilidade de leitura e o alarme *Absorbância offline* pisca intermitentemente em regime permanente.
A causa raiz reside na assimetria entre o piso de repouso térmico do LED e a ausência de parametrização dinâmica da janela de presença no Hub, somada à inibição do envio de heartbeats de presença (`idle=1`) pelo firmware do nó durante a medição periódica (`Lifecycle.h:179` impõe `g_state != MEASURING`).

#### 2. Localização Exata no Código-Fonte
- **Hub:** `ESP32S3-HUB/ESP32S3-HUB/src/core/AppContext.h:371`:
  ```cpp
  const unsigned long BIOMASS_TIMEOUT = 10000;
  ```
- **Hub:** `ESP32S3-HUB/ESP32S3-HUB/src/sensor/Telemetry.h:171–177`:
  ```cpp
  bool biomassOnline = snapBiomassUpdate > 0 && (millis() - snapBiomassUpdate <= BIOMASS_TIMEOUT);
  bool validBiomass = false;
  if (snapBiomassComm && snapBiomassSampleUpdate > 0) {
      unsigned long age = millis() - snapBiomassSampleUpdate;
      if (age <= BIOMASS_TIMEOUT) validBiomass = true;
  }
  ```
- **Hub:** `ESP32S3-HUB/ESP32S3-HUB/src/network/HttpServer.h:571`:
  ```cpp
  else if (i == DEV_BIOMASS) isOnline = (biomassLastUpdate > 0 && (now - biomassLastUpdate <= BIOMASS_TIMEOUT));
  ```
- **Firmware Nó:** `External-Devices/sensor-biomassa/firmware/biomass-sensor/src/core/Lifecycle.h:179–186`:
  ```cpp
  if (g_hubEnabled && g_state != MEASURING &&
      now - lastHubHeartbeatMs >= heartbeatInterval) {
    if (WiFi.status() == WL_CONNECTED) {
      sendDataToHub();   // updates lastHubHeartbeatMs itself
    } else {
      lastHubHeartbeatMs = now;
    }
  }
  ```

#### 3. Impacto Sistêmico Cruzado
- **Hub:** Transmite oscilação binária de presença em `/readData` e na listagem de `/nodes`.
- **App Windows:** `TelemetryParser.cs` e `BiomassControlViewModel.cs` alternam entre valor e nulo a cada 25 segundos. Disparos frequentes no `AlarmService.cs` (código de alarme `AlarmId.BiomassOffline`).
- **Motor de Receitas:** Falha na ação `AwaitBiomassMeasuringAsync` caso o timer avalie o sensor durante a janela morta.

#### 4. Decisão Técnica de Ação e Plano de Modificação (FIX COMBINADO)
A solução adotada é idêntica à consagrada para o sensor de distância (item D03):
1. **No Hub (Resolução Primária):** Tornar a janela de presença e de freshness proporcional ao período de amostragem ecoado pelo nó (`snapBiomassProbePeriodMs`). O Hub já armazena esse parâmetro vindo de `/biomassData?probe_ms=...`, bastando utilizá-lo para calcular a janela elástica:
   $$\text{biomassPresenceWindowMs}(P) = \max\left(10000\text{ ms}, \; (unsigned\ long)(P \times 2{,}5f)\right)$$
2. **No Firmware do Nó (Resolução Complementar):** Permitir o envio de heartbeats leves (`idle=1`) em `Lifecycle.h` mesmo em `MEASURING`, desde que o nó esteja em repouso escuro aguardando o próximo pulso (`now < g_nextReadTime`) e não haja acionamento de hardware em curso. Isso garante que cultivos ultra-lentos (ex: $P = 60\text{ s}$ ou $300\text{ s}$) mantenham a presença viva a cada 5 s sem renovar a amostra óptica.

##### Snippet de Modificação no Hub (`AppContext.h`, `Telemetry.h`, `HttpServer.h`):
```cpp
// Em AppContext.h:
inline unsigned long biomassPresenceWindowMs(uint32_t probePeriodMs) {
  unsigned long dyn = probePeriodMs > 0 ? (unsigned long)(probePeriodMs * 2.5f) : BIOMASS_TIMEOUT;
  return dyn > BIOMASS_TIMEOUT ? dyn : BIOMASS_TIMEOUT;
}

// Em Telemetry.h (linhas 171-177):
unsigned long bioWin = biomassPresenceWindowMs(snapBiomassProbePeriodMs);
bool biomassOnline = snapBiomassUpdate > 0 && (millis() - snapBiomassUpdate <= bioWin);
bool validBiomass = false;
if (snapBiomassComm && snapBiomassSampleUpdate > 0) {
    unsigned long age = millis() - snapBiomassSampleUpdate;
    if (age <= bioWin) validBiomass = true;
}

// Em HttpServer.h (linha 571):
else if (i == DEV_BIOMASS) {
  unsigned long bioWin = biomassPresenceWindowMs(biomassProbePeriodMs);
  isOnline = (biomassLastUpdate > 0 && (now - biomassLastUpdate <= bioWin));
}
```

##### Snippet de Modificação no Firmware (`src/core/Lifecycle.h`):
```cpp
// Em Lifecycle.h (substituir linha 179):
const bool measuringRest = (g_state == MEASURING && now < g_nextReadTime && g_targetPct == 0.0f);
if (g_hubEnabled && (g_state != MEASURING || measuringRest) &&
    now - lastHubHeartbeatMs >= heartbeatInterval) {
  if (WiFi.status() == WL_CONNECTED) {
    sendDataToHub();
  } else {
    lastHubHeartbeatMs = now;
  }
}
```

#### 5. Classificação de Risco e Segurança
- **Prioridade:** ALTA (Impacto Crítico na Usabilidade e Alarmística).
- **Classificação de Segurança:** Mitigação operacional sem risco elétrico. Preserva integralmente o piso térmico de descanso óptico.

---

### B02 — Rotinas Bloqueantes Isolam o Nó do Hub

#### 1. Problema Declarado e Causa Raiz
As rotinas de varredura completa de branco (`runBlankingRoutine()`, 20 a 40 s), busca de marchas de auto-range (`findAndSetOptimalGear()`, 3 a 10 s) e diagnóstico de conversão (`probeConversionPeriod()`, ~5 s) executam laços síncronos invocando `delayServiced()`. Essa função atende exclusivamente o watchdog (`esp_task_wdt_reset()`), o servidor Web local no AP (`server.handleClient()`) e o canal serial USB (`handleSerialInput()`). Durante esses intervalos, o nó não executa push (`sendDataToHub()`) nem polling (`pollHubForCommands()`).
Consequências:
1. O Hub marcava o nó offline após 10 s (resolvido por B01).
2. Comandos de parada ou emergência emitidos pelo Hub (`{"stop":1}`) não são lidos durante o branco;
3. A caixa confiável `biomassBox` no Hub retém apenas a última revisão: se o operador ou receita enviar uma ordem de `start:1` antes de o nó buscar o `blank:1` (no poll de 2 s), a revisão de `blank` é sobrescrita e a calibração nunca é executada.

#### 2. Localização Exata no Código-Fonte
- **Firmware Nó:** `External-Devices/sensor-biomassa/firmware/biomass-sensor/src/core/ServiceRuntime.h:1–17`:
  ```cpp
  void serviceNetwork() {
    if (g_wdtReady) esp_task_wdt_reset();
    if (g_serverStarted && !g_inHttpHandler) server.handleClient();
    handleSerialInput(/*allowBlocking=*/false);
  }

  void delayServiced(uint32_t ms) {
    const uint32_t start = millis();
    while (millis() - start < ms) {
      serviceNetwork();
      uint32_t remaining = ms - (millis() - start);
      delay(remaining > 10 ? 10 : remaining);
    }
    serviceNetwork();
  }
  ```
- **Firmware Nó:** `src/measurement/BlankingAndRange.h:13–99` (`runBlankingRoutine`).
- **Hub:** `ESP32S3-HUB/src/protocol/Mailboxes.h` (`queueReliable`, substituição monotônica).

#### 3. Impacto Sistêmico Cruzado
- **Hub:** Mantém `BiomassCommandPending = true` sem saber se o nó travou ou está executando.
- **App Windows:** O operador fica sem retorno visual durante o branco e não consegue abortar a operação remotamente.

#### 4. Decisão Técnica de Ação e Plano de Modificação (FIX CONTROLADO)
- **Decisão:** Manter a atomicidade do Core 1 sem permitir reentrâncias concorrentes no cliente HTTP do ESP32, mas permitir polling de comando de interrupção durante pausas térmicas.
- **No Firmware:** Criar rotina segura `serviceHubPolling()` chamada a cada 2 000 ms dentro de `delayServiced()`, executada somente se `!g_inHttpHandler` e se o rádio estiver livre. Se um comando `stop` for recebido pelo poll, o nó seta a flag atômica existente `g_abortRequested = true`, provocando a saída limpa do laço de varredura e retorno imediato a `IDLE`.
- **No App:** O aplicativo supervisor já serializa comandos atrás de `BiomassCommandPending` (conferido em `BiomassControlViewModel.cs:330`), impedindo a emissão de ordens conflitantes antes do ACK do branco.

##### Snippet de Modificação no Firmware (`src/core/ServiceRuntime.h`):
```cpp
void serviceHubPolling() {
  if (!g_hubEnabled || WiFi.status() != WL_CONNECTED || g_inHttpHandler) return;
  const unsigned long now = millis();
  if (now - lastHubPollMs >= HUB_POLL_PERIOD_MS) {
    lastHubPollMs = now;
    pollHubForCommands(); // se receber "stop", seta g_abortRequested = true
  }
}

void delayServiced(uint32_t ms) {
  const uint32_t start = millis();
  while (millis() - start < ms) {
    serviceNetwork();
    serviceHubPolling();
    if (g_abortRequested) break; // Interrupção rápida sob comando remoto
    uint32_t remaining = ms - (millis() - start);
    delay(remaining > 10 ? 10 : remaining);
  }
  serviceNetwork();
}
```

#### 5. Classificação de Risco e Segurança
- **Prioridade:** MÉDIA.
- **Classificação de Segurança:** Alta resiliência. A verificação da flag `g_abortRequested` no laço previne travamentos e permite aborto seguro de rotinas demoradas.

---

### B03 — Marcha Manual Pelo Hub e Intertravamento com Auto-Range

#### 1. Problema Declarado e Causa Raiz
O firmware do sensor possui suporte nativo à operação manual através dos comandos `auto` e `manual`, bem como à seleção de marcha fixa (`set_gear`). Todavia:
1. No Hub (`Commands.h:466–472`), não há mapeamento para a chave `biomassAutoRange`, nem para chaves curtas `auto`/`manual`. Se o usuário enviar `biomassGear`, a marcha é configurada, mas o auto-range permanece ativo no nó.
2. No firmware (`CommandCodec.h:221`), ao receber o comando `start`, o nó executa incondicionalmente a rotina de *Smart Start* (`findOptimalBlankGear(startIt, startPwm)`), recalculando e sobrescrevendo a marcha manual previamente escolhida pelo operador.
3. Se 10 amostras consecutivas ficarem fora da faixa linear ($[10.000, 40.000]$ contagens), a máquina de estados entra em `SEARCHING` e troca a marcha arbitrariamente.

#### 2. Localização Exata no Código-Fonte
- **Hub:** `ESP32S3-HUB/ESP32S3-HUB/src/protocol/Commands.h:466–486` (tabela `bioNewCmds` omite chave de auto-range).
- **Firmware Nó:** `src/protocol/CommandCodec.h:221` (`findOptimalBlankGear` incondicional no `start`):
  ```cpp
  } else if (cmd.equals("start")) {
    if (!g_blankIsDone)
      Serial.println("Error: Please run 'blank' first.");
    else if (g_state == IDLE) {
      Serial.println("--- Starting Measurement ---");
      int startIt, startPwm;
      findOptimalBlankGear(startIt, startPwm); // Sobrescreve marcha manual!
      g_state = MEASURING;
      ...
  ```
- **App Windows:** `Windows_app/src/OpenTECHub.Protocol/CommandKeys.cs` e `BiomassControlViewModel.cs`.

#### 3. Impacto Sistêmico Cruzado
O operador define uma marcha específica na interface de sintonia do Windows App, mas ao clicar em *Iniciar Aquisição*, o sistema desfaz a seleção e assume outra marcha sem aviso, gerando desconfiança metrológica.

#### 4. Decisão Técnica de Ação e Plano de Modificação (FIX COMPLETO: HUB + NÓ + APP)
- **No Hub (`Commands.h`):** Mapear a chave JSON `biomassAutoRange`:
  - Se `1`, `true` ou `"auto"` $\rightarrow$ despacha `{"command":"auto"}` para a `biomassBox`.
  - Se `0`, `false` ou `"manual"` $\rightarrow$ despacha `{"command":"manual"}` para a `biomassBox`.
- **No Firmware Nó (`CommandCodec.h`):** No processamento de `start`, verificar `g_autoRange`: se o auto-range estiver desabilitado e a marcha atual possuir um branco válido (`blankIsValid(g_currentItIndex, g_currentPwmIndex)`), manter a marcha manual selecionada, chamando `findOptimalBlankGear` apenas se `g_autoRange == true` ou se o branco da marcha manual for inválido.
- **No App Windows:** Adicionar constante `BiomassAutoRange = "biomassAutoRange"` em `CommandKeys.cs`, criar o builder `CommandBuilders.BiomassAutoRange(bool autoRange)` e adicionar controle visual (Toggle Switch) em `BiomassControlViewModel.cs`.

##### Snippet de Modificação no Hub (`Commands.h`):
```cpp
if (json.indexOf("\"biomassAutoRange\"") != -1) {
  String autoVal = getValueFromJson(json, "biomassAutoRange");
  if (autoVal.length() > 0) {
    if (!biomassCmdFound) {
      bool isAuto = (autoVal == "1" || autoVal == "true" || autoVal == "auto");
      biomassCommand = "\"command\":\"" + String(isAuto ? "auto" : "manual") + "\"";
      biomassCmdFound = true;
    } else {
      ESP32_EVT(String("Biomass command descartado (um por revisao): biomassAutoRange=") + autoVal);
    }
  }
}
```

##### Snippet de Modificação no Firmware (`src/protocol/CommandCodec.h`):
```cpp
int startIt = g_currentItIndex;
int startPwm = g_currentPwmIndex;
// Smart Start só executa se auto-range estiver ativo ou se o branco manual for inválido:
if (g_autoRange || !blankIsValid(startIt, startPwm)) {
  findOptimalBlankGear(startIt, startPwm);
}
```

#### 5. Classificação de Risco e Segurança
- **Prioridade:** ALTA (Conformidade com o Contrato de Sintonia do Operador).
- **Classificação de Segurança:** Totalmente seguro. Impede divergência entre a intenção do operador e a marcha de aquisição.

---

### B04 — LED Aceso Após `set_gear` em MEASURING (Risco Térmico)

#### 1. Problema Declarado e Causa Raiz
Na função `setManualGear()` em `CommandCodec.h:73–87`, ao processar a troca de marcha manual, o código invoca `pwmSetLevel(pwmIndex)`. A função auxiliar `pwmSetLevel()` define `g_currentPwmIndex = pwmIndex` e aciona imediatamente o periférico LEDC na potência configurada (`pwmSetDutyPercent(g_config.pwmSettings[pwmIndex])`).
Logo após, `setManualGear()` executa a seguinte condicional para corte de luz:
```cpp
if (g_state == IDLE) pwmSetDutyPercent(g_manualLedOn ? g_manualLedPct : 0.0f);
```
**Causa Raiz:** Se o sensor estiver no estado `MEASURING`, a condição `g_state == IDLE` é falsa. Portanto, a instrução de corte do LED **nunca é executada**. Como resultado, o LED emissor permanece ligado com potência contínua durante todo o intervalo de espera até a próxima leitura periódica (que pode durar até 25 segundos ou mais). Isso viola frontalmente o limite térmico de $8\%$ de duty cycle médio estipulado pelo projeto de hardware, provoca aquecimento do fotodiodo e distorce o baseline escuro.

#### 2. Localização Exata no Código-Fonte
- **Firmware Nó:** `External-Devices/sensor-biomassa/firmware/biomass-sensor/src/protocol/CommandCodec.h:73–87`:
  ```cpp
  void setManualGear(int itIndex, int pwmIndex) {
    if (itIndex < 0 || itIndex >= g_config.IT_COUNT ||
        pwmIndex < 0 || pwmIndex >= g_config.PWM_COUNT) {
      Serial.println("Error: gear index out of range.");
      return;
    }
    vemlSetConfig(itIndex);
    pwmSetLevel(pwmIndex); // Liga o LED imediatamente!
    if (g_state == IDLE) pwmSetDutyPercent(g_manualLedOn ? g_manualLedPct : 0.0f);
    Serial.print("Manual gear set: IT ");
    ...
  ```
- **Firmware Nó:** `src/sensor/Veml7700Driver.h:246–252` (`pwmSetLevel` aciona `pwmSetDutyPercent`).

#### 3. Impacto Sistêmico Cruzado
- **Hardware:** Sobrecarga térmica no LED emissor (GPIO 18) e no fotodetector VEML7700, podendo provocar deriva irreversível de emissão óptica.
- **Metrologia:** Elevação do ruído de fundo (*dark current*) e perda de calibração do branco $I_0$.

#### 4. Decisão Técnica de Ação e Plano de Modificação (FIX CRÍTICO MANDATÓRIO)
- **Decisão:** Corrigir imediatamente no firmware do nó. Trata-se de uma falha crítica de segurança de hardware.
- **Ação:** Em `setManualGear()`, garantir o desligamento imediato do LED (`pwmSetDutyPercent(0.0f)`), respeitando `g_manualLedOn` apenas no estado `IDLE` de bancada. Adicionalmente, quando em `MEASURING`, reprogramar `g_nextReadTime` para garantir o tempo de relaxação térmica no escuro antes da emissão do próximo pulso de medição.

##### Snippet de Modificação no Firmware (`src/protocol/CommandCodec.h:79–85`):
```cpp
void setManualGear(int itIndex, int pwmIndex) {
  if (itIndex < 0 || itIndex >= g_config.IT_COUNT ||
      pwmIndex < 0 || pwmIndex >= g_config.PWM_COUNT) {
    Serial.println("Error: gear index out of range.");
    return;
  }
  vemlSetConfig(itIndex);
  g_currentItIndex = itIndex;
  g_currentPwmIndex = pwmIndex;

  // CORREÇÃO B04: Garante corte imediato do LED fora de IDLE manual de bancada
  pwmSetDutyPercent((g_state == IDLE && g_manualLedOn) ? g_manualLedPct : 0.0f);

  if (g_state == MEASURING) {
    // Reprograma o próximo pulso para respeitar o piso térmico no escuro
    g_nextReadTime = millis() + g_config.itRefreshTimes[g_currentItIndex];
  }

  Serial.print("Manual gear set: IT ");
  Serial.print(g_config.itDelays[itIndex]);
  Serial.print("ms, PWM ");
  Serial.print(g_config.pwmSettings[pwmIndex]);
  Serial.println("%");
}
```

#### 5. Classificação de Risco e Segurança
- **Prioridade:** CRÍTICA (Proteção Direta de Integridade de Hardware).
- **Classificação de Segurança:** Intertravamento de proteção contra sobreaquecimento de emissor semicondutor.

---

### B05 — Persistência Parcial de Limiares e Período

#### 1. Problema Declarado e Causa Raiz
Os comandos de limiares de auto-range (`low`, `high`, `opt`) e os comandos de cadência de amostragem (`probe_period`, `refresh_ms`, `probe_ms`) alteram os campos da struct de configuração em RAM (`g_config`), mas **não chamam `saveConfig()`**.
O comando explícito `save_config` não é roteado pelo Hub por diretriz de segurança de flash. Consequentemente, caso o nó sofra uma perda de alimentação ou seja reiniciado, todas as alterações de limiares e períodos configurados pelo operador ou por receitas são perdidas, restaurando os padrões da NVS de fábrica.

#### 2. Localização Exata no Código-Fonte
- **Firmware Nó:** `External-Devices/sensor-biomassa/firmware/biomass-sensor/src/protocol/CommandCodec.h:260–279` (`probe_period` sem `saveConfig()`).
- **Firmware Nó:** `src/protocol/CommandCodec.h:403–453` (`low`, `high`, `opt`, `refresh_ms` alteram `g_config` sem salvar).
- **Firmware Nó:** `src/storage/Stores.h:48–78` (`saveConfig()` persiste a struct com cálculo de CRC32).

#### 3. Impacto Sistêmico Cruzado
- **Hub e App:** O aplicativo exibe os parâmetros enviados como confirmados, mas após um reset espúrio do nó, os limiares voltam a $10.000$ e $40.000$, alterando a sensibilidade de troca de marcha sem o conhecimento do operador.

#### 4. Decisão Técnica de Ação e Plano de Modificação (FIX COM GRAVAÇÃO COALESCIDA)
- **Decisão:** Implementar a chamada a `saveConfig()` no firmware do nó, com proteção de coalescência contra desgaste prematuro da flash NOR do ESP32.
- **Ação:**
  1. No bloco de `probe_period`, invocar `saveConfig()` logo após atualizar o vetor `itRefreshTimes`.
  2. No bloco de propriedades numéricas (`low`, `high`, `opt`, `refresh_ms`), utilizar a flag booleana local `bool configModified = false`. Comparar o novo valor com o valor atual em RAM; caso haja alteração real, atualizar a struct e marcar a flag. Ao término do processamento do frame JSON, se `configModified == true`, disparar exatamente uma única escrita em NVS.

##### Snippet de Modificação no Firmware (`src/protocol/CommandCodec.h`):
```cpp
// Em probe_period (linhas 274-278):
for (int i = 0; i < g_config.IT_COUNT; i++) {
  g_config.itRefreshTimes[i] = (uint32_t)periodVal;
}
saveConfig(); // Persiste alteração de período

// No bloco de propriedades numéricas (linhas 403-455):
bool configModified = false;
long lowVal = getJsonValue(json, "low");
if (lowVal != -999999 && lowVal != g_config.LOW_THRESHOLD_RAW) {
  g_config.LOW_THRESHOLD_RAW = (uint16_t)lowVal;
  configModified = true;
}
long highVal = getJsonValue(json, "high");
if (highVal != -999999 && highVal != g_config.HIGH_THRESHOLD_RAW) {
  g_config.HIGH_THRESHOLD_RAW = (uint16_t)highVal;
  configModified = true;
}
long optVal = getJsonValue(json, "opt");
if (optVal != -999999 && optVal != g_config.TARGET_RAW) {
  g_config.TARGET_RAW = (uint16_t)optVal;
  configModified = true;
}
long refreshVal = getJsonValue(json, "refresh_ms");
if (refreshVal != -999999 && refreshVal > 0 && refreshVal != g_config.itRefreshTimes[g_currentItIndex]) {
  for (int i = 0; i < g_config.IT_COUNT; i++) g_config.itRefreshTimes[i] = (uint32_t)refreshVal;
  configModified = true;
}

if (configModified) {
  saveConfig(); // Gravação única coalescida na NVS
}
```

#### 5. Classificação de Risco e Segurança
- **Prioridade:** ALTA (Consistência Pós-Reboot).
- **Classificação de Segurança:** Preserva os ciclos de vida útil da flash NOR (escrita condicional apenas se o valor diferir).

---

### B06 — Reinício Silencioso Pós-Queda de Energia

#### 1. Problema Declarado e Causa Raiz
Se o sensor de biomassa sofrer uma queda de energia ou reset enquanto estiver operando em `MEASURING`, ele é reinicializado compulsoriamente no estado `IDLE`. Ele restabelece conexão Wi-Fi com o Hub e começa a transmitir heartbeats periódicos com `idle=1` a cada 5 segundos.
Como o Hub recebe o heartbeat, ele reporta `BiomassOnline = true`. O aplicativo supervisor vê o chip verde do nó online, mas a aquisição óptica parou completamente e nenhuma nova amostra é produzida. Não há disparo de alarme sonoro ou visual, resultando em interrupção silenciosa da curva de crescimento do bioprocesso.

#### 2. Localização Exata no Código-Fonte
- **Firmware Nó:** `src/core/FirmwareApp.cpp:139` (`SystemState g_state = IDLE;`).
- **Hub:** `ESP32S3-HUB/src/network/HttpServer.h:310–357` (recebe push `idle=1` e renova presença).
- **App Windows:** `Windows_app/src/OpenTECHub/Services/Alarms/AlarmService.cs:529–532`.

#### 3. Impacto Sistêmico Cruzado
Bateladas biológicas de fermentação (que duram de 24 horas a semanas) podem ter horas de dados perdidos sem que o operador humano seja alertado de que o sensor parou de medir.

#### 4. Decisão Técnica de Ação (DECISÃO DE ARQUITETURA + WATCHDOG NO APP)
- **Decisão:** **NÃO reimpor `start` automaticamente pelo Hub nem pelo Firmware**.
  *Justificativa de Segurança:* Se o operador interrompeu a medição propositalmente para retirar a cubeta ou injetar reagente, um religamento autônomo sem supervisão acionaria o feixe de luz em frasco desmontado ou com fluido inadequado.
- **Ação Técnica no Aplicativo Windows (`AlarmService.cs`):**
  Implementar regra de supervisão no `AlarmService`: se a comunicação com o nó estiver habilitada (`BiomassCommEnabled == true`) e o nó estiver online (`BiomassOnline == true`), porém nenhuma nova amostra válida for recebida por um período superior a $2{,}5 \times \text{BiomassProbePeriodMs}$ (ou timeout de 60 segundos), disparar imediatamente um alarme visual e sonoro de advertência de processo:
  *"Aquisição de Biomassa Interrompida: Sensor reiniciado em repouso (IDLE)"*.

#### 5. Classificação de Risco e Segurança
- **Prioridade:** MÉDIA.
- **Classificação de Segurança:** Intertravamento de segurança de processo. Impede acionamento não supervisionado de atuador óptico.

---

### B07 — Inconsistência de Identidade e Versões

#### 1. Problema Declarado e Causa Raiz
O firmware anuncia `FW_VERSION "v11"` no serial de inicialização, no endpoint de handshake `/nodeHello?ver=v11` e no relatório estruturado `/diag`. Contudo:
1. A página HTML de atualização OTA em `LocalHttpApi.h:80` declara fixo o texto `"Biomass Sensor Firmware v5.3"`;
2. A constante de nome interno em `FirmwareApp.cpp:26` é declarada como `FW_NAME "biomass_sensor_analog_v04_direct"`;
3. Os arquivos documentais locais (`README.md`, `CURRENT_STATUS.md`, `CHANGELOG.md`) referenciam a versão legada `v5.3`.

#### 2. Localização Exata no Código-Fonte
- **Firmware Nó:** `External-Devices/sensor-biomassa/firmware/biomass-sensor/src/core/FirmwareApp.cpp:25–26`:
  ```cpp
  static const char* FW_VERSION = "v11";
  static const char* FW_NAME = "biomass_sensor_analog_v04_direct";
  ```
- **Firmware Nó:** `src/api/LocalHttpApi.h:80`:
  ```html
  <p>Running: <b>Biomass Sensor Firmware v5.3</b></p>
  ```
- **Documentos Locais:** `README.md:3`, `CURRENT_STATUS.md:3`, `CHANGELOG.md:5`.

#### 3. Impacto Sistêmico Cruzado
Confusão do time de validação e operadores durante atualização via interface Web do nó, parecendo que um firmware obsoleto foi gravado.

#### 4. Decisão Técnica de Ação e Plano de Modificação (FIX DOCUMENTAL E DE CÓDIGO)
- **No Firmware:**
  1. Em `FirmwareApp.cpp`: Padronizar `FW_VERSION = "v11.0"` e `FW_NAME = "biomass_sensor"`.
  2. Em `LocalHttpApi.h`: Tornar a renderização da versão dinâmica na página Web OTA, injetando `FW_VERSION`:
     ```cpp
     "<p>Running: <b>Biomass Sensor Firmware " + String(FW_VERSION) + "</b></p>"
     ```
- **Na Documentação:** Atualizar os cabeçalhos de `README.md`, `CURRENT_STATUS.md` e `CHANGELOG.md` do repositório da biomassa para referenciar a versão estável oficial `v11.0`.

#### 5. Classificação de Risco e Segurança
- **Prioridade:** BAIXA (Alinhamento Documental e Cosmético).
- **Classificação de Segurança:** Sem impacto funcional.

---

### B08 — Divergência em Documento do Aplicativo (`PROTOCOL.md`)

#### 1. Problema Declarado e Causa Raiz
O antigo arquivo de especificações `Windows_app/docs/PROTOCOL.md` descrevia comandos inexistentes (`set_ema`, `set_period`) e indicava que a caixa de correio do Hub consumia mensagens sem ACK confiável.

#### 2. Localização e Status
- **Arquivo:** `Windows_app/docs/PROTOCOL.md`.
- **Status:** **RESOLVIDO EM REVISÃO DOCUMENTAL PRÉVIA (DOC)**.
  O arquivo já foi alinhado ao comportamento real do Hub v10.2, que traduz `biomassEma` para `ema`, `biomassProbePeriodMs` para `probe_period` e opera sobre a estrutura `ReliableMailbox` com ACK monotônico (`ack_cmd_id`).

#### 3. Decisão Técnica de Ação
- **Decisão:** **FECHADO COMO RESOLVIDO / NENHUMA AÇÃO DE CÓDIGO NECESSÁRIA**.

#### 4. Classificação de Risco e Segurança
- **Prioridade:** N/A (Item Resolvido).
- **Classificação de Segurança:** N/A.

---

### B09 — Duração da Varredura de Branco pelo Hub

#### 1. Problema Declarado e Causa Raiz
Quando disparado pelo Hub (`{"blank":1}`), o sensor executa a varredura completa da matriz $4 \times 8$ (32 células) em modo não cadenciado. Com tempos de integração que chegam a 800 ms e tempos de estabilização do LED, a rotina dura entre 20 e 40 segundos (típico 30 segundos reais).
O motor de receitas do aplicativo Windows assumia um temporizador cego de $\sim 15\text{ s}$ e despachava o comando subsequente de início (`start:1`) antes do término do branco. O nó, ainda em `BLANKING`, rejeitava a ordem retornando `"Busy"`, abortando a receita automatizada de inoculação.

#### 2. Localização Exata no Código-Fonte
- **Firmware Nó:** `src/measurement/BlankingAndRange.h:13–99` (`runBlankingRoutine`).
- **App Windows:** `Windows_app/src/OpenTECHub/Services/Recipes/RecipeEngine.ExternalDevices.cs:101`.
- **App Windows:** `Windows_app/src/OpenTECHub.Protocol/RecipeEnums.cs:225`.

#### 3. Impacto Sistêmico Cruzado
Falha na execução de receitas autônomas de partida de bioprocesso no OpenTECHub.

#### 4. Decisão Técnica de Ação e Plano de Modificação (FIX NO APP / RECEITAS)
- **Decisão:** Eliminar temporizações cegas nas receitas e sincronizar o avanço pela flag de ACK do comando confiável.
- **Ação Técnica no App Windows:**
  1. Em `RecipeEngine.ExternalDevices.cs`, condicionar a transição da etapa de calibração de branco à queda da flag `BiomassCommandPending` (de `true` para `false`), que atesta a aplicação da ordem e emissão do ACK pelo nó.
  2. Ajustar o timeout de guarda da etapa para 60 segundos (margem de segurança sobre os 40 s máximos).
  3. Atualizar as descrições de tela e logs de receita alertando que o branco óptico dura tipicamente 30 segundos.

#### 5. Classificação de Risco e Segurança
- **Prioridade:** MÉDIA.
- **Classificação de Segurança:** Assegura a integridade de início de receitas automatizadas.

---

### B10 — Invalidação de Branco ao Alterar IT ou PWM

#### 1. Problema Declarado e Causa Raiz
Os comandos de reconfiguração de tabela óptica (`set_it`, `set_pwm` e `pwm_preset`) invalidam a calibração de branco armazenada na NVS (`invalidateBlank()`) e derrubam o sensor de `MEASURING` para o estado `IDLE`.

#### 2. Localização Exata no Código-Fonte
- **Firmware Nó:** `src/protocol/CommandCodec.h:325, 340, 359` (chamadas a `invalidateBlank(false)`).

#### 3. Decisão Técnica de Ação (DECISÃO DE PROJETO / RIGOR METROLÓGICO)
- **Decisão:** **JUSTIFICAR NÃO-IMPLEMENTAÇÃO / MANTER INALTERADO**.
  *Justificativa Física Fundamental:* Pela Lei de Beer-Lambert relativa ($A = -\log_{10}(I / I_0)$), a absorbância depende estritamente da relação entre o sinal atual e a intensidade de referência com líquido claro ($I_0$). O valor $I_0$ é fisicamente dependente do fluxo de fótons (determinado pela corrente do LED via PWM) e do tempo de integração no sensor ALS ($IT$). Qualquer alteração nesses parâmetros de hardware descalibra a matriz de referência $I_0$. Permitir que o sensor continue medindo com uma matriz de branco obtida sob outra escada de integração introduziria erros grosseiros de medição. A invalidação e o retorno a `IDLE` são mandatórios. O aplicativo Windows já informa claramente que alterar a configuração óptica exige nova captura de branco.

#### 4. Classificação de Risco e Segurança
- **Prioridade:** N/A (Decisão de Engenharia Fechada).
- **Classificação de Segurança:** Salvaguarda de confiabilidade metrológica analítica.

---

### B11 — Comandos Perigosos Restritos ao Canal Local (`hub_off` e `factory`)

#### 1. Problema Declarado e Causa Raiz
Os comandos `hub_off` (que desativa o cliente Wi-Fi STA com o Hub e salva essa preferência em NVS) e `factory` (que limpa tabelas e calibrações) só funcionam quando enviados via porta Serial USB ou através do servidor Web local (SoftAP `192.168.7.1`).

#### 2. Localização Exata no Código-Fonte
- **Firmware Nó:** `src/protocol/CommandCodec.h:233, 366`.
- **Hub:** `ESP32S3-HUB/src/protocol/Commands.h` (omite chaves `hub_off` e `factory`).

#### 3. Decisão Técnica de Ação (DECISÃO DE PROJETO / INTERTRAVAMENTO DE SEGURANÇA)
- **Decisão:** **JUSTIFICAR NÃO-IMPLEMENTAÇÃO / MANTER ISOLAMENTO**.
  *Justificativa de Segurança Perimetral:* Se o Hub expusesse a chave `hub_off`, um comando inadvertido ou malformado enviado pelo aplicativo supervisor cortaria a interface Wi-Fi do sensor com o Hub de forma permanente (persistida em NVS). O nó ficaria incomunicável e isolado da rede, exigindo intervenção física do operador para religar o rádio via USB ou AP. O bloqueio perimetral no gateway Hub é uma decisão deliberada de segurança operacional.

#### 4. Classificação de Risco e Segurança
- **Prioridade:** N/A (Decisão de Engenharia Fechada).
- **Classificação de Segurança:** Prevenção contra isolamento acidental e perda de rota de comunicação.

---

### B12 — Roteamento Espúrio de `test_period` no Hub

#### 1. Problema Declarado e Causa Raiz
Em `ESP32S3-HUB/src/protocol/Commands.h:459–460`, o Hub possui código ativo para extrair a chave `test_period` e encaminhá-la para a caixa postal `biomassBox`. Contudo, os comandos que ativam e desativam o sweep contínuo de teste do LED (`test_on` e `test_off`) **não são roteados pelo Hub** (são exclusivos do canal direto/bancada). O envio de `test_period` via Hub é inócuo e constitui código morto residual.

#### 2. Localização Exata no Código-Fonte
- **Hub:** `ESP32S3-HUB/ESP32S3-HUB/src/protocol/Commands.h:459–460`:
  ```cpp
  String testVal = getValueFromJson(json, "test_period");
  if (testVal.length() > 0) {
    if (biomassCmdFound) biomassCommand += ",";
    biomassCommand += "\"test_period\":" + testVal;
    biomassCmdFound = true;
  }
  ```

#### 3. Decisão Técnica de Ação e Plano de Modificação (FIX COSMÉTICO NO HUB)
- **Decisão:** Remover as linhas 459–460 de `Commands.h` no Hub e atualizar os testes de contrato em `ESP32S3-HUB/tests/contracts/test_node_commands.py`, eliminando a chave obsoleta e economizando tempo de CPU na decodificação JSON.

#### 4. Classificação de Risco e Segurança
- **Prioridade:** BAIXA (Limpeza de Código e Higiene de Arquitetura).
- **Classificação de Segurança:** Sem impacto operacional.

---

### B13 — Tratamento de Valores-Sentinela de Absorbância (`-99.0` e `9.9`)

#### 1. Problema Declarado e Causa Raiz
No pipeline de cálculo do sensor (`MeasurementPipeline.h:110–130`), são definidos dois valores-sentinela numéricos para estados de falha óptica:
1. $A = -99{,}0\text{ AU}$: emitido quando a célula de referência de branco da marcha corrente é inválida ($I_0 = 0$ ou $I_0 = 65.535$);
2. $A = 9{,}9\text{ AU}$: emitido quando a leitura de intensidade transmitida é nula ($I = 0$), representando densidade óptica infinita (feixe bloqueado ou amostra totalmente opaca).
Esses sentinelas chegam ao Hub como números de ponto flutuante comuns e são repassados ao aplicativo supervisor em `BiomassAbs`. O aplicativo exibe na tela `"−99,000"` ou `"9,900"`, plota esses pontos em gráficos de batelada (distorcendo a escala de eixos) e o motor de receitas aceita `9,9` como valor de absorbância válido para liberar rotinas de cultivo.

#### 2. Localização Exata no Código-Fonte
- **Firmware Nó:** `External-Devices/sensor-biomassa/firmware/biomass-sensor/src/measurement/MeasurementPipeline.h:110–125`:
  ```cpp
  if (current_I0 == 0 || current_I0 == 65535) {
    g_lastAbsorbance = -99.0f; // Error: Blank is 0 or Saturated
  } else {
    if (current_I > current_I0) {
      current_I = current_I0;
    }
    if (current_I == 0) {
      g_lastAbsorbance = 9.9f; // Error: True zero reading
    } else {
      g_lastAbsorbance = -log10((float)current_I / (float)current_I0);
    }
  }
  ```
- **App Windows:** `Windows_app/src/OpenTECHub.Protocol/SensorReadings.cs`, `TelemetryParser.cs`, `BiomassControlViewModel.cs:754` e `RecipeEngine.Devices.cs:132`.

#### 3. Impacto Sistêmico Cruzado
Distorção visual em telas de supervisão e risco de receitas operarem sobre dados de falha como se fossem leituras válidas de processo.

#### 4. Decisão Técnica de Ação e Plano de Modificação (FIX NO APLICATIVO WINDOWS)
- **Decisão:** Manter o contrato de fio do Hub inalterado (repassando `float` puro sem inflar o payload) e tratar a apresentação e validação nas camadas de modelo e interface do aplicativo supervisor:
  1. Em `SensorReadings.cs`: Definir constantes canônicas de sentinela:
     ```csharp
     public const double BiomassBlankInvalid = -99.0;
     public const double BiomassZeroLight = 9.9;
     ```
  2. Em `BiomassControlViewModel.cs`: Tratar a formatação da propriedade `AbsorbanceText`:
     - Se $A \le -90{,}0\text{ AU}$ $\rightarrow$ Exibir `"--- [Branco Inválido]"` em cor de alerta (laranja);
     - Se $A \ge 9{,}0\text{ AU}$ $\rightarrow$ Exibir `"> 4.0 AU [Escuro/Bloqueado]"` em cor de alerta (amarelo);
     - Caso contrário, exibir formato normal `F3` (`"0,123 AU"`).
  3. Em `RecipeEngine.Devices.cs:132`: Na validação de início, exigir que a leitura seja estritamente finita e válida:
     ```csharp
     bool isValid = s.BiomassAbsorbance > -90.0 && s.BiomassAbsorbance < 9.89;
     ```
  4. Nos buffers de séries temporais para gráficos históricos, descartar amostras que caiam fora da faixa $[-0{,}5; 4{,}0]\text{ AU}$.

#### 5. Classificação de Risco e Segurança
- **Prioridade:** ALTA (Ergonomia e Segurança de Dados no Supervisor).
- **Classificação de Segurança:** Elimina a contaminação de históricos analíticos por códigos de erro numéricos.

---

### B14 — Campos Não Roteados no Hub (`hd_mode`, `i0`, `sat`, `single`, `manual`, `boot_id`)

#### 1. Problema Declarado e Causa Raiz
Metadados de telemetria diagnóstica gerados pelo nó (como indicador de modo de alta densidade `hd_mode`, referência local $I_0$, contador de saturações `sat` e identificador de inicialização `boot_id`) não são transmitidos pelo Hub em `/readData`.
O firmware do nó já inclui `&hd_mode=%d` na query string de push enviada ao Hub (`TelemetryAndHub.h:76`), mas o Hub descarta esse parâmetro em `HttpServer.h:338–350`.

#### 2. Decisão Técnica de Ação (DECISÃO DE PROJETO / GESTÃO DE HEADROOM)
- **Decisão:** **JUSTIFICAR COMO DECISÃO CONDICIONADA A HEADROOM / MANTER DIAGNÓSTICO EM `/diag`**.
  *Justificativa Técnica:* Adicionar chaves extras na string JSON de `/readData` do Hub consome o buffer estático global reservado (`HUB_TELEMETRY_JSON_RESERVE = 3072`). O nó de biomassa está próximo do limite de flash OTA (com apenas 5,3 kB a 20 kB de margem de segurança). Adicionar formatações adicionais de string no push consome precious flash memory.
  Os dados de saturações, RSSI, heap e estado já são consultados periodicamente pelo Hub via tarefa de segundo plano (`NodeDiagTask.h`) que acessa `GET /diag` a cada 30 segundos e expõe essas informações no comando serial `{"NodeDiag":"biomass"}`. Essa arquitetura preserva o payload leve de telemetria em tempo real e atende perfeitamente aos requisitos de engenharia.

#### 3. Classificação de Risco e Segurança
- **Prioridade:** N/A (Decisão de Engenharia Fechada).
- **Classificação de Segurança:** Preserva o headroom de memória flash e estabilidade dos buffers de rede.

---

### B15 — Ordem de Envio de Parâmetros de Aquisição (`gear -> it -> pwm`)

#### 1. Problema Declarado e Causa Raiz
Os comandos de configuração óptica `set_it` e `set_pwm` aplicam seus valores ao slot de IT e PWM **corrente**. Se o operador pretender modificar um slot específico sem antes selecioná-lo através de `set_gear`, a alteração afetará o slot incorreto.

#### 2. Decisão Técnica de Ação (JUSTIFICAR COMO RESOLVIDO NO APP)
- **Decisão:** **FECHADO COMO RESOLVIDO POR CONSTRUÇÃO NO APP**.
  *Justificativa:* O aplicativo supervisor já implementa a ordenação estrita necessária em `CommandBuilders.BiomassTuning`, emitindo sequencialmente:
  $$\text{gear} \longrightarrow \text{it} \longrightarrow \text{pwm} \longrightarrow \text{ema} \longrightarrow \text{probe\_period}$$
  Cada comando é emitido individualmente, aguardando a confirmação de execução pelo nó (`BiomassCommandPending == false`) antes de despachar o próximo.

#### 3. Classificação de Risco e Segurança
- **Prioridade:** N/A (Resolvido por Arquitetura no Supervisor).
- **Classificação de Segurança:** Garante atomicidade e previsibilidade de parametrização.

---

## 4. Matriz de Compatibilidade e Interoperabilidade

A tabela a seguir consolida as diretrizes de engenharia e decisões de arquitetura fechadas para o Sensor de Biomassa, padronizada conforme a Seção "🔵 Diretrizes de Segurança e Decisões de Arquitetura Fechadas" de `COMANDOS_DISPOSITIVOS_EXTERNOS.md`:

#### 🔵 Diretrizes de Segurança e Decisões de Arquitetura Fechadas (Dispositivo 4 — Sensor de Biomassa)
*Decisões de engenharia aprovadas que definem o comportamento seguro e metrológico do sistema.*

| Decisão / Recurso | Onde Opera | Comportamento e Justificativa Técnica | Estado |
|---|---|---|:---:|
| **Janela de presença proporcional a `probe_ms` (B01)** | Hub 10.2 (`AppContext.h`, `Telemetry.h`) | A presença `BiomassOnline` e a validade de amostra `validBiomass` utilizam `biomassPresenceWindowMs(probe_ms) = max(10000UL, (unsigned long)(probe_ms * 2.5f))`. Elimina a oscilação de presença e falsos alarmes durante aquisições normais (25 s a 60 s). | 🟢 Fechado (§4.10 B01) |
| **Isolamento e atomicidade de rotinas bloqueantes (B02)** | Firmware v11 / Hub 10.2 / App | Varreduras de branco e busca de marcha executam sob `delayServiced()` alimentando WDT. Comandos no app são serializados atrás de `BiomassCommandPending`. O comando `stop` aborta localmente por flag atômica `g_abortRequested`. Não se injeta cliente HTTP reentrante na varredura óptica para proteger memória e estabilidade do Core 1. | 🟢 Fechado (§4.10 B02) |
| **Roteamento de auto-range e marcha manual (B03)** | Hub 10.2 / App Windows | O Hub roteia a chave `biomassAutoRange` (`{"command":"auto"|"manual"}`). O aplicativo expõe seletor explícito de modo automático vs manual. Ao travar marcha manual, o auto-range é desativado no nó (`g_autoRange = false`), evitando substituição involuntária de marcha. | 🟢 Fechado (§4.10 B03) |
| **Corte térmico imediato do LED após `set_gear` (B04)** | Firmware v11 (`CommandCodec.h`) | `setManualGear()` força `pwmSetDutyPercent(0.0f)` ao ajustar nova marcha fora de IDLE (em `MEASURING`) e reprograma `g_nextReadTime`, impedindo que o LED fique aceso continuamente no intervalo entre pulsos e garantindo conformidade com o limite térmico de duty cycle $\le 8\%$. | 🟢 Fechado (§4.10 B04) |
| **Gravação NVS seletiva de limiares e período (B05)** | Firmware v11 (`CommandCodec.h`) | Alterações de `low`, `high`, `opt` e `probe_period` acionam `saveConfig()`. A persistência ocorre apenas mediante alteração efetiva dos valores em relação à RAM, preservando a vida útil da memória flash NOR contra desgastes por reenvio de quadros. | 🟢 Fechado (§4.10 B05) |
| **Supervisão contra reinício silencioso pós-queda (B06)** | Windows App (`AlarmService.cs`) | O sensor nasce em `IDLE` por segurança após queda de energia. O aplicativo monitora o estado ativo de aquisição e dispara alarme de processo caso o nó esteja online e habilitado mas sem receber amostras por $> 2{,}5 \times \text{probe_ms}$, prevenindo a interrupção oculta de monitoramento biológico. | 🟢 Fechado (§4.10 B06) |
| **Identidade unificada de release `v11` (B07)** | Firmware v11 / Docs / App | Constante `FW_VERSION "v11.0"` unificada no banner serial, `/nodeHello`, página web OTA (`LocalHttpApi.h`), `/diag` e documentação técnica. O catálogo `NodeFirmwareCatalog` do Windows App valida a versão oficial. | 🟢 Fechado (§4.10 B07) |
| **Saneamento do protocolo do aplicativo (B08)** | Windows App / Docs | `PROTOCOL.md` do aplicativo alinhado à versão ativa do Hub v10.2, confirmando tradução de `biomassEma` e `biomassProbePeriodMs` e semântica com ACK. | 🟢 Fechado (§4.10 B08) |
| **Temporização estendida da rotina de branco (B09)** | Windows App (`RecipeEngine.ExternalDevices.cs`) | Documentação de receitas e avisos de operação atualizados para o tempo real medido da varredura completa não cadenciada (20 a 40 s, típico 30 s), prevenindo envios prematuros de `start` enquanto o nó opera em `BLANKING`. | 🟢 Fechado (§4.10 B09) |
| **Invalidação estrita do branco por alteração óptica (B10)** | Firmware v11 / Nó | Reconfigurações de hardware (`set_it`, `set_pwm`, `pwm_preset`) alteram a resposta do fototransistor e invalidam a matriz de referência $I_0$, forçando retorno a `IDLE`. Decisão mantida por rigor metrológico. | 🔵 Decisão de projeto |
| **Isolamento de rede e comandos destrutivos (B11)** | Hub 10.2 / Nó | Comandos `hub_off` e `factory` não são expostos pelo Hub, permanecendo exclusivos de acesso físico USB serial e SoftAP local, prevenindo desativação remota acidental do canal de comunicação. | 🔵 Decisão de projeto |
| **Saneamento de comando vestigial `test_period` (B12)** | Hub 10.2 (`Commands.h`) | Remoção do repasse da chave `test_period` na ausência de chaves de teste contínuo (`test_on`/`test_off`), eliminando tráfego ocioso na caixa de correio confiável. | 🟢 Fechado (§4.10 B12) |
| **Tratamento discriminado de sentinelas de erro óptico (B13)** | Windows App (`BiomassControlViewModel`, `RecipeEngine`) | O aplicativo converte absorbâncias de erro ($-99{,}0$ e $9{,}9$) em estados textuais de alerta no supervisor (`"--- [Branco Inválido]"` e `"> 4.0 AU [Escuro/Bloqueado]"`). O motor de receitas rejeita $9{,}9$ como dado de partida válido. | 🟢 Fechado (§4.10 B13) |
| **Preservação do payload da query string GET (B14)** | Firmware v11 / Hub 10.2 | Metadados diagnósticos secundários (`hd_mode`, `sat`, `boot_id`) permanecem na rota serial/AP local. O push para o Hub preserva o tamanho de query string $< 320$ bytes para garantir estabilidade de buffers e headroom de flash OTA. | 🔵 Decisão de projeto |
| **Sequenciamento atômico de sintonia de marcha (B15)** | Windows App (`CommandBuilders.cs`) | O construtor `BiomassTuning` emite obrigatoriamente a seleção de marcha linear (`biomassGear`) antes das reconfigurações de slot (`biomassIt`, `biomassPwm`), garantindo que o slot pretendido seja o alvo da escrita. | 🟢 Fechado (§4.10 B15) |

---

## 5. Roteiro de Implementação em Fases

Para atender rigorosamente aos requisitos do projeto e ao isolamento por componentes (Requisito R4), a execução das modificações será distribuída em fases sequenciais, cada qual consolidada em seu respectivo commit git atômico:

```
┌──────────────────────────────────────────────────────────────────────────┐
│ FASE 1: FIRMWARE DO NÓ SENSOR DE BIOMASSA                                │
│ Commit: "firmware(biomass): fix thermal bug B04, persist B05, id v11.0"  │
│ - Correção crítica do LED em setManualGear() (B04)                       │
│ - Gravação coalescida na NVS para limiares e período (B05)               │
│ - Sincronização de poll no delayServiced() com abort por stop (B02)      │
│ - Respeito à marcha manual no start quando !g_autoRange (B03)            │
│ - Padronização da versão v11.0 no código e na página Web OTA (B07)       │
└────────────────────────────────────┬─────────────────────────────────────┘
                                     │
                                     ▼
┌──────────────────────────────────────────────────────────────────────────┐
│ FASE 2: GATEWAY CENTRAL OPEN-TEC HUB                                     │
│ Commit: "hub(biomass): dynamic presence window B01, auto-range B03, B12" │
│ - Implementação de biomassPresenceWindowMs(probe_ms) em AppContext (B01) │
│ - Aplicação da janela elástica em Telemetry.h e HttpServer.h (B01)       │
│ - Roteamento da chave biomassAutoRange ("auto"/"manual") (B03)           │
│ - Remoção da chave vestigial test_period em Commands.h (B12)             │
│ - Atualização da suíte de testes de contratos Python (test_contracts)   │
└────────────────────────────────────┬─────────────────────────────────────┘
                                     │
                                     ▼
┌──────────────────────────────────────────────────────────────────────────┐
│ FASE 3: APLICATIVO SUPERVISOR WINDOWS (OpenTECHub)                       │
│ Commit: "app(biomass): handle sentinels B13, auto-range UI B03, B06/B09" │
│ - Definição de constantes de sentinela em SensorReadings.cs (B13)        │
│ - Formatação textual diferenciada de sentinelas na UI (B13)              │
│ - Inclusão de CommandKeys e builder BiomassAutoRange (B03)               │
│ - Temporização e guarda de 60 s em RecipeEngine.ExternalDevices (B09)    │
│ - Alarme de interrupção silenciosa de aquisição no AlarmService (B06)    │
│ - Validação da suíte de testes dotnet test Biomass                       │
└────────────────────────────────────┬─────────────────────────────────────┘
                                     │
                                     ▼
┌──────────────────────────────────────────────────────────────────────────┐
│ FASE 4: DOCUMENTAÇÃO TÉCNICA E AUDITORIA DE FECHAMENTO                   │
│ Commit: "docs(biomass): update COMANDOS_DISPOSITIVOS and protocol improvements"│
│ - Substituição da tabela vermelha por tabela azul fechada em §4.0         │
│ - Atualização do checklist de ensaios de bancada física em §4.11          │
│ - Atualização dos registros em HUB_PROTOCOL_IMPROVEMENTS.md              │
│ - Execução do script automatizado verify_plan_biomassa.py                │
└──────────────────────────────────────────────────────────────────────────┘
```

---

## 6. Critérios de Aceitação e Plano de Testes

### 6.1 Critérios de Aceitação de Software e Contratos
Para considerar as implementações finalizadas e aprovadas para homologação, os seguintes critérios devem ser estritamente atendidos:
1. **Verificador Automatizado do Plano:** O script `verify_plan_biomassa.py` deve ser executado no ambiente de integração e retornar código de saída `Exit Code 0`, confirmando a presença integral das 6 seções canônicas e a cobertura de todos os itens B01 a B15.
2. **Suíte de Testes do Hub Central:** A suíte de testes de contratos do Hub (`python -m unittest discover -s ESP32S3-HUB/tests/contracts/`) deve executar com 100% de aprovação (mínimo de 83 testes passando sem advertências).
3. **Suíte de Testes do Aplicativo Windows:** A suíte de testes unitários do OpenTECHub (`dotnet test Windows_app/tests/OpenTECHub.Tests --filter "FullyQualifiedName~Biomass"`) deve passar com 100% de sucesso (mínimo de 60 testes passando).
4. **Verificação Estática do Firmware:** O script de checagem estática `python External-Devices/sensor-biomassa/apps/desktop-python/pc_client/tests/check_firmware.py` deve confirmar a integridade de parênteses, blocos de código e protótipos de função.

### 6.2 Protocolo de Homologação em Bancada Física (§4.11)
A homologação final do hardware e firmware na bancada de automação TECNAL obedecerá ao seguinte roteiro experimental:

| Item | Procedimento / Ensaio de Bancada | Condições de Teste | Critério de Aceitação (Pass/Fail) |
|:---:|---|---|---|
| **1** | **Comunicação I²C e Escala do VEML7700** | Cubeta vazia, LED desligado e ligado a 100%, IT = 800 ms. | Comunicação em `0x10`, leitura de canal ALS de 16 bits em `0x04` (não `0x05`); saturação detectada estritamente em $\ge 65.530$ contagens. |
| **2** | **Conformidade Térmica do LED e Bug B04** | Medir com osciloscópio o pino GPIO 18 durante `set_gear` em `MEASURING`. | O LED deve cortar a emissão imediatamente ($0{,}0\%$ de duty) após a troca de marcha; duty cycle médio $\le 8{,}0\%$; deriva fotométrica $< 1\%$ após 60 s de repouso. |
| **3** | **Duração Real da Varredura de Branco (B09)** | Disparar `blank` pelo Hub com água destilada. Monitorar serial e `/api/status`. | Registro de `sweep_ms` entre 20 s e 40 s; confirmação de que o nó não cai para offline no Hub durante o procedimento. |
| **4** | **Janela Dinâmica de Presença em Medição (B01)** | `probe_ms = 25.000` ms em `MEASURING`. Monitorar `/readData` do Hub por 10 minutos. | `BiomassOnline` deve permanecer ininterruptamente `true`; as leituras `BiomassAbs` não podem sumir nem zerar entre amostras; alarme offline inativo. |
| **5** | **Fluxo Ponta a Ponta com Caixa de Correio Confiável** | Habilitar $\rightarrow$ Branco $\rightarrow$ Iniciar $\rightarrow$ 10 Amostras $\rightarrow$ Parar $\rightarrow$ Desabilitar. | Todos os comandos devem exibir `ack_cmd_id` sequencial; `BiomassCommandPending` deve limpar após cada etapa; ausência de comandos rejeitados. |
| **6** | **Parametrização Manual e Intertravamento Auto-Range (B03)** | Enviar `biomassAutoRange: 0` e marcha fixa via aplicativo. Disparar `start`. | O nó inicia na marcha manual configurada sem forçar Smart Start; se a turbidez mudar, a marcha permanece travada. |
| **7** | **Persistência de Limiares e Recuperação Pós-Queda (B05/B06)** | Alterar limiares para `low=12000, high=35000, probe_period=15000`. Cortar alimentação do nó durante a medição. | Ao religar, conferir via `/diag` que limiares e período foram preservados na NVS; o sensor nasce em `IDLE`; o aplicativo Windows dispara o alarme de medição interrompida após 40 s. |
| **8** | **Tratamento de Sentinelas Ópticas na Interface (B13)** | Inserir lâmina opaca bloqueando o feixe óptico ($I = 0$). | O aplicativo Windows deve exibir `"> 4.0 AU [Escuro/Bloqueado]"` sem travar o sinótico e sem corromper o histórico de gráficos. |
| **9** | **Resiliência a Falha de Barramento I²C** | Desconectar momentaneamente o pino SDA (GPIO 8) durante a aquisição. | O firmware incrementa contador de falhas em `/api/status`, tenta recuperação de barramento por 5 vezes e, persistindo, transiciona com segurança para `IDLE`. |
| **10** | **Atualização de Firmware OTA** | Upload de novo binário via `POST /update`. | LED desligado obrigatoriamente durante a regravação; watchdog de 90 s ativo; reboot autônomo com preservação da matriz de branco na época correta. |
