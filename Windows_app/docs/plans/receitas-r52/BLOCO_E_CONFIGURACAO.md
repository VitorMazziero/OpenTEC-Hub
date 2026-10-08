# R5.2 — Bloco e configuração da rampa

O catálogo declara Rampa linear de referências, com linhas de parâmetro, origem atual confirmada/explicita, início contextual, valor final e tempo final em segundos. O₂ explicita monitor ou referência da cascata; a cascata associada deve existir no grafo. Política de cancelamento explicita manter referências ou restaurar início.

O parser estrito constrói LinearSetpointRampDefinition, recusando linhas vazias, opções desconhecidas, tempos não positivos, parâmetros duplicados e trajetórias explícitas inválidas. Entradas inativas de início e destino O₂ não entram na definição. O tipo foi acrescentado ao enum sem renumerar tipos antigos.

43 testes de configuração/domínio aprovados, incluindo reabertura JSON, tempos distintos, campos inativos e catálogo completo. O primeiro teste do catálogo falhou por sua contagem anterior de 23 tipos; atualizado para 24 com a inclusão da rampa. Renderização/interação da nova lista ainda pendentes.

CanStart e o dispatcher recusam execução da rampa até integrar reservas, captura e confirmações. O bloco é um rascunho editável; não conclui silenciosamente. R5.2 permanece em implementação, sem habilitação física.

Release compilado sem erros. Suíte completa não repetida neste escopo.
