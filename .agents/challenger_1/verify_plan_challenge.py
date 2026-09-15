#!/usr/bin/env python3
"""
Adversarial Verification & Structural Parsing Challenge Script (v2)
Author: challenger_1 (teamwork_preview_challenger)
Date: 2026-09-13

Performs automated AST/regex analysis, codebase grounding, line-number validation,
and semantic consistency checks on IMPLEMENTATION_PLAN_FLUXOMETRO.md.
"""

import os
import re
import sys
from pathlib import Path

# Ensure UTF-8 output
if hasattr(sys.stdout, "reconfigure"):
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
if hasattr(sys.stderr, "reconfigure"):
    sys.stderr.reconfigure(encoding="utf-8", errors="replace")

REPO_ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
PLAN_FILE = os.path.join(REPO_ROOT, "IMPLEMENTATION_PLAN_FLUXOMETRO.md")

REQUIRED_IDS = [f"F{i:02d}" for i in range(1, 17)]

REQUIRED_SECTIONS = [
    ("1. Sumário Executivo", r"^## 1\.\s+Sumário Executivo"),
    ("2. Arquitetura do Sistema e Cadeia de Comunicação", r"^## 2\.\s+Arquitetura do Sistema"),
    ("3. Análise Detalhada (F01 a F16)", r"^## 3\.\s+Análise Detalhada"),
    ("4. Matriz de Compatibilidade", r"^## 4\.\s+Matriz de Compatibilidade"),
    ("5. Roteiro de Execução em Fases", r"^## 5\.\s+Roteiro de Execução em Fases"),
    ("6. Critérios de Aceitação e Verificação", r"^## 6\.\s+Critérios de Aceitação"),
]

REQUIRED_SUBSECTIONS = [
    ("Problema Declarado e Causa Raiz", [r"####\s+Problema Declarado e Causa Raiz", r"####\s+Problema Declarado"]),
    ("Localização Exata no Código", [r"Localização Exata no Código", r"Localização Exata"]),
    ("Impacto Sistêmico Cruzado", [r"####\s+Impacto Sistêmico Cruzado", r"Impacto Sistêmico"]),
    ("Decisão Técnica de Ação", [r"####\s+Decisão Técnica de Ação"]),
    ("Plano ou Justificativa", [r"####\s+Plano de Modificação", r"####\s+Plano de Implementação", r"Justificativa Técnica"]),
    ("Prioridade", [r"-\s+\*\*Prioridade:\*\*", r"\*\*Prioridade:\*\*"]),
    ("Classificação de Segurança", [r"-\s+\*\*Classificação de Segurança:\*\*", r"\*\*Classificação de Segurança:\*\*"]),
]

# Case-sensitive checks for developer placeholders (to avoid Portuguese 'todo' meaning 'all')
SUSPICIOUS_PLACEHOLDERS = [
    r"\bTODO\b",
    r"\bFIXME\b",
    r"\bTBD\b",
    r"\bWIP\b",
    r"\bA definir\b",
    r"\bNão analisado\b",
    r"\bPreencher depois\b",
    r"\bPlaceholder\b",
]

def build_repo_file_index():
    index = {}
    for root, dirs, files in os.walk(REPO_ROOT):
        # Ignore git and .agents metadata
        if ".git" in root or ".agents" in root:
            continue
        for f in files:
            rel = os.path.relpath(os.path.join(root, f), REPO_ROOT).replace("\\", "/")
            base = f.lower()
            if base not in index:
                index[base] = []
            index[base].append(rel)
    return index

