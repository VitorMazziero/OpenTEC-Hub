# Comunicacao Hub <-> Fluxometro - diagnostico e correcoes

> Status: **aplicado em 2026-09-10**, exceto o item 6 (politica de link perdido), que
> depende de uma decisao de processo. O firmware do fluxometro foi para a **V06**; a V05
> segue intacta na pasta ao lado como retorno. Hub e app tambem mudaram.

Fontes lidas:

- `ESP32S3-HUB/ESP32S3-HUB/src/network/HttpServer.h`
- `ESP32S3-HUB/ESP32S3-HUB/src/protocol/Mailboxes.h`
- `ESP32S3-HUB/ESP32S3-HUB/src/sensor/Telemetry.h`
- `ESP32S3-HUB/ESP32S3-HUB/src/core/AppContext.h`
- `.../Fluxometro/Software/__controler_ESP32/flowmeter_TECNALHUB_V06/flowmeter_TECNALHUB_V06.ino`
  (a V05 permanece na pasta irma, sem alteracao)

## 1. Topologia real

Não há rádio dedicado nem ESP-NOW. O fluxômetro é **cliente Wi-Fi (STA) do SoftAP do Hub**.

| Item | Valor |
|---|---|
| AP do Hub | `ModuloTECNAL_1` / `ModuloTECNAL_2` (senha = o próprio SSID), canal 6, `192.168.4.1`, máx. 8 estações |
| Fluxômetro | STA nesse AP **+** SoftAP próprio `Floxometro_AP` em `192.168.10.1`, mesmo canal 6, máx. 4 clientes |
| Comandos | fluxômetro faz `GET /flowCommand` a **10 Hz** (`commandPollInterval = 100` ms) |
| Telemetria | fluxômetro faz `GET /flowData?...` a **2 Hz** (`telemetryInterval = 500` ms) |
| Timeout de presença | Hub declara offline após **6 s** sem `/flowData` (`FLOWMETER_TIMEOUT`) |
| Timeouts do cliente | `hubConnectTimeoutMs = 1000`, `hubRequestTimeoutMs = 1500` |

## 2. O que acontece quando o link cai ("perde alcance interno")

1. **Fluxômetro:** `WiFi.status() != WL_CONNECTED` faz `httpTask` e `telemetryTask` pularem a
   iteração. O PI local continua rodando, o DAC continua escrevendo e as válvulas **congelam
   no último estado**. É *fail-in-place*, não *fail-safe*.
2. **Hub:** passados 6 s, `flowmeterCommOn = false`. Os campos `FlowVoltage/FlowRate/FlowSetpoint`
   somem do JSON de telemetria — correto, não republica dado velho.
3. **App Windows:** `IsFlowmeterOffline` → `CanSendFlowCommands = false`, controles bloqueados.
4. **Comando pendente não se perde:** a caixa é *reliable*; só limpa quando o `ack_cmd_id`
   volta na telemetria. Ao reconectar, é reentregue. Esse mecanismo está correto.

## 3. Furos na reconexão automática

Existe reconexão automática (`wifiTask`): a cada 5 s, canal 6 fixo, timeout de 8 s,
alternando entre os dois SSIDs conhecidos. Sem scan — decisão certa, scan em AP+STA derruba
o link direto. Os problemas:

### 3.1 O flip de SSID pune falha transitória
No timeout, `nextHubIndex` alterna **sempre**, mesmo quando o Hub certo está presente e apenas
ocupado. A próxima tentativa vai para o módulo errado e a volta custa ~13 s a mais.
Pior caso ~26 s para reassociar num Hub que nunca saiu do ar.
**Aplicado (V06):** `failedAttemptsOnCurrentSSID` tolera 3 timeouts no mesmo SSID antes de trocar.

### 3.2 `reconnect_wifi` é um kill switch sem volta pelo Hub
A chave existe no parser do fluxômetro, mas o Hub **não a aceita** (`queueReliableFlowCommandFromJson`
não reconhece a chave e `buildFlowCommandLocked` nunca a emite). Se alguém desligar a reconexão
pela serial USB ou pelo WebSocket do `Floxometro_AP`, o fluxômetro nunca mais volta sozinho e não
há como religar pelo Hub nem pelo app — só com cabo ou entrando no AP dele. Também não é persistida
em EEPROM, então um reboot a devolve para `true` — o que salva, mas por acidente.
**Aplicado:** o Hub passa a aceitar `{"reconnectWifi":0|1}` e a emitir `reconnect_wifi` no comando; a V06 tambem reporta o flag na telemetria, que o Hub republica como `FlowmeterReconnectWifi` e o app le. O interruptor deixou de ser de mao unica e deixou de ser invisivel.

