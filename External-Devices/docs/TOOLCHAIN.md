# Toolchain reproduzível

## Ambiente validado em 2026-09-11

- Arduino CLI: 1.5.1
- ESP32 Arduino core: 3.3.11
- Bibliotecas compartilhadas: `D:\OneDrive\Documentos\Arduino\libraries`
- ArduinoJson: 7.4.3
- Async TCP: 3.5.0
- ESP Async WebServer: 3.12.0
- VL53L0X: 1.3.1
- Adafruit ADS1X15: 2.6.2
- Adafruit MCP4725: 2.0.2

O arquivo local `tools/arduino-cli.local.yaml` não é versionado porque contém caminhos absolutos. `Compile-ExternalDevices.ps1` cria/atualiza essa configuração sem instalar ou modificar bibliotecas compartilhadas.

O ArduinoJson foi integralmente removido do firmware ativo do frasco agitador, substituído pelo mesmo padrão de parsing e serialização manual de strings adotado no Hub, bomba, fluxômetro, biomassa e distância. Nenhum firmware ativo no repositório depende mais de bibliotecas JSON externas.

