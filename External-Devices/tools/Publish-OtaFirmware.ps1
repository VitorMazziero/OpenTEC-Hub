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
    [switch]$Compile
)

$ErrorActionPreference = 'Stop'

$scriptRoot = $PSScriptRoot
$externalRoot = (Resolve-Path (Join-Path $scriptRoot '..')).Path
$cliBundled = Join-Path $scriptRoot '.bin\arduino-cli.exe'
$cliCmd = Get-Command arduino-cli -ErrorAction SilentlyContinue
$cli = if (Test-Path -LiteralPath $cliBundled) { $cliBundled } elseif ($cliCmd) { $cliCmd.Source } else { $null }
$configFile = Join-Path $scriptRoot 'arduino-cli.local.yaml'

$deviceMeta = @{
    'distance'  = @{
        Name         = 'Sensor de Distancia'
        DefaultApIp  = '192.168.5.1'
        Fqbn         = 'esp32:esp32:esp32'
        RelativePath = 'sensor-distancia\firmware\distance-sensor'
        BinPattern   = 'distance-sensor*.ino.bin'
    }
    'agitator'  = @{
        Name         = 'Frasco Agitador'
        DefaultApIp  = '192.168.4.1'
        Fqbn         = 'esp32:esp32:esp32'
        RelativePath = 'frasco-agitador\firmware\flask-agitator'
        BinPattern   = 'flask-agitator*.ino.bin'
    }
    'pump'      = @{
        Name         = 'Bomba Peristaltica'
        DefaultApIp  = '192.168.6.1'
        Fqbn         = 'esp32:esp32:esp32'
        RelativePath = 'bomba-peristaltica\firmware\peristaltic-pump'
        BinPattern   = 'peristaltic-pump*.ino.bin'
    }
    'flowmeter' = @{
        Name         = 'Fluxometro'
        DefaultApIp  = '192.168.10.1'
        Fqbn         = 'esp32:esp32:esp32:UploadSpeed=921600,CPUFreq=240,FlashFreq=80,FlashMode=qio,FlashSize=4M,PartitionScheme=default,DebugLevel=none,PSRAM=disabled,LoopCore=1,EventsCore=1,EraseFlash=none,JTAGAdapter=default,ZigbeeMode=default'
        RelativePath = 'fluxometro\firmware\flowmeter'
        BinPattern   = 'flowmeter*.ino.bin'
    }
    'biomass'   = @{
        Name         = 'Sensor de Biomassa'
        DefaultApIp  = '192.168.7.1'
        Fqbn         = 'esp32:esp32:esp32s3'
        RelativePath = 'sensor-biomassa\firmware\biomass-sensor'
        BinPattern   = 'biomass-sensor*.ino.bin'
    }
}

$info = $deviceMeta[$Device]
$targetIp = $IpAddress
if (-not $targetIp) {
    try {
        $hubNodesUrl = 'http://192.168.4.1/nodes'
        $resp = Invoke-RestMethod -Uri $hubNodesUrl -TimeoutSec 1 -ErrorAction Stop
        if ($resp.nodes) {
            $matched = $resp.nodes | Where-Object { $_.dev -eq $Device -and $_.ip -ne '0.0.0.0' }
            if ($matched) {
                $targetIp = $matched.ip
                Write-Host "[OTA] Auto-descoberta no Hub: '$Device' localizado no IP $targetIp" -ForegroundColor Green
            }
        }
    } catch {
        # Hub offline ou rede externa; prossegue com DefaultApIp
    }
}
if (-not $targetIp) {
    $targetIp = $info.DefaultApIp
}
$deviceSketchDir = Join-Path $externalRoot $info.RelativePath

