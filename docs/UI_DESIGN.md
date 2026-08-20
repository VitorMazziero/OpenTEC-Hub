# UI Design Specification

> Visual language, shell anatomy and **every main window** of TECNAL-Hub.
> Revised 2026-08-20 against the A/B concept mockups and the implemented WP3 calibration workspace.
>
> **Docs:** [README](README.md) · [Roadmap](ROADMAP.md) · [Architecture](ARCHITECTURE.md) ·
> [Protocol](PROTOCOL.md) · [Calibration](CALIBRATION.md) · [Migration](MIGRATION.md) · [Decisions](DECISIONS.md) ·
> [Conventions](CONVENTIONS.md)

---

## How to read this document

This is the **target** design, written before the UI grows large enough that changing it
becomes expensive. Sections 1-4 are binding today. Section 5 specifies eight main
windows; each one names the phase that builds or completes it.

Three words are used precisely throughout:

| Term | Meaning |
|---|---|
| **Main window** | A destination in the navigation rail. Implemented as a **page inside the single shell window**, never as a separate `Window`. See [D-006](DECISIONS.md). |
| **Pane** | A persistent region of a page — the detail pane, the variable rail. |
| **Dialog** | A genuine modal `Window`. Four modal families are listed in section 7; calibration is intentionally a page. |

> **Nothing here may contradict [PROTOCOL.md](PROTOCOL.md).** The firmware is frozen
> ([D-002](DECISIONS.md)). Where the concept mockups show a value the wire cannot
> produce, section 2 records what the UI shows instead. That table is the most important
> part of this specification.

---

## 1. Design direction

### 1.1 The chosen style

**Option A — Light Professional / Process-First**, with Option B's variable rail as a
**collapsible pane** rather than a separate product.

```text
Rail collapsed (default)              Rail expanded (persisted preference)
Nav │       Synoptic      │ Detail    Nav │ Vars │ Synoptic │ Detail
```

Both states share one `ShellViewModel`, one selection, one detail pane. The rail is a
visibility toggle, not a second layout. The product identity is therefore:

> **Process-first by default, instrumentation-dense when needed.**

Of the three style candidates the app takes **Scientific Minimal** for the shell,
navigation and synoptic, and **Process-Centric HMI** density inside the detail pane and
the Controle page. The detail pane is allowed to be tighter than the rest of the app
because it is the engineering workspace; the synoptic is not, because it is the thing
that makes this a bioreactor application rather than a dashboard.

Rejected: *Instrument Console*. Its darker chrome fights the light-first decision below,
and a lab PC running eight-hour cultivations wants the brighter workspace.

### 1.2 The five rules

Everything else follows from these. When a new screen raises a question this document
does not answer, answer it with these.

1. **Neutral by default; colour appears when something is selected, active, changing, or
   requires attention.** A calm process looks grey.
2. **Colour carries one meaning per vocabulary, and the vocabularies never mix.** State
   colour says *how it is behaving*. Accent says *you can click this*. Path colour
   (synoptic only) says *what flows here*. Series colour (charts only) says *which
   variable*. Four vocabularies, strictly separated — see 3.3.
3. **Never show a number the equipment did not provide.** No zero standing in for "no
   data", no last-good value styled as live, no commanded figure styled as measured. An
   em dash is an honest answer.
4. **Measurements outrank labels.** The reading is the largest thing in its group; its
   name and unit are secondary.
5. **The synoptic keeps context, the detail pane keeps focus.** One subsystem's controls
   on screen at a time, never twelve.

### 1.3 Light first

Light is the **default and primary** theme. Dark is offered for low-light rooms and is a
re-mapped palette, not an inversion — see 3.6. The app follows the Windows theme by
default with a manual override in Configurações → Aparência.

> This reverses the current build, which ships dark-first with a Fluent `#0067C0` accent.
> The migration is a token-value change, not a restructure — see 3.10.

---

## 2. Reality check — the mockups against the hardware

The concept mockups (`docs/UI_design_guides/A.png`, `B.png`) are a visual target, not a
data contract. Several readings they display **do not exist on the wire**. Building them
as drawn would put invented numbers in front of an operator, which rule 3 forbids and
which is the exact failure class [MIGRATION.md](MIGRATION.md) catalogues in v.6.

