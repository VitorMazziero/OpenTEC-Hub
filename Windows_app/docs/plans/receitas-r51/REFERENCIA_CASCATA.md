# R5.1 — Destino da referência de O₂ da cascata

O engine expõe leitura da referência do controlador ativo e atualização explícita por ID de cascata. TrySetCascadeOxygenReference valida finitude/faixa de O₂, exige receita Running e adquire a barreira de passo da cascata antes de atualizar o controlador. Não cria comando OxygenMonitor e não modifica o rascunho da receita.

Durante cessão ao kLa ou pausa da receita, a atualização é recusada. O futuro executor deve congelar tempo/despacho e aguardar disponibilidade conforme reserva; recusa não equivale a confirmação de envio. Leitura ausente identifica controlador ainda não iniciado ou encerrado.

36 testes do engine aprovados. O caso novo verifica controlador inativo, valores inválidos, alteração/leitura, ausência de comandos adicionais de monitor, recusa durante reserva do ensaio e pausa, retomada e término. A cascata já usa comandos de monitor no seu fluxo existente; o teste distingue esses comandos da operação nova.

R5.1 permanece parcial: destinos diretos, limites por rota, quantização/cadência e reservas da rampa pendentes. R5.2 ainda integrará executor, editor, registro e confirmação. Release compilado sem erros. A suíte completa não foi repetida nesta alteração.
