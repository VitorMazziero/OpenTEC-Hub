# Auditoria final de aceite — receitas kLa e rampas

Auditoria iniciada em 08/10/2026 sobre `e27dde8` e concluída na mesma data sobre `a5470ae` + correções desta revisão. Fonte dos requisitos: `2026-10-07-receitas-kla-autonomo-e-rampas.md`, seção 5.1 e critérios associados. Este documento não redefine o escopo e não encerra o objetivo. Cada etapa só recebe aceite quando os critérios pertinentes tiverem prova correspondente examinada.

Evidência comum atual: `receitas-r61/evidence/recipes-r61-readonly-and-link-full.trx`, 2508 aprovações, zero falhas. Release: `D:/Temp/OpenTECHub-readonly-link-release/`, zero erros, 1881 avisos. O checkout contém trabalho paralelo do autor e capturas regeneradas; não é uma árvore limpa. A evidência da receita corresponde aos arquivos selecionados nos commits de escopo; não incorpora automaticamente esse trabalho paralelo.

| Etapa | Estado da auditoria final |
|---|---|
| R0.1 | Critérios de baseline de software conferidos abaixo |
| R0.2 | Critérios contratuais de software conferidos abaixo |
| R1.1 | Critérios de reservas de software conferidos abaixo |
| R1.2 | Critérios de suspensão/PID de software conferidos abaixo |
| R1.3 | Critérios de software conferidos abaixo (seção R1.3–R3.1) |
| R3.1 | Critérios de software conferidos abaixo |
| R2.1 | Critérios de software conferidos abaixo |
| R2.2 | Critérios de software conferidos abaixo |
| R4.1 | Critérios de software conferidos abaixo |
| R4.2 | Critérios de software conferidos; verificação interativa no aplicativo pendente (A-08) |
| R5.1 | Critérios de software conferidos abaixo |
| R5.2 | Critérios de software conferidos; lacuna de relógio civil corrigida (A-03); habilitação física em decisão (A-04) |
| R6.1 | Software aceito com ressalvas A-06/A-07/A-08; recibo da revisão final abaixo |
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

## R1.3 — Captura e restauração completas

Fontes examinadas: `KlaRecipePulseLifecycle.cs`, `RecipeAssayRestoration.cs`, `RecipeEngine.Safety.cs` e runner comum. Testes no TRX desta revisão: `RecipeAssayRestorationTests` (53 casos, incluindo teorias) e `KlaTestRunnerTests` (33).

| Requisito | Prova examinada | Conclusão |
|---|---|---|
| Snapshot capturado após quiescência; desejado, transporte e medição separados | `Capture_merges_prior_configuration_and_separates_desired_transport_and_measurements`; `Snapshot_roundtrip_preserves_authority_but_changed_configuration_is_refused_before_sending` | Comprovado |
| Restaurar rotas, N/Q, modos e OFF em sucesso, falha e cancelamento | `Full_return_restores_route_modes_configuration_and_off_without_releasing_before_persistence`; `Common_runner_is_sealed_and_restores_snapshot_after_exit_at_each_protocol_stage` (dois protocolos, todas as fases) | Comprovado em software |
| Retorno abiótico deixa de ser "zero" | O pulso de receita usa `RecipeAssayRestoration` com o snapshot; `Release(true, 0, 0, ...)` ficou restrito ao fluxo manual | Comprovado |
| Recuperação com prazo próprio | `Cancelled_acquisition_does_not_cancel_an_independent_recovery_deadline`; `LifecycleRecoversWithItsOwnDeadlineAfterAcquisitionCancellation`; `KlaRecipePulseLifecycle` chama `RestoreAsync(..., CancellationToken.None)` | Comprovado |
| Confirmação compatível com o dispositivo; falha bloqueia retomada | `Wrong_or_missing_confirmation_never_counts_as_return`; `A_fabricated_confirmed_result_cannot_resume_a_captured_lease_without_physical_recovery`; `lease.Fail()` em toda recuperação não confirmada | Comprovado |
| Emergência nunca reativa saídas | `Emergency_during_route_wait_prevents_final_reference_and_late_return`; `Disconnection_during_recovery_produces_failure_and_invalidates_return_authority` | Comprovado |
| Frames enfileirados não religam após parada | Fila prioritária de R1.1 (`Emergency_invalidates_all_authorities...`) e `AutonomousAcquisitionSealsCommandsOnCancellationOrPreparationFailure` | Comprovado em transporte simulado |

Ressalva A-05: quando o retorno não é confirmado, `SafeStopAndRelease` envia motor 0, `FlowSafeStop` e `oxygenMonitor=0`. No protocolo biótico isso também interrompe aeração e agitação do cultivo; ver decisão pendente.

