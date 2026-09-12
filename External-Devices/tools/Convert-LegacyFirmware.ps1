[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('pump', 'flowmeter', 'biomass')]
    [string]$Device
)

$externalRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path

function Assert-UnderExternalRoot([string]$Path) {
    $fullPath = [System.IO.Path]::GetFullPath($Path)
    if (-not $fullPath.StartsWith($externalRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Unsafe path outside External-Devices: $fullPath"
    }
    return $fullPath
}

function Clean-Lines([string[]]$Lines) {
    $result = [System.Collections.Generic.List[string]]::new()
    $insideBlockComment = $false

    foreach ($line in $Lines) {
        $trimmed = $line.TrimStart()

        if ($insideBlockComment) {
            $endIndex = $line.IndexOf('*/')
            if ($endIndex -ge 0) {
                $insideBlockComment = $false
                $tail = $line.Substring($endIndex + 2)
                if (-not [string]::IsNullOrWhiteSpace($tail)) {
                    $result.Add($tail)
                }
            }
            continue
        }

        if ($trimmed.StartsWith('/*')) {
            $endIndex = $line.IndexOf('*/')
            if ($endIndex -lt 0) {
                $insideBlockComment = $true
                continue
            }

            $tail = $line.Substring($endIndex + 2)
            if ([string]::IsNullOrWhiteSpace($tail)) {
                continue
            }
        }

        if ($trimmed -match '^//\s*[-=─*]{3,}' -or
            $trimmed -match '^//\s*(NEW|FIXED|CHANGED|ADDED|OPTIMIZATION|Modified prototype)\b' -or
            $trimmed -match '^//\s*\.\.\.\s*\(existing code') {
            continue
        }

        $result.Add($line)
    }

    $withoutLongRuns = [System.Collections.Generic.List[string]]::new()
    $commentRun = [System.Collections.Generic.List[string]]::new()
    function Flush-CommentRun {
        if ($commentRun.Count -gt 0 -and $commentRun.Count -lt 4) {
            foreach ($commentLine in $commentRun) { $withoutLongRuns.Add($commentLine) }
        }
        $commentRun.Clear()
    }
    foreach ($resultLine in $result) {
        if ($resultLine -match '^\s*//') {
            $commentRun.Add($resultLine)
        } else {
            Flush-CommentRun
            $withoutLongRuns.Add($resultLine)
        }
    }
    Flush-CommentRun
    return $withoutLongRuns.ToArray()
}

function Get-LongCommentNotes([string[]]$Lines, [string]$Label) {
    $notes = [System.Collections.Generic.List[string]]::new()
    $run = [System.Collections.Generic.List[string]]::new()
    function Flush-NoteRun {
        if ($run.Count -ge 4) {
            $notes.Add("## $Label")
            $notes.Add('')
            foreach ($commentLine in $run) {
                $clean = $commentLine -replace '^\s*//\s?', ''
                $notes.Add('> ' + $clean)
            }
            $notes.Add('')
        }
        $run.Clear()
    }
    foreach ($line in $Lines) {
        if ($line -match '^\s*//') { $run.Add($line) } else { Flush-NoteRun }
    }
    Flush-NoteRun
    return $notes.ToArray()
}

function Slice([string[]]$Lines, [int]$Start, [int]$End) {
    return $Lines[($Start - 1)..($End - 1)]
}

function Write-Text([string]$Path, [string[]]$Lines) {
    $resolvedPath = Assert-UnderExternalRoot $Path
    $directory = Split-Path -Parent $resolvedPath
    if (-not (Test-Path -LiteralPath $directory)) {
        New-Item -ItemType Directory -Path $directory | Out-Null
    }
    [System.IO.File]::WriteAllLines($resolvedPath, $Lines, [System.Text.UTF8Encoding]::new($false))
}

$configurations = @{
    pump = @{
        Baseline = 'External-Devices\bomba-peristaltica\archive\active-baseline\peristaltic-pump-v4.ino'
        Output = 'External-Devices\bomba-peristaltica\firmware\peristaltic-pump'
        Sketch = 'peristaltic-pump'
        Preamble = @(28, 258)
        Groups = @(
            @('hardware/PwmRuntime.h', 259, 321),
            @('core/Lifecycle.h', 322, 516),
            @('storage/RuntimeStateStore.h', 517, 581),
            @('control/OperationController.h', 582, 991),
            @('control/SensorAndConversion.h', 992, 1034),
            @('storage/ConfigStore.h', 1035, 1091),
            @('protocol/TelemetryCodec.h', 1092, 1164),
            @('network/HubClient.h', 1165, 1389)
        )
    }
    flowmeter = @{
        Baseline = 'External-Devices\fluxometro\archive\active-baseline\flowmeter-v10.ino'
        Output = 'External-Devices\fluxometro\firmware\flowmeter'
        Sketch = 'flowmeter'
        Preamble = @(93, 336)
        Groups = @(
            @('core/Lifecycle.h', 337, 599),
            @('api/WebSocketApi.h', 600, 643),
            @('tasks/TaskRuntime.h', 644, 856),
            @('api/OtaService.h', 857, 926),
            @('protocol/CommandCodec.h', 927, 1144),
            @('hardware/FlowIo.h', 1145, 1209),
            @('storage/CalibrationStore.h', 1210, 1273)
        )
    }
    biomass = @{
        Baseline = 'External-Devices\sensor-biomassa\archive\active-baseline\biomass-sensor-v5.3.ino'
        Output = 'External-Devices\sensor-biomassa\firmware\biomass-sensor'
        Sketch = 'biomass-sensor'
        Preamble = @(354, 818)
        Groups = @(
            @('storage/Crc.h', 819, 845),
            @('core/ServiceRuntime.h', 846, 880),
            @('filtering/SampleFilter.h', 881, 912),
            @('sensor/Veml7700Driver.h', 913, 1273),
            @('storage/Stores.h', 1274, 1479),
            @('history/SampleHistory.h', 1480, 1734),
            @('measurement/BlankingAndRange.h', 1735, 2141),
            @('measurement/MeasurementPipeline.h', 2142, 2335),
            @('protocol/CommandCodec.h', 2336, 2914),
            @('protocol/TelemetryAndHub.h', 2915, 3132),
            @('api/LocalHttpApi.h', 3133, 3199),
            @('core/Lifecycle.h', 3200, 3409)
        )
    }
}

$configuration = $configurations[$Device]
$baselinePath = Assert-UnderExternalRoot $configuration.Baseline
$outputPath = Assert-UnderExternalRoot $configuration.Output
$lines = [System.IO.File]::ReadAllLines($baselinePath)

# Remove only implementation fragments produced by older versions of this
# converter. Arduino's sketch builder does not copy .inc files into the build
# tree, so private implementation fragments intentionally use .h below.
$sourcePath = Join-Path $outputPath 'src'
if (Test-Path -LiteralPath $sourcePath) {
    Get-ChildItem -LiteralPath $sourcePath -Recurse -File -Filter '*.inc' |
        ForEach-Object {
            $safePath = Assert-UnderExternalRoot $_.FullName
            Remove-Item -LiteralPath $safePath -Force
        }
}

$notes = [System.Collections.Generic.List[string]]::new()
$rawPreamble = Slice $lines $configuration.Preamble[0] $configuration.Preamble[1]
$preambleNotes = @(Get-LongCommentNotes $rawPreamble 'Configuração e estado global')
if ($preambleNotes.Count -gt 0) { $notes.AddRange([string[]]$preambleNotes) }
$preamble = Clean-Lines $rawPreamble
$includeLines = [System.Collections.Generic.List[string]]::new()

foreach ($group in $configuration.Groups) {
    $relativePath = $group[0]
    $rawGroupLines = Slice $lines $group[1] $group[2]
    $groupNotes = @(Get-LongCommentNotes $rawGroupLines $relativePath)
    if ($groupNotes.Count -gt 0) { $notes.AddRange([string[]]$groupNotes) }
    $groupLines = Clean-Lines $rawGroupLines
    if ($relativePath -eq 'core/Lifecycle.h') {
        $groupLines = $groupLines |
            ForEach-Object { $_ -replace '^void setup\(\)', 'void firmwareSetup()' -replace '^void loop\(\)', 'void firmwareLoop()' }
    }
    Write-Text (Join-Path $outputPath ('src\' + $relativePath.Replace('/', '\'))) $groupLines
    $includeLines.Add('#include "../' + $relativePath.Replace('\', '/') + '"')
}

$firmwareApp = @('#include "FirmwareApp.h"', '') + $preamble + @('') + $includeLines.ToArray()
Write-Text (Join-Path $outputPath 'src\core\FirmwareApp.cpp') $firmwareApp
Write-Text (Join-Path $outputPath 'src\core\FirmwareApp.h') @('#pragma once', '', 'void firmwareSetup();', 'void firmwareLoop();')
Write-Text (Join-Path $outputPath ($configuration.Sketch + '.ino')) @(
    '#include "src/core/FirmwareApp.h"',
    '',
    'void setup() {',
    '  firmwareSetup();',
    '}',
    '',
    'void loop() {',
    '  firmwareLoop();',
    '}'
)

$notesHeader = @(
    "# Notas importadas — $Device",
    '',
    'Comentários extensos retirados do firmware ativo durante a reorganização.',
    'O texto abaixo preserva o contexto histórico/técnico do monólito; valide-o',
    'contra hardware antes de tratá-lo como especificação atual.',
    ''
)
Write-Text (Join-Path (Split-Path -Parent (Split-Path -Parent $outputPath)) 'docs\IMPLEMENTATION_NOTES_IMPORTED.md') ($notesHeader + $notes.ToArray())

Write-Output "Converted $Device from $($lines.Count) monolithic lines into $($configuration.Groups.Count) implementation modules."
