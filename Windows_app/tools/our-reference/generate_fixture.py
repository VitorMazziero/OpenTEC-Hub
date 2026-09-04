"""Conditional-OUR cross-language fixture generator (development only).

Extracts a small numerical oracle from the manuscript's already-computed conditional-OUR
analysis (``analysis/2_our_soft_sensor``) so that ``OurSoftSensorScientificTests`` can pin the
C# soft sensor against the paper's own numbers without making Python a production dependency.

It records, for a set of evenly-spaced rows of the processed time series:

* ``kla_h_inv``, ``dot_smooth_percent`` and the paper's ``our_mmol_l_h`` — to check the OUR
  inversion ``OUR = kLa * C* * (1 - DOT/100)`` reproduces the paper value exactly;
* ``raw_dot_percent``, ``dot_rate_pp_h``, ``post_gate`` and ``full_post_gate_stable`` — to check
  the quasi-steady acceptance predicate reproduces the paper mask.

The centred Savitzky-Golay smoothing/derivative themselves are the paper's offline preprocessing
and are taken as given inputs here; the live sensor recomputes the rate causally, which is covered
by the unit tests, not by this parity fixture.

Usage::

    python tools/our-reference/generate_fixture.py \
      --paper-output "D:/.../04_Cascata_kLa/analysis/output" \
      --output tests/fixtures/our-reference.json \
      --rows 40
"""

from __future__ import annotations

import argparse
import json
from pathlib import Path

import numpy as np
import pandas as pd


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--paper-output", type=Path, required=True,
                        help="The manuscript analysis/output directory.")
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--rows", type=int, default=40)
    parser.add_argument("--stem", default="Bacillus")
    return parser.parse_args()


def main() -> None:
    args = parse_args()
    out_dir = args.paper_output
    stem = args.stem

    metadata = json.loads((out_dir / f"{stem}_conditional_our_metadata.json").read_text("utf-8"))
    config = metadata["configuration"]
    summary = pd.read_csv(out_dir / f"{stem}_conditional_our_summary.csv")

    def metric(name: str) -> float:
        return float(summary.loc[summary["metric"] == name, "value"].iloc[0])

    frame = pd.read_csv(out_dir / f"{stem}_conditional_our_timeseries.csv.gz")

    indices = np.linspace(0, len(frame) - 1, args.rows).round().astype(int)
    rows = []
    for i in indices:
        r = frame.iloc[int(i)]
        rows.append({
            "time_h": float(r["Time (h)"]),
            "raw_dot_percent": float(r["OD(%)"]),
            "dot_smooth_percent": float(r["DOT_smooth_percent"]),
            "dot_rate_pp_h": float(r["dDOT_dt_pp_h"]),
            "kla_h_inv": float(r["kLa_h_inv"]),
            "our_mmol_l_h": float(r["OUR_mmol_L_h"]),
            "post_gate": bool(r["post_gate"]),
            "full_post_gate_stable": bool(r["full_post_gate_stable"]),
        })

    fixture = {
        "source": {
            "analysis": metadata["analysis"],
            "our_equation": metadata["implementation"]["our_equation"],
            "acceptance_rule": metadata["implementation"]["acceptance_rule"],
        },
        "config": {
            "oxygen_saturation_mmol_l": config["oxygen_saturation_mmol_l"],
            "dot_setpoint_percent": config["dot_setpoint_percent"],
            "stable_tolerance_pp": config["stable_tolerance_pp"],
            "stable_rate_limit_pp_h": config["stable_rate_limit_pp_h"],
            "gate_tolerance_pp": config["gate_tolerance_pp"],
        },
        "documented_summary": {
            "mean_conditional_our_mmol_l_h": metric("mean_conditional_our_mmol_l_h"),
            "max_conditional_our_mmol_l_h": metric("max_conditional_our_mmol_l_h"),
            "cumulative_inferred_oxygen_uptake_accepted_mmol_l":
                metric("cumulative_inferred_oxygen_uptake_accepted_mmol_l"),
            "accepted_duration_h": metric("accepted_duration_h"),
            "evaluation_gate_time_h": metric("evaluation_gate_time_h"),
        },
        "rows": rows,
    }

    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(fixture, indent=2), encoding="utf-8")
    print(f"Wrote {len(rows)} rows to {args.output}")


if __name__ == "__main__":
    main()
