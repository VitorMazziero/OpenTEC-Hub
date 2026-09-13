# Bomba peristáltica

Versão ativa: **v3.11**. Placa de compilação: **ESP32**.

- Firmware: `firmware/peristaltic-pump`
- Aplicativo ativo: `apps/flutter`.
- Hardware: `hardware/`
- Evidências/testes: `tests/` quando aplicável
- Baseline preservado e histórico: `archive/`

## Começar

    Leia `docs/CURRENT_STATUS.md` antes de gravar hardware, `docs/PROTOCOL.md` antes de tocar em comunicação e `docs/VALIDATION.md` para os gates. Compile todos os dispositivos com `tools\Compile-ExternalDevices.ps1` a partir de `External-Devices`.

O firmware v3.11 usa uma calibração contínua em duas faixas definida por `(m_baixo, m_alto, St, Qt)`, persistida separadamente em `pump_cal` com CRC32. A reta 3.10 é migrada para dois trechos equivalentes. Perfis nomeados por mangueira pertencem ao aplicativo no PC; o nó mantém somente a última curva enviada. A integração de software foi testada com Hub 10.3 e OpenTEC-Hub; a validação volumétrica física continua pendente.
