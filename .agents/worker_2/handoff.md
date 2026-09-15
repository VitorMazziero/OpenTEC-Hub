# Handoff Report: Endurecimento e Correção de Vulnerabilidades em `verify_plan_bomba.py`

**Data:** 2026-09-13  
**Autor:** Worker 2 (`.agents/worker_2/`)  
**Parent Agent:** `a109a27d-47d4-4806-8071-687fa2f0b968`  
**Alvo:** `verify_plan_bomba.py` e `IMPLEMENTATION_PLAN_BOMBA.md`  
**Veredito:** `RESOLVIDO E HOMOLOGADO (PASS)`

---

## 1. Observation

### 1.1 Diagnóstico Inicial das Vulnerabilidades Reportadas
Foram analisados os relatórios em `.agents/challenger_1/handoff.md` e `.agents/challenger_2/handoff.md`, confirmando 4 vulnerabilidades no script `verify_plan_bomba.py` original:

1. **Colisão de Prefixo em Regex (Challenger 1, Linhas 27-68):**
   - No código original:
     ```python
     "header_pattern": r"###\s+Item\s+1\.10\.1",
     "header_pattern": r"###\s+Item\s+1\.11\.1",
     ```
   - Como `1.10.1` é prefixo literal de `1.10.10`, `1.10.11`, `1.10.12`, a exclusão total do Item 1.10.1 casava com o Item 1.10.10, aprovando o plano com 100.0% e Exit Code 0. O mesmo ocorria com `1.11.1` colidindo com `1.11.10` e `1.11.11`.
2. **Bypass por Cabeçalho Vazio (Challenger 2, Linhas 43-77):**
   - `extract_section_content` realizava:
     ```python
     subsequent_text = text[match.end():]
     ```
   - O `match.end()` ficava logo após o número do item, mantendo o restante da linha de cabeçalho (o título, ex: `: Plano de Ação Técnico\n`) no texto da seção. Se a próxima linha contivesse outro cabeçalho, um documento contendo apenas cabeçalhos vazios aprovava todos os 27 itens com Exit Code 0.
