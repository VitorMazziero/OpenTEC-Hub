#!/usr/bin/env python3
"""
Deep Codebase Grounding Verifier (v2)
Author: challenger_1
Date: 2026-09-13
"""

import os
import re
import sys

REPO_ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
PLAN_FILE = os.path.join(REPO_ROOT, "IMPLEMENTATION_PLAN_FLUXOMETRO.md")

with open(PLAN_FILE, "r", encoding="utf-8") as f:
    plan_content = f.read()

REQUIRED_IDS = [f"F{i:02d}" for i in range(1, 17)]

# Split content by H3 headers
item_blocks = {}
for i, item_id in enumerate(REQUIRED_IDS):
    start_pat = rf"^###\s+{item_id}\s*[-—–:]\s*(.+)$"
    start_m = re.search(start_pat, plan_content, re.MULTILINE)
    if not start_m:
        continue
    start_idx = start_m.start()
    if i + 1 < len(REQUIRED_IDS):
        next_id = REQUIRED_IDS[i + 1]
        next_pat = rf"^###\s+{next_id}\s*[-—–:]"
        next_m = re.search(next_pat, plan_content, re.MULTILINE)
        end_idx = next_m.start() if next_m else len(plan_content)
    else:
        next_pat = r"^##\s+\d+\."
        next_m = re.search(next_pat, plan_content[start_idx + 1:], re.MULTILINE)
        end_idx = start_idx + 1 + next_m.start() if next_m else len(plan_content)

    item_blocks[item_id] = plan_content[start_idx:end_idx]

# Pattern for file path and lines in a backticked string:
# e.g.: `External-Devices/.../File.ext:12-34` or `Windows_app/.../File.cs:123`
ref_pattern = re.compile(r"`([^`\s]+?\.(?:cpp|h|cs|dart|xaml|md|json|ino))(?::(\d+)(?:[–\-,](\d+))?)?`")

discrepancies = []
total_citations = 0

for item_id in REQUIRED_IDS:
    block = item_blocks.get(item_id, "")
    print(f"\n==================== {item_id} ====================")
    citations = ref_pattern.findall(block)
    for raw_path, start_s, end_s in citations:
        total_citations += 1
        raw_path = raw_path.replace("/", os.sep).replace("\\", os.sep)
        start_l = int(start_s) if start_s else None
        end_l = int(end_s) if end_s else start_l

        abs_path = os.path.join(REPO_ROOT, raw_path)
        if not os.path.exists(abs_path):
            # Check if there is a known prefix
            print(f"  [MISSING FILE] {raw_path}")
            discrepancies.append((item_id, raw_path, "File does not exist"))
            continue

        with open(abs_path, "r", encoding="utf-8", errors="replace") as target_f:
            target_lines = target_f.readlines()

        if start_l is not None:
            if start_l > len(target_lines):
                print(f"  [LINE OOB] {raw_path}:{start_l} > total lines ({len(target_lines)})")
                discrepancies.append((item_id, raw_path, f"Line {start_l} out of bounds (max {len(target_lines)})"))
            else:
                snippet = target_lines[start_l - 1].strip()
                end_idx = min(len(target_lines), end_l) if end_l else start_l
                context = [target_lines[k].strip() for k in range(start_l - 1, end_idx)]
                print(f"  [OK] {raw_path}:{start_l}{f'-{end_l}' if end_l and end_l != start_l else ''}")
                print(f"       -> {snippet[:80]}")

print("\n" + "=" * 80)
print(f"Total Citations Inspected: {total_citations}")
print(f"Discrepancies: {len(discrepancies)}")
print("=" * 80)
