# Compilar, enviar firmware e gerar o instalador

Execute os comandos no PowerShell **a partir da raiz do projeto `ProjetoTECNAL`**. Os comandos com `.` funcionam no desktop e no notebook, independentemente da unidade do OneDrive. Compilar apenas cria um arquivo local; somente o comando de envio grava o dispositivo. "Última versão" aqui significa o código presente nesta pasta; se quiser buscar os commits mais recentes do GitHub antes de compilar, execute `git pull --ff-only` na raiz, com suas alterações locais já salvas.

## Qual pasta abrir

No desktop:

```powershell
Set-Location 'D:\OneDrive\PosDoc_Fapesp\_Automacao_e_Controle\ProjetoTECNAL'
```

No notebook:

```powershell
Set-Location 'C:\Users\vitor\OneDrive\PosDoc_Fapesp\_Automacao_e_Controle\ProjetoTECNAL'
```

Depois de entrar na pasta, use os mesmos comandos relativos abaixo. `arduino-cli` e o core ESP32 precisam estar instalados (ou `arduino-cli.exe` disponível em `tools\.bin`); bibliotecas Arduino são procuradas no OneDrive deste computador, em `Documents/Arduino` ou no arquivo local de configuração. Se estiverem em outra pasta, passe `-LibrariesPath 'caminho\para\libraries'` nos comandos de compilação dos nós externos.

## Tabela de todos os comandos

Execute na raiz do projeto. Escolha somente a operação desejada. Para enviar por Wi-Fi, conecte-se ao AP do equipamento indicado na tabela de redes abaixo. Compilar usa o código atual da pasta; enviar usa o binário especificado.

| Dispositivo | Operação | Comando relativo |
|---|---|---|
| Fluxômetro | Só compilar | `& .\External-Devices\tools\Publish-OtaFirmware.ps1 flowmeter -CompileOnly` |
| Fluxômetro | Só enviar | `& .\External-Devices\tools\Publish-OtaFirmware.ps1 flowmeter 192.168.10.1 -BinaryPath .\External-Devices\tools\.build\flowmeter\flowmeter.ino.bin` |
| Fluxômetro | Compilar e enviar | `& .\External-Devices\tools\Publish-OtaFirmware.ps1 flowmeter 192.168.10.1 -Compile` |
| Bomba peristáltica | Só compilar | `& .\External-Devices\tools\Publish-OtaFirmware.ps1 pump -CompileOnly` |
| Bomba peristáltica | Só enviar | `& .\External-Devices\tools\Publish-OtaFirmware.ps1 pump 192.168.6.1 -BinaryPath .\External-Devices\tools\.build\pump\peristaltic-pump.ino.bin` |
| Bomba peristáltica | Compilar e enviar | `& .\External-Devices\tools\Publish-OtaFirmware.ps1 pump 192.168.6.1 -Compile` |
| Frasco agitador | Só compilar | `& .\External-Devices\tools\Publish-OtaFirmware.ps1 agitator -CompileOnly` |
| Frasco agitador | Só enviar | `& .\External-Devices\tools\Publish-OtaFirmware.ps1 agitator 192.168.4.1 -BinaryPath .\External-Devices\tools\.build\agitator\flask-agitator.ino.bin` |
| Frasco agitador | Compilar e enviar | `& .\External-Devices\tools\Publish-OtaFirmware.ps1 agitator 192.168.4.1 -Compile` |
| Biomassa | Só compilar | `& .\External-Devices\tools\Publish-OtaFirmware.ps1 biomass -CompileOnly` |
| Biomassa | Só enviar | `& .\External-Devices\tools\Publish-OtaFirmware.ps1 biomass 192.168.7.1 -BinaryPath .\External-Devices\tools\.build\biomass\biomass-sensor.ino.bin` |
| Biomassa | Compilar e enviar | `& .\External-Devices\tools\Publish-OtaFirmware.ps1 biomass 192.168.7.1 -Compile` |
| Distância | Só compilar | `& .\External-Devices\tools\Publish-OtaFirmware.ps1 distance -CompileOnly` |
| Distância | Só enviar | `& .\External-Devices\tools\Publish-OtaFirmware.ps1 distance 192.168.5.1 -BinaryPath .\External-Devices\tools\.build\distance\distance-sensor.ino.bin` |
| Distância | Compilar e enviar | `& .\External-Devices\tools\Publish-OtaFirmware.ps1 distance 192.168.5.1 -Compile` |
| Banho | Só compilar | `& .\External-Devices\tools\Publish-OtaFirmware.ps1 bath -CompileOnly` |
| Banho | Só enviar | `& .\External-Devices\tools\Publish-OtaFirmware.ps1 bath 192.168.8.1 -BinaryPath .\External-Devices\tools\.build\bath\thermostatic-bath.ino.bin` |
| Banho | Compilar e enviar | `& .\External-Devices\tools\Publish-OtaFirmware.ps1 bath 192.168.8.1 -Compile` |
| Hub | Só compilar | `& .\ESP32S3-HUB\tools\compile.ps1 -OutputDir .\ESP32S3-HUB\build\ota` |
| Hub | Só enviar | `& .\ESP32S3-HUB\tools\ota_upload.ps1 -HubIp 192.168.4.1 -Bin .\ESP32S3-HUB\build\ota\ESP32S3-HUB.ino.bin` |
| Hub | Compilar e enviar | `& .\ESP32S3-HUB\tools\ota_upload.ps1 -HubIp 192.168.4.1` |
| Windows | Limpar bin/obj e gerar Release | `& .\Windows_app\tools\Build-Release.ps1` |
| Windows | Gerar somente o instalador ISS | `& .\Windows_app\installer\build_installer.ps1 -SkipPublish` |
| Windows | Publicar e gerar ISS, sem limpeza prévia | `& .\Windows_app\installer\build_installer.ps1` |

