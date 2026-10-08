# R4.2 — Pausa dentro da mesma matriz

ExecuteAsync recebe uma barreira de pausa por invocação. Uma época é capturada antes das esperas/reserva e novamente validada na operação atômica Create/Start. Uma reserva preparada em época antiga não dispara mesmo após retomada rápida: ela é devolvida sem criar outro request com o mesmo ID. Esperas e preparação são canceláveis pela época; o deadline geral permanece independente.

Durante aquisição, a pausa cancela o token do pulso. O orquestrador aguarda o terminal comum, a recuperação e a devolução persistida antes de gravar a seleção com o recibo da época pausada. Só depois entra no estado Paused, com produtores devolvidos. Retomar mantém a matriz, sessão, histórico, tentativa consumida e prazo original; o próximo pulso recaptura o cultivo. Prazo esgotado durante a pausa termina a matriz sem novo pulso. Cancelamento definitivo/deadline não recebem autorização de repetição por pausa.

Observação ao vivo distingue matriz pausada de recuperação em curso. Esta ligação ainda precisa ser exposta pelo provedor de trabalho e pelo comando Pause/Resume do engine; os comandos públicos da interface ainda não usam esta barreira.

Seis cenários integrados novos aprovados nos dois protocolos: pausa aguardada, retomada rápida e prazo esgotado enquanto pausado. Matriz múltipla preserva invocação, decisões e snapshots recapturados; a primeira tentativa cancelada recebe Retry e recipe_pause com checkpoint reaberto. Um teste da barreira impede despacho de reserva de época anterior e verifica identidade estável do recibo.

Na primeira regressão focada, o biótico múltiplo parou corretamente no orçamento cumulativo de exposição de 300 s. A configuração de teste de retomada bem-sucedida agora fornece explicitamente 1800 s; a política do produto não foi flexibilizada. O orçamento consumido não é reiniciado na retomada.

A primeira regressão completa teve 2339 aprovações e uma falha no término normal biótico do executor. Os dez testes do executor passaram isoladamente em seguida; a repetição completa com saída detalhada é registrada separadamente. A causa dessa falha intermediária não foi determinada, e não foi alterado timeout do produto para mascará-la.

Repetição completa aprovada: 2340 testes, zero falhas, aproximadamente 76 s. Release concluído sem erros.
