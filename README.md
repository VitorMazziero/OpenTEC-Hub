# OpenTEC-Hub

Sistema de automação e controle para o módulo biorreator TECNAL. Um **Hub central (ESP32-S3)** concentra
os sensores e atuadores do biorreator e conversa, por Wi-Fi, com os **dispositivos externos**: servo de
agitação, banho termostático, bomba peristáltica, fluxômetro, frasco agitador, sensor de biomassa e
sensor de distância. O operador usa o **aplicativo Windows** ou o **aplicativo Android**.

> **📥 Downloads (aplicativos e firmware de cada equipamento): [DOWNLOADS.md](DOWNLOADS.md)**
>
> É a página aberta pelo QR code colado nos equipamentos ([PNG](docs/qrcode-downloads.png) · [SVG](docs/qrcode-downloads.svg)).

```text
                ┌──────────────────────┐
  App Windows ──┤                      ├── Servo de agitação (Delta ASDA-B2)
                │   TECNAL-Hub         ├── Banho termostático (Contemp C404)
  App Android ──┤   (ESP32-S3)         ├── Bomba peristáltica
                │                      ├── Fluxômetro
                └──────────────────────┘── Frasco agitador · Sensor de biomassa · Sensor de distância
```

## Módulos do repositório

### [`ESP32S3-HUB/`](ESP32S3-HUB) — firmware do Hub central

Firmware do controlador central do biorreator (ESP32-S3), protocolo 10.

| Pasta | Conteúdo |
|---|---|
| `ESP32S3-HUB/` | Sketch ativo (`ESP32S3-HUB.ino`, `Config.h`, `src/` modularizado) |
| `docs/` | Arquitetura, contrato HTTP e critérios de validação |
| `tests/` | Verificações executáveis do contrato |
| `tools/` | Scripts de compilação e verificação |
| `old/` | Versões históricas, preservadas sem edição |

### [`Windows_app/`](Windows_app) — aplicativo Windows

Aplicativo de operação do biorreator em C# / WPF (.NET 10), conectado ao Hub por USB ou Wi-Fi.

| Pasta | Conteúdo |
|---|---|
| `src/` | Código-fonte do aplicativo |
| `tests/` | Testes automatizados |
| `installer/` | Script do instalador (Inno Setup) |
| `docs/` | Roteiro, arquitetura e estado atual |
| `design/` | Protótipos e referências visuais |

### [`Android_app/`](Android_app) — aplicativo Android

Aplicativo Flutter para controle do biorreator pelo celular ou tablet, incluindo o cartão do banho externo C404.
O código fica em `lib/`, e o projeto Android em `android/`.

### [`ESP32S3-SERVO/`](ESP32S3-SERVO) — servo de agitação

Nó ESP32-S3 que comanda o servoacionamento Delta ASDA-B2 por Modbus RS-485.

| Pasta | Conteúdo |
|---|---|
| `Software/firmware-producao/` | Firmware a ser gravado (`ASDA_B2_Servo_Node`) |
| `Software/testes-bancada/` | Sketches de diagnóstico (não são firmware de produção) |
| `Software/documentacao/` | Diário de montagem e configuração Modbus |
| `CAD-conector-C3/` | Projeto mecânico do conector |
| raiz | Manuais do ASDA-B2 e planos de integração |

### [`External-Devices/`](External-Devices) — dispositivos externos

Um diretório por equipamento, todos com a mesma organização:

| Pasta | Conteúdo |
|---|---|
| `firmware/` | Somente a versão ativa e compilável |
| `apps/` | Aplicativo próprio do equipamento, quando existe |
| `hardware/` | CAD e documentação de hardware |
| `docs/` | Arquitetura, protocolo, validação e estado atual |
| `tests/` | Sketches de bancada e evidências |
| `archive/` | Versões antigas, preservadas para auditoria |

| Equipamento | Pasta | Placa | Aplicativo |
|---|---|---|---|
| Banho termostático (C404) | [`banho-termostatico/`](External-Devices/banho-termostatico) | ESP32-S3 | Android + Desktop (Python) |
| Bomba peristáltica | [`bomba-peristaltica/`](External-Devices/bomba-peristaltica) | ESP32 | Android + Windows |
| Fluxômetro | [`fluxometro/`](External-Devices/fluxometro) | ESP32 | Android |
| Frasco agitador | [`frasco-agitador/`](External-Devices/frasco-agitador) | ESP32 | Android |
| Sensor de biomassa | [`sensor-biomassa/`](External-Devices/sensor-biomassa) | ESP32-S3 | Desktop (Python) |
| Sensor de distância | [`sensor-distancia/`](External-Devices/sensor-distancia) | ESP32 | — |

Ferramentas comuns ficam em `External-Devices/tools/`, incluindo o atualizador OTA de firmware (`updater_app/`).
Veja o [README dos dispositivos externos](External-Devices/README.md).

## Gravar um firmware

1. Baixe o `.zip` do firmware em [DOWNLOADS.md](DOWNLOADS.md) e extraia. A pasta extraída tem o mesmo nome do `.ino`.
2. Abra o `.ino` na Arduino IDE 2 com o pacote **esp32** (Espressif) instalado.
3. Escolha a placa indicada na tabela de downloads (ESP32 ou ESP32-S3), a porta USB, e clique em **Carregar**.

Compilar não substitui a validação na bancada: leia o `README.md` e o `docs/` do equipamento antes de usá-lo em um cultivo.

## Publicar uma nova versão (mantenedores)

Os arquivos de download são publicados em [GitHub Releases](https://github.com/VitorMazziero/OpenTEC-Hub/releases)
sempre com os mesmos nomes. Assim, os links de [DOWNLOADS.md](DOWNLOADS.md), e o QR code, sempre levam à versão mais recente.

1. Compile os aplicativos (instalador Windows, APKs Flutter).
2. Rode `tools\Build-ReleaseAssets.ps1`. Ele gera a pasta `release-assets\` com os `.zip` de firmware e copia os aplicativos já compilados com os nomes certos.
3. No GitHub, crie uma nova release (**Releases → Draft a new release**) e anexe todo o conteúdo de `release-assets\`.
