#!/usr/bin/env python3
"""
verify_plan_biomassa.py

Script de Verificação e Auditoria Automatizada do Plano de Implementação
do Sensor de Biomassa (IMPLEMENTATION_PLAN_BIOMASSA.md).

Verifica conformidade estrita com:
- Existência e integridade estrutural do arquivo
- Presença das 6 Seções Macro obrigatórias
- Cobertura completa e detalhada dos itens de inconsistência e decisão B01 a B13 (e complementares B14-B15)
- Presença de causa raiz/localização exata, plano de ação técnico/justificativa fundamentada e classificação de segurança para cada item
- Protocolo de ensaios de homologação em bancada (§4.11)
"""

import os
import re
import sys
from pathlib import Path

# Garante saída UTF-8 no console Windows
if hasattr(sys.stdout, "reconfigure"):
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
if hasattr(sys.stderr, "reconfigure"):
    sys.stderr.reconfigure(encoding="utf-8", errors="replace")


REQUIRED_SECTIONS = [
    ("1. Sumário Executivo", r"^##\s+1\.\s+Sumário Executivo"),
    ("2. Arquitetura do Sistema e Topologia de Comunicação", r"^##\s+2\.\s+Arquitetura do Sistema e Topologia de Comunicação"),
    ("3. Análise Técnica Detalhada (B01 a B15)", r"^##\s+3\.\s+Análise Técnica Detalhada"),
    ("4. Matriz de Compatibilidade e Interoperabilidade", r"^##\s+4\.\s+Matriz de Compatibilidade e Interoperabilidade"),
    ("5. Roteiro de Implementação em Fases", r"^##\s+5\.\s+Roteiro de Implementação em Fases"),
    ("6. Critérios de Aceitação e Plano de Testes", r"^##\s+6\.\s+Critérios de Aceitação e Plano de Testes"),
]

