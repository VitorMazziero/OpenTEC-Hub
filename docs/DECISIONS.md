# Decision Log

> One entry per decision that would otherwise be re-litigated in three months.
> Newest last. A decision is only "Open" if it genuinely blocks work.
>
> **Docs:** [README](README.md) · [Roadmap](ROADMAP.md) · [Architecture](ARCHITECTURE.md) · [UI Design](UI_DESIGN.md)

---

### D-001 · Rebuild in C# / WPF rather than fix the Python app
**Status:** Accepted · 2026-08-19

v.6 works. The rebuild is justified by cold-start time, a UI that does not scale to
12 subsystems, and a 1086-line `main.py` whose preference marshalling makes changes
unsafe. WPF also gives a genuinely native Windows result, which is a stated goal.

*Consequence:* v.6 stays installed and is the reference implementation until
TECNAL-Hub has completed a full cultivation.

---

### D-002 · The ESP32-S3 firmware and wire protocol are frozen
**Status:** Accepted · 2026-08-19

The board is not being reflashed. The wire format is therefore a fixed contract,
documented in [PROTOCOL.md](PROTOCOL.md) and enforced by golden-string tests.

*Consequence:* oddities on the wire (`v_Flow` inversion, the `pHCal` string echo,
the USB-only trailing newline) are **preserved verbatim**. Cleanups happen above the
wire only.

---

### D-003 · .NET 10 LTS, self-contained
**Status:** Accepted · 2026-08-19

.NET 10 is supported to Nov 2028; .NET 8 LTS ends Nov 2026 — about three months from
today, which would mean shipping onto an already-expiring runtime. Self-contained
publish plus an installer means lab PCs need nothing preinstalled.

*Rejected:* .NET 8 (matches ReceitasTECNAL, but expiring). Framework-dependent
(smaller download, but a runtime prerequisite on every lab machine).

---

### D-004 · Fluent light + dark, following the system theme
**Status:** Accepted · 2026-08-19

Gives the "integrated Windows application" feel that was the goal. Colour is reserved
for equipment state; chrome stays greyscale. See [UI_DESIGN.md](UI_DESIGN.md).

*Rejected:* carrying over the ReceitasTECNAL navy palette — it is that product's
identity, and this is a different product.

---

### D-005 · Dashboard is a synoptic + detail pane + KPI strip ("2+1 hybrid")
**Status:** Accepted · 2026-08-19

The synoptic answers "what is the equipment doing" with physical context; the detail
pane answers "change this one parameter" without putting 12 subsystems on screen at
once; the KPI strip keeps the safety glance available from every page.

*Rejected:* pure synoptic (clutters as soon as controls land on the diagram); pure
instrument rail (loses the physical relationship between variables); KPI strip + tabs
alone (still reads as a software dashboard rather than a reactor interface).

---

### D-006 · Auto-connect with a status chip; Configurations stops being a destination
**Status:** Accepted · 2026-08-19

Connecting was the only reason most users opened the v.6 Configurations window.
The app now restores the last link and connects in the background after the shell is
visible. Advanced Settings keeps every existing option, out of the way.

---

### D-007 · pt-BR UI, English code
**Status:** Accepted · 2026-08-19

All identifiers, comments, logs and commit messages in English; all user-facing text
in pt-BR, in `.resx` files. Diverges from the ReceitasTECNAL mixed convention.

*Rationale:* the work is attached to an international manuscript and may be read by
collaborators outside Brazil; English code stays readable to any C# developer, while
the operators keep a Portuguese interface.

---

### D-008 · kLa surface evaluation — embed a pre-computed surface
**Status:** Proposed · needs confirmation before Phase 2

The manuscript pipeline fits kLa over agitation x aeration with a bicubic B-spline in
Python/scipy. The app does not need to *fit* — only to **evaluate** the surface and
its gradient.

*Proposal:* export the fitted surface (control points or a dense grid) as a data file
shipped with the app; implement evaluation + gradient in C#. Fitting stays an offline
research activity in the manuscript repository.

