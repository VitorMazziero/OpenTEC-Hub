# R5 — Amostragem e confirmação sob a barreira

O executor solicita a amostragem ao destino. RecipeRampGuardedDestination calcula a referência dentro da lease do produtor, evitando aplicar um quadro calculado antes de uma cessão já encerrada. Quadros repetidos continuam deduplicados. A confirmação final só pode começar com todos os alvos finais e fora de suspensão.

A confirmação mantém a lease até o destino realmente encerrar. Seu prazo cancela a operação; o destino deve observar esse cancelamento. Não se abandona uma tarefa de confirmação que ainda possa tocar os recursos. Cancelamento de receita e parada têm precedência sobre a classificação de timeout.

O token de parada do produtor é capturado na construção, permitindo que uma chamada tardia após descarte receba cancelamento sem acessar uma CancellationTokenSource já descartada.

O destino exige a mesma instância de relógio do produtor. Relógios diferentes permitiriam que o cálculo avançasse durante uma suspensão do relógio registrado no coordenador, causando salto na retomada; essa configuração é recusada.

26 testes focados de rampas, barreira e coordenação passaram. Os novos testes incluem o coordenador e árbitro reais com destino de confirmação controlado: reserva espera a confirmação sair; timeout drena a lease; aborto antes do ensaio libera o produtor. Não constitui qualificação física.

Ainda falta registrar e encerrar o produtor no ciclo de vida do bloco no engine, capturar referências iniciais confirmadas, persistir progresso/resultado e fornecer a confirmação específica dos destinos reais. O bloco permanece bloqueado para execução.
