# Levantamento Técnico: Integração do Sensor de Biomassa no ESP32S3-HUB e Análise dos Itens B01 a B13

**Data:** 2026-09-13  
**Autor:** Explorer 2 (Teamwork Preview — Milestone 1)  
**Escopo:** Firmware do Hub (`ESP32S3-HUB/`), integração do Sensor de Biomassa (`firmware/biomass-sensor/`), análise de impacto de B01 a B13 (§4.10 de `External-Devices/docs/COMANDOS_DISPOSITIVOS_EXTERNOS.md`) e verificação das ferramentas de build/teste.

---

## 1. Arquitetura e Localização no Código do ESP32S3-HUB

O ESP32S3-HUB atua como gateway mestre e coordenador da bancada do biorreator TECNAL. O Hub opera como SoftAP Wi-Fi (`ModuloTECNAL_1` ou `ModuloTECNAL_2`, IP fixo `192.168.4.1`), provendo servidor HTTP assíncrono (`AsyncWebServer` na porta 80) e interface USB Serial CDC (115200 baud) com o PC de supervisão.

A integração do sensor de biomassa ocorre nos seguintes módulos e arquivos:

| Subsistema | Arquivo de Implementação | Funções / Estruturas Principais | Descrição e Papel |
|---|---|---|---|
| **Variáveis Globais e Estado** | `ESP32S3-HUB/src/core/AppContext.h` (linhas 348–372, 424–450) | `biomassCommOn`, `biomassAbsorbance`, `biomassRaw`, `biomassIt`, `biomassPwm`, `biomassGear`, `biomassEma`, `biomassProbePeriodMs`, `biomassEchoSeen`, `biomassLastUpdate`, `biomassSampleLastUpdate`, `BIOMASS_TIMEOUT`, `g_deviceRegistry[DEV_BIOMASS]` | Mantém o estado instantâneo, timers de presença/freshness, ecos de configuração e registro do nó (IP, MAC, versão). |
| **Caixa de Correio Confiável** | `ESP32S3-HUB/src/core/AppContext.h` (linhas 101–114) e `src/protocol/Mailboxes.h` | `ReliableMailbox biomassBox;`<br>`queueReliable()`, `takeReliable()`, `ackReliable()`, `mailboxPending()` | Fila de comando confiável com revisão monotônica (`cmd_id`). Retém o comando até o nó confirmar via `ack_cmd_id`. Semente aleatória no boot evita colisão após reinício. |
| **Roteamento de Comandos** | `ESP32S3-HUB/src/protocol/Commands.h` (linhas 192–198, 230–236, 437–491) | `processCommandJson()` | Decodifica JSON vindo do PC (Serial USB ou HTTP `POST /command`). Trata habilitação de comutação (`"biomassComm"`), limpeza de fila (`"resetVariables"`), chaves curtas (`start`, `stop`, `blank`, `low`, `high`, `opt`, `test_period`) e comandos traduzidos (`biomassIt`, `biomassPwm`, `biomassGear`, `biomassEma`, `biomassProbePeriodMs`). Impõe regra estrita de *um comando por revisão*. |
| **Endpoints HTTP do Nó** | `ESP32S3-HUB/src/network/HttpServer.h` (linhas 308–358, 499–501, 521–540, 553–585) | `server.on("/biomassData", ...)`<br>`server.on("/biomassCommand", ...)`<br>`server.on("/nodeHello", ...)`<br>`server.on("/nodes", ...)` | - `/biomassData` (GET): recebe push de telemetria e heartbeat do nó via query params; atualiza presença e freshness; processa `ack_cmd_id`; responde 403 se `!biomassCommOn`.<br>- `/biomassCommand` (GET): entrega JSON pendente com `cmd_id` (`takeReliable`).<br>- `/nodeHello` (GET): registra IP, MAC e versão `v11`.<br>- `/nodes` (GET): lista saúde de todos os nós conectados. |
| **Decodificação e Publicação de Telemetria** | `ESP32S3-HUB/src/sensor/Telemetry.h` (linhas 156, 171–177, 302–315, 404) | `buildTelemetryJson()` | Constrói JSON agregado distribuído via USB Serial e HTTP `GET /readData`. Publica `BiomassOnline`, `BiomassCommEnabled`, `BiomassCommandPending`, e condicionalmente `BiomassAbs`, `BiomassRaw`, `BiomassIT`, `BiomassPWM`, `BiomassGear`, `BiomassEma`, `BiomassProbePeriodMs`, `BiomassIP`, `BiomassNodeVer`, `BiomassNodeMac`. |
| **Persistência NVS** | `ESP32S3-HUB/src/storage/Settings.h` (linhas 44, 86, 131) | `saveSettings()`, `loadSettings()`, `debugSettings()` | Persiste a preferência de comunicação na chave NVS `"bioComm"` (`biomassCommOn`). Nenhuma outra chave de biomassa é salva no Hub. |
| **Comunicação Serial** | `ESP32S3-HUB/src/core/FirmwareApp.cpp` e `src/sensor/SensorUart.h` | `handleSerial()`, `sensorSerial` (UART2, pinos 16/17) | O sensor de biomassa NÃO é conectado via UART (ele é exclusivamente nó Wi-Fi). A porta `Serial` USB é usada para envio de comandos do PC ao Hub e broadcast de telemetria. |
| **Diagnóstico de Nós (/diag)** | `ESP32S3-HUB/src/network/NodeDiagTask.h` (linhas 6–100) | `nodeDiagTask()`, `buildNodeDiagEntry()` | Tarefa em background que consulta periodicamente (a cada 30 s) o endpoint local `GET /diag` do nó de biomassa (porta 80 do IP do nó) e mantém cache para consulta via serial (`{"NodeDiag":"biomass"}`). |
| **Web / MQTT / Bluetooth** | `Config.h`, `HttpServer.h` | `AsyncWebServer server(80)` | **Web:** Servidor HTTP local ativo.<br>**MQTT:** NÃO EXISTE no Hub.<br>**Bluetooth / BLE:** NÃO EXISTE no Hub. |

