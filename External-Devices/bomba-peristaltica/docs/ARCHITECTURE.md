# Arquitetura — Bomba peristáltica

O sketch principal é uma entrada mínima que delega a `core/FirmwareApp`. Responsabilidades ativas:

`hardware/PwmRuntime.h`, `control/OperationController.h`, `control/SensorAndConversion.h`, `storage/RuntimeStateStore.h`, `storage/ConfigStore.h`, `protocol/TelemetryCodec.h`, `network/HubClient.h`

Nos firmwares extraídos para fragmentos privados, esses arquivos são incluídos por `FirmwareApp.cpp` para manter ordem de declaração, globais e semântica do monólito. A evolução para compilação independente está registrada em `../../docs/OPTIMIZATION_OPPORTUNITIES.md`.
