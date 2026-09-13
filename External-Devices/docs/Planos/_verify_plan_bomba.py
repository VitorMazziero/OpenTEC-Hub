#!/usr/bin/env python3
"""
verify_plan_bomba.py

Script de Verificação e Auditoria Automatizada do Plano de Implementação
da Bomba Peristáltica Externa (IMPLEMENTATION_PLAN_BOMBA.md).

Verifica conformidade estrita com os itens de auditoria (§1.10) e ensaios
de bancada física (§1.11) de COMANDOS_DISPOSITIVOS_EXTERNOS.md.
"""

import os
import re
import sys
from pathlib import Path

# Garante saída UTF-8 no console Windows
if hasattr(sys.stdout, "reconfigure"):
    sys.stdout.reconfigure(encoding="utf-8")


# Definição dos itens obrigatórios de §1.10 (12 itens + 4 decisões arquiteturais fechadas)
SECTION_110_ITEMS = [
    {
        "id": "1.10.1",
        "header_pattern": r"###\s+Item\s+1\.10\.1\b",
        "name": "Estimativa de Volume e Vazão por Curva de Calibração",
        "alt_tags": ["F-110-01", "1.10 #1"],
        "plan_keywords": ["Plano de Ação Técnico", "Plano Técnico", "Justificativa Técnica"],
    },
    {
        "id": "1.10.2",
        "header_pattern": r"###\s+Item\s+1\.10\.2\b",
        "name": "Preservação de Volume Acumulado no Comando de Parada e mode: 0",
        "alt_tags": ["F-110-02", "1.10 #2"],
        "plan_keywords": ["Plano de Ação Técnico", "Plano Técnico", "Justificativa Técnica"],
    },
    {
        "id": "1.10.3",
        "header_pattern": r"###\s+Item\s+1\.10\.3\b",
        "name": "Arbitragem e Desbloqueio dos Potenciômetros Físicos (pot: 1/0)",
        "alt_tags": ["F-110-03", "1.10 #3"],
        "plan_keywords": ["Plano de Ação Técnico", "Plano Técnico", "Justificativa Técnica"],
    },
    {
        "id": "1.10.4",
        "header_pattern": r"###\s+Item\s+1\.10\.4\b",
        "name": "Temporização de Velocidade Manual e Parada Autônoma (speed_ms)",
        "alt_tags": ["F-110-04", "1.10 #4"],
        "plan_keywords": ["Plano de Ação Técnico", "Plano Técnico", "Justificativa Técnica"],
    },
    {
        "id": "1.10.5",
        "header_pattern": r"###\s+Item\s+1\.10\.5\b",
        "name": "Proteção de Memória Flash NVS no Hub e Filtragem de Comandos Críticos",
        "alt_tags": ["F-110-05", "1.10 #5"],
        "plan_keywords": ["Plano de Ação Técnico", "Plano Técnico", "Justificativa Técnica"],
    },
    {
        "id": "1.10.6",
        "header_pattern": r"###\s+Item\s+1\.10\.6\b",
        "name": "Retomada Autônoma de Perfil Pós-Queda de Energia (Checkpoint NVS)",
        "alt_tags": ["F-110-06", "1.10 #6"],
        "plan_keywords": ["Plano de Ação Técnico", "Plano Técnico", "Justificativa Técnica"],
    },
    {
        "id": "1.10.7",
        "header_pattern": r"###\s+Item\s+1\.10\.7\b",
        "name": "Porta do Sensor de Presença de Líquido no Pino 15 Invisível ao App",
        "alt_tags": ["F-110-07", "1.10 #7"],
        "plan_keywords": ["Plano de Ação Técnico", "Plano Técnico", "Justificativa Técnica"],
    },
    {
        "id": "1.10.8",
        "header_pattern": r"###\s+Item\s+1\.10\.8\b",
        "name": "Eco Bidirecional e Ajuste Dinâmico de PID de Volume",
        "alt_tags": ["F-110-08", "1.10 #8"],
        "plan_keywords": ["Plano de Ação Técnico", "Plano Técnico", "Justificativa Técnica"],
    },
    {
        "id": "1.10.9",
        "header_pattern": r"###\s+Item\s+1\.10\.9\b",
        "name": "Latência de Comunicação (0–2 s) e Semântica Latest-Wins na pumpBox",
        "alt_tags": ["F-110-09", "1.10 #9"],
        "plan_keywords": ["Plano de Ação Técnico", "Plano Técnico", "Justificativa Técnica"],
    },
    {
        "id": "1.10.10",
        "header_pattern": r"###\s+Item\s+1\.10\.10\b",
        "name": "Comportamento de Ativação Ociosa com Comando start e mode=0",
        "alt_tags": ["F-110-10", "1.10 #10"],
        "plan_keywords": ["Plano de Ação Técnico", "Plano Técnico", "Justificativa Técnica"],
    },
    {
        "id": "1.10.11",
        "header_pattern": r"###\s+Item\s+1\.10\.11\b",
        "name": "Tipologia do Atuador Mecânico: Motor DC Escovado em Ponte H vs Stepper",
        "alt_tags": ["F-110-11", "1.10 #11"],
        "plan_keywords": ["Plano de Ação Técnico", "Plano Técnico", "Justificativa Técnica"],
    },
    {
        "id": "1.10.12",
        "header_pattern": r"###\s+Item\s+1\.10\.12\b",
        "name": "Potenciômetro Gain como Sentido e Escalonamento Bipolar Centrado",
        "alt_tags": ["F-110-12", "1.10 #12"],
        "plan_keywords": ["Plano de Ação Técnico", "Plano Técnico", "Justificativa Técnica"],
    },
    {
        "id": "D-SEC-01",
        "header_pattern": r"####\s+D-SEC-01\b",
        "name": "Diretriz de Calibração: Curva Linear (R² >= 0,98) vs Lookup Table",
        "alt_tags": ["D-SEC-01"],
        "plan_keywords": ["Justificativa Técnica", "Plano de Ação Técnico"],
    },
    {
        "id": "D-SEC-02",
        "header_pattern": r"####\s+D-SEC-02\b",
        "name": "Intertravamento de Segurança Local por Presença de Líquido",
        "alt_tags": ["D-SEC-02"],
        "plan_keywords": ["Justificativa Técnica", "Plano de Ação Técnico"],
    },
    {
        "id": "D-SEC-03",
        "header_pattern": r"####\s+D-SEC-03\b",
        "name": "Semeadura Pseudoaleatória de cmd_id Pós-Reboot no Hub",
        "alt_tags": ["D-SEC-03"],
        "plan_keywords": ["Justificativa Técnica", "Plano de Ação Técnico"],
    },
    {
        "id": "D-SEC-04",
        "header_pattern": r"####\s+D-SEC-04\b",
        "name": "Resiliência Numérica e Proteção contra Divisão por Zero com Slope Nulo",
        "alt_tags": ["D-SEC-04"],
        "plan_keywords": ["Justificativa Técnica", "Plano de Ação Técnico"],
    },
]