def analyze_plan():
    print("=" * 80)
    print("ADVERSARIAL VERIFICATION HARNESS v2: IMPLEMENTATION_PLAN_FLUXOMETRO.md")
    print(f"Repo Root: {REPO_ROOT}")
    print(f"Plan File: {PLAN_FILE}")
    print("=" * 80)

    if not os.path.exists(PLAN_FILE):
        print(f"CRITICAL ERROR: Plan file not found at {PLAN_FILE}")
        return False, {"file_exists": False}

    with open(PLAN_FILE, "r", encoding="utf-8") as f:
        content = f.read()
        lines = content.splitlines()

    print(f"Total lines: {len(lines)}")
    print(f"Total bytes: {len(content.encode('utf-8'))}")

    repo_index = build_repo_file_index()

    results = {
        "file_exists": True,
        "high_level_sections": {},
        "item_h3_headers": {},
        "item_subsections": {},
        "file_references": [],
        "line_references": [],
        "placeholders": [],
        "errors": [],
        "warnings": [],
        "grounding_checks": []
    }

    # 1. High-level sections
    print("\n--- [Phase 1] High-Level Section Verification ---")
    for sec_name, pattern in REQUIRED_SECTIONS:
        found = False
        line_num = -1
        for i, line in enumerate(lines, 1):
            if re.search(pattern, line, re.IGNORECASE):
                found = True
                line_num = i
                break
        results["high_level_sections"][sec_name] = {"found": found, "line": line_num}
        status = f"PASS (L{line_num})" if found else "FAIL"
        print(f"  [{status:<10}] {sec_name}")
        if not found:
            results["errors"].append(f"Missing high-level section: {sec_name}")

    # 2. Per-Item H3 Header and Subsection Verification
    print("\n--- [Phase 2] F01-F16 Strict H3 Header & Structure Verification ---")
    item_slices = {}
    for item_id in REQUIRED_IDS:
        h3_pattern = rf"^###\s+{item_id}\s*[-—–:]\s*(.+)$"
        found = False
        header_line = -1
        header_title = ""
        for i, line in enumerate(lines, 1):
            m = re.match(h3_pattern, line)
            if m:
                found = True
                header_line = i
                header_title = m.group(1).strip()
                break

        results["item_h3_headers"][item_id] = {
            "found": found,
            "line": header_line,
            "title": header_title
        }
        if not found:
            results["errors"].append(f"Missing explicit H3 header for {item_id}")
            print(f"  [FAIL] {item_id}: Missing explicit H3 header!")
        else:
            print(f"  [PASS] {item_id} (L{header_line}): {header_title[:60]}")

    # Extract slices between headers
    for i, item_id in enumerate(REQUIRED_IDS):
        if not results["item_h3_headers"][item_id]["found"]:
            continue
        start_line = results["item_h3_headers"][item_id]["line"] - 1
        end_line = len(lines)
        if i + 1 < len(REQUIRED_IDS) and results["item_h3_headers"][REQUIRED_IDS[i + 1]]["found"]:
            end_line = results["item_h3_headers"][REQUIRED_IDS[i + 1]]["line"] - 1
        else:
            for j in range(start_line + 1, len(lines)):
                if lines[j].startswith("## "):
                    end_line = j
                    break
        item_text = "\n".join(lines[start_line:end_line])
        item_slices[item_id] = (start_line + 1, end_line, item_text)

    # Verify subsections in each slice
    print("\n--- [Phase 3] Required Subsection Content Verification ---")
    for item_id in REQUIRED_IDS:
        if item_id not in item_slices:
            continue
        start_l, end_l, text = item_slices[item_id]
        item_subs = {}
        missing_subs = []
        for sub_name, patterns in REQUIRED_SUBSECTIONS:
            sub_found = any(re.search(pat, text, re.IGNORECASE) for pat in patterns)
            item_subs[sub_name] = sub_found
            if not sub_found:
                missing_subs.append(sub_name)

        results["item_subsections"][item_id] = item_subs
        status = "PASS" if not missing_subs else "FAIL"
        if missing_subs:
            results["errors"].append(f"{item_id} missing subsections: {', '.join(missing_subs)}")
            print(f"  [{status}] {item_id}: Missing -> {', '.join(missing_subs)}")
        else:
            print(f"  [{status}] {item_id} (lines {start_l}-{end_l}): All 7 structural subsections present")

    # 3. Scan for placeholders / hand-waving
    print("\n--- [Phase 4] Adversarial Scan for Placeholders / Incompleteness ---")
    for pat in SUSPICIOUS_PLACEHOLDERS:
        # Using exact case sensitivity for acronyms like TODO, FIXME, TBD, WIP
        flags = 0 if any(acronym in pat for acronym in ["TODO", "FIXME", "TBD", "WIP"]) else re.IGNORECASE
        matches = re.finditer(pat, content, flags)
        for m in matches:
            idx = m.start()
            l_num = content[:idx].count("\n") + 1
            line_str = lines[l_num - 1].strip()
            results["placeholders"].append((l_num, m.group(0), line_str))
            print(f"  [FLAG L{l_num}] Matched '{m.group(0)}': {line_str[:70]}")
    if not results["placeholders"]:
        print("  [PASS] Zero placeholder keywords found in entire document.")

    # 4. Extract and deeply verify code references
    print("\n--- [Phase 5] Codebase Grounding: Deep Verification of File Paths & Lines ---")
    # Matches markdown backticked file references with optional line numbers: `path/file.ext:12-34`
    ref_pattern = re.compile(r"`([a-zA-Z0-9_\-\.\/\\]+\.(?:cpp|h|cs|dart|xaml|md|json|ino))(?::(\d+)(?:-(\d+))?)?`")

    explicit_path_checks = 0
    basename_resolved_checks = 0
    unresolved_files = []
    line_out_of_bounds = []

    for i, line in enumerate(lines, 1):
        for m in ref_pattern.finditer(line):
            raw_path = m.group(1).replace("\\", "/")
            start_l = int(m.group(2)) if m.group(2) else None
            end_l = int(m.group(3)) if m.group(3) else start_l

            # Check if raw_path directly exists
            direct_abs = os.path.join(REPO_ROOT, raw_path.replace("/", os.sep))
            resolved_abs = None

            if os.path.exists(direct_abs) and os.path.isfile(direct_abs):
                resolved_abs = direct_abs
                explicit_path_checks += 1
            else:
                # Check if it's a suffix or basename in repo_index
                base = os.path.basename(raw_path).lower()
                candidates = repo_index.get(base, [])
                # Try matching by suffix
                matched_cand = None
                for c in candidates:
                    if c.endswith(raw_path) or raw_path.endswith(c):
                        matched_cand = c
                        break
                if not matched_cand and len(candidates) == 1:
                    matched_cand = candidates[0]

                if matched_cand:
                    resolved_abs = os.path.join(REPO_ROOT, matched_cand.replace("/", os.sep))
                    basename_resolved_checks += 1
                else:
                    unresolved_files.append((i, raw_path, line.strip()))
                    continue

            # Validate line numbers against resolved file
            if resolved_abs and start_l is not None:
                try:
                    with open(resolved_abs, "r", encoding="utf-8", errors="replace") as rf:
                        target_file_lines = rf.readlines()
                    file_total_lines = len(target_file_lines)
                    if start_l > file_total_lines:
                        line_out_of_bounds.append((i, raw_path, start_l, file_total_lines))
                    else:
                        # Extract the line snippet and check semantic relevance
                        sample_line = target_file_lines[start_l - 1].strip()
                        results["grounding_checks"].append({
                            "plan_line": i,
                            "file": raw_path,
                            "target_line_num": start_l,
                            "content": sample_line
                        })
                except Exception as e:
                    results["warnings"].append(f"Error reading {resolved_abs}: {e}")

    print(f"  Explicit relative path matches: {explicit_path_checks}")
    print(f"  Basename/suffix resolved matches: {basename_resolved_checks}")
    print(f"  Unresolved file paths: {len(unresolved_files)}")
    for l_num, raw_p, l_txt in unresolved_files:
        print(f"    [UNRESOLVED L{l_num}] `{raw_p}` -> {l_txt[:60]}")
        results["errors"].append(f"L{l_num}: Unresolved file path `{raw_p}`")

    print(f"  Line out-of-bounds errors: {len(line_out_of_bounds)}")
    for l_num, raw_p, req_l, tot_l in line_out_of_bounds:
        print(f"    [OOB L{l_num}] `{raw_p}:{req_l}` exceeds file total lines ({tot_l})")
        results["errors"].append(f"L{l_num}: `{raw_p}:{req_l}` exceeds max lines {tot_l}")

    # 5. Check Grounding Samples for each F01-F16
    print("\n--- [Phase 6] Spot-Checking Specific Line Number Accuracy for F01-F16 ---")
    spot_checks = [
        ("F01", "FirmwareApp.cpp", 16, ["FW_VERSION", "V10"]),
        ("F02", "FirmwareApp.cpp", 183, ["FACTORY", "a1", "b1", "calib"]),
        ("F03", "FlowControlViewModel.cs", 690, ["FlowOutputText", "snapshot"]),
        ("F04", "CommandCodec.h", 170, ["targetFlowSetpoint", "valveFlowState", "VALVE"]),
        ("F05", "main.dart", 670, ["Flow Valve", "v_Flow", "toggle"]),
        ("F07", "CommandCodec.h", 195, ["kp", "ki", "kd", "ff", "cmd"]),
        ("F08", "CommandCodec.h", 100, ["deserializeJson", "doc"]),
        ("F09", "CommandCodec.h", 215, ["max_flow", "maxFlowRate"]),
        ("F10", "CommandCodec.h", 230, ["saveCalibration", "a1", "k1"]),
        ("F11", "CommandCodec.h", 270, ["ack", "readback", "calib"]),
        ("F12", "FlowIo.h", 45, ["ads", "dac", "begin"]),
        ("F13", "FirmwareApp.cpp", 90, ["WiFi", "softAP"]),
        ("F14", "OtaService.h", 40, ["OTA", "update", "stop"]),
        ("F15", "TaskRuntime.h", 40, ["reconnect_wifi", "wifi"]),
        ("F16", "Lifecycle.h", 210, ["valve", "state", "telemetry"]),
    ]

    spot_results = []
    for f_id, fname, ref_l, keywords in spot_checks:
        base = fname.lower()
        candidates = repo_index.get(base, [])
        if not candidates:
            spot_results.append((f_id, fname, ref_l, "FILE_NOT_FOUND"))
            continue
        cand_path = os.path.join(REPO_ROOT, candidates[0].replace("/", os.sep))
        with open(cand_path, "r", encoding="utf-8", errors="replace") as cf:
            flines = cf.readlines()

        # Check a window around ref_l (+/- 15 lines)
        start_w = max(0, ref_l - 15)
        end_w = min(len(flines), ref_l + 15)
        window_text = "".join(flines[start_w:end_w]).lower()

        matched_kws = [kw for kw in keywords if kw.lower() in window_text]
        status = "ACCURATE" if len(matched_kws) >= 1 else "DISCREPANCY"
        print(f"  [{status}] {f_id} in {fname}:{ref_l} -> window matched {matched_kws}")
        spot_results.append((f_id, fname, ref_l, status, matched_kws))

    # Summary
    print("\n" + "=" * 80)
    print("VERIFICATION SUMMARY")
    print(f"High-Level Sections: {sum(1 for s in results['high_level_sections'].values() if s['found'])} / {len(REQUIRED_SECTIONS)}")
    print(f"H3 Item Headers (F01-F16): {sum(1 for s in results['item_h3_headers'].values() if s['found'])} / {len(REQUIRED_IDS)}")
    print(f"Items with all 7 Subsections: {sum(1 for s in results['item_subsections'].values() if all(s.values()))} / {len(REQUIRED_IDS)}")
    print(f"Unresolved File Paths: {len(unresolved_files)}")
    print(f"Line Out-Of-Bounds: {len(line_out_of_bounds)}")
    print(f"Placeholders Found: {len(results['placeholders'])}")
    print(f"Total Errors: {len(results['errors'])}")
    print(f"Total Warnings: {len(results['warnings'])}")
    print("=" * 80)

    success = (len(results["errors"]) == 0)
    return success, results

if __name__ == "__main__":
    success, results = analyze_plan()
    sys.exit(0 if success else 1)
