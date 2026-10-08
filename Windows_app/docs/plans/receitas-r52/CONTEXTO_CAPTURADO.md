# Destinos a partir da captura inicial

O executor pode criar os destinos a partir do registro inicial durável, depois da preparação coordenada. Assim os critérios congelados e a configuração anterior de pH acompanham a mesma invocação da rampa.

Quando há critérios de conclusão e uma linha de pH, a captura inclui a referência e a banda de controle anteriormente aceitas pelo transporte, mesmo com início explícito e política de manter as últimas referências. Esse contexto não substitui o valor inicial explícito da trajetória. A banda precisa ser finita, não negativa, representável com duas casas e coincidir com o comando aceito.

A leitura do registro aplica a mesma regra de captura. Registros anteriores sem critérios permanecem compatíveis. A fábrica rejeita contexto ausente ou critérios diferentes dos congelados.

Verificação: 12 testes focados e 2458 testes na regressão completa, sem falhas. Evidências: `evidence/recipes-r52-captured-context.trx` e `evidence/recipes-r52-captured-context-full.trx`.

A composição do aplicativo ainda não fornece a configuração de execução das rampas. A integração dos prazos por bloco e os cenários restantes de encerramento continuam pendentes; esta alteração não encerra R5.2 nem constitui qualificação física.
