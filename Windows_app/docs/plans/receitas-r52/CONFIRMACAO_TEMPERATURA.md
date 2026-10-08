# R5.2 — Confirmação da rampa de temperatura

O componente `RecipeRampTemperatureDestination` usa o mesmo mecanismo de mensagens novas, estabilidade monotônica, pausa e cancelamento do motor. A aplicação permanece no engine e nas rotas existentes. Não altera o comportamento dos antigos blocos de setpoint.

Temperatura positiva exige eco da referência, rota correspondente à aplicação, comando de temperatura conhecido, sensor conectado, leitura aceita no quadro atual, validade e idade declaradas pelo Hub e temperatura do **reator** dentro da tolerância pelo período configurado. Para o banho, também exige presença, comunicação habilitada, comandos concluídos, posse e cascata ativas e ausência de falha ou parada pendente. Temperatura, alvo ou setpoint do banho não substituem a temperatura do reator.

`TemperatureUpdated` identifica mensagens que trouxeram uma leitura aceita; uma mensagem parcial ou valor rejeitado conserva o valor anterior sem promovê-lo a leitura nova. A faixa numérica de aceitação do parser permanece exatamente a anterior: maior que 10 e menor que 100 °C. O componente recusa alvos positivos fora dessa faixa observável antes de atuar. Hubs sem os metadados necessários não obtêm confirmação automática; sua compatibilidade precisa ser resolvida na qualificação de instalação.

Zero significa desligamento. A rota nativa exige eco zero exato e saída do módulo desligada. A rota de banho exige cessão de posse/cascata e confirmação de conclusão do comando de parada, sem operação pendente. Um eco positivo pequeno não é tratado como zero pela tolerância. A evidência é leitura da referência/estado; não afirma que o reator chegou a zero °C. Mudança de rota invalida a confirmação em vez de migrar silenciosamente o alvo.

Validação focada: 103 testes aprovados, incluindo as duas rotas, mensagens parciais, leitura vencida/inválida, eco divergente, comando pendente, estabilidade do reator, desligamento nas duas rotas, parada do banho ainda não concluída, mudança de rota e preservação das regras do motor. A regressão completa final aprovou 2390 testes, sem falhas, incluindo recusa de eco positivo pequeno no desligamento ([TRX](evidence/recipes-r52-temperature-off-final-full.trx)). Release final compilado em `D:/Temp/OpenTECHub-ramp-temperature-release/`, com 0 erros e 1504 avisos nesta compilação incremental.

Ainda falta compor os destinos restantes e integrar recibos, confirmação, retorno e cancelamento ao ciclo de vida do bloco. A execução de rampas permanece bloqueada. Nenhuma validação física de instalação é promovida por estes testes de software.
