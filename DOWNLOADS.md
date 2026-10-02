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

Para compilar a partir do código, gravar por USB ou por Wi-Fi e gerar os instaladores, veja
[Compilar, atualizar e instalar a partir do código](#compilar-atualizar-e-instalar-a-partir-do-código).

---

<img src="docs/qrcode-downloads.png" width="160" alt="QR code desta página"><br>
QR code desta página, para imprimir e colar nos equipamentos: [PNG](docs/qrcode-downloads.png) · [SVG](docs/qrcode-downloads.svg)

---

## Compilar, atualizar e instalar a partir do código

Esta seção é para quem altera o código. Os comandos rodam no **PowerShell, a partir da raiz do
repositório** (a pasta que contém este arquivo). Eles só geram arquivos locais ou gravam no
equipamento; nada é publicado, exceto no passo 7.

### 0. Ferramentas (uma vez por PC)

| Ferramenta | Versão usada | Para quê |
|---|---|---|
| [Arduino CLI](https://arduino.github.io/arduino-cli/latest/installation/) | 1.5.1, no `PATH` ou copiado em `ESP32S3-HUB\tools\.bin\` e `External-Devices\tools\.bin\` (essas pastas ficam fora do git) | compilar e gravar firmware |
| Pacote **esp32** (Espressif) | 3.3.11 | placas ESP32 e ESP32-S3 |
| .NET SDK | 10.0.100 ou mais novo (`Windows_app\global.json`) | app Windows |
| Inno Setup | 6 ou 7 | instalador do app Windows |
| Flutter | 3.44 | apps Android e Windows dos equipamentos |
| Python | 3.12 ou mais novo | apps desktop, atualizador e testes |

Nos comandos abaixo, `$cli` é o Arduino CLI. Defina essa variável em cada janela nova do
PowerShell:

```powershell
$cli = "arduino-cli"                                   # se estiver no PATH
$cli = ".\ESP32S3-HUB\tools\.bin\arduino-cli.exe"      # ou a cópia local
& $cli config add board_manager.additional_urls https://espressif.github.io/arduino-esp32/package_esp32_index.json
& $cli core update-index
& $cli core install esp32:esp32@3.3.11
```

O Hub baixa as próprias bibliotecas pelo `sketch.yaml`. Os seis nós externos usam a pasta de
bibliotecas compartilhada da Arduino IDE. O primeiro `Compile-ExternalDevices.ps1` cria a
configuração `External-Devices\tools\arduino-cli.local.yaml` e diz se falta alguma biblioteca.
Nesse caso, instale as bibliotecas e rode de novo:

```powershell
.\External-Devices\tools\Compile-ExternalDevices.ps1 -SharedArduinoRoot "D:\OneDrive\Documentos\Arduino"
& $cli lib install --config-file .\External-Devices\tools\arduino-cli.local.yaml "Async TCP@3.5.0" "ESP Async WebServer@3.12.0" "VL53L0X@1.3.1" "Adafruit ADS1X15@2.6.2" "Adafruit MCP4725@2.0.2"
```

Troque `-SharedArduinoRoot` pela pasta `Arduino` da Arduino IDE deste PC. A mesma pasta entra
em `-LibrariesPath` no passo 3.

### O que rodar depois de cada alteração

| Alterou | Compilar | Gravar | Onde |
|---|---|---|---|
| `ESP32S3-HUB/` | passo 1 | passo 3 (Wi-Fi) ou passo 2 (USB) | Hub |
| `External-Devices/<equipamento>/firmware/` | passo 1 | passo 3 (Wi-Fi) ou passo 2 (USB) | nó do equipamento |
| `ESP32S3-SERVO/Software/firmware-producao/` | passo 1 | passo 2 (só USB) | nó do servo |
| `Windows_app/` | passo 4 | passo 4 | PC |
| `Android_app/` ou `External-Devices/<equipamento>/apps/flutter/` | passo 5 | passo 5 | celular ou PC |
| `External-Devices/*/apps/desktop-python/` | passo 6 (roda direto) | — | PC |
| qualquer um, para atualizar os links desta página | — | passo 7 | GitHub |

### 1. Firmware: compilar

```powershell
# TECNAL-Hub (ESP32-S3): gera ESP32S3-HUB\build\ota\ESP32S3-HUB.ino.bin
.\ESP32S3-HUB\tools\compile.ps1 -OutputDir .\ESP32S3-HUB\build\ota

# Servo ASDA-B2 (ESP32-S3): gera ESP32S3-SERVO\Software\firmware-producao\build\
& $cli compile --fqbn esp32:esp32:esp32s3 --output-dir .\ESP32S3-SERVO\Software\firmware-producao\build .\ESP32S3-SERVO\Software\firmware-producao\ASDA_B2_Servo_Node

# Os seis nós externos de uma vez: gera External-Devices\tools\.build\<nó>\
.\External-Devices\tools\Compile-ExternalDevices.ps1 -SharedArduinoRoot "D:\OneDrive\Documentos\Arduino"
```

Nomes dos nós externos, usados nos passos 2 e 3:

| Equipamento | `<nó>` | Placa (FQBN) | Pasta do sketch | AP próprio |
|---|---|---|---|---|
| Banho termostático | `bath` | `esp32:esp32:esp32s3` | `External-Devices\banho-termostatico\firmware\thermostatic-bath` | 192.168.8.1 |
| Bomba peristáltica | `pump` | `esp32:esp32:esp32` | `External-Devices\bomba-peristaltica\firmware\peristaltic-pump` | 192.168.6.1 |
| Fluxômetro | `flowmeter` | `esp32:esp32:esp32` | `External-Devices\fluxometro\firmware\flowmeter` | 192.168.10.1 |
| Frasco agitador | `agitator` | `esp32:esp32:esp32` | `External-Devices\frasco-agitador\firmware\flask-agitator` | 192.168.4.1 |
| Sensor de biomassa | `biomass` | `esp32:esp32:esp32s3` | `External-Devices\sensor-biomassa\firmware\biomass-sensor` | 192.168.7.1 |
| Sensor de distância | `distance` | `esp32:esp32:esp32` | `External-Devices\sensor-distancia\firmware\distance-sensor` | 192.168.5.1 |

Testes sem hardware, antes de gravar:

```powershell
python .\ESP32S3-HUB\tools\verify_contract.py
python -m unittest discover -s .\ESP32S3-HUB\tests\contracts
.\ESP32S3-HUB\tests\host-bath-cascade\build.cmd
.\External-Devices\tools\Test-FirmwareBaselines.ps1
.\External-Devices\tools\Test-HubDeviceContracts.ps1
```

### 2. Firmware: gravar por USB

Conecte a placa por USB e descubra a porta (`COM5` nos exemplos) com `& $cli board list`.
Compile antes (passo 1).

```powershell
# TECNAL-Hub
& $cli upload -p COM5 --fqbn esp32:esp32:esp32s3 --input-dir .\ESP32S3-HUB\build\ota .\ESP32S3-HUB\ESP32S3-HUB

# Servo ASDA-B2: grave antes do Hub (ordem em ESP32S3-HUB\docs\VALIDATION.md)
& $cli upload -p COM5 --fqbn esp32:esp32:esp32s3 --input-dir .\ESP32S3-SERVO\Software\firmware-producao\build .\ESP32S3-SERVO\Software\firmware-producao\ASDA_B2_Servo_Node

# Nó externo (exemplo: banho; troque <nó>, FQBN e pasta pela tabela do passo 1)
& $cli upload -p COM5 --config-file .\External-Devices\tools\arduino-cli.local.yaml --fqbn esp32:esp32:esp32s3 --input-dir .\External-Devices\tools\.build\bath .\External-Devices\banho-termostatico\firmware\thermostatic-bath
```

A Arduino IDE também serve: abra o `.ino`, escolha a placa e clique em **Carregar**. Mantenha o
esquema de partições padrão (**Default 4MB with spiffs**), que é o que permite a atualização
por Wi-Fi.

### 3. Firmware: atualizar por Wi-Fi (OTA)

Não precisa de cabo. O PC fica na rede Wi-Fi do Hub (`ModuloTECNAL_1`/`_2`) ou na rede própria
do nó. A imagem só é trocada depois de verificada; se o envio for recusado ou interrompido, o
equipamento continua com o firmware atual.

```powershell
# TECNAL-Hub (a partir do 10.7; a primeira gravação do 10.7 é por USB).
# Compila e envia para 192.168.4.1. Recusa com a cascata do banho ativa ou o motor girando.
.\ESP32S3-HUB\tools\ota_upload.ps1
.\ESP32S3-HUB\tools\ota_upload.ps1 -SkipCompile        # reenvia a última imagem compilada

# Nó externo: compila e envia. O IP vem do Hub (/nodes); sem Hub, usa o AP do nó.
.\External-Devices\tools\Publish-OtaFirmware.ps1 bath -Compile -LibrariesPath "D:\OneDrive\Documentos\Arduino\libraries"
.\External-Devices\tools\Publish-OtaFirmware.ps1 pump 192.168.4.23     # IP manual, imagem já compilada

# Todos os nós pela interface gráfica (um de cada vez, com a versão de cada um)
Push-Location .\External-Devices\tools\updater_app
python -m pip install -r requirements.txt
python app.py
Pop-Location
```

Pelo navegador também funciona: abra `http://<ip>/update` e envie o `.ino.bin`, nunca o
`merged`, `bootloader` ou `partitions`. O **servo ASDA-B2 não tem OTA**; ele é gravado só por
USB (passo 2).

### 4. App Windows do TECNAL-Hub

```powershell
dotnet test .\Windows_app\OpenTECHub.slnx                 # testes
dotnet run --project .\Windows_app\src\OpenTECHub          # abre sem instalar
.\Windows_app\installer\build_installer.ps1                # publica e gera Windows_app\installer\Output\OpenTECHub_Setup_v<versão>.exe

# Instalar a versão gerada neste PC
Start-Process (Get-ChildItem .\Windows_app\installer\Output\OpenTECHub_Setup_v*.exe | Sort-Object LastWriteTime | Select-Object -Last 1).FullName
```

A suíte de testes regrava as capturas em `Windows_app\docs\evidence`. Se não quiser commitar
essas capturas, descarte com `git checkout -- Windows_app/docs/evidence`.

### 5. Apps Flutter (Android e Windows)

| App | Pasta | Gera |
|---|---|---|
| TECNAL-Hub | `Android_app` | APK |
| Banho termostático | `External-Devices\banho-termostatico\apps\flutter` | APK |
| Bomba peristáltica | `External-Devices\bomba-peristaltica\apps\flutter` | APK e Windows |
| Fluxômetro | `External-Devices\fluxometro\apps\flutter` | APK |
| Frasco agitador | `External-Devices\frasco-agitador\apps\flutter` | APK |

```powershell
Push-Location .\Android_app          # ou a pasta do app na tabela
flutter pub get
flutter test
flutter build apk --release          # build\app\outputs\flutter-apk\app-release.apk
flutter install                      # instala no celular ligado por USB, com depuração USB ativa
Pop-Location

# Versão Windows da bomba: gera build\windows\x64\runner\Release\pump_app_advanced.exe
Push-Location .\External-Devices\bomba-peristaltica\apps\flutter
flutter build windows --release
Pop-Location
```

Sem cabo, copie o `app-release.apk` para o celular e abra o arquivo. Os APKs são assinados com
a chave de depuração do PC que compilou. Para instalar por cima de um APK gerado em outro PC,
desinstale o app antes; os dados salvos no app se perdem.

### 6. Apps desktop em Python

```powershell
# Banho termostático (sem dependências; conecta em 192.168.8.1)
python .\External-Devices\banho-termostatico\apps\desktop-python\bath_app.py

# Sensor de biomassa
Push-Location .\External-Devices\sensor-biomassa\apps\desktop-python
python -m pip install -r pc_client\requirements.txt
python app.py
Pop-Location
```

### 7. Atualizar os downloads desta página (release no GitHub)

Faça os builds dos passos 4 e 5 e depois rode:

```powershell
.\tools\Build-ReleaseAssets.ps1      # junta tudo em release-assets\ com os nomes usados nesta página
```

No GitHub, abra **Releases → Draft a new release**, crie uma tag nova (por exemplo,
`v2026.09.29`), anexe **todos** os arquivos de `release-assets\` e publique. Os links desta
página apontam sempre para a release mais recente. Com o [GitHub CLI](https://cli.github.com/)
instalado, o mesmo passo é:

```powershell
gh release create v2026.09.29 (Get-ChildItem .\release-assets\*).FullName --title "v2026.09.29" --notes "Resumo das mudanças"
```

Após publicar a release no GitHub, limpe com segurança os arquivos de build e caches locais
para liberar espaço e evitar sincronização desnecessária no OneDrive:

```powershell
.\tools\Clean-BuildArtifacts.ps1
```

[← Página inicial do repositório](README.md)
