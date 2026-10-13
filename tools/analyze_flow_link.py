"""Read-only snapshot of culture logs; a negative flow is not proof of RF loss."""
import argparse
import csv
from decimal import Decimal, ROUND_HALF_UP
import hashlib
import io
import json
from pathlib import Path
import statistics


def analyze(base):
    result = {"source": str(base), "sessions": [], "command_logs": [],
              "interpretation": "Flowmeter < 0 denotes unavailable/invalid flow; saved columns cannot distinguish Wi-Fi, HTTP or hardware faults."}
    for path in sorted((base / "Sessoes").glob("Ensaio_*.txt")):
        content = path.read_bytes()
        rows = list(csv.DictReader(io.StringIO(content.decode("utf-8-sig")), delimiter="\t"))
        episodes = []
        start = None
        count = 0
        last = None
        valid_rows = 0
        for row in rows:
            try:
                time = float(row["Time (min)"]) * 60
                flow = float(row["Flowmeter"])
            except (ValueError, TypeError, KeyError):
                continue  # tolerate a partially appended final row
            valid_rows += 1
            last = time
            if flow < 0:
                count += 1
                if start is None:
                    start = time
            elif start is not None:
                episodes.append({"start_session_s": start, "recovery_session_s": time,
                                 "sampled_duration_s": round(time - start, 3)})
                start = None
        if start is not None:
            episodes.append({"start_session_s": start, "recovery_session_s": None,
                             "sampled_duration_s": round(last - start, 3)})
        durations = [e["sampled_duration_s"] for e in episodes]
        result["sessions"].append({"file": path.name, "snapshot_bytes": len(content),
            "snapshot_sha256": hashlib.sha256(content).hexdigest(), "rows": valid_rows,
            "negative_flow_rows": count, "episodes": len(episodes),
            "median_sampled_duration_s": statistics.median(durations) if durations else 0,
            "max_sampled_duration_s": max(durations, default=0), "episode_intervals": episodes})
    for path in sorted((base / "Logs").glob("*.log")):
        content = path.read_bytes()
        previous = None
        sent = identical = same_step = 0
        for line in content.decode("utf-8-sig", errors="replace").splitlines():
            if " TX {" not in line:
                continue
            try:
                command = json.loads(line.split(" TX ", 1)[1])
            except ValueError:
                continue
            if "flowSetpoint" not in command:
                continue
            sent += 1
            value = Decimal(str(command["flowSetpoint"]))
            step = (value / Decimal("0.05")).quantize(Decimal("1"), rounding=ROUND_HALF_UP)
            if previous is not None:
                identical += value == previous[0]
                same_step += step == previous[1]
            previous = (value, step)
        result["command_logs"].append({"file": path.name, "snapshot_bytes": len(content),
            "snapshot_sha256": hashlib.sha256(content).hexdigest(), "flow_commands": sent,
            "same_flow_as_previous": identical, "same_005_step_as_previous": same_step})
    return result


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("culture", type=Path)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    report = analyze(args.culture)
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
    for session in report["sessions"]:
        print(session["file"], {key: session[key] for key in ("rows", "negative_flow_rows",
            "episodes", "median_sampled_duration_s", "max_sampled_duration_s")})
    for log in report["command_logs"]:
        print(log["file"], {key: log[key] for key in ("flow_commands", "same_flow_as_previous",
                                                  "same_005_step_as_previous")})