# 1. Compilação opcional se solicitado
if ($Compile) {
    if (-not $cli -or -not (Test-Path -LiteralPath $cli)) {
        throw "arduino-cli.exe nao encontrado para compilacao automatica."
    }
    Write-Host "[OTA] Compilando firmware para $($info.Name) ($($info.Fqbn))..." -ForegroundColor Cyan
    $buildOut = Join-Path $scriptRoot ".build\$Device"
    & $cli compile --config-file $configFile --fqbn $info.Fqbn --output-dir $buildOut $deviceSketchDir
    if ($LASTEXITCODE -ne 0) {
        throw "Falha na compilacao do firmware para $Device."
    }
    Write-Host "[OTA] Compilacao concluida com sucesso." -ForegroundColor Green
}

# 2. Localização do binário compilado
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
            $files = Get-ChildItem -LiteralPath $sp -Filter '*.bin' -File |
                Where-Object {
                    $_.Name -notmatch '\.(merged|bootloader|partitions)\.bin$'
                } |
                Sort-Object LastWriteTime -Descending
            $candidates += $files
        }
    }

    if ($candidates.Count -eq 0) {
        throw "Nenhum arquivo de firmware .bin encontrado para $Device. Use -Compile ou especifique -BinaryPath."
    }

    $BinaryPath = $candidates[0].FullName
}

if (-not (Test-Path -LiteralPath $BinaryPath)) {
    throw "Arquivo de firmware nao encontrado: $BinaryPath"
}

$binItem = Get-Item -LiteralPath $BinaryPath
$fileSizeKb = [math]::Round($binItem.Length / 1024, 1)

Write-Host "==========================================================" -ForegroundColor Cyan
Write-Host " [OTA] Atualizacao de Firmware via Wi-Fi (Web OTA)" -ForegroundColor Cyan
Write-Host " Dispositivo : $($info.Name) [$Device]" -ForegroundColor Yellow
Write-Host " IP Destino  : http://$targetIp/update" -ForegroundColor Yellow
Write-Host " Binario     : $($binItem.Name) ($fileSizeKb KB)" -ForegroundColor Yellow
Write-Host "==========================================================" -ForegroundColor Cyan

# 3. Teste rápido de conectividade HTTP
$updateUri = "http://$targetIp/update"
Write-Host "[OTA] Testando conectividade com $updateUri..." -ForegroundColor Gray
try {
    $testReq = [System.Net.WebRequest]::Create($updateUri)
    $testReq.Timeout = 3000
    $testReq.Method = "GET"
    $testResp = $testReq.GetResponse()
    $testResp.Close()
    Write-Host "[OTA] Dispositivo respondeu na rota /update. Iniciando envio..." -ForegroundColor Green
} catch {
    Write-Host "[OTA] Aviso: Dispositivo nao respondeu de imediato ao GET /update ($($_.Exception.Message))." -ForegroundColor DarkYellow
    Write-Host "[OTA] Certifique-se de estar conectado a rede Wi-Fi do dispositivo ou do Hub." -ForegroundColor DarkYellow
    Write-Host "[OTA] Prosseguindo com tentativa de upload multipart via curl..." -ForegroundColor Gray
}

# 4. Envio Multipart via curl nativo do Windows
$curlCmd = Get-Command curl.exe -ErrorAction SilentlyContinue
$curlExe = if ($curlCmd) { $curlCmd.Source } else { $null }
if (-not $curlExe) {
    throw "curl.exe nao foi encontrado no PATH do sistema operacional."
}

Write-Host "[OTA] Enviando imagem de firmware ($fileSizeKb KB)..." -ForegroundColor Cyan
& $curlExe --fail --progress-bar -F "firmware=@`"$BinaryPath`"" $updateUri

if ($LASTEXITCODE -eq 0) {
    Write-Host ""
    Write-Host "[OTA] ==========================================================" -ForegroundColor Green
    Write-Host "[OTA] Sucesso! Firmware enviado e validado pelo dispositivo." -ForegroundColor Green
    Write-Host "[OTA] A placa reiniciara automaticamente nos proximos 2 segundos." -ForegroundColor Green
    Write-Host "[OTA] ==========================================================" -ForegroundColor Green
} else {
    Write-Host ""
    Write-Host "[OTA] Erro: curl retornou codigo de saida $LASTEXITCODE." -ForegroundColor Red
    throw "Falha no upload OTA para $targetIp."
}