## R3.1 — Persistência como requisito de avanço

Testes: `KlaAttemptPersistenceTests` (14), `DurableWriterTests` (3), `BackgroundFileWriterTests` (9) e `KlaAssayApiTests` (15).

| Requisito | Prova examinada | Conclusão |
|---|---|---|
| Barreira propaga falhas anteriores | `Durable_barrier_propagates_prior_failure_without_poisoning_an_independent_session` | Comprovado |
| Persistir antes da atuação | `PreparationWriteFailureCannotEmitAcquisitionCommands`; `PreparationBarrierPrecedesAnyActuationAndRechecksTelemetry` | Comprovado |
| Falha durante/depois da corrida não produz recibo | `FailureDuringRawRecordingPreventsTerminalReceiptButPreservesPreparation`; `CheckpointWriteFailureCannotProduceReceipt`; `MissingRawDataOrMissingPreparationCannotProduceTerminalReceipt` | Comprovado |
| Reinício sem repetir pulso, com orçamento preservado | `ReconciliationPreservesBudgetAndDoesNotInferPhysicalRecoveryAfterInterruption`; `ApiReconciliationPersistsReservedAttemptWithoutRedispatchOnReopen`; `Reopening_a_reserved_request_never_actuates_and_blocks_new_pulses` | Comprovado |
| Erro de disco separado da evidência física | R0.2 `Terminal_disk_failure_preserves_confirmed_physical_return...`; `KlaRecipeAssayExecution` só devolve a reserva após o checkpoint terminal | Comprovado |
| Recibo real consumido pela devolução | `ReturnPersistedAsync` lê preparação e terminal no armazenamento comum antes de `ConfirmRecipeReturn` | Comprovado |

## R2.1 — Adaptador de pulso

Testes: `KlaRecipeAssayExecutionTests` (10), `KlaRecipeExecutionRouterTests` e `KlaAssayApiTests`.

| Requisito | Prova examinada | Conclusão |
|---|---|---|
| Runner, análise e dados comuns, sem diálogo | `ApiPulseUsesCommonRecoveryAnalysisAndPersistence` (dois protocolos) | Comprovado |
| `Completed` não significa réplica aceita | A execução grava `KlaOperatorDecision.Pending` e decisão com autor `AutomaticPolicy`/`AwaitingSelection` | Comprovado |
| Request duplicado em Running/terminal | `Duplicate_id_never_dispatches_twice_and_completed_reconnect_does_not_restart` | Comprovado |
| Cancelamento com recuperação | `Cancellation_waits_for_recovery_and_blocks_a_second_pulse` | Comprovado |
| Produção fechada | `FactoryRejectsPhysicalEnvironmentOrPhysicalCapability`; `App.ConfigureServices` só marca ambiente isolado com `--kla-test-file` | Comprovado |

## R2.2 — Matriz e decisão automática

Testes: `KlaRecipeOrchestratorTests` (30), `KlaRecipeAttemptDeciderTests` (16), `KlaRecipeSequenceTests` (7) e `KlaRecipeSelectionPersistenceTests` (6).

| Requisito | Prova examinada | Conclusão |
|---|---|---|
| Abiótico/Biótico × Único/Múltiplos sem operador | `Real_matrix_runs_restores_recaptures_and_persists_without_dialogs` (teoria 2×2) | Comprovado |
| Primeira tentativa aceitável; nunca o maior valor | `First_valid_attempt_is_selected_without_operator_approval` | Comprovado |
| Condicional recusado por padrão | `Conditional_requires_all_reasons_explicitly_allowed_and_valid_our_when_required` | Comprovado |
| Repetição apenas por motivos estruturados habilitados | `Only_known_enabled_reasons_allow_retry`; mapeamento fechado em `RetryReason` | Comprovado |
| Esgotamento termina automaticamente | `Second_attempt_requires_authorized_retry_and_stops_at_replicate_limit`; orquestrador termina por orçamento ou prazo | Comprovado |
| Falha ou retorno ausente nunca seleciona | `Failure_never_selects_even_when_numeric_quality_is_valid`; `Missing_receipt_failed_return_and_wrong_snapshot_do_not_advance` | Comprovado |
| Espera com controle devolvido e recaptura | O orquestrador libera o escopo (`ReleasePendingPreparation`) antes de aguardar e recaptura em `PrepareAsync` | Comprovado |

## R4.1 — Periodicidade e grupo paralelo

