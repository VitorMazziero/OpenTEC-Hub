# Relatório de Handoff — Mineração de Especificações da Bomba Peristáltica

**Data:** 2026-09-13  
**Agente:** `spec_miner_1` (Specification Miner)  
**Destinatário:** Orquestrador (`parent` / `a109a27d-47d4-4806-8071-687fa2f0b968`)  
**Tipo de Handoff:** Hard (Tarefa Concluída)  
**Artefato Produzido:** `d:\OneDrive\PosDoc_Fapesp\Automacao_e_Controle\ProjetoTECNAL\.agents\spec_miner_1\spec_inventory.md`

---

## 1. Observation (Observações Diretas)

1. **Documentação Autoritativa:**
   - O documento `External-Devices/docs/COMANDOS_DISPOSITIVOS_EXTERNOS.md` (§1.0 a §1.11, linhas 23 a 266) foi examinado na íntegra.
   - A tabela §1.10 enumera explicitamente 12 itens de auditoria (itens 1 a 11 mais o item não numerado de potenciômetro `gain`), resumindo decisões do operador de 2026-09-12 sobre a evolução do firmware 3.9 para o 3.10.
   - A subseção §1.11 lista 11 itens de checklist de bancada física (linhas 254 a 264), numerados sequencialmente.
   - A tabela rápida em §1.0 detalha 4 diretrizes fechadas de segurança (NVS, arbitragem de potenciômetros, modo por presença de líquido, calibração linear vs lookup table).
   - As subseções §1.1 a §1.9 estabelecem restrições de hardware, equações de velocidade e volume, catálogo de chaves, protocolos de rede e procedimentos operacionais.

2. **Código-Fonte do Firmware da Bomba (`External-Devices/bomba-peristaltica`):**
   - `FirmwareApp.cpp:8` define a versão como `3.10`: `"{\"device\":\"peristaltic-pump\",\"version\":\"3.10\"..."`.
   - `FirmwareApp.cpp:15-25` estabelece pinos: `R_EN=25`, `L_EN=26`, `R_PWM=14`, `L_PWM=27`, `POT_INT=34`, `POT_GAIN=35`, `SENSOR_PIN=15`, `SENSOR_ENABLE_BUTTON_PIN=32`, `SENSOR_STATUS_LED_PIN=33`.
   - `OperationController.h:41-54` (`startCycle()`) demonstra que a parada por `mode:0` ou novo perfil não zera `g_cumulativeVolumeMl`, mantendo-o como contador contínuo de sessão.
   - `OperationController.h:258-262` demonstra que apenas o comando explícito `reset_volume` zera `g_cumulativeVolumeMl` e `g_cycleStartVolumeMl`.
   - `Lifecycle.h:138-142` implementa o timeout de parada autônoma via deadline `g_usbSpeedUntilMs` para o comando `speed_ms`.
   - `RuntimeStateStore.h:1-39` implementa o checkpoint a cada 60 s (`s_active`, `s_vol`, `s_time`, `s_mode`, `s_cvol`) e retoma autonomamente em `OP_RUNNING` no boot.

3. **Código-Fonte do Hub 10.2 (`ESP32S3-HUB`):**
   - `Commands.h:588-625` decompõe os comandos da bomba, implementando a lista branca `allowedPumpCommands[] = { "reset_volume", "start", "stop" }` (linha 596) e rejeitando comandos perigosos de NVS (`clear_nvs`, `save_config`).
   - `Telemetry.h:325-347` agrega as métricas e ecos da bomba (`PumpOnline`, `PumpCommEnabled`, `PumpCommandPending`, `PumpMode`, `PumpPWM`, `PumpSpeed`, `PumpFlow`, `PumpVol`, `PumpTargetVol`, `PumpActive`, `PumpWaiting`, `PumpSlope`, `PumpIntercept`, `PumpPidKp`, `PumpPidKi`, `PumpPidKd`, `PumpPotEnabled`, `PumpCycleVol`).
   - `Mailboxes.h` e `Runtime.h` contêm a semeadura aleatória de `cmd_id` (`seedReliableMailboxes()`), resolvendo a perda do primeiro comando pós-reboot do Hub.

4. **Código-Fonte do Windows App (`Windows_app`):**
   - `CommandBuilders.cs:414-416` constrói `PumpStopProfile()` emitindo estritamente `{"mode":0}` sem a chave vestigial `speed`.
   - `CommandBuilders.cs:770-822` implementa construtores para `PumpManualSpeed` (com e sem `speed_ms`), `PumpPotentiometers`, `PumpResetVolume`, `PumpCalibration` e `PumpPid`.
   - `PumpCalibrationViewModel.cs` implementa a calibração volumétrica assistida multiponto com regressão linear ($R^2$), cálculo de resíduos e emissão de recibo JSON.
   - `PONTOS_DE_MELHORIA_EXPOSICAO_NOS.md:199-212` documenta a decisão do operador de descartar a calibração gravimétrica com balança em favor do método volumétrico com recipiente graduado.

---

## 2. Logic Chain (Cadeia de Raciocínio Lógico)