# Definição dos itens obrigatórios de §1.11 (Checklist de Bancada Física: 11 procedimentos)
SECTION_111_ITEMS = [
    {
        "id": "1.11.1",
        "header_pattern": r"###\s+Item\s+1\.11\.1\b",
        "name": "Conferência do Sentido Físico de Rotação (Fluxo para o Vaso)",
        "alt_tags": ["CHK-111-01", "1.11 #1"],
        "bench_keywords": ["Pré-requisitos", "Protocolo", "Critérios de Aceitação", "Pass/Fail"],
    },
    {
        "id": "1.11.2",
        "header_pattern": r"###\s+Item\s+1\.11\.2\b",
        "name": "Tempo de Resposta e Detecção de Queda de Presença (<= 4 s)",
        "alt_tags": ["CHK-111-02", "1.11 #2"],
        "bench_keywords": ["Pré-requisitos", "Protocolo", "Critérios de Aceitação", "Pass/Fail"],
    },
    {
        "id": "1.11.3",
        "header_pattern": r"###\s+Item\s+1\.11\.3\b",
        "name": "Calibração Volumétrica Assistida Multiponto e Verificação Contínua de 10 min",
        "alt_tags": ["CHK-111-03", "1.11 #3"],
        "bench_keywords": ["Pré-requisitos", "Protocolo", "Critérios de Aceitação", "Pass/Fail"],
    },
    {
        "id": "1.11.4",
        "header_pattern": r"###\s+Item\s+1\.11\.4\b",
        "name": "Liberação e Comutação dos Potenciômetros de Bancada (pump_pot: 1/0)",
        "alt_tags": ["CHK-111-04", "1.11 #4"],
        "bench_keywords": ["Pré-requisitos", "Protocolo", "Critérios de Aceitação", "Pass/Fail"],
    },
    {
        "id": "1.11.5",
        "header_pattern": r"###\s+Item\s+1\.11\.5\b",
        "name": "Corte Autônomo de Emergência por Timeout (pump_speed_ms)",
        "alt_tags": ["CHK-111-05", "1.11 #5"],
        "bench_keywords": ["Pré-requisitos", "Protocolo", "Critérios de Aceitação", "Pass/Fail"],
    },
    {
        "id": "1.11.6",
        "header_pattern": r"###\s+Item\s+1\.11\.6\b",
        "name": "Parada Autônoma por final_t, Retenção de PumpVol e Apuração de PumpCycleVol",
        "alt_tags": ["CHK-111-06", "1.11 #6"],
        "bench_keywords": ["Pré-requisitos", "Protocolo", "Critérios de Aceitação", "Pass/Fail"],
    },
    {
        "id": "1.11.7",
        "header_pattern": r"###\s+Item\s+1\.11\.7\b",
        "name": "Execução Sequencial de Múltiplos Perfis sem Zeramento de Volume",
        "alt_tags": ["CHK-111-07", "1.11 #7"],
        "bench_keywords": ["Pré-requisitos", "Protocolo", "Critérios de Aceitação", "Pass/Fail"],
    },
    {
        "id": "1.11.8",
        "header_pattern": r"###\s+Item\s+1\.11\.8\b",
        "name": "Transmissão, Eco e Retenção NVS dos Ganhos de PID",
        "alt_tags": ["CHK-111-08", "1.11 #8"],
        "bench_keywords": ["Pré-requisitos", "Protocolo", "Critérios de Aceitação", "Pass/Fail"],
    },
    {
        "id": "1.11.9",
        "header_pattern": r"###\s+Item\s+1\.11\.9\b",
        "name": "Ensaio de Retomada Autônoma Pós-Queda de Alimentação (Robust Recovery)",
        "alt_tags": ["CHK-111-09", "1.11 #9"],
        "bench_keywords": ["Pré-requisitos", "Protocolo", "Critérios de Aceitação", "Pass/Fail"],
    },
    {
        "id": "1.11.10",
        "header_pattern": r"###\s+Item\s+1\.11\.10\b",
        "name": "Injeção de Comando Pós-Reboot do Hub (Semeadura de cmd_id)",
        "alt_tags": ["CHK-111-10", "1.11 #10"],
        "bench_keywords": ["Pré-requisitos", "Protocolo", "Critérios de Aceitação", "Pass/Fail"],
    },
    {
        "id": "1.11.11",
        "header_pattern": r"###\s+Item\s+1\.11\.11\b",
        "name": "Resposta do Sensor de Presença de Líquido e Travamento de Bancada",
        "alt_tags": ["CHK-111-11", "1.11 #11"],
        "bench_keywords": ["Pré-requisitos", "Protocolo", "Critérios de Aceitação", "Pass/Fail"],
    },
]


