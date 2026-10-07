# R2.2 — primeiro componente: decisão automática

Entrega parcial de R2.2. `KlaRecipeAttemptDecider` decide sem comandos físicos, diálogos ou alteração da decisão humana. O chamador deve persistir essa decisão antes de avançar. Este componente não inicia outra tentativa.

Seleciona somente resultado numérico aceitável em pulso concluído, com retorno ao snapshot correto e recibo. Reutiliza `KlaRecipeQualityEvaluator` para qualidade condicional explicitamente permitida e OUR válido quando obrigatório. Cancelamento, interrupção ou falha não selecionam resultado, mesmo que sua qualidade numérica seja válida.

Repetição exige todos os motivos reconhecidos e autorizados, orçamento restante do cultivo, limite de réplica e tempo suficiente para o intervalo mínimo dentro do bloco. O orçamento restante deve vir do diário E6; o componente não cria nem reinicia um orçamento local.

Mapeamento conservador dos códigos já emitidos pelo núcleo científico:

| Código | Categoria do perfil |
|---|---|
| `invalid_or_short_window`, `insufficient_respiratory_window` | `InsufficientWindow` |
| `insufficient_signal_to_noise`, `respiratory_signal_too_small` | `ExcessiveNoise` |
| `nonconstant_rate_in_subwindows` | `UnstableCondition` |

Códigos desconhecidos, problemas operacionais e pressupostos sem verificação não geram repetição implícita. Histórico duplicado, pertencente a outra invocação ou sem cadeia contínua de repetição autorizada é recusado. Uma tentativa selecionada encerra a réplica.

Validação direcionada: **15 testes aprovados**, em `evidence/recipes-r22-decisions-final.trx`, incluindo Abiótico/Biótico × Único/Múltiplos, condicional, OUR, esgotamento, interrupção, recibo ausente, snapshot incorreto e histórico de repetição.

Regressão completa posterior: **2108 testes aprovados, zero falhas**, em `evidence/recipes-r22-regression-final.trx`. A execução anterior (`recipes-r22-decisions-full.trx`, preservada localmente) falhou na limpeza do teste integrado abiótico; o writer agora permanece vivo até encerrar a recuperação na cláusula `finally`. A repetição completa passou. Não houve novo teste visual do executável instalado.

Comando: `dotnet test Windows_app/tests/OpenTECHub.Tests/OpenTECHub.Tests.csproj -p:SelfContained=false --no-restore --filter FullyQualifiedName~KlaRecipeAttemptDeciderTests -v quiet --logger 'trx;LogFileName=recipes-r22-decisions-final.trx' --results-directory Windows_app/docs/plans/receitas-r22/evidence`.

Ainda necessários para concluir R2.2: diário durável da seleção, fila/counters com autoria automática, dispatcher de escopos sobre um único orçamento E6, espera com cascata retomada e nova captura/reserva antes de cada pulso. A matriz completa e a habilitação física não são declaradas concluídas.
