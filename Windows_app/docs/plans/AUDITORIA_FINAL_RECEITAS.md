# Auditoria final de aceite — receitas kLa e rampas

Auditoria iniciada em 08/10/2026 sobre `e27dde8`. Fonte dos requisitos: `2026-10-07-receitas-kla-autonomo-e-rampas.md`, seção 5.1 e critérios associados. Este documento não redefine o escopo e não encerra o objetivo. Cada etapa só recebe aceite quando os critérios pertinentes tiverem prova correspondente examinada.

Evidência comum atual: `receitas-r61/evidence/recipes-r61-readonly-and-link-full.trx`, 2508 aprovações, zero falhas. Release: `D:/Temp/OpenTECHub-readonly-link-release/`, zero erros, 1881 avisos. O checkout contém trabalho paralelo do autor e capturas regeneradas; não é uma árvore limpa. A evidência da receita corresponde aos arquivos selecionados nos commits de escopo; não incorpora automaticamente esse trabalho paralelo.

| Etapa | Estado da auditoria final |
|---|---|
| R0.1 | Critérios de baseline de software conferidos abaixo |
| R0.2 | Critérios contratuais de software conferidos abaixo |
| R1.1 | Critérios de reservas de software conferidos abaixo |
| R1.2 | Critérios de suspensão/PID de software conferidos abaixo |
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

## R1.1 — Reserva e cessão

Fontes/testes examinados nesta auditoria: `CommandAuthorityTests`, `RecipeResourceCoordinatorTests`, `RecipeResourceCoordinator.ReturnAsync`; arbitrador comum e dispatcher da rampa examinados também na auditoria de comunicação. Recibos originais vinculados aos commits `52cee5a` (R1.1) e `237c254` (R1.2), existentes no histórico. No TRX comum, **26 casos aprovados** pertencem a `CommandAuthorityTests`, `RecipeResourceCoordinatorTests`, `CascadeResumeTests` e `RecipeCascadeSuspensionGateTests`; esse número não inclui os testes do engine e não se soma à regressão completa.

| Requisito | Prova examinada | Conclusão |
|---|---|---|
| Reserva atômica N/Q/recursos | `Conflicting_reservations_wait_as_whole_sets_and_cancel_without_partial_acquisition`: reserva conjunta aguarda, recurso independente permanece adquirível, cancelamento não retém conjunto parcial | Comprovado |
| Timeout/cancelamento sem alterar posse | `Releasing_resources_wakes_a_waiter_and_timeout_never_changes_ownership`; `Cancelled_wait_does_not_disturb_an_active_assay` | Comprovado |
| Mesmo proprietário não autoriza outro bloco | `Reservation_excludes_other_blocks_with_the_same_owner_but_preserves_independent_resources`: despacho Recipe sem lease recusado, temperatura independente aceita | Comprovado |
| Receita → ensaio → receita sem Manual | `Handoff_is_atomic_and_old_generations_cannot_dispatch_or_return_ownership`: sequência de transferências exatamente KlaAssay/Recipe, geração incrementada, token antigo inválido | Comprovado |
| Drenagem antes da troca | Mesmo teste recusa transferência antes da barreira; `Transport_barrier_fails_before_handoff_if_a_previous_frame_was_not_written` preserva posse Recipe em falha | Comprovado como transporte, não aplicação física |
| Emergência invalida tokens | `Emergency_invalidates_all_authorities_and_a_late_return_cannot_restart_actuation` exige somente comando seguro; callback de rastreamento com troca de posse não enfileira comando antigo | Comprovado |
| Retorno depende do snapshot/recibo | Teste do coordenador recusa snapshot diferente e recibo ausente antes de retomar; `ReturnAsync` revalida produtores, autoridade e prova durável e somente depois devolve/resume | Comprovado no coordenador; restauração real auditada em R1.3 |
| Produtor independente continua | Teste de quiescência do coordenador mantém zero pausas no produtor de temperatura | Comprovado |

## R1.2 — Suspensão real e retomada

Fontes examinadas: `RecipeEngine.Cascade.cs`, `CascadeController.ResumeFromSuspension`, `CascadeTwoLoopPidController.ResumeFromSuspension`, gate e testes abaixo. O produtor de cascata é privado do engine; o recibo de suspensão é consumido pelo coordenador e sua disponibilidade confere estado capturado e geração de pausa.

| Requisito | Prova examinada | Conclusão |
|---|---|---|
| Barreira cobre atualização/despacho em voo | Engine entra no gate antes de construir/atualizar o controlador; `Pause_waits_for_in_flight_step_and_rejects_new_commands` aguarda o passo e recusa novo ingresso | Comprovado em composição de código/teste do gate |
| Recibo antigo e término não retomam | `Old_receipt_cannot_resume_a_new_pause`, `Stop_blocks_late_resume_and_new_step`; coordenador confere `CanResume` antes/depois da drenagem | Comprovado |
| Cancelamento de espera não interfere no ensaio ativo | Gate desfaz somente sua pausa cancelada; coordenador serializa ensaios e `Cancelled_wait_does_not_disturb_an_active_assay` mantém primeira cessão sem retomada | Comprovado |
| Nenhum Update ou comando durante ensaio | `Assay_suspension_preserves_cascade_state_and_resumes_without_integrating_the_gap` compara termos e contagem de comandos após 2 h virtuais e cinco quadros durante suspensão; sintonia recusada | Comprovado no engine |
| Sem dt acumulado/replay | Engine usa timestamp monotônico, ignora `IgnoreFramesThrough`, exige quadro novo e rebaseia sem despacho; teste integrado preserva saída/integral e exige derivada/delta zero | Comprovado |
| Preservar sintonia/alocação/história coerente | `CascadeResumeTests` preserva esforço, integral e objetos de sintonia/alocação; controlador delega ao PID que limpa história de derivada sem zerar integral/saída | Comprovado |
| Condição de saída continua observada | Engine avalia monitor antes do gate e antes de descartar quadros antigos; `Suspended_cascade_still_observes_buffered_exit_condition_and_cannot_resume_after_end` conclui por temperatura durante suspensão e não recria cascata | Comprovado |
| Debounce e quadro de retorno | Regressão de `RecipeEngineTests` no TRX comum inclui debounce; integração de grupo/retorno em andamento também disponível em `RecipePeriodicKlaIntegrationTests`, auditada novamente em R4.1/R5.2 | Sem regressão conhecida; encerramento completo permanece no escopo dessas etapas |

Esses aceites são de software. Não comprovam cessão física, estado real de válvulas, motor ou retorno do cultivo. R1.3/R3.1/R2.1 devem ter suas provas próprias examinadas.
