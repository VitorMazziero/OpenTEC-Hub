[CmdletBinding()]
param()

$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$expected = [ordered]@{
    'bomba-peristaltica\archive\active-baseline\peristaltic-pump-v4.ino' = '21332ca9899d26b0efb93ea1c47511f45ee3dfcb22120192fbcf9cf5a4ff6636'
    'fluxometro\archive\active-baseline\flowmeter-v10.ino' = '8b45121d1794886e3b7869c76999d5239ac95d59232f1201412e132ff31fa78b'
    'frasco-agitador\archive\active-baseline\flask-agitator-rev-h.ino' = 'b6d99772df2134c6c6e453b012a744c06de6620b511b807ea661ccaed4b6fdfa'
    'sensor-biomassa\archive\active-baseline\biomass-sensor-v5.3.ino' = 'd51318776f86bbd738f036be0f9faa31a7ca264325471f767c31873d3ea75974'
    'sensor-biomassa\archive\active-baseline\web_ui-v5.3.h' = 'd0fa9ff73f2a91ac60527b84a73eb9eecdebcfeb3c2f9fca0f2e9ad92b2e89a9'
    'sensor-distancia\archive\active-baseline\distance-sensor-v10.ino' = '4c621b5c126fd3e55bd3e17f5abd1f2cc6be4f8d8eaf9604d3450d7e9298d1a4'
}

$failures = @()
foreach ($entry in $expected.GetEnumerator()) {
    $path = Join-Path $root $entry.Key
    if (-not (Test-Path -LiteralPath $path)) {
        $failures += "Missing baseline: $($entry.Key)"
        continue
    }
    $actual = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actual -ne $entry.Value) { $failures += "Changed baseline: $($entry.Key)" }
}

if ($failures) { $failures | ForEach-Object { Write-Error $_ }; throw 'Firmware baseline verification failed.' }
Write-Output 'All six active firmware/UI baselines match their original SHA-256 hashes.'