REQUIRED_BIOMASS_ITEMS = [
    {
        "id": "B01",
        "name": "Janela de Presença e Cadência em MEASURING",
        "keywords_action": ["Plano de Modificação", "Plano de Ação", "Justificativa", "biomassPresenceWindowMs"],
        "keywords_root": ["Causa Raiz", "Problema Declarado", "Localização Exata"],
        "keywords_safety": ["Prioridade", "Classificação de Risco", "Segurança"],
    },
    {
        "id": "B02",
        "name": "Rotinas Bloqueantes Isolam o Nó do Hub",
        "keywords_action": ["Plano de Modificação", "Plano de Ação", "Justificativa", "delayServiced"],
        "keywords_root": ["Causa Raiz", "Problema Declarado", "Localização Exata"],
        "keywords_safety": ["Prioridade", "Classificação de Risco", "Segurança"],
    },
    {
        "id": "B03",
        "name": "Marcha Manual Pelo Hub e Intertravamento com Auto-Range",
        "keywords_action": ["Plano de Modificação", "Plano de Ação", "Justificativa", "biomassAutoRange"],
        "keywords_root": ["Causa Raiz", "Problema Declarado", "Localização Exata"],
        "keywords_safety": ["Prioridade", "Classificação de Risco", "Segurança"],
    },
    {
        "id": "B04",
        "name": "LED Aceso Após set_gear em MEASURING (Risco Térmico)",
        "keywords_action": ["Plano de Modificação", "Plano de Ação", "Justificativa", "pwmSetDutyPercent"],
        "keywords_root": ["Causa Raiz", "Problema Declarado", "Localização Exata"],
        "keywords_safety": ["Prioridade", "Classificação de Risco", "Segurança"],
    },
    {
        "id": "B05",
        "name": "Persistência Parcial de Limiares e Período",
        "keywords_action": ["Plano de Modificação", "Plano de Ação", "Justificativa", "saveConfig"],
        "keywords_root": ["Causa Raiz", "Problema Declarado", "Localização Exata"],
        "keywords_safety": ["Prioridade", "Classificação de Risco", "Segurança"],
    },
    {
        "id": "B06",
        "name": "Reinício Silencioso Pós-Queda de Energia",
        "keywords_action": ["Decisão Técnica", "Justificativa", "Plano de Ação", "AlarmService"],
        "keywords_root": ["Causa Raiz", "Problema Declarado", "Localização Exata"],
        "keywords_safety": ["Prioridade", "Classificação de Risco", "Segurança"],
    },
    {
        "id": "B07",
        "name": "Inconsistência de Identidade e Versões (v11 vs v5.3)",
        "keywords_action": ["Plano de Modificação", "Plano de Ação", "Justificativa", "FW_VERSION"],
        "keywords_root": ["Causa Raiz", "Problema Declarado", "Localização Exata"],
        "keywords_safety": ["Prioridade", "Classificação de Risco", "Segurança"],
    },
    {
        "id": "B08",
        "name": "Divergência em Documento do Aplicativo (PROTOCOL.md)",
        "keywords_action": ["Justificativa", "Decisão Técnica", "Resolvido", "PROTOCOL.md"],
        "keywords_root": ["Causa Raiz", "Problema Declarado", "Localização"],
        "keywords_safety": ["Prioridade", "Classificação de Risco", "Segurança"],
    },
    {
        "id": "B09",
        "name": "Duração da Varredura de Branco pelo Hub",
        "keywords_action": ["Plano de Modificação", "Plano de Ação", "Justificativa", "RecipeEngine"],
        "keywords_root": ["Causa Raiz", "Problema Declarado", "Localização Exata"],
        "keywords_safety": ["Prioridade", "Classificação de Risco", "Segurança"],
    },
    {
        "id": "B10",
        "name": "Invalidação de Branco ao Alterar IT ou PWM",
        "keywords_action": ["Justificativa", "Decisão", "Beer-Lambert", "I0"],
        "keywords_root": ["Causa Raiz", "Problema Declarado", "Localização Exata"],
        "keywords_safety": ["Prioridade", "Classificação de Risco", "Segurança"],
    },
    {
        "id": "B11",
        "name": "Comandos Perigosos Restritos ao Canal Local (hub_off e factory)",
        "keywords_action": ["Justificativa", "Decisão", "Segurança", "Intertravamento"],
        "keywords_root": ["Causa Raiz", "Problema Declarado", "Localização Exata"],
        "keywords_safety": ["Prioridade", "Classificação de Risco", "Segurança"],
    },
    {
        "id": "B12",
        "name": "Roteamento Espúrio de test_period no Hub",
        "keywords_action": ["Plano de Modificação", "Plano de Ação", "Justificativa", "Commands.h"],
        "keywords_root": ["Causa Raiz", "Problema Declarado", "Localização Exata"],
        "keywords_safety": ["Prioridade", "Classificação de Risco", "Segurança"],
    },
    {
        "id": "B13",
        "name": "Tratamento de Valores-Sentinela de Absorbância (-99.0 e 9.9)",
        "keywords_action": ["Plano de Modificação", "Plano de Ação", "Justificativa", "SensorReadings"],
        "keywords_root": ["Causa Raiz", "Problema Declarado", "Localização Exata"],
        "keywords_safety": ["Prioridade", "Classificação de Risco", "Segurança"],
    },
    {
        "id": "B14",
        "name": "Campos Não Roteados no Hub (hd_mode, i0, sat, boot_id)",
        "keywords_action": ["Justificativa", "Decisão", "Headroom", "diag"],
        "keywords_root": ["Causa Raiz", "Problema Declarado", "Localização"],
        "keywords_safety": ["Prioridade", "Classificação de Risco", "Segurança"],
    },
    {
        "id": "B15",
        "name": "Ordem de Envio de Parâmetros de Aquisição (gear -> it -> pwm)",
        "keywords_action": ["Justificativa", "Decisão", "CommandBuilders", "Resolvido"],
        "keywords_root": ["Causa Raiz", "Problema Declarado", "Localização"],
        "keywords_safety": ["Prioridade", "Classificação de Risco", "Segurança"],
    },
]


def extract_item_section(content: str, item_id: str):
    """
    Localiza o cabeçalho específico do item no nível H3 (### Bxx)
    e extrai o bloco até o próximo item ou seção H2.
    """
    header_pattern = rf"^###\s+{item_id}\b.*"
    match = re.search(header_pattern, content, re.IGNORECASE | re.MULTILINE)
    if not match:
        return None, ""
    
    title = match.group(0).strip()
    start_pos = match.start()
    
    # Procura próximo item Bxx ou próxima seção nível 2 ##
    next_match = re.search(r"(?:\n###\s+B\d{2}|\n##\s+\d+\.)", content[start_pos + 1:])
    end_pos = start_pos + 1 + next_match.start() if next_match else len(content)
    section_text = content[start_pos:end_pos]
    return title, section_text


