# Auditoria final de aceite — receitas kLa e rampas

Auditoria iniciada em 08/10/2026 sobre `e27dde8`. Fonte dos requisitos: `2026-10-07-receitas-kla-autonomo-e-rampas.md`, seção 5.1 e critérios associados. Este documento não redefine o escopo e não encerra o objetivo. Cada etapa só recebe aceite quando os critérios pertinentes tiverem prova correspondente examinada.

Evidência comum atual: `receitas-r61/evidence/recipes-r61-readonly-and-link-full.trx`, 2508 aprovações, zero falhas. Release: `D:/Temp/OpenTECHub-readonly-link-release/`, zero erros, 1881 avisos. O checkout contém trabalho paralelo do autor e capturas regeneradas; não é uma árvore limpa. A evidência da receita corresponde aos arquivos selecionados nos commits de escopo; não incorpora automaticamente esse trabalho paralelo.

| Etapa | Estado da auditoria final |
|---|---|
| R0.1 | Critérios de baseline de software conferidos abaixo |
| R0.2 | Critérios contratuais de software conferidos abaixo |
| R1.1 | Pendente de conferência final |
| R1.2 | Pendente de conferência final |
| R1.3 | Pendente de conferência final |
| R3.1 | Pendente de conferência final |
| R2.1 | Pendente de conferência final |
| R2.2 | Pendente de conferência final |
| R4.1 | Pendente de conferência final |
| R4.2 | Pendente de conferência final; evidências visuais recentes disponíveis |
| R5.1 | Auditoria parcial em `receitas-r52/RETOMADA_APOS_ENSAIO.md`; conferir critérios restantes |
| R5.2 | Auditoria parcial em `receitas-r52/PERDA_COMUNICACAO_E_POSSE.md`; conferir critérios restantes |
| R6.1 | Pendente de fechamento desta auditoria e recibo da revisão final |
| R6.2 | Pendente de bancada; não substituída por simulação |

## R0.1 — Baseline e consolidação

| Requisito | Evidência examinada | Conclusão |
|---|---|---|
| Inventário E5/E6/E7 e quadros | `receitas-r01/baseline-files.txt` e recibo da etapa; commit `be213dc` existe no repositório | Inventário preservado |
| Dependências/fontes reproduzíveis | Baseline registra parent `c51253f` e árvore `b273d6246e2183ce87fd7298f8cf979681afd022`; compilação/regressão atuais consumiram fontes/dependências | Baseline de software registrado; não equivale a checkout atual limpo |
| Vínculos históricos | `EXECUCAO_R01.md` vincula 1959 aprovações e ressalva recibos E7 de revisões anteriores | Origem distinguida da compilação atual |
| Quadros da cascata e relógio preservados | Arquivos `RecipeEngine.cs`, `RecipeEngine.Cascade.cs` e `TestClock.cs` sem diferenças locais na conferência; regressão atual aprovada | Sem alteração local residual nesses arquivos |
| Capturas/conflitos não apagados automaticamente | Recibo exclui capturas e duplicatas da consolidação; commits recentes selecionam arquivos explicitamente | Material paralelo preservado; não contado como evidência aprovada |

Aceite desta etapa é o baseline histórico reproduzível, não liberação física nem promessa de árvore atual sem alterações.

## R0.2 — Invocação, migração e capacidades

Fontes inspecionadas: `KlaRecipePulseBinding.cs`, `KlaAssayApi.cs`, `KlaAssayApiContracts.cs`; testes `KlaRecipePulseTests` e registros de contratos no TRX comum. Recibo original `receitas-r02/EXECUCAO_R02.md`, commit `5669f3a` existente. O TRX atual contém **62 aprovações** nas classes `KlaRecipePulseTests`, `KlaAssayApiTests` e `RecipeExecutionContractTests`; a contagem original de 61 permanece histórica, não somável.

| Requisito | Prova examinada | Conclusão |
|---|---|---|
| Multiple → Single com IDs estáveis | `Matrix_maps_to_stable_single_pulses_without_interactive_defaults`, dois protocolos, três pulsos distintos, mesmos IDs ao reenviar, tentativa diferente muda ID; mapeador congela request | Comprovado no escopo contratual |
| Chave repetida/content diferente recusado | `Changed_payload_keeps_identity_but_cannot_change_a_registered_pulse`; API calcula SHA256 e recusa substituição | Comprovado |
| Round-trip mantém retorno | `Requests_round_trip_without_losing_return_state` nos quatro modos/protocolos, aprovado no TRX | Comprovado |
| Migração sem confirmação inventada | Leitura de array legado e envelope versionado; `Legacy_journal_is_migrated_without_inventing_recipe_identity_or_return_evidence` exige origem nula e continuação recusada; versão 999 recusada sem sobrescrever | Comprovado |
| Persistência separada de retorno | `Terminal_disk_failure_preserves_confirmed_physical_return_and_blocks_new_actuation`; falha de arquivo preserva evidência declarada de retorno e bloqueia avanço | Comprovado em executor substituído; não é prova física |
| Snapshot exato/recibo para avanço | `Completion_requires_exact_return_and_persistence`; `MayContinueRecipe` exige retorno Confirmed, snapshot correspondente e recibo em request de receita | Comprovado |
| Qualidade/autoria sem aprovação fictícia | `Conditional_quality_and_OUR_follow_frozen_policy` verifica autorização explícita de motivos, OUR independente e operador Pending | Comprovado no contrato |
| Capacidade por protocolo/instalação/perfil | `EnsureAllows` confere instalação/perfil/versão/protocolo; testes de incompatibilidade exigem zero chamadas do executor | Comprovado |
| Não contornar biótico físico | `Declared_physical_capability_does_not_bypass_E7_biotic_gate` e chamada de `KlaActuationRelease` | Bloqueio preservado |
| Nenhuma atuação na etapa | Mapeador/contratos não despacham; testes usam Executor substituído | Escopo contratual, atuação concreta auditada em R2.1 |

Os recibos duráveis reais, restauração concreta e orçamento entre reinícios pertencem às etapas seguintes; esta auditoria não transfere o aceite contratual para esses requisitos.
