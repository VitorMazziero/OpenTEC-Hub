# Pontos de Melhoria e Observações — Exposição de Configurações dos Nós Externos

**Data:** 2026-09-12 (revisado no mesmo dia após as passadas de correção dos §1, §2, §3 e §8)  
**Contexto:** Levantamento cumulativo de oportunidades arquiteturais, resiliência e boas práticas identificadas durante a execução das Etapas 1 a 8 do plano `2026-09-12-plano-exposicao-config-nos-externos.md` (Hub `10.2.0-dev`, nós externos e aplicativo Windows).

**Legenda dos títulos:** ✅ implementado e verificado (compilação/testes) · 🟡 implementado, **validação de bancada pendente** · ⬜ pendente (bloqueado por bancada) · ⏸️ diferido por decisão, com justificativa no item · 🚫 avaliado e **não** implementar.

**Nota de numeração:** não existe §4. O conteúdo da Etapa 4 (ecos nos firmwares dos nós) está registrado em §3.3 e nas linhas "Eco de … (v11/3.9)" da matriz §9. A numeração foi mantida para não quebrar as referências cruzadas.

---

## 0. Estado consolidado

### O que já está implementado no código (verificado por compilação e testes)

| Item | Onde | Evidência |
|---|---|---|
| §1.1 Presença e ecos da distância independentes da leitura; filtro de estagnação removido | Hub `Telemetry.h`, `HttpServer.h` | 77/77 testes de contrato; compila 1 117 800 B |
| §1.2 Marca d'água do quadro agregado na serial | Hub `Telemetry.h` | idem |
| §1.3 Linha serial Hub→PC > 1024 B | App `SerialTransport.cs` | verificação de código; sem correção necessária |
| §1.4 Um `command` por revisão (biomassa) | Hub `Commands.h`; App `CommandBuilders.BiomassTuning` | testes existentes |
| §2.1 Faixas [−50, 200] mm e [100, 60000] ms nos três caminhos do nó | Nó distância `ConfigCodec.cpp` | compila 1 100 488 B |
| §2.2 Dedupe de `cmd_id` e NVS só em mudança real (nó) + botão explícito (app) | Nó distância `ConfigCodec.cpp`; App §6 | idem |
| §3.1 Formas compactas de `set_it`/`set_pwm`/`set_gear`/`ema`/`probe_period` | Nó biomassa `CommandCodec.h` v11 | verificação de código; doc do Hub corrigida (código 0-5) |
| §5.1–§5.4, §6.1–§6.4, §7.1–§7.3 (lado software) | App | 1598/1598 testes |
| §8.1 Proxy `/nodeDiag` em tarefa própria; linha serial `NodeDiag`; tabela de saúde | Hub `NodeDiagTask.h`; App `HubNodeDiagClient.cs`, `HubNodesViewModel.cs` | testes de contrato + app |
| §8.3 `truncated` e `body_bytes` no cache e na UI | Hub `NodeDiagTask.h`; App `HubNodeDiagClient.cs`, `HubNodesViewModel.cs`, simulador | teste Hub `test_truncated_body_is_flagged_with_real_size`; teste app `Truncated_diag_is_reported_as_a_hub_limit_not_as_missing_metrics` |
| §10.1 Semeadura de `cmd_id` por boot nas quatro caixas confiáveis | Hub `Mailboxes.h`, `Runtime.h` | teste `test_reliable_mailboxes_are_seeded_per_hub_boot`; compila 1 118 408 B |
| §10.2 "roteamento" → "habilitado no Hub" em chips, alarmes, receitas, manual | App (13 arquivos) | 1605/1605 testes |
| §6.1 Indicador "Gravando na NVS do nó…" com confirmação pelo eco e timeout de 20 s | App `FoamControlViewModel.cs`, `ControlView.xaml` | build |
| §7.1 Calibração volumétrica: `pump_speed`, parada pelo app, pontos, ajuste, recibo | App `PumpCalibrationViewModel.cs`, `CalibrationView.xaml`, `CommandBuilders.cs`, simulador | 7 testes novos em `PumpCalibrationTests` |
| §7.2 Seletores de IT (ms→código) e marcha (slot IT × slot PWM) | App `BiomassControlViewModel.cs`, `ControlView.xaml` | build; testes de fila inalterados |

### O que falta — e o que bloqueia

| Item | Falta | Bloqueio |
|---|---|---|
| §1.1 | Ensaio de nível parado 30 min (`VALIDATION.md` item 14) | Hub e nó na bancada |
| §1.2 | Ler o `ESP32_EVT` de máximo com os cinco nós ecoando | Bancada com os cinco nós |
| §2.2 | Confirmar na serial do nó "cmd_id já aplicado" e "nada persistido" nos cenários de reentrega e comando repetido | Nó na bancada |
| §3.3 | Biomassa a 5,3 kB do piso de 160 kB: decidir partições/poda antes da próxima função nesse nó | Decisão de projeto |
| §7.1 | Bancada da calibração volumétrica: 3 velocidades × 60 s, réplica, verificação independente; conferir gate do sensor de gotas | Bomba e recipiente graduado na bancada |
| §10.1 | Reiniciar o Hub com os nós ligados e enviar um comando a cada um (confirma a semeadura de `cmd_id`) | Hub e nós na bancada |
| §10.3 | `reconnectWifi` do fluxômetro como comando do app; `sensorBypass`/`disablePot` da bomba via Hub | Decisão após a bancada |
| §8.2 | Medir heap antes/depois da tarefa `NodeDiag` e mínimo por 30 min; limite > 150 kB | **Hub na bancada** — nenhum valor sintético foi registrado |
| §8.4 | Soak de 30 s de varredura sem aumentar perdas de push / `hub_fail_streak` | Bancada com os cinco nós |
| Pré-existente | `ESP32S3-HUB/tools/verify_contract.py` aponta para `_old/LEGACY_SHA256.txt`; a pasta no repositório é `old/` e o script falha antes de testar qualquer coisa | Ajuste de uma linha; fora do escopo destas passadas |

Os itens diferidos por decisão (⏸️ §2.4, §8.4-autenticação) e o não implementar (🚫 §3.2) têm a justificativa no próprio item e não entram na lista de pendências.

---

## 1. Hub (`ESP32S3-HUB`)

### 1.1 ✅ Desacoplamento entre Ecos de Configuração e Filtro de Estagnação
- **Situação atual:** No Hub (`Telemetry.h`), as chaves de eco da distância (`DistanceOffsetMm`, `DistanceSamplePeriodMs`, `DistanceSendPeriodMs`) são emitidas sob a condição:
  ```cpp
  if (validDistance) {
    jsonResponse += ",\"Distance\":" + String(snapDistanceValue, 2);
    if (snapDistanceEchoSeen) {
      jsonResponse += ",\"DistanceOffsetMm\":" + String(snapDistanceOffsetMm, 2);
      ...
    }
  }
  ```
  `validDistance` é verdadeira apenas quando o valor da distância não está estagnado (`distanceSensorValue >= 0.0f`). Se o sensor passar 5 leituras sem alteração física (por exemplo, mira fixa em bancada estática), o filtro de estagnação marca `-1.0f`.
