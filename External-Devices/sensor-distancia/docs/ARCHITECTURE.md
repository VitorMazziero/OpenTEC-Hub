# Arquitetura — Sensor de distância

O sketch principal é uma entrada mínima que delega a `core/FirmwareApp`. Responsabilidades ativas:

`sensor/DistanceSensor`, `network/NetworkManager`, `protocol/ConfigCodec`, `api/LocalHttpApi`, `core/FirmwareApp`

Nos firmwares extraídos para fragmentos privados, esses arquivos são incluídos por `FirmwareApp.cpp` para manter ordem de declaração, globais e semântica do monólito. A evolução para compilação independente está registrada em `../../docs/OPTIMIZATION_OPPORTUNITIES.md`.