### 3.3 Não há watchdog de link em nível de aplicação
`telemetryFailures` é contado e logado e **nunca dispara nada**. O caso "`WL_CONNECTED` mas nenhum
HTTP passa" (AP lotado, lease perdido, socket travado) fica indefinidamente sem recuperação.
É o modo de falha mais difícil de diagnosticar porque o `WiFi.status()` diz que está tudo bem.
**Aplicado (V06):** `telemetryFailuresBeforeRelink = 10` derruba a associacao e deixa o `wifiTask` refaze-la.

### 3.4 Reboot do fluxômetro apaga o setpoint em silêncio — **risco mais sério**
`targetFlowSetpoint` não é persistido. Se o fluxômetro reiniciar (brownout no pico de TX), volta
com setpoint 0 e válvulas fechadas. Como não há comando aguardando ack, o Hub **adota o estado do
dispositivo** (`HttpServer.h`, ramo `if (!flowCommandAwaitingAck)`) e sobrescreve o desejado com 0.
A vazão de gás para, o app mostra 0 como se fosse o valor pedido, e o único rastro é um piscar de
"offline". Sintoma de campo: o `seconds` da telemetria reiniciando do zero.
**Aplicado, por outro caminho:** a V06 sorteia um `boot_id` a cada energizacao e o envia em toda
telemetria; o Hub guarda o ultimo e, ao ver o id **mudar**, reafirma o proprio estado desejado em vez
de adotar o do dispositivo (`HttpServer.h`, `rebootDetected`).
Deliberadamente **nao** persisti o `targetFlowSetpoint` na EEPROM como estava proposto aqui: isso faria
o fluxometro restaurar vazao de gas sozinho ao energizar, sem ninguem por perto. Com o `boot_id` quem
reafirma e o Hub, que so faz isso se ja estava acompanhando o no - e a primeira observacao de um id
apenas o registra, para um reboot isolado do Hub nao atropelar uma aeracao em curso.

## 4. Por que as perdas acontecem (ordem de probabilidade)

1. **12 conexões TCP novas por segundo.** Cada `/flowCommand` e cada `/flowData` faz
   `http.begin()` / `http.end()`, sem keep-alive. São ~12 handshakes + teardowns por segundo,
   cada um deixando socket em TIME_WAIT no lwIP do Hub, que ainda atende PC, biomassa, agitador,
   bomba e servo. Causa estrutural, não ambiental.
2. **Rádio único em AP+STA.** O fluxômetro mantém `Floxometro_AP` e a STA no mesmo rádio. Mesmo
   com canal 6 fixo (que já evita o pior), o airtime é dividido.
3. **Ambiente 2,4 GHz.** Canal fixo, sem seleção adaptativa; vaso metálico, inversor, gabinete.
   `WiFi.setSleep(false)` já está correto.
4. **Hub ocupado.** `readAndBroadcastSensorData` faz leituras UART sequenciais bloqueantes
   (250 ms de timeout cada, ~6 por ciclo) e, em falha, entra em cooldown de 5 s. Sob contenção
   a resposta pode passar dos timeouts do cliente e contar como falha.
5. **Alimentação.** Pico de TX ~300 mA; fonte/cabo marginais causam brownout → §3.4.

## 5. Avaliação do projeto

O **protocolo está bem projetado**: pull-based, comandos idempotentes com `cmd_id`, retry até ack,
revisão semeada por `esp_random()` no boot para rejeitar ack de sessão anterior, mailbox que não
limpa na leitura, mutex separando os dois `HTTPClient`, telemetria que omite campo velho em vez de
republicá-lo. As decisões difíceis já estão certas.

A fragilidade está em **transporte e recuperação**: polling caro demais, nenhum watchdog acima do
`WiFi.status()`, e nenhuma política explícita para o que o fluxômetro deve fazer quando ficar sozinho.

## 6. Plano de ação, por custo-benefício

