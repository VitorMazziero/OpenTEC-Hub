<#
.SYNOPSIS
    Script automatizado de publicacao e geracao do instalador do OpenTEC-Hub.

.DESCRIPTION
    1. Executa 'dotnet publish' no projeto OpenTECHub em modo Release self-contained (win-x64).
    2. Localiza o compilador do Inno Setup 6 (ISCC.exe).
    3. Compila 'OpenTECHub_Setup.iss', gerando o executavel do instalador em 'installer/Output/'.
#>

[CmdletBinding()]
param(
    [switch]$SkipPublish = $false,
    [string]$PublishDir = ""
)

$ErrorActionPreference = "Stop"
$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Definition
$ProjectDir = Join-Path $ScriptDir "..\src\OpenTECHub"
$ProjectFile = Join-Path $ProjectDir "OpenTECHub.csproj"
$IssFile = Join-Path $ScriptDir "OpenTECHub_Setup.iss"

Write-Host "======================================================" -ForegroundColor Cyan
Write-Host " OpenTEC-Hub - Build do Instalador e Publicacao" -ForegroundColor Cyan
Write-Host "======================================================" -ForegroundColor Cyan

if (-not $SkipPublish) {
    Write-Host "`n[1/3] Publicando aplicacao (.NET 10 self-contained win-x64)..." -ForegroundColor Yellow
    
    $publishArgs = @(
        "publish",
        $ProjectFile,
        "-c", "Release",
        "-r", "win-x64",
        "--self-contained", "true",
        "-p:PublishSingleFile=false"
    )
    
    & dotnet @publishArgs
    if ($LASTEXITCODE -ne 0) {
        Write-Error "Falha na publicacao do projeto OpenTECHub. Codigo: $LASTEXITCODE"
        exit $LASTEXITCODE
    }
    Write-Host "Publicacao concluida com sucesso!" -ForegroundColor Green
} else {
    Write-Host "`n[1/3] Publicacao ignorada (-SkipPublish fornecido)." -ForegroundColor Gray
}

# Determinar pasta de publicacao
if ([string]::IsNullOrWhiteSpace($PublishDir)) {
    $PublishDir = Join-Path $ProjectDir "bin\Release\net10.0-windows10.0.19041.0\win-x64\publish"
}

$DllPath = Join-Path $PublishDir "OpenTECHub.dll"
if (-not (Test-Path $DllPath)) {
    Write-Error "DLL de publicacao nao encontrada em '$DllPath'. Execute sem -SkipPublish primeiro."
    exit 1
}

$version = (Get-Item $DllPath).VersionInfo.ProductVersion
Write-Host "`n[2/3] Versao detectada no binario: $version" -ForegroundColor Cyan

# Localizar compilador Inno Setup
Write-Host "`n[3/3] Localizando compilador do Inno Setup (ISCC.exe)..." -ForegroundColor Yellow
# This bench keeps its tooling on D:; the Program Files paths stay for a stock install.
$isccCandidates = @(
    "D:\Arquivos_de_Programas\Inno Setup 7\ISCC.exe",
    "D:\Arquivos_de_Programas\Inno Setup 6\ISCC.exe",
    "C:\Program Files\Inno Setup 7\ISCC.exe",
    "C:\Program Files (x86)\Inno Setup 6\ISCC.exe",
    "C:\Program Files\Inno Setup 6\ISCC.exe",
    (Get-Command iscc.exe -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Source -First 1)
)

$isccPath = $isccCandidates | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1

if ($isccPath) {
    Write-Host "ISCC encontrado: $isccPath" -ForegroundColor Green
    Write-Host "Compilando script Inno Setup..." -ForegroundColor Yellow
    
    $outputDir = Join-Path $ScriptDir "Output"
    if (-not (Test-Path $outputDir)) {
        New-Item -ItemType Directory -Path $outputDir -Force | Out-Null
    }

    $isccArgs = @(
        "/DPublishDir=$PublishDir",
        $IssFile
    )

    & $isccPath @isccArgs
    if ($LASTEXITCODE -ne 0) {
        Write-Error "Erro ao compilar instalador com ISCC. Codigo: $LASTEXITCODE"
        exit $LASTEXITCODE
    }

    $setupExe = Join-Path $outputDir "OpenTECHub_Setup_v$version.exe"
    Write-Host "`nInstalador gerado com sucesso!" -ForegroundColor Green
    Write-Host "Caminho do arquivo: $setupExe" -ForegroundColor White
} else {
    Write-Host "`n[AVISO] Compilador Inno Setup 6 (ISCC.exe) nao foi encontrado no sistema." -ForegroundColor Magenta
    Write-Host "A pasta publicada esta pronta em: $PublishDir" -ForegroundColor White
    Write-Host "Para gerar o arquivo '.exe' do instalador:" -ForegroundColor White
    Write-Host "  1. Baixe e instale o Inno Setup 6 em: https://jrsoftware.org/isdl.php" -ForegroundColor Cyan
    Write-Host "  2. Execute este script novamente: .\build_installer.ps1" -ForegroundColor Cyan
    Write-Host "  ou abra 'installer\OpenTECHub_Setup.iss' no Inno Setup e pressione F9." -ForegroundColor Cyan
}

Write-Host "`nOperacao finalizada." -ForegroundColor Cyan

