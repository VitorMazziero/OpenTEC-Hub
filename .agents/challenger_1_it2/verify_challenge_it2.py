#!/usr/bin/env python3
"""
Adversarial Verification and Empirical Challenge Harness (Iteration 2)
Author: challenger_1_it2 (teamwork_preview_challenger)
Date: 2026-09-13
"""

import os
import re
import sys
import tempfile
import subprocess
import shutil

# Configure UTF-8 stdout
if hasattr(sys.stdout, "reconfigure"):
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
if hasattr(sys.stderr, "reconfigure"):
    sys.stderr.reconfigure(encoding="utf-8", errors="replace")

REPO_ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
PLAN_FILE = os.path.join(REPO_ROOT, "IMPLEMENTATION_PLAN_FLUXOMETRO.md")
VERIFY_SCRIPT = os.path.join(REPO_ROOT, "verify_plan.py")

REQUIRED_IDS = [f"F{i:02d}" for i in range(1, 17)]

REQUIRED_SECTIONS = [
    ("Sumário Executivo", r"^## 1\.\s+Sumário Executivo"),
    ("Arquitetura do Sistema", r"^## 2\.\s+Arquitetura do Sistema"),
    ("Análise Detalhada (F01 a F16)", r"^## 3\.\s+Análise Detalhada"),
    ("Matriz de Compatibilidade", r"^## 4\.\s+Matriz de Compatibilidade"),
    ("Roteiro em Fases", r"^## 5\.\s+Roteiro de Execução em Fases"),
    ("Critérios de Aceitação", r"^## 6\.\s+Critérios de Aceitação"),
]

REQUIRED_SUBSECTIONS = [
    ("Problema Declarado e Causa Raiz", [r"Causa Raiz", r"Problema Declarado"]),
    ("Localização Exata no Código", [r"Localização Exata no Código", r"Localização Exata"]),
    ("Impacto Sistêmico Cruzado", [r"Impacto Sistêmico Cruzado", r"Impacto Sistêmico"]),
    ("Decisão Técnica de Ação", [r"Decisão Técnica de Ação"]),
    ("Plano de Modificação / Justificativa", [r"Plano de Modificação", r"Plano de Implementação", r"Justificativa Técnica"]),
    ("Prioridade", [r"\*\*Prioridade:\*\*"]),
    ("Classificação de Segurança", [r"\*\*Classificação de Segurança:\*\*"]),
]

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

