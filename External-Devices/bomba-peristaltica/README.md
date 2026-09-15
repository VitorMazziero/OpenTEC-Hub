# Bomba peristáltica

Versão ativa: **v3.12**. Placa de compilação: **ESP32**.

- Firmware: `firmware/peristaltic-pump`
- Aplicativo ativo: `apps/flutter`.
- Hardware: `hardware/`
- Evidências/testes: `tests/` quando aplicável
- Baseline preservado e histórico: `archive/`

## Começar

    Leia `docs/CURRENT_STATUS.md` antes de gravar hardware, `docs/PROTOCOL.md` antes de tocar em comunicação e `docs/VALIDATION.md` para os gates. Compile todos os dispositivos com `tools\Compile-ExternalDevices.ps1` a partir de `External-Devices`.

O firmware v3.12 espelha o modelo do fluxômetro no domínio da velocidade: quarto grau abaixo de `St`, quadrático acima e continuidade C0+C1; `Qt=Q(St)` é derivado. A curva ativa é persistida em `pump_poly_cal` com CRC32. Esta é a primeira implantação: sem registro válido, o firmware permanece sem conversão Q↔S até receber a primeira curva; não existe migração ou fallback linear. Perfis nomeados por mangueira pertencem ao OpenTEC-Hub no PC; o nó mantém somente a última curva enviada. A integração foi testada com Hub 10.4 e OpenTEC-Hub; a validação volumétrica física continua pendente.
