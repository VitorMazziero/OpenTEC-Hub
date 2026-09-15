[CmdletBinding()]
param(
    [Parameter(Mandatory = $true, Position = 0)]
    [ValidateSet('distance', 'agitator', 'pump', 'flowmeter', 'biomass')]
    [string]$Device,

    [Parameter(Position = 1)]
    [string]$IpAddress,

    [Parameter()]
    [string]$BinaryPath,

    [Parameter()]
    [switch]$Compile,

    # Arduino user libraries.
    # This is explicitly passed to arduino-cli so compilation does not
    # depend on the sketchbook path configured in arduino-cli.local.yaml.
    [Parameter()]
    [string]$LibrariesPath = 'C:\Users\vitor\OneDrive\Documentos\Arduino\libraries'
)

$ErrorActionPreference = 'Stop'

# ============================================================
# Paths / tools
# ============================================================

$scriptRoot = $PSScriptRoot
$externalRoot = (Resolve-Path (Join-Path $scriptRoot '..')).Path

$cliBundled = Join-Path $scriptRoot '.bin\arduino-cli.exe'
$cliCmd = Get-Command arduino-cli -ErrorAction SilentlyContinue

$cli = if (Test-Path -LiteralPath $cliBundled) {
    $cliBundled
}
elseif ($cliCmd) {
    $cliCmd.Source
}
else {
    $null
}

$configFile = Join-Path $scriptRoot 'arduino-cli.local.yaml'


# ============================================================
# Device configuration
# ============================================================

$deviceMeta = @{
    'distance' = @{
        Name         = 'Sensor de Distancia'
        DefaultApIp  = '192.168.5.1'
        Fqbn         = 'esp32:esp32:esp32'
        RelativePath = 'sensor-distancia\firmware\distance-sensor'
        BinPattern   = 'distance-sensor*.ino.bin'
    }

    'agitator' = @{
        Name         = 'Frasco Agitador'
        DefaultApIp  = '192.168.4.1'
        Fqbn         = 'esp32:esp32:esp32'
        RelativePath = 'frasco-agitador\firmware\flask-agitator'
        BinPattern   = 'flask-agitator*.ino.bin'
    }

    'pump' = @{
        Name         = 'Bomba Peristaltica'
        DefaultApIp  = '192.168.6.1'
        Fqbn         = 'esp32:esp32:esp32'
        RelativePath = 'bomba-peristaltica\firmware\peristaltic-pump'
        BinPattern   = 'peristaltic-pump*.ino.bin'
    }

    'flowmeter' = @{
        Name        = 'Fluxometro'
        DefaultApIp = '192.168.10.1'

        Fqbn = 'esp32:esp32:esp32:UploadSpeed=921600,CPUFreq=240,FlashFreq=80,FlashMode=qio,FlashSize=4M,PartitionScheme=default,DebugLevel=none,PSRAM=disabled,LoopCore=1,EventsCore=1,EraseFlash=none,JTAGAdapter=default,ZigbeeMode=default'

        RelativePath = 'fluxometro\firmware\flowmeter'
        BinPattern   = 'flowmeter*.ino.bin'
    }

    'biomass' = @{
        Name         = 'Sensor de Biomassa'
        DefaultApIp  = '192.168.7.1'
        Fqbn         = 'esp32:esp32:esp32s3'
        RelativePath = 'sensor-biomassa\firmware\biomass-sensor'
        BinPattern   = 'biomass-sensor*.ino.bin'
    }
}

$info = $deviceMeta[$Device]


# ============================================================
# Discover device IP through Hub
# ============================================================

$targetIp = $IpAddress

