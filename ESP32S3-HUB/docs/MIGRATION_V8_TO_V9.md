# Migração v8 para v9

## Recibo de origem

O commit `baseline-v8-organized` contém o seed original `TECNAL_ESP32_v9.ino` (renomeado posteriormente para `ESP32S3-HUB.ino`) byte a byte idêntico ao v8 arquivado. O SHA-256 de ambos naquele ponto é:

`B8AC4D63C07F23B29D41129F0AC01AC045C1E96C72FFB69229B41D0400CDE4B7`

Os dez snapshots históricos e seus tamanhos estão em `_old/LEGACY_SHA256.txt`. `_old` é somente leitura.

## Mudanças estruturais

1. O ponto de entrada foi reduzido a `setup()`/`loop()`.
2. O bloco v8 foi separado por responsabilidade, inicialmente numa única unidade de compilação para preservar comportamento.
3. Estado Servo, parsing estrito e filas HTTP/Servo viraram módulos compiláveis independentes.
4. Cache, `sampleId`, amostra Servo e estados de callbacks agora usam snapshots protegidos.

## Mudanças comportamentais intencionais

- `/command` enfileira o frame e o `loop` o aplica; sucesso continua HTTP 200.
- fila HTTP cheia retorna 503 antes de qualquer mutação.
- presença Servo não depende mais de `servoComm`.
- `/servoData` desligado deixa de retornar 403: push válido prova presença, mas não substitui valores.
- parâmetros Servo passam por conversão estrita.
- o único `String` Servo sobrescrevível foi substituído por FIFO fixa de oito comandos.
- novos campos de versão, roteamento e profundidade da fila entram em `/readData`.
- a busca de chave dos dois parsers passou a exigir posição de chave. O `indexOf`
  do v8 também casava dentro de um valor string: em
  `{"pump_command":"start","mode":2}` o Hub enxergava um `start` de biomassa e
  lia o `2` do `mode`, enfileirando um comando para outro dispositivo. Os
  quadros que o aplicativo emite dão resultado idêntico ao do v8.
- `distanceSensorValue` deixa de ser zerado quando a leitura envelhece. No v8,
  se o nó voltasse publicando o mesmo valor de antes, o filtro de estagnação
  nunca reaceitava a leitura e o sensor ficava offline para sempre. A validade
  continua decidida pela idade da amostra, tanto no `/readData` quanto no
  intertravamento de espuma.
- o intertravamento de espuma lê o estado do sensor de distância sob
  `stateMutex`, que é o mutex sob o qual `/distance` passou a escrevê-lo.
- `startWatchDog()` cai para `esp_task_wdt_reconfigure()` quando o core já
  inicializou o TWDT. Sem isso o `WDT_TIMEOUT` de 10 s declarado desde o v8
  nunca valia e o firmware rodava com os 5 s do core.

## Compatibilidade preservada

Nomes NVS, defaults, debounce, UART, endpoints, chaves v8, mailbox revisionada do fluxômetro e mailboxes com ACK de biomassa/bomba/agitador foram mantidos. O teste estático impede remoção acidental dos endpoints e campos retidos.

## Fora de escopo

O texto acima registra a migração v8→v9. Em 2026-09-04, a mudança coordenada
Hub 10 + ESP32S3-driver 2.0 acrescentou comando direto de rpm com `cmd_id`, ACK e
lease. O aplicativo preserva `motorSetpoint`; o protocolo interno do nó está em
`docs/WIRE_CONTRACT_V9.md` (nome de arquivo histórico) e o plano físico no
projeto `ESP32S3-SERVO/PLANO_MIGRACAO_RPM_MODBUS.md`.
