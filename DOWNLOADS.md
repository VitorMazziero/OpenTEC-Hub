# 📥 Downloads — OpenTEC-Hub

Aplicativos e firmware de cada equipamento. Os links apontam sempre para a **versão mais recente**.

| Equipamento | Aplicativo | Firmware | Código-fonte |
|---|---|---|---|
| **TECNAL-Hub** (central) | [🪟 Windows (.exe)](https://github.com/VitorMazziero/OpenTEC-Hub/releases/latest/download/TECNAL-Hub_Windows.exe)<br>[🤖 Android (.apk)](https://github.com/VitorMazziero/OpenTEC-Hub/releases/latest/download/TECNAL-Hub_Android.apk) | [📦 ESP32-S3 (.zip)](https://github.com/VitorMazziero/OpenTEC-Hub/releases/latest/download/TECNAL-Hub_firmware.zip) | [Hub](ESP32S3-HUB) · [Windows](Windows_app) · [Android](Android_app) |
| **Servo de agitação** (Delta ASDA-B2) | Controlado pelo app do TECNAL-Hub | [📦 ESP32-S3 (.zip)](https://github.com/VitorMazziero/OpenTEC-Hub/releases/latest/download/Servo_firmware.zip) | [ESP32S3-SERVO](ESP32S3-SERVO) |
| **Banho termostático** (Contemp C404) | [🤖 Android (.apk)](https://github.com/VitorMazziero/OpenTEC-Hub/releases/latest/download/Banho_Android.apk)<br>[🐍 Desktop Python (.zip)](https://github.com/VitorMazziero/OpenTEC-Hub/releases/latest/download/Banho_Desktop-Python.zip) | [📦 ESP32-S3 (.zip)](https://github.com/VitorMazziero/OpenTEC-Hub/releases/latest/download/Banho_firmware.zip) | [banho-termostatico](External-Devices/banho-termostatico) |
| **Bomba peristáltica** | [🤖 Android (.apk)](https://github.com/VitorMazziero/OpenTEC-Hub/releases/latest/download/Bomba_Android.apk)<br>[🪟 Windows (.zip)](https://github.com/VitorMazziero/OpenTEC-Hub/releases/latest/download/Bomba_Windows.zip) | [📦 ESP32 (.zip)](https://github.com/VitorMazziero/OpenTEC-Hub/releases/latest/download/Bomba_firmware.zip) | [bomba-peristaltica](External-Devices/bomba-peristaltica) |
| **Fluxômetro** | [🤖 Android (.apk)](https://github.com/VitorMazziero/OpenTEC-Hub/releases/latest/download/Fluxometro_Android.apk) | [📦 ESP32 (.zip)](https://github.com/VitorMazziero/OpenTEC-Hub/releases/latest/download/Fluxometro_firmware.zip) | [fluxometro](External-Devices/fluxometro) |
| **Frasco agitador** | [🤖 Android (.apk)](https://github.com/VitorMazziero/OpenTEC-Hub/releases/latest/download/Frasco-Agitador_Android.apk) | [📦 ESP32 (.zip)](https://github.com/VitorMazziero/OpenTEC-Hub/releases/latest/download/Frasco-Agitador_firmware.zip) | [frasco-agitador](External-Devices/frasco-agitador) |
| **Sensor de biomassa** | [🐍 Desktop Python (.zip)](https://github.com/VitorMazziero/OpenTEC-Hub/releases/latest/download/Sensor-Biomassa_Desktop-Python.zip) | [📦 ESP32-S3 (.zip)](https://github.com/VitorMazziero/OpenTEC-Hub/releases/latest/download/Sensor-Biomassa_firmware.zip) | [sensor-biomassa](External-Devices/sensor-biomassa) |
| **Sensor de distância** | Controlado pelo app do TECNAL-Hub | [📦 ESP32 (.zip)](https://github.com/VitorMazziero/OpenTEC-Hub/releases/latest/download/Sensor-Distancia_firmware.zip) | [sensor-distancia](External-Devices/sensor-distancia) |

📋 Histórico de versões e downloads anteriores: [Releases](https://github.com/VitorMazziero/OpenTEC-Hub/releases)

## Como instalar

**🪟 Windows (.exe)**: execute o instalador. Se o Windows mostrar "O Windows protegeu o computador",
clique em **Mais informações → Executar assim mesmo**. Os `.zip` de Windows não precisam de instalação:
extraia e abra o `.exe` de dentro da pasta.

**🤖 Android (.apk)**: baixe o arquivo pelo celular e abra-o. Na primeira vez, o Android pede para
permitir a instalação de apps desta fonte.

**🐍 Desktop Python (.zip)**: extraia e siga o `README.md` da pasta (é preciso ter o Python 3 instalado).

**📦 Firmware (.zip)**: extraia, abra o `.ino` na Arduino IDE 2 com o pacote **esp32** (Espressif),
escolha a placa indicada na tabela (ESP32 ou ESP32-S3) e clique em **Carregar**.

> ⚠️ Antes de regravar um equipamento em uso, leia o `README.md` dele no link de código-fonte.
> Firmware compilado não substitui a validação na bancada.

---

<img src="docs/qrcode-downloads.png" width="160" alt="QR code desta página"><br>
QR code desta página, para imprimir e colar nos equipamentos: [PNG](docs/qrcode-downloads.png) · [SVG](docs/qrcode-downloads.svg)

[← Página inicial do repositório](README.md)