---

## 2. Análise Detalhada dos Itens B01 a B13 (§4.10) e Impacto no Hub

### B01 — Janela de Presença e Freshness em MEASURING
- **Problema (§4.10):**  
  Em `MEASURING`, o nó de biomassa só emite telemetria a cada `probe_ms` (padrão de fábrica 25 000 ms = 25 s; com piso térmico de ~24,3 s para IT=800 ms). No entanto, o Hub possui timeout fixo de 10 s (`const unsigned long BIOMASS_TIMEOUT = 10000;` em `AppContext.h:371`).
- **Afeta o Hub?**  
  **SIM — IMPACTO CRÍTICO.**
- **Como o Hub opera hoje:**  
  Em `Telemetry.h:171-177`:
  ```cpp
  bool biomassOnline = snapBiomassUpdate > 0 && (millis() - snapBiomassUpdate <= BIOMASS_TIMEOUT);
  bool validBiomass = false;
  if (snapBiomassComm && snapBiomassSampleUpdate > 0) {
      unsigned long age = millis() - snapBiomassSampleUpdate;
      if (age <= BIOMASS_TIMEOUT) validBiomass = true;
  }
  ```
  Em `HttpServer.h:571` (`/nodes`):
  ```cpp
  else if (i == DEV_BIOMASS) isOnline = (biomassLastUpdate > 0 && (now - biomassLastUpdate <= BIOMASS_TIMEOUT));
  ```
- **Consequência no sistema:**  
  Passados 10 s da última amostra recebida, o Hub declara `BiomassOnline = false` e omite `BiomassAbs`, `BiomassRaw`, etc. O App Windows apaga as leituras na tela e o alarme *Absorbância offline* dispara intermitentemente a cada 25 s. Além disso, a receita automatizada *Iniciar Aquisição* falha se tentar avaliar a estabilidade do sensor durante a janela em que ele parece offline.
- **Chaves/Telemetria envolvidas:**  
  O parâmetro `probe_ms` já é enviado pelo nó via `/biomassData?probe_ms=...` e armazenado na variável global `biomassProbePeriodMs` (`AppContext.h:359`), porém **nunca era utilizado** para ajustar a tolerância de tempo.
