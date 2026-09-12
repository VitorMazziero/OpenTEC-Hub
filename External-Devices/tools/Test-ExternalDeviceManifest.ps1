[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$DevicePath,

    [Parameter(Mandatory = $true)]
    [string]$ManifestPath
)

$resolvedDevice = (Resolve-Path -LiteralPath $DevicePath).Path
$resolvedManifest = (Resolve-Path -LiteralPath $ManifestPath).Path
$expected = Get-Content -LiteralPath $resolvedManifest |
    Where-Object { $_ -and -not $_.StartsWith('#') } |
    ForEach-Object { ($_ -split ' ', 2)[0].ToLowerInvariant() } |
    Group-Object -NoElement |
    Sort-Object Name

$actual = Get-ChildItem -LiteralPath $resolvedDevice -Recurse -Force -File |
    Where-Object { $_.FullName -ne $resolvedManifest } |
    ForEach-Object { (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() } |
    Group-Object -NoElement |
    Sort-Object Name

$actualByHash = @{}
foreach ($entry in $actual) { $actualByHash[$entry.Name] = $entry.Count }
$missing = foreach ($entry in $expected) {
    $actualCount = if ($actualByHash.ContainsKey($entry.Name)) { $actualByHash[$entry.Name] } else { 0 }
    if ($actualCount -lt $entry.Count) {
        [pscustomobject]@{ Hash = $entry.Name; Expected = $entry.Count; Actual = $actualCount }
    }
}
if ($missing) {
    $missing | Format-Table -AutoSize
    throw 'Manifest verification failed: imported file content is missing.'
}

$expectedCount = ($expected | Measure-Object Count -Sum).Sum
Write-Output "Verified preservation of $expectedCount imported file hashes; additional reorganized files are allowed."
