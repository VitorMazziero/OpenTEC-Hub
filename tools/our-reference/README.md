# Conditional-OUR cross-language fixture generator

This development-only tool extracts a small numerical oracle from the manuscript's already-computed
conditional-OUR analysis (`analysis/2_our_soft_sensor`) and records it for
`OurSoftSensorScientificTests`. It is not called by the application and does not make Python a
production dependency.

```powershell
python tools\our-reference\generate_fixture.py `
  --paper-output "D:\path\to\04_Cascata_kLa\analysis\output" `
  --output tests\fixtures\our-reference.json `
  --rows 40
```

It reads the paper's `Bacillus_conditional_our_timeseries.csv.gz`, `_summary.csv` and
`_metadata.json` and records, for evenly-spaced rows:

- `kla_h_inv`, `dot_smooth_percent`, `our_mmol_l_h` — to pin the OUR inversion
  `OUR = kLa · C* · (1 − DOT/100)` against the paper value;
- `raw_dot_percent`, `dot_rate_pp_h`, `post_gate`, `full_post_gate_stable` — to pin the quasi-steady
  acceptance predicate against the paper mask.

The centred Savitzky-Golay smoothing and derivative are the paper's offline preprocessing and are
taken as **given inputs**: the live sensor recomputes the rate causally with a least-squares slope,
which the unit tests and the live run cover, not this parity fixture. Only `pandas`/`numpy` are
needed (no SciPy) because the analysis has already been run — the fixture reads its output.
