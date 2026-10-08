# R5 — Despacho das referências pelo engine

TryApplyRampFrame valida e monta o quadro inteiro usando configuração e rota atuais. Envia os parâmetros diretos pelo árbitro comum e aplica O₂ ao controlador ativo sob sua barreira, sem emitir OxygenMonitor para esse destino. Recusa durante pausa, cessão da cascata, ausência do controlador ou tomada manual de N/Q/O₂. Se o comando direto for recusado, não altera a referência local.

Rampas diretas de N/Q e monitor de O₂ são recusadas quando há cascata ativa manipulando esses recursos. O destino protegido deve envolver esta chamada com a lease do produtor da rampa. A integração ao executor de bloco continua pendente.

A configuração do aplicativo permite máximo de vazão 50 L/min, enquanto o contrato de rampa aceita alvos até 25 L/min. O construtor agora conserva a configuração de rota sem bloquear parâmetros independentes. Alvos continuam limitados pelo envelope de 25 L/min e pelo máximo configurado quando menor; nenhum alvo é recortado silenciosamente.

107 testes focados passaram, incluindo quadro misto temperatura/O₂, recusa durante reserva N/Q, conflito direto com cascata, reserva independente de temperatura, pausa/retomada e parada. O teste aguarda a quiescência do primeiro quadro da cascata antes de contar comandos, evitando confundir seu envio inicial com o despacho da rampa.

Ainda faltam captura inicial, registro durável e confirmação final de cada referência; envio aceito não é confirmação física.