- **Modificações necessárias no ESP32S3-HUB:**  
  Implementar função de janela dinâmica análoga à criada para o sensor de distância (D03, `distancePresenceWindowMs`):
  1. Em `AppContext.h`:
     ```cpp
     inline unsigned long biomassPresenceWindowMs(uint32_t probePeriodMs) {
       unsigned long dyn = probePeriodMs > 0 ? (unsigned long)(probePeriodMs * 2 + 5000) : BIOMASS_TIMEOUT;
       return dyn > BIOMASS_TIMEOUT ? dyn : BIOMASS_TIMEOUT;
     }
     ```
  2. Em `Telemetry.h`:
     Substituir `BIOMASS_TIMEOUT` por `biomassPresenceWindowMs(snapBiomassProbePeriodMs)` para o cálculo de `biomassOnline` e `validBiomass`.
  3. Em `HttpServer.h`:
     Usar `biomassPresenceWindowMs(biomassProbePeriodMs)` na rota `/nodes`.

---

### B02 — Rotinas Bloqueantes no Nó (`BLANKING`, `SEARCHING`, probeConversionPeriod)
- **Problema (§4.10):**  
  As rotinas `runBlankingRoutine()`, `findAndSetOptimalGear()` e `probeConversionPeriod()` chamam `delayServiced()`, que no nó apenas atende requisições do servidor web local e serial, sem enviar dados ao Hub (`sendDataToHub()`) nem consultar comandos (`pollHubForCommands()`). Durante os 20 a 40 s de varredura do branco, o nó fica em silêncio de rede.
- **Afeta o Hub?**  
  **SIM — IMPACTO CONCORRENTE / TEMPORAL.**
- **Como o Hub opera hoje:**  
  O Hub enfileira `{"blank":1}` em `biomassBox` (`ReliableMailbox`). A caixa retém o comando até o ACK.
  Contudo:
  1. O Hub marcava o nó ausente aos 10 s (resolvido por B01 ou por conhecimento de comando pendente).
  2. Como a caixa `biomassBox` retém apenas **uma revisão** por vez, se o operador ou receita enviar um comando (ex: `start:1`) antes de o nó buscar o `blank` no poll periódico (a cada 2 s), o `start` substitui o `blank` na caixa e o branco nunca é executado.
  3. Comandos de parada (`stop:1`) enviados pelo Hub não interrompem o branco no nó em tempo real, pois o nó não consulta o Hub durante o bloco.
- **Modificações necessárias no ESP32S3-HUB:**  
  - A correção raiz do atraso e ausência de poll está no firmware do nó (`delayServiced()` atender o Hub a cada 5 s).
  - No Hub:
    - O dimensionamento da janela de presença (B01) e retenção confiável em `biomassBox` garantem que o Hub não perca a referência.
    - Confirmar que o Hub mantém `biomassPending` ativo durante todo o processo até o `ack_cmd_id` chegar no primeiro push após o término da rotina.

---

### B03 — Falta de Roteamento de Marcha Manual vs Auto-Range (`biomassAutoRange`)
- **Problema (§4.10):**  
  O comando `start` no nó sempre executa o *Smart Start* (escolhendo a marcha mais clara dentro de $I_0 \le high$). Além disso, o auto-range do nó comuta marchas automaticamente se 10 leituras ficarem fora da faixa. O nó possui internamente os comandos `auto` e `manual`, mas o Hub **não possui nenhuma chave** para comandá-los.
- **Afeta o Hub?**  
  **SIM — LACUNA DIRETA DE CÓDIGO NO HUB.**
- **Como o Hub opera hoje:**  
  Em `Commands.h:466-486`, o Hub possui a tabela de tradução:
  ```cpp
  const BiomassCmdMap bioNewCmds[] = {
    { "biomassIt", "set_it" },
    { "biomassPwm", "set_pwm" },
    { "biomassGear", "set_gear" },
    { "biomassEma", "ema" },
    { "biomassProbePeriodMs", "probe_period" }
  };
  ```
  Não há menção a `biomassAutoRange`, `auto` ou `manual`. Se o usuário enviar `biomassGear`, ela só dura até o próximo `start` ou até o auto-range atuar.
