# R5.2 — Reserva dos comandos por quadro

O destino protegido pode receber o árbitro, a execução e um prazo de reserva. Dentro da barreira do produtor, reserva todos os atuadores diretos do quadro, drena os comandos anteriores, amostra a trajetória e usa a autoridade reservada para enviar o quadro inteiro. O tempo ativo congela durante a espera e a drenagem inicial.

Após o envio, a drenagem usa um prazo independente do cancelamento da aquisição. Somente depois ela libera a reserva e a barreira. Assim, o coordenador de kLa não captura o estado enquanto um comando da rampa ainda está em trânsito. Uma drenagem que falha não libera silenciosamente a reserva: a execução falha e precisa de recuperação ou parada segura.

A referência de O₂ é estado do controlador local. Ela permanece protegida pela barreira compartilhada, sem reservar N/Q e bloquear os próprios comandos da cascata. Os demais parâmetros do mesmo quadro usam a reserva dos respectivos atuadores diretos.

O engine valida a execução, o proprietário e a geração da autoridade antes de enviar o quadro reservado. O produtor precisa proteger todos os recursos afetados. Reservas de outra execução são recusadas sem envio.

Verificação: nove testes do quadro completo e 2423 testes na regressão completa passaram. Cobertura nova: espera de reserva sem avanço do tempo ativo, envio através da reserva, liberação antes da cessão, rejeição de execução estrangeira e retenção da reserva quando a drenagem falha antes ou depois do envio. Evidência: `evidence/recipes-r52-step-reservation-final-full.trx`.

O aplicativo compilou em Release com zero erros, em `D:/Temp/OpenTECHub-step-reservation-release/`.

A execução do bloco no engine permanece desabilitada: ainda faltam registro do produtor durante todo o bloco, preparação/captura inicial coordenada e recuperação ao cancelar. Os construtores anteriores continuam disponíveis para os componentes e testes existentes; o ciclo de execução deverá usar a variante com reserva.
