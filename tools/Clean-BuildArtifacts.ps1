<#
.SYNOPSIS
  Remove com seguranca artefatos de compilacao e caches do monorepo, liberando espaco no OneDrive.

.DESCRIPTION
  Localiza e exclui pastas temporarias geradas durante a compilacao (.NET bin/obj, Flutter build/.dart_tool,
  Arduino/ESP32 .build e build, caches Python e pastas de release temporarias).
  Nenhum arquivo de codigo-fonte, configuracao ou documentacao e afetado.

.PARAMETER DryRun
  Apenas analisa e exibe os diretorios e o espaco que seriam liberados, sem excluir nada.

.PARAMETER IncludeToolchains
  Tambem remove executaveis locais de ferramentas (arduino-cli em tools/.bin e caches de pacotes locais).
  Utilize quando o arduino-cli estiver configurado no PATH do Windows.

.PARAMETER Force
  Executa a exclusao diretamente sem solicitar confirmacao interativa no terminal.

.EXAMPLE
  .\tools\Clean-BuildArtifacts.ps1 -DryRun
  .\tools\Clean-BuildArtifacts.ps1
  .\tools\Clean-BuildArtifacts.ps1 -IncludeToolchains
#>
[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [switch]$DryRun,
    [switch]$IncludeToolchains,
    [switch]$Force
)

$ErrorActionPreference = 'Stop'
$Root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path

Write-Host "=================================================================" -ForegroundColor Cyan
Write-Host "   OpenTEC-Hub - Limpeza de Artefatos de Build (OneDrive/Local)   " -ForegroundColor Cyan
Write-Host "=================================================================" -ForegroundColor Cyan
Write-Host ('Diretorio raiz: {0}' -f $Root)
Write-Host ''

$TargetDirs = [System.Collections.Generic.List[string]]::new()

# 1. Firmwares e Arduino CLI builds
$FirmwareBuilds = @(
    'External-Devices\tools\.build',
    'ESP32S3-HUB\build',
    'ESP32S3-HUB\ESP32S3-HUB\build',
    'ESP32S3-HUB\tests\host-bath-cascade\build',
    'ESP32S3-SERVO\Software\firmware-producao\build',
    'ESP32S3-SERVO\Software\firmware-producao\ASDA_B2_Servo_Node\build'
)
foreach ($rel in $FirmwareBuilds) {
    $full = Join-Path $Root $rel
    if (Test-Path $full) { $TargetDirs.Add($full) }
}

# Firmwares em External-Devices
Get-ChildItem -Path (Join-Path $Root 'External-Devices') -Recurse -Directory -Filter 'build' -ErrorAction SilentlyContinue |
    ForEach-Object {
        if ($_.FullName -notmatch '\\\.git\\') {
            $TargetDirs.Add($_.FullName)
        }
    }

# 2. .NET (Windows_app)
Get-ChildItem -Path (Join-Path $Root 'Windows_app') -Recurse -Directory -Force -ErrorAction SilentlyContinue |
    Where-Object {
        $_.Name -in @('bin', 'obj', 'Output') -and
        $_.FullName -notmatch '\\\.git\\'
    } |
    ForEach-Object { $TargetDirs.Add($_.FullName) }

# 3. Flutter / Android
Get-ChildItem -Path $Root -Recurse -Directory -Force -ErrorAction SilentlyContinue |
    Where-Object {
        $_.Name -in @('build', '.dart_tool') -and
        $_.FullName -notmatch '\\\.git\\'
    } |
    ForEach-Object { $TargetDirs.Add($_.FullName) }

# 4. Release assets temporarios
$ReleaseAssets = Join-Path $Root 'release-assets'
if (Test-Path $ReleaseAssets) { $TargetDirs.Add($ReleaseAssets) }

# 5. Caches Python
Get-ChildItem -Path $Root -Recurse -Directory -Force -ErrorAction SilentlyContinue |
    Where-Object {
        $_.Name -in @('__pycache__', '.pytest_cache') -and
        $_.FullName -notmatch '\\\.git\\'
    } |
    ForEach-Object { $TargetDirs.Add($_.FullName) }

# 6. Toolchains locais opcionais
if ($IncludeToolchains) {
    $ToolchainDirs = @(
        'External-Devices\tools\.bin',
        'ESP32S3-HUB\tools\.bin',
        'External-Devices\tools\.arduino-user'
    )
    foreach ($rel in $ToolchainDirs) {
        $full = Join-Path $Root $rel
        if (Test-Path $full) { $TargetDirs.Add($full) }
    }
}

