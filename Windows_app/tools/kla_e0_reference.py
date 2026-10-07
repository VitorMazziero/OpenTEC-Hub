"""Capture or verify E0 references; standard library only, no actuator access."""
import argparse
import csv
import hashlib
import json
import math
import shutil
import subprocess
from datetime import datetime, timezone
from pathlib import Path

APP = Path(__file__).resolve().parents[1]
ROOT = APP.parent
DEST = APP / "tests/fixtures/kla-e0"


def digest(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def git(repo, *args):
    return subprocess.check_output(["git", "-C", str(repo), *args], text=True).strip()


def write_json(path, value):
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(value, indent=2, ensure_ascii=False, allow_nan=False) + "\n", encoding="utf-8")


def inventory(repo, paths):
    return {p: digest(repo / p) for p in paths}


def capture(science, culture):
    if (DEST / "baseline.json").exists():
        raise SystemExit("Frozen baseline already exists. Create a new version instead of overwriting it.")
    DEST.mkdir(parents=True, exist_ok=True)
    app_sources = [
        "src/OpenTECHub/Services/KlaTesting/KlaAnalysisEngine.cs",
        "src/OpenTECHub/Services/KlaTesting/KlaTestModels.cs",
        "src/OpenTECHub/Services/KlaTesting/KlaTestFileContracts.cs",
        "src/OpenTECHub/Services/KlaTesting/KlaTestStore.cs",
        "src/OpenTECHub/Services/KlaTesting/KlaTestRunner.cs",
        "src/OpenTECHub/Services/Control/OurSoftSensor.cs",
        "src/OpenTECHub/Services/Control/OurSoftSensorService.cs",
        "src/OpenTECHub.Protocol/TelemetryParser.cs",
        "src/OpenTECHub.Protocol/SensorReadings.cs", "Directory.Build.props",
    ]
    science_sources = ["PROJECT_STATE.md", "docs/decisions.md", "src/klacore/phases.py",
        "data/experimental/curated/curves_manifest.csv",
        "data/experimental/curated/LEIA-ME_conjunto_de_curvas.md"]
    science_sources += [f"src/klacore/estimation/{p}.py" for p in ("core", "equilibrium", "kernel", "rates", "window")]
    for method in ("torres2017", "cerri2016", "aroniada2019", "damiani2014", "brownstenstrom1980"):
        science_sources += [f"src/comparadores/{method}/{method}.py", f"src/comparadores/{method}/LEIA-ME_{method}.md"]
    baseline = {
        "contractVersion": "OpenTecDeterministicKlaV1-E0.1",
        "capturedUtc": datetime.now(timezone.utc).isoformat(),
        "applicationCommit": git(ROOT, "rev-parse", "HEAD"),
        "applicationDescribe": git(ROOT, "describe", "--tags", "--always", "--dirty"),
        "applicationSourceHashes": inventory(APP, app_sources),
        "scienceCommit": git(science, "rev-parse", "HEAD"),
        "scienceWorkingTreeStatus": git(science, "status", "--porcelain"),
        "scienceSourceHashes": inventory(science, science_sources),
        "runtime": subprocess.check_output(["dotnet", "--version"], text=True).strip(),
        "scope": "References only; not a biotic release or independent scientific validation",
    }
    # Preserve the exact pre-E0 worktree, including uncommitted calibration changes.
    baseline["preexistingWorkingTreeStatus"] = git(ROOT, "status", "--porcelain")
    dirty_paths = git(ROOT, "diff", "--name-only").splitlines()
    dirty_paths += git(ROOT, "ls-files", "--others", "--exclude-standard").splitlines()
    baseline["preservedWorkingFileHashes"] = {
        p: digest(ROOT / p) for p in sorted(set(dirty_paths))
        if (ROOT / p).is_file() and not p.startswith("Windows_app/tests/fixtures/kla-e0/")
        and p != "Windows_app/tools/kla_e0_reference.py"
    }

    legacy = APP / "OpenTEC-Hub/Testes-kLa/Ensaio Alivio Parada"
    for source in sorted(legacy.rglob("*")):
        if source.is_file():
            target = DEST / "legacy-v1" / source.relative_to(legacy)
            target.parent.mkdir(parents=True, exist_ok=True)
            shutil.copyfile(source, target)
    baseline["legacyV1Origin"] = str(legacy.relative_to(APP)).replace("\\", "/")
    baseline["legacyV2Origin"] = "synthetic contract fixture; no real v2 assay was found in the inspected application/culture folders"

    header = "TimestampUtc,RelativeSeconds,Phase,DORaw,DOFiltered,FlowMeasured,FlowSetpoint,AgitationSetpoint,Valve1,Valve2,VFlow,TemperatureC,RpmMeasured"
    analytical = []
    for name, ceq, start, rate, offset, our in [
        ("abiotic", 100., 10., 72., 0., 0.),
        ("biotic-constant-our", 75., 35., 72., 0., 1800.),
        ("shifted-time", 100., 10., 72., 123., 0.),
    ]:
        case_dir = DEST / "analytic-v2" / name
        run_dir = case_dir / "Corridas/N0400_Q03p00_Rep01"
        run_dir.mkdir(parents=True, exist_ok=True)
        samples = []
        for i in range(101):
            t = 2 * i
            y = ceq - (ceq - start) * math.exp(-rate * t / 3600)
            # ADC deliberately differs from calibrated OD to detect a channel mix-up.
            temp = "" if i == 0 else "30.00"
            rpm = "" if i == 0 else "400.00"
            samples.append(f"2026-10-06T12:00:00+00:00,{offset+t:.3f},Reoxygenating,{10000+100*y:.10f},{y:.10f},3,3,400,1,0,1,{temp},{rpm}")
        (run_dir / "dados-brutos.csv").write_text(header + "\n" + "\n".join(samples) + "\n", encoding="utf-8")
        write_json(case_dir / "teste.json", {"schemaVersion": 2, "name": name,
            "folderName": name, "status": "Completed", "nature": "Abiotico",
            "appVersion": "E0-fixture", "algorithmVersion": "LogLinear_OLS_v2"})
        analytical.append({"id": name, "path": str(case_dir.relative_to(DEST)).replace("\\", "/"),
            "ceqPercent": ceq, "physicalSaturationPercent": 100., "klaPerHour": rate,
            "ourPercentPointsPerHour": our, "windowStartSeconds": offset+10,
            "windowEndSeconds": offset+120, "points": 101, "expectedUsedPoints": 56,
            "toleranceKlaPerHour": 1e-6,
            "provenance": "analytic truth, ideal probe; biotic is an algebraic kernel fixture, not an executable biotic assay"})
    write_json(DEST / "analytical-cases.json", analytical)

    with (science / "data/experimental/curated/curves_manifest.csv").open(encoding="utf-8-sig", newline="") as handle:
        rows = list(csv.DictReader(handle))
    chosen = [next(r for r in rows if r["curve_id"] == name) for name in ("ALR5L-01", "ALR5L-02", "SC0708-02")]
    development_groups = {(r["assay"], r["batch"]) for r in chosen}
    split = []
    for row in rows:
        # All biotic curves share one cultivation and cannot supply independent holdout.
        role = "development" if (row["assay"], row["batch"]) in development_groups or row["biology"] == "biotic" else "reserved-not-evaluated"
        split.append({"curveId": row["curve_id"], "biology": row["biology"],
            "group": f'{row["assay"]}:{row["batch"]}', "role": role,
            "reason": "group-level split; new cultivations required for independent biotic evaluation"})
    for row in chosen:
        source = science / f'data/experimental/curated/curves/{row["curve_id"]}.csv'
        target = DEST / "experimental-development" / source.name
        target.parent.mkdir(parents=True, exist_ok=True)
        shutil.copyfile(source, target)
    write_json(DEST / "experimental-selection.json", chosen)
    write_json(DEST / "split-manifest.json", split)

    cfg_path = culture / "Configuracoes/settings.json"
    cfg = json.loads(cfg_path.read_text(encoding="utf-8-sig"))
    # Only relevant configuration; never copy connection credentials into test fixtures.
    config = {k.lower(): v for k, v in cfg.items()}
    write_json(DEST / "operating-envelope.json", {
        "status": "pending-operator-input", "bioticExecutionApproved": False,
        "settingsSourceSha256": digest(cfg_path),
        "observedSetpointsNotExperimentalApproval": config.get("setpoints"),
        "observedOurSettingsNotScientificCalibration": config.get("our"),
        "approved": {key: None for key in ("workingVolumeL", "temperatureC", "agitationMinRpm",
            "agitationMaxRpm", "airflowMinLpm", "airflowMaxLpm", "operatingDoPercent",
            "minimumDoPercent", "maximumDoDropPoints", "maximumGasOffSeconds",
            "maximumRecoverySeconds", "minimumInterAssaySeconds", "probeModel", "probeTauSeconds",
            "sampleIntervalSeconds", "maximumOxygenSampleAgeSeconds", "approvedBy", "approvedUtc")},
    })
    baseline["fixtureHashes"] = {str(p.relative_to(DEST)).replace("\\", "/"): digest(p)
        for p in sorted(DEST.rglob("*")) if p.is_file() and p.name != "baseline.json"}
    write_json(DEST / "baseline.json", baseline)
    print(f"Captured {len(baseline['fixtureHashes'])} fixture files; operational limits pending.")


