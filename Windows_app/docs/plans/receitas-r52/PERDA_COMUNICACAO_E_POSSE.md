# R5.2 — Perda de comunicação e posse durante a rampa

Dois casos novos do executor integrado usam transporte substituído, captura inicial durável, destino de temperatura, produtor/reservas e armazenamento terminal reais. Após o primeiro comando de rampa (28 °C, referência anterior capturada de 25 °C), interrompem a conexão ou transferem a posse de temperatura ao usuário.

- Desconexão: o arbitrador revoga a autoridade com causa de aborto seguro. O executor grava `EmergencyStopped` e `SuppressedForEmergency`, sem confirmação de retorno.
- Transferência manual: grava `Faulted` e `Failed`, sem confirmação de retorno.
- Ambos: nenhum comando adicional é enviado, a referência registrada permanece 28 °C, a posse passa a Manual e o registro terminal é reaberto pelo armazenamento.

Validação focada: **16 casos aprovados, zero falhas**, em `../receitas-r61/evidence/recipes-r52-link-and-ownership.trx`. Comando: `dotnet test Windows_app/tests/OpenTECHub.Tests/OpenTECHub.Tests.csproj --no-restore -p:EnableSourceLink=false -p:SelfContained=false --filter FullyQualifiedName~RecipeRampBlockRunnerTests --logger trx --results-directory Windows_app/docs/plans/receitas-r61/evidence -v quiet` (nome do TRX especificado na execução).

## Auditoria de confirmação por destino

Código dos testes inspecionado nesta revisão:

- Temperatura: `RecipeRampTemperatureDestinationTests.ConfirmsTheReactorThroughBothRoutesAndRejectsPartialStaleOrPendingFeedback`, nativo/banho, rejeita medição antiga/parcial/inválida, referência diferente e comando pendente no banho; requer estabilidade e registra feedback de processo.
- Motor: `RecipeRampMotorDestinationTests.RequiresNewStableMotorSamplesAndPreservesMeasuredEvidence` exige amostra nova e estável, rejeita servo pendente e registra valor medido/tolerância. `MissingFeedbackTimesOutAndCancellationDoesNotInventAReceipt` testa timeout, cancelamento, pausa/retomada sem inventar recibo.
- Vazão: `RecipeRampFlowDestinationTests.RequiresAcknowledgedRouteAndFreshStableMeasuredFlow`, positivo/OFF, rejeita canal offline, ACK incorreto, referência/rota errada e amostra parcial; confirma por valor medido estável.
- Disponibilidade: `RecipeEngine.RampMotor.cs` invalida confirmação após mudança de rota, execução, recursos ou posse. `CommandArbiter.OnInnerStateChanged` revoga proprietários não manuais após desconexão.

Essas provas são de software com feedback injetado; não qualificam banho, motor ou fluxômetro físicos. A auditoria global e R6.2 continuam pendentes.

Complemento inspecionado: `RecipeRampSensorModuleDestinationTests.CompleteFramePreservesPhBandAndConfirmsBothModuleParameters` confirma pH/pressão no quadro e preserva banda de pH; `RecipeRampFrameDestinationTests.AppliesOneCompleteCommandAndNeverCombinesConfirmationsFromDifferentFrames` verifica N/Q/temperatura no mesmo comando, rejeita feedback de outro quadro e exige três confirmações correspondentes antes da conclusão.

Regressão completa conjunta após a correção de proteção do histórico: **2508 aprovações, zero falhas**, em `../receitas-r61/evidence/recipes-r61-readonly-and-link-full.trx`. Release em `D:/Temp/OpenTECHub-readonly-link-release/`, zero erros e 1881 avisos. A proteção do histórico está em `1cfb1ff`; este incremento acrescenta os dois cenários de perda de comunicação/posse. Nenhuma atuação física executada.
