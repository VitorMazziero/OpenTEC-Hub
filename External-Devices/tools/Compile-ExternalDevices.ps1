[CmdletBinding()]
param(
    [string]$SharedArduinoRoot = 'D:\OneDrive\Documentos\Arduino'
)

$externalRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$cliBundled = Join-Path $PSScriptRoot '.bin\arduino-cli.exe'
$cli = if (Test-Path -LiteralPath $cliBundled) { $cliBundled } else { (Get-Command arduino-cli -ErrorAction Stop).Source }
$libraryRoot = Join-Path $SharedArduinoRoot 'libraries'
if (-not (Test-Path -LiteralPath $libraryRoot)) { throw "Shared Arduino library folder not found: $libraryRoot" }

$config = Join-Path $PSScriptRoot 'arduino-cli.local.yaml'
$dataRoot = Join-Path $env:LOCALAPPDATA 'Arduino15'
& $cli config init --dest-file $config --overwrite | Out-Null
& $cli config set directories.data $dataRoot --config-file $config
& $cli config set directories.user $SharedArduinoRoot --config-file $config
if ($LASTEXITCODE -ne 0) { throw 'Could not configure Arduino CLI.' }

$requiredLibraries = [ordered]@{
    'Async TCP' = '3.5.0'
    'ESP Async WebServer' = '3.12.0'
    'VL53L0X' = '1.3.1'
    'Adafruit ADS1X15' = '2.6.2'
    'Adafruit MCP4725' = '2.0.2'
}
$installed = (& $cli lib list --config-file $config --format json | ConvertFrom-Json)
$installedLibraries = $installed.installed_libraries | ForEach-Object { $_.library }
foreach ($entry in $requiredLibraries.GetEnumerator()) {
    $match = $installedLibraries | Where-Object { $_.name -eq $entry.Key -and $_.version -eq $entry.Value }
    if (-not $match) { throw "Required shared library missing or wrong version: $($entry.Key) $($entry.Value)" }
}

$jobs = @(
    @('distance', 'esp32:esp32:esp32', 'sensor-distancia\firmware\distance-sensor'),
    @('agitator', 'esp32:esp32:esp32s3', 'frasco-agitador\firmware\flask-agitator'),
    @('pump', 'esp32:esp32:esp32', 'bomba-peristaltica\firmware\peristaltic-pump'),
    @('flowmeter', 'esp32:esp32:esp32:UploadSpeed=921600,CPUFreq=240,FlashFreq=80,FlashMode=qio,FlashSize=4M,PartitionScheme=default,DebugLevel=none,PSRAM=disabled,LoopCore=1,EventsCore=1,EraseFlash=none,JTAGAdapter=default,ZigbeeMode=default', 'fluxometro\firmware\flowmeter'),
    @('biomass', 'esp32:esp32:esp32s3', 'sensor-biomassa\firmware\biomass-sensor')
)

foreach ($job in $jobs) {
    Write-Output "Building $($job[0])"
    $output = Join-Path $PSScriptRoot ".build\$($job[0])"
    & $cli compile --config-file $config --fqbn $job[1] --output-dir $output (Join-Path $externalRoot $job[2])
    if ($LASTEXITCODE -ne 0) { throw "Build failed: $($job[0])" }
}

Write-Output 'All five external-device firmwares compiled successfully.'
