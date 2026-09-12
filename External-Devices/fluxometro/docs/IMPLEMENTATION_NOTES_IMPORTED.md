# Notas importadas — flowmeter

Comentários extensos retirados do firmware ativo durante a reorganização.
O texto abaixo preserva o contexto histórico/técnico do monólito; valide-o
contra hardware antes de tratá-lo como especificação atual.

## Configuração e estado global

> ----- OTA firmware update -----
> GET /update serves a one-field form; POSTing the exported app image there
> (Arduino IDE: Sketch > Export Compiled Binary -> build/esp32.esp32.esp32/*.ino.bin)
> streams it into the inactive OTA slot and reboots into it. The boot pointer only
> moves after Update.end() verifies the image, so a bad or interrupted upload leaves
> the running firmware untouched. No login: the AP is open and so is this page.
> Hub polling, telemetry and reconnects stand down while a transfer is in flight so
> the Wi-Fi stack serves the upload alone. Cleared on completion, failure, or stall.

## Configuração e estado global

> Setpoint corrigido: the value actually handed to the MFC as the base of the PI
> output. The MFC over-delivers (1 -> 1.4, 5 -> 6, 12 -> 14.8), so the base is
> ff_gain * real + ff_offset, the least-squares inverse of those points. The PI
> only trims the residual on top of it.

## Configuração e estado global

> Link watchdog. WiFi.status() reporting WL_CONNECTED is not proof the path works:
> a lost DHCP lease, a full AP or a wedged socket all leave the station "connected"
> and mute. After this many consecutive failed telemetry posts (~5 s at 2 Hz) the
> association is torn down so wifiTask rebuilds it from scratch.

## Configuração e estado global

> Identifies this power-on to the hub. The hub adopts the flowmeter's reported state
> whenever no command is pending, so a silent reboot used to hand it a zero setpoint
> as if the operator had asked for it. A changed id tells the hub to re-assert its
> own desired state instead. Seeded in setup() from the hardware RNG.

## Configuração e estado global

> Schema v2 added a1/b1 and reset everything. v3 appended the feedforward pair.
> v4 re-seeded the curve. v5 (V10) appends ramp_rate/dac_hold, re-seeds the curve
> from the constants below and sets the PI gains the fast measurement chain wants;
> ff_* is kept when present. Older records migrate in place.

## Configuração e estado global

> Factory curve, as fitted on the hub (Curva do fluxometro, dois segmentos):
>   V <= 0.0545: y = a1 V^4 + b1 V^3 + k1 V^2 + f1 V + c1
>   V >  0.0545: y = k2 V^2 + f2 V + c2
> Both segments give 0.8713 L/min at the 0.0545 V threshold (no jump).

## core/Lifecycle.h

> First thing, before the serial settle delay: assert Valve Off. With dacHold the
> MCP4725 still holds the last setpoint through an ESP32 reboot (OTA included),
> and the valve must not be live on a stale command. The hub re-asserts the
> valve state once it sees the new boot_id; direct users send v_Flow=0.

## core/Lifecycle.h

> A client that vanishes mid-upload never reaches the completion handler. Without
> this the hub tasks would stay parked on otaInProgress forever. Only the flag is
> touched here: Update is owned by the AsyncTCP task, and aborting it from this
> task while a write is in flight frees the buffer under it (StoreProhibited).
> If data does resume, the chunk handler re-arms the flag and the upload goes on.

## tasks/TaskRuntime.h

> One client for the life of the task, with keep-alive enabled once. Each poll still
> does begin()/GET()/end(), but end() now hands the socket back instead of tearing it
> down: rebuilding it per request meant a TCP handshake and teardown every 100 ms.
> Together with telemetry that was ~12 connections/s piling into TIME_WAIT on the
> hub's lwIP, which is where socket exhaustion showed up as "lost" commands.

## tasks/TaskRuntime.h

> ----- Non-blocking AP+STA reconnection logic -----
> No broad WiFi scan is used here. ESP32 AP+STA has one physical radio; scans
> can stall the direct Flowmeter_AP/WebSocket link. Both known TECNAL hubs use
> channel 6, so association is attempted directly on that channel.

## tasks/TaskRuntime.h

> Do not flip hubs on the first timeout. The usual cause is the right hub
> being momentarily busy, and flipping sent the next attempt to a module
> that is not even powered - roughly 13 s wasted per flip, so a hub that
> never went away took ~26 s to come back instead of ~13 s.

