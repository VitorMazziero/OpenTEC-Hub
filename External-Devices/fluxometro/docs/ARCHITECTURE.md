# Arquitetura — Fluxômetro

O sketch principal é uma entrada mínima que delega a `core/FirmwareApp`. Responsabilidades ativas:

`core/Lifecycle.h`, `tasks/TaskRuntime.h`, `protocol/CommandCodec.h`, `hardware/FlowIo.h`, `storage/CalibrationStore.h`, `api/WebSocketApi.h`, `api/OtaService.h`

Nos firmwares extraídos para fragmentos privados, esses arquivos são incluídos por `FirmwareApp.cpp` para manter ordem de declaração, globais e semântica do monólito. A evolução para compilação independente está registrada em `../../docs/OPTIMIZATION_OPPORTUNITIES.md`.
