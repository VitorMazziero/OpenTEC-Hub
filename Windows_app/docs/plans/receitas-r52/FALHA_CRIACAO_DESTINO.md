# Falha ao criar o destino após preparação

Depois que a captura inicial foi gravada e a preparação terminou, uma falha na fábrica do destino gera registro terminal `Faulted`/`Failed`, sem evidência de restauração. A exceção original continua interrompendo o fluxo da receita. Não há comando de trajetória nem tentativa de usar um destino que não foi criado.

A gravação usa cancelamento independente. Se ela também falhar, a exceção agregada preserva tanto a causa da criação quanto a causa da persistência. A interrupção não autoriza avanço da receita.

O teste integrado mantém a receita em execução, captura a referência anterior de temperatura (25 °C), simula a fábrica indisponível e verifica: registro inicial existente, registro terminal de falha, nenhuma referência de rampa enviada e referência anterior preservada.

O encerramento conjunto da cascata e da rampa permanece pendente: o controlador associado precisa continuar disponível até terminar o retorno, antes de ser removido do engine. A composição do aplicativo e a qualificação física também permanecem pendentes.