- **Impacto:** O valor do offset e períodos aplicados pelo nó deixam de ser emitidos no `/readData` durante a estagnação, voltando para `"—"` no app, embora o nó continue online e enviando pushes com ecos perfeitamente saudáveis.
- **Avaliação (2026-09-12):** A preocupação procede e é mais ampla do que o enunciado. O nó publica `distance=%d` (mm inteiro, `FirmwareApp.cpp:154`), então cinco pushes idênticos são rotina com nível parado — e `DistanceOnline` também era calculado a partir de `validDistance`. Resultado: com o nível parado o Hub publicava `DistanceOnline:false`, contradizendo `PROTOCOL.md` ("o Hub recebeu um push dentro da janela"), o app disparava o alarme `DistanceSensorOffline` ("sensor não está respondendo à Central") e desabilitava a edição da configuração do nó (`CanEditNodeConfig`), com o nó respondendo a cada segundo.
- **Aplicado no Hub (`Telemetry.h`):** presença e leitura foram separadas. `distanceOnline = distanceSensorComm && idade <= DISTANCE_PRESENCE_TIMEOUT` (mesma expressão de `/nodes`, `HttpServer.h`); `validDistance = distanceOnline && valor >= 0`. `DistanceOnline` e os três ecos seguem `distanceOnline`; só a chave `Distance` segue `validDistance`. O intertravamento de espuma (`AgitatorFoam.h`) lê o estado bruto sob `stateMutex` e não foi tocado. O app não precisou de alteração: `TelemetryParser.ParseDistance` já resolve `DistanceOnline:true` sem `Distance` como "online, valor envelhece após `DistanceTimeout`", e o simulador (`WireCodec.cs:142`) já modelava `DistanceOnline` como presença. Contrato documentado em `WIRE_CONTRACT_V9.md` (§ Ecos por nó › Sensor de distância) e `PROTOCOL.md` (linha `Distance`); teste de contrato `test_telemetry_distance_presence_is_independent_of_stagnation`.
- **Filtro de estagnação removido (2026-09-12, segunda passada):** mesmo com a presença corrigida, o filtro continuava zerando `distanceSensorValue` com nível parado e pausava a lógica de espuma ("dados do sensor inválidos ou expirados") em bancada estática. Decisão: remover, não afrouxar. (a) O nó v11 já detecta falha do VL53L0X — timeout I²C, leitura 0 / ≥ 4000 / 8190 (`DistanceSensor.cpp:47`) — empurra `distance=-1` (`FirmwareApp.cpp:132`) e roda escada de recuperação em três níveis (re-init, bus clear, XSHUT); o Hub só precisa honrar `-1`. (b) Nenhum limiar de tempo separa "nível parado por horas", estado normal de uma batelada sem espuma, de "sensor travado". (c) O `MIGRATION_V8_TO_V9.md` registra que esse mesmo filtro já causou um "offline para sempre". Handler `/distance` em `HttpServer.h` reduzido a `distanceSensorValue = (newDistance >= 0) ? newDistance : -1`. Caso residual não coberto: VL53L0X que responde ao I²C com valor constante válido — raro, modo de falha "não reage a espuma nova", e o lugar para uma heurística seria o nó (`lastGoodRawMm`), não o Hub. Validação de bancada acrescentada em `VALIDATION.md` (nível parado por 30 min com espuma simulada no fim).

### 1.2 🟡 Monitoramento do Tamanho do Quadro Agregado (`HUB_TELEMETRY_JSON_RESERVE = 3072`)
- **Situação atual:** Com a adição dos ecos da distância (Etapa 1) e dos futuros ecos de fluxômetro, bomba e biomassa (Etapa 3), o tamanho da string JSON agregada cresce em ~200–350 bytes.
- **Impacto:** O buffer pré-alocado de 3072 bytes comporta com folga o pior caso atual (~2050 B $\rightarrow$ ~2400 B), mas aproxima-se do limite de segurança. Se ultrapassar 3072 bytes, a heap do ESP32 sofrerá realocações dinâmicas a cada ciclo de telemetria, gerando fragmentação.
- **Melhoria sugerida:** Medir em bancada o `Content-Length` real do `/readData` ao final da Etapa 3/4 com todos os periféricos conectados e ecoando simultaneamente (§7.3.5 do plano). Se exceder 2600 bytes, priorizar a poda de campos de depuração pouco usados (`FlowOutput`, `FlowSetpointCorrected`).
- **Instrumentação aplicada (2026-09-12):** `Telemetry.h` mantém uma marca d'água do tamanho do quadro e registra na serial `[ESP32_EVT]: Quadro agregado: novo maximo de N bytes (reserva 3072)` a cada degrau de 64 B de crescimento (o tamanho oscila alguns bytes com a contagem de dígitos de `Time` e dos volumes; o degrau evita ruído). Se o máximo ultrapassar a reserva, sai `[ESP32_AVISO]: Quadro agregado com N bytes excede a reserva ...` — a partir daí há realocação a cada ciclo. A medição de bancada passa a ser ler o último `ESP32_EVT` desse tipo depois de todos os nós registrados e ecoando; nenhuma chave nova no quadro. Compilação após a mudança: 1 117 656 B de flash (85%), 50 544 B de RAM estática (15%).

### 1.3 ✅ Limite de Linha Serial USB (1024 B)
- **Situação atual:** O contrato USB em `WIRE_CONTRACT_V9.md` delimita 1024 bytes por comando app $\rightarrow$ Hub. No sentido inverso (telemetria broadcast Hub $\rightarrow$ PC), a linha emitida por `Serial.println(lastSensorJson)` pode exceder 2000 bytes.
- **Verificação necessária:** Confirmar que os buffers de recepção serial no `Windows_app` (`ConnectionManager` / `System.IO.Ports.SerialPort`) não impõem truncamento em 1024 B ou 2048 B para linhas recebidas.
- **Verificado (2026-09-12) — sem correção necessária.** O limite de 1024 B é só do sentido app → Hub (`USB_LINE_MAX` em `Commands.h`; `MAX_SENSOR_BUFFER_LENGTH` em `AppContext.h` é o buffer da UART do sensor interno, não da USB). No sentido Hub → PC, `SerialTransport.cs` usa `SerialPort.ReadLine()` com `NewLine="
"` e `ReadTimeout=750 ms`: `ReadLine` acumula até o terminador sem teto de comprimento (o `ReadBufferSize` de 4096 B do driver é um anel de recepção, não um limite de linha), e em caso de `TimeoutException` o .NET devolve os bytes parciais ao buffer interno — o próprio laço trata esse caso como "linha em voo, próxima leitura completa". Um quadro de ~2400 B a 115200 bps leva ~210 ms, abaixo do timeout; em USB CDC nativo do S3 o tempo é menor ainda. Do lado do Hub, `Serial.println` bloqueia até escoar a linha inteira. Ponto fechado; a única condição de perda continua sendo o host não drenar a porta, que já derruba a conexão por outros caminhos.

### 1.4 ✅ Regra de "Um `command` por Revisão" na Biomassa e Despacho no App
- **Situação identificada (Etapa 3):** No Hub (`Commands.h`), os comandos de configuração da biomassa (`biomassIt`, `biomassPwm`, `biomassGear`, `biomassEma`, `biomassProbePeriodMs`) montam uma estrutura com chave `"command":"<nome>","value":<valor>`. Como o protocolo JSON do nó aceita apenas uma chave `"command"` por objeto, o Hub enfileira apenas a primeira chave encontrada e descarta quaisquer outras presentes no mesmo quadro, registrando `ESP32_EVT`.
- **Requisito para o App (Etapas 5 e 7):** O aplicativo não deve agrupar alterações de biomassa num único payload JSON. Os `CommandBuilders` e ViewModels devem despachar os comandos sequencialmente, aguardando que `BiomassCommandPending` retorne a `false` antes de enviar o próximo parâmetro.

---

## 2. Sensor de Distância (`External-Devices/sensor-distancia`)

