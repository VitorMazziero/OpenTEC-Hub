# OpenTEC-Hub Documentation

Índice mestre da documentação técnica do projeto OpenTEC-Hub. O ponto de entrada da aplicação é [../README.md](../README.md).

---

## 📌 Documentos Principais (Raiz de `docs/`)

Estes são os documentos centrais que definem o estado, arquitetura, protocolo e regras de design do projeto:

| Documento | Descrição |
|---|---|
| [CURRENT_STATUS.md](CURRENT_STATUS.md) | **Estado auditado atual.** Postura de release, auditoria de defeitos, evidências de testes e checklist de gate da v0.25.0 |
| [IMPLEMENTATION_STEPS.md](IMPLEMENTATION_STEPS.md) | **Plano de implementação passo a passo.** Roteiro detalhado de resolução de pendências de software, bancada e firmware |
| [ROADMAP.md](ROADMAP.md) | **Roadmap estratégico.** Fases do projeto, escopo, metas não-funcionais e trabalho planejado |
| [ARCHITECTURE.md](ARCHITECTURE.md) | **Arquitetura de software.** Padrões MVVM, threading model, camadas, arbitração de comandos e pipeline de dados |
| [PROTOCOL.md](PROTOCOL.md) | **Contrato de comunicação serial/Wi-Fi.** Chaves JSON, tipos, unidades, limites e temporização com o ESP32-S3 |
| [DECISIONS.md](DECISIONS.md) | **Registro de Decisões Arquiteturais (ADRs).** Justificativas e histórico (D-001 a D-036) |
| [UI_DESIGN.md](UI_DESIGN.md) | **Especificação de interface.** Identidade visual, tokens, paleta de cores, telas de sinótico e controles |
| [CONVENTIONS.md](CONVENTIONS.md) | **Convenções de código.** Nomenclatura, padrões assíncronos, testes e regras de engenharia de software |
| [CHANGELOG.md](CHANGELOG.md) | **Histórico de versões.** Alterações registradas por versão e lançamentos |

---

## 📁 Documentação Especializada por Subpasta

### 1. Planos Técnicos e Implementação (`docs/plans/`)
Planos de engenharia e especificações detalhadas de novos módulos e subsistemas:
- [PLANO_ENSAIOS_POTENCIA_IMPELIDOR.md](plans/PLANO_ENSAIOS_POTENCIA_IMPELIDOR.md) — Ensaios de potência de impelidor (Np, $P_g/P_0$, flooding, parada adaptativa por $IC_{95}$).
- [PLANO_IMPLEMENTACAO_TESTES_KLA.md](plans/PLANO_IMPLEMENTACAO_TESTES_KLA.md) — Especificação do módulo de determinação de $k_L a$ e persistência de corridas.
- [PLANO_DISPOSITIVOS_EXTERNOS.md](plans/PLANO_DISPOSITIVOS_EXTERNOS.md) — Auditoria e padronização dos dispositivos externos (biomassa, bomba externa, agitador).
- [PLANO_SERVO_POTENCIA_APP.md](plans/PLANO_SERVO_POTENCIA_APP.md) — Integração do servo acionamento ASDA-B2 sobre Modbus RTU para leitura de torque e potência.
- [FLOWMETER_V05_HUB_V7_SYNC_PLAN.md](plans/FLOWMETER_V05_HUB_V7_SYNC_PLAN.md) — Sincronização do firmware do medidor de vazão v05 com o Hub v7.

### 2. Hardware, Firmware e Validação (`docs/hardware/`)
Validação na bancada, integração física com sensores e atuadores, e simulador de hardware:
- [HARDWARE_VALIDATION.md](hardware/HARDWARE_VALIDATION.md) — Procedimentos de validação e checklist de bancada para hardware real.
- [PHASE0_RESULTS.md](hardware/PHASE0_RESULTS.md) — Resultados medidos dos testes de hardware da Fase 0 na placa física.
- [ACEITACAO_BANCADA_POTENCIA.md](hardware/ACEITACAO_BANCADA_POTENCIA.md) — Critérios de aceitação de bancada e ensaios do módulo de potência.
- [FIRMWARE_DISPOSITIVOS_EXTERNOS.md](hardware/FIRMWARE_DISPOSITIVOS_EXTERNOS.md) — Guia de gravação, alterações e pinouts dos firmwares dos periféricos auxiliares.
- [SIMULATOR.md](hardware/SIMULATOR.md) — Especificação e operação do simulador de firmware ESP32 para desenvolvimento offline.

### 3. Processos e Bioprocessos (`docs/processes/`)
Ciência de bioprocessos, calibração e procedimentos operacionais:
- [CALIBRATION.md](processes/CALIBRATION.md) — Procedimentos de calibração para sondas de pH, $O_2$ dissolvido e vazão de ar/nitrogênio.
- [KLA_MAPPING.md](processes/KLA_MAPPING.md) — Mapeamento experimental de $k_L a$, cálculo de headroom e algoritmo de interpolação.
- [SIMULACAO_TESTES_KLA.md](processes/SIMULACAO_TESTES_KLA.md) — Guia para testes em modo offline com reprodução de corridas experimentais de $k_L a$.

### 4. Histórico e Migração (`docs/history/`)
Registros de execução e legado do software:
- [PHASE_LOG.md](history/PHASE_LOG.md) — Diário de bordo detalhado com evidências e marcos técnicos de cada fase de desenvolvimento.
- [MIGRATION.md](history/MIGRATION.md) — Mapeamento dos módulos do app legado em Python (v.6) para C# e catálogo de defeitos históricos corrigidos.

### 5. Governança e Ativos (`docs/governance/`)
Proveniência técnica de modelos e avisos legais:
- [ASSET_PROVENANCE.md](governance/ASSET_PROVENANCE.md) — Proveniência, contrato alfa e prompt de renderização do modelo tridimensional do biorreator.
- [THIRD_PARTY_NOTICES.md](governance/THIRD_PARTY_NOTICES.md) — Reconhecimento de direitos autorais e licenças de algoritmos científicos de terceiros.

### 6. Guias de Design e Evidências
- `docs/UI_design_guides/` — Ativos visuais e referências de renderização consumidos pela interface (XAML).
- `docs/evidence/` — Logs de testes de longa duração, capturas de telemetria e evidências empíricas.

---

## 🧭 Guias de Leitura Recomendados

- **Avaliar o status e próximos passos do release:** [CURRENT_STATUS.md](CURRENT_STATUS.md) → [ROADMAP.md](ROADMAP.md)
- **Implementar novas funcionalidades:** [ROADMAP.md](ROADMAP.md) → [ARCHITECTURE.md](ARCHITECTURE.md) → [CONVENTIONS.md](CONVENTIONS.md)
- **Modificar comunicação ou comandos com o ESP32:** [PROTOCOL.md](PROTOCOL.md)
- **Construir ou modificar telas e componentes:** [UI_DESIGN.md](UI_DESIGN.md) → Tokens em `src/OpenTECHub/Themes/`
- **Entender decisões de projeto históricas:** [DECISIONS.md](DECISIONS.md) → [MIGRATION.md](history/MIGRATION.md)
