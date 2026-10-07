# Concorrência no encerramento da aquisição

A regressão completa expôs uma disputa entre o watchdog do runner e a selagem da aquisição para recuperação independente. A selagem bloqueava o árbitro antes de adquirir a trava do runner; um watchdog em andamento ainda tentava despachar por esse árbitro fechado.

Selagem, watchdog e aborto agora compartilham a trava do runner. Telemetria pendente verifica novamente a selagem dentro da trava; aborto tardio não altera a recuperação nem envia comandos. Os testes exercitam telemetria, watchdog e aborto depois da selagem nos estágios dos dois protocolos.

O simulador do teste biótico limpava os comandos depois de amostras que já podiam iniciar a recuperação. A limpeza foi antecipada para preservar os comandos observados; os limites de produção foram mantidos.

Validação focada: 64 testes aprovados, zero falhas em `evidence/recipes-r42-recovery-and-control-reconciled.trx`. Inclui os testes de restauração e ensaio e a reconciliação de procedência do RPM comandado. Evidência de software, sem qualificação física.
