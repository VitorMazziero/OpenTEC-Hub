# Relatório Técnico de Auditoria: Firmware do Sensor de Biomassa e Inconsistências de Seção 4.10 (B01 a B13)

**Documento:** `survey_firmware.md`  
**Autor:** Explorer 1 (Biomass Sensor Firmware Specialist)  
**Alvo:** `External-Devices/sensor-biomassa/` e subsistemas integrados (`ESP32S3-HUB/`, `Windows_app/`)  
**Referência Normativa:** `External-Devices/docs/COMANDOS_DISPOSITIVOS_EXTERNOS.md` (§4 e especificamente §4.10)  
**Data:** 13 de Setembro de 2026  
**Status:** Concluído / Pronto para Elaboração do Plano de Implementação (M2)

---

## 1. Sumário Executivo e Escopo da Investigação

Esta auditoria técnica exaustiva investigou o subsistema de medição óptica do **Sensor de Biomassa** (turbidímetro/fotômetro baseado em ESP32-S3, sensor digital de luz ambiente VEML7700 com barramento I²C a 100 kHz e LED emissor regulado via PWM a 2 kHz em GPIO 18).

O objetivo primário consistiu em auditar todas as inconsistências, lacunas e decisões catalogadas na **Seção 4.10 (B01 a B13, estendido a B14 e B15)** do manual de integração `COMANDOS_DISPOSITIVOS_EXTERNOS.md`, confrontando diretamente:
1. O código-fonte modular ativo do firmware (`External-Devices/sensor-biomassa/firmware/biomass-sensor/`).
2. O contrato de comunicação de fio e documentos arquiteturais (`PROTOCOL.md`, `ARCHITECTURE.md`, `HARDWARE.md`, `PONTOS_DE_MELHORIA_EXPOSICAO_NOS.md`).
3. O código de roteamento, caixas postais (`biomassBox`) e telemetria do Hub (`ESP32S3-HUB/src/protocol/Commands.h`, `HttpServer.h`, `Telemetry.h`, `AppContext.h`).
4. O comportamento dos ViewModels e construtores de comandos do aplicativo Windows (`Windows_app/src/OpenTECHub/`).

### Principais Conclusões da Auditoria:
- **Estado de Hardware / Firmware:** O firmware do nó é maduro e implementa uma malha óptica sofisticada com detecção de fronteira de conversão (`waitForConversionBoundary`), filtro mediano de 5 pontos, filtro passa-baixas exponencial (EMA) e proteção térmica rígida do LED ($8\%$ de *duty cycle* médio).
- **Problema de Presença e Oscilação (B01):** O sensor adota período de amostragem padrão de 25 s (piso térmico de 24,3 s para integração de 800 ms), enquanto o Hub impõe timeout rígido de 10 s (`BIOMASS_TIMEOUT`). No firmware, o envio de heartbeat a cada 5 s é ativamente desativado durante a medição (`g_state != MEASURING`), fazendo com que o nó seja declarado offline e as leituras zeradas pelo app a cada ciclo de 25 segundos.
- **Bug Térmico Crítico no Hardware (B04):** Ao receber o comando `set_gear` no estado `MEASURING`, a função `setManualGear()` configura a potência do PWM e liga o LED via `pwmSetLevel()`, porém só o desliga se o nó estiver em `IDLE`. Durante `MEASURING`, o LED fica aceso continuamente com 100% de intensidade até a próxima leitura periódica (até 25 segundos), violando gravemente o limite térmico do LED e saturando a linha de base óptica.
- **Perda de Persistência em Reboot (B05):** Parâmetros essenciais como limiares de auto-range (`low`, `high`, `opt`) e período de amostragem (`probe_period`, `refresh_ms`) gravam apenas na struct `g_config` em RAM e omitem a chamada `saveConfig()`, perdendo os valores configurados caso o nó reinicie.
- **Isolamento em Rotinas Bloqueantes (B02):** Durante a varredura da matriz de calibração de branco (`runBlankingRoutine`, 20 a 40 s) ou busca de marchas (`findAndSetOptimalGear`), a rotina de espera `delayServiced()` atende apenas o servidor Web local (AP) e a serial USB, não executando polling de comandos do Hub nem despacho de dados. Comandos remotos (como parada de emergência `stop`) são ignorados até o fim da rotina.
- **Identidade Fragmentada (B07):** A versão de protocolo foi elevada para `v11` em todo o ecossistema, mas a página HTML de atualização OTA em `LocalHttpApi.h:80` e os documentos locais do sensor continuam rotulados como `v5.3`.

---

## 2. Topologia do Firmware e Organização dos Módulos

O código-fonte do firmware está localizado em `External-Devices/sensor-biomassa/firmware/biomass-sensor/` e adota compilação unificada C++ (*unity build* / agregação via cabeçalhos) a partir de `FirmwareApp.cpp`:

```
External-Devices/sensor-biomassa/firmware/biomass-sensor/
├── biomass-sensor.ino                  # Entry point Arduino (setup/loop delegam a FirmwareApp)
├── web_ui.h                            # Single-Page UI completa em PROGMEM (HTML/CSS/JS)
├── build/esp32.esp32.esp32s3/          # Artefatos binários exportados pelo Arduino IDE 2.x
│   ├── biomass-sensor.ino.bin          # Binário compilado (1.129.872 bytes)
│   ├── partitions.csv                  # Tabela de partições (app0/app1 de 0x140000 = 1.310.720 B)
│   └── build.options.json              # FQBN: esp32:esp32:esp32s3, core 3.3.11, flags -Os
└── src/
    ├── core/
    │   ├── FirmwareApp.h / .cpp        # Variáveis globais, definições de pinos e macros
    │   ├── Lifecycle.h                 # setup(), loop(), máquina de estados e laço de rede
    │   └── ServiceRuntime.h            # serviceNetwork() e delayServiced() com reset de WDT
    ├── protocol/
    │   ├── CommandCodec.h              # Parser JSON manual (zero-allocation) e despacho de comandos
    │   └── TelemetryAndHub.h           # Formatação JSON de telemetria, push HTTP e poll de comandos
    ├── measurement/
    │   ├── BlankingAndRange.h          # Varredura de branco (4x8), piso térmico, auto-range
    │   └── MeasurementPipeline.h       # Disparo pulsado, filtros mediano e EMA, sentinelas, publish
    ├── sensor/
    │   └── Veml7700Driver.h            # Driver I2C VEML7700, LEDC PWM, detecção de fronteira
    ├── storage/
    │   ├── Stores.h                    # Persistência NVS (namespace biomass_sensor, Preferences)
    │   └── Crc.h                       # Soma de verificação para integridade de structs
    ├── filtering/
    │   └── SampleFilter.h              # Filtro mediano de 5 amostras com priming imediato
    ├── history/
    │   └── SampleHistory.h             # Ring buffer de 1024 amostras em SRAM (20 KB)
    └── api/
        └── LocalHttpApi.h              # Endpoints HTTP locais (/api/*, /diag, /readData, /update OTA)
```