| # | Mockup shows | What [PROTOCOL.md](PROTOCOL.md) actually provides | What the UI does instead |
|---|---|---|---|
| 1 | `RPM 790` with a green "normal" dot | **No RPM telemetry key exists.** `motorSetpoint` is write-only | Tile shows the commanded value with a `comandado` tag, state **Actuating** (never Ok), **no trend arrow**. Charts label the channel *Agitação (comandada)* |
| 2 | `Biomassa 25.000 cells/mL` | `BiomassAbs` (AU), `BiomassRaw` (counts), `BiomassIT` (ms), `BiomassPWM` (%) | Display **AU**. `cells/mL` needs a growth calibration curve that does not exist; out of scope until someone supplies it |
| 3 | `Nutriente 99 %` with a green dot | **No nutrient telemetry key exists.** Only commands: `nutriOperation`, `nutriMix`, `nutriOpCycle`, `nutriMixCycle`, `nutriIntensity` | Commanded-only tile showing **duty cycle %**, tagged `comandado`. A green "healthy" dot on an unmeasured pump is a lie |
| 4 | `Antiespumante 20 %` | `Antifoam` — a float with **no documented unit** | Show the raw figure **without a unit** until the meaning is confirmed on hardware. Command side is `antifoamOperation` / `antifoamMix` / `antifoamIntensity` |
| 5 | `Pressão 3.0 mmHg` | `Pressure` in **kPa**; `pressureReference` range 1-380 | **kPa**. An mmHg display option lives in Configurações → Unidades and converts on presentation only — never on the wire |
| 6 | `Volume 10.0 L` (Option B rail) | **No volume key.** `Distance` (mm) is the level/foam sensor, forced to `-1` when absent > 3 s | Show **Nível (mm)**. Volume appears only as a *derived* value, once vessel geometry is entered, tagged `calculado` |
| 7 | A `PID` tab on every device | **The firmware has no PID keys at all.** It accepts setpoints | The PID tab appears **only where an app-side controller actually runs** — the O₂ cascade, Phase 2. On temperature it would describe a controller the app cannot see |
| 8 | `Cascata (O₂ → Vazão de Ar)` | Phase 2's real cascade is **kLa gradient-path allocation over agitation × aeração** ([ROADMAP](ROADMAP.md) Phase 2) | Mode label: **`Cascata kLa (O₂ → agitação + aeração)`**. Single-actuator cascade is a special case of it, not the design |
| 9 | `Usuário: admin` | No authentication, no user model, no roles | Dropped. The slot holds the **command-ownership Modo** selector (4.2), which is load-bearing |
| 10 | `Próxima ação: Alimentação de Nutriente em 02:15:30` | Only the recipe engine can know this (Phase 3) | `—` until a recipe is running |
| 11 | `O₂ 40.2 %` | [PROTOCOL §2.1](PROTOCOL.md#21-client-side-signal-conditioning) names the calibration output `oxygen_mg_per_L`, while `oxygenMonitor` is a **% setpoint** | Display **% (saturação)**, matching the app today. **Flag:** the protocol doc is internally inconsistent here; confirm against live sensors before Phase 2 |
| 12 | `pH 7.02` with a control panel | `pHval` is parsed, calibrated and echoed back as `pHCal`; dosing is the separate five-field v.6 state | Phase 2 WP3 delivered the complete pH panel. Calibration remains app-side and starting its procedure interlocks dosing off |
| 13 | `Alarmes` with a red badge `2` | **No alarm engine exists** ([ROADMAP](ROADMAP.md) Phase 5) | Badge and page are real design, but both depend on the alarm engine specified in 5.4. Until it ships, the nav item carries no badge |
| 14 | — (absent from both mockups) | `agitatorAuto`, `agitatorPercent`, `agitatorDir`, `agitatorOn` | This is the **flask agitator, a separate bench device**, not the reactor impeller. It must not appear on the reactor synoptic. It gets its own card on Controle |
| 15 | Nine KPI tiles fitting comfortably | Phase 1 has five variables; Phases 2-3 bring roughly twelve | The strip **scrolls and is user-configurable** (4.4). A strip that silently drops tiles off the edge is worse than one that admits it cannot fit them |

Two further constraints the mockups cannot show:

- **Sentinels are gaps, not values.** `-1.0` is "never received". Charts draw a gap;
  readouts draw `—`. A line diving to −1 is a bug.
- **Agitation, nutrient and antifoam are commanded-only.** Every surface that displays
  them — tile, synoptic label, detail pane, chart legend, session log — carries the same
  `comandado` marking. That consistency is what stops an operator inferring a measurement
  that was never taken.

---

## 3. Design tokens

All tokens live in `src/TecnalHub/Themes/Tokens.{Light,Dark,Shared}.xaml`. Views bind to
`*Brush` keys via `DynamicResource` — **never** to a `*Color` key and **never** to a
literal, so a runtime theme swap repaints without rebuilding the visual tree.

### 3.1 Neutrals

The slight blue bias is deliberate: it reads as technical without making the interface
look blue.

| Role | Token | Light | Notes |
|---|---|---|---|
| App background | `SurfaceBaseColor` | `#F7F9FC` | Behind everything |
| Main surface | `SurfaceCardColor` | `#FFFFFF` | Synoptic, detail pane, tables |
| Secondary surface | `SurfaceSunkenColor` | `#F3F6F9` | Rails, headers, inset groups |
| Hover | `SurfaceHoverColor` | `#EDF3FA` | *new* |
| Selected | `SurfaceSelectedColor` | `#EAF3FF` | *new* |
| Elevated | `SurfaceElevatedColor` | `#FFFFFF` | Popovers, dialogs (+ 1 px stroke) |
| Primary border | `StrokeDefaultColor` | `#D9E0E8` | |
| Subtle divider | `StrokeSubtleColor` | `#E7EBF0` | *new* |
| Strong divider | `StrokeStrongColor` | `#C5CED8` | |
| Primary text | `TextPrimaryColor` | `#172033` | |
| Secondary text | `TextSecondaryColor` | `#526071` | |
| Muted text | `TextMutedColor` | `#7C8795` | *new* — units, metadata |
| Disabled text | `TextDisabledColor` | `#A5ADB8` | |

### 3.2 Accent

One interaction colour. **TECNAL Control Blue `#2563D9`.**

| Token | Light | Used for |
|---|---|---|
| `AccentPressedColor` | `#1749A6` | Pressed |
| `AccentColor` | `#2563D9` | Primary buttons, focus ring, selected nav, links |
| `AccentHoverColor` | `#3182F6` | Hover, active synoptic path |
| `AccentBorderColor` | `#B9D5FF` | *new* — selected tile outline |
| `AccentSelectionColor` | `#EAF3FF` | *new* — selected row background |
| `AccentSubtleColor` | `#F4F8FF` | *new* — zebra striping, gentle emphasis |

Every blue shade means **selection, control, connectivity or interaction**. None of them
ever encodes process data.

### 3.3 Process state — the colour discipline

> **Green does not mean "enabled". Green means healthy or operating normally.**

This is the rule that keeps an instrument UI readable. `Controle habilitado` is a **blue**
toggle; `Agitador operando normalmente` is a **green** status. Conflating them is how
state colour loses its authority.

| State | Meaning | Fill (dots, chips, strokes) | Text variant |
|---|---|---|---|
| **Ok** | At setpoint, within limits, healthy | `#16A34A` | `#0E7A3C` |
| **Actuating** | A controller is actively moving this actuator | `#2563D9` | `#1749A6` |
| **Warning** | Approaching or outside the acceptable band | `#C67F00` | `#8A5A00` |
| **Alarm** | Alarm condition, or link lost | `#DC3545` | `#B42318` |
| **Idle** | Disabled, disconnected, or no data yet | `#7C8795` | `#526071` |
| **Disabled** | Intentionally inactive | `#B6BEC8` | `#A5ADB8` |

**Why two variants per state.** `#16A34A` on white is 3.48:1 — fine for an 8 px dot,
failing for text. Any state colour used as *type* uses the text variant. Tokens:
`StateOkBrush` / `StateOkTextBrush`, and so on for each state.

**Two thresholds, both enforced.** Fills are marks, not type, so they need WCAG's
**3:1** for non-text components; text variants need **4.5:1**. The amber originally
proposed here was `#D99000`, which measures **2.66:1 on white** and fails even the
lower bar — an amber dot nobody can see is worse than no dot. It was darkened to
`#C67F00` (3.24:1) when `TokenParityTests` caught it. That test computes every ratio
from the token files on each run, so this class of error cannot return quietly.

These six are the **only** colours permitted to say anything about the equipment.

### 3.4 Synoptic path colours

A fifth vocabulary, confined to the synoptic drawing. It never leaks into the rest of the
UI: purple identifies the antifoam line on the diagram, but the antifoam **settings** use
the ordinary accent blue like every other panel.

| Element | Token | Colour |
|---|---|---|
| General piping | `PathPipingColor` | `#596575` |
| Selected / control path | `PathSelectedColor` | `#2563D9` |
| Gas / airflow | `PathGasColor` | `#2F80ED` |
| Heating | `PathHeatingColor` | `#D94A45` |
| Cooling | `PathCoolingColor` | `#3B82D0` |
| Nutrient feed | `PathFeedColor` | `#37975A` |
| Antifoam | `PathAntifoamColor` | `#8754C9` |
| Sensors / instrumentation | `PathInstrumentColor` | `#455568` |
| Vessel metal | neutral greys | from 3.1 |

Path colour is **subordinate to state colour**. A line in alarm draws in alarm red
regardless of what it carries.

### 3.5 Chart colours

Charts need two palettes, and confusing them is a common failure.

**Role palette** — a single loop, where every series describes the *same* variable. Used
by the detail-pane trend, the cascade tuning plot, and any single-channel view.

| Series | Colour | Stroke |
|---|---|---|
| PV (measured) | `#2563D9` | solid, 2 px |
| Setpoint | `#22A447` | **dashed** |
| Controller output | `#E67E22` | **dotted** |
| Alarm limits (HH/H/L/LL) | `#D95C5C` | thin, 1 px |
| Non-selected / historical | `#8A96A5` | solid, 1 px |

**Identity palette** — Históricos, where each series is a *different* variable.
Okabe-Ito, colour-blind safe, unchanged from the current build: `Series1..6` =
`#0072B2 #E69F00 #009E73 #CC79A7 #56B4E9 #D55E00`.

Three rules keep them apart:

- Never draw both palettes on one axis.
- Setpoint green is **always dashed**, so it cannot be mistaken for the state green of a
  status dot.
- When many channels are plotted, the selected one stays saturated and the rest drop to
  `#8A96A5`. Eight bright series at once is not a chart, it is a plaid.

### 3.6 Dark theme

A re-mapped palette, not an inversion. State hues hold; they brighten for contrast.

| Role | Dark |
|---|---|
| App background | `#11161D` |
| Main surface | `#181E26` |
| Elevated surface | `#202731` |
| Hover / selected | `#232C38` / `#1B2B45` |
| Border / subtle / strong | `#303946` / `#262E39` / `#3D4756` |
| Text primary / secondary / muted / disabled | `#E7ECF2` / `#A8B2BF` / `#7E8A99` / `#5F6A78` |
| Accent pressed / base / hover | `#3B7FE0` / `#5B9CFF` / `#7CB1FF` |
| Ok · Actuating · Warning · Alarm · Idle | `#3DD07A` · `#5B9CFF` · `#F0B429` · `#F1707B` · `#8A94A3` |

In dark, fill and text variants converge — the fill colours already clear 4.5:1 on
`#181E26`, so `StateOkTextBrush` maps to the same value as `StateOkBrush`.

### 3.7 Typography

**Segoe UI**, with `Segoe UI Variable` deliberately *not* named first.

> **Do not reinstate `Segoe UI Variable` as the primary family.** It ships with Windows 11
> only; on Windows 10 WPF's fallback resolution recursed until the stack overflowed inside
> `TextBlock.MeasureOverride`, before the first frame ever rendered. The target machines
> include Windows 10. The Variable faces may return only behind a runtime OS check.

| Element | Size | Weight |
|---|---|---|
| Page / window title | 20 | Semibold |
| Detail pane title | 18 | Semibold |
| Main PV readout | 36-40 | Semibold |
| KPI value | 24-26 | Semibold |
| Section heading | 13 | Semibold |
| Body / parameter label | 13 | Regular |
| Variable-rail value | 16 | Medium |
| Secondary numeric | 14 | Medium |
| Metadata, units, status | 11 | Regular |

**Numeric rendering.** Process values use **tabular figures** so `9.9 → 10.0` does not
shift width. In WPF that is `Typography.NumeralAlignment="Tabular"` on the run — **not** a
monospace family.

> **Change from the current build.** `FontNumeric` is `Consolas` today, which makes every
> readout look like a terminal rather than an instrument. Replace it: keep Segoe UI and
> switch on tabular figures. `ReadoutTextStyle` is the only style that needs editing.

### 3.8 Geometry and spacing

| | |
|---|---|
| Radius | **4 px** inputs, buttons, chips · **6 px** cards and panels · nothing larger |
| Spacing | 4 / 8 / 12 / 16 / 24. Anything off this scale is a bug in the View |
| Input height | 30 px (28 px in dense contexts: detail pane, tables) |
| Border | 1 px `StrokeDefault`. Focus: 2 px `Accent`, inset |
| Elevation | Dialogs and popovers only. Everything else uses a border |

No gradients, no glossy fills, no large shadows, no floating-card aesthetic. Regions are
defined by **alignment and thin dividers**.

### 3.9 Iconography

Fluent-style outline: 1.5-1.75 px stroke, rounded joins, geometric, minimal internal
detail, on a 16 / 20 / 24 px grid.

**Source: vector `Path` geometries in `src/TecnalHub/Resources/Icons/`**, merged as a
`ResourceDictionary` — **not** an icon font. `Segoe Fluent Icons` is Windows 11 only, and
this project has already been bitten once by a Win11-only font (3.7). Geometries also take
the theme stroke brush directly.

| Purpose | Glyph |
|---|---|
| Visão Geral | vessel outline |
| Controle | sliders |
| Receitas | node graph / branching flow |
| Alarmes | bell |
| Históricos | line chart |
| Eventos | list with ticks |
| Calibrações | crosshair / target |
| Configurações | gear |
| Temperature · pH · O₂ · Agitation | thermometer · probe marked `pH` · probe marked `O₂` · rotor |
| Airflow · Pressure · Level | wind · gauge · vertical scale |
| Antifoam · Nutrient · Pump · Biomass | droplet · flask · pump head · cells |
| Cascade · Trend · Export · Fullscreen | branching loop · line chart · down-tray · corners |

**Icon colour.** Navigation and toolbar icons are monochrome slate (`TextSecondary`),
turning `Accent` when selected. Colour appears only where the icon *is* the state: green
check, amber triangle, red bell. If every icon has its own colour, alarm colours stop
meaning anything.

### 3.10 Migration from the current build

The current shell is structurally correct and mis-tinted. This is a token edit, not a
rewrite — which is precisely why it should happen now.

| Change | Files | Cost |
|---|---|---|
| Re-tint light tokens to 3.1-3.4; make Light the default | `Tokens.Light.xaml`, `ThemeService` | Low |
| Re-tint dark tokens to 3.6 | `Tokens.Dark.xaml` | Low |
| Add tokens: hover, selected, subtle stroke, muted text, accent shades, `*TextBrush` state variants, path colours, role-chart colours | `Tokens.{Light,Dark,Shared}.xaml` | Low |
| `FontNumeric` Consolas → Segoe UI + tabular figures | `Tokens.Shared.xaml` | Low |
| Radius 8 → 6 for cards, 4 for inputs | `Tokens.Shared.xaml` | Low |
| Nav rail 132 px → 184 px, add icons and grouping | `MainWindow.xaml` | Medium |
| KPI strip → scrollable and configurable (4.4) | `MainWindow.xaml`, `ShellViewModel` | Medium |
| Nav destinations 4 → 8 | `ShellViewModel`, new Views | Per phase |

**Do this before Phases 2-3 add screens.** Every page built against the old tokens is a
page that has to be re-checked afterwards.

---

## 4. Shell anatomy

One `Window`. Six persistent regions; the workspace is the only one that changes with
navigation.

```text
┌───────────────────────────────────────────────────────────────────────────────┐
│ ⬡ TECNAL-Hub    Executando: Fed-Batch-01  01:32:46   ● Conectado   🔔² ? ─ □ ✕ │  4.1
├──────────┬────────────────────────────────────────────────────────────────────┤
│          │  Temp   pH    O₂    Agit   Vazão  Press  Nível  …          ⚙  ‹ ›  │  4.4
│  Nav     ├───────────────┬────────────────────────────┬───────────────────────┤
│  rail    │  Variable     │                            │                       │
│          │  rail         │        WORKSPACE           │   Detail pane         │  4.5
│  184px   │  (optional)   │                            │   380-460px           │
│          │  220-250px    │                            │                       │
│          │               │                            │                       │
│  ─────   │               │                            │                       │
│  Modo    │               │                            │                       │
├──────────┴───────────────┴────────────────────────────┴───────────────────────┤
│ Receita: —   Fase: —   Decorrido: 08:32:14   Próxima ação: —    ● Online  ⟳   │  4.6
└───────────────────────────────────────────────────────────────────────────────┘
```

### 4.1 Title bar — 48 px

Custom chrome, three zones.

| Zone | Contents |
|---|---|
| **Left** | App icon (20 px) · `TECNAL-Hub` (15 px Semibold) · version, muted |
| **Centre** | Run context: `Executando: <nome>` · elapsed `hh:mm:ss` (tabular) · `▶/⏸` when a recipe is running. All `—` when idle |
| **Right** | Connection chip · alarm bell with badge · help · minimise / maximise / close |

**Connection chip** — the only entry point to connection state (8).

| Control | Type | Behaviour |
|---|---|---|
| Chip | `ToggleButton` with state dot + text | `Conectado · USB COM7 ▾` — opens the popover |
| Popover | Flyout, 320 px | Contents in section 8 |

**Alarm bell** — count badge in `StateAlarm` (or `StateWarning` when only warnings are
active). No badge at zero. Clicking navigates to Alarmes. Right-click → `Silenciar áudio
por 10 min`.

**Sensor-module indicator** — a separate small chip, shown **only** when
`SensorCommOK` is false: `⚠ Módulo sem resposta`. The PC link and the module's internal
UART are independent; a healthy USB connection can sit in front of a dead module, and
conflating the two tells the operator the wrong thing.

### 4.2 Navigation rail — 184 px expanded, 52 px collapsed

`ListBox` — selection is exactly what it models. Rows are 40 px: 20 px icon, 12 px gap,
13 px label. Selected row gets `SurfaceSelected` fill, a 3 px `Accent` left bar, and an
`Accent` icon.

Three groups, separated by a 1 px `StrokeSubtle` divider with no group heading:

| Group | Items |
|---|---|
| **Operação** | Visão Geral · Controle · Receitas · Alarmes |
| **Registro** | Históricos · Eventos |
| **Sistema** | Calibrações · Configurações |

Only **Alarmes** carries a badge.

**Rail footer** — pinned to the bottom, above a divider:

| Control | Type | Options | Notes |
|---|---|---|---|
| `Modo` | `ComboBox` | `Manual` · `Automático` · `Receita` | **Command ownership.** See below |
| Mode detail | caption | e.g. `Cascata kLa ativa` | Muted; `—` in Manual |
| Rail toggle | icon button | — | Collapse to 52 px |

> **`Modo` is the command-ownership selector and is load-bearing.** [D-009](DECISIONS.md)
> requires one command queue with one owner so that a running recipe and an operator
> cannot fight over the link. `Manual` = operator writes setpoints. `Automático` = the
> cascade owns its actuators. `Receita` = the recipe engine owns everything it declares.
> Controls owned by something else are **disabled with a reason tooltip**, never silently
> ignored. Changing away from `Receita` while a recipe runs requires confirmation.
>
> This replaces the mockups' `Usuário: admin`, which has nothing behind it (2, item 9).

### 4.3 Variable rail

Option B's contribution, and since [D-011](DECISIONS.md) **the variable display itself**
rather than an optional extra: with the KPI strip confined to Receitas, this is where live
values are read on every other page. Always present, not collapsible.

A live instrument index, beside the drawing of the equipment that produced the numbers.

Each row, 52 px:

```text
[icon]  Temperatura                    ●
        30.0 °C
```

| Element | Style |
|---|---|
| Icon | 20 px outline, `TextSecondary`; `Accent` when selected |
| Name | 13 px Medium, `TextSecondary` |
| Value + unit | 16 px Medium tabular, `TextPrimary`; unit 11 px `TextMuted` |
| State dot | 8 px, state fill. **The only colour in a normal row** |
| `comandado` tag | 10 px `TextMuted` pill, where applicable |
| Selected row | `SurfaceSelected` fill, `Accent` left bar, `Accent` name |

Header: `Variáveis` + a filter icon → `Todas` · `Somente controladas` · `Somente em
alarme` · `Editar variáveis…`. Footer: derived readouts that are not process variables
(`Nível 120 mm`, `Volume 10.0 L calculado`) below a divider, plus `Editar variáveis…`.

**Selection is bidirectional and total.** Clicking a rail row, a synoptic element, or a
KPI tile produces **identical state**: rail row highlighted, synoptic element and its
control path highlighted, detail pane switched, trend loaded. There is no difference
between the three paths. One `SelectedVariable` on `ShellViewModel`; everything else
observes it.

### 4.4 KPI strip — 84 px, Receitas only

> **Changed by [D-011](DECISIONS.md).** This originally read "visible on every page".
> Building it that way put **two viewers of the same six variables** on Painél - this
> strip on top and the variable rail on the left, showing identical values a few hundred
> pixels apart. The strip now appears **only on Receitas**; everywhere else the variable
> rail ([4.3](#43-variable-rail)) is the variable display.
>
> The strip earns its place on Receitas for the opposite reason it failed on Painél:
> there the canvas describes the *intended* process and the strip is the only thing on
> screen showing the *actual* one. See [5.3.15](#5315-process-versus-recipe).
>
> It is built and kept as `Views/KpiStripView.xaml`, unused until Phase 3 places it.

Tile, 132-160 px wide:

```text
O₂                        ●
40.2 %                    ↑
SP 40.0
```

| Element | Style |
|---|---|
| Name | 11 px `TextSecondary` |
| Value | 24-26 px Semibold tabular; `—` when absent |
| Unit | 11 px `TextMuted`, baseline-aligned to the value |
| Setpoint | 11 px `TextMuted`, `SP 40.0`; omitted where there is none |
| State dot | 8 px, state fill |
| Trend arrow | `↑ ↓ —`, `TextMuted`. **Suppressed for commanded-only variables** |
| `comandado` tag | 10 px pill under the value |
| Selected | 1 px `AccentBorder` outline + `AccentSubtle` fill + a small caret pointing at the workspace |

**Overflow — the Phase 5 problem, solved here.** Five tiles today, ~12 after Phase 3.

- The strip is a single non-wrapping row in a horizontal `ScrollViewer`.
- `‹ ›` chevrons appear at the edges when content overflows; mouse wheel scrolls.
- A gear button opens `Configurar indicadores`: a checklist of every available variable
  in strip order, drag to reorder, plus `Mostrar barra de variáveis` and `Restaurar
  padrão`. Persisted in `AppSettings`.
- A pinned tile whose channel has no data shows `—` in Idle. **It is never removed**;
  disappearing tiles are how an operator stops trusting the strip.

Default pinned set: Phase 1 — Temperatura, pH, O₂, Agitação, Vazão, Pressão.
Phase 2 adds Nível and Antiespumante; Phase 3 adds Biomassa.

Clicking a tile selects that variable everywhere (4.3) **and**, if the current page is not
Visão Geral, navigates there. Double-click opens it in Históricos.

### 4.5 Workspace

The page area. Every page owns the full width between the rails and gets a 16 px margin.
Pages that need a right-hand detail pane use the same `DetailPaneView` (6.1); pages that
do not, use the full width.

### 4.6 Status bar — 32 px

Run and system context that is true regardless of page. 11-12 px, `TextSecondary`,
separated by 1 px `StrokeSubtle` dividers.

| Field | Source | Idle |
|---|---|---|
| `Receita:` | Recipe engine (Phase 3) | `—` |
| `Fase:` | Current recipe phase | `—` |
| `Decorrido:` | `Time` telemetry minus the user-zeroed offset | `—` |
| `Próxima ação:` | Recipe engine only | `—` |
| `Registro:` | Session logger — `gravando · 1832 linhas` or `parado` | `parado` |
| `● Sistema Online` | Composite: link + module + no active alarms | state dot |
| `Última atualização hh:mm:ss` | Last accepted telemetry frame | `—` |
| `⟳` | Manual poll / reconnect | — |

> **`Última atualização` is a safety feature, not decoration.** A frozen link that keeps
> showing the last good frame is the failure mode this app exists to prevent. When the
> gap exceeds three telemetry periods the field turns `StateWarning`; readouts across the
> app go Idle and the values are replaced with `—`.

### 4.7 Responsive behaviour

| Width | Layout |
|---|---|
| **≥ 1600 px** | Nav 184 · variable rail (if enabled) · workspace · detail 460 |
| **1400-1600 px** | Same, detail 380 |
| **1200-1400 px** | Nav collapses to 52 px icons; variable rail auto-hides with a restore chip |
| **< 1200 px** | Detail becomes a **bottom drawer** over the workspace, dismissible with `Esc`; KPI strip scrolls |

The drawer is not a degraded mode — same ViewModel, different `ControlTemplate`, so
behaviour is identical and only presentation changes.

Minimum window 960 × 640. Window size, position, last page, rail state and KPI
configuration all persist.

---

## 5. The main windows

Eight destinations. **kLa gassing-out is deliberately absent** — it leaves the controller
app entirely ([D-010](DECISIONS.md)) because it depends on torch and is the single largest
contributor to v.6's cold start. **Receitas is a main window**, not a dialog.

| # | Window | Purpose | Phase |
|---|---|---|---|
| 5.1 | **Visão Geral** | Process picture; select and adjust one subsystem | 1 ✅ |
| 5.2 | **Controle** | Every setpoint at once; controller tuning | 1 partial → 2 |
| 5.3 | **Receitas** | Author, validate and run automated sequences | 3 |
| 5.4 | **Alarmes** | Active alarms, acknowledgement, limits | 5 |
| 5.5 | **Históricos** | Trends, session files, export | 1 ✅ → 5 |
| 5.6 | **Eventos** | Unified audit trail | 1 partial → 5 |
| 5.7 | **Calibrações** | Calibration procedures with live feedback | 2 WP3 partial → 3 |
| 5.8 | **Configurações** | Everything genuinely configuration | 1 ✅ |

### The device inventory

Every window below draws from one inventory. This is the complete set of selectable
entities, derived from [PROTOCOL.md](PROTOCOL.md) — not from the mockups.

| Id | Label (pt-BR) | Measured | Telemetry keys | Command keys | Phase |
|---|---|---|---|---|---|
| `temperature` | Temperatura | ✅ | `Tempval` | `tempSetpoint` | 1 |
| `ph` | pH | ✅ | `pHval` → calibrated | `pHCal` echo; `pHSetpoint`, `pHError`, `pHOperation`, `pHMix`, `pHIntensity` | 1 read · 2 WP3 control/calibration |
| `oxygen` | Oxigênio dissolvido | ✅ | `Oxyval` → calibrated | `oxygenMonitor` | 1 |
| `motor` | Agitação | ❌ **commanded** | *none* | `motorSetpoint` | 1 |
| `flow` | Vazão de ar | ✅ | `FlowRate`, `FlowSetpoint`, `FlowVoltage`, `FlowmeterOnline` | `flowmeterComm`, `flowSetpoint`, `maxFlow`, `v_Flow`; `k1..c2` calibration | 1 control · 2 WP3 calibration |
| `valves` | Válvulas | ✅ state | `Valve1`, `Valve2`, `ValveFlow` | `valve_1`, `valve_2` | 1 |
| `pressure` | Pressão | ✅ | `Pressure` | `pressureReference` | 1 |
| `level` | Nível / espuma | ✅ | `Distance` | `distanceSensorComm`, `distanceSensorReference`, `foamStartDelay_s`, `foamPulse_s`, `foamInterval_s` | 2 |
| `antifoam` | Antiespumante | ⚠ unitless | `Antifoam` | `antifoamOperation`, `antifoamMix`, `antifoamIntensity` | 2 |
| `nutrient` | Nutriente | ❌ **commanded** | *none* | `nutriOperation`, `nutriMix`, `nutriOpCycle`, `nutriMixCycle`, `nutriIntensity` | 2 |
| `agitator` | Agitador de frasco | ❌ **commanded** | *none* | `agitatorAuto`, `agitatorReEnablePot`, `agitatorPercent`, `agitatorDir`, `agitatorOn` | 2 |
| `biomass` | Biomassa | ✅ | `BiomassAbs`, `BiomassRaw`, `BiomassIT`, `BiomassPWM` | `biomassComm`, `blank`, `low`, `high`, `opt` | 3 |
| `pump` | Bomba externa | ✅ | `PumpFlow`, `PumpVol` | `pumpComm`, `mode`, profile parameters | 3 |
| `our` | OUR | 🔬 derived | soft sensor | — | 2 |
| `kla` | kLa / cascata | 🔬 derived | app-side | drives `flow` + `oxygen` + `motor` | 2 |

> **`agitator` is the flask agitator — a separate bench device, not the reactor
> impeller.** It has no position on the reactor synoptic and appears only as a card on
> Controle. Confusing it with `motor` would put a second, wrong agitation control on the
> vessel.

---

### 5.1 Visão Geral

**Purpose:** understand the process by looking at it; select a component; adjust it
without losing sight of the reactor. **Phase 1 software surface — complete; Phases 2-3
add new process elements without changing this visual contract.**

```text
┌─────────────────────────────────────────────┬──────────────────────────┐
│ SINÓPTICO                    ⤓  ⛶  ⋯        │ O₂ (Oxigênio)   ● Normal │
│                                             ├──────────────────────────┤
│         [ reactor drawing, live values ]    │ PV 40.2 %  SP 40.0  Δ+0.2│
│                                             │ [    trend chart       ] │
│                                             │ Controle                 │
│ ── Temperatura ── Ar/Gás ── Controle ──     │ [SP][Cascata][PID][Saída]│
│    Alimentação ── Medição                   │ Calibração  Saúde  Avanç.│
└─────────────────────────────────────────────┴──────────────────────────┘
```

#### 5.1.1 The reactor image

> **Decision [D-012](DECISIONS.md).** The flat vector reactor is replaced by a
> photorealistic render with callout cards anchored to the physical ports. Reference:
> `docs/UI_design_guides/Bioreactor Panel.png`. **Implemented 2026-08-20** with the
> transparent cross-theme master `Resources/Images/reactor-neutral.png`; generation
> provenance and the accepted prompt are in [ASSET_PROVENANCE.md](ASSET_PROVENANCE.md).

**The cards are controls, not labels.** This is the substantive change. Clicking or
keyboard-activating a card selects that variable into the detail pane, so the physical
position of a probe or port is the fast path to its operational context. The selected
card shares the same state as the variable rail and detail pane; no parallel selection
or value copy exists.

##### Producing the asset

Four options, in the order they were considered:

| Approach | Verdict |
|---|---|
| Keep hand-authored vector XAML | **Rejected.** It is what exists, and it reads as a diagram of a tank. Photorealism in vectors means hundreds of gradient stops, which is neither maintainable nor fast to render |
| Photograph the real reactor | **Rejected as the primary source.** Lighting, background and lens distortion fight the interface, the ports would sit wherever the photo put them, and a second machine means a second photo shoot. Useful as *reference* for the modeller |
| **AI-generated PBR product render, validated as a 2D asset** | **Chosen for Phase 1.** Generate one orthographic, state-free equipment master; reject mechanically wrong geometry and false transparency; pin the accepted PNG and its normalized anchors in tests. An editable 3D source remains the better route if future hardware variants require repeated camera-identical renders |
| Real-time 3D in the app | **Rejected.** A 3D viewport in a control application costs GPU, startup time and a dependency, to gain a rotation nobody needs. It also fights the "cold start under 2 s" target |

##### Why one neutral transparent master

True alpha lets WPF own the equipment bay, selection and theme surfaces. Neutral lighting
was reviewed on both palettes and remained legible, so one asset is safer than two files
that could drift in geometry. If a future renderer cannot produce usable alpha, generate
camera-identical light and dark images against the exact `SurfaceCard` colours; do not
remove a background heuristically inside the application.

##### Asset requirements

| Requirement | Value | Why |
|---|---|---|
| Format | PNG, transparent background | The app background is a theme token and must show through |
| Projection | **Orthographic**, dead-on front | A perspective render makes anchor points drift as the image scales, and the vessel look like it is falling over |
| Resolution | **1024 × 1536 px** accepted master | More than 3x the current ~450 px display height and sufficient for 200 % scaling |
| Variants | **One neutral RGBA master**, validated on both themes | Genuine transparency succeeded. Camera-identical solid light/dark variants are the fallback only when alpha cannot be produced |
| Liquid | **Rendered empty** | Broth level is live data from `Distance` and must be a WPF overlay. Baking a level in would show a fill nobody measured |
| Neutral state | No status colours, no glow, no labels | Every one of those is state, and state belongs to the overlay |
| Structure | Double-wall jacket, top motor/entries/probes, two Rushton levels, independent ring sparger | The shaft terminates below the lower turbine with visible clearance; it never continues to the sparger |

##### What stays vector, and why

**Nothing that changes may be baked into the image.** The render is scenery; everything
live is drawn over it:

- Live values, units and setpoints
- State colour — the ring on a card, the dot, an alarm border
- Leader lines and their anchor dots, coloured by the path palette ([3.4](#34-synoptic-path-colours))
- Selection highlighting, including the control-path emphasis in [5.1](#51-visão-geral)
- Broth level, from `Distance`, with the honest fallback when that key is absent
- Valve open/closed indicators
- Hit targets

##### Anchoring

The overlay needs port positions in **image-relative coordinates** (0-1 in both axes), not
pixels, so the layout survives any scale. Ship them beside the asset:

```json
{ "asset": "reactor-neutral.png",
  "anchors": {
    "ph": { "x": 0.43, "y": 0.61 },
    "oxygen": { "x": 0.62, "y": 0.54 },
    "flow": { "x": 0.50, "y": 0.80 }
  } }
```

A card is then positioned by its anchor and a side, and the leader line is generated —
an orthogonal path from port to card, in the path colour, with a dot at the port end.
That keeps the card layout data rather than hand-placed XAML, which is what makes a
re-render cheap instead of a re-layout.

##### Fallback

If the asset is missing or fails to decode, `OnReactorRenderFailed` exposes the bundled
vector schematic rather than showing an empty panel. The live cards and leader lines are
identical either way, because they never depended on state baked into the image.

#### Synoptic header

| Control | Type | Behaviour |
|---|---|---|
| `SINÓPTICO` | label, 13 px Semibold, letterspaced | — |
| `⤓` | icon button | `Exportar imagem…` → PNG / SVG |
| `⛶` | icon button | Fullscreen; `Esc` restores |
| `⋯` | overflow menu | See below |

Overflow menu items:

| Item | Type | Default |
|---|---|---|
| `Mostrar valores no diagrama` | checkable | on |
| `Mostrar setpoints` | checkable | off |
| `Realçar caminho de controle` | checkable | on |
| `Mostrar legenda de linhas` | checkable | on |
| `Mostrar estado das válvulas` | checkable | on |
| `Animar fluxo` | checkable | **off** — motion in peripheral vision competes with alarms |
| `Copiar imagem` | action | — |
| `Restaurar zoom` | action | — |

#### Synoptic elements

Transparent PBR equipment render plus native WPF overlays. The token-driven equipment
bay, cards, lines, state, focus and fallback all theme and scale independently of the
PNG. The reactor sits centrally with generous negative space. The visual hierarchy is:
**reactor → physical connections → instrument symbols → live values → control
relationships.**

| Element | Selects | Live label | Path colour | Phase |
|---|---|---|---|---|
| Vessel body, headplate, jacket wall | — | — | vessel greys | 1 |
| Broth level fill | `level` | — | translucent `PathGas` tint | 2 |
| Jacket circuit | `temperature` | `30.0 °C` | Heating / Cooling by sign of error | 1 |
| Motor drive + shaft + impellers | `motor` | `790 rpm` `comandado` | `PathInstrument` | 1 |
| pH probe | `ph` | `7.02 pH` | `PathInstrument` | 1 |
| O₂ probe | `oxygen` | `40.2 %` | `PathInstrument` | 1 |
| Air inlet → flowmeter → control valve | `flow` | `0.50 L/min` | `PathGas` | 1 |
| `valve_1` aux · `valve_2` N₂ | `valves` | open/closed glyph | `PathGas` | 1 |
| Sparger | `flow` | — | `PathGas` | 1 |
| Vent / exhaust + pressure gauge | `pressure` | `3.0 kPa` | `PathPiping` | 1 |
| Level / foam sensor on headplate | `level` | `120 mm` | `PathInstrument` | 2 |
| Antifoam bottle + pump → port | `antifoam` | `20` | `PathAntifoam` | 2 |
| Nutrient bottle + pump → port | `nutrient` | `99 %` `comandado` | `PathFeed` | 2 |
| Acid / base bottles + pumps → port | `ph` | duty % `comandado` | `PathFeed` | 2 |
| Biomass optical sensor on vessel wall | `biomass` | `0.42 AU` | `PathInstrument` | 3 |
| External pump + feed line | `pump` | `1.2 mL/min` | `PathFeed` | 3 |

**Broth level is honest.** Phase 1 has no level channel in the active inventory, so the
asset stays visibly empty and the footer says `nível não monitorado`. Phase 2 may add a
WPF fill only when `Distance` is live; when the key is absent the correct state remains
empty/unknown, never a nominal animated fill driven by nothing.

**Selection highlight** is what gives the synoptic its purpose. Selecting `oxygen`
highlights the **whole control relationship**, not just one label:

```text
O₂ probe ──▶ controller ──▶ airflow ──▶ control valve ──▶ sparger
```

Selected elements draw in `PathSelected` at 2 px; everything else drops to 45 % opacity
but stays visible. That teaches the process rather than merely marking a label.

**Line legend** below the drawing, toggleable: `Temperatura` · `Ar / Gás` · `Controle` ·
`Alimentação` · `Medição`, each a 16 px colour rule plus an 11 px label.

#### Detail pane

Specified once in 6.1 and reused unchanged by every window that has one.

---

### 5.2 Controle

**Purpose:** the two jobs the detail pane deliberately cannot do — *verify every setpoint
before starting a run*, and *tune a controller*. **Phase 1** ships the setpoint table;
**Phase 2** adds cascade/tuning and the complete pH dosing card.

> [ROADMAP](ROADMAP.md) Phase 5 records the gap directly: *"No 'all setpoints at a glance'
> view. The detail pane deliberately shows one subsystem at a time, which is right for
> editing and wrong for verifying a run's configuration before starting it."* Phase 2 also
> lists *"Controller tuning UI with live term display"* with nowhere to live. This page is
> both answers.

Full width, no detail pane and no variable rail: the table already carries every live PV,
so a second variable display would be redundant and would hide the action columns at the
1280 px acceptance width. Two tabs.

#### Tab 1 — `Parâmetros`

A dense table, one row per controllable subsystem. This is where an operator checks a run
before pressing start.

| Column | Type | Notes |
|---|---|---|
| ` ` | state dot | 8 px |
| `Variável` | icon + label | Click selects and navigates to Visão Geral |
| `PV` | tabular readout | `—` when absent; `comandado` tag where applicable |
| `SP aplicado` | tabular, `TextSecondary` | What the device acknowledged |
| `Novo SP` | inline `TextBox`, 88 px | Inline validation; `AccentBorder` when dirty |
| `Unidade` | 11 px `TextMuted` | |
| `Faixa` | 11 px `TextMuted` | e.g. `15,0-60,0 °C` |
| `Ativo` | `ToggleSwitch` | **Blue when on, never green** (3.3) |
| `Modo` | `ComboBox` | `Manual` · `Automático` · `Receita` — per subsystem |
| `Dono` | 11 px badge | Who currently owns the command: operator, cascade, recipe |
| ` ` | `Aplicar` / `Reverter` | Per row |

Page footer:

| Control | Type | Behaviour |
|---|---|---|
| `Aplicar alterações (n)` | primary button | Sends only dirty rows, **combined into one command object** where the protocol allows — it reduces round trips on the shared UART |
| `Reverter tudo` | secondary | Restores acknowledged values |
| `Salvar como predefinição…` | secondary | Named setpoint set |
| `Carregar predefinição ▾` | dropdown | Fills fields; **does not send** |
| `⛔ Parada segura` | **danger** | Confirmation dialog (7.2). Sends every subsystem's safe-off, including the flow safe-stop that forces both valves closed and the complete pH-off state |

> **`Parada segura` is the only red button in the application.** Flow disable is not
> merely zero flow: `flowmeterComm:0, flowSetpoint:0, v_Flow:1, valve_1:0, valve_2:0`,
> because leaving a nitrogen valve open through a stop is a hazard.

Below the table, cards for subsystems that are not simple setpoints:

| Card | Controls | Phase |
|---|---|---|
| **Válvulas** | `valve_1` aux toggle · `valve_2` N₂ toggle · `v_Flow` vent state (read-only, **derived and inverted** — shown so nobody has to remember the inversion) · `maxFlow` entry | 1 |
| **Agitador de frasco** | `Ativo` · `Automático` · `Intensidade` 0-100 slider + entry · `Sentido` `Horário`/`Anti-horário` radio · `Reativar potenciômetro` button | 2 |
| **Controle de pH** | `Setpoint` · `Banda inativa` · `Bomba ligada (s)` · `Repouso/mistura (s)` · `Velocidade %` · `Ativo`; all five wire fields are atomic | 2 WP3 — built |
| **Dosagem — Nutriente** | `Operação` · `Mistura` · `Ciclo op.` · `Ciclo mist.` · `Intensidade %` · `Ativo` | 2 |
| **Dosagem — Antiespumante** | `Operação` 0-999 · `Mistura` 1-999 · `Intensidade` 0-99 · `Ativo` | 2 |
| **Controle de espuma** | `Sensor ativo` · `Referência (mm)` · `Atraso inicial (s)` · `Pulso (s)` · `Intervalo (s)` | 2 |
| **Bomba externa** | `Ativa` · `Modo ▾` `Constante`/`Linear`/`Exponencial`/`Polinomial`/`Por segmentos` · parameters per mode · **profile preview chart** | 3 |
| **Biomassa** | `Ativo` · `Capturar branco` · `Limiar baixo`/`alto`/`ótimo` · live `Abs`, `Raw`, `IT`, `PWM` | 3 |

> The polynomial pump mode takes `p0..p20` — 21 coefficients. Present it as a compact
> grid with a live curve preview, not 21 stacked labelled fields. Piecewise takes
> `num_segments` with `t0..tN` / `q0..qN`: an editable two-column point table plus the
> same preview.

#### Tab 2 — `Cascata e sintonia` *(Phase 2)*

The scientific payload gets a real workspace.

| Group | Controls |
|---|---|
| **Malha** | `Controlada ▾` (O₂) · `Manipuladas` checklist: `Agitação` · `Aeração` · `Enriquecimento (N₂)` — the last disabled until the enrichment path is validated |
| **Janelas de atuação** | Per actuator: `Mín` · `Máx` · `Prioridade` · overlap band, with a stacked bar showing the allocation |
| **Trajetória kLa** | `Superfície ▾` (loaded surface file) · `Ponto inicial: máxima folga` / `manual` · `Passo` · contour plot of kLa over agitation × aeração with the gradient path drawn |
| **PID** | `Kp` · `Ki` · `Kd` · `I_min` · `I_max` · `Horizonte de predição (s)` 30-60 · `Janela de estimativa de taxa (s)` |
| **Termos ao vivo** | Read-only tabular: `P` · `I` · `D` · `dSaída` · `Saída` · `DOT_pred`, updating at 1 Hz |
| **Gráfico de sintonia** | Role palette (3.5): PV solid blue, SP dashed green, output dotted orange, limits thin red |
| Footer | `Aplicar` · `Reverter` · `Salvar sintonia…` · `Carregar sintonia ▾` |

The form makes the corrected design visible rather than hiding it: `I_min`/`I_max` exist
because v.6 wound up over long transients; the prediction horizon exists because the
polarographic probe has 20-40 s of dead time; the output is **velocity-form**, so the
displayed `Saída` is `Saída[i-1] + dSaída[i]` and the label says so.

---

### 5.3 Receitas

**Purpose:** author, validate, store and run automated experimental protocols.
**Phase 3** ([D-009](DECISIONS.md)). A main window at exactly the same level as Visão
Geral, Históricos, Alarmes, Eventos, Calibrações and Configurações — never a sub-tool,
never a separate application.

The identity of the page is:

> **A graphical experimental-protocol editor integrated directly into the bioreactor
> control system.**

It is Option A translated into a node editor: same font, same neutral palette, same
navigation, same KPI styling, same icons, same button geometry, same status semantics —
with a large graphical canvas as the main workspace.

#### 5.3.1 What is inherited, and what is removed

The concept and the engine architecture come from **ReceitasTECNAL**
(`…/Ourofino SA/Aplicativos/ReceitasTECNAL/app`). The UI, visual language and product
identity do not. Reading that source turns up a structural fact the mockups hide:

> **ReceitasTECNAL drives a different machine.** It reads a TECNAL HMI over **Modbus**
> and writes setpoints by **screen-scraping that HMI over VNC** — see its
> `Services/Automation/VncAutomationService.cs`, `VncScripts/`, and the comment in
> `ProcessVariable.cs` recording that Modbus control-word writes *do not work* on the
> real HMI. TECNAL-Hub talks directly to the ESP32-S3 in JSON over USB or Wi-Fi. The node
> graph, the engine slicing and the control mathematics carry over. **The entire
> transport layer does not.**

**Removed completely** — from both the old UI and the concept mockups:

| Removed | Why |
|---|---|
| Ourofino logo, dark-blue branded header, second branded bar | The shell has no branding; the window is the application ([4.1](#41-title-bar--48-px)) |
| **Entire VCN/VNC robot panel** — `CONFIGURAÇÃO DO ROBÔ VCN`, general parameters (`Tela (s)`, `Pop-up (s)`, `Clique (s)`), user/password, `Modelo VCN-5000`, `Com Port`, rack positions, default volumes, robot calibration, movement tests, `Salvar Configuração` | Pipetting-robot automation is not part of this product. TECNAL-Hub has no VNC layer at all |
| Right-hand pane permanently occupied by hardware settings | The right pane is **contextual**, mirroring the Visão Geral detail pane |
| Modbus register map, `ScaleType`, `ObterFatorEscala` (×1/×10/×100), `ControlWordBit` numeric positions | Modbus scaling artefacts. The JSON wire carries engineering units |
| Duplicated in-page navigation (`Editor` / `Modelos` / `Biblioteca` nested under the global rail, as in `Receitas_idea_2.png`) | The global rail is the only global navigation. Recipe sub-views are **tabs inside the page** |
| Separate connection/controller strip (`Sistema`, `Controlador VCN-5000`, `IP do TECNAL`, `Encontrar`) | Connection lives in the title-bar chip, once, for the whole app ([8](#8-connection-ux)) |
| Global action buttons in the title bar (`Receitas_idea_2.png`) | `Salvar` / `Carregar` / `Nova` are **page** actions and belong in the page action bar |
| Excessive dark chrome, multiple unrelated status bars | One status bar, at the shell level ([4.6](#46-status-bar--32-px)) |

**Inherited, and improved while re-implementing:**

| Inherited | Improvement |
|---|---|
| Node graph, ports, connections, JSON persistence | **Node definitions declared once and generated.** ReceitasTECNAL hand-writes a model + viewmodel + view triple per node type — ~10 near-duplicates in `Models/Nodes`, `ViewModels/Nodes`, `Views` |
| `RecipeEngine` sliced by responsibility (`.Flow`, `.Nodes`, `.Actuation`, `.Cascade`, `.Pumps`, `.Safety`, `.State`, `.LiveTuning`, `.Logging`) | Keep the slicing exactly. It is the best-organised part of that codebase |
| `RecipeValidator` with a real rule set | Keep every rule; surface them in a **persistent validation strip** rather than only on save |
| `ConnectorNames` with canonical + tolerated spellings | Keep the pattern. Canonical names are written; historical spellings are only *read*, so old hand-written recipes still load |
| Live parameter tuning during execution (`RecipeEngine.LiveTuning.cs`) | Keep — it is what makes the cascade block usable during a real cultivation |
| Recipe JSON | **Versioned from v1 with a migration hook.** ReceitasTECNAL learned this late and now converts v2.x recipes at load time with log warnings |

> **One command queue, one owner.** The engine drives the **same** `ITransport` and the
> same command queue as manual control, so a running recipe and an operator cannot fight
> over the link. This is why `Modo` ([4.2](#42-navigation-rail--184-px-expanded-52-px-collapsed))
> exists.

#### 5.3.2 Page layout

Target geometry at 1920 × 1080. The canvas is the dominant surface and expands into any
space the optional panes give back.

```text
┌──────────┬────────────────────────────────────────────────────────────────┐
│          │ KPI strip (shell, 4.4)                                         │
│  Global  ├────────────────────────────────────────────────────────────────┤
│  nav     │ Receitas ⓘ          [Salvar] [Carregar] [+ Nova]   Execução ▶ ⏸ ⏹│  action bar 44px
│  184px   ├────────────────────────────────────────────────────────────────┤
│          │ Minhas Receitas  Receita_Exemplo  Nova Receita 1 ✕   +          │  tabs 34px
│          ├───────────┬─────────────────────────────────┬──────────────────┤
│          │  Blocos   │  ↶ ↷  ⊖ 100% ⊕  ⛶   ✓ Validada  │  Propriedades    │  toolbar 36px
│          │  210px    ├─────────────────────────────────┤  do Bloco        │
│          │           │                                 │  340px           │
│          │           │           CANVAS                │  contextual      │
│          │           │                                 │                  │
│          ├───────────┴─────────────────────────────────┴──────────────────┤
│          │ ▸ JSON da Receita                            ⧉  ⇩  ⛶           │  0 / 220px
├──────────┴────────────────────────────────────────────────────────────────┤
│ Status bar (shell, 4.6)                                                   │
└───────────────────────────────────────────────────────────────────────────┘
```

| Region | Width / height | Collapsible |
|---|---|---|
| KPI strip | 84 px | No — shell-level |
| Page action bar | 44 px | No |
| Recipe tabs | 34 px | No |
| Block library | 210 px (190-230) | Yes → 40 px icon strip |
| Editor toolbar | 36 px | No |
| Canvas | flexible, dominant | — |
| Properties pane | 340 px (320-380) | **Auto** — collapses when nothing is selected |
| JSON panel | 0 / 220 px (180-250) | Yes, collapsed by default |

Below 1400 px the properties pane overlays the canvas rather than displacing it; below
1200 px the block library collapses to its icon strip.

#### 5.3.3 Page action bar

| Control | Type | Style | Notes |
|---|---|---|---|
| `Receitas` | page title, 20 px Semibold | — | Left-aligned. **No logo** |
| `ⓘ` | icon button | — | Recipe metadata popover: name, version, author, created, modified, step count, estimated duration |
| `Salvar Receita` | button | secondary | `Ctrl+S`. Disabled when clean |
| `Carregar Receita` | button | secondary | Opens the library tab |
| `+ Nova Receita` | button | **primary, accent fill** | The one dominant action |
| `Execução` group | — | right-aligned, divider before | See below |

> **Only one of the three file actions is coloured.** Save, Load and New as three strongly
> coloured buttons simultaneously is how a toolbar stops having a hierarchy.

**Execution controls**, right-aligned in the action bar. Process execution is the one
place in the application where strong semantic colour on a *control* is legitimate,
because the control is the process action:

| Control | Colour | Enabled when |
|---|---|---|
| `▶ Iniciar` | `StateOk` green fill | Recipe valid, link connected, `Modo` free to take |
| `⏸ Pausar` / `▶ Retomar` | `StateWarning` amber | Running |
| `⏹ Parar` | `StateAlarm` red | Running or paused. Confirms, then safe-stops every declared subsystem |
| `Status:` | state chip | `● Parado` · `● Executando` · `● Pausado` · `● Falhou` |
| `Tempo:` | tabular readout | `00:00:00` |

Starting switches `Modo` to `Receita` and disables the manual controls the recipe owns.

#### 5.3.4 Recipe tabs

Conventional document tabs, 34 px high — not browser-sized.

| State | Style |
|---|---|
| Selected | White fill, 2 px `Accent` bottom rule, Semibold text |
| Inactive | `SurfaceSunken` fill, `TextSecondary` |
| Hover | `SurfaceHover` |
| Dirty | `*` after the name |
| Close | `✕` on hover or when selected; prompts if dirty |
| `+` | New recipe tab |

The first tab is `Minhas Receitas` — the **library**, which is a tab rather than a
separate sub-view so that browsing and editing are one keystroke apart.

**Library tab contents:** toolbar (`Importar…` · `Exportar…` · `Duplicar` · `Excluir` ·
search · `Ordenar ▾`) over a list showing name · description · version · block count ·
estimated duration · last modified · last run · validation state. Selecting a row shows a
graph thumbnail, the subsystems the recipe declares, and `Abrir` / `Executar…`.

#### 5.3.5 Block library

`Blocos` — part of the Receitas workspace, **not** global navigation. Collapsible
categories, searchable, 210 px.

Items are quiet rows, not large coloured buttons:

```text
Gatilhos                                    ▾
  ●  Temporizador
  ●  Monitorar Variável
  ●  Intervenção Manual
```

| Property | Value |
|---|---|
| Row height | 32 px (30-34) |
| Background | transparent; hover `SurfaceHover` |
| Radius | 4 px |
| Leading mark | 8 px category dot, or a 16 px outline icon |
| Text | 13 px Regular `TextPrimary` |
| Drag handle | appears on hover |
| Interaction | drag onto canvas, or double-click to drop at canvas centre |

Category header: 12 px Semibold `TextSecondary`, chevron, item count, remembered
expand/collapse state.

#### 5.3.6 The block inventory

Taken from ReceitasTECNAL's `NodeType` enum and node classes, then **re-targeted to the
ESP32-S3 protocol**. Nineteen block types in six categories.

| Category | Header colour | Blocks |
|---|---|---|
| **Fluxo** | slate `#596575` | Início · Fim |
| **Lógica / Cascata** | violet `#8B3CC2` | Sincronizar (E) · Qualquer (OU) · Controle Cascata O₂ |
| **Gatilhos** | blue `#3182F6` | Temporizador · Monitorar Variável · Intervenção Manual |
| **Ações** | orange `#E89A18` | Definir Ponto de Ajuste · Múltiplos Pontos de Ajuste · Controle de Malha · Múltiplos Controles |
| **Bombas** | teal `#0E8A8A` | Bomba pH · Bomba Antiespuma · Bomba Nutrientes · Controle da Bomba |
| **Utilitários** | slate `#596575` | Aquisição de Dados · Registrar Evento · Zerar Variáveis |

`Fim` keeps a **green** header `#3AA75B` as the single exception — successful termination
is the one place where a block's category and its meaning coincide.

Full parameter inventory:

| Block | Ports | Parameters | Re-targeting note |
|---|---|---|---|
| **Início** | out | — | Exactly one per recipe |
| **Fim** | in | — | At least one per recipe |
| **Temporizador** | in, out | `Duração` · `Unidade ▾` `Segundos`/`Minutos`/`Horas` | Unchanged |
| **Monitorar Variável** | in, out | `Variável ▾` · `Condição ▾` `>`,`<`,`≥`,`≤`,`=` · `Valor alvo` · `Intervalo de polling (ms)` · `Confirmações consecutivas` · `Tempo limite (ms)`, 0 = sem limite | **Variable list widens.** ReceitasTECNAL allows only Temperatura, pH, O₂. TECNAL-Hub adds Pressão, Vazão, Nível, Biomassa — all real telemetry |
| **Intervenção Manual** | in, out | `Operação ▾` `Bloquear`/`Passar`, plus a live action button on the block face | The recipe holds in standby at this block until an operator releases it |
| **Sincronizar (E)** | in ×n, out | — | All inbound branches must complete |
| **Qualquer (OU)** | in ×n, out | — | First inbound branch to complete wins |
| **Controle Cascata O₂** | in, out, **Saída Loop**, **Entrada Loop** | See 5.3.7 | The scientific core |
| **Definir Ponto de Ajuste** | in, out | `Variável ▾` · `Valor` · `Histerese` | Scale factors dropped — JSON carries engineering units |
| **Múltiplos Pontos de Ajuste** | in, out | Repeating list of (`Variável`, `Valor`, `Histerese`) with add/remove | Sent as **one combined command object** — the protocol prefers this, and it saves round trips on the shared UART |
| **Controle de Malha** | in, out | `Malha ▾` · `Operação ▾` `Ligar`/`Desligar` · `Período de ciclo (s)` for the gas mixer | **Re-targeted.** The Modbus control word is gone; enabling a loop means the subsystem's own enable (`flowmeterComm:1`) or setpoint `0` = off |
| **Múltiplos Controles** | in, out | Repeating list of (`Malha`, `Operação`) | Same re-targeting; combined into one object |
| **Bomba pH** | in, out | `Bomba alvo ▾` `Ácido`/`Base` · `Operação ▾` · `Intensidade %` 0-100 · `Tempo ligada (s)` · `Tempo desligada (s)` · `Ação manual ▾` `Ligar`/`Desligar` | Maps to `pHOperation`, `pHMix`, `pHIntensity` |
| **Bomba Antiespuma** | in, out | `Operação ▾` · `Intensidade %` · `Tempo ligada` · `Tempo desligada` · `Ação manual ▾` | Maps to `antifoamOperation`, `antifoamMix`, `antifoamIntensity`. Renamed from "Bomba Espuma" — it dispenses antifoam |
| **Bomba Nutrientes** | in, out | `Operação ▾` · `Tempo dosagem ligada` · `Tempo dosagem desligada` · `Volume a dosar` · `Ação manual ▾` | Maps to `nutriOperation`, `nutriMix`, `nutriOpCycle`, `nutriMixCycle`, `nutriIntensity`. Cycle-only; the v2.x `Dosagem` mode was removed upstream |
| **Controle da Bomba** | in, out | `Modo ▾` `Temporizado` · `Tempo ligado` · `Tempo desligado` | Phase 3 widens this to the external pump's five profile modes |
| **Aquisição de Dados** | in, out | `Modo ▾` `Tempo definido`/`Finalização manual` · `Duração` · `Unidade ▾` | Marks a labelled acquisition window in the session log |
| **Registrar Evento** | in, out | `Mensagem` | Writes to Eventos ([5.6](#56-eventos)) |
| **Zerar Variáveis** | in, out | — | **Re-targeted.** Was `Zerar Acumulador` writing Modbus register 20; becomes `resetVariables:1`. **Confirms before running**, because it destroys module process state |

> **`Monitorar Variável` must refuse actuation variables.** ReceitasTECNAL blocks
> monitoring Agitação and Aeração because they are setpoints, not measurements. On the
> ESP32-S3 the reason is stronger still: **agitation has no feedback key on the wire at
> all** ([2](#2-reality-check--the-mockups-against-the-hardware), item 1). Monitoring it
> would wait forever on a condition nothing can satisfy. The variable dropdown must not
> offer it, and the validator must reject it if a hand-edited recipe contains it.

#### 5.3.7 The cascade block

The one block that deserves its own specification, because it is the scientific payload
and because ReceitasTECNAL's implementation is **the corrected one** the roadmap says to
port ([ROADMAP](ROADMAP.md) Phase 2).

Four ports, and the loop pair is what makes it a cascade rather than a step:

| Port | Meaning |
|---|---|
| `Entrada` | Normal flow in |
| `Saída` | Flow out when the loop terminates |
| `Saída Loop` | Fires each loop iteration — drives the blocks inside the loop |
| `Entrada Loop` | Where the loop body returns |

Canonical spellings are `Saida Loop` and `Entrada Loop`; `Saída Loop` and `SaidaLoop` are
**recognised on load, never written**, so hand-written and legacy recipes keep working.

Properties, grouped in the pane:

| Group | Fields | Defaults (validated in the field) |
|---|---|---|
| **Setpoint** | `SP de O₂ (%)` | 30.0 |
| **Ganhos** | `K_DOT` (outer loop) · `Kp` · `Ki` · `Kd` | 0.07 · 0.065 · 0.0010 · 0.50 |
| **Anti-windup** | `I_min` · `I_max` · `Janela do integrador (s)` | −30 · +30 · 120 |
| **Predição** | `Horizonte t_pred (s)` · `Janela do preditor (amostras)` · `τ_D do filtro (s)` | 60 · 7 · 20 |
| **Estimativa de taxa** | `Método ▾` `Mínimos quadrados`/`Diferença de extremos` · `Janela da média (amostras)` | Mínimos quadrados · 9 |
| **Temporização** | `Intervalo de cálculo do PID (s)` | 3.0 |
| **Atuadores** | `Agitação` ☑ `Aeração` ☑ `Misturador de gases` ☐ | — |
| **Faixas físicas** | `N_min`/`N_max` rpm · `Q_min`/`Q_max` vvm · `O₂_min`/`O₂_max` % | 150-350 · 0.5-5.0 · 0-90 |
| **Janelas de atuação** | Per actuator `OutMin`/`OutMax` on a 0-100 % control axis | Agitação 0-40 · Aeração 30-70 · Misturador 60-100 |
| **Ganhos relativos** | Per actuator gain factor | 1.0 · 1.43 · 1.2 |
| **Laço** | `Loop infinito` | true |

Three things the form must make visible rather than hide, because each one is a fix for a
specific v.6 defect:

- **Velocity form.** The inner loop emits `dSaída`; the applied output is
  `Saída[i-1] + dSaída[i]`. A positional PID drives output to zero at setpoint, which is
  structurally wrong — the organism keeps consuming oxygen. Label the displayed output
  accordingly.
- **Sliding-window integral with explicit clamps.** `I_min`/`I_max` and the window length
  exist because v.6 wound up over long transients.
- **Prediction horizon.** `DOT_pred = DOT + (dDOT/dt)·t_pred`, with the rate estimated by
  **least squares over the window** rather than an endpoint difference — that is what
  cancels the polarographic probe's quantisation staircase across its 20-40 s dead time.

The **actuation windows overlap on purpose**: as demand rises the controller moves
agitation first, brings aeration in from 30 %, and only reaches for gas enrichment above
60 %. Show this as a **stacked horizontal bar** with a live marker at the current output —
it is far more legible than six numeric fields, and it is the whole allocation strategy in
one glance.

> `Misturador de gases` is the **nitrogen-enrichment path** and ships **disabled**. The
> cascade core is stable without it; enrichment needs the changes described in the
> manuscript and is explicitly deferred ([ROADMAP](ROADMAP.md), *Explicitly deferred*).
>
> ReceitasTECNAL's `IntervaloAtuacaoVnc` (batched actuation every 5 s through the VNC
> screen driver) has **no equivalent here and must not be ported**. TECNAL-Hub writes
> directly to the ESP32-S3; the only rate limit is the command queue.

#### 5.3.8 Canvas

| Property | Value |
|---|---|
| Background | `#FFFFFF` |
| Grid | Dots at 16-20 px in `#E8EDF3` — present, never competing with the diagram |
| Pan | Space-drag, middle-drag, or the Pan tool |
| Zoom | `Ctrl`+wheel, 25-400 %, snapped presets in the toolbar |
| Fit | `⛶` fits the graph to the viewport |
| Selection | Click, `Ctrl`+click to add, marquee drag |
| Move | Drag with snap-to-grid; arrow keys nudge 1 px, `Shift`+arrow 8 px |
| Delete | `Delete` — removes selected blocks and their connections |
| Undo / redo | `Ctrl+Z` / `Ctrl+Y`, full graph history |
| Align | Align and distribute for multi-selection; `Auto-organizar` for the whole graph |
| Minimap | Bottom-right, fades in when the graph exceeds the viewport |

The canvas should carry **plenty of empty space**. Density belongs to the properties pane,
not the diagram.

#### 5.3.9 Block geometry

```text
┌────────────────────────────┐
│ ◉ Controle Cascata O₂   ⋮ │  ← header, category colour, white icon + text
├────────────────────────────┤
│ Cascata O₂: SP 30 %        │  ← summary line, 12 px TextSecondary
│                            │
│ ● Saída do Loop            │  ← ports with labels
│ ● Entrada do Loop          │
├────────────────────────────┤
│ ▸ Propriedades             │  ← expander, opens the right pane
└────────────────────────────┘
```

| Property | Value |
|---|---|
| Width | 220-260 px |
| Radius | 6 px |
| Border | 1 px `#D9E0E8` |
| Shadow | `0 1px 3px rgba(0,0,0,0.08)` — barely there |
| Header height | 32 px, category fill, 16 px white outline icon, 13 px Semibold white text, `⋮` overflow |
| Body | White, 12 px padding |
| Inputs | 28-30 px high, 4 px radius, `#D9E0E8` border, `#2563D9` focus |
| Labels | 11 px `TextSecondary` above the field; the **value** carries the visual weight |

> **Category colour lives in the header only, never in the whole card.** A canvas of
> fully-coloured cards reads as a toy programming environment, and it would put six more
> colours into competition with the state palette.

**Execution state** is a separate channel from category colour, so the two can never be
confused:

| State | Rendering |
|---|---|
| `Aguardando` | Normal border |
| `Avaliando` | 2 px `StateActuating` border + a slow pulse on the left edge |
| `Concluído` | 2 px `StateOk` left edge, header desaturated to 70 % |
| `Erro` | 2 px `StateAlarm` border + an alarm icon in the header |

Selected: 2 px `#2563D9` border and an `AccentSubtle` background wash. **The block is not
recoloured.**

#### 5.3.10 Ports and connectors

| Element | Spec |
|---|---|
| Port | 8-10 px circle. Unconnected `#526071`, connected `#2563D9`, hovered ring, invalid target `#DC3545` |
| Port label | 12 px `TextSecondary`, inside the block |
| Connector | 1.5-2 px orthogonal bezier, small arrowhead |
| Defined flow | **`#596575` neutral slate** |
| Executed path | `#24A35A` green, drawn during and after execution |
| Selected | `#2563D9` |
| Unreachable | `#A5ADB8` dashed |
| Invalid | `#DC3545` with a reason tooltip; the connection does not commit |

> **Deliberate deviation from the concept mockups.** Both drawings colour *every*
> connector green, which makes an idle recipe look like a running one and spends the app's
> strongest "healthy" signal on a static diagram. Neutral slate for the defined graph, and
> green **only for the path execution has actually taken**, keeps rule 1 intact and turns
> the canvas into a live execution display for free.

#### 5.3.11 Properties pane

Contextual, 340 px, mirroring the Visão Geral detail pane's hierarchy. **When nothing is
selected it collapses and the canvas takes the space.**

```text
Propriedades do Bloco                    ✕
──────────────────────────────────────────
◉ Temporizador
──────────────────────────────────────────
Duração
[ 720                                    ]
Unidade
[ Minutos                              ▾ ]
Ao completar
[ Próximo bloco                        ▾ ]
──────────────────────────────────────────
Validação
✓ Configuração válida
```

| Region | Contents |
|---|---|
| Header | Category icon + block title + `✕`; `Renomear` in the overflow |
| Parameters | The block's fields, using the same validated entry controls as the rest of the app ([9](#9-input-validation)) |
| Notes | `Notas` free text, shown as a tooltip on the block |
| Condition | `Executar apenas se…` optional guard |
| Validation | Live per-block result: `✓ Configuração válida` or the specific findings |
| Footer | `Aplicar` / `Reverter` when the block is under a running recipe; immediate otherwise |

During execution the pane also shows **live values** for the selected block — for the
cascade block, the same `P` / `I` / `D` / `dSaída` / `Saída` / `DOT_pred` readout specified
in [5.2](#52-controle), with editable gains, because tuning while the culture runs is the
point of `RecipeEngine.LiveTuning`.

#### 5.3.12 Editor toolbar

Above the canvas, 36 px, icons with tooltips.

`↶` Desfazer · `↷` Refazer │ `⊹` Selecionar · `✋` Mover │ `⊖` `100% ▾` `⊕` · `⛶` Ajustar
à tela │ `⊞` Auto-organizar │ right-aligned: `✓ Validada` / `⚠ 3 problemas`, `⋮`.

Zoom presets: `50%` · `75%` · `100%` · `125%` · `150%` · `Ajustar`.

Overflow `⋮`: `Exportar imagem…` · `Copiar imagem` · `Mostrar grade` · `Ajustar à grade` ·
`Mostrar minimapa` · `Mostrar rótulos das conexões`.

#### 5.3.13 Validation

A chip in the toolbar, always visible, never a modal:

| State | Chip |
|---|---|
| Valid | `✓ Receita válida` — `StateOk` text on a subtle green tint, restrained |
| Warnings | `⚠ 2 avisos` — amber |
| Errors | `⚠ 3 problemas` — red |

Clicking opens a findings list; clicking a finding selects and centres the offending
block. `Iniciar` is disabled while any error stands.

Rules ported directly from `RecipeValidator.cs`:

| Scope | Rule |
|---|---|
| Graph | Recipe contains no blocks · duplicate or empty block id · must contain **exactly one** `Início` · must contain **at least one** `Fim` · connection references a missing source or target · block unreachable from `Início` · no path from `Início` to `Fim` · cycle detected outside a loop construct |
| Temporizador | Duration negative, `NaN` or infinite |
| Setpoint | Value outside the subsystem's device range |
| Malha | Invalid target loop |
| Bombas | Intensity outside 0-100 % · times negative, `NaN` or infinite · times exceeding 3600 s · invalid operation for that pump |
| Monitorar | Polling interval ≤ 0 · consecutive-hold < 1 · negative timeout · target `NaN`/infinite · **actuation variable selected** |
| Cascata | PID interval outside 0.1-60 s · no actuator selected · actuator min > max · window outside `0 ≤ OutMin < OutMax ≤ 100` |

Ranges come from **the same source as the manual setpoint fields and the wire builders**,
so a recipe cannot accept a value the device would reject.

#### 5.3.14 JSON panel

Kept, because it is genuinely useful for engineering and for the Phase 0 byte-comparison —
but no longer a large permanent region. Collapsed by default:

```text
▸ JSON da Receita
```

Expanded, 220 px:

| Element | Spec |
|---|---|
| Header | `JSON da Receita` + `⧉` copiar · `⇩` exportar · `⛶` expandir · `▾` recolher |
| Editor | Light background, line numbers, syntax highlighting, read-only by default with an `Editar` toggle |
| Font | **Cascadia Mono** (Cascadia Code fallback), 11-12 px |
| Sync | Selecting a block highlights its object; editing and applying re-validates |

> The JSON viewer is one of the very few places in the application permitted to use a
> monospace face. Process readouts use Segoe UI with tabular figures instead
> ([3.7](#37-typography)).

#### 5.3.15 Process versus recipe

The single most important conceptual distinction on this page, and the one the old
interface blurred:

```text
PROCESSO   (KPI strip, top)        O₂ = 40,2 %        ← measured, now
   ↓
RECEITA    (canvas, centre)        O₂ SP → 40 %       ← intended, later
   ↓
BLOCO      (properties, right)     após Temporizador  ← how this step behaves
```

The KPI strip describes the **real process**. The canvas describes the **recipe
definition**. A recipe target must never be mistaken for a current measurement.

Reinforced three ways: placement (process on top, recipe in the middle, configuration on
the right); typography (KPI values 24-26 px Semibold with a state dot, block setpoints
13 px inside a bordered field); and wording — blocks say `SP → 40 %` or
`Definir O₂ = 40 %`, never a bare `40 %`.
### 5.4 Alarmes

**Purpose:** raise, present, acknowledge and configure alarms. **Phase 5** — nothing
raises an alarm today, which is why this window and its engine are specified together.

Three tabs: `Ativos` · `Histórico` · `Configuração`.

#### 5.4.1 `Ativos`

| Column | Notes |
|---|---|
| Severity | Icon + colour: `Alarme` red · `Aviso` amber · `Informação` blue |
| Variable | Icon + label; click selects it on Visão Geral |
| Message | pt-BR, e.g. `O₂ acima do limite alto (45,2 % > 44,0 %)` |
| Value / limit | Tabular, side by side |
| Since | `hh:mm:ss` + duration |
| State | `Não reconhecido` · `Reconhecido` · `Normalizado, não reconhecido` |

Toolbar: `Reconhecer` · `Reconhecer todos` · `🔇 Silenciar áudio (10 min)` · severity
filter · `Somente não reconhecidos` toggle.

Rows sort by severity then age. An alarm that returns to normal while unacknowledged
**stays in the list** in the third state — an alarm nobody saw is the one worth keeping.

#### 5.4.2 `Histórico`

Same columns plus `Normalizado em`, `Reconhecido por`, `Duração`. Filters: date range,
variable, severity, text. `Exportar CSV…`.

#### 5.4.3 `Configuração`

One row per variable:

| Field | Type |
|---|---|
| `Ativo` | toggle |
| `LL` · `L` · `H` · `HH` | four numeric entries, in engineering units |
| `Banda morta` | numeric — stops a value sitting on a limit from chattering |
| `Atraso (s)` | numeric — condition must persist before raising |
| `Severidade` | `ComboBox` per level |
| `Som` | toggle |
| `Ação` | `ComboBox`: `Somente registrar` · `Notificar` · `Parada segura` |

Plus **system alarms**, which are not variable limits and must be configurable
separately:

| Alarm | Trigger |
|---|---|
| `Link perdido` | Connection state leaves Connected |
| `Módulo sem resposta` | `SensorCommOK` false |
| `Fluxômetro offline` | `FlowmeterOnline` false |
| `Dados congelados` | No accepted frame for > 3 telemetry periods |
| `Sensor ausente` | Channel sentinel persists beyond a grace period |
| `Comando não confirmado` | `FlowCommandId` without a matching `FlowCommandAck` |
| `Espuma persistente` | `Distance` below reference beyond the foam timers |

> Alarm limits are entered in **engineering units** and stored that way. They must not be
> confused with the spike-filter thresholds in Configurações, which are in **raw ADC
> counts** — a distinction that has already caused one documented defect
> ([MIGRATION.md](MIGRATION.md) item 4).

---

### 5.5 Gráficos and Históricos

**Purpose:** read trends and get data out. **Phase 1b WP7 built.** These are two
dedicated rail destinations, not tabs: `Gráficos` owns the focused dual-chart workspace;
`Históricos` owns persisted-session discovery and hands an accepted file to Gráficos.
This supersedes the earlier proposed `Gráficos`→`Históricos` rename and preserves the
operator's dedicated two-graph page.

#### 5.5.1 `Gráficos`

Two large panels side by side. Side by side rather than stacked because a trend is read
along the time axis, and on a wide screen that is where the pixels are. Five small charts
fitted on screen and answered nothing.

Per panel:

| Control | Type | Options |
|---|---|---|
| Channel | `ComboBox` | Every channel the app can actually produce — see the inventory. Never an always-empty channel |
| `Mostrar setpoint` | checkbox | Dashed green overlay |
| `Mostrar limites` | checkbox | Thin red HH/H/L/LL |
| `Eixo Y` | `ComboBox` | `Automático` · `Fixo` (+ min/max entries) |
| `⤓` | menu | `Exportar PNG…` · `Exportar CSV…` · `Copiar imagem` |
| `✕` | button | Collapse — the other panel takes the full width |

Shared toolbar: `Janela ▾` (`5 min` · `30 min` · `2 h` · `12 h` · `Tudo`) ·
`⏸ Pausar` · `Cursor` toggle · `Sincronizar eixo X` · `Exportar tudo…`.

The cursor is a vertical rule with a readout box giving each visible series' value at that
instant — the thing an operator actually wants when comparing two variables.

| Concern | Decision |
|---|---|
| History | Fixed-capacity ring buffer, ~48 h at the field `dataDelay`. Flat memory |
| Downsampling | Stride-sampled to 2000 points before the plot — no display has 86,000 horizontal pixels |
| Sentinels | Stored as `NaN`, so a chart shows a **gap**, never a line diving to −1 |
| Redraw | 1 Hz on a timer, not per frame. The device emits every 2 s |
| Colour | Identity palette here; role palette in single-loop views (3.5) |

The same panels render live telemetry or a session loaded from Históricos. Source and
sample count are always stated; `Voltar ao tempo real` removes the file source without
starting or changing any equipment communication.

#### 5.5.2 `Históricos`

Closes a real gap: v.6 exported a PNG per parameter while the session file had no UI.

| Region | Contents |
|---|---|
| File list | Name · date range · duration · rows · size · connection medium |
| Toolbar | `Atualizar` · `Abrir pasta` · `Carregar no Gráficos` · `Exportar CSV` · search |
| Preview | Column summary, first and last rows, and a **format check** confirming the header matches `SessionLogFormat.Header` exactly |

> The session log is tab-separated, UTF-8, with v.6's exact header including the accented
> final column `Conexão`. Column set, order and decimal places are a **contract** with
> existing analysis scripts, not a formatting preference. Columns outside the current
> phase are still emitted carrying the not-received sentinel so the column count never
> changes between versions.

---

### 5.6 Eventos

**Purpose:** one honest, filterable audit trail of everything that happened. **Phase 1b
WP7 built.** Sources whose owning subsystem arrives later (`Alarme`, `Receita`) are
already typed and filterable; their producers arrive with those phases.

The old Registro page showed device log lines and nothing else — which made it **empty
over Wi-Fi**, because `[ESP32_` lines only appear on the serial stream. Eventos fixes
that gap by recording app-known facts on both media.

| Column | Notes |
|---|---|
| Timestamp | `hh:mm:ss.fff`, tabular |
| Source | Badge — see below |
| Severity | Icon + colour |
| Message | pt-BR |
| Detail | Expander: raw wire bytes, exception, previous/new value |

| Source | Contents | Available |
|---|---|---|
| `Equipamento` | `[ESP32_AVISO]` device log lines | USB only |
| `Comando` | Every command sent, **with the exact JSON put on the wire** | Both |
| `Conexão` | State changes, retries, medium switches, handshake attempts | Both |
| `Setpoint` | Applied setpoints: variable, old, new, who owned the command | Both |
| `Alarme` | Raised, normalised, acknowledged | Both |
| `Receita` | Step entered/left, condition evaluated, abort | Both |
| `Calibração` | Coefficients applied, with before/after | Both |
| `Aplicação` | Startup, shutdown, settings applied, theme, errors | Both |

Toolbar: source multi-select · severity filter · text search · time range ·
`Seguir novas entradas` toggle (auto-scroll) · `Pausar` · `Copiar` · `Exportar…` ·
`Limpar visualização` (view only, never the file).

**Session-log panel** at the foot of the page:

| Control | Type |
|---|---|
| `Gravando` / `Parado` | state chip |
| Path | read-only + `Alterar…` + `Abrir pasta` |
| `Linhas` · `Tamanho` · `Início` | tabular readouts |
| `Iniciar` / `Parar registro` | toggle button |
| `Novo arquivo` | button — closes the current file, opens the next |

> **Showing the raw wire bytes is deliberate.** [Phase 0's](ROADMAP.md) outstanding exit
> criterion is that a v.6 session and a TECNAL-Hub session produce **identical command
> bytes** for the same operator actions. This page is where that comparison is made
> without attaching a debugger.

---

### 5.7 Calibrações

**Purpose:** run calibration *procedures* with live feedback. **Phase 2 WP3 built for
pH, oxygen and airflow.** Level and biomass procedures remain Phase 3 work.

Calibration is not a coefficient form. The dedicated destination uses three segmented
tabs and keeps preparation, acquisition, the current curve, the proposed result and the
explicit apply/send action visible together at 1280×800. The full operational contract is
[CALIBRATION.md](CALIBRATION.md).

| Tab | Ownership | Implemented procedure |
|---|---|---|
| **pH** | App parser; accepted values return to the module as quoted `pHCal` | One point keeps the current slope; two points replace the pair. Twenty accepted raw frames establish stability (`sample σ < 5` by default), then twenty distinct frames are averaged |
| **Oxigênio** | App parser only; there is no v.6 coefficient command | Direct two-point zero/span capture with the live raw and decoded values beside the current equation |
| **Vazão de ar** | Dedicated flowmeter firmware | Certified real-flow rows, prepare/fine-adjust controls, ten-frame `FlowVoltage` average, live plot and the fixed two-segment 0.0545 V curve |

#### pH control and calibration interlock

The five-field dosing panel (`setpoint`, inactive band, pump-on time, mix/rest time and
speed) appears on Painel and Controle, not inside the calibration form. Starting either
pH procedure first sends a **complete pH-off state**, then asks the operator to move the
probe to the buffer. The pump stays off after cancel/apply; reactivation is explicit only
after the probe returns to the vessel. Losing the link while waiting or acquiring refuses
the run without changing coefficients.

The pH acquisition counts telemetry events, not timer polls, so the same device frame can
never satisfy multiple samples. The review card shows the current and proposed equations.
Equal raw means, non-finite results and degenerate slopes are refused instead of installing
v.6's unsafe `slope=1` fallback.

#### Oxygen and airflow

Oxygen is deliberately direct because v.6 has no stability wizard: the operator
stabilizes the physical standard, then captures the current accepted raw value. Applying
changes both coefficients together and sends no wire command.

For airflow, the operator enters the external standard's real flow, prepares that point,
fine-adjusts the commanded flow, and captures ten distinct voltages. Point editing locks
during capture. The low segment (`V <= 0.0545`) needs three points; the high segment uses
a line with two or a quadratic with three. A partial curve may be sent because v.6 allows
it, but the page labels it partial. **Parar ensaio de vazão** always exposes the complete
flow safe-stop.

> **Two warnings are permanent design constraints.**
>
> 1. Calibration criteria and spike filters are in raw ADC counts. Applying a curve does
>    not rescale them; the page states this beside the pH acquisition fields.
> 2. Coefficients are applied as a pair. A calculated result remains merely proposed until
>    the operator explicitly applies it; a flow curve remains local until explicitly sent.

**Deferred:** known-level reference, biomass blank/thresholds, calibration history and
rollback. They must use the same ownership and explicit-apply language when introduced.

---

### 5.8 Configurações

**Purpose:** everything that is genuinely configuration, out of the way. **Phase 1b
WP7 built**, including in-page section navigation ready for later subsystem sections.

Two columns: a 230 px section list on the left, the form on the right, a staged
apply/revert footer across the bottom.

| Section | Fields |
|---|---|
| **Conexão** | `Conectar automaticamente ao iniciar` · `Alternar de meio ao perder o link` · `Meio preferido ▾` `USB`/`Wi-Fi` · `Porta COM ▾` + `Detectar` · `Endereço Wi-Fi` · `Período de telemetria (ms)` · `Tempo limite de silêncio (s)` · `Pulsar DTR/RTS ao reconectar` (default **off**, with the reason inline) |
| **Aquisição** | `Filtro de spike — pH`: `Limiar absoluto` · `Tolerância de seguimento` · `Confirmações`; the same for `Oxigênio`. Banner: **valores em contagens brutas, não em unidades de engenharia** |
| **Unidades** | `Pressão ▾` `kPa`/`mmHg`/`bar` · `Temperatura ▾` `°C`/`°F` · `Volume da dorna (L)` · `Geometria da dorna…` (enables derived volume) · `Casas decimais` per channel. **Display only — the wire is unaffected** |
| **Registro** | Session log path · `Anexar a arquivo existente` · `Novo arquivo por execução` · rotation limit · app log path + level · `Abrir pasta de dados` |
| **Aparência** | `Tema ▾` `Sistema`/`Claro`/`Escuro` · `Mostrar barra de variáveis` · `Densidade ▾` `Confortável`/`Compacta` · `Animar fluxo no sinóptico` · `Configurar indicadores…` |
| **Comandos do equipamento** | `Resetar variáveis do módulo` · `Reiniciar comunicações` · `Enviar comando personalizado…` — **each behind a confirmation dialog** |
| **Sobre** | Version, build, .NET runtime, protocol contract version, data folder, third-party licences, `Copiar diagnóstico` |

> **The device commands must confirm.** `resetVariables` and `restart` act immediately,
> mid-cultivation, on one click today. Both destroy process state. Confirmation dialog
> 7.2, with the consequence spelled out.

**Staged apply.** Nothing here writes per keystroke. The footer shows
`n alterações pendentes` with `Aplicar` and `Reverter`; navigating away with pending
changes prompts. Calibration is the reason the whole page works this way — applying a new
slope against an old intercept, even for the half-second before the second field is
typed, would put visibly wrong numbers on screen and into the session log.

---

## 6. Shared components

Built once, used everywhere. A component defined here must not be re-implemented per
page — that is how twelve subsystems become twelve dialects.

### 6.1 `DeviceDetailPane`

The single most reused component. Identical on Visão Geral and anywhere else a subsystem
is inspected. **The information architecture never changes; only which sections are
present.**

```text
O₂ (Oxigênio dissolvido)                    ● Normal   ✕
──────────────────────────────────────────────────────────
PV                    SP                    Δ
40,2 %                40,0 %                +0,2 %
──────────────────────────────────────────────────────────
[                  trend chart                          ]
──────────────────────────────────────────────────────────
Controle
Modo de Controle
[ Cascata kLa (O₂ → agitação + aeração)               ▾ ]

Estado
[ SP & Limites ] [ Cascata ] [ PID ] [ Saída ]
──────────────────────────────────────────────────────────
Calibração                                             ›
Saúde do Sensor                                        ›
Configurações avançadas                                ›
```

| Region | Contents |
|---|---|
| **Header** | Icon · name · state chip (dot + word) · `✕` to deselect |
| **PV / SP / Δ** | Three columns. PV 36-40 px Semibold tabular; SP and Δ 18 px. Δ signed and coloured by state, not by sign |
| **Trend** | ~120 px sparkline-plus, role palette, window matching the KPI default. Click opens Históricos with this channel loaded |
| **Controle** | `Modo de Controle` dropdown, then a segmented tab strip |
| **Tabs** | Only the tabs that exist for this device — see the matrix |
| **Expanders** | `Calibração` · `Saúde do Sensor` · `Configurações avançadas`, each navigating to the relevant page section rather than duplicating it |

**Section matrix.** This is where [section 2](#2-reality-check--the-mockups-against-the-hardware)
item 7 lands concretely — the tabs are not uniform, because the underlying control is not.

| Device | SP & Limites | Cascata | PID | Saída | Dosagem | Calibração | Saúde |
|---|:---:|:---:|:---:|:---:|:---:|:---:|:---:|
| `temperature` | ✅ | — | — | — | — | — | ✅ |
| `ph` | ✅ *(P2)* | — | — | — | ✅ *(P2)* | ✅ | ✅ |
| `oxygen` | ✅ | ✅ *(P2)* | ✅ *(P2)* | ✅ *(P2)* | — | ✅ | ✅ |
| `motor` | ✅ | — | — | — | — | — | — |
| `flow` | ✅ | — | — | ✅ | — | ✅ | ✅ |
| `valves` | — | — | — | ✅ | — | — | — |
| `pressure` | ✅ | — | — | — | — | — | ✅ |
| `level` | ✅ *(P2)* | — | — | — | — | ✅ | ✅ |
| `antifoam` | — | — | — | — | ✅ *(P2)* | — | — |
| `nutrient` | — | — | — | — | ✅ *(P2)* | — | — |
| `biomass` | — | — | — | — | — | ✅ *(P3)* | ✅ |
| `pump` | — | — | — | ✅ *(P3)* | ✅ *(P3)* | — | — |

> **`temperature` has no PID tab and no output tab.** Its controller lives inside the
> device; the app sends `tempSetpoint` and can see neither the terms nor the actuator
> output. A PID tab there would describe a controller the application cannot observe.

**`Saúde do Sensor`** shows what the parser knows and nothing more: last accepted value
and timestamp, raw count, whether the spike filter is currently holding a candidate,
consecutive rejected samples, sentinel state, and — for flow — `FlowmeterOnline` plus the
`FlowCommandId`/`FlowCommandAck` correlation.

### 6.2 `KpiTile`

Specified in [4.4](#44-kpi-strip--84-px-visible-on-every-page). Reused unchanged by the
Controle summary and the recipe execution header.

### 6.3 `SetpointField`

The validated numeric entry, used by the detail pane, Controle, recipe blocks and
calibration procedures. Behaviour is specified once in [section 9](#9-input-validation).

```text
Setpoint
[ 30,0            ] °C        ● não aplicado
15,0-60,0 °C
```

| Part | Spec |
|---|---|
| Field | 30 px (28 dense), 4 px radius, tabular figures, right-aligned |
| Unit | Outside the field, 11 px `TextMuted` |
| Range hint | 11 px `TextMuted` below |
| Pending marker | `● não aplicado` in `StateWarningText` when the entry differs from the acknowledged value |
| Error | Inline, 11 px `StateAlarmText`, replacing the range hint. **The command is not sent** |
| Actions | `Aplicar` (primary, disabled while invalid) · `Reverter` |

### 6.4 `StateChip` and `StateDot`

The only two ways state colour reaches the screen.

- **Dot** — 8 px circle, state fill. Used in tiles, rails, tables, list rows.
- **Chip** — dot + word (`Normal` · `Atuando` · `Atenção` · `Alarme` · `Ocioso`), state
  *text* variant on a 10 %-tint background. Used in pane headers and page headers.

Never a coloured background block; never a coloured card.

### 6.5 `CommandedBadge`

A 10 px `TextMuted` pill reading `comandado`, attached to every surface that displays a
value with no feedback path — agitation, nutrient, flask agitator. Rendering it in one
component is what guarantees the marking cannot be forgotten on a new screen.

### 6.6 `TrendChart`

ScottPlot. Two configurations, one component:

| Mode | Used by | Palette |
|---|---|---|
| `Loop` | Detail pane, cascade tuning, recipe execution | Role ([3.5](#35-chart-colours)) |
| `Compare` | Históricos | Identity |

Shared behaviour: 1 Hz redraw, stride-sampled to 2000 points, `NaN` gaps, tabular axis
labels, cursor readout, pause.

### 6.7 `SectionHeader`, `Expander`, `SegmentedControl`

- **SectionHeader** — 13 px Semibold, 1 px `StrokeSubtle` rule beneath, 16 px above /
  8 px below.
- **Expander** — chevron + label, no card chrome; used for `Calibração`, `Saúde`,
  `Avançado`, and the JSON panel.
- **SegmentedControl** — the `[SP][Cascata][PID][Saída]` strip. 28 px, 4 px radius,
  selected segment white on `SurfaceSunken` with `Accent` text.

---

## 7. Dialogs and secondary surfaces

Four modal-dialog families. Calibration deliberately stays on its dedicated page so the
live readings, procedure state and proposed curve do not disappear behind a modal. Everything
else is a page, a pane or a popover
([D-006](DECISIONS.md)).

| # | Dialog | Trigger | Contents |
|---|---|---|---|
| 7.1 | **Conexão avançada** | Chip popover → `Configurações avançadas` | Navigates to Configurações → Conexão; not truly modal |
| 7.2 | **Confirmação destrutiva** | `Parada segura` · `Resetar variáveis` · `Reiniciar comunicações` · `Abortar receita` · `Excluir receita` | Title, plain-language consequence, the exact command that will be sent, `Confirmar` (danger) / `Cancelar`. Defaults to Cancel |
| 7.3 | **Abrir / salvar** | Recipes, session files, exports | Standard Windows dialogs. Do not re-implement |
| 7.4 | **Comando personalizado** | Configurações → Comandos do equipamento | Free-text JSON, validated as flat JSON before sending, with a preview of the exact bytes and a warning. Confirmation required |

**Popovers** (not dialogs): connection chip, KPI configuration, alarm bell context menu,
recipe metadata, chart export menu, validation findings.

**Toasts:** bottom-right, 4 s, for non-blocking confirmations (`Setpoint aplicado`,
`Calibração salva`, `Receita exportada`). Never for errors — errors go inline or to
Alarmes, because a toast that disappears is not an error report.

---

## 8. Connection UX

**Auto-connect, with a status chip.** Connecting was the only reason most users opened the
v.6 Configurations window, so it stops being a destination
([D-006](DECISIONS.md)).

On launch the app restores the last-used medium and port/IP and connects **in the
background, after the shell is already on screen**. The user sees the window immediately
and the chip resolves a moment later. Nothing heavy may sit between process start and a
visible window — the cold-start budget is first frame under 2 s.

```text
title bar:   ● Conectado · USB COM7  ▾
                └ click ────────────────┐
                ┌───────────────────────┴───┐
                │ ◉ USB    COM7 (ESP32) ▾ ⟳ │
                │ ○ Wi-Fi  192.168.4.1      │
                │                           │
                │ Latência    24 ms         │
                │ Quadros     1832          │
                │ Comandos    91            │
                │ Último erro —             │
                │                           │
                │ [ Reconectar ]  [ Parar ] │
                │ ⚙ Configurações avançadas │
                └───────────────────────────┘
```

| Chip state | Colour |
|---|---|
| `Conectado` | Ok |
| `Conectando…` | Actuating |
| `Reconectando…` | Warning |
| `Desconectado` · `Erro` | Alarm |

The diagnostics in the popover are fields the app already parses but never showed —
`FlowCommandDeliveries`, `FlowCommandAgeMs`, ack correlation
([MIGRATION.md](MIGRATION.md#3-known-defects-carried-in-from-v6) item 8).

> **`Latência` must not lie on USB.** `LastRoundTripMs` currently times a fire-and-forget
> write and reports ~0 ms. Either correlate it against `FlowCommandAck` or rename the
> field to say what it measures ([ROADMAP](ROADMAP.md) Phase 0 follow-ups, P2).

---

## 9. Input validation

A direct consequence of [MIGRATION.md](MIGRATION.md#3-known-defects-carried-in-from-v6)
item 11, where a malformed pH entry silently became setpoint 7.

- Out-of-range or unparseable input shows an inline error and **the command is not sent**.
- Ranges come from **one place per subsystem**, shared between validation, the recipe
  validator and the wire builder. Separating them is how a UI comes to accept a value the
  device will reject.
- Re-validate **where the send happens**, not only in `CanExecute`. Greying out a button
  does not stop the Enter key, a recipe engine or a test — and out-of-range values parse
  perfectly well.
- Accepting a decimal comma **and** a decimal point is required (pt-BR keyboards); the
  wire always gets `InvariantCulture`
  ([PROTOCOL.md](PROTOCOL.md#22-the-ph-echo-back)).
- A setpoint typed but not yet applied is **visually distinct** from one the device has
  acknowledged. v.6 gave no such feedback.
- Restoring a persisted value into a field is **not** an operator edit and must not raise
  the pending marker. A warning that is always on is a warning nobody reads.
- Disabling a subsystem must never be blocked by a typo in its value field.

---

## 10. Charts

**Two panels at most, side by side.** The rationale, the ring buffer, the downsampling,
the `NaN` gaps and the 1 Hz redraw are specified in
[5.5](#55-históricos); the palette split is in [3.5](#35-chart-colours).

Charts appear in four places, and all four use `TrendChart` ([6.6](#66-trendchart)):

| Place | Mode | Size |
|---|---|---|
| Detail pane | `Loop` | ~120 px |
| Históricos | `Compare` | Full panel |
| Controle → sintonia | `Loop` | ~280 px |
| Receitas → execução | `Loop`, with step boundaries | ~200 px |

---

## 11. Localisation and accessibility

**UI is pt-BR; code, comments and logs are English** ([CONVENTIONS.md](CONVENTIONS.md),
[D-007](DECISIONS.md)).

- All user-facing strings live in `.resx` **from day one** — not because an English UI is
  planned, but because inline strings make the pt-BR wording impossible to review in one
  place.
- Numbers display in the current culture (decimal comma) and are **always**
  `InvariantCulture` on the wire.
- Units are never translated: `°C`, `L/min`, `kPa`, `rpm`, `AU`, `mm`.

**Accessibility**, treated as a lab-readability requirement rather than a checkbox:

| Concern | Requirement |
|---|---|
| Contrast | Text ≥ 4.5:1, UI strokes ≥ 3:1. This is why state colours have text variants ([3.3](#33-process-state--the-colour-discipline)) |
| Colour alone | **Never the only signal.** Every state dot pairs with a word, an icon or a position. A red/green-blind operator must be able to run a cultivation |
| Focus | Visible 2 px `Accent` ring on every interactive element. Full keyboard reachability |
| Keyboard | `Ctrl+1..8` navigate · `Ctrl+K` command search · `Ctrl+R` variable rail · `Esc` closes drawer/dialog/selection · `Ctrl+S` save recipe · `F5` reconnect · `Space` pause charts |
| Hit targets | ≥ 24 × 24 px, including synoptic elements and canvas ports |
| Motion | Flow animation **off by default**; motion in peripheral vision competes with alarms |
| Text scaling | Layout survives 125 % and 150 % Windows scaling without clipping |

**Current implementation.** The seven present destinations use `Ctrl+1`–`Ctrl+7`;
Calibrações is 6, Configurações is 7, and the handler reserves `Ctrl+8`. `Ctrl+S`
is likewise reserved for Receitas: before Phase 3 it opens command search on a disabled
`Salvar receita` entry and states why it is unavailable. `Space` pauses Gráficos only
when an input, button, list item, tab or data-grid cell does not own the key. Closing the
palette by Escape, its button, command execution or outside click restores the previous
focus target.

Window persistence stores the last normal bounds, maximized state and stable page id.
Saved geometry is clamped to the current virtual desktop, with a centred fallback when
too little of the old window remains visible after a monitor-layout change. Runtime
evidence for the command palette and focus ring is in `docs/evidence/ui/wp8-*.png`.

---

## 12. Build order

What to do, in what order, so that the design lands before the UI is too large to change.
Scheduled as **Phase 1b** in [ROADMAP.md](ROADMAP.md#phase-1b--design-system-refit-and-shell-completion),
which carries the work-package breakdown, the open decisions and the exit criteria. This
section is the design-side summary; the roadmap is the plan of record.

### Phase 1b — before any new page

| WP | Work |
|---|---|
| 1 | Re-tint tokens to [3.1-3.6](#3-design-tokens); Light becomes the default. `FontNumeric` → Segoe UI with tabular figures; radii to 4 / 6 |
| 2 | Control templates that do not exist yet: `ToggleSwitch`, `SegmentedControl`, `Expander`, `ScrollBar`, table. Shared components from [6](#6-shared-components) |
| 3 | Icon `ResourceDictionary` in `Resources/Icons/`; wire the unused `NavigationItem.Glyph` |
| 4 | Nav rail to 184 px with icons, groups and the `Modo` footer. KPI strip scrollable, configurable, `comandado` badge, suppressed trend arrow. Status bar. Variable rail |
| 5 | Detail pane: PV/SP/Δ, state chip, inline trend, section matrix, `Saúde do Sensor` |
| 6 | `Controle` page — all-setpoints table, **valve card**, bulk apply, `Parada segura` — **done 2026-08-20** |
| 7 | `Gráficos` → `Históricos` (+ Sessões, export, cursor); `Registro` → `Eventos` (eight sources); Configurações section nav + Unidades; confirmation dialog ([7.2](#7-dialogs-and-secondary-surfaces)) |
| 8 | Persist window size, position, last page, rail state, KPI configuration. Keyboard map |

### Phase 2

9. Detail-pane tabs for `oxygen`: Cascata, PID, Saída.
10. `Controle` → `Cascata e sintonia`, with the overlapping actuator-window bar.
11. Complete pH control plus the pH/O₂/airflow Calibrações workspace — **done in WP3**.
    Nutrient, antifoam, foam and flask-agitator cards **and their synoptic elements** remain,
    added alongside each subsystem rather than in a later pass.

### Phase 3

12. Remaining level and biomass calibration procedures on the existing page.
13. Receitas: generated node definitions, canvas, library, properties pane, JSON panel,
    validator strip, execution view.
14. Biomass and external-pump cards, synoptic elements, chart channels.

### Phase 5

15. Alarm engine, then the Alarmes page and the bell badge. The page is fully specified in
    [5.4](#54-alarmes) and waits only on the engine.

### Continuous

16. Responsive verification at every breakpoint in [4.7](#47-responsive-behaviour).
17. Screenshot evidence per page in `docs/evidence/ui/`, refreshed after the re-tint.

---

## Appendix — what this document changed

Recorded so the previous revision's decisions are not silently lost.

| Was | Is | Why |
|---|---|---|
| Dark-first, Fluent `#0067C0` | **Light-first, `#2563D9`** | Long lab sessions; readability for graphs and numbers |
| Four nav destinations | **Eight main windows** | Receitas, Alarmes, Calibrações, Eventos and Controle all had roadmap deliverables with nowhere to live |
| "2+1 hybrid" | Same structure, plus an **optional variable rail** | Option B's density without a second layout |
| Chart palette: Okabe-Ito only | **Role palette + identity palette** | A single-loop plot and a multi-variable comparison are different questions |
| Five state colours | **Six, each with a text variant** | `Disabled` was missing; the vivid fills fail contrast as type |
| `FontNumeric` = Consolas | Segoe UI + **tabular figures** | Monospace reads as a terminal, not an instrument |
| Radius 4 / 8 / 12 | **4 / 6** | 8 px cards read as consumer software |
| Section numbering | Renumbered | Cross-references in `ROADMAP.md` and in four source-file comments point at old section numbers and need updating |