def extract_section_content(text: str, header_pattern: str) -> str:
    """Localiza o cabeçalho e extrai o corpo da seção até o próximo cabeçalho equivalente,
    ignorando linhas dentro de blocos de código (code fences ``` ou ~~~).
    O corpo retornado inicia estritamente após a quebra de linha do cabeçalho.
    """
    match = re.search(header_pattern, text, re.IGNORECASE)
    if not match:
        return ""
    start_pos = match.start()
    newline_pos = text.find("\n", start_pos)
    if newline_pos == -1:
        return ""

    header_line = text[start_pos:newline_pos]
    level_match = re.match(r"^(#+)", header_line.strip())
    header_level = len(level_match.group(1)) if level_match else 3

    # O corpo da seção começa estritamente na linha seguinte ao cabeçalho (isolando a linha de título)
    body_start = newline_pos + 1
    subsequent_text = text[body_start:]

    header_stop_regex = re.compile(rf"^\s*#{{1,{max(header_level, 4)}}}\s+")
    in_code_block = False
    body_lines = []

    for line in subsequent_text.splitlines(keepends=True):
        stripped = line.strip()
        if stripped.startswith("```") or stripped.startswith("~~~"):
            in_code_block = not in_code_block
            body_lines.append(line)
            continue

        if not in_code_block and header_stop_regex.match(line):
            break

        body_lines.append(line)

    return "".join(body_lines)