| # | Mudanca | Onde | Estado |
|---|---|---|---|
| 1 | `setReuse(true)` e `HTTPClient` de vida longa nas duas tarefas | `httpTask` / `telemetryTask` | **aplicado** |
| 2 | Watchdog de link: 10 falhas seguidas derrubam a associacao | `telemetryTask` | **aplicado** |
| 3 | So alterna de hub apos 3 falhas no mesmo SSID | `wifiTask` | **aplicado** |
| 4 | `boot_id` por energizacao; o Hub reafirma o desejado em vez de adotar o zero | ambos os lados | **aplicado** |
| 5 | Poll de comando de 10 Hz para 4 Hz | `commandPollInterval` | **aplicado** |
| 6 | Politica de link perdido: **fail-in-place mantido**, com alarme no app | fluxometro + `AlarmService` | **decidido e aplicado** |
| 7 | `reconnectWifi` aceito no contrato Hub->fluxometro, e o flag fica visivel na telemetria | `Mailboxes.h`, `HttpServer.h`, `Telemetry.h`, app | **aplicado** |

## 7. Politica de link perdido: fail-in-place, com alarme

Decidido em 2026-09-10: **o fluxometro continua fail-in-place**. Ao perder o enlace, o laco
PI local segue rodando, o DAC segue escrevendo e as valvulas congelam no ultimo estado. Numa
linha de gas de biorreator isso e o comportamento certo - a cultura continua aerada enquanto
a rede se recupera, em vez de ser sufocada por uma falha de Wi-Fi.

O preco dessa escolha e que gas pode estar entrando no reator sem que nada no app consiga
fecha-lo. Isso nao pode acontecer em silencio, entao entrou o alarme
`AlarmId.UnsupervisedGasFlow` - **"Gas aberto sem supervisao"**, severidade **critica**,
2 s de atraso de ativacao, visivel na faixa de alarmes e no diario da aba Alarmes e Eventos.

Detalhes que importam na implementacao:

- **O app precisa lembrar.** O Hub para de publicar vazao, setpoint e valvulas assim que o
  no some, e o parser zera esses campos. Quando o fluxometro le offline, portanto, ja nao ha
  como enxergar o que as valvulas estao fazendo. O `AlarmService` guarda em
  `_gasOpenWhenLastSeen` se a rota estava aberta no ultimo quadro que ainda trazia estado de
  vazao. Como o no e fail-in-place, o que ele estava fazendo e o que ele continua fazendo.
- **A vazao medida conta aqui**, ao contrario do pre-voo do ensaio de potencia. La a pergunta
  e "alguem deixou uma valvula aberta", que so o estado comandado responde; aqui e "esta
  entrando gas agora", e uma leitura bem acima do residual e evidencia direta disso.
- **Nao e condicionado ao interruptor de roteamento**, diferente do alarme de fluxometro
  offline. Desligar um interruptor no app nao fecha valvula em um no que parou de ouvir.
- **Zera na desconexao**, seguindo a politica que o proprio `AlarmService` ja aplica ao
  `_lastSnapshot`: o que as valvulas faziam antes de uma lacuna nao e evidencia do que fazem
  depois dela.

## 8. Como verificar em bancada

1. **Keep-alive e carga:** com o ensaio rodando, o console do Hub nao deve mais registrar
   rajadas de falha de `/flowData` quando o PC e os outros nos estao ativos.
2. **Watchdog:** desligue o AP do Hub por ~10 s com o fluxometro associado. O log do
   fluxometro deve mostrar `Link is associated but mute; forcing reassociation.` e voltar
   sozinho, sem power-cycle.
3. **Backoff de SSID:** com o `ModuloTECNAL_2` desligado, o log deve mostrar
   `timed out (1/3)` e `(2/3)` no mesmo SSID antes de tentar o outro modulo.
4. **Reboot silencioso (o mais importante):** comande 5 L/min, confirme a vazao, e entao
   reinicie so o fluxometro pelo botao de reset. O Hub deve logar
   `Fluxometro reiniciou (boot_id=...); reenviando estado desejado` e a vazao deve voltar
   aos 5 L/min. Antes dessa correcao ela caia para zero em silencio.
5. **Kill switch:** com `{"reconnectWifi":0}` o flag some do painel; com
   `{"reconnectWifi":1}` volta - agora pelo Hub, sem cabo.
6. **Alarme de gas sem supervisao:** com 5 L/min confirmados no reator, desligue o AP do Hub
   ou tire o fluxometro da tomada. Em ~2 s a faixa de alarmes deve mostrar
   **"Gas aberto sem supervisao"** em vermelho, com registro na aba Alarmes e Eventos. Com a
   rota fechada, a mesma queda deve levantar so o alarme ambar de fluxometro offline.
