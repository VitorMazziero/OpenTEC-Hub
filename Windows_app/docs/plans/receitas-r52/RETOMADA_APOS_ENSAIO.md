# R5.2 — Retomada da rampa após ensaio completo

A auditoria do cenário de grafo concorrente encontrou uma lacuna: os casos de ensaio concluído encerravam a cascata imediatamente após a gravação do resultado. Assim, comprovavam retorno e encerramento, mas não comprovavam avanço posterior da rampa.

Os casos concretos abiótico/biótico agora mantêm a cascata por mais cinco segundos virtuais após o resultado terminal. Consultam a referência real pela confirmação pública do engine, registram a referência inicial e exigem avanço posterior positivo e menor que 0,15 ponto percentual. Para a trajetória 80→90% em 1000 s, esse limite exclui compensação do tempo suspenso do ensaio. Depois encerram a cascata e continuam exigindo retorno verificado da rampa a 80%, recibo terminal, ramo cancelado sem executar a ligação seguinte e diário de agenda concluído.

Validação: **26 cenários aprovados, zero falhas**, em `../receitas-r61/evidence/recipes-r52-resume-after-assay-final.trx`. Comando: `dotnet test Windows_app/tests/OpenTECHub.Tests/OpenTECHub.Tests.csproj --no-restore -p:EnableSourceLink=false -p:SelfContained=false --filter FullyQualifiedName~RecipePeriodicKlaIntegrationTests --logger trx --results-directory Windows_app/docs/plans/receitas-r61/evidence -v quiet` (nome do TRX especificado na execução). A evidência é transporte simulado, aquisição/análise/restauração/persistência concretas; não é confirmação física.

## Auditoria parcial dos critérios

- R5.1: `LinearSetpointRampTrajectoryTests` inspecionado cobre subida de N, descida de Q, O₂ constante, durações independentes e final quantizado; rejeita cruzamento OFF e quantização inválida.
- Cadência: `LinearSetpointRampExecutorTests.CadenceSkipsOverdueValuesFreezesOnPauseAndAwaitsFinalConfirmation` inspecionado pula quadros atrasados sem rajada, congela pausa e aguarda confirmação antes do término.
- Pausas sobrepostas/repetidas: `RecipeRampActiveClockTests` inspecionado verifica dois ciclos, combinação receita/ensaio e ponto preservado após espera longa.
- Conflito direto: `RecipeResourceCoordinator.Ramp.cs` inspecionado recusa produtores conflitantes que não sejam a cascata associada, antes da captura; não concede automaticamente convivência a rampas N/Q.
- Retomada integrada após kLa: agora coberta nos dois protocolos por este incremento.

A lista acima é parcial. Rotas e confirmação por parâmetro, perda de comunicação/posse e a auditoria dos demais critérios devem ter sua evidência examinada antes de encerrar R5.1/R5.2. A regressão completa final de R6.1 e a bancada R6.2 continuam pendentes.