if (-not $targetIp) {

    try {
        $hubNodesUrl = 'http://192.168.4.1/nodes'

        $resp = Invoke-RestMethod `
            -Uri $hubNodesUrl `
            -TimeoutSec 1 `
            -ErrorAction Stop

        if ($resp.nodes) {

            $matched = $resp.nodes |
                Where-Object {
                    $_.dev -eq $Device -and
                    $_.ip -and
                    $_.ip -ne '0.0.0.0'
                } |
                Select-Object -First 1

            if ($matched) {
                $targetIp = [string]$matched.ip

                Write-Host `
                    "[OTA] Auto-descoberta no Hub: '$Device' localizado no IP $targetIp" `
                    -ForegroundColor Green
            }
        }
    }
    catch {
        # Hub offline, USB-network interface unavailable,
        # or device currently operating as its own AP.
    }
}

if (-not $targetIp) {
    $targetIp = $info.DefaultApIp
}


# ============================================================
# Firmware sketch path
# ============================================================

$deviceSketchDir = Join-Path $externalRoot $info.RelativePath

if (-not (Test-Path -LiteralPath $deviceSketchDir)) {
    throw "Diretorio do firmware nao encontrado: $deviceSketchDir"
}


# ============================================================
# 1. Optional compilation
# ============================================================

if ($Compile) {

    if (-not $cli -or -not (Test-Path -LiteralPath $cli)) {
        throw "arduino-cli.exe nao encontrado para compilacao automatica."
    }

    if (-not (Test-Path -LiteralPath $configFile)) {
        throw "Arquivo de configuracao do Arduino CLI nao encontrado: $configFile"
    }

    if (-not (Test-Path -LiteralPath $LibrariesPath)) {
        throw "Diretorio de bibliotecas Arduino nao encontrado: $LibrariesPath"
    }

    Write-Host ""
    Write-Host "==========================================================" -ForegroundColor Cyan
    Write-Host " [OTA] Compilacao de Firmware" -ForegroundColor Cyan
    Write-Host " Dispositivo : $($info.Name) [$Device]" -ForegroundColor Yellow
    Write-Host " Sketch      : $deviceSketchDir" -ForegroundColor Yellow
    Write-Host " FQBN        : $($info.Fqbn)" -ForegroundColor Yellow
    Write-Host " Libraries   : $LibrariesPath" -ForegroundColor Yellow
    Write-Host "==========================================================" -ForegroundColor Cyan
    Write-Host ""

    $buildOut = Join-Path $scriptRoot ".build\$Device"

    if (-not (Test-Path -LiteralPath $buildOut)) {
        New-Item `
            -ItemType Directory `
            -Path $buildOut `
            -Force | Out-Null
    }

    Write-Host "[OTA] Compilando firmware para $($info.Name)..." `
        -ForegroundColor Cyan

    # IMPORTANT:
    # --libraries explicitly tells arduino-cli where your Arduino IDE
    # libraries are located. This fixes errors such as:
    #
    #   fatal error: AsyncTCP.h: No such file or directory
    #
    # even when AsyncTCP is already installed.

    $compileArgs = @(
        'compile'
        '--config-file'
        $configFile
        '--fqbn'
        $info.Fqbn
        '--libraries'
        $LibrariesPath
        '--output-dir'
        $buildOut
        $deviceSketchDir
    )

    & $cli @compileArgs

    if ($LASTEXITCODE -ne 0) {
        throw "Falha na compilacao do firmware para $Device."
    }

    Write-Host ""
    Write-Host "[OTA] Compilacao concluida com sucesso." `
        -ForegroundColor Green
}


# ============================================================
# 2. Find compiled firmware binary
# ============================================================

