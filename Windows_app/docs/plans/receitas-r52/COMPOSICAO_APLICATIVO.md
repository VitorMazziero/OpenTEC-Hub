# Rampas conectadas à composição do aplicativo

A inicialização registra `RecipeRampExecutionConfiguration` e fornece essa configuração ao engine usado pelas receitas. A fábrica cria os destinos a partir da captura durável, incluindo a banda anterior de pH, e consome os critérios de confirmação congelados no bloco.

Os registros ficam em `Minhas Receitas/AutomacaoRampas/simulacao` ou `Minhas Receitas/AutomacaoRampas/fisico`, conforme o ambiente já selecionado pelo aplicativo. Preparação tem prazo de 30 s e a cadência mínima é 100 ms; confirmação e recuperação usam o prazo do bloco.

Uma configuração antiga sem critérios recebe os mesmos valores operacionais iniciais do editor na nova execução. O engine registra essa origem no diário e persiste a configuração resultante na captura inicial, antes da atuação. A definição original e os registros antigos permanecem inalterados. Esses valores são sugestões operacionais, não qualificação de um processo ou instalação. O catálogo e a composição compartilham uma única definição desses valores.

O teste de receita completa percorre Início → Rampa → Evento → Fim com a fábrica do aplicativo nos dois diretórios de ambiente, usando transporte simulado em ambos. Verifica critérios persistidos, isolamento dos diretórios e recibo terminal anterior ao evento seguinte. O teste de compatibilidade verifica o congelamento dos critérios sem mutação da configuração original.

Ainda faltam a receita completa com cascata/rampa/ensaio concorrentes, exemplos, verificação visual restante e qualificação física. O registro da composição remove o bloqueio por ausência de configuração; as validações de receita, rotas, posse, captura e confirmação continuam aplicadas.
