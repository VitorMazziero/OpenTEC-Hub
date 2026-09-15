#!/usr/bin/env python3
"""
stress_test_bomba_verifier.py

Adversarial Stress-Testing Suite for verify_plan_bomba.py and IMPLEMENTATION_PLAN_BOMBA.md.
Tests edge cases, mutations, omissions, malformed headers, and codebase grounding.
"""

import os
import re
import sys
from pathlib import Path

WORKSPACE_ROOT = Path(__file__).resolve().parent.parent.parent
sys.path.insert(0, str(WORKSPACE_ROOT))

try:
    import verify_plan_bomba
except ImportError as e:
    print(f"[FATAL] Failed to import verify_plan_bomba: {e}")
    sys.exit(1)


def run_test_suite():
    genuine_plan_path = WORKSPACE_ROOT / "IMPLEMENTATION_PLAN_BOMBA.md"
    assert genuine_plan_path.exists(), f"Plan not found at {genuine_plan_path}"
    genuine_content = genuine_plan_path.read_text(encoding="utf-8")

    dummy_dir = Path(__file__).resolve().parent / "dummy_test_plans"
    dummy_dir.mkdir(exist_ok=True)

    passed_tests = 0
    failed_tests = 0
    total_tests = 0

    def record_result(name: str, condition: bool, details: str = ""):
        nonlocal passed_tests, failed_tests, total_tests
        total_tests += 1
        if condition:
            passed_tests += 1
            print(f"  [PASS] {name} {details}")
        else:
            failed_tests += 1
            print(f"  [FAIL] {name} {details}")

    print("=" * 80)
    print(" SUÍTE DE TESTES ADVERSARIAIS: verify_plan_bomba.py")
    print("=" * 80)

    # 1. Teste de Linha de Base: Plano Genuíno
    print("\n[-] 1. Testando plano genuíno:")
    baseline_result = verify_plan_bomba.verify_plan(genuine_plan_path)
    record_result("Plano genuíno retorna True", baseline_result is True)

    # 2. Testes de Arquivo Inexistente e Vazio
    print("\n[-] 2. Testando resiliência a arquivos inválidos/vazios:")
    non_existent = dummy_dir / "non_existent.md"
    if non_existent.exists():
        non_existent.unlink()
    record_result("Arquivo inexistente retorna False", verify_plan_bomba.verify_plan(non_existent) is False)

    empty_file = dummy_dir / "empty.md"
    empty_file.write_text("", encoding="utf-8")
    record_result("Arquivo vazio retorna False", verify_plan_bomba.verify_plan(empty_file) is False)

    # 3. Teste Adversarial: Detecção de ausência de itens em §1.10
    print("\n[-] 3. Testando detecção de itens ausentes em §1.10:")
    for item in verify_plan_bomba.SECTION_110_ITEMS:
        item_id = item["id"]
        pattern = item["header_pattern"]
        match = re.search(pattern, genuine_content, re.IGNORECASE)
        if not match:
            record_result(f"Remover §1.10 {item_id}", False, "Header não encontrado no original")
            continue

        subsequent = genuine_content[match.end():]
        next_header = re.search(r"\n#{1,4}\s+", subsequent)
        cut_end = match.end() + next_header.start() if next_header else len(genuine_content)

        mutated_content = genuine_content[:match.start()] + "\n<!-- Item removed -->\n" + genuine_content[cut_end:]
        dummy_file = dummy_dir / f"missing_110_{item_id}.md"
        dummy_file.write_text(mutated_content, encoding="utf-8")

        res = verify_plan_bomba.verify_plan(dummy_file)
        # Note: if res is True, verify_plan_bomba failed to detect the missing item!
        detected = (res is False)
        record_result(
            f"Detecção de omissão de §1.10 {item_id} ({item['name'][:25]}..)",
            detected,
            "CORRETAMENTE DETECTADO" if detected else "FALHA GRAVE: VERIFIER RETORNOU SUCESSO MESMO COM ITEM AUSENTE!"
        )
        dummy_file.unlink(missing_ok=True)

    # 4. Teste Adversarial: Detecção de ausência de itens em §1.11
    print("\n[-] 4. Testando detecção de itens ausentes em §1.11:")
    for item in verify_plan_bomba.SECTION_111_ITEMS:
        item_id = item["id"]
        pattern = item["header_pattern"]
        match = re.search(pattern, genuine_content, re.IGNORECASE)
        if not match:
            record_result(f"Remover §1.11 {item_id}", False, "Header não encontrado no original")
            continue

        subsequent = genuine_content[match.end():]
        next_header = re.search(r"\n#{1,3}\s+", subsequent)
        cut_end = match.end() + next_header.start() if next_header else len(genuine_content)

        mutated_content = genuine_content[:match.start()] + "\n<!-- Item removed -->\n" + genuine_content[cut_end:]
        dummy_file = dummy_dir / f"missing_111_{item_id}.md"
        dummy_file.write_text(mutated_content, encoding="utf-8")

        res = verify_plan_bomba.verify_plan(dummy_file)
        detected = (res is False)
        record_result(
            f"Detecção de omissão de §1.11 {item_id} ({item['name'][:25]}..)",
            detected,
            "CORRETAMENTE DETECTADO" if detected else "FALHA GRAVE: VERIFIER RETORNOU SUCESSO MESMO COM ITEM AUSENTE!"
        )
        dummy_file.unlink(missing_ok=True)

    # 5. Teste Adversarial: Cabeçalho presente mas SEM Plano de Ação ou Justificativa (§1.10)
    print("\n[-] 5. Testando detecção de corpo oco (sem keywords) em §1.10:")
    for item in verify_plan_bomba.SECTION_110_ITEMS[:4]:
        item_id = item["id"]
        pattern = item["header_pattern"]
        match = re.search(pattern, genuine_content, re.IGNORECASE)
        subsequent = genuine_content[match.end():]
        next_header = re.search(r"\n#{1,4}\s+", subsequent)
        cut_end = match.end() + next_header.start() if next_header else len(genuine_content)

        hollow_body = "\n- Status: Incompleto.\n- Descrição: O problema existe mas nada foi providenciado aqui.\n"
        mutated_content = genuine_content[:match.end()] + hollow_body + genuine_content[cut_end:]
        dummy_file = dummy_dir / f"hollow_110_{item_id}.md"
        dummy_file.write_text(mutated_content, encoding="utf-8")

        res = verify_plan_bomba.verify_plan(dummy_file)
        detected = (res is False)
        record_result(f"Corpo oco (sem plano/justificativa) em §1.10 {item_id}", detected)
        dummy_file.unlink(missing_ok=True)

    # 6. Teste Adversarial: Critérios insuficientes em §1.11 (< 2 keywords)
    print("\n[-] 6. Testando detecção de critérios insuficientes em §1.11:")
    for item in verify_plan_bomba.SECTION_111_ITEMS[:4]:
        item_id = item["id"]
        pattern = item["header_pattern"]
        match = re.search(pattern, genuine_content, re.IGNORECASE)
        subsequent = genuine_content[match.end():]
        next_header = re.search(r"\n#{1,3}\s+", subsequent)
        cut_end = match.end() + next_header.start() if next_header else len(genuine_content)

        # Apenas texto neutro sem nenhuma keyword de bancada
        sparse_body = "\n- Descrição resumida do procedimento de teste de bancada.\n"
        mutated_content = genuine_content[:match.end()] + sparse_body + genuine_content[cut_end:]
        dummy_file = dummy_dir / f"sparse_111_{item_id}.md"
        dummy_file.write_text(mutated_content, encoding="utf-8")

        res = verify_plan_bomba.verify_plan(dummy_file)
        detected = (res is False)
        record_result(f"Critérios insuficientes em §1.11 {item_id}", detected)
        dummy_file.unlink(missing_ok=True)

    # 7. Teste de Suporte a sys.argv no CLI
    print("\n[-] 7. Testando suporte a argumentos CLI (sys.argv):")
    import subprocess
    empty_path = dummy_dir / "cli_empty.md"
    empty_path.write_text("", encoding="utf-8")
    proc = subprocess.run([sys.executable, str(WORKSPACE_ROOT / "verify_plan_bomba.py"), str(empty_path)], capture_output=True, text=True)
    # Se main() aceitasse sys.argv[1], deveria falhar com exit code 1. Como ignora sys.argv, roda o original e sai 0!
    cli_handles_arg = (proc.returncode == 1)
    record_result(
        "CLI 'python verify_plan_bomba.py <file>' respeita o argumento",
        cli_handles_arg,
        "PASS" if cli_handles_arg else "FALHA: main() ignora sys.argv[1] e sempre avalia IMPLEMENTATION_PLAN_BOMBA.md"
    )
    empty_path.unlink(missing_ok=True)

    # Limpeza
    try:
        empty_file.unlink(missing_ok=True)
        dummy_dir.rmdir()
    except Exception:
        pass

    # 8. Verificação de Aterramento no Código-Fonte e Placeholders
    print("\n[-] 8. Verificação de Aterramento no Repositório (Grounding):")
    file_refs = set(re.findall(r"[`'\"]([a-zA-Z0-9_\-\.\/]+\.(?:h|cpp|dart|cs|py|json|md))[`'\"]", genuine_content))
    verified_files = 0
    missing_files = []
    proposed_new_files = ["Android_app/lib/screens/pump_calibration_screen.dart"]
    for ref in file_refs:
        if ref in proposed_new_files:
            continue
        p = WORKSPACE_ROOT / ref
        if p.exists():
            verified_files += 1
        else:
            matches = list(WORKSPACE_ROOT.glob(f"**/{Path(ref).name}"))
            if matches:
                verified_files += 1
            else:
                missing_files.append(ref)

    checked_total = len(file_refs) - len(proposed_new_files)
    record_result(
        f"Aterramento de arquivos citados ({verified_files}/{checked_total} existentes)",
        len(missing_files) == 0,
        f"Ausentes: {missing_files}" if missing_files else "Todos os arquivos existentes conferidos no repositório"
    )

    placeholders = re.findall(r"\b(TODO|FIXME|TBD|XXX|WIP)\b", genuine_content)
    record_result("Ausência de placeholders (TODO, FIXME, TBD, XXX, WIP)", len(placeholders) == 0, f"Encontrados: {placeholders}")

    print("\n" + "=" * 80)
    print(f" RESUMO DOS TESTES ADVERSARIAIS: {passed_tests}/{total_tests} PASSARAM ({failed_tests} FALHAS DETECTADAS)")
    print("=" * 80)

    return failed_tests == 0


if __name__ == "__main__":
    success = run_test_suite()
    sys.exit(0 if success else 1)
