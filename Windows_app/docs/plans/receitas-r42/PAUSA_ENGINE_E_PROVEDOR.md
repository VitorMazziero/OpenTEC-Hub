# R4.2 — Pausa da receita ligada ao provedor e à matriz

O trabalho autônomo concreto agora leva uma barreira de pausa por bloco. Pause/Resume do engine encaminha a transição somente às matrizes independentes; alvos de Periodicidade mantêm cancelamento do slot ativo e descarte dos slots vencidos, com a agenda monotônica existente. Não retomar uma matriz periódica antiga no lugar de seu próximo slot.

A preparação observa a mesma barreira antes de reservar. Espera pelo intervalo do cultivo e reserva pendente são canceláveis pela época da pausa. O tempo monotônico de entrada e prazo de preparação permanecem contando; retomada não reinicia InvocationId. Uma reserva preparada em época antiga é devolvida antes de tentar outra. Ao criar a sessão, o provedor passa a mesma barreira ao orquestrador: a recuperação, checkpoint/seleção e espera entre pulsos continuam no caminho comum.

Callbacks de aquisição executam fora do lock de estado do engine. Pausa/retomada e descarte das barreiras têm serialização própria; encerramento e reinício aguardam a execução anterior antes de liberar suas fontes. O estado Paused do engine significa solicitação de pausa; o progresso distingue recuperação pendente da matriz já pausada com controle devolvido.

Testes do engine verificam uma única chamada/invocação ao pausar e retomar, parada enquanto pausado e liberação da barreira após encerramento aguardável. O alvo periódico não recebe pausa de matriz; slots 0/1 vencidos são pulados e a retomada executa apenas o slot 2 futuro. No provedor concreto, os quatro modos exercitam pausa antes de preparar e durante aquisição: nenhum comando/reserva na espera inicial, mesmo InvocationId, tentativa interrompida mantida e snapshots recapturados após devolução. As repetições de teste usam limite explícito de duas tentativas por réplica; uma configuração limitada a uma tentativa encerra corretamente com resultado inconclusivo após interrupção.

Validação focada: 73 testes passaram em engine/provedor/configuração/perfis/construtor; após a melhoria da visibilidade do editor e o caso periódico, 50 testes rápidos de engine/editor/construtor passaram. Contagens não se somam. Aprovação de software não habilita operação física: perfis físicos continuam fechados até R6.2.

Regressão completa: 2352 testes aprovados, zero falhas, aproximadamente 81 s (evidence/recipes-r42-engine-pause-final-full.trx). Compilação Release concluída sem erros.
