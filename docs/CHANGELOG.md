# Changelog

All notable changes to TECNAL-Hub. Version numbers follow
[Semantic Versioning](https://semver.org/); the single source for the number is
`<Version>` in `Directory.Build.props`.

---

## [0.1.0] — 2026-08-19

Project scaffold and plan. No application features yet.

### Added
- Solution scaffold: `TecnalHub.Protocol` (net10.0, no WPF) + `TecnalHub`
  (net10.0-windows, WPF) + `TecnalHub.Tests`. Builds clean with 0 warnings.
- Build configuration: `Directory.Build.props` (single-source version, analyzers,
  language standards), `global.json` (SDK pin), `.editorconfig` (naming rules
  enforced at build).
- Fluent design tokens for light and dark themes, plus the shared brush, typography
  and geometry scales.
- Documentation set: [ROADMAP](ROADMAP.md), [PROTOCOL](PROTOCOL.md),
  [MIGRATION](MIGRATION.md), [ARCHITECTURE](ARCHITECTURE.md), [UI_DESIGN](UI_DESIGN.md),
  [DECISIONS](DECISIONS.md), [CONVENTIONS](CONVENTIONS.md).

### Decided
- .NET 10 LTS, self-contained publish ([D-003](DECISIONS.md#d-003--net-10-lts-self-contained)).
- Fluent light + dark following the system theme ([D-004](DECISIONS.md)).
- Dashboard as synoptic + detail pane + KPI strip ([D-005](DECISIONS.md)).
- Auto-connect with a status chip; Configurations stops being a destination ([D-006](DECISIONS.md)).
- pt-BR UI, English code ([D-007](DECISIONS.md)).
- Recipes as a node canvas, new implementation and new identity ([D-009](DECISIONS.md)).
- kLa gassing-out and torch leave the controller app ([D-010](DECISIONS.md)).

### Documented from the v.6 source
- The complete ESP32-S3 wire contract: both transports, all telemetry keys, ~60
  command keys, signal conditioning, and the timing constants that are load-bearing.
- Startup cost analysis: `import torch`, and serial probing that can exceed 10 s per
  unresponsive port with no way to cancel it.
- 11 defects found while reading v.6, each with a disposition.
