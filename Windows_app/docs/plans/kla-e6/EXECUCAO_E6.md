# E6 — Contrato para receitas futuras

## API entregue

`IKlaAssayApi` oferece Create, StartAsync, Observe, CancelWithRecoveryAsync e GetResult. Cada solicitação representa uma condição, uma réplica e um pulso. A matriz e as tentativas de E5 não são transformadas em repetições automáticas da receita.

A solicitação inclui ID estável, cultivo, definição E1 (protocolo, parâmetros, contexto e limites operacionais), origem da condição, prazo de início, deadline da aquisição, limites acumulados do cultivo e política de falha. `AtCurrentCondition` recebe N/Q observados pelo chamador e congela a condição; não lê valores variáveis durante a execução. A faixa configurada é validada antes do despacho.

Create grava o pedido sem comandar atuadores. Repetir o mesmo ID retorna o registro existente; reapresentar esse ID com parâmetros diferentes é recusado. Start reserva e grava antes de chamar o executor. Uma falha de gravação anterior ao despacho impede a execução. Um processo detém exclusivamente o journal. Reabrir uma reserva em andamento marca Interrupted e bloqueia novos pulsos; nunca reinicia automaticamente nem presume recuperação.

## Cancelamento, retorno e resultado

O token e o deadline encerram a aquisição. O contrato `IKlaAssayExecution` exige que o executor realize recuperação com seu próprio prazo limitado, mesmo após cancelamento, e só retorne depois de confirmar sucesso/falha. CancelWithRecoveryAsync espera esse retorno. Fechar a API durante execução é recusado. Erros do executor ou retorno não confirmado produzem RestorationFailed e impedem novo despacho.

Os resultados distinguem Completed, Inconclusive, Cancelled e RestorationFailed. Created, Running, Skipped e Interrupted descrevem o despacho. Em biótico somente Confirmed comprova retorno; NotRequired não é aceito. Resultado científico e decisão do operador permanecem independentes. A API não aceita resultados ou publica mapas automaticamente.

HaltRecipe é o padrão. ContinueAfterConfirmedReturn permite seguir após inconclusão/cancelamento apenas quando o retorno estiver confirmado (ou dispensado em abiótico). Falha ou interrupção não autoriza continuar.

## Exclusão, intervalo e exposição

Há uma execução por API e journal. O adaptador de produção deverá usar o mesmo `KlaAssayCoordinator` e arbitragem E2 da interface; outro journal não concede posse de atuadores. A cascata deve ser suspensa/retomada por E2, e uma receita concorrente que detenha os atuadores deve ser encerrada ou transferir sua posse explicitamente. A API não toma essa posse à força.

Limites por cultivo sobrevivem à reabertura. São contabilizadas reservas conservadoras completas, incluindo confirmações de comutação, mesmo se a retirada real for mais curta. Novas solicitações podem apertar os limites, mas não afrouxar os já utilizados. O intervalo começa no término da recuperação, tomando o maior intervalo entre política do cultivo e parâmetros do protocolo. Solicitação adiada conserva o ID, não reserva outro pulso e ainda pode expirar.

## Periodicidade e integração futura

`KlaPeriodicSchedule.Latest` retorna no máximo a ocorrência mais recente, a quantidade de ocorrências puladas e o próximo prazo. O integrador registra esses valores no log da receita. Se ocupado, mantém essa ocorrência adiada somente até seu prazo; após expirar registra Skipped. Não acumula pulsos atrasados. Réplicas imediatas pertencem à fila E5; periodicidade pertence a esta agenda.

O futuro bloco `KlaAssay` deve ser acrescentado ao fim de NodeType para preservar os valores anteriores, ter parâmetros tipados no catálogo e editor, e ser recusado pelo validador e engine até haver liberação operacional versionada. O engine deverá criar uma solicitação com ID persistido por instância de receita/nó/ocorrência, observar a API, encaminhar cancelamento com recuperação e aplicar MayContinueRecipe. O retorno físico deve preceder a liberação/transferência da posse e a execução do próximo nó. Não se deve acionar Start a cada chegada de telemetria.

**Limite desta entrega:** contrato, implementação de despacho durável e testes de API com executor simulado. Nenhum adaptador de atuação ou bloco executável foi registrado no aplicativo. A ligação da API ao runner/receitas e o editor periódico dependem da validação operacional E7; o ponto de extensão IsValidated permanece fechado para produção. Não confundir os testes deste serviço com comprovação de recuperação física do equipamento.

## Evidência

O recibo `validation-receipt.json` registra o TRX e o escopo final. Os testes exercitam idempotência, reconexão sem atuação, prazos, cancelamento aguardando recuperação, bloqueios, limites persistentes, política de continuidade e periodicidade sem rajada.
