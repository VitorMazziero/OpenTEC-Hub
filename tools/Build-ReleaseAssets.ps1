<#
.SYNOPSIS
  Gera em release-assets\ os arquivos de download listados em DOWNLOADS.md.

.DESCRIPTION
  Compacta o firmware ativo de cada equipamento e os apps desktop em Python, e copia os
  aplicativos já compilados (instalador Windows, APKs e build Windows do Flutter) com os
  nomes fixos que DOWNLOADS.md usa. Não compila nada: rode antes o build de cada app.
  Anexe todo o conteúdo de release-assets\ a uma nova release no GitHub.

.EXAMPLE
  tools\Build-ReleaseAssets.ps1
#>
[CmdletBinding()]
param(
    [string]$OutDir = (Join-Path $PSScriptRoot '..\release-assets')
)

$ErrorActionPreference = 'Stop'
$Root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$Ext = Join-Path $Root 'External-Devices'

# Pastas geradas pela compilação ou pela execução, que não entram nos .zip.
$Excluded = @('build', '__pycache__', 'runs', '.dart_tool')

$Firmware = [ordered]@{
    'TECNAL-Hub_firmware.zip'       = 'ESP32S3-HUB\ESP32S3-HUB'
    'Servo_firmware.zip'            = 'ESP32S3-SERVO\Software\firmware-producao\ASDA_B2_Servo_Node'
    'Banho_firmware.zip'            = 'External-Devices\banho-termostatico\firmware\thermostatic-bath'
    'Bomba_firmware.zip'            = 'External-Devices\bomba-peristaltica\firmware\peristaltic-pump'
    'Fluxometro_firmware.zip'       = 'External-Devices\fluxometro\firmware\flowmeter'
    'Frasco-Agitador_firmware.zip'  = 'External-Devices\frasco-agitador\firmware\flask-agitator'
    'Sensor-Biomassa_firmware.zip'  = 'External-Devices\sensor-biomassa\firmware\biomass-sensor'
    'Sensor-Distancia_firmware.zip' = 'External-Devices\sensor-distancia\firmware\distance-sensor'
}

$PythonApps = [ordered]@{
    'Banho_Desktop-Python.zip'           = 'External-Devices\banho-termostatico\apps\desktop-python'
    'Sensor-Biomassa_Desktop-Python.zip' = 'External-Devices\sensor-biomassa\apps\desktop-python'
}

$Apks = [ordered]@{
    'TECNAL-Hub_Android.apk'      = 'Android_app'
    'Banho_Android.apk'           = 'External-Devices\banho-termostatico\apps\flutter'
    'Bomba_Android.apk'           = 'External-Devices\bomba-peristaltica\apps\flutter'
    'Fluxometro_Android.apk'      = 'External-Devices\fluxometro\apps\flutter'
    'Frasco-Agitador_Android.apk' = 'External-Devices\frasco-agitador\apps\flutter'
}

# Compacta $Source numa pasta de mesmo nome dentro do .zip (a Arduino IDE exige que a
# pasta do sketch tenha o nome do .ino).
function New-FolderZip([string]$Source, [string]$ZipPath, [string]$FolderName) {
    $stage = Join-Path ([IO.Path]::GetTempPath()) ([Guid]::NewGuid().ToString())
    $target = Join-Path $stage $FolderName
    New-Item -ItemType Directory -Path $target | Out-Null
    try {
        Get-ChildItem -LiteralPath $Source -Force |
            Where-Object { $Excluded -notcontains $_.Name } |
            Copy-Item -Destination $target -Recurse -Force
        Get-ChildItem -LiteralPath $target -Recurse -Directory -Force |
            Where-Object { $Excluded -contains $_.Name } |
            Remove-Item -Recurse -Force -ErrorAction SilentlyContinue
        Compress-Archive -Path $target -DestinationPath $ZipPath -Force
    } finally {
        Remove-Item -LiteralPath $stage -Recurse -Force
    }
}

New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
$OutDir = (Resolve-Path $OutDir).Path
$missing = @()

foreach ($name in $Firmware.Keys) {
    $src = Join-Path $Root $Firmware[$name]
    New-FolderZip $src (Join-Path $OutDir $name) (Split-Path $src -Leaf)
    Write-Host "ok  $name"
}

foreach ($name in $PythonApps.Keys) {
    $src = Join-Path $Root $PythonApps[$name]
    New-FolderZip $src (Join-Path $OutDir $name) ($name -replace '\.zip$', '')
    Write-Host "ok  $name"
}

foreach ($name in $Apks.Keys) {
    $apk = Join-Path $Root (Join-Path $Apks[$name] 'build\app\outputs\flutter-apk\app-release.apk')
    if (Test-Path $apk) {
        Copy-Item $apk (Join-Path $OutDir $name) -Force
        Write-Host "ok  $name"
    } else {
        $missing += "$name  (rode 'flutter build apk --release' em $($Apks[$name]))"
    }
}

$installer = Get-ChildItem (Join-Path $Root 'Windows_app\installer\Output') -Filter 'OpenTECHub_Setup_*.exe' -ErrorAction SilentlyContinue |
    Sort-Object LastWriteTime -Descending | Select-Object -First 1
if ($installer) {
    Copy-Item $installer.FullName (Join-Path $OutDir 'TECNAL-Hub_Windows.exe') -Force
    Write-Host "ok  TECNAL-Hub_Windows.exe  ($($installer.Name))"
} else {
    $missing += "TECNAL-Hub_Windows.exe  (rode Windows_app\installer\build_installer.ps1)"
}

$pumpWin = Join-Path $Ext 'bomba-peristaltica\apps\flutter\build\windows\x64\runner\Release'
if (Test-Path $pumpWin) {
    New-FolderZip $pumpWin (Join-Path $OutDir 'Bomba_Windows.zip') 'Bomba_Windows'
    Write-Host "ok  Bomba_Windows.zip"
} else {
    $missing += "Bomba_Windows.zip  (rode 'flutter build windows --release' em External-Devices\bomba-peristaltica\apps\flutter)"
}

Write-Host ""
Write-Host "Arquivos em $OutDir"
if ($missing.Count -gt 0) {
    Write-Warning "Faltam aplicativos compilados; os links correspondentes em DOWNLOADS.md ficarão quebrados:"
    $missing | ForEach-Object { Write-Warning "  $_" }
}
