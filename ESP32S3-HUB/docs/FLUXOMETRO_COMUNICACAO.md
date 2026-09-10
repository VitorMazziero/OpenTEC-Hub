# Comunicação Hub ↔ Fluxômetro — diagnóstico e plano de correção

> Status: **pendente**. Levantado em 2026-09-10 a partir da leitura do firmware dos dois
> lados. Nenhuma correção foi aplicada ainda. O link está operacional com as ressalvas
> abaixo; este documento existe para retomar o trabalho sem refazer a investigação.

Fontes lidas:

- `ESP32S3-HUB/ESP32S3-HUB/src/network/HttpServer.h`
- `ESP32S3-HUB/ESP32S3-HUB/src/protocol/Mailboxes.h`
- `ESP32S3-HUB/ESP32S3-HUB/src/sensor/Telemetry.h`
- `ESP32S3-HUB/ESP32S3-HUB/src/core/AppContext.h`
- `.../Fluxometro/Software/__controler_ESP32/flowmeter_TECNALHUB_V05/flowmeter_TECNALHUB_V05.ino`

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
**Correção:** só alternar depois de 2–3 falhas seguidas no SSID atual.

### 3.2 `reconnect_wifi` é um kill switch sem volta pelo Hub
A chave existe no parser do fluxômetro, mas o Hub **não a aceita** (`queueReliableFlowCommandFromJson`
não reconhece a chave e `buildFlowCommandLocked` nunca a emite). Se alguém desligar a reconexão
pela serial USB ou pelo WebSocket do `Floxometro_AP`, o fluxômetro nunca mais volta sozinho e não
há como religar pelo Hub nem pelo app — só com cabo ou entrando no AP dele. Também não é persistida
em EEPROM, então um reboot a devolve para `true` — o que salva, mas por acidente.
**Correção:** aceitar `reconnect_wifi` no contrato Hub→fluxômetro, ou remover a chave.

### 3.3 Não há watchdog de link em nível de aplicação
`telemetryFailures` é contado e logado e **nunca dispara nada**. O caso "`WL_CONNECTED` mas nenhum
HTTP passa" (AP lotado, lease perdido, socket travado) fica indefinidamente sem recuperação.
É o modo de falha mais difícil de diagnosticar porque o `WiFi.status()` diz que está tudo bem.
**Correção:** após N falhas consecutivas (~10 ≈ 5 s), forçar `WiFi.disconnect()` + reassociar.

### 3.4 Reboot do fluxômetro apaga o setpoint em silêncio — **risco mais sério**
`targetFlowSetpoint` não é persistido. Se o fluxômetro reiniciar (brownout no pico de TX), volta
com setpoint 0 e válvulas fechadas. Como não há comando aguardando ack, o Hub **adota o estado do
dispositivo** (`HttpServer.h`, ramo `if (!flowCommandAwaitingAck)`) e sobrescreve o desejado com 0.
A vazão de gás para, o app mostra 0 como se fosse o valor pedido, e o único rastro é um piscar de
"offline". Sintoma de campo: o `seconds` da telemetria reiniciando do zero.
**Correção:** persistir `targetFlowSetpoint` na EEPROM e enviar `boot=1` na primeira telemetria
após o boot, para o Hub reenviar o estado desejado em vez de adotar o do dispositivo.

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

| # | Mudança | Onde | Ganho | Decisão |
|---|---|---|---|---|
| 1 | `http.setReuse(true)` e HTTPClient de vida longa | `httpTask` / `telemetryTask` | corta ~12 handshakes/s para ~0 | aplicar |
| 2 | Após N falhas seguidas, `WiFi.disconnect()` + reassociar | `telemetryTask` | fecha o buraco "conectado mas mudo" | aplicar |
| 3 | Alternar `nextHubIndex` só após 2–3 falhas no mesmo SSID | `wifiTask` | reconexão típica de ~26 s → ~13 s | aplicar |
| 4 | Persistir `targetFlowSetpoint` + flag `boot=1` na telemetria | ambos os lados | elimina perda silenciosa de setpoint | aplicar |
| 5 | Poll de comando de 10 Hz → 2–4 Hz | `commandPollInterval` | menos tráfego, sem perda prática de resposta | aplicar |
| 6 | Política explícita de link perdido (manter estado por T min, depois fechar/zerar) | fluxômetro | — | **depende de decisão de processo** |
| 7 | Aceitar `reconnect_wifi` no contrato Hub→fluxômetro | `Mailboxes.h` + parser | remove o kill switch sem volta | avaliar junto com o item 6 |

Itens 1–3 e 5 não tocam no contrato de wire com o Hub. Itens 4, 6 e 7 tocam e exigem regravar
os dois firmwares juntos.
