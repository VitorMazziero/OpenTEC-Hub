param(
    [string]$ArduinoCli = "",
    [switch]$LegacySeed
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot

if ([string]::IsNullOrWhiteSpace($ArduinoCli)) {
    $fromPath = Get-Command arduino-cli -ErrorAction SilentlyContinue
    if ($null -ne $fromPath) {
        $ArduinoCli = $fromPath.Source
    } else {
        $portable = Join-Path $PSScriptRoot ".bin\arduino-cli.exe"
        if (Test-Path -LiteralPath $portable) {
            $ArduinoCli = $portable
        } else {
            throw "arduino-cli não encontrado no PATH nem em tools\.bin."
        }
    }
}

$sketch = if ($LegacySeed) {
    Join-Path $repoRoot "_old\TECNAL_ESP32_v8"
} else {
    Join-Path $repoRoot "ESP32S3-HUB"
}

& $ArduinoCli compile --fqbn "esp32:esp32:esp32s3" --warnings all $sketch
if ($LASTEXITCODE -ne 0) {
    throw "Compilação Arduino falhou com código $LASTEXITCODE."
}

