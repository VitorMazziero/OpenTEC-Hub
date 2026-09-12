[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$DevicePath,

    [Parameter(Mandatory = $true)]
    [string]$OutputPath
)

$resolvedDevice = (Resolve-Path -LiteralPath $DevicePath).Path
$resolvedRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
$resolvedOutput = [System.IO.Path]::GetFullPath($OutputPath)

if (-not $resolvedDevice.StartsWith($resolvedRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "DevicePath must be inside $resolvedRoot"
}

if (-not $resolvedOutput.StartsWith($resolvedRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "OutputPath must be inside $resolvedRoot"
}

$outputDirectory = Split-Path -Parent $resolvedOutput
if (-not (Test-Path -LiteralPath $outputDirectory)) {
    New-Item -ItemType Directory -Path $outputDirectory | Out-Null
}

$entries = Get-ChildItem -LiteralPath $resolvedDevice -Recurse -Force -File |
    Where-Object { $_.FullName -ne $resolvedOutput } |
    Sort-Object FullName |
    ForEach-Object {
        $relativePath = $_.FullName.Substring($resolvedDevice.Length + 1).Replace('\', '/')
        $hash = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        "$hash *$relativePath"
    }

$header = @(
    "# SHA-256 manifest before External-Devices reorganization"
    "# Source: $resolvedDevice"
    "# Generated: $([DateTimeOffset]::Now.ToString('o'))"
)

[System.IO.File]::WriteAllLines($resolvedOutput, $header + $entries, [System.Text.UTF8Encoding]::new($false))
Write-Output "$($entries.Count) entries written to $resolvedOutput"
