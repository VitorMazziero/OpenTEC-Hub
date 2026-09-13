# Firmwares dos dispositivos externos

> Atualizado em 2026-09-11. O código passou a fazer parte deste repositório em `External-Devices/`. Este documento indica o que compilar; não autoriza gravação sem os gates de bancada.

## Seleção atual

| Dispositivo | Firmware ativo | Placa/FQBN base | Estado |
|---|---|---|---|
| Hub ESP32-S3 | `ESP32S3-HUB/ESP32S3-HUB` | configuração própria do Hub | código atual é o contrato autoritativo |
| Bomba peristáltica | `External-Devices/bomba-peristaltica/firmware/peristaltic-pump` | `esp32:esp32:esp32` | compila; bancada pendente |
| Fluxômetro | `External-Devices/fluxometro/firmware/flowmeter` | `esp32:esp32:esp32` com opções preservadas do build importado | compila; bancada pendente |
| Frasco agitador | `External-Devices/frasco-agitador/firmware/flask-agitator` | `esp32:esp32:esp32` | compila; bancada pendente |
| Sensor de biomassa | `External-Devices/sensor-biomassa/firmware/biomass-sensor` | `esp32:esp32:esp32s3` | compila; bancada pendente |
| Sensor de distância | `External-Devices/sensor-distancia/firmware/distance-sensor` | `esp32:esp32:esp32` | compila; bancada pendente |

As versões monolíticas que originaram a reorganização estão em `archive/active-baseline` dentro de cada dispositivo. Versões anteriores e sketches experimentais ficam em `archive/legacy` ou `tests/bench` e não devem ser gravados por engano.

## Ambiente de compilação

Execute na raiz do repositório:

```powershell
External-Devices\tools\Compile-ExternalDevices.ps1
External-Devices\tools\Test-HubDeviceContracts.ps1
```

O script usa ESP32 Arduino core 3.3.11 e as bibliotecas compartilhadas em `D:\OneDrive\Documentos\Arduino\libraries`. Os cinco firmwares ativos usam parsers manuais e não dependem de ArduinoJson.

## Ordem de integração em bancada

1. Registrar hash/commit do Hub e de cada firmware.
2. Compilar os cinco nós e executar o teste estático de contrato.
3. Gravar primeiro o Hub que contém as rotas documentadas.
4. Gravar um nó por vez, confirmando presença e telemetria antes do próximo.
5. Testar `cmd_id`/`ack_cmd_id`, pacote perdido e reinício para bomba, fluxômetro, biomassa e agitador.
6. Confirmar estados físicos seguros e realizar soak test.

## Critérios essenciais

- Distância: `GET /distance`, sentinela e temporização de intertravamento preservadas.
- Fluxômetro: `/flowCommand` e `/flowData`, estados das três válvulas, calibração, `boot_id` e recibos diretos/Hub.
- Biomassa: `/biomassCommand` e `/biomassData`, heartbeat `idle=1`, blanking idempotente.
- Bomba: `/pumpCommand` e `/pumpData`, perfil de dosagem não reiniciado por reentrega e parada física confirmada.
- Agitador: `/agitatorHello`, `/agitatorCommand`, `/agitatorData`, autoridade do potenciômetro e parada segura.

Documentação detalhada fica em `External-Devices/<dispositivo>/docs/`. Compilação e testes de contrato não encerram os gates de hardware.