- **Chaves/Telemetria envolvidas:**  
  - Falta no Hub: suporte à chave `biomassAutoRange` (valores aceitos: `"auto"`/`"manual"`, ou booleano `1`/`0`).
  - O firmware do nó aceita: `{"command":"auto"}` ou `{"auto":1}`, e `{"command":"manual"}` ou `{"manual":1}`.
- **Modificações necessárias no ESP32S3-HUB:**  
  Em `Commands.h`, adicionar o mapeamento de `biomassAutoRange`:
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
  Também aceitar chaves curtas diretas `"auto"` e `"manual"` se enviadas no payload.

---

### B04 — LED Aceso após `setManualGear()` em MEASURING
- **Problema (§4.10):**  
  No nó, `setManualGear()` chama `pwmSetLevel()` e só apaga o LED se `g_state == IDLE`. Em `MEASURING`, o LED fica ligado até o próximo ciclo de amostragem (até 25 s), violando o limite térmico do LED (duty limit 8%) e elevando baseline óptico.
- **Afeta o Hub?**  
  **NÃO.**
- **Como o Hub opera hoje:**  
  O Hub apenas repassa `biomassGear` como `{"command":"set_gear","value":N}`.
- **Modificações necessárias no ESP32S3-HUB:**  
  Nenhuma. Correção exclusiva do firmware do nó (`CommandCodec.h`).

---

### B05 — Persistência Parcial na NVS do Nó (`low`/`high`/`opt` e `probe_period`)
- **Problema (§4.10):**  
  Limiares de auto-range (`low`, `high`, `opt`) e `probe_period` alteram `g_config` em RAM no nó, mas não chamam `saveConfig()`. O comando `save_config` é bloqueado pelo Hub pela rede.
- **Afeta o Hub?**  
  **NÃO (Decisão de Arquitetura Mantida).**
- **Como o Hub opera hoje:**  
  O Hub repassa `low`, `high`, `opt` e `probe_period`. O Hub não roteia comandos destrutivos ou de formatação de flash (`clear_nvs`, `save_config`) para proteger a vida útil da flash do ESP32 contra escritas em loop de rede.
- **Modificações necessárias no ESP32S3-HUB:**  
  Nenhuma. A correção correta deve ser feita no nó chamando `saveConfig()` ao receber esses comandos individuais. O Hub deve manter a proteção de segurança ativa.

---

### B06 — Reinício Silencioso do Nó durante MEASURING
- **Problema (§4.10):**  
  Se o nó de biomassa sofrer queda de energia enquanto estiver em `MEASURING`, ele reinicia em `IDLE`. Em `IDLE`, ele emite heartbeats a cada 5 s com `&idle=1`. O Hub não impõe automaticamente um novo comando de `start`.
- **Afeta o Hub?**  
  **AVALIAÇÃO / DECISÃO DE PROJETO.**
- **Como o Hub opera hoje:**  
  O Hub trata `idle=1` corretamente: atualiza `biomassLastUpdate` (mantendo presença), mas não atualiza `biomassSampleLastUpdate`. Logo, os campos de leitura somem após o timeout. O Hub não força `start` automaticamente.
- **Decisão (§4.10):**  
  Reimpor `start` pelo Hub causaria risco de iniciar medição indesejada caso o operador tenha parado manualmente. A diretriz é tratar via alarme no App ("habilitado e online, mas sem amostras há > 2 · probe_ms").
- **Modificações necessárias no ESP32S3-HUB:**  
  Nenhuma modificação no Hub. O Hub já fornece todas as variáveis para o App diagnosticar o estado (`BiomassOnline: true`, `BiomassCommEnabled: true`, ausência de `BiomassAbs`, e `BiomassProbePeriodMs`).

---

### B07 — Unificação de Versão (`v11` vs `v5.3`)
- **Problema (§4.10):**  
  A string de versão do nó diferia entre documentos (`v5.3`) e código de rede (`v11`).