### Servo ASDA-B2: somente USB

Defina `$cli` para o Arduino CLI instalado: `$cli = "arduino-cli"`, ou `$cli = (Resolve-Path .\ESP32S3-HUB\tools\.bin\arduino-cli.exe).Path` se usar a cópia local. Troque `COM5` pela porta da placa (`& $cli board list`). Confira `MODULO_TECNAL_ALVO` no sketch antes de compilar.

| Operação | Comando relativo |
|---|---|
| Só compilar | `& $cli compile --fqbn esp32:esp32:esp32s3 --output-dir .\ESP32S3-SERVO\Software\firmware-producao\build .\ESP32S3-SERVO\Software\firmware-producao\ASDA_B2_Servo_Node` |
| Só enviar por USB | `& $cli upload -p COM5 --fqbn esp32:esp32:esp32s3 --input-dir .\ESP32S3-SERVO\Software\firmware-producao\build .\ESP32S3-SERVO\Software\firmware-producao\ASDA_B2_Servo_Node` |

Para compilar e enviar o servo, execute essas duas linhas na ordem e só faça o envio se a compilação terminar sem erro. O servo não oferece OTA.


## Firmware dos seis dispositivos externos

Escolha o nome e o IP da tabela. Conecte o computador **ao Wi-Fi do próprio dispositivo** antes de enviar; o IP explícito impede que o script escolha outro nó por descoberta no Hub.

