"""Freeze actual local Python component I/O; never use acquisition folders as truth."""
import argparse
import hashlib
import json
import math
import platform
import subprocess
import sys
from pathlib import Path


def clean(value):
    if isinstance(value, dict):
        return {k: clean(v) for k, v in value.items()}
    if isinstance(value, (list, tuple)):
        return [clean(v) for v in value]
    if isinstance(value, float) and not math.isfinite(value):
        return None
    return value


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--source", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    if args.output.exists():
        parser.error("Frozen reference already exists; choose a new version instead of overwriting.")
    sys.path.insert(0, str(args.source / "src"))
    import numpy as np
    import scipy
    from klacore.estimation.kernel import Window, fit_log_window
    from klacore.estimation.equilibrium import EquilibriumConfig, estimate_equilibrium
    from klacore.estimation.rates import estimate_our
    from klacore.phases import GAS_ON_RECOVERY

    cases = []
    for name, ceq, offset, noise in [("abiotic", 100, 0, 0),
                                    ("biotic", 75, 0, 0),
                                    ("shifted", 100, 123, 0),
                                    ("noisy", 75, 0, .04)]:
        t = np.arange(0, 202, 2, dtype=float) + offset
        y = ceq - (ceq - 10) * np.exp(-.02 * (t - offset))
        if noise:
            y += noise * np.sin(np.arange(t.size) * 2.37)
        window = Window(5, 60)
        kernel = fit_log_window(t, y, ceq, window)
        config = EquilibriumConfig(smoothing_window=0, weighting="linear_ramp", weight_ratio=5)
        eq = estimate_equilibrium(t, y, np.full(t.size, GAS_ON_RECOVERY), config=config)
        cases.append(dict(id=name, time=t.tolist(), oxygen=y.tolist(), ceq=ceq,
                          start=window.start, end=window.end, kernel=kernel.to_dict(),
                          equilibrium=eq.to_dict()))
    t = np.arange(0, 102, 2, dtype=float)
    y = 80 - .2 * t
    our_window = Window(5, 40)
    our_cases = []
    for mode in ["respiration", "nitrogen_stripping"]:
        our_cases.append(dict(mode=mode, time=t.tolist(), oxygen=y.tolist(),
                              start=our_window.start, end=our_window.end,
                              output=estimate_our(t, y, our_window, gas_off_mode=mode).to_dict()))
    paths = ["src/klacore/estimation/" + n + ".py" for n in
             ["kernel", "rates", "equilibrium", "_utils", "window"]]
    result = dict(schemaVersion=1, generator=dict(python=platform.python_version(),
                  numpy=np.__version__, scipy=scipy.__version__),
                  sourceCommit=subprocess.check_output(["git", "-C", str(args.source), "rev-parse", "HEAD"], text=True).strip(),
                  sourceHashes={p: hashlib.sha256((args.source / p).read_bytes()).hexdigest() for p in paths},
                  scope="Component parity: raw OLS, respiratory OUR, raw exponential equilibrium; phase/window selector is an explicitly versioned C# adaptation.",
                  equilibriumConfig=dict(smoothingWindow=0, weighting="linear_ramp", weightRatio=5),
                  cases=cases, ourCases=our_cases)
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(clean(result), indent=2, allow_nan=False) + "\n", encoding="utf-8")
    print(f"Frozen {len(cases)} recovery and {len(our_cases)} uptake component references.")


if __name__ == "__main__":
    main()
