# UI Design Specification

> Visual language and layout for TECNAL-Hub, as decided 2026-08-19.
>
> **Docs:** [README](README.md) · [Roadmap](ROADMAP.md) · [Architecture](ARCHITECTURE.md) · [Decisions](DECISIONS.md)

---

## 1. Visual identity

**Fluent / Windows 11, light and dark, following the system theme by default** with
a manual override in settings.

| | |
|---|---|
| Accent | `#0067C0` light · `#60CDFF` dark — **interaction only, never process data** |
| Surfaces | Layered: base → card → elevated. No borders where a surface change will do. |
| Radius | 4 / 8 / 12 px. Cards are 8. |
| Type | Segoe UI Variable (Segoe UI fallback). Process readouts in a **tabular-figure** face so digits do not jitter at 1 Hz. |
| Spacing | 4 / 8 / 12 / 16 / 24. Nothing off this scale. |

All tokens live in `src/TecnalHub/Themes/Tokens.{Light,Dark,Shared}.xaml`. Views bind
to `*Brush` keys via `DynamicResource` — **never** to a `*Color` key and **never**
to a literal, so the runtime theme switch repaints without rebuilding the tree.

### Colour discipline

This is the rule that keeps an instrument UI readable:

> **Colour carries exactly one meaning: equipment state.** Chrome is greyscale.
> Accent is for things you can click. Five state colours, and nothing else:

| State | Meaning |
|---|---|
| Ok | At setpoint, healthy |
| Actuating | Controller is actively moving this actuator |
| Warning | Outside the acceptable band |
| Alarm | Alarm condition, or link lost |
| Idle | Disabled, or no data yet |

Chart series use the Okabe-Ito colour-blind-safe palette, which is a separate
concern from state colour and must not be mixed with it.

---

## 2. Main layout — the 2+1 hybrid

Three elements, all visible at once on a normal screen:

```text
┌──────────────────────────────────────────────────────────────────────┐
│  ◆ TECNAL-Hub                          ● Conectado · USB COM7 ▾   ⚙  │  title bar
├──────┬───────────────────────────────────────────────────────────────┤
│      │ 30.2°C↑ │ 790rpm→ │ 42.1%↓ │ 6.98→ │ 0.50L↑ │ 3kPa→ │        │  KPI strip
│ Pai  ├────────────────────────────────────┬──────────────────────────┤
│ nel  │                                    │  TEMPERATURA             │
│      │      30.2°C ▸╔═══════════════╗     │  ──────────────────────  │
│ Rec  │        ███   ║  ⟳ 790 rpm    ║     │  Atual      30.2 °C      │
│ eit  │              ║               ║     │  Setpoint  [ 30.0 ] °C   │
│      │      pH ▸    ║ ░░░░░░░░░░░░░ ║     │                          │
│ Grá  │      6.98    ║ ░░░  ⌁⌁⌁  ░░░ ║ ◂ OD│  ┌────────────────────┐  │
│ fic  │              ╚══════╤════════╝42.1%│  │      ╱‾‾‾‾‾‾‾‾‾‾   │  │
│      │        Bombas ▲     ⊕ 0.50 L/min   │  └────────────────────┘  │
│ kLa  │                                    │  ☑ Ativo    PID ▾        │
│      │                                    │                          │
│ Cfg  │            SINÓPTICO               │       PAINEL DETALHE     │
└──────┴────────────────────────────────────┴──────────────────────────┘
        nav rail          ~55%                        ~35%
```

**KPI strip** — always visible, on every page including Charts and Recipes. Small
tiles: value, unit, trend arrow, state dot. This is the "am I still safe?" glance,
and it is why the operator never has to navigate back to the dashboard.

**Synoptic** — a drawn bioreactor with live values pinned at the physical location
of the hardware they come from: jacket, impeller, probes, sparger, pumps, valves.
Clicking any element selects it. Vector XAML, not an image, so it themes and scales.

**Detail pane** — the selected element's full controls: current value, setpoint
entry, enable toggle, its chart, and a collapsible PID/advanced section. Only one
subsystem's controls are on screen at a time. **This is the thing that makes 12
subsystems stop feeling like a wall** — the synoptic keeps context, the pane keeps
focus.

### Responsive behaviour

| Window width | Layout |
|---|---|
| ≥ 1400 px | Nav rail · synoptic · detail pane, as drawn above |
| 1200-1400 px | Same, detail pane narrows; nav rail collapses to icons |
| < 1200 px | Detail becomes a **bottom drawer** sliding over the synoptic, dismissible with Esc |

The drawer fallback is not a degraded mode — it is the same ViewModel in a different
`ControlTemplate`, so behaviour is identical and only the presentation changes.

### What replaces the card board

The v.6 card board put every subsystem's full controls on screen simultaneously:
~12 group boxes of text fields competing for attention, with no indication of which
matter right now or how they relate physically. The hybrid separates the two jobs
that board was doing badly at once:

| Job | Where it goes |
|---|---|
| "What is the equipment doing?" | KPI strip (glance) + synoptic (context) |
| "Change this one parameter" | Detail pane (focus) |

---

## 3. Connection UX

**Auto-connect, with a status chip.** Connecting is the only reason most users
opened the v.6 Configurations window, so it stops being a destination.

On launch: the app restores the last-used medium and port/IP and connects **in the
background, after the shell is already on screen**. The user sees the window
immediately and the chip resolve a moment later.

```text
title bar:   ● Conectado · USB COM7  ▾
                └ click ────────────────┐
                ┌───────────────────────┴───┐
                │ ◉ USB    COM7 (ESP32) ▾   │
                │ ○ Wi-Fi  192.168.4.1      │
                │                           │
                │ Latência   24 ms          │
                │ Pacotes    rx 1832 tx 91  │
                │ Último erro  —            │
                │                           │
                │ [ Reconectar ]  [ Parar ] │
                │ ⚙ Configurações avançadas │
                └───────────────────────────┘
```

Chip states: `Conectado` (ok) · `Conectando…` (actuating) · `Reconectando…`
(warning) · `Desconectado` / `Erro` (alarm).

The diagnostics in the popover are the fields v.6 already parses but never showed —
`FlowCommandDeliveries`, `FlowCommandAgeMs`, ack correlation
([MIGRATION.md](MIGRATION.md#3-known-defects-carried-in-from-v6) item 8).

**Advanced Settings** remains a real window, reachable from the popover and from the
nav rail, holding everything that is genuinely configuration: calibration coefficients,
spike-filter tuning, control intervals, serial frame parameters, log paths, gassing-out
constants. Nothing is removed — it is just no longer in the way.

---

## 4. Input validation

A direct consequence of [MIGRATION.md](MIGRATION.md#3-known-defects-carried-in-from-v6)
item 11, where a malformed pH entry silently became setpoint 7.

- Out-of-range or unparseable input shows an inline error and **the command is not sent**.
- Ranges come from one place per subsystem, shared between validation and the wire builder.
- Accepting a decimal comma **and** a decimal point in the UI is required (pt-BR
  keyboards); the wire always gets `InvariantCulture`
  ([PROTOCOL.md](PROTOCOL.md#22-the-ph-echo-back)).
- A setpoint that has been typed but not yet applied is visually distinct from one
  the device has acknowledged. v.6 gave no such feedback.

---

## 5. Localisation

UI is **pt-BR**; code, comments and logs are **English**
([CONVENTIONS.md](CONVENTIONS.md)). All user-facing strings live in `.resx` from day
one — not because an English UI is planned, but because inline strings make the pt-BR
wording impossible to review in one place.
