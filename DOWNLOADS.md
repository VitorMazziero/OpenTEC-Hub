<div align="center">

# 📥 Downloads

[![Versão](https://img.shields.io/github/v/release/VitorMazziero/OpenTEC-Hub?label=vers%C3%A3o&sort=date)](https://github.com/VitorMazziero/OpenTEC-Hub/releases/latest)

Os links apontam sempre para a **versão mais recente**. Versões anteriores em [Releases](https://github.com/VitorMazziero/OpenTEC-Hub/releases).

</div>

> [!NOTE]
> A primeira versão desta série está em testes de bancada. Os links passam a funcionar quando ela for publicada.

## 🖥️ Aplicativos

| Aplicativo | Para quê | Sistema | Download |
|---|---|---|---|
| **TECNAL-Hub** | Operação do biorreator (aplicativo principal) | Windows 10/11, 64 bits | [⬇️ OpenTECHub_Setup.exe](https://github.com/VitorMazziero/OpenTEC-Hub/releases/latest/download/OpenTECHub_Setup.exe) |
| **Atualizador** | Baixar, conferir e gravar os firmwares | Windows, Python 3.12 | [⬇️ OpenTEC-Updater.zip](https://github.com/VitorMazziero/OpenTEC-Hub/releases/latest/download/OpenTEC-Updater.zip) |
| **TECNAL-Hub para Android** | Acompanhar e operar o Hub pelo celular | Android | [⬇️ OpenTEC-Hub_Android.apk](https://github.com/VitorMazziero/OpenTEC-Hub/releases/latest/download/OpenTEC-Hub_Android.apk) |
| **Estação do sensor de biomassa** | Calibração e aquisição direta do sensor | Windows, 64 bits | [⬇️ Biomassa_Windows_Setup.exe](https://github.com/VitorMazziero/OpenTEC-Hub/releases/latest/download/Biomassa_Windows_Setup.exe) |

<details>
<summary><b>Apps diretos de manutenção (Android)</b>: falam com o equipamento pela rede própria dele, sem o Hub</summary>

<br>

| Equipamento | Download |
|---|---|
| 🌬️ Fluxômetro | [⬇️ Fluxometro_Android.apk](https://github.com/VitorMazziero/OpenTEC-Hub/releases/latest/download/Fluxometro_Android.apk) |
| 💧 Bomba peristáltica | [⬇️ Bomba_Android.apk](https://github.com/VitorMazziero/OpenTEC-Hub/releases/latest/download/Bomba_Android.apk) |
| 🌡️ Banho termostático | [⬇️ Banho_Android.apk](https://github.com/VitorMazziero/OpenTEC-Hub/releases/latest/download/Banho_Android.apk) |

</details>

## 🔌 Firmwares

Grave pelo [**Atualizador**](docs/GRAVAR_FIRMWARE.md): ele baixa a versão, confere o SHA-256 e a placa (tipo, MAC e, no Hub, o módulo) antes de gravar. Para atualizar por Wi-Fi basta a imagem da aplicação.

| | Equipamento | Placa | Imagem (`.bin`) |
|:-:|---|---|---|
| 🎛️ | **TECNAL-Hub — módulo 1** | ESP32-S3 | [⬇️ hub-modulo1.ino.bin](https://github.com/VitorMazziero/OpenTEC-Hub/releases/latest/download/hub-modulo1.ino.bin) |
| 🎛️ | **TECNAL-Hub — módulo 2** | ESP32-S3 | [⬇️ hub-modulo2.ino.bin](https://github.com/VitorMazziero/OpenTEC-Hub/releases/latest/download/hub-modulo2.ino.bin) |
| ⚙️ | Servo de agitação | ESP32-S3 | [⬇️ servo-drive.ino.bin](https://github.com/VitorMazziero/OpenTEC-Hub/releases/latest/download/servo-drive.ino.bin) |
| 🌡️ | Banho termostático | ESP32-S3 | [⬇️ thermostatic-bath.ino.bin](https://github.com/VitorMazziero/OpenTEC-Hub/releases/latest/download/thermostatic-bath.ino.bin) |
| 💧 | Bomba peristáltica | ESP32 | [⬇️ peristaltic-pump.ino.bin](https://github.com/VitorMazziero/OpenTEC-Hub/releases/latest/download/peristaltic-pump.ino.bin) |
| 🌬️ | Fluxômetro | ESP32 | [⬇️ flowmeter.ino.bin](https://github.com/VitorMazziero/OpenTEC-Hub/releases/latest/download/flowmeter.ino.bin) |
| 🔬 | Sensor de biomassa | ESP32-S3 | [⬇️ biomass-sensor.ino.bin](https://github.com/VitorMazziero/OpenTEC-Hub/releases/latest/download/biomass-sensor.ino.bin) |
| 📏 | Sensor de distância | ESP32 | [⬇️ distance-sensor.ino.bin](https://github.com/VitorMazziero/OpenTEC-Hub/releases/latest/download/distance-sensor.ino.bin) |

> [!IMPORTANT]
> A imagem do Hub de um módulo **não serve** no outro. Nunca renomeie um arquivo de módulo 1 para módulo 2.

<details>
<summary><b>Primeira gravação por USB</b>: bootloader e tabela de partições</summary>

<br>

Só para uma placa nova ou para recuperar uma placa que não liga. O Atualizador usa estes arquivos junto com a imagem da aplicação.

| Equipamento | Bootloader | Partições |
|---|---|---|
| TECNAL-Hub — módulo 1 | [hub-modulo1.ino.bootloader.bin](https://github.com/VitorMazziero/OpenTEC-Hub/releases/latest/download/hub-modulo1.ino.bootloader.bin) | [hub-modulo1.ino.partitions.bin](https://github.com/VitorMazziero/OpenTEC-Hub/releases/latest/download/hub-modulo1.ino.partitions.bin) |
| TECNAL-Hub — módulo 2 | [hub-modulo2.ino.bootloader.bin](https://github.com/VitorMazziero/OpenTEC-Hub/releases/latest/download/hub-modulo2.ino.bootloader.bin) | [hub-modulo2.ino.partitions.bin](https://github.com/VitorMazziero/OpenTEC-Hub/releases/latest/download/hub-modulo2.ino.partitions.bin) |
| Servo de agitação | [servo-drive.ino.bootloader.bin](https://github.com/VitorMazziero/OpenTEC-Hub/releases/latest/download/servo-drive.ino.bootloader.bin) | [servo-drive.ino.partitions.bin](https://github.com/VitorMazziero/OpenTEC-Hub/releases/latest/download/servo-drive.ino.partitions.bin) |
| Banho termostático | [thermostatic-bath.ino.bootloader.bin](https://github.com/VitorMazziero/OpenTEC-Hub/releases/latest/download/thermostatic-bath.ino.bootloader.bin) | [thermostatic-bath.ino.partitions.bin](https://github.com/VitorMazziero/OpenTEC-Hub/releases/latest/download/thermostatic-bath.ino.partitions.bin) |
| Bomba peristáltica | [peristaltic-pump.ino.bootloader.bin](https://github.com/VitorMazziero/OpenTEC-Hub/releases/latest/download/peristaltic-pump.ino.bootloader.bin) | [peristaltic-pump.ino.partitions.bin](https://github.com/VitorMazziero/OpenTEC-Hub/releases/latest/download/peristaltic-pump.ino.partitions.bin) |
| Fluxômetro | [flowmeter.ino.bootloader.bin](https://github.com/VitorMazziero/OpenTEC-Hub/releases/latest/download/flowmeter.ino.bootloader.bin) | [flowmeter.ino.partitions.bin](https://github.com/VitorMazziero/OpenTEC-Hub/releases/latest/download/flowmeter.ino.partitions.bin) |
| Sensor de biomassa | [biomass-sensor.ino.bootloader.bin](https://github.com/VitorMazziero/OpenTEC-Hub/releases/latest/download/biomass-sensor.ino.bootloader.bin) | [biomass-sensor.ino.partitions.bin](https://github.com/VitorMazziero/OpenTEC-Hub/releases/latest/download/biomass-sensor.ino.partitions.bin) |
| Sensor de distância | [distance-sensor.ino.bootloader.bin](https://github.com/VitorMazziero/OpenTEC-Hub/releases/latest/download/distance-sensor.ino.bootloader.bin) | [distance-sensor.ino.partitions.bin](https://github.com/VitorMazziero/OpenTEC-Hub/releases/latest/download/distance-sensor.ino.partitions.bin) |

</details>

## ✅ Conferir os arquivos

Cada versão traz `SHA256SUMS.txt`. No Windows (PowerShell):

```powershell
Get-FileHash .\hub-modulo2.ino.bin -Algorithm SHA256
```

O valor deve ser igual ao da linha do arquivo em `SHA256SUMS.txt`. O Atualizador faz essa conferência sozinho.
