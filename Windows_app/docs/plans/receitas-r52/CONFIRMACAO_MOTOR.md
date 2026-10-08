# R5.2 — Confirmação medida da rampa de agitação

`RecipeRampMotorDestination` liga a aplicação do setpoint ao engine e a confirmação às amostras do mesmo serviço de telemetria e relógio. O componente usa a execução identificada e a rota ativa do engine; deve operar dentro do destino protegido do produtor da rampa.

Somente amostras posteriores à aplicação aceita podem confirmar. São exigidos dados novos do servo, comunicação habilitada, motor online, ausência de alarme e de comando pendente, rota declarada e confirmada correspondente à aplicação e rotação finita dentro da tolerância. Amostras parciais não reciclam a rotação anterior.

A estabilidade usa tempo monotônico entre amostras recebidas. Intervalo excessivo, perda de amostras ou leitura inválida reiniciam a janela. Uma nova aplicação, mesmo com o mesmo alvo, invalida a confirmação anterior e acorda uma confirmação pendente. Perda de execução, rota ou posse do motor impede conclusão. A pausa da receita acorda a espera e devolve confirmação pendente; retomada pode confirmar novamente. O destino protegido também recusa conclusão se a suspensão for solicitada enquanto confirma. O prazo é limitado e cancelável; descarte remove as assinaturas de telemetria e estado e acorda esperas.

O recibo inclui alvo, rotação medida e tolerância. A persistência terminal exige leitura e tolerância finitas e coerentes para evidência `ProcessFeedback`; não basta atribuir esse nome a uma confirmação sem dados.

Validação focada: 47 testes aprovados, incluindo engine, destino protegido e persistência terminal. Os testes do motor cobrem dado anterior, amostra parcial, comando pendente, estabilidade, lacuna de amostras, reaplicação, perda de posse, ausência de dados, cancelamento, prazo esgotado e pausa/retomada. Após os últimos ajustes de concorrência e o teste adicional de suspensão durante confirmação, a regressão completa final aprovou 2383 testes, sem falhas ([saída final](evidence/recipes-r52-motor-final-full.txt)). Release compilado em `D:/Temp/OpenTECHub-ramp-motor-release/`, com 0 erros e 1533 avisos.

A política de tolerância, estabilidade, intervalo e prazo é explícita, sem qualificação física automática. Ainda faltam compor os destinos diretos restantes, consumir os recibos no ciclo de vida da rampa e validar o retorno no cancelamento. A execução de rampas continua bloqueada até essa integração. Evidência simulada não habilita hardware.
