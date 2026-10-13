[CmdletBinding()]
param(
    [switch]$SkipClean
)

$ErrorActionPreference = 'Stop'
$appRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$project = Join-Path $appRoot 'src\OpenTECHub\OpenTECHub.csproj'
$publishDir = Join-Path $appRoot 'src\OpenTECHub\bin\Release\net10.0-windows10.0.19041.0\win-x64\publish'

if (-not $SkipClean) {
    if (Get-Process OpenTECHub -ErrorAction SilentlyContinue) {
        throw 'Feche o OpenTEC-Hub antes da limpeza. A sessao em execucao nao sera interrompida pelo script.'
    }

    # Only generated directories belonging to these explicit .NET projects.
    $projects = @(
        'src\OpenTECHub',
        'src\OpenTECHub.Protocol',
        'src\OpenTECHub.Simulator',
        'src\OpenTECHub.Harness',
        'tests\OpenTECHub.Tests',
        'tools\KlaCorpusAudit'
    )
    foreach ($relativeProject in $projects) {
        $projectDir = [IO.Path]::GetFullPath((Join-Path $appRoot $relativeProject))
        if (-not $projectDir.StartsWith($appRoot + [IO.Path]::DirectorySeparatorChar,
                [StringComparison]::OrdinalIgnoreCase)) {
            throw "Projeto fora da pasta Windows_app: $projectDir"
        }
        foreach ($generatedName in @('bin', 'obj')) {
            $generated = [IO.Path]::GetFullPath((Join-Path $projectDir $generatedName))
            if (-not $generated.StartsWith($projectDir + [IO.Path]::DirectorySeparatorChar,
                    [StringComparison]::OrdinalIgnoreCase)) {
                throw "Pasta gerada fora do projeto: $generated"
            }
            if (Test-Path -LiteralPath $generated) {
                Write-Host "Limpando $generated"
                Remove-Item -LiteralPath $generated -Recurse -Force
            }
        }
    }
}

& dotnet publish $project -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false
if ($LASTEXITCODE -ne 0) { throw "Falha na publicacao Release: $LASTEXITCODE" }

$publishedDll = Join-Path $publishDir 'OpenTECHub.dll'
$publishedExe = Join-Path $publishDir 'OpenTECHub.exe'
if (-not (Test-Path -LiteralPath $publishedDll) -or -not (Test-Path -LiteralPath $publishedExe)) {
    throw "Publicacao incompleta: $publishDir"
}
Write-Host "Release pronta: $publishedExe"
Write-Host "Versao: $((Get-Item -LiteralPath $publishedDll).VersionInfo.ProductVersion)"
Write-Host 'Instalador: .\Windows_app\installer\build_installer.ps1 -SkipPublish'
