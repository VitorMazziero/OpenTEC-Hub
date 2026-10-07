# R2.1 — pulso autônomo sobre o runner comum

Implementada e validada em software. Componentes anteriores: `d66ae0c` (preparação), `b16108b` (aquisição), `ecf0f68` (recuperação). Esta entrega conecta esses componentes à API E6 por `KlaRecipeAssayExecution` e registra sua fábrica na aplicação.

## Comportamento entregue

- Um executor por pulso, com identidade e condição conferidas contra o request congelado. O runner, os dados brutos e a análise determinística são os mesmos do ensaio comum.
- Checkpoint com identidades reais da sessão/corrida antes do primeiro comando. Observação inicial nova e preflight biótico obrigatórios; nenhum diálogo ou aceitação humana automática.
- Prazo de aquisição separado do prazo de recuperação. Cancelamento ou expiração da aquisição não cancelam a recuperação do estado capturado.
- Análise e resultado salvos com decisão do operador ainda pendente. O checkpoint identifica autoria automática e seleção pendente; a seleção da matriz pertence a R2.2.
- Retorno confirmado e recibo terminal verificável precedem a devolução da reserva e a retomada dos produtores. Falha de escrita bloqueia continuidade e preserva a evidência física de retorno.
- A fábrica exige ambiente isolado e capacidades isoladas; declarar capacidades não habilita operação física. A aplicação normal permanece fechada para esse adaptador, enquanto playback pode construir escopos de teste.

## Validação

Regressão completa: **2093 aprovados, zero falhas, 39 s** em `evidence/recipes-r21-full.trx`. Execução direcionada: **8 aprovados** em `evidence/recipes-r21-executor-protocols.trx`.

Comando da regressão: `dotnet test Windows_app/tests/OpenTECHub.Tests/OpenTECHub.Tests.csproj -p:SelfContained=false --no-restore -v quiet --logger 'trx;LogFileName=recipes-r21-full.trx' --results-directory Windows_app/docs/plans/receitas-r21/evidence`.

Os testes percorrem conclusão automática abiótica e biótica, cancelamento, prazo vencido, falha terminal do escritor, partida repetida e reabertura do diário sem repetir atuação. Conferem hash dos dados brutos, recuperação, recibo, ausência de aprovação humana e fechamento da fábrica física.

## Correção do erro observado

O diagnóstico de `CancellationTokenSource` descartado foi corrigido no commit `b83bd07`: o salvamento adiado captura o token antes de cancelar uma operação anterior, e a operação proprietária descarta sua própria fonte. A escrita é serializada, e falhas inesperadas das tarefas são observadas. A regressão inclui os testes de atualizações rápidas e concorrentes das configurações. Este recibo não afirma novo teste visual do executável instalado.

## Limites e continuação

R2.2 deve acrescentar fila, seleção, repetição limitada e registro durável da decisão, usando o orçamento cumulativo E6. R4 ainda deve conectar os blocos ao editor/executor de receitas. Operação física e qualificação de bancada permanecem pendentes em R6.2.
