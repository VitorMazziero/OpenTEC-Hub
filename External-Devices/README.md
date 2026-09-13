# Dispositivos externos

Este diretório reúne firmware, aplicativos, hardware, evidências e histórico dos cinco dispositivos externos integrados ao `ESP32S3-HUB`.

| Dispositivo | Firmware ativo | Aplicativo |
|---|---|---|
| Bomba peristáltica | `bomba-peristaltica/firmware/peristaltic-pump` | Flutter |
| Fluxômetro | `fluxometro/firmware/flowmeter` | Flutter v05 |
| Frasco agitador | `frasco-agitador/firmware/flask-agitator` | Flutter |
| Sensor de biomassa | `sensor-biomassa/firmware/biomass-sensor` | Desktop Python |
| Sensor de distância | `sensor-distancia/firmware/distance-sensor` | — |

## Documentação transversal

- `docs/COMANDOS_DISPOSITIVOS_EXTERNOS.md` — por dispositivo: interações que o nó aceita, o que o firmware faz com cada uma e o que o hardware deve fazer; conferido no código, não nos protocolos. Bomba peristáltica completa em 2026-09-12; demais nós a preencher.
- `docs/HUB_PROTOCOL_IMPROVEMENTS.md`, `docs/OPTIMIZATION_OPPORTUNITIES.md`, `docs/TOOLCHAIN.md`.

## Regras

- `firmware/` contém somente a versão ativa e compilável.
- `archive/active-baseline/` preserva o monólito de origem, sem edição, para auditoria.
- `archive/legacy/` contém versões históricas e sketches antigos.
- `tests/bench/` contém sketches de bancada, nunca firmware de produção.
- `tests/evidence/` contém resultados e dados de caracterização.
- `hardware/cad/source/` contém fontes mecânicas; binários grandes são tratados por Git LFS.
- Cada dispositivo mantém `README.md`, `CHANGELOG.md` e documentação de arquitetura, protocolo, hardware, validação e estado atual.

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
- [Oportunidades de otimização](docs/OPTIMIZATION_OPPORTUNITIES.md)
- [Propostas para o protocolo do Hub](docs/HUB_PROTOCOL_IMPROVEMENTS.md)
- [Toolchain](docs/TOOLCHAIN.md)
- [Relatório da implementação](docs/IMPLEMENTATION_REPORT.md)