3. **Truncamento Prematuro por Comentários em Blocos de Código (Challenger 2, Linhas 78-81):**
   - O padrão `next_header_pattern = rf"\n#{{1,{header_level}}}\s+"` interpretava qualquer comentário Python/Bash iniciado por `# ` dentro de blocos de código (```` ```python ````) como novo cabeçalho Markdown de nível 1, descartando todo o texto subsequente da seção.
4. **Ausência de Suporte a Argumentos CLI (Challenger 1, Linhas 81-92):**
   - Em `main()`, o script lia um caminho estático `IMPLEMENTATION_PLAN_BOMBA.md`, ignorando `sys.argv[1]`.

### 1.2 Modificações Implementadas em `verify_plan_bomba.py`

1. **Adição de Fronteira de Palavra (`\b`) a Todos os Padrões de Cabeçalho:**
   - Em `SECTION_110_ITEMS`: aplicado `\b` aos 12 itens (1.10.1 a 1.10.12) e às 4 diretrizes (`####\s+D-SEC-01\b` a `D-SEC-04\b`).
   - Em `SECTION_111_ITEMS`: aplicado `\b` aos 11 procedimentos de bancada (1.11.1 a 1.11.11).
2. **Isolamento Estrito da Linha de Cabeçalho e Validação de Corpo Mínimo:**
   - `extract_section_content`:
     ```python
     newline_pos = text.find("\n", start_pos)
     if newline_pos == -1:
         return ""
     body_start = newline_pos + 1
     subsequent_text = text[body_start:]
     ```
     O corpo extraído começa rigorosamente no byte seguinte à quebra de linha do cabeçalho.
   - Validação de substância em `verify_plan`:
     ```python
     has_substance = len(sec_body.strip()) >= 50
     ```
     Se `len(sec_body.strip()) < 50`, o item é imediatamente reprovado com o detalhe `"Corpo insuficiente (<N> chars < 50)"`.
   - Busca de palavras-chave restrita estritamente a `sec_body` (o corpo real), impedindo que palavras no título aprovem a seção.
   - Reforço no validador estruturado fallback:
     ```python
     if re.search(r"-\s+\*\*(?:Plano|Justificativa)", sec_body, re.IGNORECASE):
     ```
3. **Rastreamento de Blocos de Código (`in_code_block`) e Delimitação Segura:**
   - Detecção de cercas de código ```` ``` ```` e `~~~` alternando o estado booleano `in_code_block`. Comentários com `# ` dentro de código não ativam a condição de parada.
   - `header_stop_regex` configurado como `re.compile(rf"^\s*#{{1,{max(header_level, 4)}}}\s+")`, garantindo que cabeçalhos de parada até nível 4 (incluindo `#### D-SEC-xx`) finalizem a extração de itens de nível 3.
4. **Habilitação de Parâmetro CLI via `sys.argv[1]` em `main()`:**
   ```python
   def main():
       workspace_dir = Path(__file__).resolve().parent
       if len(sys.argv) > 1:
           plan_file = Path(sys.argv[1])
       else:
           plan_file = workspace_dir / "IMPLEMENTATION_PLAN_BOMBA.md"
       success = verify_plan(plan_file)
       sys.exit(0 if success else 1)
   ```

---

## 2. Logic Chain

1. **Premissa:** O harness de auditoria `verify_plan_bomba.py` deve garantir detecção estrita e determinística de omissões, recortes ou falsificações no plano `IMPLEMENTATION_PLAN_BOMBA.md`, rejeitando com Exit Code 1 quaisquer arquivos mutilados ou simulados.
2. **Resolução de Colisão:** Com `r"###\s+Item\s+1\.10\.1\b"`, o delimitador `\b` exige que o caractere após `1` seja não-alfanumérico (ex: `:` ou espaço). Assim, `1.10.1` jamais casa com `1.10.10`, `1.10.11` ou `1.10.12`. O mesmo ocorre para `1.11.1` em relação a `1.11.10`.
3. **Resolução de Cabeçalhos Vazios:** Ao deslocar `body_start = newline_pos + 1`, a linha que define o título é 100% excluída do escopo de inspeção semântica. Qualquer arquivo contendo apenas cabeçalhos resulta em `len(sec_body.strip()) == 0 < 50`, reprovando o item de imediato.
4. **Resolução de Truncamento em Código:** Ao rastrear `in_code_block`, linhas de comentário dentro de scripts não disparam o regex de interrupção. O texto explicativo e as palavras-chave posicionadas após blocos de código são integralmente retidos.
5. **Consistência do Plano Genuíno:** Todas as 27 seções do plano genuíno possuem corpos substanciais (variando de 281 a 2.429 caracteres) e contêm as palavras-chave mandatórias no corpo. Consequentemente, o plano genuíno mantém 100% de aprovação.

---

## 3. Caveats

- O script de auditoria valida o rigor formal, estrutural e técnico da documentação do plano em relação à especificação em `COMANDOS_DISPOSITIVOS_EXTERNOS.md`. A execução prática dos 11 ensaios de bancada física (§1.11) requer bancada física com fluido real e está programada para a Fase 3 do Roadmap.
- No momento, nenhum outro arquivo de código-fonte de implementação além de `verify_plan_bomba.py` foi alterado.

---

## 4. Conclusion

O script `verify_plan_bomba.py` foi totalmente endurecido e refatorado com sucesso:
- **Plano Genuíno (`IMPLEMENTATION_PLAN_BOMBA.md`):** 27 itens auditados (16 em §1.10 e 11 em §1.11), 27 aprovados (100.0% PASS), Exit Code `0`.
- **Bateria Adversarial 1 (Arquivo Sintético de Cabeçalhos Vazios):** 0 itens aprovados, 27 reprovados (0.0% PASS), Exit Code `1`.
- **Bateria Adversarial 2 (Plano com Item 1.10.1 Deletado):** Item 1.10.1 reprovado (`Seção não localizada`), 1 FAIL, Exit Code `1`.
- **Bateria Adversarial 3 (Plano com Item 1.11.1 Deletado):** Item 1.11.1 reprovado (`Seção não localizada`), 1 FAIL, Exit Code `1`.
- **Bateria Adversarial 4 (Corpo com Menos de 50 Caracteres):** Reprovado imediatamente (`Corpo insuficiente (30 chars < 50)`), Exit Code `1`.
- **Bateria Adversarial 5 (Palavras-Chave no Título mas Ausentes no Corpo):** Reprovado imediatamente (`Sem plano/justificativa`), Exit Code `1`.
- **Bateria Adversarial 6 (Blocos de Código com Comentários `# `):** Comentários e seções pós-código preservados sem truncamento.
- **Suporte a CLI:** Habilitado e testado com sucesso via `python verify_plan_bomba.py <arquivo>`.

---

## 5. Verification Method

Para verificar independentemente a correção, execute os comandos abaixo no terminal da raiz do projeto:

### 5.1 Verificação do Plano Genuíno (Deve retornar Exit Code 0 e 100% PASS)
```powershell
python verify_plan_bomba.py
```
*Saída esperada:* `[RESULTADO DA AUDITORIA]: SUCESSO ABSOLUTO (Exit Code 0)`, 27 PASS, 0 FAIL.

### 5.2 Verificação com Argumento CLI e Rejeição de Cabeçalhos Vazios (Deve retornar Exit Code 1 e 0% PASS)
```powershell
python -c "
import subprocess, sys
from pathlib import Path

dummy_md = '''# FAKE PLAN
### Item 1.10.1: Plano de Ação Técnico
### Item 1.10.2: Plano de Ação Técnico
### Item 1.10.3: Plano de Ação Técnico
### Item 1.10.4: Plano de Ação Técnico
### Item 1.10.5: Justificativa Técnica
### Item 1.10.6: Justificativa Técnica
### Item 1.10.7: Justificativa Técnica
### Item 1.10.8: Plano de Ação Técnico
### Item 1.10.9: Plano de Ação Técnico
### Item 1.10.10: Justificativa Técnica
### Item 1.10.11: Plano de Ação Técnico
### Item 1.10.12: Justificativa Técnica
#### D-SEC-01: Justificativa Técnica
#### D-SEC-02: Justificativa Técnica
#### D-SEC-03: Justificativa Técnica
#### D-SEC-04: Justificativa Técnica
### Item 1.11.1: Protocolo e Pré-requisitos
### Item 1.11.2: Protocolo e Pré-requisitos
### Item 1.11.3: Protocolo e Pré-requisitos
### Item 1.11.4: Protocolo e Pré-requisitos
### Item 1.11.5: Protocolo e Pré-requisitos
### Item 1.11.6: Protocolo e Pré-requisitos
### Item 1.11.7: Protocolo e Pré-requisitos
### Item 1.11.8: Protocolo e Pré-requisitos
### Item 1.11.9: Protocolo e Pré-requisitos
### Item 1.11.10: Protocolo e Pré-requisitos
### Item 1.11.11: Protocolo e Pré-requisitos
'''
tmp = Path('.agents/worker_2/tmp_verify.md')
tmp.write_text(dummy_md, encoding='utf-8')
proc = subprocess.run([sys.executable, 'verify_plan_bomba.py', str(tmp)], capture_output=True, text=True, encoding='utf-8')
tmp.unlink()
print('Return Code:', proc.returncode)
assert proc.returncode == 1, 'Dummy plan should have failed with exit code 1'
print('Empty header bypass test: PASSED (Correctly rejected with Exit Code 1)')
"
```

### 5.3 Verificação de Detecção de Omissão de 1.10.1 e 1.11.1 (Deve retornar Exit Code 1)
```powershell
python -c "
import subprocess, sys, re
from pathlib import Path

content = Path('IMPLEMENTATION_PLAN_BOMBA.md').read_text(encoding='utf-8')
m1 = re.search(r'###\s+Item\s+1\.10\.1:', content)
m2 = re.search(r'###\s+Item\s+1\.10\.2:', content)
mut = content[:m1.start()] + content[m2.start():]
tmp = Path('.agents/worker_2/tmp_no_1101.md')
tmp.write_text(mut, encoding='utf-8')
proc = subprocess.run([sys.executable, 'verify_plan_bomba.py', str(tmp)], capture_output=True, text=True, encoding='utf-8')
tmp.unlink()
assert proc.returncode == 1, 'Deletion of 1.10.1 should fail'
print('Prefix collision test for 1.10.1: PASSED (Correctly caught with Exit Code 1)')
"
```
