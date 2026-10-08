# R5.2 — Registro terminal durável da rampa

O resultado terminal possui identidade de execução, invocação, nó e snapshot inicial; estado final; tempo ativo; motivo de interrupção; resultado do retorno; e confirmações individuais dos destinos. A persistência exige a captura inicial já durável e usa a mesma trava de arquivo. Gravação repetida do mesmo resultado é idempotente; resultado diferente para a mesma invocação é recusado.

Conclusão exige duração ativa suficiente, todos os parâmetros confirmados, alvos finais coerentes com a quantização dos comandos comuns e evidência apropriada ao destino. Aceitação pelo transporte sozinha não conclui a rampa. Confirmação da cascata não pode ser usada para destinos diretos. Datas de fim e confirmação são informativas; a duração ativa é independente de correções do relógio de parede.

Cancelamento e falha exigem motivo e um resultado de retorno compatível com a política salva. Falha de retorno pode ser registrada, sem promover conclusão. Emergência exige restauração suprimida, preservando a prioridade da parada de segurança. Não há atuação nem restauração dentro do armazenamento.

Sete testes focados aprovados, cobrindo os registros inicial e terminal: término sem início, duração insuficiente, ausência de confirmação, snapshot errado, alvo errado, transporte insuficiente, evidência de destino incompatível, idempotência, conflito terminal, adulteração em disco, cancelamento e emergência. Release compilado em `D:/Temp/OpenTECHub-ramp-terminal-release/`, com 0 erros e 1473 avisos. A regressão completa não foi repetida neste incremento.

O recibo ainda precisa ser consumido pelo ciclo de vida do executor. Os registros não habilitam reinício automático nem provam confirmação física em bancada. Execução de rampas continua bloqueada até integrar os destinos, pausa, retorno e consumo dos recibos no engine.