if (-not $BinaryPath) {

    $searchPaths = @(
        (Join-Path $scriptRoot ".build\$Device"),
        (Join-Path $deviceSketchDir "build\esp32.esp32.esp32"),
        (Join-Path $deviceSketchDir "build\esp32.esp32.esp32s3"),
        $deviceSketchDir
    )

    $candidates = @()

    foreach ($sp in $searchPaths) {

        if (Test-Path -LiteralPath $sp) {

            $files = Get-ChildItem `
                -LiteralPath $sp `
                -Filter '*.bin' `
                -File |
                Where-Object {
                    $_.Name -notmatch '\.(merged|bootloader|partitions)\.bin$'
                } |
                Sort-Object LastWriteTime -Descending

            $candidates += $files
        }
    }

    if ($candidates.Count -eq 0) {
        throw @"
Nenhum arquivo de firmware .bin encontrado para $Device.

Compile usando:
    .\Publish-OtaFirmware.ps1 $Device -Compile

ou especifique manualmente:
    .\Publish-OtaFirmware.ps1 $Device -BinaryPath "C:\caminho\firmware.bin"
"@
    }

    $BinaryPath = $candidates[0].FullName
}


# ============================================================
# Validate binary
# ============================================================

if (-not (Test-Path -LiteralPath $BinaryPath)) {
    throw "Arquivo de firmware nao encontrado: $BinaryPath"
}

$binItem = Get-Item -LiteralPath $BinaryPath

$fileSizeKb = [math]::Round(
    $binItem.Length / 1024,
    1
)


# ============================================================
# OTA information
# ============================================================

Write-Host ""
Write-Host "==========================================================" -ForegroundColor Cyan
Write-Host " [OTA] Atualizacao de Firmware via Wi-Fi (Web OTA)" -ForegroundColor Cyan
Write-Host " Dispositivo : $($info.Name) [$Device]" -ForegroundColor Yellow
Write-Host " IP Destino  : http://$targetIp/update" -ForegroundColor Yellow
Write-Host " Binario     : $($binItem.Name) ($fileSizeKb KB)" -ForegroundColor Yellow
Write-Host "==========================================================" -ForegroundColor Cyan
Write-Host ""


# ============================================================
# 3. HTTP connectivity test
# ============================================================

$updateUri = "http://$targetIp/update"

Write-Host "[OTA] Testando conectividade com $updateUri..." `
    -ForegroundColor Gray

try {

    $testReq = [System.Net.WebRequest]::Create($updateUri)

    $testReq.Timeout = 3000
    $testReq.Method = 'GET'

    $testResp = $testReq.GetResponse()

    $testResp.Close()

    Write-Host `
        "[OTA] Dispositivo respondeu na rota /update. Iniciando envio..." `
        -ForegroundColor Green
}
catch {

    Write-Host `
        "[OTA] Aviso: Dispositivo nao respondeu de imediato ao GET /update ($($_.Exception.Message))." `
        -ForegroundColor DarkYellow

    Write-Host `
        "[OTA] Certifique-se de estar conectado a rede Wi-Fi do dispositivo ou do Hub." `
        -ForegroundColor DarkYellow

    Write-Host `
        "[OTA] Prosseguindo com tentativa de upload multipart via curl..." `
        -ForegroundColor Gray
}


# ============================================================
# 4. Multipart upload using native Windows curl
# ============================================================

$curlCmd = Get-Command curl.exe -ErrorAction SilentlyContinue

$curlExe = if ($curlCmd) {
    $curlCmd.Source
}
else {
    $null
}

if (-not $curlExe) {
    throw "curl.exe nao foi encontrado no PATH do sistema operacional."
}


Write-Host ""
Write-Host "[OTA] Enviando imagem de firmware ($fileSizeKb KB)..." `
    -ForegroundColor Cyan


# curl form:
#
#     firmware=@C:\path\firmware.bin
#
# Passing the argument as one PowerShell argument avoids problems
# with spaces in OneDrive paths.

$firmwareForm = "firmware=@$BinaryPath"

& $curlExe `
    --fail `
    --progress-bar `
    -F $firmwareForm `
    $updateUri


# ============================================================
# Upload result
# ============================================================

if ($LASTEXITCODE -eq 0) {

    Write-Host ""
    Write-Host "[OTA] ==========================================================" `
        -ForegroundColor Green

    Write-Host "[OTA] Sucesso! Firmware enviado e validado pelo dispositivo." `
        -ForegroundColor Green

    Write-Host "[OTA] A placa reiniciara automaticamente nos proximos 2 segundos." `
        -ForegroundColor Green

    Write-Host "[OTA] ==========================================================" `
        -ForegroundColor Green
}
else {

    $curlExitCode = $LASTEXITCODE

    Write-Host ""
    Write-Host "[OTA] Erro: curl retornou codigo de saida $curlExitCode." `
        -ForegroundColor Red

    throw "Falha no upload OTA para $targetIp."
}