- **Afeta o Hub?**  
  **NÃO.**
- **Como o Hub opera hoje:**  
  O Hub recebe `ver=v11` no `/nodeHello` e publica no JSON de `/readData` como `"BiomassNodeVer":"v11"`.
- **Modificações necessárias no ESP32S3-HUB:**  
  Nenhuma. O Hub já está 100% alinhado com `v11`.

---

### B08 — Desalinhamento Documental do PROTOCOL.md do App
- **Problema (§4.10):**  
  Documentação anterior citava `set_ema`/`set_period` em vez de `ema`/`probe_period`.
- **Afeta o Hub?**  
  **NÃO (Já corrigido no documento).**
- **Como o Hub opera hoje:**  
  O Hub 10.2 já traduz `biomassEma` -> `ema` e `biomassProbePeriodMs` -> `probe_period`.
- **Modificações necessárias no ESP32S3-HUB:**  
  Nenhuma.

---

### B09 — Duração do Branco e Temporização de Receita
- **Problema (§4.10):**  
  O branco pelo Hub roda sem cadência térmica e leva de 20 a 40 s. A receita do aplicativo assumia temporizador de ~15 s e disparava `start` com o nó ainda ocupado (`Busy`).
- **Afeta o Hub?**  
  **NÃO (Ajuste no App / Receitas).**
- **Como o Hub opera hoje:**  
  O Hub repassa `{"blank":1}` e mantém `biomassBox` em espera até o ACK.
- **Modificações necessárias no ESP32S3-HUB:**  
  Nenhuma no Hub (a receita do App deve aguardar `BiomassCommandPending == false` ou estender o tempo para 40 s).

---

### B10 — Invalidação de Branco por `set_it` / `set_pwm`
- **Problema (§4.10):**  
  Alterações de IT ou PWM resetam a tabela $I_0$ e invalidam o branco óptico.
- **Afeta o Hub?**  
  **NÃO (Comportamento Físico Correto).**
- **Modificações necessárias no ESP32S3-HUB:**  
  Nenhuma. Decisão de projeto mantida.

---

### B11 — Bloqueio de Comandos Destrutivos (`hub_off`, `factory`)
- **Problema (§4.10):**  
  Comandos `hub_off` e `factory` desconectam o nó e desabilitam a rota de rede.
- **Afeta o Hub?**  
  **NÃO (Proteção de Segurança Mantida).**
- **Como o Hub opera hoje:**  
  O Hub deliberadamente não possui rota para esses comandos.
- **Modificações necessárias no ESP32S3-HUB:**  
  Nenhuma. Manter sem roteamento.

---

### B12 — Roteamento Inútil de `test_period` no Hub
- **Problema (§4.10):**  
  O Hub possui código para extrair `test_period` em `Commands.h:459`, mas `test_on` e `test_off` não são roteados. Logo, `test_period` é uma chave inútil pelo Hub.
- **Afeta o Hub?**  
  **SIM — LIMPEZA COSMÉTICA DE CÓDIGO.**
- **Como o Hub opera hoje:**  
  `Commands.h:459`:
  ```cpp
  String testVal = getValueFromJson(json, "test_period");
  if (testVal.length() > 0) { if (biomassCmdFound) biomassCommand += ","; biomassCommand += "\"test_period\":" + testVal; biomassCmdFound = true; }
  ```
- **Modificações necessárias no ESP32S3-HUB:**  
  Remover a extração de `test_period` de `Commands.h` e atualizar o fixture correspondente em `tests/contracts/test_node_commands.py`.

---

### B13 — Sentinelas Numéricas de Absorbância (-99.0 e 9.9)
- **Problema (§4.10):**  
  Quando $I_0$ é 0 ou saturado, o nó emite $A = -99.0$. Quando a leitura de luz é zero, emite $A = 9.9$. Ambos saem no push HTTP como números comuns de ponto flutuante.
- **Afeta o Hub?**  
  **NÃO — RESPONSABILIDADE DO APP ("no Hub, nada").**