### 2.1 ✅ Alinhamento da Faixa de `offset_mm`
- **Situação identificada:** No baseline de `ConfigCodec.cpp`, a validação era `if (offsetVal >= 0.0f)`. No entanto, o Hub e os requisitos de calibração física aceitam offset negativo (faixa $[-50.0, 200.0]$ mm), útil quando a face do sensor é instalada atrás da linha de referência do vaso.
- **Resolução na Etapa 2:** O nó foi ajustado para validar `offsetVal >= -50.0f && offsetVal <= 200.0f`, sincronizando com a validação do Hub.
- **Revisão (2026-09-12):** confirmado no código. Encontrada a mesma classe de desalinhamento em `sample_period`/`send_period`: o `PROTOCOL.md` do nó (§4.2) e o Hub afirmam $[100, 60000]$ ms, mas o nó aceitava qualquer valor $> 0$ por `POST /config` local e pela serial (o Hub só filtra o caminho por carona). Corrigido em `ConfigCodec.cpp` com as mesmas constantes do Hub; os três caminhos aceitam agora o mesmo conjunto.

### 2.2 🟡 Política de Proteção da Memória Flash (NVS Wear Leveling)
- **Situação atual:** Toda chamada a `processConfigUpdate` que altere parâmetros chama `saveNvsConfig()`, gravando na memória flash interna do ESP32 via Preferences API.
- **Impacto:** Embora o ESP32 utilize *wear leveling* no partição NVS, gravações em alta frequência degradam a vida útil da flash.
- **Recomendação para o App (Etapa 6):** Assegurar que o app nunca envie comandos de configuração em modo "reativo contínuo" (por exemplo, `TextChanged` sem confirmação ou sem botão explícito). O operador deve preencher os campos e clicar em um botão explícito *"Enviar configuração do nó"*, prevenindo dezenas de escritas em flash a cada ajuste.
- **Revisão (2026-09-12):** o lado do app está aplicado (§6, botão explícito). Do lado do nó, dois endurecimentos aplicados em `ConfigCodec.cpp`, porque a proteção não deve depender só do cliente: (a) **reentrega da mesma `cmd_id` é ignorada** — o Hub reenvia a carga a cada push até ver o `ack_cmd_id`, e cada reentrega reaplicava e regravava a NVS (para `reset_nvs`, reexecutava o reset); após reboot `g_lastCmdId` volta a 0 e a reentrega é aplicada, o que é correto; (b) **`saveNvsConfig()` só roda se algum valor mudou** — comando com valores já vigentes é sucesso sem escrita. Com isso o custo por clique do operador é no máximo uma passagem pela NVS, independentemente do que o cliente faça. O `nvs_set_*` do ESP-IDF já pula gravações de valor idêntico, mas a comparação explícita no nó não depende desse detalhe interno.

### 2.3 ✅ Resiliência de Comunicação sob Backoff
- **Situação atual:** Em caso de perda de conexão com o Hub, o nó adota backoff exponencial progressivo até 15 segundos (`MAX_HUB_BACKOFF_MS = 15000`).
- **Comportamento do Piggyback:** Se o operador enviar uma alteração de offset durante esse intervalo, o comando aguarda na `distanceBox` do Hub até o próximo push do nó (podendo levar até 15 s).
- **Tratamento:** O mecanismo de re-entrega (`takeReliable`) reenvia a configuração em cada push subsequente até receber o `ack_cmd_id`. No aplicativo, o indicador `DistanceCommandPending = true` deve manter a mensagem informativa "Aguardando confirmação do nó" para evitar que o operador tente reenviar repetidamente durante o backoff.
- **Revisão (2026-09-12) — sem alteração.** O laço está correto por construção: a entrega só acontece como resposta a um push que chegou ao Hub, e esse push já carrega o `ack_cmd_id` da revisão anterior, então não há janela em que o nó receba a mesma revisão duas vezes sem ter reiniciado (e para esse caso residual entrou o dedupe de §2.2). O atraso de até 15 s é o backoff do próprio nó e é a informação que `DistanceCommandPending` já transmite. Reduzir `MAX_HUB_BACKOFF_MS` trocaria latência de configuração (rara) por carga de rádio em falha (frequente); não vale.

### 2.4 ⏸️ Isolamento de Tarefas de Rede (FreeRTOS)
- **Situação atual:** O sensor de distância executa no modelo *single-loop* Arduino (`FirmwareApp.cpp`), onde `httpGet()` possui timeout de 2500 ms. Em redes muito congestionadas ou com pacotes perdidos, o laço de leitura do sensor I2C VL53L0X pode sofrer jitter durante o bloqueio da requisição HTTP.
- **Melhoria futura (diferida):** Migrar a comunicação HTTP e OTA para uma tarefa FreeRTOS secundária em core separado (`xTaskCreatePinnedToCore`), isolando a taxa de amostragem do sensor óptico da pilha Wi-Fi (padrão já utilizado com sucesso no firmware da biomassa).
- **Revisão (2026-09-12) — mantido diferido, com justificativa.** O bloqueio de 2,5 s só ocorre quando o Hub não responde; nesse estado nenhum consumidor depende das amostras além do `GET /` local, e o backoff espaça as tentativas em até 15 s. Com o Hub respondendo, o push leva dezenas de ms — jitter irrelevante para um período de amostragem de 1 s e para um intertravamento que decide por histerese de 0,5 mm. O custo real da migração é sincronizar ~20 globais compartilhados (`g_offsetMm`, períodos, `g_lastCmdId`, estado OTA, `failStreak`) entre duas tarefas, e o firmware da biomassa mostra que isso vem com mutexes e mais superfície de bug. Reabrir apenas se a bancada mostrar perda de amostra atribuível à rede com o Hub online.

---

## 3. Sensor de Biomassa e Bomba Peristáltica

### 3.1 ✅ Semântica de `set_it`, `set_pwm`, `set_gear`, `ema` e `probe_period` na Biomassa (`CommandCodec.h`)
- **Situação identificada (Etapa 3):** No firmware anterior da biomassa, `set_it` exigia `index` (0–3) e `code` (0–5), `set_pwm` exigia `index` e `value`, e `set_gear` exigia ambos `it` e `pwm`. No entanto, o Hub e o aplicativo enviam formas mais compactas baseadas em `value` ou índice linear.
- **Resolução aplicada na Etapa 4 (v11):**
  - `set_gear`: aceita tanto o par `{"it": i, "pwm": j}` quanto o índice linear direto `{"value": N}` ou `{"gear": N}` ($0 \le N \le 31$, onde $\text{gear} = \text{IT} \times 8 + \text{PWM}$).
  - `set_pwm`: aceita `{"value": V}` diretamente (aplicando ao nível de PWM corrente quando `index` é omitido).
  - `set_it`: aceita `{"code": C}` ou `{"value": C}` diretamente (aplicando ao slot de IT corrente quando `index` é omitido).
  - `probe_period`: quando enviado com `{"value": ms}`, atualiza com segurança o período de amostragem respeitando o limite térmico do LED (`minSafeRefreshMs`); quando enviado sem valor em `IDLE`, dispara a rotina de diagnóstico de tempo de conversão do sensor.
  - `ema`: aceita tanto o comando explícito `{"command":"ema","value":0.8}` quanto o parâmetro de nível superior `{"ema":0.8}`.
- **Revisão (2026-09-12):** confirmado em `CommandCodec.h` (v11) — todas as cinco formas estão implementadas como descrito. Um erro de documentação corrigido: a tabela de `WIRE_CONTRACT_V9.md` descrevia `biomassIt` como "índice de tempo de integração (0-3)", que é a faixa do *slot* (`index`), não do *código* que o app envia em `value` (0-5 → 25…800 ms, como `CommandBuilders.BiomassIt` e §7.2 já assumem). O Hub não valida o valor; o nó rejeita fora de 0-5.

