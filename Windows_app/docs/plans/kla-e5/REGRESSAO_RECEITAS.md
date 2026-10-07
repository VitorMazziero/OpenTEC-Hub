# Correção complementar das receitas

A cascata conserva até 256 quadros recentes: confirmações consecutivas avaliam cada quadro, mas comandos usam somente a leitura mais recente. Uma lacuna no histórico reinicia a contagem. Prazos de monitoramento e temporização encerram a espera mesmo sem novo quadro.

Os 32 testes de RecipeEngineTests passaram na execução integrada de 298 testes registrada em validation-receipt.json e no TRX de E5. Sem equipamento físico. Esta correção tem commit separado da etapa E5.
