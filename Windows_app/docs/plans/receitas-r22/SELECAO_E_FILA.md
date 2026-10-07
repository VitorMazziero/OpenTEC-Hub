# R2.2 — seleção durável, fila e escopos

Segunda entrega parcial de R2.2. Acrescenta persistência de seleção ao armazenamento comum, contadores com autoria automática, fila pura e roteamento de escopos por um único diário E6. Ainda não conclui o orquestrador que inicia sucessivas corridas reais.

## Comportamento entregue

- `KlaRecipeSelectionCheckpoint` registra observação E6, decisão, histórico e orçamento usado na decisão. A leitura recomputa a política; recusa decisão divergente, JSON ambíguo e campos desconhecidos.
- `KlaTestStore.PersistRecipeSelectionAsync` cruza request, resultado, recibo terminal e hash dos dados brutos. Usa a mesma exclusão e barreira durável dos checkpoints. Uma decisão conflitante para a mesma identidade é recusada. Histórico precisa corresponder às seleções já gravadas. Nenhuma fonte paralela de dados científicos foi introduzida.
- A seleção imutável antecede a atualização do manifesto, tabela de condições e resumo. O recibo de seleção só retorna após concluir e verificar a gravação. Reabertura reconstrói autoria e contadores a partir da seleção, inclusive quando o manifesto salvo ainda contém os contadores anteriores.
- `KlaTestRunSummary.AutomaticDecision` mantém autoria automática separada de `OperatorDecision`, que continua pendente. `KlaSequence` reconhece seleções e réplicas esgotadas sem simular aceitação humana. Sessões legadas continuam no caminho manual.
- `KlaRecipeSequence` percorre condições na ordem e réplicas planejadas; repetição permanece na réplica corrente. Recusa lacunas, histórico duplicado, autoria/perfil divergente e nova tentativa depois de uma decisão terminal. Aplica a política de parada ou continuação sem resultado e distingue falha de recuperação, persistência, cancelamento, falha operacional e resultado inconclusivo.
- `ReadCultivationBudget` consulta o diário sem reservar. A partida usa a mesma projeção; limites anteriores mais estritos e exposição reservada sobrevivem à reabertura.
- `KlaRecipeExecutionRouter` permite registrar um executor reservado para cada request em um único diário E6. Confere payload/capacidades, impede sobreposição e remoção durante recuperação. Histórico concluído não precisa de novo escopo para ser observado. Operação física permanece fechada.

## Evidências

`recipes-r22-selection-boundaries.trx`: **33 testes aprovados**, incluindo seleção e projeção no adaptador real dos dois protocolos, reabertura, falha de escrita síncrona/assíncrona, resultado bruto alterado, JSON duplicado, fila dos quatro modos e leitura dos limites persistidos.

`recipes-r22-routing.trx`: **8 testes aprovados** na fila e no roteador; dois escopos sucessivos usam o mesmo diário, consomem o orçamento cumulativo e não são repetidos após reabertura. O roteador usa executores isolados de contrato; a atuação comum é coberta separadamente pelos testes do adaptador.

Comando da regressão final: `dotnet test Windows_app/tests/OpenTECHub.Tests/OpenTECHub.Tests.csproj -p:SelfContained=false --no-restore -v quiet --logger 'trx;LogFileName=recipes-r22-selection-routing-full.trx' --results-directory Windows_app/docs/plans/receitas-r22/evidence`.

Resultado final: **2122 testes aprovados, zero falhas, 37 s**. Contagens não se somam. Esta evidência qualifica o software no recorte descrito; não substitui execução de toda a matriz pelo orquestrador nem bancada física.

## Próxima execução necessária

Implementar o orquestrador sobre estes componentes: consultar orçamento, esperar com cascata retomada, adquirir recursos, capturar um novo snapshot, congelar e registrar o pulso, executar pela mesma API, persistir seleção e só então avançar a fila. Corrigir o agregado `KlaRecipeResult.ValidateAgainst`, que ainda compara todas as tentativas com um snapshot único, para conferir o vínculo individual de cada pulso recapturado. Validar execução de toda a matriz no runner comum, limites de tempo/exposição e cancelamento durante espera/cessão. Não habilitar blocos no catálogo antes desse aceite. R4/R5/R6 continuam pendentes.