### 3.2 🚫 Padronização de Nomenclatura nas Chaves da Bomba
- **Situação identificada:** O firmware da bomba espera `pumpSlope` e `pumpIntercept` para calibração, mas `pid_kp`, `pid_ki`, `pid_kd` para PID.
- **Resolução no Hub (Etapa 3):** O Hub faz a ponte traduzindo `pumpPid*` $\rightarrow$ `pid_*` e mantendo `pumpSlope`/`pumpIntercept` como *pass-through*.
- **Oportunidade futura:** Em versões posteriores da bomba, permitir que todas as chaves aceitem o prefixo `pump_` de forma consistente.
- **Revisão (2026-09-12) — não implementar.** A ponte no Hub já isola o app da inconsistência, e nenhum consumidor fala com a bomba sem passar pelo Hub. Acrescentar aliases `pump_*` no firmware 3.9 criaria duas grafias válidas para a mesma chave sem retirar a antiga (o Hub continuaria enviando `pid_*`), ou seja, mais superfície e nenhuma simplificação. Só vale se uma futura versão da bomba renomear as chaves de vez e o Hub for atualizado junto.

### 3.3 🟡 Verificação de Headroom dos Nós Externos após Inclusão dos Ecos (Etapa 4)
- **Fluxômetro v11:** O firmware utiliza 1 141 407 B de flash (87%), restando **169 313 B livres** (> 169 KB), superando com folga o limite de segurança mínimo estabelecido de 160 KB.
- **Bomba peristáltica 3.9:** Utiliza 1 095 067 B (83%) de flash e 55 060 B (16%) de RAM estática. Headroom livre de 215 KB.
- **Sensor de biomassa v11:** Utiliza 1 129 728 B (86%) de flash e 69 984 B (21%) de RAM estática. Headroom livre de 181 KB.
- **Remedição (2026-09-12, ESP32 core 3.3.11, `esp32:esp32:esp32`, sem `--warnings`):**

  | Nó | Flash | RAM estática | Livre na partição | Δ vs Etapa 4 |
  |---|---|---|---|---|
  | Fluxômetro v11 | 1 141 199 B (87%) | 52 120 B (15%) | 169 521 B | −208 B |
  | Bomba 3.9 | 1 095 067 B (83%) | 55 060 B (16%) | 215 653 B | 0 |
  | Biomassa v11 | **1 145 455 B (87%)** | **72 212 B (22%)** | **165 265 B** | **+15 727 B** |
  | Distância v11 | 1 100 488 B (83%) | 50 680 B (15%) | 210 232 B | (novo nesta revisão) |

  Fluxômetro e bomba batem com o registrado. **A biomassa cresceu 15,7 kB desde a Etapa 4 e está a 5,3 kB do piso de 160 kB** — qualquer acréscimo de funcionalidade nesse nó (o seletor de marcha de §7.2, por exemplo) precisa vir acompanhado de medição, e é o primeiro candidato a revisar o esquema de partições (a `default` do core reserva SPIFFS que o nó não usa) ou a podar o `web_ui.h` embutido. Os demais têm folga confortável.

---

---

## 5. Aplicativo Windows (`Windows_app`) — Camada de Fio e Simulador (Etapa 5)

### 5.1 ✅ Remoção do Campo Vestigial `speed` em `PumpStopProfile()`
- **Decisão (§3.7 do plano):** O comando `PumpStopProfile()` emitia historicamente `{"mode":0,"speed":0}` por paridade com o v.6. No entanto, o firmware do nó de bomba v3.9 tratava qualquer presença da chave de velocidade como solicitação de armar/operar modo de velocidade.
- **Implementação:** O builder foi alterado para emitir estritamente `{"mode":0}`. Todos os testes de formato de fio, parada de emergência e cenários de receitas foram atualizados para validar esse comportamento seguro.

### 5.2 ✅ Semântica de Ecos Não-Sticky vs Identidade Sticky
- **Arquitetura:** `TelemetryParser` foi estruturado para que todos os novos ecos de configuração de nós externos (`Distance*`, `Flow*`, `Pump*`, `Biomass*`) sejam estritamente **não-sticky**: na ausência da chave no quadro JSON (ex.: nó desconectado ou modo de dropout), a leitura no snapshot do app é imediatamente redefinida para `null`.
- **Exceção Confiável:** `FlowmeterBootId` é mantido intencionalmente **sticky** (`long?`), pois o `boot_id` identifica o ciclo de inicialização do nó e deve permanecer visível para auditoria de reinicializações espúrias durante a sessão.

### 5.3 ✅ Sequenciamento de Comandos de Ajuste da Biomassa
- **Implementação:** Para aderir à restrição de "um `command` por revisão" da caixa de correio do Hub, o método `CommandBuilders.BiomassTuning(...)` retorna `IReadOnlyList<OpenTECCommand>`.
- **Recomendação para Etapa 7:** O ViewModel da Biomassa deve despachar esses comandos de maneira serializada, monitorando `BiomassCommandPending` antes de submeter o próximo elemento da lista.

### 5.4 ✅ Precisão de Ponto Flutuante no Simulador (`PumpVolume`)
- **Observação:** O acumulador de volume no simulador é baseado no tempo contínuo de uptime (`model.UptimeSeconds`). Em testes de ciclo rápido, o reset de volume e leitura imediata podem apresentar resíduos infinitesimais ($\sim 10^{-6}$ mL).
- **Tratamento:** As asserções de teste utilizam tolerância de precisão (`< 0.001 mL`) para garantir robustez independente do jitter de temporização do sistema operacional.

---

## 6. Aplicativo Windows (`Windows_app`) — Controle › Distância e Vazão de Ar (Etapa 6)

### 6.1 ✅ Confirmação Destrutiva de NVS do Sensor de Distância
- **Decisão:** A restauração de parâmetros de fábrica do sensor de distância (`distanceResetNvs`) sobrescreve diretamente a memória flash NVS do ESP32 remoto.
- **Implementação:** O comando foi vinculado a `IDialogService.ConfirmDestructive`, exigindo confirmação explícita do operador ("Restaurar") e alertando expressamente sobre a reversão para o offset de 20 mm e períodos de 1000 ms.
- **Ponto de Melhoria:** Adicionar na UI um indicador transitório de sincronização (ex: "Gravando NVS...") enquanto o nó reinicializa/aplica e o novo eco não é refletido na telemetria.
- **Aplicado (2026-09-12):** `FoamControlViewModel` guarda o trio pedido (offset, amostragem, envio — no reset, os padrões de fábrica 20 mm / 1000 / 1000 do `ConfigCodec.cpp`) e liga `IsNodeConfigSyncing`; a UI mostra "Gravando na NVS do nó… aguardando o eco na telemetria" em cor de aviso até o quadro trazer os três valores iguais ao pedido, quando o status passa a "Nó confirmou e gravou: …". Se em 20 s (cobre o backoff de 15 s do nó) o eco não vier, o aviso pede para comparar os valores aplicados antes de reenviar. Distinção deliberada: o `DistanceCommandPending` do Hub diz que o nó *recebeu*; só o eco diz que *aplicou* — e no reset há um reboot no meio.

### 6.2 ✅ Precisão de Feedforward do Fluxômetro (`FormatTuning`)
- **Problema Detectado:** O offset de feedforward do medidor de vazão (`flowFfOffset`) possui o valor de calibração padrão `0.01033` (5 casas decimais). Máscaras convencionais de formatação (`"F2"` ou `"F4"`) truncavam a exibição visual para `0.0103`.
- **Implementação:** Foi introduzido o método auxiliar `FormatTuning` com máscara `"0.#####"` em `FlowControlViewModel`, garantindo que tanto o valor staged quanto o eco aplicado preservem a precisão original sem sufixar zeros espúrios.

### 6.3 ✅ Preservação dos Contratos de Layout Unificado no XAML
- **Contrato:** O layout da tela `Controle` possui verificações rígidas de regressão de ergonomia (`ControlWorkspaceContractTests`), monitorando contagens exatas de gatilhos e estilos compartilhados (como 11 instâncias de `ConnectedExternalDeviceEntryStyle`).
- **Implementação:** Os novos expanders de *"Configuração do nó"* e *"Sintonia do controlador"* foram encapsulados de forma auto-contida, utilizando `MultiDataTrigger` de habilitação nos próprios painéis e vinculações de comando com feedback de validação, sem interferir nos marcadores e contadores globais da tabela.

