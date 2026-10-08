# R5.2 — Executor e recibos duráveis

`LinearSetpointRampExecutor.ExecutePersistedAsync` conecta a trajetória aos registros inicial e terminal. O tempo ativo fica suspenso durante a gravação e validação inicial; apenas esse motivo de suspensão é removido ao terminar a preparação. Uma pausa da receita permanece válida.

A trajetória usa a configuração relida do registro inicial. A quantização do destino precisa coincidir com a representação da rota capturada. Cada executor aceita uma única execução, incluindo a preparação, para impedir que sua reutilização crie registros iniciais de corridas que nunca serão executadas.

O executor aguarda a confirmação dos destinos e grava o resultado terminal sem reaproveitar o token cancelável da aquisição. O armazenamento valida as evidências, grava e relê o resultado antes de retorná-lo. Aceite do transporte isolado não permite concluir.

Verificação: 57 testes de rampas e 2419 testes na regressão completa passaram. Os cenários novos cobrem falha de armazenamento sem comando, preservação de uma pausa independente, conclusão com resultado legível e rejeição de evidência apenas de transporte. Evidência: `evidence/recipes-r52-persisted-executor-full.trx`.

O aplicativo compilou em Release com zero erros, em `D:/Temp/OpenTECHub-persisted-executor-release/`.

Este incremento ainda não habilita o bloco no engine. A reserva durante a captura, as reservas de execução compatíveis com cessão ao ensaio e a recuperação ao cancelar continuam pendentes. O chamador precisa conservar a autoridade e executar essa recuperação quando o executor falha ou é cancelado.
