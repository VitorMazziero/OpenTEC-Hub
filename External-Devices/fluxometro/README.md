# Fluxômetro

Versão ativa: **v12.0**. Placa de compilação: **ESP32**.

- Firmware: `firmware/flowmeter`
- Aplicativo ativo: `apps/flutter`.
- Hardware: `hardware/`
- Evidências/testes: `tests/` quando aplicável
- Baseline preservado e histórico: `archive/`

## Começar

    Leia `docs/CURRENT_STATUS.md` antes de gravar hardware, `docs/PROTOCOL.md` antes de tocar em comunicação e `docs/VALIDATION.md` para os gates. Compile todos os dispositivos com `tools\Compile-ExternalDevices.ps1` a partir de `External-Devices`.

O firmware v12.0 mantém o modelo polinomial em duas faixas e torna editável a tensão de transição `transition_v`. O default/migração é `0.0545 V`; a aplicação exige os dois segmentos completos, valida continuidade e inclui a transição no CRC32 do schema EEPROM v7. A integração de software foi testada com Hub 10.3 e OpenTEC-Hub; a validação física continua pendente.
