# R5.2 — Confirmação da rampa de vazão

`RecipeRampFlowDestination` aplica a referência pela rota existente do engine e confirma vazão medida dentro da tolerância durante uma janela estável. Reutiliza o mecanismo de prazo, pausa, cancelamento, sequência de mensagens e descarte de recibos da confirmação de motor e temperatura.

A confirmação exige fluxômetro online, leitura finita não negativa, eco da referência, comando sem pendência e identificação positiva com ACK igual à identificação atual. As duas válvulas devem corresponder ao caminho para o reator na configuração de gás capturada na aplicação. Alterar essa configuração invalida a confirmação. Uma referência zero exige eco exatamente zero, válvulas fechadas e vazão residual dentro da tolerância; não conclui apenas porque o comando foi enviado.

`FlowFeedbackUpdated` exige que leitura, referência, estado online, pendência, identificação, ACK e válvulas estejam presentes e válidos no quadro atual. Valores retidos em mensagens parciais não podem formar uma confirmação. `FlowRateUpdated` conserva sua identificação específica de leitura recebida. Os valores numéricos e regras existentes de retenção/offline permanecem preservados.

Esses indicadores provam atualização do quadro recebido pelo aplicativo. Não acrescentam um timestamp de aquisição física ao protocolo nem substituem a qualificação de atualização do nó/Hub em bancada. O ACK é o da caixa de comandos atual, acompanhado do eco e da rota; não é um recibo individual do envio local.

Testes cobrem estabilidade nos alvos positivo e zero, exclusão de leitura anterior à aplicação, mensagens parciais, offline, comando pendente, ACK divergente, rota incorreta, eco divergente e vazão fora da tolerância. A execução geral do bloco permanece bloqueada até a composição dos destinos e a integração de reservas, recibos, retorno e cancelamento ao ciclo de vida.

Validação: 55 testes focados aprovados; regressão completa final com 2394 aprovados, sem falhas ([TRX](evidence/recipes-r52-flow-full.trx)). Release compilado em `D:/Temp/OpenTECHub-flow-confirm-release/`, com 0 erros e 1549 avisos na compilação incremental. Não houve qualificação física nesta entrega.