def verify_plan(plan_path: Path) -> bool:
    print(f"=" * 90)
    print(f" AUDITORIA DE CONFORMIDADE: IMPLEMENTATION_PLAN_BOMBA.md")
    print(f" Arquivo Alvo: {plan_path}")
    print(f"=" * 90)

    if not plan_path.exists():
        print(f"\n[ERRO CRÍTICO] Arquivo '{plan_path}' não encontrado!")
        return False

    content = plan_path.read_text(encoding="utf-8")
    total_chars = len(content)
    total_lines = len(content.splitlines())
    print(f" Documento carregado: {total_lines} linhas, {total_chars} caracteres.\n")

    results = []
    all_passed = True

    # 1. Verificação dos Itens de §1.10
    print(f"[-] Auditando Seção 1.10 (Limitações, Riscos e Decisões de Engenharia)...")
    for item in SECTION_110_ITEMS:
        item_id = item["id"]
        header_exists = bool(re.search(item["header_pattern"], content, re.IGNORECASE))
        sec_body = extract_section_content(content, item["header_pattern"])
        found = header_exists
        has_substance = len(sec_body.strip()) >= 50

        # Checa se possui plano de ação ou justificativa técnica estritamente no corpo
        plan_or_justification = False
        details = []
        if not found:
            details.append("Seção não localizada")
        elif not has_substance:
            details.append(f"Corpo insuficiente ({len(sec_body.strip())} chars < 50)")
        else:
            for kw in item["plan_keywords"]:
                if kw.lower() in sec_body.lower():
                    plan_or_justification = True
                    details.append(kw)
            if not plan_or_justification:
                # Checa se menciona justificativa/plano em lista estruturada
                if re.search(r"-\s+\*\*(?:Plano|Justificativa)", sec_body, re.IGNORECASE):
                    plan_or_justification = True
                    details.append("Racional estruturado presente")

        status = found and has_substance and plan_or_justification
        if not status:
            all_passed = False

        results.append({
            "section": "1.10",
            "id": item_id,
            "name": item["name"],
            "found": found,
            "content_valid": has_substance and plan_or_justification,
            "status": "PASS" if status else "FAIL",
            "details": ", ".join(details) if details else "Sem plano/justificativa"
        })

    # 2. Verificação dos Itens de §1.11
    print(f"[-] Auditando Seção 1.11 (Checklist de Homologação em Bancada Física)...")
    for item in SECTION_111_ITEMS:
        item_id = item["id"]
        header_exists = bool(re.search(item["header_pattern"], content, re.IGNORECASE))
        sec_body = extract_section_content(content, item["header_pattern"])
        found = header_exists
        has_substance = len(sec_body.strip()) >= 50

        # Checa critérios de bancada (Pass/Fail, Protocolo, Pré-requisitos) estritamente no corpo
        bench_valid = False
        details = []
        if not found:
            details.append("Seção não localizada")
        elif not has_substance:
            details.append(f"Corpo insuficiente ({len(sec_body.strip())} chars < 50)")
        else:
            matched_kws = [kw for kw in item["bench_keywords"] if kw.lower() in sec_body.lower()]
            if len(matched_kws) >= 2:
                bench_valid = True
                details.append(f"{len(matched_kws)} critérios atendidos")
            else:
                details.append(f"Apenas {len(matched_kws)} critérios: {', '.join(matched_kws)}")

        status = found and has_substance and bench_valid
        if not status:
            all_passed = False

        results.append({
            "section": "1.11",
            "id": item_id,
            "name": item["name"],
            "found": found,
            "content_valid": has_substance and bench_valid,
            "status": "PASS" if status else "FAIL",
            "details": ", ".join(details)
        })

    # Exibição do Relatório Tabular
    print("\n" + "=" * 115)
    print(f"{'SEC':<6} | {'ID':<10} | {'STATUS':<6} | {'SEÇÃO ENCONTRADA':<16} | {'CONTEÚDO/PLANO':<16} | {'TEMA / DETALHES':<48}")
    print("-" * 115)
    for r in results:
        f_str = "SIM" if r["found"] else "NÃO"
        c_str = "SIM" if r["content_valid"] else "NÃO"
        title_snippet = (r["name"][:32] + "..") if len(r["name"]) > 34 else r["name"]
        print(f"{r['section']:<6} | {r['id']:<10} | {r['status']:<6} | {f_str:<16} | {c_str:<16} | {title_snippet:<34} ({r['details']})")
    print("=" * 115)

    # Estatísticas Finais
    total_items = len(results)
    passed_items = sum(1 for r in results if r["status"] == "PASS")
    failed_items = total_items - passed_items

    print(f"\n[ESTATÍSTICAS GERAIS]")
    print(f"  - Total de Itens Auditados: {total_items} (16 em §1.10 e 11 em §1.11)")
    print(f"  - Aprovados (PASS):         {passed_items}")
    print(f"  - Reprovados (FAIL):        {failed_items}")
    print(f"  - Taxa de Conformidade:     {(passed_items / total_items) * 100:.1f}%")

    if all_passed:
        print("\n[RESULTADO DA AUDITORIA]: SUCESSO ABSOLUTO (Exit Code 0)")
        print("Todos os requisitos de cobertura de §1.10 e §1.11 foram rigorosamente atendidos.")
        return True
    else:
        print("\n[RESULTADO DA AUDITORIA]: FALHA DE CONFORMIDADE (Exit Code 1)")
        print("Existem itens obrigatórios ausentes ou com conteúdo insuficiente.")
        return False


def main():
    workspace_dir = Path(__file__).resolve().parent
    if len(sys.argv) > 1:
        plan_file = Path(sys.argv[1])
    else:
        plan_file = workspace_dir / "IMPLEMENTATION_PLAN_BOMBA.md"
    success = verify_plan(plan_file)
    sys.exit(0 if success else 1)


if __name__ == "__main__":
    main()
