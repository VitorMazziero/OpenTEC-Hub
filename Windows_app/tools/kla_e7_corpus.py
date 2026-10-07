"""Read-only corpus bundle for the C# E7 audit. Never infer gas events from an OD trough."""
import argparse
import csv
import hashlib
import json
import math
from pathlib import Path

APP = Path(__file__).resolve().parents[1]


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("science", type=Path)
    parser.add_argument("output", type=Path)
    args = parser.parse_args()
    source = args.science / "data/experimental/curated"
    baseline = json.loads((APP / "tests/fixtures/kla-e0/baseline.json").read_text(encoding="utf-8-sig"))
    manifest = source / "curves_manifest.csv"
    expected = baseline["scienceSourceHashes"]["data/experimental/curated/curves_manifest.csv"]
    if hashlib.sha256(manifest.read_bytes()).hexdigest() != expected:
        raise SystemExit("Scientific manifest changed since E0. Review before audit.")
    roles = {r["curveId"]: r for r in json.loads((APP / "tests/fixtures/kla-e0/split-manifest.json").read_text(encoding="utf-8-sig"))}
    with manifest.open(encoding="utf-8-sig", newline="") as handle:
        rows = list(csv.DictReader(handle))
    if len(rows) != 92 or {r["curve_id"] for r in rows} != set(roles):
        raise SystemExit("Corpus IDs differ from the frozen group split.")
    curves = []
    for row in rows:
        path = source / "curves" / (row["curve_id"] + ".csv")
        with path.open(encoding="utf-8-sig", newline="") as handle:
            points = list(csv.DictReader(handle))
        samples = []
        for point in points:
            time = float(point["time_s"])
            od = float(point["do_percent"])
            # NaN is represented as null and restored as an invalid observation in C#.
            samples.append({"Seconds": time if math.isfinite(time) else None,
                            "CalibratedDoPercent": od if math.isfinite(od) else None})
        tau = row["probe_tau_s"]
        tau = float(tau) if tau.strip() else None
        if tau is not None and not math.isfinite(tau):
            tau = None
        curves.append({"CurveId": row["curve_id"], "Biology": row["biology"],
                       "Group": roles[row["curve_id"]]["group"], "Role": roles[row["curve_id"]]["role"],
                       "Sha256": hashlib.sha256(path.read_bytes()).hexdigest(),
                       "ProbeTechnology": row["probe_type"], "ProbeResponseSeconds": tau,
                       "EventProvenance": row["event_provenance"], "Samples": samples})
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(curves, allow_nan=False), encoding="utf-8")
    print(f"Bundled {len(curves)} read-only curves; no gas confirmation invented.")


if __name__ == "__main__":
    main()
