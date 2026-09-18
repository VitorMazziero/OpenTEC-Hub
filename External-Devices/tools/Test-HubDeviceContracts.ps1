[CmdletBinding()]
param()

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$hubRoot = Join-Path $repoRoot 'ESP32S3-HUB\ESP32S3-HUB\src'
$externalRoot = Join-Path $repoRoot 'External-Devices'
$failures = [System.Collections.Generic.List[string]]::new()

function Read-SourceTree([string]$path) {
    return (Get-ChildItem -LiteralPath $path -Recurse -File |
        Where-Object { $_.Extension -in '.ino', '.h', '.hpp', '.cpp' } |
        ForEach-Object { [System.IO.File]::ReadAllText($_.FullName) }) -join "`n"
}

function Require([string]$label, [string]$text, [string[]]$tokens) {
    foreach ($token in $tokens) {
        if (-not $text.Contains($token)) {
            $failures.Add("$label missing token: $token")
        }
    }
}

$hub = Read-SourceTree $hubRoot
$pump = Read-SourceTree (Join-Path $externalRoot 'bomba-peristaltica\firmware\peristaltic-pump')
$flow = Read-SourceTree (Join-Path $externalRoot 'fluxometro\firmware\flowmeter')
$agitator = Read-SourceTree (Join-Path $externalRoot 'frasco-agitador\firmware\flask-agitator')
$biomass = Read-SourceTree (Join-Path $externalRoot 'sensor-biomassa\firmware\biomass-sensor')
$distance = Read-SourceTree (Join-Path $externalRoot 'sensor-distancia\firmware\distance-sensor')

Require 'Hub routes' $hub @('/distance', '/flowData', '/flowCommand', '/biomassData', '/biomassCommand', '/pumpData', '/pumpCommand', '/agitatorHello', '/agitatorData', '/agitatorCommand', '/nodeHello', '/nodes')
Require 'Hub reliability' $hub @('cmd_id', 'ack_cmd_id', 'takeReliable', 'ackReliable')

Require 'Distance node' $distance @('/distance', 'distance=', '&time=', '/nodeHello', '&offset=', '&ack_cmd_id=', 'processConfigUpdate(body')
Require 'Pump node' $pump @('/pumpData', '/pumpCommand', 'mode=', '&flow=', '&vol=', '&v_tgt=', 'cmd_id', 'ack_cmd_id', '/nodeHello')

# Calibracao polinomial da bomba (3.12, commit 73e9a80), que substituiu o par
# linear slope/intercept: quartica na faixa baixa (a1..c1), quadratica na faixa
# alta (k2, f2, c2) e a velocidade de transicao entre elas. O nome difere por
# direcao e nao e erro de digitacao: o no ecoa 'trans_speed' na telemetria e
# aceita 'transition_speed' no comando.
Require 'Pump calibration echo' $pump @('&a1=', '&b1=', '&k1=', '&f1=', '&c1=', '&k2=', '&f2=', '&c2=', '&trans_speed=', '&cal_crc=')
Require 'Pump calibration command' $pump @('"transition_speed"')
Require 'Flowmeter node' $flow @('/flowData', '/flowCommand', 'seconds=', '&flow_voltage=', '&flow_rate=', '&flow_setpoint=', '&valve1State=', '&valve2State=', '&kp=', 'cmd_id', 'ack_cmd_id', '/nodeHello')
Require 'Biomass node' $biomass @('/biomassData', '/biomassCommand', 'absorbance=', '&raw=', '&gear=', 'cmd_id', 'ack_cmd_id', '&idle=', '/nodeHello')
Require 'Agitator node' $agitator @('/agitatorHello', '/nodeHello', '/agitatorData', '/agitatorCommand', 'pct=', '&dir=', '&pot=', '&src=', 'cmd_id', 'ack_cmd_id')

Require 'Hub distance fields' $hub @('hasParam("distance")', 'hasParam("time")', 'hasParam("offset")', 'hasParam("sample_ms")', 'hasParam("send_ms")')
Require 'Hub pump fields' $hub @('hasParam("mode")', 'hasParam("flow")', 'hasParam("vol")', 'hasParam("v_tgt")')
Require 'Hub pump calibration' $hub @('hasParam("a1")', 'hasParam("b1")', 'hasParam("k1")', 'hasParam("f1")', 'hasParam("c1")', 'hasParam("k2")', 'hasParam("f2")', 'hasParam("c2")', 'hasParam("trans_speed")', 'hasParam("cal_crc")')
Require 'Hub flow fields' $hub @('hasParam("seconds")', 'hasParam("flow_voltage")', 'hasParam("flow_rate")', 'hasParam("flow_setpoint")', 'hasParam("valve1State")', 'hasParam("valve2State")', 'hasParam("kp")', 'hasParam("ki")', 'hasParam("ramp")', 'hasParam("ff_gain")', 'hasParam("ff_offset")', 'hasParam("flow_output")', 'hasParam("flow_setpoint_corrected")')
Require 'Hub biomass fields' $hub @('hasParam("absorbance")', 'hasParam("raw")', 'hasParam("idle")', 'hasParam("gear")', 'hasParam("ema")', 'hasParam("probe_ms")')
Require 'Hub agitator fields' $hub @('hasParam("pct")', 'hasParam("dir")', 'hasParam("pot")', 'hasParam("src")')
Require 'Hub node identity (10.1)' $hub @('appendNodeIdentity(jsonResponse, "Distance"', 'appendNodeIdentity(jsonResponse, "Agitator"', 'appendNodeIdentity(jsonResponse, "Pump"', 'appendNodeIdentity(jsonResponse, "Flowmeter"', 'appendNodeIdentity(jsonResponse, "Biomass"', 'NodeVer', 'NodeMac', 'hub_time_ms', 'last_hello_ms', 'last_data_ms')

if ($failures.Count -gt 0) {
    $failures | ForEach-Object { Write-Error $_ }
    throw "$($failures.Count) Hub/device contract checks failed."
}

Write-Output 'Hub/device contract check passed for 12 routes, /nodeHello dynamic registration, node identity keys (*IP/*NodeVer/*NodeMac), required telemetry fields, cmd_id and ack_cmd_id.'