### 6.4 ✅ Arbitragem e Concorrência de Sintonia via `ActuatorId.Aeration`
- **Segurança:** O ajuste dos parâmetros de malha (`Kp`, `Ki`, `FfGain`, `FfOffset`, `RampRate`) altera a resposta física da válvula de aeração.
- **Implementação:** Todos os comandos de sintonia foram mapeados para `ActuatorId.Aeration` no `CommandActuators.KeyToActuator`. Com isso, o árbitro central recusa imediatamente qualquer tentativa de envio de sintonia durante ensaios automatizados de $k_L a$, ensaios de potência ou execução de receitas, informando o operador via `DispatchRefusal.Describe`.
- **Ponto de Melhoria Futura:** Prover predefinições (presets) de sintonia do controlador para diferentes faixas operacionais de vazão (ex.: baixa vazão 0.1–1.0 L/min vs alta vazão > 5.0 L/min).
- **Revisão (2026-09-12) — não implementar agora.** Um preset é uma promessa de comportamento da malha numa faixa de vazão; sem curvas de resposta levantadas na bancada para a válvula real, qualquer par (Kp, Ki) embutido no software seria um chute com aparência de recomendação. O caminho correto é o inverso: registrar sintonias validadas por faixa nos recibos de bancada e, só então, promovê-las a presets. Reabrir com dados.

---

## 7. Aplicativo Windows — Bomba Externa e Aquisição da Biomassa (Etapa 7)

### 7.1 🟡 Calibração volumétrica da bomba peristáltica (substitui o assistente gravimétrico)

- **Entregue nesta etapa:** A aba **Calibrações › Bomba Externa** permite editar `slope` e `intercept`, visualizar $Q = slope \cdot S + intercept$ em $S = 250, 500, 1000$, enviar o par pelo contrato atual e somente persistir/gravar o recibo quando os dois valores forem ecoados pelo nó. O recibo inclui pedido, eco aplicado, firmware do Hub e identidade da bomba. No firmware 3.9, $S$ é a unidade interna 0–1000 e é convertida em duty PWM 155–1023; usar diretamente PWM 64/128/255, como no rascunho original da Etapa 7, produziria uma calibração incompatível e foi corrigido.
- **Referência legada auditada:** A pasta `Windows_app/_old/_Windows App/v.6` contém `ui/pump_mode_window.py`, uma janela modal de configuração e simulação dos perfis da bomba, mas não contém um procedimento gravimétrico multiponto nem cálculo de calibração da bomba. Foram aproveitados apenas os princípios de fluxo isolado, prévia antes do envio e persistência explícita; seus payloads v3.7 não são autoridade para o contrato atual.
- **Decisão do operador (2026-09-12): volume, nunca massa.** O laboratório não usa balança na calibração da bomba; a referência é o recipiente graduado. O plano gravimétrico (densidade, temperatura, tara, Δm/ρ) acima foi **descartado** e substituído por um fluxo volumétrico no molde da aba Vazão de ar — "envie um setpoint, leia o medidor" — com o recipiente como medidor.
- **Aplicado (2026-09-12) — `Calibrações › Bomba Externa`, cartão "Acionamento volumétrico":**
  1. O operador escolhe a velocidade interna S (1..1000) e a duração (5..600 s) e clica **Acionar bomba**. O app despacha `{"pump_speed":S}` (chave nova `CommandKeys.PumpManualSpeed`, mapeada a `ActuatorId.ExternalPump` no árbitro; o Hub já a traduzia para `speed`, e o nó 3.9 a aplica em modo ocioso mantendo S).
  2. **A parada é automática e do app**, como pedido: um `DispatcherTimer` de 250 ms e todo quadro de telemetria chamam `Tick()`; ao vencer a duração o app despacha `{"pump_speed":0}` e registra o Δt **realmente decorrido** entre o quadro de partida e o de parada (`TimeProvider`), não o valor nominal. Uma parada recusada pelo árbitro mantém o acionamento aberto e tenta de novo no tick seguinte — nunca é reportada como parada.
  3. O operador lê o volume no recipiente e clica **Registrar ponto**: o ponto guarda (S, Δt, V) e deriva Q = V/(Δt/60) mL/min. Os pontos persistem em `PumpControlSettings.CalibrationPoints` e aparecem na tabela com resíduo contra o ajuste.
  4. Com ≥ 2 pontos em velocidades distintas, mínimos quadrados dá ganho, deslocamento e R²; **Usar ajuste nos coeficientes** copia para os campos (sem enviar nada). **Aplicar calibração** segue o fluxo existente: eco do nó, persistência e recibo — que agora traz `method`, os `runs` brutos e o `fit` quando os coeficientes vieram dele. Editar os campos depois de "Usar ajuste" apaga a proveniência do ajuste no recibo.
  5. Segurança: **Abortar** para a bomba sem criar ponto; queda de conexão durante o acionamento marca `_stopPendingOnReconnect`, avisa que a bomba pode continuar girando e envia a parada ao reconectar; `Dispose` com acionamento em curso também envia a parada. Limitação honesta: o nó **não tem temporizador** para `speed`; se o link cair e não voltar, a parada é no próprio nó.
- **Validação pendente (bancada):** três velocidades (p. ex. 250/500/1000) com 60 s cada, réplica em uma delas, e verificação independente de vazão após aplicar a curva. Conferir também que a porta do sensor de líquido não está bloqueando o motor durante o acionamento — o app não controla esse gate. Pelo código (`COMANDOS_DISPOSITIVOS_EXTERNOS.md` §1.1), a porta nasce **desligada** (`sensorEnable=false`) e o botão físico só manda nela após `sensorButtonOverride:0`; portanto, numa bomba de fábrica o gate não interfere, mas uma bomba configurada localmente pode ter a porta ligada.
- **Testes:** `PumpCalibrationTests` — parada automática pelo relógio do app com Δt medido, parada pela chegada de telemetria atrasada, aborto sem ponto, três pontos na curva de fábrica reproduzem 0,0280/1,7602 com R² = 1, velocidade única sem ajuste, recibo com `runs`/`fit`, e parada devida na reconexão.

### 7.2 ✅ Semântica real dos parâmetros ópticos da biomassa

- **Correção aplicada:** `BiomassIT` é ecoado em milissegundos, porém `set_it` recebe um código discreto 0–5. A UI mantém 25/50/100/200/400/800 ms para o operador e converte para 0/1/2/3/4/5 no fio. A marcha `gear` é o índice óptico combinado 0–31, e não um ganho TIA 1–7. Como `set_it` e `set_pwm` modificam os slots atualmente selecionados, a fila envia primeiro `set_gear`, depois IT e PWM; a ordem inversa alteraria outra posição da tabela.
- **Efeito operacional:** Alterar a tabela de IT ou PWM invalida o branco no firmware v11. A UI avisa que a captura de branco deve ser repetida antes de medir.
- **Período da sonda:** O firmware aceita até 3 600 000 ms e eleva solicitações abaixo do piso térmico calculado. O app expõe essa faixa e inicia em 25 000 ms, em vez do valor de 1 000 ms do rascunho, que seria automaticamente corrigido pelo nó na configuração padrão.
- **Melhoria futura:** Substituir os campos livres de IT e Gear por seletores que mostrem simultaneamente tempo, índice de PWM e PWM efetivo, evitando que o operador precise calcular `gear = IT_idx × 8 + PWM_idx`.
- **Aplicado (2026-09-12):** IT virou lista (25/50/100/200/400/800 ms → código 0-5 no fio) e a marcha virou dois seletores, **slot IT (0-3) × slot PWM (0-7)**, com a prévia "Marcha N = slot IT i × slot PWM j" e o eco do nó decodificado da mesma forma ao lado. Os campos de texto continuam sendo a fonte única (validação, fila de envio e testes inalterados); os seletores só fazem a aritmética `gear = IT_idx × 8 + PWM_idx` nos dois sentidos, com trava contra realimentação (`_syncingSelectors`). Sem novo eco do nó, portanto sem impacto no headroom da biomassa (§3.3).

