[CmdletBinding()]
param()

function Extract-JsonFloat([string]$payload, [string]$key) {
    $searchKey = '"' + $key + '"'
    $keyIndex = $payload.IndexOf($searchKey)
    if ($keyIndex -lt 0) { return $null }
    $colon = $payload.IndexOf(':', $keyIndex + $searchKey.Length)
    if ($colon -lt 0) { return $null }
    $valueIndex = $colon + 1
    while ($valueIndex -lt $payload.Length -and [char]::IsWhiteSpace($payload[$valueIndex])) {
        $valueIndex++
    }
    if ($valueIndex -ge $payload.Length) { return $null }
    $sub = $payload.Substring($valueIndex)
    $match = [regex]::Match($sub, '^[-+]?[0-9]*\.?[0-9]+([eE][-+]?[0-9]+)?')
    if (-not $match.Success) { return $null }
    return [float]$match.Value
}

function Extract-JsonInt([string]$payload, [string]$key) {
    $searchKey = '"' + $key + '"'
    $keyIndex = $payload.IndexOf($searchKey)
    if ($keyIndex -lt 0) { return $null }
    $colon = $payload.IndexOf(':', $keyIndex + $searchKey.Length)
    if ($colon -lt 0) { return $null }
    $valueIndex = $colon + 1
    while ($valueIndex -lt $payload.Length -and [char]::IsWhiteSpace($payload[$valueIndex])) {
        $valueIndex++
    }
    if ($valueIndex -ge $payload.Length) { return $null }
    $sub = $payload.Substring($valueIndex)
    $match = [regex]::Match($sub, '^[-+]?[0-9]+')
    if (-not $match.Success) { return $null }
    return [int]$match.Value
}

function Extract-CmdId([string]$payload) {
    $keyIndex = $payload.IndexOf('"cmd_id"')
    if ($keyIndex -lt 0) { return 0 }
    $colon = $payload.IndexOf(':', $keyIndex + 8)
    if ($colon -lt 0) { return 0 }
    $sub = $payload.Substring($colon + 1).TrimStart()
    $match = [regex]::Match($sub, '^[0-9]+')
    if (-not $match.Success) { return 0 }
    return [uint32]$match.Value
}

function Parse-AndApply([string]$payload) {
    $first = $payload.IndexOf('{')
    $last = $payload.LastIndexOf('}')
    if ($first -lt 0 -or $last -le $first) { return $false }
    
    $valid = $false
    $rpm = Extract-JsonFloat $payload 'RPM_percent'
    if ($null -ne $rpm) {
        if ($rpm -ge 0.0 -and $rpm -le 100.0) { $valid = $true }
    }
    
    $dir = Extract-JsonInt $payload 'Dir'
    if ($null -ne $dir) {
        if ($dir -eq 0 -or $dir -eq 1) { $valid = $true }
    }
    
    $pot = Extract-JsonInt $payload 'ActivePot'
    if ($null -ne $pot) {
        if ($pot -eq 0 -or $pot -eq 1) { $valid = $true }
    }
    return $valid
}

$tests = @(
    @{ Name = 'Hub standard payload'; Json = '{"cmd_id":12,"RPM_percent":45.5,"Dir":1,"ActivePot":0}'; ExpectedValid = $true; ExpectedCmdId = 12 },
    @{ Name = 'Reordered with whitespace'; Json = "`n{`n  `"ActivePot`": 1,`n  `"RPM_percent`" : 80.0,`n  `"Dir`" : 0`n}`n"; ExpectedValid = $true; ExpectedCmdId = 0 },
    @{ Name = 'Partial RPM only'; Json = '{"RPM_percent":100.0}'; ExpectedValid = $true; ExpectedCmdId = 0 },
    @{ Name = 'Partial Dir only'; Json = '{"Dir":0}'; ExpectedValid = $true; ExpectedCmdId = 0 },
    @{ Name = 'Partial ActivePot only'; Json = '{"ActivePot":1}'; ExpectedValid = $true; ExpectedCmdId = 0 },
    @{ Name = 'Out of range RPM high'; Json = '{"RPM_percent":105.0}'; ExpectedValid = $false; ExpectedCmdId = 0 },
    @{ Name = 'Out of range RPM negative'; Json = '{"RPM_percent":-5.0}'; ExpectedValid = $false; ExpectedCmdId = 0 },
    @{ Name = 'Invalid Dir'; Json = '{"Dir":2}'; ExpectedValid = $false; ExpectedCmdId = 0 },
    @{ Name = 'Invalid ActivePot'; Json = '{"ActivePot":-1}'; ExpectedValid = $false; ExpectedCmdId = 0 },
    @{ Name = 'Malformed string'; Json = 'not json at all'; ExpectedValid = $false; ExpectedCmdId = 0 },
    @{ Name = 'Empty braces'; Json = '{}'; ExpectedValid = $false; ExpectedCmdId = 0 },
    @{ Name = 'Hub command with spaces in cmd_id'; Json = '{"cmd_id" : 9999, "RPM_percent": 10.0}'; ExpectedValid = $true; ExpectedCmdId = 9999 }
)

$failures = 0
foreach ($t in $tests) {
    $isValid = Parse-AndApply $t.Json
    $cmdId = Extract-CmdId $t.Json
    if ($isValid -ne $t.ExpectedValid -or $cmdId -ne $t.ExpectedCmdId) {
        Write-Error "Test '$($t.Name)' failed: Got Valid=$isValid (exp $($t.ExpectedValid)), CmdId=$cmdId (exp $($t.ExpectedCmdId))"
        $failures++
    }
}

if ($failures -gt 0) {
    throw "$failures parser unit tests failed."
}
Write-Output "All $($tests.Count) agitator manual parser test cases passed successfully."
