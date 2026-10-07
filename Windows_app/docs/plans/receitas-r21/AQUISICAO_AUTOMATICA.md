# R2.1 — aquisição automática sobre o runner comum

`KlaRecipeAcquisition` inicia uma corrida do runner reservado, acompanha suas fases e devolve conclusão, cancelamento ou falha. Não chama aceitação/rejeição humana, não calcula kLa e não libera recursos. Cada objeto aceita uma única execução.

No encerramento, inclusive falha de preparação e cancelamento, retira o observador e sela os enqueues do runner antes da recuperação independente. O estado físico permanece `Pending` e a autoridade continua `KlaAssay`. Selar novamente um runner já encerrado não sobrescreve recuperação confirmada.

84 testes direcionados aprovados em `evidence/recipes-r21-acquisition-verified.trx`. Os novos testes verificam término automático pela telemetria do runner real, ausência de réplica aceita, cancelamento, falha anterior à atuação, recusa de repetição e ausência de comandos tardios após o encerramento. A matriz de recuperação abiótica/biótica existente também passou.

Entrega parcial: o executor `IKlaAssayExecution` ainda precisa unir preparação, aquisição, recuperação independente, análise comum, resultado persistido e devolução. O componente não está registrado para uso físico ou no editor. R2.1 permanece em andamento.