### 7.3 ⏸️ Confirmações e temporização

- **Entregue nesta etapa:** O zeramento de volume é não-otimista e só é declarado concluído após `PumpVol < 0,05 mL`; a calibração só gera recibo após eco compatível; a aquisição óptica envia um comando por vez e permite cancelamento.
- **Melhoria futura:** Centralizar os timeouts de comandos externos num serviço de relógio/ACK acionado mesmo quando a telemetria cessa por completo. Hoje os avisos de 5 s (zeramento), 10 s (biomassa) e 15 s (calibração) são avaliados na chegada de um quadro subsequente; uma queda total do link é indicada pelo estado offline, mas não produz o texto específico de timeout até haver nova telemetria.
- **Revisão (2026-09-12) — diferido.** O ganho é um texto de timeout mais cedo numa situação (queda total do link) que a UI já sinaliza como "desconectado" e que agora, no caso da bomba, também dispara a parada devida na reconexão. O custo é um serviço transversal de relógio entrando em cinco ViewModels que hoje são deterministicamente testáveis por telemetria. Não vale antes de haver um caso de bancada em que o estado offline tenha sido insuficiente.

---

## 8. Hub e aplicativo — Saúde dos nós (Etapa 8)

### 8.1 🟡 Proxy assíncrono e contrato multimeio

- **Entregue no Hub:** Uma tarefa FreeRTOS independente consulta `GET /diag` dos cinco nós registrados a cada 30 s, com timeout de 500 ms, intervalo de 200 ms entre placas e corpo limitado a 511 bytes. Os handlers `/nodeDiag` apenas serializam o cache sob mutex; nenhuma chamada HTTP externa ocorre dentro de callback do servidor.
- **Entregue por USB:** `{"nodeDiag":"all"}` produz uma linha `{"NodeDiag":{...}}` por nó. Essa linha possui resultado próprio no parser do app: não vira telemetria, log ou falha consecutiva de parse e não toma posse de nenhum atuador no árbitro.
- **Entregue no app:** A tabela **Nós na rede do Hub** recebe RSSI, heap livre, uptime, falhas consecutivas com o Hub, estado OTA e métricas específicas. Em Wi-Fi, `/nodes` e `/nodeDiag` são consultados juntos a cada 10 s; em USB, a solicitação serial ocorre a cada 30 s enquanto a seção Conexão está visível.
- **Semântica operacional:** `code = 0` significa que o Hub ainda nunca consultou aquele nó; código negativo representa erro do cliente HTTP do ESP32; outros códigos preservam a resposta HTTP. `age_ms` é a idade da tentativa armazenada no cache, não substitui a idade da última telemetria da coluna **Visto há**.
- **Revisão (2026-09-12):** cada afirmação acima foi conferida contra o código — `NODE_DIAG_PERIOD_MS = 30000`, `http.setTimeout(500)`, `NODE_DIAG_INTER_NODE_MS = 200`, `body[512]` com corte em 511, handler `/nodeDiag` sem `HTTPClient`; no app, `HubNodesViewModel` alterna 10 s (Wi-Fi) / 30 s (USB) e `ParseOutcome.NodeDiag` não passa pelo árbitro. Sem inconsistências. O que falta é só o soak físico de §8.4.

### 8.2 ⬜ Medição física de heap ainda necessária

- **Instrumentação aplicada:** O Hub registra na serial `heap antes` e `heap depois` imediatamente ao criar a tarefa `NodeDiag`, permitindo preencher evidência reprodutível sem estimativa manual.
- **Bloqueio de bancada:** Não havia Hub conectado nesta execução. Portanto, o critério de mais de 150 kB livres depois da criação da tarefa permanece **não validado fisicamente** e nenhum valor sintético foi registrado como medição.
- **Próxima ação:** Após gravar o Hub 10.2, capturar o banner de boot e uma varredura completa com os cinco nós em `docs/evidence/hub-node-diag-heap-<data>.md`; registrar heap antes/depois, mínimo observado por 30 min, versões/placas e resultado do limite de 150 kB.
- **Revisão (2026-09-12) — continua pendente.** Confirmado que a instrumentação está no código (`startNodeDiagTask()` em `NodeDiagTask.h` registra `heap antes=… depois=…` via `ESP32_INFO`). Nada mais pode ser feito sem Hub físico; o item fica ⬜ até a evidência existir. Estimativa estática, para referência e não como medição: pilha de 6144 B + 5 × `sizeof(NodeDiagCache)` (≈ 5 × 528 B) ≈ 8,8 kB, contra 277 120 B livres para locais após as globais — o limite de 150 kB tem margem de sobra na teoria, o que só reforça que a medição é barata e deve ser feita.

### 8.3 ✅ Truncamento, validade JSON e evolução do esquema

- **Situação atual:** Todos os `/diag` atuais cabem em 512 bytes. O Hub sempre termina o buffer em NUL e só incorpora o corpo quando ele ainda possui delimitadores `{...}`; um corpo futuro truncado no meio não corrompe o documento externo, mas aparece como `diag: null` mantendo o código HTTP.
- **Melhoria recomendada:** Acrescentar `truncated: true` e `body_bytes` ao cache se algum firmware ampliar `/diag`. Isso separa explicitamente “HTTP 200 sem métricas” de “resposta maior que o contrato” e permite que o app dê uma orientação precisa de atualização.
- **Aplicado (2026-09-12):** não esperou um firmware ampliar `/diag`, porque o custo é de duas linhas e o sintoma sem o campo é enganoso — um 200 truncado caía em "Sem métricas específicas", apontando para o nó quando a correção é no Hub. `NodeDiagCache` ganhou `bodyBytes` e `truncated`; cada entrada de `/nodeDiag` e da linha serial `NodeDiag` emite `"truncated":bool,"body_bytes":N` (0/false antes da primeira coleta; a resposta `404` para nome desconhecido também os carrega). No app, `HubNodeDiag.Truncated`/`BodyBytes` são opcionais (Hubs anteriores continuam a ser lidos) e `DiagnosticText` mostra "/diag com N B excede os 511 B do cache do Hub; atualize o Hub" **antes** do ramo "Sem métricas". O simulador emite os campos. Contrato em `WIRE_CONTRACT_V9.md` e `PROTOCOL.md`.
- **Compatibilidade:** As métricas comuns são tipadas; campos desconhecidos ficam no dicionário `Extra`. Novos campos de um nó podem ser exibidos progressivamente sem alterar o quadro agregado nem quebrar versões anteriores do app.

### 8.4 ⏸️ Segurança e carga de diagnóstico

- **Superfície exposta:** `/nodeDiag` replica IP, MAC, SSID e estado operacional que já existem no `/diag` local. Hoje a rede do Hub é um SoftAP protegido, mas a rota não possui autenticação própria.
- **Melhoria futura:** Se o Hub passar a operar numa LAN compartilhada, filtrar `ssid`/`mac` por padrão e exigir autenticação para diagnóstico detalhado. Não reutilizar o endpoint para comandos ou OTA.
- **Monitoramento de carga:** Confirmar em soak de bancada que a varredura de 30 s não aumenta perdas de push, jitter do quadro agregado ou `hub_fail_streak`. Se houver contenção no rádio, elevar o intervalo ou escalonar por atividade; não mover as consultas de volta para o handler HTTP.
- **Revisão (2026-09-12):** autenticação/filtragem fica ⏸️ — a premissa (LAN compartilhada) não existe hoje; o Hub é SoftAP e `/nodeDiag` só replica o que o `/diag` local de cada nó já expõe a quem está na mesma rede. Implementar autenticação agora adicionaria um segredo a gerir no app e nos cinco nós sem reduzir superfície real. Reabrir junto com qualquer plano de colocar o Hub numa LAN. O soak de carga permanece pendente de bancada (mesma sessão de §8.2).