Testes: `RecipePeriodicExecutorTests` (10), `RecipeCascadePeriodicGroupTests` (6), `RecipeAutonomousEngineTests` (18) e `RecipePeriodicKlaIntegrationTests` (26).

| Requisito | Prova examinada | Conclusão |
|---|---|---|
| 2/6/10 h com mudança UTC | `Runs_at_two_six_ten_hours_despite_utc_changes`; o executor usa `GetTimestamp` | Comprovado |
| Ensaio atravessando slot, sem sobreposição | `Target_crossing_slot_keeps_original_cadence_and_never_overlaps` | Comprovado |
| Pausa e atraso pulam slots sem rajada | `Pause_and_delayed_wakeup_skip_slots_without_catchup`; `Pause_during_target_recovers_before_parking_until_next_slot` | Comprovado |
| Saída da cascata durante ensaio aguarda recuperação | `Group_owner_exit_cancels_target_and_waits_for_recovery`; `Cascade_exit_pause_or_emergency_during_common_assay_awaits_terminal_group` | Comprovado |
| Emergência e falha do grupo | `Group_fault_or_emergency_waits_for_independent_recovery`; `Unreturned_assay_authority_is_explicitly_stopped_and_cannot_report_success` | Comprovado |
| Diário do slot antes do disparo | `Failed_start_record_prevents_target_dispatch`; `Cancellation_record_is_written_only_after_target_recovery` | Comprovado |

## R4.2 — Blocos, editor e resultados

Testes: `RecipeAutonomousBlockConfigurationTests` (21), `RecipePeriodicTopologyTests` (17), `KlaRecipeApplicationHostTests` (11), `RecipeOperationalProfileEditorTests`, `RecipeLiveAssayViewModelTests`, `KlaAutomaticResultsTests`, `ReceitasViewModelTests` (28) e `RecipeEditorRenderingTests` (16).

| Requisito | Prova examinada | Conclusão |
|---|---|---|
| Salvar/reabrir receitas antigas e novas | `Protocol_and_conditions_survive_roundtrip_with_contextual_fields`; `RecipeExampleTests` (7 arquivos) | Comprovado |
| Campos inativos fora do request | `RoundtripPreservesIndependentTimesAndIgnoresInactiveInputs`; `Protocol_switch_updates_choices_and_context_fields_without_promoting_foreign_choice` | Comprovado |
| Topologia, recursos e ciclos validados antes de iniciar | `Unsafe_topology_is_rejected_before_execution`; `Parallel_writes_to_assay_resources_are_rejected_but_independent_references_remain_allowed`; `Legacy_exit_condition_cannot_be_used_as_periodic_branch` | Comprovado |
| Fluxo sem diálogo; tentativas ruins visíveis | `Summary_keeps_every_attempt_and_independent_science...`; histórico renderizado nos dois protocolos e temas | Comprovado |
| Navegação receita → sessão | `Live_progress_renders_with_terminal_navigation_separate_from_active_acquisition`; recibo `receitas-r61/NAVEGACAO_RESULTADOS.md` | Comprovado |
| Capacidades indisponíveis sem habilitar atuação | `Unavailable_host_shows_saved_reference_without_inventing_capability`; `Physical_environment_cannot_import_isolated_capability` | Comprovado |

Ressalva A-08: nenhuma execução interativa do fluxo autônomo no aplicativo (modo `--kla-test-file`) foi registrada.

## R5.1/R5.2 — Rampas

Testes: `LinearSetpointRampTrajectoryTests`, `LinearSetpointRampExecutorTests`, `RecipeRampActiveClockTests` (3 após esta revisão), destinos (temperatura, motor, vazão, pH/pressão, quadro completo e cascata), `RecipeRampBlockRunnerTests` (16), `RecipeRampTerminalPersistenceTests`, `RecipeRampRecoveryCoordinatorTests`, `RecipeRampPreparationTests` e `RecipeRampGuardedDestinationTests`.