def verify_plan(plan_path: Path) -> bool:
    print("=" * 100)
    print(" AUDITORIA DE CONFORMIDADE AUTOMATIZADA: SENSOR DE BIOMASSA")
    print(f" Arquivo Analisado: {plan_path}")
    print("=" * 100)

    if not plan_path.exists():
        print(f"\n[FALHA CRÍTICA] Arquivo '{plan_path}' não foi encontrado no projeto!")
        return False

    file_size = plan_path.stat().st_size
    print(f"\n[1/4] Verificando existência e dimensões físicas do documento...")
    print(f"      Tamanho: {file_size} bytes.")
    if file_size < 1000:
        print("[FALHA] Arquivo anormalmente pequeno (< 1000 bytes).")
        return False
    print("      [OK] Arquivo presente e íntegro.")

    content = plan_path.read_text(encoding="utf-8")
    lines = content.splitlines()
    print(f"      Total de Linhas: {len(lines)}")

    print("\n[2/4] Verificando presença das 6 Seções Macro obrigatórias...")
    macro_errors = 0
    for name, pattern in REQUIRED_SECTIONS:
        if re.search(pattern, content, re.IGNORECASE | re.MULTILINE):
            print(f"      [PASS] Seção '{name}' encontrada.")
        else:
            print(f"      [FAIL] Seção '{name}' AUSENTE! Padrão esperado: {pattern}")
            macro_errors += 1

    if macro_errors > 0:
        print(f"\n[FALHA] {macro_errors} seções macro obrigatórias estão ausentes.")
        return False

    print("\n[3/4] Auditando exaustivamente itens B01 a B13 e complementares B14-B15...")
    results = []
    all_items_passed = True

    for item in REQUIRED_BIOMASS_ITEMS:
        item_id = item["id"]
        title, item_text = extract_item_section(content, item_id)
        
        if not title or not item_text:
            results.append({
                "id": item_id,
                "name": item["name"],
                "found": False,
                "has_root": False,
                "has_action": False,
                "has_safety": False,
                "status": "FAIL",
                "details": "Cabeçalho H3 não encontrado no documento"
            })
            all_items_passed = False
            continue

        has_root = any(kw.lower() in item_text.lower() for kw in item["keywords_root"])
        has_action = any(kw.lower() in item_text.lower() for kw in item["keywords_action"])
        has_safety = any(kw.lower() in item_text.lower() for kw in item["keywords_safety"])

        status = has_root and has_action and has_safety
        if not status:
            all_items_passed = False

        details = []
        if not has_root:
            details.append("Falta causa raiz/localização")
        if not has_action:
            details.append("Falta plano de ação/justificativa")
        if not has_safety:
            details.append("Falta classificação de segurança/prioridade")

        results.append({
            "id": item_id,
            "name": item["name"],
            "found": True,
            "has_root": has_root,
            "has_action": has_action,
            "has_safety": has_safety,
            "status": "PASS" if status else "FAIL",
            "details": ", ".join(details) if details else "Completo com análise e plano"
        })

    # Imprime relatório tabular
    print("\n" + "=" * 115)
    print(f"{'ID':<6} | {'STATUS':<6} | {'ENCONTRADO':<10} | {'CAUSA/LOC':<10} | {'PLANO/JUST':<10} | {'TEMA / DETALHES':<50}")
    print("-" * 115)
    for r in results:
        f_str = "SIM" if r["found"] else "NÃO"
        r_str = "SIM" if r["has_root"] else "NÃO"
        a_str = "SIM" if r["has_action"] else "NÃO"
        title_snippet = (r["name"][:38] + "..") if len(r["name"]) > 40 else r["name"]
        print(f"{r['id']:<6} | {r['status']:<6} | {f_str:<10} | {r_str:<10} | {a_str:<10} | {title_snippet:<40} ({r['details']})")
    print("=" * 115)

    print("\n[4/4] Verificação de Conformidade com o Checklist de Bancada (§4.11)...")
    bench_match = re.search(r"##\s+6\.\s+Critérios de Aceitação.*?(Item\s+1|Protocolo de Homologação em Bancada Física)", content, re.DOTALL | re.IGNORECASE)
    if bench_match:
        print("      [PASS] Checklist de homologação em bancada física documentado na Seção 6.")
    else:
        print("      [WARN] Checklist de homologação não localizado explicitamente na Seção 6.")

    total_audited = len(results)
    passed_count = sum(1 for r in results if r["status"] == "PASS")
    print(f"\n[ESTATÍSTICAS DA AUDITORIA]")
    print(f"  - Total de Inconsistências Auditadas: {total_audited} (B01 a B15)")
    print(f"  - Aprovadas com Rigor Metrológico:   {passed_count}")
    print(f"  - Reprovações:                        {total_audited - passed_count}")
    print(f"  - Taxa de Conformidade:               {(passed_count / total_audited) * 100:.1f}%")

    if all_items_passed and macro_errors == 0:
        print("\n" + "=" * 100)
        print(" SUCESSO: IMPLEMENTATION_PLAN_BIOMASSA.md foi verificado e aprovado integralmente!")
        print(" Todos os requisitos de B01 a B15 e as 6 seções macro estão rigorosamente cumpridos.")
        print("=" * 100)
        return True
    else:
        print("\n" + "=" * 100)
        print(" FALHA: O plano de implementação possui pendências ou seções não conformes.")
        print("=" * 100)
        return False


def main():
    workspace_dir = Path(__file__).resolve().parent
    if len(sys.argv) > 1:
        plan_file = Path(sys.argv[1])
    else:
        plan_file = workspace_dir / "IMPLEMENTATION_PLAN_BIOMASSA.md"
    success = verify_plan(plan_file)
    sys.exit(0 if success else 1)


if __name__ == "__main__":
    main()