1. A partir das observações do texto de `External-Devices/docs/COMANDOS_DISPOSITIVOS_EXTERNOS.md` e do `ORIGINAL_REQUEST.md`, identificou-se a necessidade de mapear todos os itens das seções §1.10 e §1.11, sem omissões, correlacionando-os com o contexto das subseções §1.1 a §1.9.
2. Cruzando as asserções documentais com os arquivos reais de firmware, Hub e App, comprovou-se que as decisões de 2026-09-12 foram efetivamente incorporadas no código ativo (firmware 3.10, Hub 10.2 e App Windows), mas 11 ensaios de bancada física permanecem pendentes (§1.11).
3. Cada item de §1.10 foi decomposto em um registro formal com ID único (`F-110-01` a `F-110-12`), citações exatas em português, implicações técnicas, restrições eletromecânicas, subsistemas impactados e perguntas para validação no código. Decisões de segurança adicionais (`D-SEC-01/02`) e riscos sistêmicos (`R-SYS-01..06`) foram igualmente formalizados.
4. Cada passo de ensaio de §1.11 foi expandido em um procedimento experimental estruturado com ID único (`CHK-111-01` a `CHK-111-11`), contendo objetivo, pré-requisitos, procedimento passo a passo, comportamento esperado, critérios de aprovação/rejeição e subsistemas afetados.
5. As tabelas obrigatórias de Descoberta de Recursos (*Features Discovered*) e Casos de Borda (*Edge Cases*) foram construídas para atender rigorosamente às instruções de especialidade do agente, abrangendo desde comandos de baixo nível até comportamentos assíncronos do rádio e NVS.

---

## 3. Caveats (Ressalvas e Suposições)

1. **Ensaios Físicos de Bancada:** O presente agente atuou em modo puramente analítico e documental. A comprovação de que o motor físico DC de fato gira no sentido correto ou atinge $R^2 \ge 0,99$ depende exclusivamente da execução dos ensaios em hardware real listados em `CHK-111-01` a `CHK-111-11`.
2. **Compatibilidade com Hardware 3.9:** O inventário documentou o modo de degradação graciosa com o firmware 3.9 (onde `speed` é pegajoso e `mode:0` zera o volume), assumindo que nós legados podem coexistir temporariamente até atualização OTA para 3.10.
3. **Não-Modificação do Código:** Nenhuma alteração foi realizada nos fontes do projeto, conforme restrição explícita do prompt.

---

## 4. Conclusion (Conclusão)

O inventário de especificações da Bomba Peristáltica Externa foi concluído com exaustividade absoluta e estruturado no documento:
`d:\OneDrive\PosDoc_Fapesp\Automacao_e_Controle\ProjetoTECNAL\.agents\spec_miner_1\spec_inventory.md`

O documento contém 762 linhas e 83,8 kB de documentação técnica densa e estruturada, cobrindo:
- Todos os 12 itens de auditoria e decisões de §1.10 (`F-110-01` a `F-110-12`).
- As 4 diretrizes fechadas de segurança e arquitetura (`D-SEC-01` a `D-SEC-04` / `D-SEC-01/02`).
- Os 6 riscos sistêmicos e restrições de baixo nível (`R-SYS-01` a `R-SYS-06`).
- A diretriz de compatibilidade retroativa (`NOTE-COMPAT-01`).
- Todos os 11 itens de ensaio prático de bancada de §1.11 (`CHK-111-01` a `CHK-111-11`).
- O catálogo completo de chaves, pinos, equações de conversão, dinâmica do motor e canais de ingestão (§1.1 a §1.9).
- A tabela de 12 recursos descobertos e a tabela de 10 casos de borda.
- A matriz cruzada de rastreabilidade entre subsistemas (Firmware, Hub, App e Bancada).
- O roteiro de questões pontuais para os agentes subsequentes de auditoria e planejamento (`IMPLEMENTATION_PLAN_BOMBA.md`).

---

## 5. Verification Method (Método de Verificação Independente)

Para auditar e verificar independentemente os achados e artefatos deste agente:

1. **Inspeção do Artefato Principal:**
   ```powershell
   Get-Content -Path "d:\OneDrive\PosDoc_Fapesp\Automacao_e_Controle\ProjetoTECNAL\.agents\spec_miner_1\spec_inventory.md" -TotalCount 50
   ```
2. **Verificação de Completude dos IDs de §1.10:**
   Verificar no arquivo `spec_inventory.md` a presença explícita dos identificadores `F-110-01` até `F-110-12`.
3. **Verificação de Completude dos IDs de §1.11:**
   Verificar no arquivo `spec_inventory.md` a presença explícita dos identificadores `CHK-111-01` até `CHK-111-11`.
4. **Verificação das Tabelas Requeridas:**
   Confirmar a presença das seções `## 5. Tabela de Descoberta de Recursos (Features Discovered)` e `## 6. Tabela de Casos de Borda (Edge Cases)`.
5. **Checagem Cruzada no Código:**
   - Conferir `allowedPumpCommands` em `ESP32S3-HUB/ESP32S3-HUB/src/protocol/Commands.h:596`.
   - Conferir `PumpStopProfile()` em `Windows_app/src/OpenTECHub.Protocol/CommandBuilders.cs:414`.
   - Conferir `startCycle()` e `resetOperationState()` em `External-Devices/bomba-peristaltica/firmware/peristaltic-pump/src/control/OperationController.h:41-54`.
