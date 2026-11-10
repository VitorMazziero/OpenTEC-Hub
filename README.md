<div align="center">

# OpenTEC-Hub

**Automação de biorreator de bancada: aplicativos e firmwares prontos para instalar**

[![Versão](https://img.shields.io/github/v/release/VitorMazziero/OpenTEC-Hub?label=vers%C3%A3o&sort=date)](https://github.com/VitorMazziero/OpenTEC-Hub/releases/latest)
[![Licença](https://img.shields.io/badge/licen%C3%A7a-PolyForm%20Noncommercial%201.0.0-blue)](LICENSE.md)

[**📥 Downloads**](DOWNLOADS.md) · [Instalar os aplicativos](docs/INSTALAR_APLICATIVOS.md) · [Gravar firmware](docs/GRAVAR_FIRMWARE.md) · [Versões](CHANGELOG.md)

</div>

---

O OpenTEC controla um biorreator de bancada a partir de uma central, o **TECNAL-Hub**, que conversa por Wi-Fi com seis equipamentos externos. O operador usa o aplicativo do Hub no Windows; cada equipamento também tem seu firmware e, em alguns casos, um app próprio para manutenção.

Este repositório é só para **baixar e instalar**. Cada versão publicada em [Releases](https://github.com/VitorMazziero/OpenTEC-Hub/releases) traz as imagens de firmware prontas para gravar, os aplicativos, os SHA-256 de todos os arquivos e o manifesto que o Atualizador confere antes de gravar.

> [!NOTE]
> A primeira versão desta série está em testes de bancada. Os links de download passam a funcionar quando ela for publicada.

## Equipamentos

| | Equipamento | Placa | Função |
|:-:|---|---|---|
| 🎛️ | **TECNAL-Hub** | ESP32-S3 | Central do módulo: lê os sensores do biorreator e coordena os equipamentos |
| ⚙️ | **Servo de agitação** (Delta ASDA-B2) | ESP32-S3 | Velocidade do agitador |
| 🌡️ | **Banho termostático** (Contemp C404) | ESP32-S3 | Temperatura |
| 💧 | **Bomba peristáltica** | ESP32 | Alimentação e dosagem |
| 🌬️ | **Fluxômetro** | ESP32 | Vazão de gás e válvulas |
| 🔬 | **Sensor de biomassa** | ESP32-S3 | Densidade óptica em linha |
| 📏 | **Sensor de distância** | ESP32 | Nível do líquido |

O Hub tem uma imagem para cada **módulo** (1 ou 2). Os seis equipamentos usam a mesma imagem em qualquer módulo e acompanham o Hub a que estão ligados.

## Como começar

1. **Instale o aplicativo do Hub** no Windows: [`OpenTECHub_Setup.exe`](https://github.com/VitorMazziero/OpenTEC-Hub/releases/latest/download/OpenTECHub_Setup.exe). Guia: [Instalar os aplicativos](docs/INSTALAR_APLICATIVOS.md).
2. **Atualize os firmwares** com o Atualizador: [`OpenTEC-Updater.zip`](https://github.com/VitorMazziero/OpenTEC-Hub/releases/latest/download/OpenTEC-Updater.zip). Ele confere cada arquivo e cada placa antes de gravar. Guia: [Gravar firmware](docs/GRAVAR_FIRMWARE.md).
3. **Conecte** o computador ao Hub pela USB ou pela rede Wi-Fi `ModuloTECNAL_1` / `ModuloTECNAL_2`.

Todos os arquivos, um a um: [**DOWNLOADS.md**](DOWNLOADS.md).

## Licença

[PolyForm Noncommercial 1.0.0](LICENSE.md). Uso, cópia e modificação livres para fins **não comerciais**: pesquisa, ensino, uso pessoal, instituições de ensino e pesquisa, órgãos públicos e entidades sem fins lucrativos. Uso comercial depende de autorização do autor.
