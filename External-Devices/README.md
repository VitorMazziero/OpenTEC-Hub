# Dispositivos externos

Este diretório reúne firmware, aplicativos, hardware, evidências e histórico dos cinco dispositivos externos integrados ao `ESP32S3-HUB` e do banho termostático (`banho-termostatico`: nó r3 e app Android próprio prontos em software, ainda sem integração implementada no Hub).

| Dispositivo | Firmware ativo | Aplicativo |
|---|---|---|
| Bomba peristáltica | `bomba-peristaltica/firmware/peristaltic-pump` | Flutter |
| Fluxômetro | `fluxometro/firmware/flowmeter` | Flutter v05 |
| Frasco agitador | `frasco-agitador/firmware/flask-agitator` | Flutter |
| Sensor de biomassa | `sensor-biomassa/firmware/biomass-sensor` | Desktop Python |
| Sensor de distância | `sensor-distancia/firmware/distance-sensor` | — |
| Banho termostático (C404) | `banho-termostatico/firmware/thermostatic-bath` | Desktop Python (bancada) + Android (Flutter) |

## Documentação transversal

- `docs/COMANDOS_DISPOSITIVOS_EXTERNOS.md` — por dispositivo: interações que o nó aceita, o que o firmware faz com cada uma e o que o hardware deve fazer; conferido no código, não nos protocolos. §1–§6 completos (2026-09-13).
- `docs/Planos/` — planos de implementação por dispositivo, `HUB_PROTOCOL_IMPROVEMENTS.md`, `OPTIMIZATION_OPPORTUNITIES.md`, `IMPLEMENTATION_REPORT.md`; `docs/TOOLCHAIN.md`.

## Regras

- `firmware/` contém somente a versão ativa e compilável.
- `archive/active-baseline/` preserva o monólito de origem, sem edição, para auditoria.
- `archive/legacy/` contém versões históricas e sketches antigos.
- `tests/bench/` contém sketches de bancada, nunca firmware de produção.
- `tests/evidence/` contém resultados e dados de caracterização.
- `hardware/cad/source/` contém fontes mecânicas; binários grandes são tratados por Git LFS.
- Cada dispositivo mantém `README.md`, `CHANGELOG.md` e documentação de arquitetura, protocolo, hardware, validação e estado atual.

## Atualização de firmware

Pela interface, para os seis dispositivos de uma vez — estado dos nós, compilação e gravação OTA:

```powershell
python External-Devices\tools\updater_app\app.py
```

Por linha de comando, um dispositivo por vez: `tools\Publish-OtaFirmware.ps1 <dispositivo> [-Compile]`. Os dois usam o mesmo catálogo de endereços, FQBN e caminhos de sketch; ver [o aplicativo](tools/updater_app/README.md).

## Verificação

Execute no PowerShell, na raiz do repositório:

```powershell
External-Devices\tools\Test-FirmwareBaselines.ps1
External-Devices\tools\Test-HubDeviceContracts.ps1
External-Devices\tools\Compile-ExternalDevices.ps1
```

A compilação usa as bibliotecas compartilhadas em `D:\OneDrive\Documentos\Arduino\libraries`. Build e testes estáticos não substituem validação de bancada.

## Documentos transversais

- [Convenções](CONVENTIONS.md)
- [Plano executado](PLANO_REORGANIZACAO.md)
- [Oportunidades de otimização](docs/Planos/OPTIMIZATION_OPPORTUNITIES.md)
- [Propostas para o protocolo do Hub](docs/Planos/HUB_PROTOCOL_IMPROVEMENTS.md)
- [Toolchain](docs/TOOLCHAIN.md)
- [Relatório da implementação](docs/Planos/IMPLEMENTATION_REPORT.md)
