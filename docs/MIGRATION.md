# Migration Map — Python v.6 to C# TECNAL-Hub

> What each piece of the working Python app becomes, what gets fixed on the way,
> and what is deliberately left behind.
>
> **Source:** `D:\OneDrive\Doutorado_CNPq\_Automacao_Controle\_devices\TECNAL_control\_Wifi Hub\Software\_Windows App\v.6`
> (11,519 lines of Python across 18 modules)
>
> **Docs:** [README](README.md) · [Roadmap](ROADMAP.md) · [Protocol](PROTOCOL.md) · [Calibration](CALIBRATION.md) · [Architecture](ARCHITECTURE.md) · [Decisions](DECISIONS.md)

---

## 1. Why startup is slow

Two independent causes, both measurable, both fixed by the rebuild rather than by
any clever trick.

**Cause A — `import torch`.** `kLa_methods/kla_gassing_out_page.py` imports torch,
`torch.nn`, scipy.ndimage, scipy.optimize and pyqtgraph at module scope. v.6 already
tries to defer this (`main.py:335` comments "Faz a importação pesada"), but the tab
is constructed during window setup, so the deferral does not fully land. A frozen
build pays this on every launch.

*Fix:* the gassing-out estimator leaves the controller app entirely
([ROADMAP.md](ROADMAP.md#explicitly-deferred)).

**Cause B — serial port probing, and this is the bigger one.** `USBTransport.probe_ports`
walks every candidate COM port and calls the full `connect()` on each. Per port that is:

```text
  1.80 s   boot settle (mandatory, cannot be shortened)
+ 10 x     handshake attempt x (0.75 s readline timeout + 0.10 s sleep)
= up to   10.3 s per unresponsive port
```

A machine with a Bluetooth-adjacent virtual port, a USB-serial dongle and the ESP32
enumerated third spends **20+ seconds** before it finds the device. Worse,
`probe_ports` constructs its `USBConfig` **without a `cancel_event`**, so the probe
cannot be aborted once started — the window is unresponsive until it finishes.

*Fix:* three changes, all in Phase 0/1.
1. Probe candidates **in parallel**, not serially.
2. Try the **remembered port first** and short-circuit on success — the common case
   becomes one 1.8 s settle, not N of them.
3. Probe **after** the shell is on screen, never before, with a real cancellation token.

---

## 2. File-by-file mapping

### Communication — port nearly as-is

| v.6 | Lines | Becomes | Notes |
|---|---:|---|---|
| `communication/transport.py` | 532 | `Protocol/SerialTransport.cs`, `HttpTransport.cs` | **Clean already.** Near-direct port; keep every timing constant. |
| `communication/data_parser.py` | 381 | `Protocol/TelemetryParser.cs`, `SpikeFilter.cs` | **Clean already.** Key-by-key port; sentinel semantics matter. |
| `communication/connection_manager.py` | 989 | `Services/Communication/ConnectionManager.cs` | Qt signals become C# events / `IObservable`. The state machine itself is sound — keep it. Split logging and the alarm loop out; they are unrelated concerns that landed here. |

> This is the part of v.6 with the worst reputation and the best code. The
> `_worker_loop` / request-queue design is genuinely good. Resist rewriting it.

### UI — replaced wholesale

| v.6 | Lines | Becomes |
|---|---:|---|
| `ui/parameter_settings_page.py` | 1526 | Synoptic + detail pane + per-subsystem ViewModels |
| `ui/configurations_page.py` | 1272 | Connection chip + popover, typed Settings, pH control and the app-side pH/O₂ calibration procedures |
| `ui/graphs_page.py` | 444 | `Views/ChartsView.xaml` (ScottPlot) |
| `ui/pump_mode_window.py` | 537 | `Views/PumpProfileView.xaml` (Phase 3) |
| `ui/flow_calibration_dialog.py` | 500 | `Views/CalibrationView.xaml` + `FlowCalibrationViewModel` (Phase 2 WP3) |
| `ui/configurations_page_integration.py` | 296 | **Deleted.** Glue that exists only because the pages could not talk to each other. MVVM removes the need. |

### Control

| v.6 | Lines | Becomes |
|---|---:|---|
| `kLa_methods/simple_cascade_control.py` | 422 | `Services/Control/CascadeController.cs` — **rewritten** from the ReceitasTECNAL design, not ported |
| `kLa_methods/kla_cascade_control.py` | 368 | `Services/Control/KlaPathController.cs` — **rewritten** per the manuscript |
| `kLa_methods/kla_cascade_page.py` | 918 | Phase 2 kLa view |
| `kLa_methods/kla_gassing_out_page.py` | 1716 | **Removed** — separate project |

### Application

| v.6 | Lines | Becomes |
|---|---:|---|
| `main.py` | 1086 | `App.xaml.cs` (~80) + `ShellViewModel` + `SettingsService` |
| `config/preferences.py` | 397 | `Services/Persistence/SettingsService.cs` |

> `main.py`'s `collect_preferences` (534-721) and `apply_preferences` (722-905)
> are **370 lines of hand-written field marshalling** — every widget read and
> written by name, in two places that must be kept in sync by hand. This is the
> single largest source of "add a field, forget a line, silently lose the setting".
> It becomes a typed record and `System.Text.Json`, and disappears.

**Calibration ownership after WP3:** pH and oxygen coefficients remain in the app parser;
accepted pH is echoed to the module as the quoted `pHCal` display value. The six airflow
coefficients alone are sent to the dedicated flowmeter. The exact procedures and safety
interlocks are in [CALIBRATION.md](CALIBRATION.md).

---

## 3. Known defects carried in from v.6

Found while reading the source. Each is a real behaviour difference to fix, keep,
or decide about — **not** a licence to redesign the wire.

| # | Defect | Location | Disposition |
|---|---|---|---|
| 1 | `probe_ports` builds `USBConfig` with no `cancel_event`, so probing cannot be cancelled | `transport.py` `probe_ports` | **Fix.** Cancellation token throughout. |
| 2 | Default calibration in code (`oxy_a=0.030573419314`, `oxy_b=-25.09036520919`) differs from the field values in `preferences.json` (`0.0305473419314`, `-25.09136520919`) | `data_parser.py` `ParserConfig` vs `preferences.json` | **Fixed.** Typed defaults match the field values; guided procedures replace both coefficients atomically. |
| 3 | `biomass_hd_mode` is declared on `SensorReadings` and exposed via `biomassHdMode`, but `_parse_biomass` never assigns it — always `False` | `data_parser.py` | **Fix or drop.** Confirm whether the firmware sends a HD-mode key at all (see [PROTOCOL.md](PROTOCOL.md#5-open-questions-for-hardware-verification)). |
| 4 | Spike-filter thresholds are in **raw ADC counts** while the user tunes **calibration** — recalibrating silently changes filter aggressiveness | `data_parser.py` | **Documented, behaviour preserved.** The procedure warns that curves do not rescale raw thresholds. Converting units still needs a bench comparison. |
| 5 | `dir_val` is computed from `_last_dir`, then immediately overwritten on the next line — the `_last_dir` memory is dead code | `parameter_settings_page.py` `send_agitator` | **Fix.** Decide whether direction should be remembered at zero percent; implement one behaviour deliberately. |
| 6 | `WiFiTransport.write()` hard-codes `timeout=0.5` and ignores the configured timeouts | `transport.py` | **Fix.** Honour config. |
| 7 | Wi-Fi `read()` returns `None` for both "no new data" (304) and "exception" — link loss is invisible until the separate heartbeat notices | `transport.py` | **Fix.** Distinguish the two; a read fault should feed the state machine directly. |
| 8 | `FlowCommandDeliveries` and `FlowCommandAgeMs` are parsed but never surfaced anywhere | `data_parser.py` | **Use them.** They are exactly the command round-trip diagnostics the connection popover should show. |
| 9 | `transport.py`'s threading docstring says read/write happen on the Qt thread, but `_flush_commands` calls `write()` from the worker thread. Correctness rests on `_rw_lock`, not on the documented contract | `transport.py` vs `connection_manager.py` | **Fix the design, not the comment.** In C#, one owner for the link; all I/O through it. |
| 10 | `com_port_edit` is persisted as `"Nenhuma porta disponível"` — a UI label stored as configuration | `preferences.json` | **Fix.** Settings store values; labels are presentation. |
| 11 | Bare `except:` around every setpoint parse silently substitutes a default (e.g. a malformed pH becomes 7) | `parameter_settings_page.py`, many sites | **Fixed.** Every pH field has an explicit range; malformed enabled state never sends, while an off command remains available through the last valid auxiliaries. |

> Item 11 deserves emphasis. `except: ph_value = 7` means a typo in the pH field
> sends setpoint 7 to the reactor and says nothing. Input validation must be
> visible in the new UI.

---

## 4. What is deliberately NOT changed

| Kept | Why |
|---|---|
| The wire format, every key, every unit | Firmware is frozen. See [PROTOCOL.md](PROTOCOL.md). |
| 1.8 s USB boot settle | Below it the handshake reads bootloader noise. |
| DTR/RTS pulse | Required to reset the ESP32 cleanly. |
| `v_Flow` inverted logic | Firmware semantics. |
| Forcing both valves closed on flow disable | Safety behaviour, deliberate in v.6. |
| Drain-to-newest-line on serial read | Prevents the UI lagging behind the device. |
| The `ConnectionManager` state machine shape | It works and the transitions are correct. |
| CSV log column format | Existing analysis scripts read it. |