### Orçamento de Memória e Headroom de Flash (Métrica de Segurança)
- **Tamanho da Partição de Aplicação (`app0` / `app1`):** `0x140000` = $1.310.720\text{ bytes}$ ($1.280\text{ kB}$).
- **Tamanho Atual do Binário (`biomass-sensor.ino.bin`):** $1.129.872\text{ bytes}$ ($86,2\%$ da partição).
- **Espaço Livre na Partição:** $180.848\text{ bytes}$ ($176,6\text{ kB}$).
- **Piso de Segurança para Atualização OTA Segura:** Estabelecido em $160.000\text{ bytes}$ de folga.
- **Margem Livre Acima do Piso:** $\approx 20,8\text{ kB}$ (ou $\approx 5,3\text{ kB}$ considerando compilações com flags completas de debug/telemetria conforme medido em `PONTOS_DE_MELHORIA_EXPOSICAO_NOS.md:144`).
- **Diretriz Mandatória:** Qualquer modificação de código no firmware deve ser extremamente enxuta para não inflar o segmento `.text` além do piso de segurança OTA. Não devem ser incluídas bibliotecas pesadas (ex.: ArduinoJson não é utilizado; o parser manual em `CommandCodec.h` deve ser preservado).

---

## 3. Mecanismos de Build, Testes e Ferramentas Disponíveis

Foi realizada a auditoria das ferramentas de construção e das suítes de testes automatizados disponíveis no projeto:

### 3.1 Verificação do Ambiente de Compilação
- **PlatformIO:** Não há arquivos `platformio.ini` no repositório. O ecossistema de dispositivos externos utiliza primariamente **Arduino CLI / Arduino IDE 2.x** com o pacote de suporte de hardware ESP32 oficial da Espressif (`esp32:esp32:esp32s3`, versão do core `3.3.11`).
- **Cadeia de Ferramentas Instalada:** O compilador oficial Xtensa GCC (`xtensa-esp32s3-elf-g++.exe`) está devidamente instalado em `C:\Users\vitor\AppData\Local\Arduino15\packages\esp32\tools\esp-x32\2601\bin\`.
- **Configuração de Partições:** Definida em `partitions.csv` (modo SPIFFS com 1,44 MB livre, OTA de 1,28 MB).

### 3.2 Suítes de Testes Existentes e Validadas

| Componente / Suíte | Ferramenta / Comando | Qtd. Testes | Resultado | Cobertura / Finalidade |
|---|---|:---:|:---:|---|
| **Hub Central (Contratos)** | `python -m unittest discover -s ESP32S3-HUB/tests/contracts/` | 83 | 🟢 83/83 PASS (0,018s) | Validação de decodificação de comandos, integridade de frames JSON, descarte de comandos múltiplos na biomassa (`test_individual_biomass_commands`), timeouts e ecos. |
| **App Windows (Biomassa)** | `dotnet test Windows_app/tests/OpenTECHub.Tests --filter "FullyQualifiedName~Biomass"` | 60 | 🟢 60/60 PASS (0,446s) | Validação de ViewModels, serialização de comandos de sintonia de biomassa (`BiomassTuning`), tratamento de telemetria, parsing de absorbância e descarte de comandos inválidos. |
| **App Python / Firmware Client** | `python External-Devices/sensor-biomassa/apps/desktop-python/pc_client/tests/check_firmware.py` | 18 | 🟢 Validação Estrutural | Varre estaticamente os arquivos `.ino`, `.cpp` e `.h` da biomassa: verifica balanceamento de chaves/parênteses, correspondência entre protótipos e definições, travas de reentrância do WDT e guards de HTTP. |
| **Buffer Circular / RingBuffer** | `test_ringbuffer.py` | 12 | 🟢 12/12 PASS | Testa paginação de histórico (/api/history), limite de 60 registros por chamada e integridade de descarte do ring buffer de 1024 amostras. |
| **Serial Framing** | `test_serial.py` | 10 | 🟢 10/10 PASS | Testa ingestão de linhas seriais, validação de JSON, roteamento de matriz de branco e telemetria. |

---

## 4. Análise Exaustiva Item a Item: B01 a B13 (mais B14 e B15)

---

### B01 — Janela de Presença e Cadência em MEASURING

#### 1. Identificação e Problema Declarado
- **Item:** B01 (§4.10).
- **Problema:** Em `MEASURING`, o nó só envia telemetria (`/biomassData`) ao publicar uma amostra fresca (a cada `probe_ms`, padrão 25 000 ms; piso térmico de 24 325 ms para IT 800 ms). O Hub central possui timeout fixo de presença de 10 000 ms (`BIOMASS_TIMEOUT`). Como 25 s > 10 s, o Hub declara o nó offline após 10 segundos de cada amostra, fazendo `BiomassOnline` oscilar, limpando os campos de absorbância no aplicativo e disparando falsos alarmes de *Absorbância offline*.

#### 2. Localização Exata no Código
- **Firmware:** `External-Devices/sensor-biomassa/firmware/biomass-sensor/src/core/Lifecycle.h:179–186`:
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
- **Firmware:** `src/core/FirmwareApp.cpp:45`: `constexpr uint32_t DEFAULT_REFRESH_MS = 25000;`.
- **Firmware:** `src/measurement/BlankingAndRange.h:112`: Piso térmico calculado: `(ledOnMsFor(itMs) / LED_DUTY_LIMIT) = 24.325 ms`.
- **Hub:** `ESP32S3-HUB/ESP32S3-HUB/src/core/AppContext.h:371`: `const unsigned long BIOMASS_TIMEOUT = 10000;`.
- **Hub:** `ESP32S3-HUB/ESP32S3-HUB/src/sensor/Telemetry.h:171–177`:
  ```cpp
  bool biomassOnline = snapBiomassUpdate > 0 && (millis() - snapBiomassUpdate <= BIOMASS_TIMEOUT);
  bool validBiomass = false;
  if (snapBiomassComm && snapBiomassSampleUpdate > 0) {
      unsigned long age = millis() - snapBiomassSampleUpdate;
      if (age <= BIOMASS_TIMEOUT) validBiomass = true;
  }
  ```

#### 3. Causa Raiz Técnica
A condição `g_state != MEASURING` em `Lifecycle.h:179` foi intencionalmente adicionada no firmware para que o heartbeat de repouso não concorresse com a amostragem óptica. Todavia, a amostragem periódica demora mais do que o dobro do tempo de guarda do Hub. O Hub avalia `BIOMASS_TIMEOUT = 10000` fixo, tanto para conectividade do nó quanto para o freshness da amostra de absorbância.

#### 4. Decisão Proposta: FIX (Solução Combinada Hub + Firmware)
- **Resolução Principal (Hub):** Tornar a janela de presença dinâmica com base no período de amostragem ecoado pelo nó (`biomassProbePeriodMs`), idêntico ao tratamento já aprovado para o Sensor de Distância (`distancePresenceWindowMs`):
  $$\text{biomassPresenceWindowMs} = \max(10000\text{ ms}, 2 \times \text{biomassProbePeriodMs} + 5000\text{ ms})$$
  Tanto `biomassOnline` quanto `validBiomass` utilizam essa janela flexível.
- **Resolução Complementar (Firmware):** Permitir que o nó envie heartbeats de presença (`idle=1`) durante `MEASURING` quando estiver aguardando o próximo pulso (`g_nextReadTime - now > 5000`), sem acionar o LED. O Hub já trata `idle=1` renovando `biomassLastUpdate` sem atualizar `biomassSampleLastUpdate`, preservando a semântica de integridade.

#### 5. Especificação Técnica das Modificações
- **No Hub (`ESP32S3-HUB/src/core/AppContext.h` e `Telemetry.h`):**
  Definir função inline:
  ```cpp
  inline unsigned long biomassPresenceWindowMs(uint32_t probePeriodMs) {
    if (probePeriodMs == 0) return BIOMASS_TIMEOUT;
    return max(BIOMASS_TIMEOUT, (unsigned long)(probePeriodMs * 2 + 5000));
  }
  ```
  Em `Telemetry.h`, substituir `BIOMASS_TIMEOUT` por `biomassPresenceWindowMs(biomassProbePeriodMs)`.
- **No Firmware (`src/core/Lifecycle.h`):**
  Remover a restrição rígida `g_state != MEASURING` e enviar heartbeat se o LED estiver inativo há mais de 5 s:
  ```cpp
  const bool measuringIdle = (g_state == MEASURING && now < g_nextReadTime && g_targetPct == 0.0f);
  if (g_hubEnabled && (g_state != MEASURING || measuringIdle) &&
      now - lastHubHeartbeatMs >= heartbeatInterval) { ... }
  ```

---

### B02 — Rotinas Bloqueantes Isolam o Nó do Hub

#### 1. Identificação e Problema Declarado
- **Item:** B02 (§4.10).
- **Problema:** As rotinas de varredura de branco (`runBlankingRoutine`, 20–40 s), busca de marchas de auto-range (`findAndSetOptimalGear`, 3–10 s) e teste de conversão (`probeConversionPeriod`) executam em laços bloqueantes chamando `delayServiced()`. Essa função atende apenas o servidor Web local (`server.handleClient()`) e a serial, omitindo polling HTTP (`pollHubForCommands()`) e push (`sendDataToHub()`). O Hub marca o nó como offline e comandos de cancelamento (`{"stop":1}`) emitidos pelo Hub são ignorados até a conclusão da rotina.

#### 2. Localização Exata no Código
- **Firmware:** `External-Devices/sensor-biomassa/firmware/biomass-sensor/src/core/ServiceRuntime.h:1–15`:
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
- **Firmware:** `src/measurement/BlankingAndRange.h:13–99` (`runBlankingRoutine`), `217–319` (`findAndSetOptimalGear`).

#### 3. Causa Raiz Técnica
A arquitetura do nó foi desenvolvida para evitar reentrância no cliente HTTP (`HTTPClient http`). Chamar `pollHubForCommands()` no meio de uma leitura óptica poderia introduzir atrasos indeterminados no barramento I²C ou estourar a pilha do laço. Por isso, a rede do Hub foi completamente excluída de `serviceNetwork()`.

#### 4. Decisão Proposta: FIX (Serviço Controlado no delayServiced + Abort Remoto)
- **Decisão:** Corrigir de forma segura no Firmware e proteger no Hub/App.
- **No Firmware:** Em `ServiceRuntime.h`, durante pausas longas (`delayServiced`), verificar a cadência de polling (`now - lastHubPollMs >= HUB_POLL_PERIOD_MS`). Se expirado, invocar `pollHubForCommands()`. Se o comando recebido for `stop`, a flag `g_abortRequested` já é setada para `true` (`CommandCodec.h:139`), permitindo que a rotina aborte imediatamente o laço e restaure o estado `IDLE` de forma limpa.
- **No Hub / App:** A janela estendida de B01 impede que o nó caia para offline durante os intervalos entre amostras. No app, a interface mantém o indicador de comando pendente até a conclusão da rotina.

#### 5. Especificação Técnica das Modificações
- **No Firmware (`src/core/ServiceRuntime.h`):**
  Adicionar a chamada condicional para atendimento do Hub dentro de `delayServiced()` quando não estiver dentro de um handler HTTP:
  ```cpp
  void serviceHubNetwork() {
    if (!g_hubEnabled || WiFi.status() != WL_CONNECTED || g_inHttpHandler) return;
    const unsigned long now = millis();
    if (now - lastHubPollMs >= HUB_POLL_PERIOD_MS) {
      lastHubPollMs = now;
      pollHubForCommands();
    }
  }
  ```
  Invocar `serviceHubNetwork()` dentro do laço `while (millis() - start < ms)` em `delayServiced()`.

---

### B03 — Marcha Manual Pelo Hub e Intertravamento com Auto-Range

#### 1. Identificação e Problema Declarado
- **Item:** B03 (§4.10).
- **Problema:** O operador define uma marcha manual pelo Hub via `biomassGear` (`set_gear`), mas ao disparar `start`, o firmware executa incondicionalmente o *Smart Start*, recalculando a melhor marcha óptica e sobrescrevendo a escolha do operador. Além disso, se o auto-range estiver ativado (padrão), qualquer leitura fora da faixa linear dispara a busca automática de marchas (`SEARCHING`). Os comandos `auto` e `manual` existem no firmware mas não possuem chaves mapeadas no Hub.

#### 2. Localização Exata no Código
- **Firmware:** `External-Devices/sensor-biomassa/firmware/biomass-sensor/src/protocol/CommandCodec.h:56–71` (`setAutoRange`), `214–230` (`cmd.equals("start")`):
  ```cpp
  } else if (cmd.equals("start")) {
    if (!g_blankIsDone)
      Serial.println("Error: Please run 'blank' first.");
    else if (g_state == IDLE) {
      Serial.println("--- Starting Measurement ---");

      int startIt, startPwm;
      findOptimalBlankGear(startIt, startPwm); // Smart Start incondicional!

      g_state        = MEASURING;
      g_nextReadTime = millis();
      vemlSetConfig(startIt);
      pwmSetLevel(startPwm);
      ...
  ```
- **Firmware:** `src/protocol/CommandCodec.h:237–240` (`auto` / `manual`).
- **Hub:** `ESP32S3-HUB/ESP32S3-HUB/src/protocol/Commands.h:466–472` (`bioNewCmds` não inclui `auto` nem `manual`).

#### 3. Causa Raiz Técnica
1. O *Smart Start* em `CommandCodec.h:221` não verifica se o auto-range está desabilitado (`g_autoRange == false`) ou se o operador selecionou uma marcha válida manualmente antes do `start`.
2. O Hub não expõe uma chave para comutar `auto` e `manual`.

#### 4. Decisão Proposta: FIX (Roteamento de `biomassAutoRange` + Respeito à Marcha Manual)
- **Decisão:** Implementar a integração completa Hub ↔ Nó ↔ App.
- **No Hub (`Commands.h`):** Mapear a chave JSON `"biomassAutoRange"`:
  - Se `"biomassAutoRange": 1` ou `true` $\rightarrow$ despacha `{"command":"auto"}` para a `biomassBox`.
  - Se `"biomassAutoRange": 0` ou `false` $\rightarrow$ despacha `{"command":"manual"}` para a `biomassBox`.
- **No Firmware (`CommandCodec.h`):** No bloco `start`, verificar `g_autoRange`: se `!g_autoRange` e a marcha corrente tiver um branco válido (`blankIsValid(g_currentItIndex, g_currentPwmIndex)`), manter a marcha manual selecionada em vez de forçar o recálculo do Smart Start.
- **No App:** Expor interruptor *Auto-Range* na tela de controle de biomassa enviando `biomassAutoRange`.

#### 5. Especificação Técnica das Modificações
- **No Hub (`Commands.h`):**
  Adicionar em `bioNewCmds` ou parser dedicado:
  ```cpp
  if (json.indexOf("\"biomassAutoRange\"") != -1) {
    String autoVal = getValueFromJson(json, "biomassAutoRange");
    if (autoVal == "1" || autoVal == "true") {
      biomassCommand = "\"command\":\"auto\"";
      biomassCmdFound = true;
    } else if (autoVal == "0" || autoVal == "false") {
      biomassCommand = "\"command\":\"manual\"";
      biomassCmdFound = true;
    }
  }
  ```
- **No Firmware (`CommandCodec.h:220–228`):**
  ```cpp
  int startIt = g_currentItIndex;
  int startPwm = g_currentPwmIndex;
  if (g_autoRange || !blankIsValid(startIt, startPwm)) {
    findOptimalBlankGear(startIt, startPwm); // Smart Start só se auto ou se manual for inválido
  }
  ```

---

### B04 — LED Aceso Após `set_gear` em `MEASURING` (Risco Térmico)

#### 1. Identificação e Problema Declarado
- **Item:** B04 (§4.10).
- **Problema:** Ao receber `set_gear` durante o estado `MEASURING`, o firmware aciona o LED na potência da nova marcha através de `pwmSetLevel()`. Contudo, a rotina só desliga o LED se o nó estiver em `IDLE`. Como resultado, durante `MEASURING` o LED permanece aceso continuamente em potência plena até o próximo pulso periódico (que pode demorar até 25 s), causando aquecimento excessivo, violação do limite térmico de $8\%$ e degradação da linha de base de escuridão.

#### 2. Localização Exata no Código
- **Firmware:** `External-Devices/sensor-biomassa/firmware/biomass-sensor/src/protocol/CommandCodec.h:73–87`:
  ```cpp
  void setManualGear(int itIndex, int pwmIndex) {
    if (itIndex < 0 || itIndex >= g_config.IT_COUNT ||
        pwmIndex < 0 || pwmIndex >= g_config.PWM_COUNT) {
      Serial.println("Error: gear index out of range.");
      return;
    }
    vemlSetConfig(itIndex);
    pwmSetLevel(pwmIndex);
    if (g_state == IDLE) pwmSetDutyPercent(g_manualLedOn ? g_manualLedPct : 0.0f);
    Serial.print("Manual gear set: IT ");
    Serial.print(g_config.itDelays[itIndex]);
    Serial.print("ms, PWM ");
    Serial.print(g_config.pwmSettings[pwmIndex]);
    Serial.println("%");
  }
  ```
- **Firmware:** `src/sensor/Veml7700Driver.h:246–252`:
  ```cpp
  void pwmSetLevel(int pwmIndex) {
    if (pwmIndex < 0 || pwmIndex >= g_config.PWM_COUNT) return;
    g_currentPwmIndex = pwmIndex;
    pwmSetDutyPercent(g_config.pwmSettings[pwmIndex]); // Liga o LED imediatamente!
  }
  ```

#### 3. Causa Raiz Técnica
A função auxiliar `pwmSetLevel()` armazena o índice `g_currentPwmIndex` e imediatamente aciona a saída PWM com `g_config.pwmSettings[pwmIndex]`. Em `setManualGear()`, o desenvolvedor colocou o corte do LED subordinado a `if (g_state == IDLE)`. Logo, se `g_state == MEASURING`, o corte nunca é executado e o LED fica travado em estado emissivo.

#### 4. Decisão Proposta: FIX MANDATÓRIO (Correção Crítica no Firmware)
- **Decisão:** Corrigir imediatamente no firmware. É uma falha crítica de integridade de hardware com impacto térmico direto sobre o emissor óptico.
- **Ação:** Em `setManualGear()`, desligar incondicionalmente o LED (exceto se em IDLE com o modo manual de bancada `g_manualLedOn == true`), e se estiver em `MEASURING`, reprogramar `g_nextReadTime` para garantir o repouso escuro exigido pela física do sensor antes do próximo pulso.

#### 5. Especificação Técnica da Modificação
- **No Firmware (`src/protocol/CommandCodec.h:81`):**
  Substituir as linhas 80–82 por:
  ```cpp
  vemlSetConfig(itIndex);
  g_currentPwmIndex = pwmIndex;
  // Garante que o LED permaneça apagado, respeitando acendimento manual apenas em IDLE:
  pwmSetDutyPercent((g_state == IDLE && g_manualLedOn) ? g_manualLedPct : 0.0f);
  if (g_state == MEASURING) {
    // Reprograma o próximo pulso garantindo tempo escuro para estabilização óptica
    g_nextReadTime = millis() + g_config.itRefreshTimes[g_currentItIndex];
  }
  ```

---

### B05 — Persistência Parcial de Limiares e Período

#### 1. Identificação e Problema Declarado
- **Item:** B05 (§4.10).
- **Problema:** Os comandos de limiares de auto-range (`low`, `high`, `opt`) e os comandos de período de amostragem (`probe_period`, `refresh_ms`, `probe_ms`) modificam as variáveis correspondentes na struct em RAM `g_config`, mas **não chamam `saveConfig()`**. O comando direto `save_config` não é roteado pelo Hub. Caso o nó reinicie, esses parâmetros são perdidos e voltam aos padrões da NVS.

#### 2. Localização Exata no Código
- **Firmware:** `External-Devices/sensor-biomassa/firmware/biomass-sensor/src/protocol/CommandCodec.h:260–279`:
  ```cpp
  } else if (cmd.equals("probe_period")) {
    long periodVal = getJsonValue(json, "value");
    if (periodVal != -999999 && periodVal > 0) {
      ...
      for (int i = 0; i < g_config.IT_COUNT; i++) {
        g_config.itRefreshTimes[i] = (uint32_t)periodVal;
      }
      Serial.print("Sampling interval set to ");
      Serial.print(periodVal);
      Serial.println(" ms");
      // Falta saveConfig() aqui!
    }
  ```
- **Firmware:** `src/protocol/CommandCodec.h:403–453` (`low`, `high`, `opt`, `refresh_ms`, `probe_ms` alteram campos de `g_config` sem persistir).

#### 3. Causa Raiz Técnica
A struct `DeviceConfig g_config` possui um campo `crc32` calculado no momento da gravação. Apenas comandos que realizavam alterações estruturais (como `set_it`, `set_pwm`, `pwm_preset` e `factory`) invocavam `saveConfig()`. Os desenvolvedores omitiram a chamada nas propriedades numéricas pontuais para poupar ciclos de flash, mas deixaram esses parâmetros vulneráveis a reboots.

#### 4. Decisão Proposta: FIX (Persistência Coalescida no Firmware)
- **Decisão:** Corrigir no firmware.
- **Ação:**
  1. No bloco de `probe_period` com `value > 0`, chamar `saveConfig()` após atualizar os tempos de integração na struct.
  2. No bloco de propriedades numéricas (`low`, `high`, `opt`, `refresh_ms` em `CommandCodec.h:402–454`), utilizar uma flag local `bool configChanged = false`. Se qualquer parâmetro for modificado em relação ao valor corrente, setar `configChanged = true`. Ao término do processamento, se `configChanged == true`, invocar `saveConfig()`. Essa abordagem assegura que se `low`, `high` e `opt` chegarem agrupados no mesmo JSON, a NVS será gravada exatamente uma vez.

#### 5. Especificação Técnica da Modificação
- **No Firmware (`src/protocol/CommandCodec.h`):**
  Em `probe_period`:
  ```cpp
  for (int i = 0; i < g_config.IT_COUNT; i++) {
    g_config.itRefreshTimes[i] = (uint32_t)periodVal;
  }
  saveConfig();
  ```
  Nas propriedades numéricas (linhas 402 em diante):
  ```cpp
  bool configModified = false;
  long val = getJsonValue(json, "low");
  if (val != -999999 && val != g_config.LOW_THRESHOLD_RAW) {
    g_config.LOW_THRESHOLD_RAW = (uint16_t)val;
    configModified = true;
  }
  // [Idem para high, opt, refresh_ms...]
  if (configModified) {
    saveConfig();
  }
  ```

---

### B06 — Reinício Silencioso Pós-Queda de Energia

#### 1. Identificação e Problema Declarado
- **Item:** B06 (§4.10).
- **Problema:** Em caso de reinicialização espúria ou queda momentânea de energia com o nó em `MEASURING`, o nó reinicia sempre em `IDLE`. Ele se conecta à rede, transmite heartbeats `idle=1` a cada 5 s para o Hub e o Hub reporta `BiomassOnline = true`. O aplicativo supervisor vê o nó online (chip verde), mas a aquisição foi silenciosamente paralisada, sem disparo de alarme.

#### 2. Localização Exata no Código
- **Firmware:** `src/core/FirmwareApp.cpp:139` (`SystemState g_state = IDLE;`).
- **Firmware:** `src/core/Lifecycle.h:110` (`Serial.println("System IDLE.");`).
- **Hub:** `ESP32S3-HUB/src/network/HttpServer.h:310–357` (registra presença mas não força re-início).
- **App:** `Windows_app/src/OpenTECHub/Services/Telemetry/TelemetryParser.cs` e `BiomassControlViewModel.cs`.

#### 3. Causa Raiz Técnica
O nó não salva seu estado operacional em NVS para não degradar a flash com escritas de ciclo. O Hub adota postura passiva e não reenvia ordens de acionamento sem comando explícito do operador. O app só alarma se `BiomassCommEnabled && !BiomassOnline`.

#### 4. Decisão Proposta: FIX NO APP / DECISÃO DE ARQUITETURA
- **Decisão:** Não re-impor `start` cegamente pelo Hub nem pelo firmware (diretriz de segurança: religar emissor de luz sem supervisão humana pode incidir em frasco desmontado ou amostra fora de rota).
- **Ação no App:** Adicionar regra de alarme no `AlarmService` / `BiomassControlViewModel`: se o roteamento estiver ativado (`BiomassCommEnabled == true`), o nó estiver online (`BiomassOnline == true`), mas nenhuma amostra fresca tiver sido recebida por mais de $2,5 \times \text{BiomassProbePeriodMs}$ (ou timeout padrão de 60 s), disparar o alarme de advertência:
  *"Sensor de biomassa em repouso — Medição interrompida pós-reinício"*.

---

### B07 — Inconsistência de Identidade e Versões

#### 1. Identificação e Problema Declarado
- **Item:** B07 (§4.10).
- **Problema:** O firmware anuncia `FW_VERSION "v11"` no serial de boot, no `/nodeHello?ver=v11` e no `/diag`. Contudo, a página HTML de atualização OTA em `LocalHttpApi.h:80` declara fixo `"Biomass Sensor Firmware v5.3"`. Além disso, a macro interna em `FirmwareApp.cpp:26` declara `FW_NAME "biomass_sensor_analog_v04_direct"`, e os arquivos documentais locais (`README.md`, `CURRENT_STATUS.md`, `CHANGELOG.md`) referenciam a versão legada `v5.3`.

#### 2. Localização Exata no Código
- **Firmware:** `External-Devices/sensor-biomassa/firmware/biomass-sensor/src/core/FirmwareApp.cpp:25–26`:
  ```cpp
  static const char* FW_VERSION = "v11";
  static const char* FW_NAME    = "biomass_sensor_analog_v04_direct";
  ```
- **Firmware:** `src/api/LocalHttpApi.h:80`:
  ```html
  <p>Running: <b>Biomass Sensor Firmware v5.3</b></p>
  ```
- **Documentação:** `External-Devices/sensor-biomassa/README.md:3`, `CURRENT_STATUS.md:3`, `CHANGELOG.md:5`.

#### 3. Causa Raiz Técnica
Descompasso histórico ocorrido durante a reorganização do repositório em 2026-09-12, quando a versão de contrato foi unificada em `v11` para todos os nós no Hub, mas as referências visuais e textuais internas da biomassa permaneceram como `v5.3`.

#### 4. Decisão Proposta: FIX DOCUMENTAL E DE CÓDIGO
- **Decisão:** Unificar completamente na macro canônica `"v11.0"` (compatível com o catálogo do Hub e App Windows).
- **No Firmware:**
  1. Em `FirmwareApp.cpp:25–26`: Definir `FW_VERSION = "v11.0"` e `FW_NAME = "biomass_sensor"`.
  2. Em `LocalHttpApi.h:80`: Atualizar o HTML para exibir a versão dinâmica ou `"Biomass Sensor Firmware v11.0"`.
- **Na Documentação:** Atualizar `README.md`, `CURRENT_STATUS.md` e `CHANGELOG.md` para consagrar a versão ativa `v11.0`.

---

### B08 — Divergência em Documento do Aplicativo

#### 1. Identificação e Problema Declarado
- **Item:** B08 (§4.10).
- **Problema:** O antigo documento `Windows_app/docs/PROTOCOL.md` listava comandos inexistentes `set_ema`/`set_period` e descrevia a caixa de correio como consumível no envio sem ACK.
- **Estado Atual:** Já corrigido nas revisões documentais anteriores.
- **Decisão Proposta:** **JUSTIFICAR COMO RESOLVIDO (DOC)**. Nenhuma alteração de código necessária.

---

### B09 — Duração da Varredura de Branco pelo Hub

#### 1. Identificação e Problema Declarado
- **Item:** B09 (§4.10).
- **Problema:** Ao receber `blank` via Hub (`{"blank":1}`), o nó executa a varredura sem cadência (`dutyPct = 0.0f`). A varredura testa 32 células ($4 \times 8$). Com tempos de integração de até 800 ms e tempo de estabilização do LED, a varredura dura entre 20 e 40 segundos reais. A receita do aplicativo estipula um temporizador curto de $\sim 15\text{ s}$ e tenta iniciar a medição antes da conclusão, resultando em recusa com erro `"Busy"`.

#### 2. Localização Exata no Código
- **Firmware:** `src/measurement/BlankingAndRange.h:13–99` (`runBlankingRoutine`).
- **Hub:** `ESP32S3-HUB/src/protocol/Commands.h:451` (`String blankVal = getValueFromJson(json, "blank");`).
- **App:** `Windows_app/src/OpenTECHub/Services/Recipes/RecipeEngine.ExternalDevices.cs`.

#### 3. Causa Raiz Técnica
A varredura completa da matriz necessita de tempo de hardware real para integrar os fótons em cada slot de IT. A receita no aplicativo foi projetada com temporização otimista arbitrária sem aguardar o retorno da caixa de correio (`BiomassCommandPending == false`).

#### 4. Decisão Proposta: FIX NO APP / DOCUMENTAL
- **Decisão:**
  1. No App / Motor de Receitas: Condicionar o avanço da etapa de calibração de branco à transição de `BiomassCommandPending` de `true` para `false` (confirmando o ACK do nó via `ack_cmd_id`), com timeout de segurança estendido para 60 segundos (em vez de timer cego de 15 s).
  2. No Firmware: O envio de ACK no término da varredura já está implementado em `TelemetryAndHub.h:148` (`g_lastAppliedHubCmdId = id;`).

---

### B10 — Invalidação de Branco ao Alterar IT ou PWM

#### 1. Identificação e Problema Declarado
- **Item:** B10 (§4.10).
- **Problema:** Comandos `set_it`, `set_pwm` e `pwm_preset` invalidam o branco armazenado e derrubam o nó para `IDLE`.
- **Localização:** `src/protocol/CommandCodec.h:325, 340, 359` (`invalidateBlank(...)`).
- **Causa Raiz:** A absorbância é calculada pela lei de Beer-Lambert relativa: $A = -\log_{10}(I / I_0)$. O valor $I_0$ depende estritamente do tempo de integração $IT$ e da potência PWM na qual foi obtido. Alterar as tabelas físicas de IT ou PWM descalibra a matriz $I_0$ de referência.
- **Decisão Proposta:** **DECISÃO DE PROJETO (JUSTIFICAR NÃO-IMPLEMENTAÇÃO)**. A invalidação é mandatória pela física óptica do método turbidimétrico. O aplicativo já exibe avisos informando que modificar tabelas de aquisição exige nova captura de branco.

---

### B11 — Comandos Perigosos Restritos ao Canal Local (`hub_off` e `factory`)

#### 1. Identificação e Problema Declarado
- **Item:** B11 (§4.10).
- **Problema:** Os comandos `hub_off` (que desliga o rádio STA com o Hub) e `factory` (restauração de padrões de calibração) só funcionam via serial USB ou Web UI local (AP `192.168.7.1`).
- **Localização:** `src/protocol/CommandCodec.h:233, 366`.
- **Causa Raiz:** Se `hub_off` fosse roteado pelo Hub, um comando remoto faria o nó cortar sua própria via de comunicação de rede, tornando impossível reverter a ação remotamente.
- **Decisão Proposta:** **DECISÃO DE PROJETO (JUSTIFICAR NÃO-IMPLEMENTAÇÃO)**. Trata-se de um intertravamento de proteção arquitetural fechado. Não deve ser roteado pelo Hub.

---

### B12 — Roteamento Espúrio de `test_period` no Hub

#### 1. Identificação e Problema Declarado
- **Item:** B12 (§4.10).
- **Problema:** O Hub possui parser para a chave `test_period` em `Commands.h:459–460`, mas os comandos que ativam e desativam o sweep de teste do LED (`test_on` e `test_off`) não são roteados. Enviar `test_period` via Hub é inócuo.
- **Localização:** `ESP32S3-HUB/src/protocol/Commands.h:459–460`.
- **Causa Raiz:** Resíduo da migração inicial do protocolo.
- **Decisão Proposta: FIX COSMÉTICO NO HUB**. Remover o bloco de `test_period` em `Commands.h` do Hub para eliminar código morto.

---

### B13 — Tratamento de Valores-Sentinela de Absorbância (`-99.0` e `9.9`)

#### 1. Identificação e Problema Declarado
- **Item:** B13 (§4.10).
- **Problema:** Em caso de branco nulo ou saturado na marcha atual, o firmware atribui $A = -99,0\text{ AU}$. Em caso de sinal RAW zero (escuro total / feixe óptico bloqueado), atribui $A = 9,9\text{ AU}$. Esses valores são transmitidos pelo Hub e chegam ao aplicativo como números normais, sendo plotados em gráficos e exibidos na tela (`-99,000`), causando distorções na interface e interpretação incorreta em receitas.

#### 2. Localização Exata no Código
- **Firmware:** `External-Devices/sensor-biomassa/firmware/biomass-sensor/src/measurement/MeasurementPipeline.h:110–121`:
  ```cpp
  if (current_I0 == 0 || current_I0 == 65535) {
    g_lastAbsorbance = -99.0f; // Error: Blank is 0 or Saturated
  } else {
    if (current_I > current_I0) {
      current_I = current_I0; // Cap reading at blank value
    }
    if (current_I == 0) {
      g_lastAbsorbance = 9.9f; // Error: True zero reading
    } else {
      g_lastAbsorbance = -log10((float)current_I / (float)current_I0);
    }
  }
  ```
- **App:** `Windows_app/src/OpenTECHub/Services/Telemetry/TelemetryParser.cs` e `BiomassControlViewModel.cs`.

#### 3. Causa Raiz Técnica
O contrato de comunicação do Hub utiliza números de ponto flutuante simples no parâmetro `absorbance`. O firmware utiliza valores-sentinela herdados da instrumentação analógica clássica, sem flags booleanas associadas no push leve de telemetria.

#### 4. Decisão Proposta: FIX NO APP
- **Decisão:** Tratar os valores-sentinela no frontend (App Windows) e no motor de receitas, sem quebrar o contrato numérico do Hub:
  - No `TelemetryParser.cs` / `BiomassControlViewModel.cs`:
    - Se $\text{absorbance} \le -90,0\text{ AU}$ $\rightarrow$ Tratar como status `InvalidBlank` e formatar na UI como `"--- [Branco Inválido]"`.
    - Se $\text{absorbance} \ge 9,0\text{ AU}$ $\rightarrow$ Tratar como status `SensorDark` e formatar na UI como `"> 4.0 AU [Escuro/Bloqueado]"`.
    - Bloquear a inserção de valores fora de $[-0,5; 4,0]$ no histórico de séries temporais de gráficos de batelada.

---

### B14 — Campos Não Roteados no Hub (`hd_mode`, `i0`, `sat`, `single`, `manual`, `boot_id`)

#### 1. Identificação e Problema Declarado
- **Item:** B14 (§4.10).
- **Problema:** Campos de estado presentes no JSON local e no push não são propagados pelo Hub para o App.
- **Análise:** O parâmetro `hd_mode` (High Density Mode) já é transmitido pelo firmware em `TelemetryAndHub.h:76` (`snprintf` query param `hd_mode=%d`), mas é descartado pelo Hub em `HttpServer.h:335–350`.
- **Decisão Proposta:** **DECISÃO CONDICIONADA A HEADROOM / JUSTIFICAR NÃO-IMPLEMENTAÇÃO**. Adicionar novos campos de eco ao quadro de telemetria geral do Hub consome buffer de string global (`HUB_TELEMETRY_JSON_RESERVE = 3072`). Apenas `hd_mode` tem relevância operacional direta; os demais campos podem permanecer restritos à porta de diagnóstico USB/AP local.

---

### B15 — Ordem de Envio de Parâmetros de Aquisição (`gear -> it -> pwm`)

#### 1. Identificação e Problema Declarado
- **Item:** B15 (§4.10).
- **Problema:** `set_it` e `set_pwm` aplicam ao slot corrente.
- **Estado Atual:** Já solucionado no App através do sequenciamento garantido em `CommandBuilders.BiomassTuning`.
- **Decisão Proposta:** **JUSTIFICAR COMO RESOLVIDO (APP)**.

---

## 5. Matriz Consolidada de Decisões Técnicas (B01 a B15)

| ID | Item Auditado | Componentes Afetados | Decisão | Complexidade | Justificativa / Plano de Ação |
|:---:|---|:---:|:---:|:---:|---|
| **B01** | Janela de presença em MEASURING | Hub / Firmware | **FIX** | Média | Hub calcula janela dinâmica $\max(10\text{s}, 2 \cdot \text{probe\_ms} + 5\text{s})$; Firmware envia heartbeat com `idle=1` no repouso entre amostras. |
| **B02** | Rotinas bloqueantes (branco/busca) | Firmware / Hub | **FIX** | Média | `delayServiced()` invoca `serviceHubNetwork()` a cada 2 s, permitindo processar comando `stop` e abortar rotina. |
| **B03** | Marcha manual e auto-range | Hub / Firmware / App | **FIX** | Média | Hub e App mapeiam `biomassAutoRange` (`auto`/`manual`); Firmware respeita marcha configurada no `start` quando `!g_autoRange`. |
| **B04** | LED aceso após `set_gear` em MEASURING | Firmware | **FIX (Crítico)** | Baixa | `setManualGear()` desliga o LED incondicionalmente (`pwmSetDutyPercent(0)`) e reprograma `g_nextReadTime`. |
| **B05** | Persistência parcial (limiares/período) | Firmware | **FIX** | Baixa | Adicionar `saveConfig()` coalescido em `probe_period`, `low`, `high` e `opt` em `CommandCodec.h`. |
| **B06** | Reinício silencioso pós-queda | App / Hub | **FIX (App)** | Baixa | App dispara alarme de cultura interrompida se nó estiver online sem amostra há $> 2,5 \times \text{probe\_ms}$. |
| **B07** | Identidade v11 vs v5.3 | Firmware / Docs | **FIX** | Baixa | Padronizar `FW_VERSION "v11.0"` em `FirmwareApp.cpp`, atualizar HTML OTA em `LocalHttpApi.h:80` e docs. |
| **B08** | Divergência em `PROTOCOL.md` do app | Documentação | **JUSTIFICAR** | N/A | Já resolvido em revisões anteriores. |
| **B09** | Duração do branco pelo Hub | App / Docs | **FIX (App)** | Baixa | Motor de receitas aguarda `BiomassCommandPending == false` com timeout de 60 s em vez de timer cego de 15 s. |
| **B10** | `set_it`/`set_pwm` invalidam branco | Firmware | **JUSTIFICAR** | N/A | Decisão de projeto: mandatória por calibração da física óptica de $I_0$. |
| **B11** | `hub_off`/`factory` somente locais | Hub / Firmware | **JUSTIFICAR** | N/A | Decisão de projeto: intertravamento de segurança para prevenir perda de comunicação remota. |
| **B12** | Roteamento espúrio de `test_period` | Hub | **FIX (Hub)** | Baixa | Remoção de código morto em `Commands.h`. |
| **B13** | Sentinelas `-99.0` e `9.9` | App | **FIX (App)** | Baixa | App mascara sentinelas na UI (`[Branco Inválido]`, `[Escuro/Bloqueado]`) e filtra gráficos. |
| **B14** | Campos extras de diagnóstico no Hub | Hub / Firmware | **JUSTIFICAR** | N/A | Decisão condicionada a limite de headroom e tamanho do buffer JSON do Hub. |
| **B15** | Ordem de envio de tuning | App | **JUSTIFICAR** | N/A | Já garantido por construção em `CommandBuilders.BiomassTuning`. |

---

## 6. Verificação de Integridade e Próximos Passos (M2)

1. **Validação da Estrutura de Código:** O firmware da biomassa é compilável via Arduino IDE 2.x com core ESP32 3.3.11 (`esp32:esp32:esp32s3`), mantendo o headroom dentro dos limites seguros da partição OTA de 1,28 MB.
2. **Separação de Commits (Requisito R4 do Projeto):**
   - **Commit 1 (Firmware Biomassa):** B04 (corte do LED em `setManualGear`), B05 (`saveConfig` em limiares e período), B07 (rótulos `v11.0` em `FirmwareApp.cpp` e `LocalHttpApi.h`), B02 (atendimento do Hub em `delayServiced`), B03 (respeito à marcha manual no `start`).
   - **Commit 2 (Hub Central):** B01 (janela dinâmica `biomassPresenceWindowMs`), B03 (roteamento de `biomassAutoRange`), B12 (limpeza de `test_period`).
   - **Commit 3 (Aplicativo e Docs):** B06 (alarme de medição parada), B09 (temporização de receitas), B13 (máscara de sentinelas na UI), atualização de `COMANDOS_DISPOSITIVOS_EXTERNOS.md` e `HUB_PROTOCOL_IMPROVEMENTS.md`.
