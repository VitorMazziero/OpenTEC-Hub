# R2.1 — aquisição seguida de recuperação independente

`KlaRecipePulseLifecycle` conecta a aquisição ao serviço R1.3, usando a mesma instância de reserva. Antes da execução, valida contrato, critérios de recuperação e snapshot ativo. Aquisição cancelada ou com falha segue para recuperação obrigatória. O token da aquisição nunca é repassado à recuperação, que conserva seu próprio prazo.

O ciclo registra a recuperação no runner e mantém os recursos com `KlaAssay` até a persistência final e devolução verificadas. Falha de recuperação encerra a cessão sem retomar produtores. Se a gravação do retorno falha depois de confirmar o estado físico, o resultado mantém essa confirmação e informa o erro de registro; não libera recursos.

86 testes direcionados aprovados em `evidence/recipes-r21-lifecycle-final.trx`. Novos casos exercitam cancelamento da aquisição seguido de recuperação real simulada e falha do writer depois do retorno confirmado. Os testes existentes de aquisição e recuperação dos dois protocolos continuam aprovados.

R2.1 permanece parcial: falta o adaptador `IKlaAssayExecution` completo, ligando preparação persistida, esse ciclo, análise determinística comum, resultado durável e devolução, além de registro por capacidades. Nenhuma habilitação física ou do editor foi feita.
