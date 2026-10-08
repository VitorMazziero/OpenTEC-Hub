# R5.2 — Coordenação do retorno da rampa

`RecoverRampAsync` serializa a recuperação com os ensaios e a preparação de rampas. Confere a captura inicial contra o registro durável, exige o produtor registrado e recusa sobreposição com produtores além da cascata associada. Suspende a rampa e a cascata antes de reservar e drenar os destinos.

O adaptador de retorno deve restaurar e confirmar as referências anteriores sob essa reserva. A coordenação exige resultado Cancelled/Faulted da mesma execução, invocação e snapshot, com evidência de recuperação versão 2. O armazenamento valida todas as referências e grava o recibo terminal antes da drenagem final e liberação. A autoridade e a possibilidade de retomada são verificadas novamente após a drenagem. A rampa encerrada permanece parada; apenas a cascata é retomada.

Retorno incompleto, falha de gravação ou perda de autoridade não reabre os produtores nem libera uma reserva de recuperação não resolvida. A recuperação usa um prazo independente do cancelamento da execução original.

Testes próprios cobrem reserva fechada enquanto a confirmação está pendente, recibo válido antes da liberação, evidência incompleta sem liberação e captura alterada rejeitada antes de suspensão. Um teste com engine e cascata reais confirma o retorno conjunto de O₂ e temperatura sob a pausa e invalidação do comprovante após a reserva terminar. Evidência focada: `evidence/recipes-r52-recovery-coordinator-focused.trx`.

Permanecem pendentes o ciclo do bloco no engine, tratamento de parada/emergência e produtores já encerrados, testes de falha de gravação e autoridade durante a drenagem, e a qualificação física. A execução de rampas continua bloqueada até concluir essa integração.

Regressão completa final: 2444 testes aprovados, sem falhas (`evidence/recipes-r52-recovery-coordinator-full-final.trx`). A primeira execução encontrou timeout de recuperação biótica por entrega de amostras virtuais em rajada; o simulador passou a espaçar as observações sem alterar a exigência de amostras consecutivas ou estabilidade do aplicativo.