| Requisito | Prova examinada | Conclusão |
|---|---|---|
| Tempos distintos, subida/descida/constante, alvo quantizado | `IndependentDurationsReachExactQuantizedTargetsAndKeepOxygenDestination`; `CompletionUsesTheSameQuantizedTargetAsTheTrajectory` | Comprovado |
| Trajetória inválida rejeitada (OFF, pH não representável) | `MissingReferencesOffCrossingsInvalidQuantizationAndTimeAreRejected`; `UnrepresentablePositivePhCannotObtainADurableStartReceipt` | Comprovado |
| Cadência sem rajada, pausa congela, confirmação final | `CadenceSkipsOverdueValuesFreezesOnPauseAndAwaitsFinalConfirmation` | Comprovado |
| Relógio civil alterado | **Lacuna encontrada nesta auditoria**, corrigida por `CivilClockChangesNeitherAdvanceNorRewindTheRamp` (A-03) | Comprovado após correção |
| O₂ apenas na referência da cascata; `MonitorReference` recusado | `StoredMonitorRampIsRejectedWithoutSilentCascadeMigration`; `AssociationAppearsOnlyForCascadeOxygen...` | Comprovado |
| Conflito com kLa/cascata; retomada sem salto | `SamplesInsideLeaseAndCannotCompleteDuringAssayOrRecipePause`; recibo `RETOMADA_APOS_ENSAIO.md` | Comprovado |
| Cancelamento, perda de posse/comunicação, emergência | `CancellationHonorsSavedPolicyAndEmergencySuppressesRestoration`; recibo `PERDA_COMUNICACAO_E_POSSE.md` | Comprovado |
| Avanço só após recibo terminal | `RecipeAdvancesPastRampOnlyAfterItsTerminalReceiptExists` | Comprovado |

Ressalva A-04: a composição do aplicativo habilita rampas também no ambiente físico, enquanto o plano condiciona a habilitação física à qualificação R6.2.

## R6.1 — Regressão integrada

Primeira regressão desta auditoria (Release, sobre `a5470ae`): **2507 aprovadas e 1 falha**. `ScreenshotCaptureTests.Render_calibration_view_across_dpi_scales(125dpi)` produziu imagem vazia; a classe passou isolada em duas repetições (23/23). Causa: 16 classes compartilham o dispatcher STA de `WpfRenderingHost`, e `PumpDispatcher` executa frames aninhados nos quais trocas de tema e capturas de outras classes paralelas podiam entrar. A correção A-01 foi aplicada. A regressão final desta revisão e a build Release estão registradas em [PLANO_FINALIZACAO.md](PLANO_FINALIZACAO.md).

## Achados desta auditoria

| ID | Gravidade | Achado | Solução | Estado |
|---|---|---|---|---|
| A-01 | Média | Falha intermitente de captura WPF na regressão completa, por concorrência no dispatcher compartilhado | Coleção xUnit `WpfRendering` sem paralelismo para as 16 classes que usam o host | Corrigido |
| A-02 | Baixa | Avisos reais do compilador: CS8602 em `KlaRecipeOrchestrator.cs:181` e CS9124 em `KlaRecipeOperationalProfiles.cs:82-83` | Verificação explícita de nulo; uso das propriedades em vez dos parâmetros capturados | Corrigido |
| A-03 | Média | Critério R5 "relógio de parede alterado" sem teste dedicado | Teste `CivilClockChangesNeitherAdvanceNorRewindTheRamp` | Corrigido |
| A-04 | Alta (liberação) | Rampas executam no ambiente físico sem gate de qualificação, divergente de R6.2 | Gate por destino qualificado (como os perfis de kLa) **ou** decisão registrada em `DECISIONS.md` liberando rampas como extensão de `SetSetpoint` | Decisão do autor |
| A-05 | Média (biótico) | Retorno não confirmado aciona parada de N/Q/O₂ e interrompe a aeração do cultivo | Política de falha por protocolo: no biótico, comando de segurança de ar ao reator na condição do snapshot antes de liberar; manter parada total no abiótico | Decisão antes de R6.2 |
| A-06 | Média (gate 0.25.0) | 2120 avisos IDE0011 (chaves) na compilação; o gate exige zero avisos e `dotnet format` limpo | Recomendado: `csharp_prefer_braces = when_multiline:suggestion`, coerente com o estilo adotado, e zero avisos CS/CA; alternativa: commit isolado de formatação | Decisão do autor |
| A-07 | Baixa | Testes de renderização regravam PNGs versionados a cada execução (cerca de 60 arquivos modificados) | Gravar em diretório de artefatos e atualizar evidência só com `OPENTEC_UPDATE_EVIDENCE=1` | Planejado |
| A-08 | Média | Sem verificação interativa do fluxo autônomo no aplicativo; o perfil isolado precisa ser criado manualmente para o `InstallationId` do workspace | Roteiro de smoke em `--kla-test-file` e gerador externo de perfil sintético de simulação | Planejado |
| A-09 | Baixa | Cabeçalho do plano, `EXECUCAO_RECEITAS.md` e `CURRENT_STATUS.md` desatualizados | Atualização documental | Corrigido |
| A-10 | — | E7 (bancada e cultivos independentes) e R6.2 pendentes | Roteiro de bancada; não substituível por simulação | Pendente físico |
