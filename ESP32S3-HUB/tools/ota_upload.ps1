# Compila o firmware do Hub e grava pelo Wi-Fi (POST /update).
#
# O PC precisa estar na rede do proprio modulo (ModuloTECNAL_1 ou _2). O Hub recusa o
# envio enquanto comanda um processo (cascata do banho com setpoint do reator, motor com
# rotacao); pare o processo antes. A gravacao por USB continua funcionando como antes.
#
#   .\tools\ota_upload.ps1                      compila e envia para 192.168.4.1
#   .\tools\ota_upload.ps1 -SkipCompile         reenvia a ultima imagem de build\ota
#   .\tools\ota_upload.ps1 -Bin <arquivo.bin>   envia uma imagem exportada pela Arduino IDE
#
# Mensagens sem acento: o PowerShell 5.1 le arquivo UTF-8 sem BOM como ANSI.
param(
    [string]$HubIp = "192.168.4.1",
    [string]$Bin = "",
    [switch]$SkipCompile,
    [string]$ArduinoCli = ""
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot

if ([string]::IsNullOrWhiteSpace($Bin)) {
    $outDir = Join-Path $repoRoot "build\ota"
    if (-not $SkipCompile) {
        & (Join-Path $PSScriptRoot "compile.ps1") -ArduinoCli $ArduinoCli -OutputDir $outDir
    }
    $Bin = Join-Path $outDir "ESP32S3-HUB.ino.bin"
}
if (-not (Test-Path -LiteralPath $Bin)) {
    throw "Imagem nao encontrada: $Bin"
}

try {
    Invoke-WebRequest -UseBasicParsing -TimeoutSec 5 "http://$HubIp/ping" | Out-Null
} catch {
    throw "O Hub nao respondeu em $HubIp. O PC esta na rede Wi-Fi ModuloTECNAL?"
}

$size = [math]::Round((Get-Item -LiteralPath $Bin).Length / 1024)
Write-Host "Enviando $Bin ($size KB) para http://$HubIp/update ..."
# O Hub confere o nome do arquivo (ESP32S3-HUB*.bin); a imagem vai com o nome dela.
& curl.exe --silent --show-error --fail-with-body --max-time 300 `
    -F "firmware=@$Bin" "http://$HubIp/update"
$code = $LASTEXITCODE
Write-Host ""
if ($code -ne 0) {
    throw "OTA falhou ou foi recusado (curl saiu com $code). O Hub mantem o firmware atual."
}
Write-Host "Pronto. O Hub reinicia em alguns segundos; confira a versao em http://$HubIp/update."
