# R5.2 — Aplicação e confirmação do quadro completo

`RecipeRampFrameDestination` compõe os componentes existentes de temperatura, motor, vazão e referência da cascata. Valida o conjunto de parâmetros e cada alvo antes do envio. Aplica o quadro inteiro pela chamada comum do engine, vinculada à execução atual e às rotas capturadas na mesma seção de exclusão. Os componentes recebem a aplicação aceita sem reenviar cada parâmetro separadamente.

A confirmação ocorre em paralelo, respeitando as políticas próprias de cada componente. O quadro só publica os recibos quando todos concluíram, a revisão aplicada continua a mesma e os retornos medidos correspondem ao mesmo quadro de telemetria atual. Enquanto um componente espera seus irmãos, leituras novas válidas atualizam sua evidência; uma leitura inválida ou lacuna descarta o recibo e exige confirmação novamente. Isso permite janelas de estabilidade diferentes e evita combinar motor/vazão anteriormente corretos com temperatura que só chegou depois, quando o motor já saiu da faixa.

O₂ usa a confirmação da referência do controlador associado, sob a barreira da cascata e com posse dos atuadores; não exige que OD medido seja igual ao setpoint. Temperatura pode compor esse quadro. Conflitos entre uma cascata ativa e rampas diretas de N/Q continuam recusados pelo engine antes de atuar.

Pausa, cancelamento, reaplicação ou divergência do quadro impedem a publicação de recibos parciais. O objeto deve ser envolvido por `RecipeRampGuardedDestination` para calcular a trajetória dentro da reserva do produtor. Sua presença não habilita a execução do bloco: faltam integrar o ciclo de vida, os recibos de armazenamento e o retorno/cancelamento. Incremento posterior: [pH/pressão](CONFIRMACAO_PH_PRESSAO.md) agora participam da composição com políticas explícitas e banda de pH preservada.

Testes cobrem um envio comum para T/N/Q, referências inválidas no meio/fim do quadro sem envio de prefixo válido, combinação de confirmações de quadros diferentes, pausa/cancelamento sem recibos parciais e composição de temperatura com uma cascata ativa de O₂. Nenhuma qualificação física é atribuída a esses testes.

Validação final: 17 testes focados dos componentes/quadro aprovados; regressão completa de 2407 testes aprovada, sem falhas ([TRX](evidence/recipes-r52-frame-recovery-final-full.trx)), após [correção separada da sincronização do teste de retorno de kLa](SINCRONIZACAO_TESTE_RETORNO_KLA.md). Release compilado em `D:/Temp/OpenTECHub-ramp-frame-release/`, com 0 erros e 1599 avisos na compilação incremental.
