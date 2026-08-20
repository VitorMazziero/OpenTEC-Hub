# kLa mapping and allocation profiles

> Operator and scientific contract for Phase 2 WP5. This page describes fitting and
> publishing only. Live cascade ownership and actuation belong to WP6.
>
> **Related:** [D-008](DECISIONS.md#d-008--kla-mapping-and-path-allocation-are-an-in-app-experimental-workflow) ·
> [Roadmap WP5](ROADMAP.md#wp5--mapeamento-kla-and-path-publication--p1--d-008) ·
> [UI design §5.9](UI_DESIGN.md#59-mapeamento-kla)

---

## 1. Boundary

`Mapeamento kLa` is an experimental workspace, not a library of built-in broths. A clean
installation contains no experiment and no operational profile. The operator supplies the
measured triples

\[
(Q_g\;[\mathrm{L\,min^{-1}}],\;N\;[\mathrm{rpm}],\;k_La\;[\mathrm{h^{-1}}])
\]

and declares the physical actuator domain. Paper datasets exist only under `tests/`; they
are never copied to `%APPDATA%\TECNAL-Hub\kla-mapping` and never appear in the production
selector.

The fitting service has no `IDeviceService` dependency. Estimating, searching, reviewing,
publishing, importing or exporting a profile sends **no command** and does not activate
automatic control.

---

## 2. Reference method

The production-publishable identity reproduces
`04_Cascata_kLa/analysis/1_kla_mapping_gradient/gradient_path_score.py`. It applies these
operations in this order:

1. Normalize physical airflow and agitation independently to `q,n ∈ [0,1]` using the
   declared domain.
2. Delaunay-triangulate the measured coordinates and evaluate a C1 Clough–Tocher cubic
   interpolant on a `300 × 300` normalized grid.
3. Fill nodes outside the convex hull with the nearest measured value.
4. Apply the reference separable Gaussian filter (`sigma=5` grid cells, reflected edge).
5. Fit the zero-smoothing, not-a-knot tensor-product bicubic spline used for continuous
   value and derivative evaluation.
6. Evaluate `∂kLa/∂q` and `∂kLa/∂n` in normalized actuator coordinates. Physical units are
   used only for display and the final allocation table.
7. For each candidate start, integrate the unit gradient in both directions with adaptive
   Dormand–Prince RK45. Stop on the domain boundary or when the gradient magnitude falls
   below the threshold.
8. Score the joined path with the paper's mean boundary clearance:

   \[
   h_i=\min(q_i,1-q_i,n_i,1-n_i),\qquad H=\frac{1}{m}\sum_i h_i.
   \]

9. Select the candidate with maximum `H`, orient its path from low to high kLa, discard
   repeated/decreasing kLa samples from the allocatable relation, and interpolate
   `kLa_requested → (Q_g,N)`. Requests outside the published kLa span clamp to its endpoint.

The mean is deliberately taken over the RK45 output samples, as in the paper script. Its
adaptive initial-step selection, RMS error norm, accepted-step growth rule and dense event
localization are therefore part of the reproduced numerical contract, not implementation
details that may be exchanged silently.

### Reference parameters

| Parameter | Reference value | Meaning |
|---|---:|---|
| Surface grid | `300 × 300` | Clough–Tocher evaluation grid |
| Gaussian sigma | `5` cells | Smoothing after nearest fill |
| Clough–Tocher gradient tolerance | `1e-6` | Global gradient iteration stop |
| Clough–Tocher iteration limit | `400` | Refusal/warning boundary |
| Candidate grid | `150 × 150` | `22,500` possible starts |
| Candidate interval | `[0.0001, 0.9999]` | Normalized `q` and `n` axes |
| RK45 maximum step | `0.05` | Normalized path time |
| RK45 relative / absolute tolerance | `1e-5` / `1e-7` | Adaptive local error |
| Gradient termination | `1e-4` | Low-gradient event |
| Integration horizon | `10` | Each direction |

Every value is visible and editable under **Parâmetros numéricos**. `Prévia rápida` uses a
smaller surface/search grid only to inspect a draft. Any non-reference value changes the
algorithm identity to **Método parametrizado** and disables review/publication. `Referência`
restores all values together.

---

## 3. Operator procedure

1. Open **Mapeamento kLa** (`Ctrl+8`) and create a named experiment.
2. Record broth/run metadata and the actual minimum/maximum airflow and agitation used in
   the experiment.
3. Add measured rows manually or choose **Criar desenho 3² vazio**. The helper fills only
   the nine coordinates; it never inserts paper kLa values.
4. Save at any time. Blank or temporarily invalid cells are preserved exactly as draft
   text, but cannot enter a numerical snapshot.
5. Complete all values and choose **Estimar superfície**. Review range, measured-point
   residuals, convex-hull coverage, nearest-filled node count, warnings and fingerprint.
6. Choose **Calcular trajetória**. The full search runs off the UI thread, reports row/start/
   headroom progress, accepts cancellation, and discards a result if its input fingerprint
   became stale while it ran.
7. Review both plots: the physical-domain surface with measured anchors, normalized
   gradient arrows and selected path; and the headroom landscape with its selected start.
   Confirm the displayed kLa span and allocation-point count.
8. Enter a review/version note and choose **Marcar revisada**.
9. Choose **Publicar perfil**. Publication creates a new immutable receipt version. It
   still does not activate or command the cascade.

Changing an anchor, domain bound or numerical parameter invalidates the surface, path and
review immediately. Metadata changes preserve current scientific arrays but remove the
review gate when necessary, because the next receipt would have different context.

---

## 4. Refusals and warnings

Calculation is refused for fewer than six complete points, non-finite/negative kLa,
duplicate coordinates, points outside the declared domain, invalid units/bounds, collinear
coverage or invalid numerical parameters. Review/publication also requires a non-blank
version note. A regular `3²` design is recommended, not loaded.

The diagnostics distinguish warnings from publication blockers:

- incomplete convex-hull coverage reports its percentage and exact nearest-filled node
  count;
- failure of the global Clough–Tocher gradient iteration to converge is visible;
- a negative smoothed surface blocks publication;
- a degenerate/zero-gradient path or fewer than two strictly increasing allocation samples
  blocks the path stage;
- custom/preview parameters may calculate but cannot be reviewed as the paper method.

---

## 5. Drafts, receipts and import

Mutable drafts live in `%APPDATA%\TECNAL-Hub\kla-mapping\experiments.json`. The raw table
text is stored beside the complete numerical snapshot so a blank 3² worksheet survives a
restart. Computed grids are rebuilt rather than trusted as mutable cached state.

Publication receipts live under `kla-mapping\receipts\<sha256>.kla.json` and contain:

- experiment/profile identifiers and monotonically increasing version;
- timestamp, metadata, review note, physical domain and every measured anchor;
- complete algorithm parameters and human-readable algorithm identity;
- surface/path fingerprints and diagnostics;
- the monotonic kLa-to-`(Q_g,N)` allocation samples.

The filename and embedded SHA-256 are verified on load/read/export. Export copies the stored
bytes exactly. Import verifies integrity, assigns a new local experiment id, and always
returns to **Rascunho**; a foreign review never becomes a locally active profile silently.

WP6 may consume only a separately selected published allocation receipt. It cannot edit or
refit it, and it must still acquire `Automatic` actuator ownership from WP4 before sending.

---

## 6. Scientific verification

`tools/kla-reference/generate_fixture.py` calls the paper functions unchanged and records
the Python, NumPy and SciPy versions, paper-script SHA-256, surface probes, fixed path, full
candidate winner, score-grid hash and allocation points. Candidate rows are parallelized
only for verification speed; their original order is restored before selection.

The managed tests currently enforce these maximum absolute differences against that oracle:

| Quantity | Tolerance |
|---|---:|
| Surface value | `0.25 h⁻¹` |
| Normalized `∂kLa/∂q`, `∂kLa/∂n` | `2.0 h⁻¹` |
| Fixed-path endpoint `q,n` | `0.015` |
| Fixed-path endpoint kLa | `2.0 h⁻¹` |
| Fixed-path mean headroom | `0.004` |
| Reduced-grid candidate start | `0.001` |
| Reduced-grid maximum mean headroom | `0.003` |
| Full 150² winner `q,n,H` | `1e-10` |
| Full-path endpoint `q,n` / kLa | `1e-9` / `1e-8 h⁻¹` |

These are cross-language numerical tolerances, not biological validation. The paper fixture
proves implementation parity for a known dataset; each new broth still needs its own measured
anchors, review and bioreactor validation.

The pinned oracle was generated with Python 3.12.7, NumPy 2.4.3 and SciPy 1.17.1 from paper
script SHA-256 `a230fceeadfb08ba31b51c6c735fe70e5ff1e8fa0466b642ba2c0b23431385bf`.
Its full winner is `q=0.597295973154362`, `n=0.630846308724832`,
`H=0.202430019755805`; the managed result agrees within the table above.