- **Como o Hub opera hoje:**  
  O Hub decodifica `absorbance` como `float` e repassa fielmente com 3 casas decimais (`String(snapBiomassAbs, 3)`).
- **Modificações necessárias no ESP32S3-HUB:**  
  Nenhuma no Hub. Conforme especificado em §4.10: "No app: tratar `≤ −90` e `≥ 9,9` como 'branco inválido'/'escuro' com aviso; no Hub, nada". O Hub atua como canal transparente.

---

### Achado Adicional: B14 (`hd_mode` e telemetria estendida)
- O nó de biomassa em `TelemetryAndHub.h:76` **já envia** `&hd_mode=%d` em todo push para o Hub!
- No entanto, `HttpServer.h:338-350` no Hub descarta `hd_mode`.
- Se o Hub adicionar a captura de `hd_mode` e exportar `"BiomassHdMode": true/false` em `Telemetry.h`, o aplicativo poderá exibir o indicador de Alta Densidade sem exigir nenhuma modificação de firmware no nó!

---

## 3. Matriz Sintética de Impacto e Modificações no Hub

| Item | Tópico | Afeta Hub? | Ação no ESP32S3-HUB |
|:---:|---|:---:|---|
| **B01** | Janela de presença e freshness em MEASURING | **SIM (Crítico)** | Implementar `biomassPresenceWindowMs(probePeriodMs)` em `AppContext.h`, `Telemetry.h` e `HttpServer.h`. |
| **B02** | Rotinas bloqueantes no nó | **Indireto** | Preservação de comando garantida por `ReliableMailbox`; janela ampliada por B01 evita flap offline. |
| **B03** | Marcha manual vs Auto-range | **SIM (Funcional)** | Mapear `biomassAutoRange` (`"auto"` / `"manual"`) em `Commands.h`. |
| **B04** | LED aceso após setManualGear | **NÃO** | Correção exclusiva no nó. |
| **B05** | Persistência parcial de limiares | **NÃO** | Manter Hub bloqueando `save_config`; nó deve salvar ao receber comandos. |
| **B06** | Queda de energia em medição | **NÃO** | Manter Hub sem reimpor `start`; App gerencia alarme de amostras ausentes. |
| **B07** | Rótulo de versão v11 | **NÃO** | Hub já alinhado com v11. |
| **B08** | Documentação desatualizada | **NÃO** | Resolvido em documentação. |
| **B09** | Duração do branco | **NÃO** | Ajuste nos temporizadores de receita do App. |
| **B10** | Invalidação do branco | **NÃO** | Comportamento físico esperado mantido. |
| **B11** | hub_off e factory locais | **NÃO** | Diretriz de segurança mantida (bloqueados no Hub). |
| **B12** | Limpeza de test_period | **SIM (Cosmético)** | Remover chave inútil `test_period` de `Commands.h` e testes. |
| **B13** | Sentinelas -99.0 e 9.9 | **NÃO** | App trata valores sentinelas; Hub repassa float transparente. |

---

## 4. Verificação dos Mecanismos de Build e Teste do Hub

### 4.1 Ambiente de Compilação
- **PlatformIO:** O projeto ESP32S3-HUB **não utiliza PlatformIO** (inexistência de `platformio.ini`).
- **Arduino CLI:** O build oficial do Hub é configurado através do Arduino CLI utilizando o perfil do sketch:
  - Arquivo: `ESP32S3-HUB/ESP32S3-HUB/sketch.yaml`
  - FQBN: `esp32:esp32:esp32s3`
  - Plataforma: `esp32:esp32 (3.3.11)`
  - Bibliotecas requeridas: `Async TCP (3.4.2)`, `ESP Async WebServer (3.7.7)`
  - Script de compilação: `ESP32S3-HUB/tools/compile.ps1` (`arduino-cli compile --fqbn "esp32:esp32:esp32s3" --warnings all ESP32S3-HUB`).
  - Nota de ambiente: Atualmente o executável `arduino-cli` não está no `PATH` global do sistema operacional Windows, mas o diretório de dados `C:\Users\vitor\AppData\Local\Arduino15` com as bibliotecas correspondentes está presente.

