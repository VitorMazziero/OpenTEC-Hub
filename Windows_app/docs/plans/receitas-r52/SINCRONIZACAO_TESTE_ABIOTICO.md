# Sincronização dos ecos do teste abiótico

A regressão encontrou falha no cenário abiótico de conclusão normal da API. O simulador do teste aguardava qualquer primeiro comando antes de publicar o eco de fechamento. Como o primeiro comando podia ser o motor, um eco de gás prematuro podia ser incorporado à linha de base do comando posterior e deixar o protocolo aguardando confirmação.

O cenário normal agora aguarda o envio de cada comando de gás correspondente à sua fase: fechamento, abertura de nitrogênio, pré-estágio de ar, transferência ao reator e parada. Somente então publica o eco daquela etapa. Mantém valores de OD, qualidade, limites e prazos de produção. A recuperação conserva a sincronização anterior com a referência restaurada do motor.

Os 10 testes focados da API passaram. A regressão completa final é registrada junto ao incremento de confirmação de pH/pressão, executado no mesmo checkout. Essa alteração corrige a condução do teste; não afrouxa os intertravamentos do protocolo.

Regressão completa final: 2413 testes aprovados, sem falhas ([TRX](evidence/recipes-r52-module-final-full.trx)).
