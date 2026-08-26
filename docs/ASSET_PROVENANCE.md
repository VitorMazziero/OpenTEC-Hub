# Visual asset provenance

## `Imagem_biorreator_side.png`

| Field | Value |
|---|---|
| Purpose | Approved side-view equipment figure for the Painel synoptic |
| Source accepted | 2026-08-26, operator-provided project reference |
| Source path | `docs/UI_design_guides/Bioreactor references/Imagem_biorreator_side.png` |
| Packaged path | linked resource `Resources/Images/Imagem_biorreator_side.png` |
| Dimensions | 1920 × 1920 px |
| Encoding | PNG colour type 6, 8-bit truecolour with alpha |
| File size | 2,951,990 bytes |
| SHA-256 | `B7AEED6AB631AB17F7EA8F834F11A3CDBF145046AAE4931D3957E3E5DB069DF4` |

The approved figure has its own neutral gray background. Painel therefore renders it as
one self-contained image and does not place the former generated PNG, impeller overlay or
vector fallback behind it.

The image is operational scenery, not a fabrication drawing. Its required process
structure was visually checked: double-wall glass jacket, top-drive motor and coupling,
multiple top entries, four process probes, two separated Rushton disc-turbine levels,
and an independent annular ring sparger. The agitator shaft terminates below the lower
turbine and has a visible clearance from the sparger; the sparger has its own wall-side
gas dip tube.

All process facts remain native WPF controls beside the figure: readings, units, state
dots, selected state and hit targets. Tests pin the approved image dimensions and encoding,
the resource reference, and the absence of the superseded figures/fallback in Painel.

### Superseded generated-asset prompt (historical record)

```text
Use case: product-mockup
Asset type: transparent industrial HMI equipment render

Create one mechanically credible compact 5 L laboratory stirred-tank bioreactor as a premium PBR 3D product visualization for the central equipment view of a professional control-room HMI.

COMPOSITION
- Strict orthographic dead-on front elevation, centered vertical axis, tall portrait canvas.
- Show the complete equipment from top motor through bottom drain, with 10–12 percent empty transparent padding on every side.
- One reactor only. No perspective and no cropped fittings.

ACTUAL BIOREACTOR STRUCTURE
- Matte graphite finned electric top-drive motor, short gearbox/coupling, central mechanical seal, and central agitator shaft entry.
- Thick circular stainless-steel headplate secured by realistic clamps/bolts to a compact cylindrical borosilicate vessel.
- Multiple sanitary top-entry ports clearly visible around the headplate, with realistic ferrules, clamps, caps, and seals.
- Four separate slender top-entry process probes extending into the vessel at different plausible depths: pH, dissolved oxygen, temperature, and foam/level. Give their immersed tips subtly different believable shapes. No text or color coding.
- A clearly readable transparent DOUBLE-WALL thermal jacket surrounding the vessel: inner glass process wall plus outer glass jacket wall, with distinct jacket inlet and outlet fittings. Brushed stainless support/lower bowl and bottom drain.

AGITATOR AND SPARGER — MECHANICALLY CRITICAL
- Exactly TWO classic six-blade RUSHTON DISC TURBINES on one central vertical agitator shaft, one in the upper-middle mixing zone and one in the lower-middle mixing zone.
- Each Rushton turbine has a thin HORIZONTAL circular metal disk plus SIX straight, flat, VERTICAL rectangular blades equally spaced around the disk perimeter. The blade faces are vertical and radial. They are not pitched blades, not propellers, not hydrofoils, and not curved paddles. Make the disk edge and turbine hubs visibly distinct.
- The agitator shaft begins at the motor coupling, passes through both turbine hubs, then TERMINATES a short distance immediately below the lower turbine hub.
- Leave an unmistakable open vertical clearance gap below the shaft end.
- Near the vessel bottom, show a separate horizontal perforated ANNULAR RING SPARGER. The sparger is below the lower turbine and physically independent from it.
- Feed the ring sparger from its own slim gas dip tube descending from a separate top-headplate entry near the vessel wall and connecting to the side of the annular ring.
- The shaft must NEVER extend down to, touch, connect to, support, or align as plumbing with the sparger. No central rod or fitting may occupy the open gap beneath the lower turbine.

STYLE AND MATERIALS
- Professional industrial operator-panel quality, technically restrained and photorealistic, not illustration or concept art.
- Brushed/polished stainless steel, clear borosilicate glass, matte graphite motor, precise clamps and fittings.
- Balanced neutral studio illumination designed to remain legible on both an off-white light UI and a charcoal dark UI; controlled highlights, medium-gray contours, crisp glass edges.

BACKGROUND AND STATE
- Output a TRUE RGBA PNG with alpha-zero pixels everywhere outside the equipment silhouette.
- Do not draw or depict a checkerboard. Do not include white, gray, black, colored, gradient, studio, floor, room, wall, glow rectangle, halo, or photographic background. No external cast shadow.
- Dry empty vessel only: no liquid, fill level, foam, bubbles, droplets, condensation, or process tint.
- No text, numbers, labels, arrows, callouts, cards, icons, logos, trademarks, watermark, status lights, colored process states, hoses into the margins, pumps, bottles, gauges, or surrounding equipment.
```

This prompt documents the previous `reactor-neutral.png` generation only. That image and
its vector fallback are no longer displayed or packaged by Painel.
