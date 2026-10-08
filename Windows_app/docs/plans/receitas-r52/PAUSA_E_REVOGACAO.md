# R5.2 — Pausa e revogação no executor

O executor acompanha a pausa da receita e mantém um motivo independente de suspensão no relógio ativo. Um ensaio que reserva recursos sobrepostos suspende o produtor da rampa pelo mesmo mecanismo de coordenação. O executor aguarda a devolução antes de continuar; o intervalo suspenso não adianta a trajetória.

Uma transferência para Manual dos destinos da rampa cancela a execução local. Revogação marcada como aborto seguro grava EmergencyStopped/SuppressedForEmergency; transferência comum grava Faulted/Failed. Nenhuma dessas ocorrências inicia restauração. O encerramento remove os assinantes antes de descartar o token, com sincronização para impedir acesso ao CancellationTokenSource já descartado.

Testes do executor cobrem pausa da receita, cessão/devolução da reserva de ensaio e parada de segurança durante a trajetória. Na parada, a temperatura permanece zero e há recibo terminal de emergência, sem retorno à referência capturada. O teste de cessão não executa aquisição kLa completa.

Permanecem pendentes pausas repetidas/sobrepostas, ensaio completo concorrente, revogação durante gravação e recuperação no ciclo completo, produtor encerrado/falha de drenagem e configuração das políticas no aplicativo. A produção continua sem configuração de rampas na composição atual.

Regressão completa: 2454 testes aprovados, sem falhas (`evidence/recipes-r52-pause-revocation-full.trx`).
