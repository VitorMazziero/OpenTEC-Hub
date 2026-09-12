# Arquitetura — Frasco agitador

O sketch principal é uma entrada mínima que delega a `core/FirmwareApp`. Responsabilidades ativas:

`motor/MotorDriver`, `input/Potentiometer`, `protocol/CommandCodec`, `network/NetworkManager`, `network/HubClient`, `api/LocalHttpApi`, `core/FirmwareApp`

Nos firmwares extraídos para fragmentos privados, esses arquivos são incluídos por `FirmwareApp.cpp` para manter ordem de declaração, globais e semântica do monólito. A evolução para compilação independente está registrada em `../../docs/OPTIMIZATION_OPPORTUNITIES.md`.
