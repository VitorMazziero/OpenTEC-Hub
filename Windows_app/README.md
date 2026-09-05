# OpenTEC-Hub

Windows control application for the OpenTEC bioreactor module, communicating with an
**ESP32-S3** hub over USB or Wi-Fi.

C# / WPF on .NET 10, rebuilding the working Python application at
`_Wifi Hub/Software/_Windows App/v.6` — faster to start, with a UI that scales to the
full instrument, and without ever changing the firmware protocol.

> **Status:** v0.24.0. The application core now includes the Phase 1/1b shell, manual control,
> safety/alarm kernel, kLa mapping, live oxygen cascade, conditional OUR and gain scheduling,
> cultivation auxiliaries, biomass, the external pump and the Receitas editor/engine. The
> 2026-08-26 post-merge audit classifies it as **feature-complete but not field-release-ready**:
> active-recipe safe-stop and ownership-feedback defects must be fixed before the planned
> v0.25.0 stabilization/UI-polish release. Real-hardware parity, a full cultivation, packaging
> and operator validation remain open. v.6 stays installed as the production fallback. See
> [CURRENT_STATUS.md](docs/CURRENT_STATUS.md) for evidence, findings and the release plan.

---

## Documentation

Read in this order:

| Document | What it covers |
|---|---|
| [docs/README.md](docs/README.md) | **Índice completo de documentação.** Navegação por categorias e subsistemas |
| [CURRENT_STATUS.md](docs/CURRENT_STATUS.md) | **Current audited state.** Release posture, defects, verification evidence and the v0.25.0 stabilization plan |
| [ROADMAP.md](docs/ROADMAP.md) | **Start here.** Phases, scope per phase, non-functional targets, what is deferred |
| [ARCHITECTURE.md](docs/ARCHITECTURE.md) | Layers, threading model, directory layout, testing strategy |
| [PROTOCOL.md](docs/PROTOCOL.md) | The frozen ESP32-S3 wire contract. Every key, unit, range and timing constant |
| [DECISIONS.md](docs/DECISIONS.md) | Decision log — why things are the way they are (ADRs) |
| [UI_DESIGN.md](docs/UI_DESIGN.md) | Visual identity, the synoptic + detail layout, connection UX |
| [CONVENTIONS.md](docs/CONVENTIONS.md) | Naming, layering rules, async, error handling |
| [CHANGELOG.md](docs/CHANGELOG.md) | Version history |
| [CALIBRATION.md](docs/processes/CALIBRATION.md) | Calibration ownership, pH/O₂/airflow procedures, interlocks and refusal states |
| [KLA_MAPPING.md](docs/processes/KLA_MAPPING.md) | kLa experiment, reference algorithm/parameters, headroom path, refusals and receipts |
| [PHASE0_RESULTS.md](docs/hardware/PHASE0_RESULTS.md) | Hardware validation results — measured, on a real board |
| [PHASE_LOG.md](docs/history/PHASE_LOG.md) | Decisions taken while executing each phase, with evidence |
| [MIGRATION.md](docs/history/MIGRATION.md) | What each v.6 module becomes, why startup is slow, and 11 known defects found in the source |
| [ASSET_PROVENANCE.md](docs/governance/ASSET_PROVENANCE.md) | Generated reactor prompt, structure, alpha contract and hash |
| [THIRD_PARTY_NOTICES.md](docs/governance/THIRD_PARTY_NOTICES.md) | Notices for source-derived third-party algorithms |

---

## Build and run

Requires the **.NET 10 SDK** (Windows).

```bash
dotnet build OpenTECHub.slnx
```

```bash
dotnet run --project src/OpenTECHub
```

Para iniciar sem abrir o seletor de pasta do Windows, informe o workspace explicitamente:

```bash
dotnet run --project src/OpenTECHub -- --workspace "C:\Users\usuario\Documents\OpenTEC-Hub"
```

`--no-workspace-prompt` reutiliza o workspace configurado ou o diretório padrão.

```bash
dotnet test OpenTECHub.slnx
```

Release publish, self-contained — the target machine needs no runtime installed:

```bash
dotnet publish src/OpenTECHub -c Release
```

---

## Repository layout

```text
ProjetoOpenTEC/
├─ docs/                    documentation (start with ROADMAP.md)
├─ tools/                   development-time hardware/scientific verification
├─ src/
│  ├─ OpenTECHub.Protocol/   ESP32-S3 wire layer — no WPF, headlessly testable
│  ├─ OpenTECHub.Simulator/  localhost HTTP / virtual-serial device simulator
│  ├─ OpenTECHub.Harness/    hardware validation and protocol experiments
│  └─ OpenTECHub/            the WPF operator application
└─ tests/
   └─ OpenTECHub.Tests/      golden wire strings, parser, filters, controllers
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
| `OpenTEC_control/.../_Windows App/v.6` | The working Python app being replaced. Reference implementation and fallback. |
| `Artigos/04_Cascata_kLa` | The submitted manuscript defining the kLa gradient-path control method implemented in Phase 2. |
| `Ourofino SA/.../ReceitasOpenTEC` | Separate product for a different (PRO/HMI) module. Source of the corrected cascade control design and of the recipe-canvas concept — **not** re-skinned or reused as a product. |
