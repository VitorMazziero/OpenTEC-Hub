#!/usr/bin/env python3
"""
test_verify_plan_biomassa_adversarial.py

Adversarial and empirical challenge test suite for verify_plan_biomassa.py.
Tests failure modes:
1. Missing or corrupted macro sections (Sections 1 to 6)
2. Missing items (e.g. B04, B13, B01)
3. Corrupted item headers (e.g. wrong heading level, misspelled IDs)
4. Missing item sub-dimensions (root cause, action plan, safety classification)
5. Section boundary isolation (keywords leakage between sections)
6. Bench checklist omission behavior in Section 6
"""

import io
import os
import re
import sys
import tempfile
import unittest
from pathlib import Path
from contextlib import redirect_stdout, redirect_stderr

# Add root directory to sys.path to import verify_plan_biomassa
WORKSPACE_DIR = Path(__file__).resolve().parents[3]
if str(WORKSPACE_DIR) not in sys.path:
    sys.path.insert(0, str(WORKSPACE_DIR))

import verify_plan_biomassa as vpb


class TestVerifyPlanBiomassaAdversarial(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.plan_path = WORKSPACE_DIR / "IMPLEMENTATION_PLAN_BIOMASSA.md"
        assert cls.plan_path.exists(), f"Target plan not found: {cls.plan_path}"
        cls.canonical_content = cls.plan_path.read_text(encoding="utf-8")

    def run_verifier_on_text(self, text: str) -> tuple[bool, str]:
        """Helper to write modified text to a temp file and run verify_plan."""
        with tempfile.NamedTemporaryFile("w", encoding="utf-8", suffix=".md", delete=False) as tf:
            tf.write(text)
            temp_name = tf.name

        try:
            f_out = io.StringIO()
            with redirect_stdout(f_out), redirect_stderr(f_out):
                success = vpb.verify_plan(Path(temp_name))
            return success, f_out.getvalue()
        finally:
            try:
                os.remove(temp_name)
            except OSError:
                pass

    # =========================================================================
    # Baseline Test
    # =========================================================================
    def test_canonical_plan_passes(self):
        """Confirm the canonical IMPLEMENTATION_PLAN_BIOMASSA.md passes verification."""
        success, out = self.run_verifier_on_text(self.canonical_content)
        self.assertTrue(success, "Canonical plan should pass verification.")
        self.assertIn("SUCESSO", out)
        self.assertIn("100.0%", out)

    # =========================================================================
    # File Existence and Size Tests
    # =========================================================================
    def test_missing_file_fails(self):
        """Verifier must return False if the specified file does not exist."""
        non_existent = WORKSPACE_DIR / "NON_EXISTENT_PLAN_FILE_XYZ.md"
        f_out = io.StringIO()
        with redirect_stdout(f_out), redirect_stderr(f_out):
            success = vpb.verify_plan(non_existent)
        self.assertFalse(success)
        self.assertIn("[FALHA CRÍTICA]", f_out.getvalue())

    def test_tiny_file_fails(self):
        """Verifier must return False if the plan is abnormally small (< 1000 bytes)."""
        tiny_content = "# Title\n\nShort placeholder text."
        success, out = self.run_verifier_on_text(tiny_content)
        self.assertFalse(success)
        self.assertIn("anormalmente pequeno", out)

    # =========================================================================
    # Macro Section Omission and Corruption Tests
    # =========================================================================
    def test_missing_each_macro_section(self):
        """Verifier must fail if ANY of the 6 required macro sections is missing."""
        sections = [
            ("1. Sumário Executivo", r"##\s+1\.\s+Sumário Executivo.*?(?=\n##|\Z)"),
            ("2. Arquitetura do Sistema", r"##\s+2\.\s+Arquitetura do Sistema.*?(?=\n##|\Z)"),
            ("3. Análise Técnica Detalhada", r"##\s+3\.\s+Análise Técnica Detalhada.*?(?=\n##|\Z)"),
            ("4. Matriz de Compatibilidade", r"##\s+4\.\s+Matriz de Compatibilidade.*?(?=\n##|\Z)"),
            ("5. Roteiro de Implementação em Fases", r"##\s+5\.\s+Roteiro de Implementação em Fases.*?(?=\n##|\Z)"),
            ("6. Critérios de Aceitação e Plano de Testes", r"##\s+6\.\s+Critérios de Aceitação e Plano de Testes.*?(?=\n##|\Z)"),
        ]

        for sec_name, pattern in sections:
            with self.subTest(section=sec_name):
                # Replace the section with a dummy comment, keeping length > 1000 bytes
                modified = re.sub(pattern, f"\n<!-- {sec_name} removed -->\n", self.canonical_content, flags=re.DOTALL)
                self.assertGreater(len(modified), 2000, "Content must remain above 1000 bytes")
                success, out = self.run_verifier_on_text(modified)
                self.assertFalse(success, f"Verifier should fail when macro section '{sec_name}' is missing.")
                self.assertIn("AUSENTE", out)

    def test_corrupted_macro_section_heading_level(self):
        """Verifier must fail if a macro section uses H3 (###) instead of H2 (##)."""
        # Change '## 1. Sumário Executivo' to '### 1. Sumário Executivo'
        modified = self.canonical_content.replace("## 1. Sumário Executivo", "### 1. Sumário Executivo")
        success, out = self.run_verifier_on_text(modified)
        self.assertFalse(success)
        self.assertIn("Seção '1. Sumário Executivo' AUSENTE", out)

    # =========================================================================
    # Item Omission Tests (B04, B13, B01, etc.)
    # =========================================================================
    def test_missing_item_B04_fails(self):
        """Verifier must fail if B04 (LED duty cycle / thermal bug) is missing."""
        # Remove ### B04 block up to next ### B05
        pattern = r"###\s+B04\b.*?(?=\n###\s+B05)"
        match = re.search(pattern, self.canonical_content, re.DOTALL)
        self.assertIsNotNone(match, "B04 section should exist in canonical plan")
        modified = re.sub(pattern, "", self.canonical_content, flags=re.DOTALL)

        success, out = self.run_verifier_on_text(modified)
        self.assertFalse(success, "Verifier must FAIL if B04 is removed.")
        self.assertIn("B04    | FAIL   | NÃO", out)

    def test_missing_item_B13_fails(self):
        """Verifier must fail if B13 (sentinel values -99.0 and 9.9) is missing."""
        pattern = r"###\s+B13\b.*?(?=\n###\s+B14)"
        match = re.search(pattern, self.canonical_content, re.DOTALL)
        self.assertIsNotNone(match, "B13 section should exist in canonical plan")
        modified = re.sub(pattern, "", self.canonical_content, flags=re.DOTALL)

        success, out = self.run_verifier_on_text(modified)
        self.assertFalse(success, "Verifier must FAIL if B13 is removed.")
        self.assertIn("B13    | FAIL   | NÃO", out)

    def test_missing_item_B01_fails(self):
        """Verifier must fail if B01 (dynamic presence window) is missing."""
        pattern = r"###\s+B01\b.*?(?=\n###\s+B02)"
        match = re.search(pattern, self.canonical_content, re.DOTALL)
        self.assertIsNotNone(match, "B01 section should exist in canonical plan")
        modified = re.sub(pattern, "", self.canonical_content, flags=re.DOTALL)

        success, out = self.run_verifier_on_text(modified)
        self.assertFalse(success, "Verifier must FAIL if B01 is removed.")
        self.assertIn("B01    | FAIL   | NÃO", out)

    def test_corrupted_item_header_syntax(self):
        """Verifier must fail if an item header is corrupted (e.g. H4 #### B04 or typo)."""
        modified = self.canonical_content.replace("### B04 —", "#### B04 —")
        success, out = self.run_verifier_on_text(modified)
        self.assertFalse(success)
        self.assertIn("B04    | FAIL   | NÃO", out)

        modified_typo = self.canonical_content.replace("### B13 —", "### B13_CORRUPTED —")
        # In regex rf"^###\s+{item_id}\b.*", B13_CORRUPTED has no word boundary after B13 if underscore is considered word char (\w includes _)
        success_typo, out_typo = self.run_verifier_on_text(modified_typo)
        self.assertFalse(success_typo)
        self.assertIn("B13    | FAIL   | NÃO", out_typo)

    # =========================================================================
    # Keyword Category Omission Tests (Root Cause, Action Plan, Safety)
    # =========================================================================
    def test_item_B04_missing_root_cause_fails(self):
        """Verifier must fail if B04 lacks root cause keywords."""
        title, text = vpb.extract_item_section(self.canonical_content, "B04")
        self.assertTrue(bool(text))
        # Strip all keywords_root
        stripped_text = text
        for kw in ["Causa Raiz", "Problema Declarado", "Localização Exata"]:
            stripped_text = re.sub(kw, "REDACTED_ROOT", stripped_text, flags=re.IGNORECASE)
        modified = self.canonical_content.replace(text, stripped_text)
        success, out = self.run_verifier_on_text(modified)
        self.assertFalse(success)
        self.assertIn("Falta causa raiz/localização", out)

    def test_item_B04_missing_action_plan_fails(self):
        """Verifier must fail if B04 lacks action plan keywords."""
        title, text = vpb.extract_item_section(self.canonical_content, "B04")
        self.assertTrue(bool(text))
        stripped_text = text
        for kw in ["Plano de Modificação", "Plano de Ação", "Justificativa", "pwmSetDutyPercent"]:
            stripped_text = re.sub(kw, "REDACTED_ACT", stripped_text, flags=re.IGNORECASE)
        modified = self.canonical_content.replace(text, stripped_text)
        success, out = self.run_verifier_on_text(modified)
        self.assertFalse(success)
        self.assertIn("Falta plano de ação/justificativa", out)

    def test_item_B04_missing_safety_fails(self):
        """Verifier must fail if B04 lacks safety classification keywords."""
        title, text = vpb.extract_item_section(self.canonical_content, "B04")
        self.assertTrue(bool(text))
        stripped_text = text
        for kw in ["Prioridade", "Classificação de Risco", "Segurança"]:
            stripped_text = re.sub(kw, "REDACTED_SAFE", stripped_text, flags=re.IGNORECASE)
        modified = self.canonical_content.replace(text, stripped_text)
        success, out = self.run_verifier_on_text(modified)
        self.assertFalse(success)
        self.assertIn("Falta classificação de segurança/prioridade", out)

    def test_item_B13_missing_action_plan_fails(self):
        """Verifier must fail if B13 lacks action plan keywords."""
        title, text = vpb.extract_item_section(self.canonical_content, "B13")
        self.assertTrue(bool(text))
        stripped_text = text
        for kw in ["Plano de Modificação", "Plano de Ação", "Justificativa", "SensorReadings"]:
            stripped_text = re.sub(kw, "REDACTED_ACT", stripped_text, flags=re.IGNORECASE)
        modified = self.canonical_content.replace(text, stripped_text)
        success, out = self.run_verifier_on_text(modified)
        self.assertFalse(success)
        self.assertIn("Falta plano de ação/justificativa", out)

    # =========================================================================
    # Section Boundary Isolation Stress Test
    # =========================================================================
    def test_section_isolation_prevents_leakage(self):
        """Keywords in subsequent sections (e.g. B05) must not leak into an empty B04."""
        # Replace B04 body with empty placeholder, leaving B05 intact with its keywords
        empty_b04 = "### B04 — LED Aceso Após set_gear em MEASURING\n\nEmpty content without keywords.\n\n"
        title, text = vpb.extract_item_section(self.canonical_content, "B04")
        modified = self.canonical_content.replace(text, empty_b04)
        success, out = self.run_verifier_on_text(modified)
        self.assertFalse(success, "Empty B04 should not inherit keywords from B05.")
        self.assertIn("B04    | FAIL", out)

    # =========================================================================
    # Bench Checklist Behavior Test (Surface Subtle Verification Behavior)
    # =========================================================================
    def test_bench_checklist_omission_warning(self):
        """
        Adversarial discovery:
        Removing the bench checklist emits a WARN, but does verify_plan return False or True?
        Testing whether verify_plan strictly enforces the bench checklist in exit code.
        """
        modified = re.sub(r"##\s+6\.\s+Critérios de Aceitação.*",
                          "## 6. Critérios de Aceitação e Plano de Testes\n\nAll tests pass.\n",
                          self.canonical_content, flags=re.DOTALL)
        success, out = self.run_verifier_on_text(modified)
        self.assertIn("[WARN] Checklist de homologação não localizado", out)
        # Note: In current verify_plan_biomassa.py, bench_match failure only triggers a warning
        # and all_items_passed and macro_errors == 0 still evaluates to True!
        # We record this finding empirically:
        self.assertTrue(success, "EMPIRICAL FINDING: Missing bench checklist only produces WARN; verify_plan still returns True.")


if __name__ == "__main__":
    unittest.main(verbosity=2)
