# Protocolo — Frasco agitador

## Contrato vigente

Registro `GET /agitatorHello`, pull `GET /agitatorCommand` e push `GET /agitatorData`; comandos usam parser manual e `cmd_id`/`ack_cmd_id`.

A identidade vigente é `v10`. A mesma constante alimenta `/nodeHello`, `/diag` e a página OTA; `rev H` designa apenas o baseline histórico arquivado.

Os nomes, tipos, unidades, sentinelas, rotas e semântica de confirmação são compatibilidade de fio. O código atual de `ESP32S3-HUB/ESP32S3-HUB` é a contraparte autoritativa. Alterações sugeridas ficam em `../../docs/Planos/HUB_PROTOCOL_IMPROVEMENTS.md` e não estão implementadas.

## JSON

Consulte a matriz transversal em `../../docs/Planos/HUB_PROTOCOL_IMPROVEMENTS.md`. Um HTTP 200 confirma recebimento da requisição; `ack_cmd_id` confirma a revisão aplicada, não necessariamente a conclusão física de um atuador.