---

## 9. Matriz de Prioridades e Rastreamento

| Item | Componente | Impacto | Prioridade | Tratamento |
|---|---|---|---|---|
| Desacoplar presença e ecos de distância da estagnação | Hub | Telemetria / Alarme falso | Média | **Aplicado no Hub em 2026-09-12 (§1.1); validação de bancada pendente** |
| Remover filtro de estagnação de `/distance` (validade vem do nó) | Hub / Espuma | Intertravamento / Segurança de Processo | Alta | **Aplicado no Hub em 2026-09-12 (§1.1); ensaio de nível parado pendente (`VALIDATION.md`)** |
| Dedupe de `cmd_id` e gravação NVS só em mudança real | Nó Distância | Flash / Idempotência | Média | **Aplicado em 2026-09-12 (§2.2); bancada pendente** |
| Faixa $[100, 60000]$ ms de períodos também por `/config` e serial | Nó Distância | Consistência | Média | **Aplicado em 2026-09-12 (§2.1)** |
| Prefixo `pump_*` uniforme na bomba | Nó Bomba | Nomenclatura | Baixa | **Não implementar (§3.2): a ponte no Hub já resolve; aliases só somariam superfície** |
| Headroom da biomassa a 5 kB do piso de 160 kB | Nó Biomassa | Memória | Média | **Remedido em 2026-09-13 após v11.1: 1 129 932 B, 5,1 kB do piso; B02/B14 ficam condicionados à revisão de partições** |
| Validação [-50, 200] mm no nó | Nó Distância | Consistência | Alta | **Aplicado na Etapa 2** |
| Whitelist e tradução de chaves (Bomba/Fluxômetro/Biomassa) | Hub | Comunicação | Alta | **Aplicado na Etapa 3** |
| Regra "um command por revisão" na biomassa | Hub / App | Confiabilidade | Alta | **Aplicado no Hub (Etapa 3); builders em lista (Etapa 5); regra para App (Etapa 7)** |
| Suporte a `value` direto em `set_it`/`set_pwm`/`set_gear` | Nó Biomassa | Protocolo | Alta | **Aplicado na Etapa 4** |
| Eco de `kp`/`ki`/`ramp` no push do fluxômetro (v11) | Nó Fluxômetro | Telemetria / Malha | Alta | **Aplicado na Etapa 4** |
| Eco de `slope`/`intercept` no push da bomba (3.9) | Nó Bomba | Telemetria / Calibração | Alta | **Aplicado na Etapa 4** |
| Eco de `gear`/`ema`/`probe_ms` no push da biomassa (v11)| Nó Biomassa | Telemetria / Óptica | Alta | **Aplicado na Etapa 4** |
| Remoção de `speed` no `PumpStopProfile()` | App / Hub / Nó | Segurança de Acionamento | Alta | **Aplicado na Etapa 5** |
| Ecos não-sticky e `FlowmeterBootId` sticky | App Protocol | Integridade de Dados | Alta | **Aplicado na Etapa 5** |
| Botão explícito de envio (sem auto-save) | App UI | Hardware (Flash NVS) | Alta | **Aplicado na Etapa 6 (Distância e Vazão)** |
| Formatação com precisão 5 casas decimais para `flowFfOffset` | App UI | Exibição / Calibração | Média | **Aplicado na Etapa 6** |
| Arbitragem de comandos de sintonia via `ActuatorId.Aeration` | App Árbitro | Segurança de Processo | Alta | **Aplicado na Etapa 6** |
| Confirmação destrutiva no reset de NVS do sensor de distância | App UI | Prevenção de Falha | Alta | **Aplicado na Etapa 6** |
| Resoluções e sincronização do fluxômetro v11.0 (F01 a F16) | Firmware / Hub / App | Metrologia e Segurança | Alta | **Aplicado (2026-09-13, `COMANDOS_DISPOSITIVOS_EXTERNOS.md` §3.10): F01–F16 integrados; ensaio físico de bancada pendente (§3.11)** |
| Zeramento não-otimista do volume da bomba | App / Nó Bomba | Integridade operacional | Alta | **Aplicado na Etapa 7; validação física pendente** |
| Janela de presença da distância proporcional a `send_ms` (D03) | Hub | Telemetria / Alarme falso | Média | **Aplicado no Hub em 2026-09-13: `max(3 s, 2,5 × send_ms)`; intertravamento mantido em 1,2 s** |
| Janela de presença da biomassa (10 s) menor que `probe_ms` (25 s) em MEASURING | Hub | Telemetria / Alarme falso | Alta | **Aplicado no Hub em 2026-09-13 (B01): `max(10 s, 2,5 × probe_ms)`; bancada pendente** |
| Branco e busca de marcha não servem o Hub (sem push/poll; `stop` não aborta) | Nó Biomassa | Presença / Comando | Média | **Aberto por decisão (B02): flash a 5,1 kB do piso; B01 cobre a presença; receita instrui temporizador ≥ 60 s** |
| `set_gear` pelo Hub não trava a marcha; `auto`/`manual` não roteados | Nó / Hub / App | Óptica / UI enganosa | Média | **Aplicado em 2026-09-13 (B03/B04): nó v11.1 trava a marcha e apaga o LED; Hub roteia `biomassAutoRange`; app com interruptor** |
| Reinício silencioso da aquisição após queda de energia do nó | App (alarme) | Integridade de dados | Alta | **Aplicado no app em 2026-09-13 (B06): alarme *Aquisição de biomassa interrompida*** |
| `low/high/opt` e `probe_period` sem `saveConfig()` no nó | Nó Biomassa | Persistência | Média | **Aplicado no nó v11.1 (B05)** |
| Calibração linear manual com recibo após eco | App / Nó Bomba | Rastreabilidade | Alta | **Aplicado na Etapa 7; assistente gravimétrico diferido** |
| Assistente gravimétrico multiponto da bomba | App / Bancada | Calibração física | Alta | **Diferido; especificado em §7.1** |
| Conversão IT ms → código e Gear 0–31 | App / Nó Biomassa | Contrato de fio | Alta | **Corrigido na Etapa 7** |
| Fila cancelável de aquisição da biomassa | App / Hub / Nó | Confiabilidade | Alta | **Aplicado na Etapa 7** |
| Serviço central de timeout independente de telemetria | App | Diagnóstico | Média | **Diferido; especificado em §7.3** |
| Proxy `/nodeDiag` fora do handler HTTP | Hub | Responsividade / Rede | Alta | **Aplicado na Etapa 8; soak físico pendente** |
| Linha serial `NodeDiag` fora da telemetria | Hub / App Protocol | Integridade de Dados | Alta | **Aplicado na Etapa 8** |
| Saúde dos cinco nós em Wi-Fi e USB | App UI | Diagnóstico | Alta | **Aplicado na Etapa 8; validação física pendente** |
| Medição de heap do Hub após tarefa NodeDiag | Hub / Bancada | Memória / Estabilidade | Alta | **Instrumentada; pendente de bancada conforme §8.2** |
| Sinalizador explícito de corpo `/diag` truncado | Hub / App | Diagnóstico | Média | **Aplicado em 2026-09-12 (§8.3)** |
| Autenticação/filtragem da rota `/nodeDiag` em LAN compartilhada | Hub | Segurança | Média | **Diferido por decisão (§8.4): premissa de LAN não existe; reabrir com ela** |
| Soak de carga da varredura `NodeDiag` (30 s) | Hub / Bancada | Rede | Média | **Pendente de bancada (§8.4)** |
| Medição Content-Length `/readData` | Hub / Infra | Confiabilidade | Média | **Instrumentada em 2026-09-12 (§1.2)**; ler o `ESP32_EVT` de máximo na bancada da Etapa 4/7 |
| Truncamento de linha serial Hub → PC | App Protocol | Integridade de Dados | Média | **Verificado em 2026-09-12 (§1.3); sem correção** |
| FreeRTOS multi-core no sensor | Nó Distância | Desempenho | Baixa | **Diferido por decisão (§2.4): reabrir só com perda de amostra medida com o Hub online** |
| Semeadura de `cmd_id` por boot nas caixas confiáveis | Hub | Confiabilidade após reboot | Alta | **Aplicado em 2026-09-12 (§10.1); ensaio de reboot pendente** |
| Chip/textos "roteamento" → "habilitado no Hub" | App UI | Ergonomia | Média | **Aplicado em 2026-09-12 (§10.2)** |
| Indicador "Gravando na NVS do nó…" | App UI | Feedback | Média | **Aplicado em 2026-09-12 (§6.1)** |
| Seletores de IT e marcha (slot IT × slot PWM) | App UI | Ergonomia / Contrato | Média | **Aplicado em 2026-09-12 (§7.2)** |
| Calibração volumétrica da bomba (`pump_speed`, parada pelo app, pontos, ajuste) | App / Hub / Nó Bomba | Calibração | Alta | **Aplicado em 2026-09-12 (§7.1); bancada pendente** |
| Presets de sintonia do fluxômetro | App | Malha | Baixa | **Não implementar sem curvas de bancada (§6.4)** |
| Serviço central de timeout | App | Diagnóstico | Baixa | **Diferido (§7.3)** |
| `reconnectWifi` do fluxômetro como comando do app | App / Hub | Recuperação | Média | **Lacuna identificada (§10.3); próxima passada** |
| `sensorBypass`/`disablePot` da bomba via Hub | Hub / App | Calibração | Média | **Decidir após a bancada da calibração (§10.3)** |

