# R5.2 — Autoridade e confirmação durante a recuperação

O controlador permite preparar a restauração sem alterar seu estado. O engine valida o snapshot completo antes de enviar o quadro direto e aplica o estado do PID/alocação somente após a aceitação desse quadro. Para O₂, exige a cascata associada pausada sem passo em andamento, a referência anterior correspondente e uma reserva vigente de agitação, aeração e oxigênio. Referências diretas N/Q continuam incompatíveis com uma cascata ativa.

O destino do quadro adota referências restauradas para os mesmos canais de confirmação. A confirmação da cascata exige igualdade do estado completo, referência anterior, pausa efetiva e reserva atual. Os canais diretos podem confirmar durante uma pausa da receita somente com a reserva de recuperação vigente; aplicação normal limpa esse contexto. Liberação, substituição ou perda da reserva invalida a confirmação.

22 testes focados e 2441 testes na regressão completa passaram. Os cenários acrescentados verificam preparação sem mutação, snapshot inválido, retorno direto durante pausa e rejeição após liberação da reserva. Evidências: `evidence/recipes-r52-recovery-authority-focused.trx` e `evidence/recipes-r52-recovery-authority-full.trx`.

O retorno misto O₂ e destinos diretos está conectado aos componentes, mas seu cenário integrado ainda precisa ser testado. A operação coordenada de recuperação, gravação terminal antes de retomada e ciclo completo do bloco permanecem pendentes. O bloqueio de execução de rampas no engine permanece vigente. Estes testes não constituem qualificação física.
