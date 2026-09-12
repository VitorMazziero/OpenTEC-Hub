# Protocolo — Sensor de distância

## Contrato vigente

Push `GET /distance?distance=<valor>&time=<segundos>`; não recebe comandos do Hub.

Os nomes, tipos, unidades, sentinelas, rotas e semântica de confirmação são compatibilidade de fio. O código atual de `ESP32S3-HUB/ESP32S3-HUB` é a contraparte autoritativa. Alterações sugeridas ficam em `../../docs/HUB_PROTOCOL_IMPROVEMENTS.md` e não estão implementadas.

## JSON

Consulte a matriz transversal em `../../docs/HUB_PROTOCOL_IMPROVEMENTS.md`. Um HTTP 200 confirma recebimento da requisição; `ack_cmd_id` confirma a revisão aplicada, não necessariamente a conclusão física de um atuador.
