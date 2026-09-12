# Arquitetura — Sensor de biomassa

O sketch principal é uma entrada mínima que delega a `core/FirmwareApp`. Responsabilidades ativas:

`sensor/Veml7700Driver.h`, `measurement/MeasurementPipeline.h`, `measurement/BlankingAndRange.h`, `filtering/SampleFilter.h`, `history/SampleHistory.h`, `storage/Stores.h`, `protocol/CommandCodec.h`, `protocol/TelemetryAndHub.h`, `api/LocalHttpApi.h`

Nos firmwares extraídos para fragmentos privados, esses arquivos são incluídos por `FirmwareApp.cpp` para manter ordem de declaração, globais e semântica do monólito. A evolução para compilação independente está registrada em `../../docs/OPTIMIZATION_OPPORTUNITIES.md`.