### 4.2 Suíte de Testes Automatizados (Contratos e Regras)
O Hub possui uma suíte completa de testes em Python que valida contratos de comunicação, decodificação JSON, regras de fila e integridade sem necessidade de hardware físico:

1. **Pytest / Unittest (`ESP32S3-HUB/tests/contracts/`):**
   - Comando verificado:
     ```powershell
     python -m pytest ESP32S3-HUB/tests/contracts
     # OU
     python -m unittest discover -s ESP32S3-HUB/tests/contracts -p "test_*.py"
     ```
   - **Resultado:** 83 testes executados com sucesso (0 falhas) em ~0,18 s.
   - Módulos testados:
     - `test_node_commands.py`: valida tradução de comandos, listas brancas e isolamento de `biomassBox` (`BiomassCommandTests`).
     - `test_json_keys.py`: valida presença e ecos das chaves de telemetria (`BiomassGear`, `BiomassEma`, `BiomassProbePeriodMs`).
     - `test_http_frames.py`: simula parsing de endpoints e payloads HTTP.
     - `test_node_diag.py` e `test_node_registry.py`: testam cache e registro de nós.
     - `test_servo_*.py` e `test_usb_line_framing.py`: testes do servo motor e canal USB.

2. **Verificador PowerShell de Contratos de Dispositivos Externos:**
   - Comando verificado:
     ```powershell
     powershell -ExecutionPolicy Bypass -File External-Devices/tools/Test-HubDeviceContracts.ps1
     ```
   - **Resultado:** Executado com sucesso.
   - Valida a consistência cruzada entre os fontes em C++ do Hub (`ESP32S3-HUB/ESP32S3-HUB/src`) e os firmwares dos 5 nós externos (`External-Devices/`).

---

## 5. Propostas de Alteração no Código (Code Snippets)

### 5.1 `ESP32S3-HUB/src/core/AppContext.h` (Item B01)
```cpp
// Substituir a constante fixa por função de janela dinâmica:
const unsigned long BIOMASS_TIMEOUT = 10000;

// B01: Janela dinâmica dimensionada pelo probe_ms ecoado pelo nó:
// Piso de 10s (para IDLE e primeiro contato), ou 2 * probe_ms + 5s (em MEASURING).
inline unsigned long biomassPresenceWindowMs(uint32_t probePeriodMs) {
  unsigned long dyn = probePeriodMs > 0 ? (unsigned long)(probePeriodMs * 2 + 5000) : BIOMASS_TIMEOUT;
  return dyn > BIOMASS_TIMEOUT ? dyn : BIOMASS_TIMEOUT;
}
```

### 5.2 `ESP32S3-HUB/src/sensor/Telemetry.h` (Item B01)
```cpp
  // Validação de Biomassa com janela dinâmica (B01)
  uint32_t snapBiomassProbeMs = snapBiomassProbePeriodMs;
  unsigned long bioWindow = biomassPresenceWindowMs(snapBiomassProbeMs);

  bool biomassOnline = snapBiomassUpdate > 0 &&
                       (millis() - snapBiomassUpdate <= bioWindow);
  bool validBiomass = false;
  if (snapBiomassComm && snapBiomassSampleUpdate > 0) {
      unsigned long age = millis() - snapBiomassSampleUpdate;
      if (age <= bioWindow) validBiomass = true;
  }
```

### 5.3 `ESP32S3-HUB/src/network/HttpServer.h` (Item B01)
```cpp
  // Na rota /nodes:
  else if (i == DEV_BIOMASS) {
    unsigned long bioWin = biomassPresenceWindowMs(biomassProbePeriodMs);
    isOnline = (biomassLastUpdate > 0 && (now - biomassLastUpdate <= bioWin));
  }
```

### 5.4 `ESP32S3-HUB/src/protocol/Commands.h` (Itens B03 e B12)
```cpp
  // Remover a chave inútil test_period (B12):
  // (Remover linhas 459-460)

  // Adicionar roteamento de biomassAutoRange (B03):
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
