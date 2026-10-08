# Cascata, rampa e kLa no mesmo grafo

A matriz integrada acrescenta um ramo de rampa da referência de O₂ ao grafo com cascata e agenda periódica. Usa a composição de rampas do aplicativo e o provedor concreto de ensaios, com aquisição E6, análise e persistência comuns, em transporte simulado.

Para cada protocolo abiótico/biótico, cobre conclusão do ensaio, saída da cascata durante aquisição, pausa/retomada e emergência. A rampa é preparada antes do primeiro slot. O tempo ativo terminal permanece abaixo de 15 s apesar do avanço do relógio durante aquisição e retorno, e a restauração da rampa confirma a referência anterior de 80%. Emergência registra supressão de retorno e não permite novos comandos.

A integração identificou e corrigiu dois problemas:

- Uma rampa encerrada normalmente pela saída da cascata era tratada como falha do grafo. Agora, somente após recibo durável e retorno verificado (ou política de manter referências), esse ramo termina sem executar os blocos seguintes. O bloco recebe estado `Cancelled`, distinto de conclusão do alvo. Cancelamento geral, falha de retorno e emergência mantêm seus caminhos de interrupção.
- O cancelamento de outros produtores podia chegar antes da notificação de revogação ao executor, produzindo uma falsa falha de retorno. O executor observa a mudança de propriedade anterior à revogação e preserva a precedência da emergência. O teste de retorno também envia uma notificação manual posterior, sem substituir a causa de segurança.

Os testes verificam registros kLa, registro terminal da rampa, ausência de travessia da ligação posterior à rampa e fechamento do grupo antes do término da receita. A representação visual do estado cancelado usa contorno neutro; a verificação visual interativa ainda pertence a R6.1.

Esta matriz cobre uma rampa de referência de O₂. Exemplos de receitas, validação visual restante e qualificação física continuam pendentes; não é evidência de bancada.