---

## 10. Integração Hub ↔ aplicativo por dispositivo (auditoria de 2026-09-12)

Respostas às três perguntas práticas feitas antes das Etapas 5–7, verificadas no código. O catálogo por dispositivo (interações aceitas × efeito no firmware × ação do hardware) está sendo centralizado em `External-Devices/docs/COMANDOS_DISPOSITIVOS_EXTERNOS.md`, um nó por vez — a bomba peristáltica está completa e revelou `clear_nvs` acessível pelo Hub, `mode:0` zerando o volume, `speed` pegajoso e retomada automática após queda de energia.

**1. O nó retoma a conexão se o Hub reiniciar?** Sim, em duas camadas. (a) Wi-Fi: `checkWifi()` do nó reconecta ao SSID conhecido a cada 10 s e, após 8 pushes falhados, derruba a associação e refaz o hello (`LINK_WATCHDOG_FAILS`); o hello também se repete a cada 30 s, então o registro `/nodeHello` do Hub (que é RAM e some no reboot) volta sozinho em ≤ 30 s. (b) Comandos: **havia um bug** — o Hub recomeçava o `cmd_id` das quatro caixas confiáveis em 1 a cada boot, enquanto o nó que ficou ligado continuava ecoando o `ack_cmd_id` da sessão anterior; como `ackReliable` roda antes de `takeReliable`, o primeiro comando depois do reboot era dado como confirmado sem ser entregue. Corrigido em 2026-09-12 com semeadura aleatória por boot (`seedReliableMailboxes()`), igual à que fluxômetro e servo já tinham. Falta o ensaio de bancada: reiniciar o Hub com os nós ligados e enviar um comando a cada um.

**2. O que o app recebe como sinal de vida, e o termo "roteamento".** Por dispositivo, três coisas independentes chegam em todo quadro: `*Online` (o Hub recebeu push dentro da janela), `*CommEnabled` (o interruptor do Hub para aquele dispositivo, persistido na NVS dele) e `*CommandPending` (comando entregue e ainda não confirmado). Além disso, identidade (`*IP`/`*NodeVer`/`*NodeMac`) e, pelo `/nodeDiag`, RSSI, heap, uptime e falhas consecutivas. A UI mostra: chip vermelho "desconectado" (`Online=false`), chip âmbar "aguardando" (pendente) e — antes chamado "roteamento" — a divergência entre o interruptor do Hub e o do app. **Renomeado em 2026-09-12**: o chip passou a "Hub divergente" e todos os textos de UI, alarmes, receitas e manual trocaram "roteamento" por "habilitado/desabilitado no Hub", com a instrução de sincronização no tooltip. A palavra sobrevive apenas em comentários de código e nos nomes de chaves de fio (`pumpComm` etc.), que são contrato.

**3. As funções de cada dispositivo estão integradas Hub ↔ app?** Tabela por nó (o que o firmware aceita × o que o Hub traduz × o que o app expõe):

| Nó | Comandos que o nó aceita | Hub traduz | App expõe | Lacuna |
|---|---|---|---|---|
| Distância v11 | `offset_mm`, `sample_period`, `send_period`, `reset_nvs`, cooldowns/limiares de recuperação (`cooldown_*`, `l1_reinit`…) | os quatro primeiros | os quatro primeiros (Controle › Distância) | cooldowns/limiares só por `POST /config` local ou serial; não há motivo para expor no app |
| Fluxômetro v11 | setpoint, válvulas, `max_flow`, curva `a1…c2`, `kp/ki/ff/ramp`, `reconnect_wifi` | todos | todos exceto `reconnect_wifi` (só ecoado como `FlowmeterReconnectWifi`) | **`reconnectWifi` sem comando no app** — um fluxômetro estacionado com o interruptor desligado só volta pelo cabo. Baixo custo; candidato à próxima passada |
| Bomba 3.9 | `mode`, `init_t`, `final_t`, λ/φ, `p0..p20`, `t/q` por partes (≤ 100), `speed`, `command` (`start`, `stop`, `reset_volume`, `save/load/print_config`, `clear_nvs`), `pumpSlope/Intercept`, `pid_*`, `disablePot`, `sensorEnable`, `sensorBypass`, `sensorButtonOverride` — catálogo completo com efeitos em `External-Devices/docs/COMANDOS_DISPOSITIVOS_EXTERNOS.md` §1 | todos até `pid_*` (com `pump_speed` → `speed`) | perfis, zerar volume, calibração, PID, **e agora `pump_speed`** (calibração volumétrica) | `disablePot`/`sensor*` não passam pelo Hub: gate do sensor de gotas e potenciômetro só no nó. Relevante para a calibração (o sensor pode travar o motor) — decidir se entra na whitelist do Hub após a bancada |
| Biomassa v11 | `blank`, `start`, `stop`, limiares, `set_it`, `set_pwm`, `set_gear`, `ema`, `probe_period`, `led`, `pwm_preset`, `test_on/off`, `read_once`, `hub_on/off`, `auto/manual`, `save/load_config`, `print_*` | um `command` por revisão para os cinco de sintonia; `blank/start/stop/low/high/opt/test_period` | os mesmos | `led`, `pwm_preset`, `read_once`, `auto/manual` ficam na serial/UI local do nó — diagnóstico de bancada, não operação |
| Agitador de frasco | `pct`, `dir`, `on`, `auto`, `reEnablePot` | todos | todos | — |
| Servo (ESP32S3-driver) | rpm direto com `cmd_id`, rota Modbus, `poll_ms`, reset de energia | todos | todos | — |

Conclusão: a operação está integrada de ponta a ponta nos seis nós; as lacunas são diagnósticos locais por desenho, mais duas candidatas reais (`reconnectWifi` do fluxômetro e o gate de sensor da bomba) que dependem de decisão após a bancada.
