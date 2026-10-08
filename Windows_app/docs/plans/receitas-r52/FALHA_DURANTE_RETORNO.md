# Falha e emergência durante o retorno da rampa

O executor reavalia a revogação de autoridade quando a operação de retorno falha. Uma emergência durante essa operação grava `EmergencyStopped` e `SuppressedForEmergency`, sem evidência de restauração. A coordenação mantém os produtores interrompidos e não reabre a reserva perdida.

Na ausência de emergência, a falha de retorno grava `Faulted` e `Failed` e continua lançando a falha ao fluxo da receita. Um comando de restauração aceito pelo transporte não constitui retorno confirmado: sem feedback suficiente, o registro não contém evidência de recuperação.

Se já existe um registro terminal, ele não é substituído. Se a gravação do novo registro também falha, a exceção preserva as causas da trajetória, do retorno e da persistência; o fluxo não avança.

Os testes integrados cancelam uma rampa após o primeiro comando, aguardam o envio efetivo da referência anterior e então: (1) omitem o feedback até vencer o prazo de retorno; (2) revogam a autoridade com parada de emergência. No segundo caso a referência de segurança permanece como último comando e não há recuperação confirmada.

Permanecem pendentes a composição do aplicativo, a falha de criação do destino após preparação, o encerramento conjunto com a cascata e demais cenários concorrentes. Esta entrega não encerra R5.2 nem constitui qualificação física.
