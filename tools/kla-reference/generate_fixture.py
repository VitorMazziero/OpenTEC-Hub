"""Generate an auditable SciPy oracle for OpenTEC-Hub's D-008 implementation.

This script calls the paper analysis functions unchanged. It parallelizes only the
independent candidate rows because the reference 150 x 150 scan is otherwise slow in
CPython. It is a development-time verifier and is not shipped with or called by the app.
"""

from __future__ import annotations

import argparse
import hashlib
import importlib.util
import json
import multiprocessing as mp
import platform
import time
from pathlib import Path

import numpy as np
import scipy


_paper = None
_spline = None


def load_paper(path: str):
    spec = importlib.util.spec_from_file_location("opentec_kla_paper_reference", path)
    if spec is None or spec.loader is None:
        raise RuntimeError(f"Cannot load paper script: {path}")
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def initialize_worker(paper_script: str, profile_name: str) -> None:
    global _paper, _spline
    _paper = load_paper(paper_script)
    _spline, _ = _paper.build_surrogate_model(_paper.KLA_PROFILES[profile_name])


def evaluate_candidate_row(row: int):
    resolution = int(_paper.SEARCH_GRID_RES)
    q_axis = np.linspace(0.0001, 0.9999, resolution)
    n_axis = np.linspace(0.0001, 0.9999, resolution)
    n = n_axis[row]
    scores = np.empty(resolution, dtype=np.float64)
    for column, q in enumerate(q_axis):
        path_q, path_n = _paper.generate_path_from_start(_spline, [q, n])
        scores[column] = _paper.score_path_headroom(path_q, path_n)
    return row, scores


def path_payload(module, spline, start):
    path_q, path_n = module.generate_path_from_start(spline, start)
    kla = spline.ev(path_n, path_q)
    return {
        "start": [float(start[0]), float(start[1])],
        "count": int(len(path_q)),
        "meanHeadroom": float(module.score_path_headroom(path_q, path_n)),
        "points": [
            {
                "q": float(q),
                "n": float(n),
                "klaPerHour": float(value),
            }
            for q, n, value in zip(path_q, path_n, kla)
        ],
    }


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--paper-script", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path)
    parser.add_argument("--profile", default="Serratia marcescens")
    parser.add_argument("--processes", type=int, default=max(1, (mp.cpu_count() or 2) - 2))
    args = parser.parse_args()

    paper_path = args.paper_script.resolve()
    module = load_paper(str(paper_path))
    spline, bounds = module.build_surrogate_model(module.KLA_PROFILES[args.profile])
    resolution = int(module.SEARCH_GRID_RES)
    scores = np.empty((resolution, resolution), dtype=np.float64)

    started = time.perf_counter()
    context = mp.get_context("spawn")
    with context.Pool(
        processes=args.processes,
        initializer=initialize_worker,
        initargs=(str(paper_path), args.profile),
    ) as pool:
        for row, values in pool.imap_unordered(evaluate_candidate_row, range(resolution)):
            scores[row, :] = values
            print(f"row {row + 1:03d}/{resolution}", flush=True)

    best_row, best_column = np.unravel_index(np.argmax(scores), scores.shape)
    axis = np.linspace(0.0001, 0.9999, resolution)
    best_start = [float(axis[best_column]), float(axis[best_row])]
    probes = []
    for q, n in [(0, 0), (.25, .25), (.5, .5), (.75, .75), (1, 1), (.25, .75), (.75, .25)]:
        probes.append({
            "q": q,
            "n": n,
            "value": float(spline.ev(n, q)),
            "dq": float(spline.ev(n, q, dx=0, dy=1)),
            "dn": float(spline.ev(n, q, dx=1, dy=0)),
        })

    elapsed_seconds = time.perf_counter() - started
    fixture = {
        "schemaVersion": 1,
        "generator": {
            "python": platform.python_version(),
            "numpy": np.__version__,
            "scipy": scipy.__version__,
            "paperScript": paper_path.name,
            "paperScriptSha256": hashlib.sha256(paper_path.read_bytes()).hexdigest(),
        },
        "profile": args.profile,
        "anchors": module.KLA_PROFILES[args.profile],
        "physicalBounds": list(map(float, bounds)),
        "settings": {
            "surfaceGridResolution": int(module.DENSE_GRID_RES),
            "gaussianSigmaGridCells": float(module.SMOOTH_SIGMA),
            "candidateGridResolution": resolution,
            "candidateMinimum": 0.0001,
            "candidateMaximum": 0.9999,
            "odeMaximumStep": float(module.ODE_MAX_STEP),
            "odeRelativeTolerance": float(module.ODE_RTOL),
            "odeAbsoluteTolerance": float(module.ODE_ATOL),
            "gradientTermination": float(module.GRAD_MIN),
            "integrationHorizon": 10.0,
        },
        "surfaceProbes": probes,
        "fixedPath": path_payload(module, spline, [.59, .66]),
        "bestPath": path_payload(module, spline, best_start),
        "candidateSearch": {
            "bestRow": int(best_row),
            "bestColumn": int(best_column),
            "maximumHeadroom": float(scores[best_row, best_column]),
            "scoreGridLittleEndianFloat64Sha256": hashlib.sha256(
                scores.astype("<f8", copy=False).tobytes(order="C")
            ).hexdigest(),
        },
    }

    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(
        json.dumps(fixture, indent=2, ensure_ascii=False) + "\n",
        encoding="utf-8",
    )
    print(
        f"wrote {args.output} in {elapsed_seconds:.3f} s with {args.processes} processes",
        flush=True,
    )


if __name__ == "__main__":
    mp.freeze_support()
    main()
