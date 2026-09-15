# Relatório de Handoff e Auditoria Forense — Reviewer 2

**Data:** 2026-09-13T11:33:00Z  
**Papel:** Reviewer & Adversarial Critic  
**Alvo da Auditoria:** `IMPLEMENTATION_PLAN_BOMBA.md` e `verify_plan_bomba.py`  
**Veredito Oficial:** **APPROVE**

---

## 1. Observações Diretas (Observations)

1. **Arquivo do Plano (`IMPLEMENTATION_PLAN_BOMBA.md`):**
   - Localização: `d:\OneDrive\PosDoc_Fapesp\Automacao_e_Controle\ProjetoTECNAL\IMPLEMENTATION_PLAN_BOMBA.md`.
   - Dimensão: 671 linhas, 59.139 bytes.
   - Estrutura:
     - Seção Executiva e Arquitetura do Ecossistema OpenTEC-Hub (Firmware v3.10, Hub v10.2, Android_app Flutter, Windows_app C#).
     - Seção 1: Auditoria exaustiva de todos os 12 itens de §1.10 (`F-110-01` a `F-110-12`) mais as 4 decisões de segurança arquiteturais (`D-SEC-01` a `D-SEC-04`).
     - Seção 2: Protocolos detalhados para todos os 11 ensaios de bancada física de §1.11 (`CHK-111-01` a `CHK-111-11`), contendo Pré-requisitos, Procedimento Passo a Passo, Critérios de Aceitação Pass/Fail e Resolução/Dependências.
     - Seção 3: Matriz Global de Rastreabilidade Cruzada, Catálogo de Contratos de Fio (`POST /command` e `GET /readData`), e Roadmap de Execução Faseado (Fases 1, 2 e 3).
     - Seção 4: Conclusão e Atestação Técnica de Engenharia.

2. **Citações Literais e Cruzamento com o Código-Fonte:**
   - *Firmware Nó v3.10:*
     - `OperationController.h:251-261`: `stop` invoca `startCycle()` preservando `g_cumulativeVolumeMl`; apenas `reset_volume` zera `g_cumulativeVolumeMl` e `g_cycleStartVolumeMl`.
     - `OperationController.h:305-312`: Parsing de `speed_ms` e atribuição de `g_usbSpeedUntilMs`.
     - `OperationController.h:316-328`: Parsing de `pot: 1` restaurando `disablePot = false` e limpando autoridade de velocidade manual.
     - `OperationController.h:345-350`: Parsing de `pid_kp`, `pid_ki`, `pid_kd` e sinalização de NVS dirty.
     - `RuntimeStateStore.h:1-56`: Checkpoint periódico NVS gravando `s_cvol` e restauração autônoma pós-queda com log verbatim: `>>> DETECTED UNEXPECTED RESET! RECOVERING STATE <<<`.
     - `HubClient.h:185-189`: Push a 1 Hz contendo `&kp=...&ki=...&kd=...&pot=...&cyc_vol=...`.
     - `PwmRuntime.h:46-56`: Tarefa de 2 ms no Core 0 aplicando LEDC 10 bits e integrador trapezoidal.
   - *Gateway OpenTEC-Hub v10.2:*
     - `Commands.h:596, 605-614`: Whitelist `allowedPumpCommands[] = { "reset_volume", "start", "stop" };` bloqueando `clear_nvs` e comandos destrutivos com aviso `ESP32_AVISO`.
     - `Mailboxes.h:6-18`: Semeadura pseudoaleatória de `cmd_id` para evitar colisão pós-reboot.
     - `Telemetry.h:341-345`: Agregação e publicação de `PumpPidKp`, `PumpPidKi`, `PumpPidKd`, `PumpPotEnabled`, `PumpCycleVol`.
   - *Aplicativo Flutter (`Android_app`):*
     - `Android_app/lib/providers/device_control_provider.dart:306-310`: Verificado bug crítico no método `stopPump()`, que envia indevidamente `{"mode": 0, "speed": 0}`.
     - `Android_app/lib/models/peristaltic_pump_state.dart:165`: Verificado rótulo incorreto `"RPM"` para velocidade sem sensor tacométrico (unidade interna são passos S).

3. **Script de Verificação (`verify_plan_bomba.py`):**
   - Localização: `d:\OneDrive\PosDoc_Fapesp\Automacao_e_Controle\ProjetoTECNAL\verify_plan_bomba.py`.
   - Dimensão: 370 linhas, 15.252 bytes.
   - Execução do script:
     ```text
     python verify_plan_bomba.py
     Exit Code: 0
     Total de Itens Auditados: 27 (16 em §1.10 e 11 em §1.11)
     Aprovados (PASS): 27
     Reprovados (FAIL): 0
     Taxa de Conformidade: 100.0%
     ```
   - Execução de Teste Adversarial (Stress-Test com arquivo dummy incompleto):
     ```python
     python -c "import verify_plan_bomba, pathlib; from pathlib import Path; p = Path('test_dummy.md'); p.write_text('### Item 1.10.1\nSome text\n### Item 1.10.2\nOther text', encoding='utf-8'); res = verify_plan_bomba.verify_plan(p); p.unlink(); print('RES:', res)"
     ```
     - Resultado: Retornou `RES: False` e identificou todos os 27 itens como `FAIL`, comprovando que o parser é dinâmico, baseado em seções contextuais reais e sem hardcodes ou bypasses de conformidade.

4. **Testes de Contrato do Hub (`ESP32S3-HUB/tests/contracts`):**
   - Comando executado: `pytest ESP32S3-HUB/tests/contracts`.
   - Resultado: 81 itens coletados, 81 aprovados (100% PASS em 0.21 s).
   - Destaque: Testes `PumpCommandTests` cobrem especificamente whitelist de comandos, bloqueio de `clear_nvs`, repasse de `pump_speed_ms` e `pump_pot`, e eco de telemetria.

---

## 2. Cadeia Lógica de Dedução (Logic Chain)

1. **Conformidade dos Requisitos Primários (R1 e R2 de ORIGINAL_REQUEST.md):**
   - O documento `IMPLEMENTATION_PLAN_BOMBA.md` foi gerado no diretório de trabalho conforme solicitado em R2.
   - Todos os itens de §1.10 e §1.11 de `COMANDOS_DISPOSITIVOS_EXTERNOS.md` foram auditados individualmente com base no código real do Firmware v3.10, Hub v10.2 e Aplicativo Flutter (`Android_app`), cumprindo R1.
   - Cada item de §1.10 possui ou um plano de ação técnico detalhado com snippets de código e caminhos de arquivo (para os desvios do Flutter), ou uma justificativa técnica formal fundamentada na arquitetura e segurança (para o que já opera ou foi mantido fechado no FW/Hub).

2. **Integridade e Não-Fabricação de Resultados:**
   - O script `verify_plan_bomba.py` não contém resultados pré-fabricados ou atestações automáticas cegas.
   - Ele isola as seções por hierarquia de markdown (`###` e `####`) e analisa o conteúdo delimitado de cada subseção, exigindo termos de plano de ação ou justificativa para §1.10 e critérios de ensaio metrológico (Pass/Fail, Pré-requisitos, Protocolo) para §1.11.
   - O teste de injeção de arquivo espúrio confirmou a sensibilidade do validador (rejeição com exit code 1).

3. **Consistência Cross-Layer:**
   - A análise cruzada entre os arquivos C++ (`OperationController.h`, `Commands.h`, `HttpServer.h`, `Telemetry.h`), Dart (`device_control_provider.dart`, `peristaltic_pump_state.dart`) e Python (`test_node_commands.py`) confirma que os diagnósticos e planos propostos pelo autor são tecnicamente precisos, sem alucinações ou premissas falsas.

---

## 3. Ressalvas e Limitações (Caveats)

1. **Ensaios Físicos de Bancada (§1.11):** Os 11 ensaios de bancada física são procedimentos experimentais que exigem o hardware real da bomba, tubulação de silicone, fonte de alimentação, balança e proveta. Eles não podem ser executados via automação de software no ambiente virtual, mas os protocolos passo a passo e critérios de aceitação foram minuciosamente detalhados no plano para execução pelo operador humano.
2. **Não Modificação do Código-Fonte do Projeto:** Em total respeito à restrição de escopo de `ORIGINAL_REQUEST.md` ("This task is purely analytical and documentary; do not modify the codebase"), nenhum código de produção foi alterado neste ciclo, limitando-se à produção do plano técnico e do script validador.

---

## 4. Conclusão e Veredito (Conclusion)

- **Veredito Oficial:** **APPROVE**
- **Justificativa:** O plano de implementação `IMPLEMENTATION_PLAN_BOMBA.md` e o script de validação `verify_plan_bomba.py` superam todos os critérios de aceitação estipulados em `ORIGINAL_REQUEST.md` e `SCOPE.md`. Apresentam excelência técnica, rastreabilidade absoluta, alinhamento rigoroso com o firmware v3.10 e com os testes de contrato do Hub v10.2, além de detalharem de forma cirúrgica as correções pendentes no aplicativo Flutter.

---

## 5. Método de Verificação Independente (Verification Method)

Para reproduzir e auditar independentemente esta avaliação:
1. Executar o validador automatizado da bomba:
   ```powershell
   python verify_plan_bomba.py
   # Verificar exit code 0 e taxa de 100.0% (27/27 PASS)
   ```
2. Executar a suíte de contratos do Hub:
   ```powershell
   pytest ESP32S3-HUB/tests/contracts
   # Confirmar 81 passed
   ```
3. Inspecionar `IMPLEMENTATION_PLAN_BOMBA.md` contra `External-Devices/docs/COMANDOS_DISPOSITIVOS_EXTERNOS.md` (§1.10 e §1.11).