def run_tests():
    print("=" * 80)
    print("EMPIRICAL CHALLENGER ITERATION 2 RE-CHALLENGE HARNESS")
    print(f"Repo Root: {REPO_ROOT}")
    print(f"Plan File: {PLAN_FILE}")
    print("=" * 80)

    errors = []
    warnings = []

    # 1. Existence and size
    if not os.path.exists(PLAN_FILE):
        print(f"[FAIL] Plan file {PLAN_FILE} does not exist!")
        return 1

    file_size = os.path.getsize(PLAN_FILE)
    print(f"[TEST 1] Plan file existence and size: {file_size} bytes -> PASS")
    if file_size < 50000:
        errors.append(f"File size {file_size} is suspiciously small!")

    with open(PLAN_FILE, "r", encoding="utf-8") as f:
        content = f.read()
        lines = content.splitlines()

    print(f"         Total document lines: {len(lines)}")

    # 2. Structural high-level sections
    print("\n[TEST 2] Checking 6 high-level structural sections...")
    for sec_name, pattern in REQUIRED_SECTIONS:
        matched = any(re.search(pattern, line, re.IGNORECASE) for line in lines)
        status = "PASS" if matched else "FAIL"
        print(f"  [{status}] Section: {sec_name}")
        if not matched:
            errors.append(f"Missing high-level section: {sec_name}")

    # 3. Strict H3 Header and 7 required subsections for F01-F16
    print("\n[TEST 3] Checking F01-F16 H3 headers and 7 required subsections...")
    item_slices = {}
    for i, item_id in enumerate(REQUIRED_IDS):
        h3_pattern = rf"^###\s+{item_id}\s*[-—–:]\s*(.+)$"
        header_line = -1
        header_title = ""
        for line_idx, line in enumerate(lines, 1):
            m = re.match(h3_pattern, line)
            if m:
                header_line = line_idx
                header_title = m.group(1).strip()
                break

        if header_line == -1:
            errors.append(f"Missing H3 header for {item_id}")
            print(f"  [FAIL] {item_id}: Missing explicit H3 header!")
            continue

        start_line = header_line - 1
        end_line = len(lines)
        for next_idx in range(start_line + 1, len(lines)):
            if re.match(r"^###\s+F\d{2}", lines[next_idx]) or re.match(r"^##\s+\d+\.", lines[next_idx]):
                end_line = next_idx
                break

        item_text = "\n".join(lines[start_line:end_line])
        item_slices[item_id] = item_text

        missing_sub = []
        for sub_name, pats in REQUIRED_SUBSECTIONS:
            if not any(re.search(p, item_text, re.IGNORECASE) for p in pats):
                missing_sub.append(sub_name)

        status = "PASS" if not missing_sub else "FAIL"
        print(f"  [{status}] {item_id} (L{header_line}-{end_line}): {header_title[:45]} | Missing subs: {missing_sub if missing_sub else 'None'}")
        if missing_sub:
            errors.append(f"{item_id} missing subsections: {missing_sub}")

    # 4. Scan for developer placeholders
    print("\n[TEST 4] Adversarial placeholder and slop detection...")
    placeholder_count = 0
    for pat in SUSPICIOUS_PLACEHOLDERS:
        flags = 0 if any(acronym in pat for acronym in ["TODO", "FIXME", "TBD", "WIP"]) else re.IGNORECASE
        for m in re.finditer(pat, content, flags):
            idx = m.start()
            line_no = content[:idx].count("\n") + 1
            print(f"  [FLAG L{line_no}] Matched placeholder: {m.group(0)}")
            placeholder_count += 1
            errors.append(f"Placeholder '{m.group(0)}' at line {line_no}")

    if placeholder_count == 0:
        print("  [PASS] Zero placeholders found.")

    # 5. Deep verification of Iteration 2 Challenger Remediations
    print("\n[TEST 5] Deep Audit of Iteration 2 Remediations (Deficiencies from Challenger 2):")

    # F04 Check
    f04_text = item_slices.get("F04", "")
    has_integralError = "integralError" in f04_text
    has_no_integral_term = "integral_term" not in f04_text or "não integral_term" in f04_text or "corrigindo o identificador errôneo integral_term" in f04_text
    has_dac_hold = "dacHold" in f04_text
    has_min_cutoff = "MIN_FLOW_CUTOFF_THRESHOLD" in f04_text
    has_finitude = "isnan" in f04_text and "isinf" in f04_text
    print(f"  [F04] integralError used: {has_integralError}")
    print(f"  [F04] dacHold respected: {has_dac_hold}")
    print(f"  [F04] MIN_FLOW_CUTOFF_THRESHOLD defined: {has_min_cutoff}")
    print(f"  [F04] isnan/isinf checks: {has_finitude}")
    if not (has_integralError and has_dac_hold and has_min_cutoff and has_finitude):
        errors.append("F04 remediation audit failed!")

    # F06 Check
    f06_text = item_slices.get("F06", "")
    has_unconditional_cutoff = "stagedV1 == 0 && stagedV2 == 0" in f06_text and "stagedVFlow = 1" in f06_text
    has_hardware_staging = "stagedV1 = valve1State" in f06_text
    print(f"  [F06] Unconditional cutoff on both valves closed: {has_unconditional_cutoff}")
    print(f"  [F06] Staging initialized from hardware state: {has_hardware_staging}")
    if not (has_unconditional_cutoff and has_hardware_staging):
        errors.append("F06 remediation audit failed!")

    # F07/F08 Check
    f07_text = item_slices.get("F07", "")
    f08_text = item_slices.get("F08", "")
    has_scale_1e7 = "1.0e7" in f07_text or "1e7" in f07_text
    has_strcasecmp = "strcasecmp" in f08_text
    has_bool_cases = '"true"' in f08_text and '"false"' in f08_text
    has_transactional = "2 fases" in f08_text or "Staged" in f08_text
    print(f"  [F07] Quartic scale range +/- 1.0e7: {has_scale_1e7}")
    print(f"  [F08] Case-insensitive strcasecmp for booleans: {has_strcasecmp}")
    print(f"  [F08] True/False/1/0 support: {has_bool_cases}")
    print(f"  [F08] Transactional staging in 2 phases: {has_transactional}")
    if not (has_scale_1e7 and has_strcasecmp and has_bool_cases and has_transactional):
        errors.append("F07/F08 remediation audit failed!")

    # F09 Check
    f09_text = item_slices.get("F09", "")
    has_v5_magic = "0xCAFEBAC3" in f09_text
    has_v6_magic = "0xCAFEBAC4" in f09_text
    has_max_flow_field = "float max_flow;" in f09_text or "float max_flow" in f09_text
    has_max_flow_50 = "calParams.max_flow = MAX_FLOW_DEFAULT" in f09_text or "calParams.max_flow = 50.0f" in f09_text
    has_load_assign = "maxFlowRate = calParams.max_flow" in f09_text
    has_hub_rearm = "pendingMaxFlow" in f09_text
    print(f"  [F09] EEPROM Schema V5 (0xCAFEBAC3) to V6 (0xCAFEBAC4) migration: {has_v5_magic and has_v6_magic}")
    print(f"  [F09] CalibrationParams includes max_flow: {has_max_flow_field}")
    print(f"  [F09] Factory/fallback initializes max_flow = 50.0: {has_max_flow_50}")
    print(f"  [F09] loadParameters assigns maxFlowRate = calParams.max_flow: {has_load_assign}")
    print(f"  [F09] Hub rearms pendingMaxFlow: {has_hub_rearm}")
    if not (has_v5_magic and has_v6_magic and has_max_flow_field and has_max_flow_50 and has_load_assign and has_hub_rearm):
        errors.append("F09 remediation audit failed!")

    # F12/F14 Check
    f12_text = item_slices.get("F12", "")
    f14_text = item_slices.get("F14", "")
    has_hw_latch = "hardwareFaultLatched" in f12_text and "adsHealthy" in f12_text and "dacHealthy" in f12_text
    has_loop_latch = "hardwareFaultLatched || !adsHealthy || !dacHealthy" in f12_text
    has_ota_mutex = "commandMutex" in f14_text
    has_ota_safe_latch = "otaSafeLatch" in f14_text
    print(f"  [F12] Continuous hardware fault latch: {has_hw_latch and has_loop_latch}")
    print(f"  [F14] OTA safe-stop under commandMutex: {has_ota_mutex}")
    print(f"  [F14] OTA watchdog stall locks in otaSafeLatch: {has_ota_safe_latch}")
    if not (has_hw_latch and has_loop_latch and has_ota_mutex and has_ota_safe_latch):
        errors.append("F12/F14 remediation audit failed!")

    # 6. Adversarial Negative Mutation Testing on verify_plan.py
    print("\n[TEST 6] Adversarial Negative Mutation Testing of verify_plan.py...")
    # Test 6.1: Run verify_plan.py on unchanged file
    res = subprocess.run([sys.executable, VERIFY_SCRIPT], capture_output=True, text=True, cwd=REPO_ROOT)
    print(f"  [Baseline] verify_plan.py on untouched plan -> Exit code: {res.returncode} (Expected 0)")
    if res.returncode != 0:
        errors.append(f"Baseline verify_plan.py failed with exit code {res.returncode}")

    # Helper for running negative tests with modified plan
    backup_file = PLAN_FILE + ".bak_challenge"
    shutil.copyfile(PLAN_FILE, backup_file)
    try:
        # Mutation A: Empty plan
        with open(PLAN_FILE, "w", encoding="utf-8") as f:
            f.write("# Empty\n")
        res_empty = subprocess.run([sys.executable, VERIFY_SCRIPT], capture_output=True, text=True, cwd=REPO_ROOT)
        passed_empty = (res_empty.returncode != 0)
        print(f"  [Mutation A - Empty File] Exit code: {res_empty.returncode} (Expected != 0) -> {'PASS' if passed_empty else 'FAIL'}")
        if not passed_empty:
            errors.append("verify_plan.py accepted empty file!")

        # Mutation B: Missing Section 4
        mut_b = re.sub(r"## 4\.\s+Matriz de Compatibilidade.*?(?=## 5\.)", "", content, flags=re.DOTALL)
        with open(PLAN_FILE, "w", encoding="utf-8") as f:
            f.write(mut_b)
        res_b = subprocess.run([sys.executable, VERIFY_SCRIPT], capture_output=True, text=True, cwd=REPO_ROOT)
        passed_b = (res_b.returncode != 0)
        print(f"  [Mutation B - Drop Section 4] Exit code: {res_b.returncode} (Expected != 0) -> {'PASS' if passed_b else 'FAIL'}")
        if not passed_b:
            errors.append("verify_plan.py failed to catch missing Section 4!")

        # Mutation C: Drop F07 Header
        mut_c = content.replace("### F07 —", "### G07 —")
        with open(PLAN_FILE, "w", encoding="utf-8") as f:
            f.write(mut_c)
        res_c = subprocess.run([sys.executable, VERIFY_SCRIPT], capture_output=True, text=True, cwd=REPO_ROOT)
        passed_c = (res_c.returncode != 0)
        print(f"  [Mutation C - Rename F07 to G07] Exit code: {res_c.returncode} (Expected != 0) -> {'PASS' if passed_c else 'FAIL'}")
        if not passed_c:
            errors.append("verify_plan.py failed to catch missing F07!")

        # Mutation D: Remove "Causa Raiz" and "Problema" from F12
        mut_d = content.replace("Problema Declarado e Causa Raiz", "Descricao Geral")
        with open(PLAN_FILE, "w", encoding="utf-8") as f:
            f.write(mut_d)
        res_d = subprocess.run([sys.executable, VERIFY_SCRIPT], capture_output=True, text=True, cwd=REPO_ROOT)
        passed_d = (res_d.returncode != 0)
        print(f"  [Mutation D - Drop Causa Raiz headers] Exit code: {res_d.returncode} (Expected != 0) -> {'PASS' if passed_d else 'FAIL'}")
        if not passed_d:
            errors.append("verify_plan.py failed to catch missing subchecks in F12!")

    finally:
        # Restore original plan file
        shutil.move(backup_file, PLAN_FILE)

    # 7. Final Assessment
    print("\n" + "=" * 80)
    print("FINAL CHALLENGE ASSESSMENT")
    print(f"Total Errors: {len(errors)}")
    print(f"Total Warnings: {len(warnings)}")
    if errors:
        print("Encountered Errors:")
        for e in errors:
            print(f"  - {e}")
        print("VERDICT: REQUEST_CHANGES")
        return 1
    else:
        print("All structural, semantic, adversarial mutation, and remediation checks passed!")
        print("VERDICT: APPROVE")
        return 0

if __name__ == "__main__":
    sys.exit(run_tests())