| Dispositivo | Nome no comando | Wi-Fi direto | IP direto | Arquivo gerado em `External-Devices\tools\.build\<nome>\` |
|---|---|---|---|---|
| Fluxômetro | `flowmeter` | `Floxometro_AP` | `192.168.10.1` | `flowmeter.ino.bin` |
| Bomba peristáltica | `pump` | `FeedPump` | `192.168.6.1` | `peristaltic-pump.ino.bin` |
| Agitador | `agitator` | `MotorBoeco` | `192.168.4.1` | `flask-agitator.ino.bin` |
| Biomassa | `biomass` | `BiomassSensor` | `192.168.7.1` | `biomass-sensor.ino.bin` |
| Distância | `distance` | `Distance Sensor` | `192.168.5.1` | `distance-sensor.ino.bin` |
| Banho | `bath` | `Banho Termostatico` | `192.168.8.1` | `thermostatic-bath.ino.bin` |

Exemplo com o **fluxômetro**. Para outro dispositivo, troque nome, IP e nome do binário conforme a tabela.

**Só compilar** (não usa Wi-Fi nem envia):

```powershell
& .\External-Devices\tools\Publish-OtaFirmware.ps1 flowmeter -CompileOnly
```

**Só enviar um binário já compilado** (sem recompilar):

```powershell
& .\External-Devices\tools\Publish-OtaFirmware.ps1 flowmeter 192.168.10.1 -BinaryPath .\External-Devices\tools\.build\flowmeter\flowmeter.ino.bin
```

**Compilar a fonte atual e enviar em seguida**:

```powershell
& .\External-Devices\tools\Publish-OtaFirmware.ps1 flowmeter 192.168.10.1 -Compile
```

`-BinaryPath` aceita também um arquivo versionado escolhido manualmente. No envio sem compilação, indique sempre o arquivo para saber exatamente qual versão será gravada. A OTA do fluxômetro fecha o gás e zera a referência no início; atualize com o processo parado.

## Firmware do Hub

Confira antes de compilar o valor `MODULO_TECNAL` em `ESP32S3-HUB\ESP32S3-HUB\Config.h`: `1` gera a imagem para `ModuloTECNAL_1`; `2`, para `ModuloTECNAL_2`. O IP direto de ambos é `192.168.4.1`.

**Só compilar**:

```powershell
& .\ESP32S3-HUB\tools\compile.ps1 -OutputDir .\ESP32S3-HUB\build\ota
```

**Só enviar a imagem compilada**, conectado ao Wi-Fi do Hub correspondente:

```powershell
& .\ESP32S3-HUB\tools\ota_upload.ps1 -HubIp 192.168.4.1 -Bin .\ESP32S3-HUB\build\ota\ESP32S3-HUB.ino.bin
```

**Compilar e enviar**:

```powershell
& .\ESP32S3-HUB\tools\ota_upload.ps1 -HubIp 192.168.4.1
```

O Hub recusa OTA enquanto controla determinados atuadores do processo. A atualização é para quando o cultivo estiver parado. A seleção do módulo no código precisa corresponder à placa que receberá a imagem.

O nó **servo ASDA-B2** não oferece OTA por Wi-Fi. Para ele, a compilação e a gravação por USB estão no [guia completo de downloads](../DOWNLOADS.md#1-firmware-compilar).

## Aplicativo Windows e instalador ISS

**Limpar `bin/obj` dos projetos .NET e gerar a publicação Release** (`win-x64`, .NET incluído):

```powershell
& .\Windows_app\tools\Build-Release.ps1
```

O script não encerra o aplicativo em execução; se ele estiver aberto, a limpeza é recusada. O executável publicado fica em `Windows_app\src\OpenTECHub\bin\Release\net10.0-windows10.0.19041.0\win-x64\publish\OpenTECHub.exe`.

**Gerar somente o instalador ISS**, usando a publicação Release anterior:

```powershell
& .\Windows_app\installer\build_installer.ps1 -SkipPublish
```

O instalador fica em `Windows_app\installer\Output\OpenTECHub_Setup_v<VERSAO>.exe`. Esta etapa exige o Inno Setup 6/7 (`ISCC.exe`). A versão do instalador é lida da publicação; gere a publicação novamente após alterar o código ou fazer um novo commit. O script ISS sem `-SkipPublish` também publica, mas não limpa `bin/obj`.

## Mesmo comando com caminho global

Se o PowerShell estiver em outra pasta, escolha **uma** raiz e use os caminhos absolutos abaixo.

Desktop:

```powershell
$repo = 'D:\OneDrive\PosDoc_Fapesp\_Automacao_e_Controle\ProjetoTECNAL'
```

Notebook:

```powershell
$repo = 'C:\Users\vitor\OneDrive\PosDoc_Fapesp\_Automacao_e_Controle\ProjetoTECNAL'
```

Depois de definir `$repo`, escolha **uma linha por operação**:

```powershell
# Fluxômetro: só compilar; só enviar; ou compilar e enviar.
& "$repo\External-Devices\tools\Publish-OtaFirmware.ps1" flowmeter -CompileOnly
& "$repo\External-Devices\tools\Publish-OtaFirmware.ps1" flowmeter 192.168.10.1 -BinaryPath "$repo\External-Devices\tools\.build\flowmeter\flowmeter.ino.bin"
& "$repo\External-Devices\tools\Publish-OtaFirmware.ps1" flowmeter 192.168.10.1 -Compile

# Hub: só compilar; só enviar; ou compilar e enviar.
& "$repo\ESP32S3-HUB\tools\compile.ps1" -OutputDir "$repo\ESP32S3-HUB\build\ota"
& "$repo\ESP32S3-HUB\tools\ota_upload.ps1" -HubIp 192.168.4.1 -Bin "$repo\ESP32S3-HUB\build\ota\ESP32S3-HUB.ino.bin"
& "$repo\ESP32S3-HUB\tools\ota_upload.ps1" -HubIp 192.168.4.1

# Windows: primeiro Release limpo; depois instalador ISS.
& "$repo\Windows_app\tools\Build-Release.ps1"
& "$repo\Windows_app\installer\build_installer.ps1" -SkipPublish
```

Para os **envios externos**, não basta trocar apenas o caminho da raiz: use o Wi-Fi, o nome, o IP e o binário do mesmo dispositivo na tabela. Os comandos de compilação não gravam hardware; as etapas de envio reiniciam o nó após a atualização.