def verify():
    baseline = json.loads((DEST / "baseline.json").read_text(encoding="utf-8"))
    failures = [p for p, expected in baseline["fixtureHashes"].items()
        if not (DEST / p).is_file() or digest(DEST / p) != expected]
    if failures:
        raise SystemExit("Reference hash mismatch: " + ", ".join(failures))
    split = json.loads((DEST / "split-manifest.json").read_text(encoding="utf-8"))
    roles = {}
    for row in split:
        roles.setdefault(row["group"], set()).add(row["role"])
    assert all(len(role) == 1 for role in roles.values()), "Acquisition group leakage"
    assert all(r["role"] == "development" for r in split if r["biology"] == "biotic")
    for case in json.loads((DEST / "analytical-cases.json").read_text(encoding="utf-8")):
        path = DEST / case["path"] / "Corridas/N0400_Q03p00_Rep01/dados-brutos.csv"
        with path.open(encoding="utf-8", newline="") as handle:
            rows = list(csv.DictReader(handle))
        selected = [(float(r["RelativeSeconds"]), math.log(case["ceqPercent"] - float(r["DOFiltered"])))
            for r in rows if case["windowStartSeconds"] <= float(r["RelativeSeconds"]) <= case["windowEndSeconds"]]
        xbar = sum(x for x, _ in selected) / len(selected)
        ybar = sum(y for _, y in selected) / len(selected)
        slope = sum((x-xbar)*(y-ybar) for x, y in selected) / sum((x-xbar)**2 for x, _ in selected)
        assert abs(-3600*slope - case["klaPerHour"]) <= case["toleranceKlaPerHour"]
    print(f"Verified {len(baseline['fixtureHashes'])} hashes, {len(split)} split entries and 3 analytical truths.")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("mode", choices=["capture", "verify"])
    parser.add_argument("--science", type=Path)
    parser.add_argument("--culture", type=Path)
    args = parser.parse_args()
    if args.mode == "capture":
        if args.science is None or args.culture is None:
            parser.error("capture requires --science and --culture")
        capture(args.science, args.culture)
    else:
        verify()
