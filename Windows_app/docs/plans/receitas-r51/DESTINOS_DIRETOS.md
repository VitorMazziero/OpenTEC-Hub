# R5.1 — Construção de comandos dos destinos diretos

Correção posterior: a modalidade de monitor de O₂ descrita neste incremento foi removida, pois a chave no firmware é um interruptor. Consulte [correção do destino de O₂](../receitas-r52/CORRECAO_DESTINO_O2.md). O destino numérico disponível é a referência da cascata.

RecipeRampDirectCommands fornece quantização e construção de comandos sem despacho. Reusa MotorSetpoint e FlowRoute; mantém as chaves comuns de temperatura/banho, pH com banda inativa, pressão e monitor de O₂. A referência de cascata é recusada neste caminho e usa o destino do controlador implementado separadamente.

O construtor valida faixas, finitude, teto de vazão e limite de fallback UART antes de fornecer valor/comando. Recusa alvo que seria silenciosamente limitado pelo builder. Motor usa rpm inteiro arredondado; outros destinos conservam doubles em unidades de engenharia, conforme os builders existentes. A validação da trajetória usa esse quantizador e recusa também início/alvo incompatíveis.

Quatro testes focados aprovados em destinos/trajetória. Casos novos verificam equivalência de comandos motor/vazão, rota fechada em zero, destinos restantes, separação de O₂ de cascata, alvo além do teto de vazão/fallback e rejeição antes de construir a trajetória.

Este serviço ainda não despacha nem confirma aplicação. O executor deve revalidar rota/configuração antes de cada envio e usar confirmações existentes de banho/motor/fluxo, reserva e cadência. R5.1/R5.2 permanecem em implementação. Regressão completa não repetida neste escopo.
