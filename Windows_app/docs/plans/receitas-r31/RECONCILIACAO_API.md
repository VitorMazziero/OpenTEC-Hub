# R3.1 — reconciliação persistida no diário E6

`IKlaAssayApi.ReconcileRecipeAttempt` integra o leitor de recibos ao diário durável. Só opera sem executor ativo e com vínculo de receita. Confere identidades e resultados da sessão comum, preserva o instante de início existente e registra uma cobrança conservadora quando há preparação persistida sem início no diário. Não repete aquisição. Resultado histórico confirmado não certifica recuperação atual: estados incertos continuam `Interrupted`, impedindo avanço e novo pulso.

O checkpoint possui exclusão por arquivo entre instâncias do armazenamento. Leitura rejeita propriedades desconhecidas e chaves duplicadas em qualquer nível. Os testes exercitam reabertura do diário, persistência da cobrança, resultado terminal histórico sem liberação de atuação, gravador concorrente e alteração da estrutura JSON.

Validação: 90 testes direcionados aprovados em `evidence/recipes-r31-api-reconciliation-final.trx`. Build da aplicação concluído sem erros. A regressão completa anterior tinha 2068 testes aprovados; não é apresentada como verificação das alterações desta entrega.

R3.1 continua em andamento: falta exercitar e integrar as fronteiras completas de falha no fluxo de produção antes de concluir seu aceite e avançar ao adaptador R2.1. Não libera atuação física autônoma.
