#!/usr/bin/env python3
"""
Verification Script for IMPLEMENTATION_PLAN_FLUXOMETRO.md
Author: worker_plan_1 (teamwork_preview_worker)
Date: 2026-09-13

Validates that IMPLEMENTATION_PLAN_FLUXOMETRO.md exists, contains all 16 inconsistency
specifications (F01 to F16), and satisfies structural and analytical completeness criteria.
"""

import os
import re
import sys

# Ensure UTF-8 output on Windows consoles
if hasattr(sys.stdout, "reconfigure"):
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
if hasattr(sys.stderr, "reconfigure"):
    sys.stderr.reconfigure(encoding="utf-8", errors="replace")

PLAN_FILE = os.path.join(os.path.dirname(os.path.abspath(__file__)), "IMPLEMENTATION_PLAN_FLUXOMETRO.md")
if not os.path.exists(PLAN_FILE):
    PLAN_FILE = os.path.join(os.path.dirname(os.path.abspath(__file__)), "External-Devices", "docs", "IMPLEMENTATION_PLAN_FLUXOMETRO.md")

REQUIRED_IDS = [f"F{i:02d}" for i in range(1, 17)]

REQUIRED_SECTIONS = [
    ("Sumário Executivo", r"## 1\.\s+Sumário Executivo"),
    ("Arquitetura do Sistema", r"## 2\.\s+Arquitetura do Sistema"),
    ("Análise Detalhada (F01-F16)", r"## 3\.\s+Análise Detalhada"),
    ("Matriz de Compatibilidade", r"## 4\.\s+Matriz de Compatibilidade"),
    ("Roteiro em Fases (Roadmap)", r"## 5\.\s+Roteiro de Execução em Fases"),
    ("Critérios de Aceitação e Verificação", r"## 6\.\s+Critérios de Aceitação"),
]

PER_ITEM_CHECKS = [
    ("Problema Declarado / Causa Raiz", [r"Causa Raiz", r"Problema Declarado", r"Localização Exata"]),
    ("Decisão Técnica / Plano de Ação", [r"Decisão Técnica de Ação", r"Plano de Modificação", r"Justificativa Técnica"]),
    ("Prioridade / Segurança", [r"Prioridade:", r"Classificação de Segurança:"]),
]


def verify_plan():
    print(f"[1/4] Checking file existence: {PLAN_FILE}")
    if not os.path.exists(PLAN_FILE):
        print(f"FAIL: File '{PLAN_FILE}' does not exist!")
        return 1

    file_size = os.path.getsize(PLAN_FILE)
    print(f"      File exists ({file_size} bytes).")
    if file_size < 1000:
        print("FAIL: File is unexpectedly small!")
        return 1

    with open(PLAN_FILE, "r", encoding="utf-8") as f:
        content = f.read()

    print("\n[2/4] Verifying high-level document sections...")
    section_errors = 0
    for sec_name, pattern in REQUIRED_SECTIONS:
        if re.search(pattern, content, re.IGNORECASE):
            print(f"      [PASS] Section '{sec_name}' found.")
        else:
            print(f"      [FAIL] Section '{sec_name}' not found matching pattern: {pattern}")
            section_errors += 1

    if section_errors > 0:
        print(f"FAIL: {section_errors} required document sections are missing!")
        return 1

    print("\n[3/4] Verifying each of the 16 inconsistencies (F01 to F16)...")
    id_results = {}
    missing_ids = []

    for item_id in REQUIRED_IDS:
        # Check header presence: e.g. ### F01 — ... or ## F01 ...
        header_match = re.search(rf"###\s+{item_id}\s*[-—–:]\s*(.+)", content)
        if not header_match:
            # Fallback check for any header or list item with the ID
            header_match = re.search(rf"(?:#+|\-|\*)\s+.*?\b{item_id}\b.*", content)

        if not header_match:
            missing_ids.append(item_id)
            id_results[item_id] = {"found": False, "title": None, "subchecks": {}}
            continue

        title = header_match.group(0).strip()
        subchecks = {}

        # Locate the content slice for this item until the next item or next major section
        start_idx = header_match.start()
        next_match = re.search(r"(?:###\s+F\d{2}|##\s+\d+\.)", content[start_idx + 1:])
        end_idx = start_idx + 1 + next_match.start() if next_match else len(content)
        item_text = content[start_idx:end_idx]

        for check_name, patterns in PER_ITEM_CHECKS:
            found = any(re.search(pat, item_text, re.IGNORECASE) for pat in patterns)
            subchecks[check_name] = found

        id_results[item_id] = {
            "found": True,
            "title": title,
            "subchecks": subchecks,
        }

    # Print summary table
    print(f"\n{'ID':<5} | {'Status':<6} | {'Header Title':<50} | {'Checks (Root/Action/Safety)'}")
    print("-" * 95)
    all_subchecks_passed = True

    for item_id in REQUIRED_IDS:
        res = id_results[item_id]
        if not res["found"]:
            print(f"{item_id:<5} | MISSING| {'-' * 50} | [FAILED]")
            all_subchecks_passed = False
        else:
            checks_status = [
                "OK" if res["subchecks"].get(chk, False) else "FAIL"
                for chk, _ in PER_ITEM_CHECKS
            ]
            checks_str = "/".join(checks_status)
            if any(s == "FAIL" for s in checks_status):
                all_subchecks_passed = False
            title_display = res["title"][:50]
            print(f"{item_id:<5} | PASS   | {title_display:<50} | [{checks_str}]")

    print("\n[4/4] Final Verification Assessment...")
    if missing_ids:
        print(f"FAIL: Missing IDs in plan: {', '.join(missing_ids)}")
        return 1

    if not all_subchecks_passed:
        print("FAIL: Some per-item subchecks failed!")
        return 1

    print("\n==================================================================")
    print("SUCCESS: IMPLEMENTATION_PLAN_FLUXOMETRO.md is complete and verified!")
    print(f"All 16 inconsistencies (F01-F16) are thoroughly documented.")
    print("==================================================================")
    return 0


if __name__ == "__main__":
    sys.exit(verify_plan())