# Desduplicar e filtrar apenas diretorios existentes
$UniqueTargets = $TargetDirs | Select-Object -Unique | Where-Object { Test-Path $_ }

if ($UniqueTargets.Count -eq 0) {
    Write-Host "Nenhum arquivo ou pasta de build encontrada para limpeza. O repositorio ja esta limpo!" -ForegroundColor Green
    return
}

# Filtrar para evitar listar subpastas se a pasta pai ja estiver na lista de exclusao
$FilteredTargets = [System.Collections.Generic.List[string]]::new()
$sortedByLen = $UniqueTargets | Sort-Object { $_.Length }
foreach ($p in $sortedByLen) {
    $hasParent = $false
    foreach ($existing in $FilteredTargets) {
        if ($p.StartsWith($existing + [System.IO.Path]::DirectorySeparatorChar, [System.StringComparison]::OrdinalIgnoreCase)) {
            $hasParent = $true
            break
        }
    }
    if (-not $hasParent) {
        $FilteredTargets.Add($p)
    }
}

# Calcular espaco ocupado
$ItemsToProcess = [System.Collections.Generic.List[PSObject]]::new()
$TotalBytes = 0

foreach ($dir in $FilteredTargets) {
    $files = Get-ChildItem -LiteralPath $dir -Recurse -File -Force -ErrorAction SilentlyContinue
    $dirBytes = ($files | Measure-Object -Property Length -Sum).Sum
    if ($null -eq $dirBytes) { $dirBytes = 0 }
    $TotalBytes += $dirBytes
    $rel = $dir.Substring($Root.Length + 1)
    
    $ItemsToProcess.Add([PSCustomObject]@{
        Path      = $rel
        SizeBytes = $dirBytes
        SizeMB    = [math]::Round($dirBytes / 1MB, 2)
        FileCount = $files.Count
        FullPath  = $dir
    })
}

$Sorted = $ItemsToProcess | Sort-Object -Property SizeBytes -Descending

Write-Host "Pastas identificadas para limpeza:" -ForegroundColor Yellow
Write-Host "-----------------------------------------------------------------------------------------------"
$header = '{0,-70} | {1,10} | {2,8}' -f 'Caminho Relativo', 'Tamanho MB', 'Arquivos'
Write-Host $header
Write-Host "-----------------------------------------------------------------------------------------------"
foreach ($item in $Sorted) {
    $row = '{0,-70} | {1,10:N2} | {2,8}' -f $item.Path, $item.SizeMB, $item.FileCount
    Write-Host $row
}
Write-Host "-----------------------------------------------------------------------------------------------"
$TotalMB = [math]::Round($TotalBytes / 1MB, 2)
$TotalGB = [math]::Round($TotalBytes / 1GB, 2)
$totalMsg = 'TOTAL: {0:N2} MB ({1:N2} GB) em {2} pastas' -f $TotalMB, $TotalGB, $Sorted.Count
Write-Host $totalMsg -ForegroundColor Cyan
Write-Host ''

if ($DryRun) {
    Write-Host "[MODO SIMULACAO (-DryRun)]: Nenhum arquivo foi modificado ou excluido." -ForegroundColor Yellow
    return
}

if (-not $Force) {
    $confirm = Read-Host "Deseja excluir permanentemente estas pastas de build para liberar espaco? (S/N)"
    if ($confirm -notmatch '^[sSyY]') {
        Write-Host "Operacao cancelada pelo usuario." -ForegroundColor Yellow
        return
    }
}

Write-Host "Iniciando exclusao..." -ForegroundColor Yellow
$cleanedCount = 0
$errorCount = 0

foreach ($item in $Sorted) {
    try {
        if (Test-Path -LiteralPath $item.FullPath) {
            Remove-Item -LiteralPath $item.FullPath -Recurse -Force -ErrorAction Stop
            Write-Host (' [REMOVIDO] {0}' -f $item.Path) -ForegroundColor Green
            $cleanedCount++
        }
    } catch {
        Write-Warning (' Nao foi possivel remover {0}: {1}' -f $item.Path, $_.Exception.Message)
        $errorCount++
    }
}

Write-Host ''
if ($errorCount -eq 0) {
    $doneMsg = 'Limpeza concluida com sucesso! {0:N2} MB liberados no OneDrive.' -f $TotalMB
    Write-Host $doneMsg -ForegroundColor Green
} else {
    $doneMsg = 'Limpeza finalizada com {0} pastas removidas e {1} avisos.' -f $cleanedCount, $errorCount
    Write-Host $doneMsg -ForegroundColor Yellow
}
