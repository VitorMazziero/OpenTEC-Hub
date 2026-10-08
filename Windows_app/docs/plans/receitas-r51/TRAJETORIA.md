# R5.1 — Cálculo de trajetória

LinearSetpointRampTrajectory congela os inícios confirmados ou explícitos e valida todas as linhas antes de fornecer amostras. Cada linha usa seu próprio tempo final relativo ao tempo ativo comum; atraso na chamada não cria fila de valores passados. A partir do fim da linha, mantém o alvo quantizado capturado. A referência de O₂ conserva seu destino explícito.

A quantização é fornecida pelo futuro adaptador do destino, sem inventar resolução universal. Valores quantizados inválidos e trajetórias que atravessam a faixa entre desligado e operação são recusados. O serviço recebe segundos ativos; congelamento em pausa e cadência de comandos pertencem ao executor.

Dois testes focados aprovados: subida, descida, constante, tempos distintos, alvos arredondados, amostra atrasada, independência do dicionário de captura, O₂ de cascata, referência ausente, faixa OFF, quantização inválida e tempo não finito/negativo.

R5.1 permanece em implementação: faltam adaptadores das rotas existentes, referência central da cascata, reserva e cadência. R5.2 fará integração ao engine/editor, persistência e confirmação por parâmetro. A verificação visual restante de R4.2 continua pendente e não foi dada como concluída nesta entrega.

Release concluído: zero erros, 1319 avisos. Regressão completa não repetida nesta entrega de cálculo puro; última regressão anterior: 2352 testes.
