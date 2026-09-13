# Sensor de biomassa

Versão ativa: **v11.1** (rótulo interno da reorganização de 2026-09-11: v5.3). Placa de compilação: **ESP32-S3**.

- Firmware: `firmware/biomass-sensor`
- Aplicativo ativo: `apps/desktop-python`.
- Hardware: `hardware/`
- Evidências/testes: `tests/` quando aplicável
- Baseline preservado e histórico: `archive/`

## Começar

    Leia `docs/CURRENT_STATUS.md` antes de gravar hardware, `docs/PROTOCOL.md` antes de tocar em comunicação e `docs/VALIDATION.md` para os gates. Compile todos os dispositivos com `tools\Compile-ExternalDevices.ps1` a partir de `External-Devices`.

O código ativo foi reorganizado sem mudança intencional de comportamento. O monólito original permanece em `archive/active-baseline` para comparação e os hashes importados estão em `archive/IMPORT_MANIFEST.sha256`.
