# TECNAL-Hub Documentation

Index of the project documentation. The repository entry point is
[../README.md](../README.md).

| Document | What it covers |
|---|---|
| [ROADMAP.md](ROADMAP.md) | **Start here.** Phases, scope, non-functional targets, deferred work |
| [PROTOCOL.md](PROTOCOL.md) | The frozen ESP32-S3 wire contract |
| [PHASE0_RESULTS.md](PHASE0_RESULTS.md) | Hardware validation results and what the bench taught us |
| [MIGRATION.md](MIGRATION.md) | v.6 module mapping, startup analysis, known defects |
| [ARCHITECTURE.md](ARCHITECTURE.md) | Layers, threading, layout, testing |
| [UI_DESIGN.md](UI_DESIGN.md) | Visual identity, dashboard layout, connection UX |
| [DECISIONS.md](DECISIONS.md) | Decision log, and the open questions |
| [CONVENTIONS.md](CONVENTIONS.md) | Code conventions |
| [CHANGELOG.md](CHANGELOG.md) | Version history |

## Reading paths

**Implementing a phase** → [ROADMAP.md](ROADMAP.md) → [ARCHITECTURE.md](ARCHITECTURE.md) → [CONVENTIONS.md](CONVENTIONS.md)

**Touching anything that reaches the ESP32** → [PROTOCOL.md](PROTOCOL.md), all of it, before writing code

**Building a screen** → [UI_DESIGN.md](UI_DESIGN.md) → the token dictionaries in `src/TecnalHub/Themes/`

**Wondering why something is the way it is** → [DECISIONS.md](DECISIONS.md), then [MIGRATION.md](MIGRATION.md)
