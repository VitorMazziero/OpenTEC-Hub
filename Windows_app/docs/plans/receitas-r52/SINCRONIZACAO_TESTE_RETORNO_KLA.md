# Sincronização do teste de retorno da API de kLa

Uma regressão de 2407 casos encontrou timeout no cenário biótico cancelado de `ApiPulseUsesCommonRecoveryAnalysisAndPersistence`, ao aguardar o término do retorno. O teste usava a contagem global de comandos enviados para decidir quando publicar leituras estáveis. Comandos de aquisição/abortamento podiam participar dessa contagem e permitir que as leituras fossem publicadas antes da linha de base final da recuperação.

O teste agora aguarda um evento específico: envio da referência original do motor durante `RestoringCultivation`. A implementação da recuperação fixa sua linha de base de telemetria antes desse envio. Assim, os quadros de estabilidade são posteriores à aplicação restaurada, independentemente de outros comandos presentes na lista. Prazos de produção, critérios de OD e restauração não foram relaxados.

Validação: 15 testes focados de API/quadro aprovados e regressão completa final de 2407 testes aprovada, sem falhas ([TRX](evidence/recipes-r52-frame-recovery-final-full.trx)). O resultado inclui o novo destino de quadro da rampa; essa composição é registrada em escopo separado.
