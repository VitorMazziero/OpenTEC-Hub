# R5.2 — Critérios de conclusão no bloco

Novos blocos possuem tolerâncias editáveis e identificadas por unidade: temperatura em °C, agitação em rpm, vazão em L/min, pH e pressão em kPa. A interface mostra apenas tolerâncias dos parâmetros selecionados. Tempo de estabilidade, intervalo máximo sem medição e prazo de confirmação aparecem quando há destinos com feedback físico. O₂ usa a referência exata da cascata, sem tolerância de OD medido.

Os valores iniciais do editor são sugestões operacionais, não evidência de qualificação: 0,5 °C, 2 rpm, 0,1 L/min, 0,05 pH, 0,5 kPa, estabilidade de 10 s, intervalo de 5 s e confirmação em 300 s. Devem ser revisados para o processo. Os critérios são congelados na configuração e persistidos na captura inicial.

`RecipeRampCompletionCriteria` valida números finitos, tolerâncias não negativas, estabilidade menor que o prazo e limites temporais representáveis. A validação ocorre na leitura do bloco, no executor e na leitura/gravação da captura. A fábrica cria políticas de confirmação dos destinos a partir desses valores. Receitas antigas sem critérios continuam representadas sem inventar critérios no registro.

Testes cobrem persistência dos valores definidos, limites inválidos, unidade interna de pressão e relevância dos campos no editor. A composição do aplicativo ainda precisa consumir a fábrica; os critérios por si só não habilitam execução nem qualificação física.

Regressão completa: 2456 testes aprovados, zero falhas (`evidence/recipes-r52-completion-criteria-full.trx`).
