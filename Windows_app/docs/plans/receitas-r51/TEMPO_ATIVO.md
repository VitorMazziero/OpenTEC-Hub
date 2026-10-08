# R5 — Relógio de tempo ativo da rampa

RecipeRampActiveClock acumula segmentos monotônicos de tempo ativo. Motivos independentes de suspensão podem se sobrepor: retomar a pausa da receita não libera a cessão ao ensaio ainda vigente. Solicitações repetidas do mesmo motivo são idempotentes; a retomada inicia um novo segmento sem incluir a duração suspensa.

Quatro testes focados aprovados em relógio/trajetória. Os novos casos cobrem pausa da receita sobreposta à cessão ao kLa, pausas repetidas, retomada parcial, motivo desconhecido e manutenção de referência de O₂ após quatro horas suspensas. A trajetória termina após os segundos ativos restantes.

É infraestrutura para R5.2, ainda sem emissão de comandos. O futuro produtor de recursos deve usar motivo identificado pela reserva e congelar relógio/despacho na mesma barreira. Não confundir este tempo ativo com a agenda periódica: a agenda mantém tempo monotônico total e descarta slots vencidos. Destinos, confirmação, cadência, persistência e integração engine/editor continuam pendentes.

Release: zero erros, 1331 avisos. Suíte completa não repetida nesta entrega; última regressão completa anterior: 2352 testes.
