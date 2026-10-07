# R3.1 — devolução vinculada ao armazenamento

`RecipeAssayResourceLease.ReturnPersistedAsync` lê a preparação, o checkpoint terminal e seu recibo no armazenamento comum. Confere request, IDs de sessão/corrida, reserva, proprietário, geração, recursos e snapshot antes de devolver a autoridade. A recuperação física confirmada continua sendo requisito independente. Reservas com snapshot capturado recusam identificadores de recibo preenchidos pelo chamador sem essa verificação.

Os testes de recuperação dos dois protocolos e da cascata real passaram a criar checkpoints e recibos pelo `KlaTestStore`, em vez de usar identificadores fictícios. Isso verifica recuperação e persistência antes da retomada do PID. Os testes antigos que exercitam apenas o contrato de cessão sem captura mantêm seu escopo isolado.

Validação direcionada: 57 testes aprovados em `evidence/recipes-r31-return.trx`. A primeira regressão completa encontrou um teste da cascata que ainda fornecia recibo fictício; esse teste foi atualizado para passar pelo armazenamento real. Evidências da primeira execução foram preservadas.

Limites: ainda falta integrar a reconciliação ao ciclo de vida de produção, endurecer os checkpoints e exercitar falhas nas fronteiras completas de escrita. R3.1 permanece em andamento. O adaptador do runner é R2.1; bancada permanece R6.2.

Regressão completa final: 2068 testes aprovados, zero falhas, 35 s; `evidence/recipes-r31-return-full-final.trx`.
