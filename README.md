# TECNAL-Hub

Windows control application for the TECNAL bioreactor module, communicating with an
**ESP32-S3** hub over USB or Wi-Fi.

C# / WPF on .NET 10, rebuilding the working Python application at
`_Wifi Hub/Software/_Windows App/v.6` — faster to start, with a UI that scales to the
full instrument, and without ever changing the firmware protocol.

> **Status:** v0.9.0 — Phase 1 and Phase 1b WP1-WP8 are implemented. The dedicated
> dual-graph workspace shares persisted sessions with a separate Históricos browser;
> Eventos records exact transmitted JSON; window/page state persists; and the shell now
> has command search, keyboard navigation and visible focus treatment. USB and Wi-Fi were
> validated against a real ESP32-S3; live-sensor and full-cultivation validation still
> need the bioreactor.
> v.6 remains the production application until TECNAL-Hub has completed a full
> cultivation run.

---

## Documentation

Read in this order:

| Document | What it covers |
|---|---|
| [ROADMAP.md](docs/ROADMAP.md) | **Start here.** Phases, scope per phase, non-functional targets, what is deferred |
| [PROTOCOL.md](docs/PROTOCOL.md) | The frozen ESP32-S3 wire contract. Every key, unit, range and timing constant |
| [PHASE0_RESULTS.md](docs/PHASE0_RESULTS.md) | Hardware validation results — measured, on a real board |
| [PHASE_LOG.md](docs/PHASE_LOG.md) | Decisions taken while executing each phase, with evidence |
| [MIGRATION.md](docs/MIGRATION.md) | What each v.6 module becomes, why startup is slow, and 11 known defects found in the source |
| [ARCHITECTURE.md](docs/ARCHITECTURE.md) | Layers, threading model, directory layout, testing strategy |
| [UI_DESIGN.md](docs/UI_DESIGN.md) | Visual identity, the synoptic + detail layout, connection UX |
| [DECISIONS.md](docs/DECISIONS.md) | Decision log — why things are the way they are |
| [CONVENTIONS.md](docs/CONVENTIONS.md) | Naming, layering rules, async, error handling |
| [CHANGELOG.md](docs/CHANGELOG.md) | Version history |

---

## Build and run

Requires the **.NET 10 SDK** (Windows).

```bash
dotnet build TecnalHub.slnx
```

```bash
dotnet run --project src/TecnalHub
```

```bash
dotnet test TecnalHub.slnx
```

Release publish, self-contained — the target machine needs no runtime installed:

```bash
dotnet publish src/TecnalHub -c Release
```

---

## Repository layout

```text
ProjetoTECNAL/
├─ docs/                    documentation (start with ROADMAP.md)
├─ src/
│  ├─ TecnalHub.Protocol/   ESP32-S3 wire layer — no WPF, headlessly testable
│  ├─ TecnalHub.Simulator/  localhost HTTP / virtual-serial device simulator
│  ├─ TecnalHub.Harness/    hardware validation and protocol experiments
│  └─ TecnalHub/            the WPF operator application
└─ tests/
   └─ TecnalHub.Tests/      golden wire strings, parser, filters, controllers
```

---

## The one rule

> **The ESP32-S3 firmware is frozen.** Every byte on the wire must match what v.6
> sends. Oddities in [PROTOCOL.md](docs/PROTOCOL.md) — the inverted `v_Flow`, the
> `pHCal` string echo, the USB-only trailing newline — are preserved deliberately.
> Cleanups happen above the wire, never on it.

---

## Related projects

| Project | Relationship |
|---|---|
| `TECNAL_control/.../_Windows App/v.6` | The working Python app being replaced. Reference implementation and fallback. |
| `Artigos/04_Cascata_kLa` | The submitted manuscript defining the kLa gradient-path control method implemented in Phase 2. |
| `Ourofino SA/.../ReceitasTECNAL` | Separate product for a different (PRO/HMI) module. Source of the corrected cascade control design and of the recipe-canvas concept — **not** re-skinned or reused as a product. |