*Rejected:* implementing B-spline fitting in C# (duplicates validated research code);
a Python sidecar (reintroduces the runtime dependency this rebuild is removing).

*Open:* which export format, and whether one surface per broth is shipped or loaded
by the user.

---

### D-009 · Recipes — node canvas, new implementation, new identity
**Status:** Accepted · 2026-08-19

The canvas concept and the engine architecture from ReceitasTECNAL carry over; the
UI, visual language and product identity do not. TECNAL-Hub is a distinct product,
not a re-skin.

Improvements to make while re-implementing rather than copying forward:
- Node definitions declared once and generated, instead of a hand-written
  model + viewmodel + view triple per node type.
- The recipe engine drives the **same command queue** as manual control, so a running
  recipe and an operator cannot fight over the link.
- Recipe JSON versioned from v1 with a migration hook.

*Scope:* Phase 3. Not before the cascade control is validated.

---

### D-010 · kLa gassing-out and torch leave the controller app
**Status:** Accepted · 2026-08-19

`import torch` is the largest single contributor to v.6's cold start, and neural
inference is not something a control loop should carry. The gassing-out estimator
becomes its own project.

*Consequence:* the `.pth` model, the gassing-out page and its scipy dependencies are
not ported. A plan for the standalone module is written separately.

---

### D-011 · The KPI strip belongs to Receitas, not to every page
**Status:** Accepted · 2026-08-20

WP4 delivered the strip and the variable rail together, and Painel ended up carrying
**two viewers of the same six variables** — the horizontal strip on top and the rail on
the left, showing identical values a few hundred pixels apart.

*Decision:* the KPI strip appears **only on Receitas**. On process and record pages the
variable rail is the variable display and is no longer optional. **Controle is the one
exception:** its all-setpoints table already carries every PV, so the rail is hidden there
to keep the complete configuration and action columns visible at 1280 px.

*Rationale:* the rail keeps its meaning next to the synoptic, where a value sits beside
the drawing of the thing that produced it. On Receitas the strip earns its place for the
opposite reason — the canvas there describes the *intended* process, and the strip is the
only thing on screen showing the *actual* one. That contrast is the whole point of
[UI_DESIGN.md](UI_DESIGN.md#5315-process-versus-recipe).

*Consequence:* reverses "KPI strip visible on every page" from the original §4.4. The
strip is kept as `Views/KpiStripView.xaml` — built, tested, and unused until Phase 3
places it on Receitas. The shell now binds the rail's actual visibility to its responsive
state; before WP6 that property existed but the rail border ignored it.

---

### D-012 · The synoptic becomes a photorealistic render with editable anchored cards
**Status:** Accepted · 2026-08-20 · **implementation deferred, see [UI_DESIGN.md §5.1.1](UI_DESIGN.md#511-the-reactor-image)**

The flat vector drawing reads as a diagram of a tank rather than as the machine in the
room. Reference for the target: `docs/UI_design_guides/Bioreactor Panel.png`.

*Decision:* the reactor becomes a high-resolution render, with callout cards anchored to
the physical ports by leader lines. **Those cards are the primary way an operator edits a
setpoint** — not read-only labels.

*Rationale:* the reactor drawing is what makes this a bioreactor application rather than a
dashboard. Anchoring the control to the physical port also teaches the process: the
operator learns where the pH probe enters the vessel while adjusting it.

*Constraint:* nothing that changes at 1 Hz may be baked into the image. The render is a
static asset; values, state colours, leader lines and hit targets stay as WPF overlays on
top of it.

---

## Open questions

| # | Question | Blocks |
|---|---|---|
| D-008 | kLa surface export format | Phase 2 |
| [PROTOCOL.md](PROTOCOL.md#5-open-questions-for-hardware-verification) Q1-Q5 | Firmware behaviours that could not be confirmed from Python source | Phase 0 exit |
| — | Does the nitrogen-enrichment path change the wire protocol, or only the control law? | Phase 2 |
| — | Is there a second bioreactor module to support (`HubStations`)? | Phase 3+ |
