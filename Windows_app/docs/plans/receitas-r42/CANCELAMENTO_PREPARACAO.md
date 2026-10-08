# R4.2 — Cancelamento antes da primeira observação

O executor comum podia receber cancelamento enquanto esperava a leitura inicial. A aquisição ainda não havia criado corrida/checkpoint, mas a recuperação era executada; o término então falhava por ausência de preparação durável, mantendo a reserva fechada.

Após selar a aquisição, o runner agora cria um registro de preparação cancelada somente quando não há corrida. Não valida uma leitura inexistente nem inicia aquisição: congela a definição/condição, cria a pasta comum, marca recuperação pendente e grava o checkpoint antes da recuperação. Essa gravação usa seu próprio término aguardável, sem o token de aquisição já cancelado. A recuperação continua com prazo independente. Falha de registro é preservada no resultado do ciclo e impede liberação, mesmo se a recuperação física confirmar retorno.

O terminal usa o armazenamento, análise e recibos comuns. Dados brutos vazios e qualidade inconclusiva ficam explícitos; motivo acquisition_cancelled permanece estruturado. Retorno, checkpoint terminal e devolução persistida da reserva seguem a ordem existente. Este registro não constitui medição física nem autorização de repetição por si só.

Dez testes focados do executor aprovados: os dois protocolos cancelados antes da primeira observação confirmam ausência de comando de aquisição, zero amostras, resultado inconclusivo, recibo terminal, devolução e reabertura da seleção/API. Os cenários anteriores de término, prazo, cancelamento ativo e falha de persistência continuam aprovados. Integração da pausa independente no engine/orquestrador permanece pendente; a barreira e a evidência de decisão já existem.

Regressão completa: 2333 testes aprovados, zero falhas, 61 s. Compilação Release: zero erros.
