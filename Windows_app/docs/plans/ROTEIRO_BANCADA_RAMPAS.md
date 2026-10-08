# Roteiro de bancada — Rampa linear de referências (R6.2)

Data: 08/10/2026. Decisão: [D-058](../DECISIONS.md). Build: instalador `OpenTECHub_Setup_v0.27.0`.

Objetivo: comprovar, no reator real, cada destino da rampa e os caminhos de interrupção. A aprovação em software está na [auditoria final](AUDITORIA_FINAL_RECEITAS.md); este roteiro produz a evidência física que falta. As tolerâncias usadas são as do bloco (padrão D-063: ±0,5 °C, ±5 rpm, ±0,2 L/min, ±0,2 pH, ±1 kPa, 10 s de estabilidade, 900 s de prazo e 1 s entre comandos).

## Preparação

1. Instale a build e abra **sem** `--kla-test-file` (ambiente físico). Conecte o Hub e confirme telemetria de todos os nós usados.
2. Desative o Automático (cascata) antes de iniciar receitas; a receita assume todos os atuadores.
3. Para cada caso, crie uma receita `Início → Rampa linear → Fim` (ou o grafo indicado), salve e inicie.
4. Registros de cada execução: `<workspace>/Receitas/AutomacaoRampas/fisico/<execução>/<invocação>/start.json` e `terminal.json`, além do log da receita (página Receitas) e do diário de eventos.
5. Anote em cada linha: data/hora, valores medidos no início, no meio e no fim, `terminal.json` (status e retorno) e observações.

## Destinos

| # | Caso | Configuração | Resultado esperado | OK? |
|---|---|---|---|---|
| B1 | Temperatura nativa | 30 → 32 °C em 20 min, início "atual confirmado" | Referência sobe linearmente, no máximo um comando por segundo; termina após a temperatura do reator ficar dentro de ±0,5 °C por 10 s; `Completed` | |
| B2 | Temperatura via banho | Mesma rampa com o banho C404 na via externa | Mesmo comportamento; conclusão pela temperatura do **reator** (`Tempval`), não pela do banho | |
| B3 | Agitação | 200 → 400 rpm em 10 min | Comandos apenas quando muda o valor representável; final confirmado pelo servo | |
| B4 | Vazão | 1 → 3 L/min em 10 min | Final confirmado pelo fluxômetro (ACK e leitura estável) | |
| B5 | Descida e alvo igual | 3 → 1 L/min; e uma linha com início = fim | Ambos concluem; a linha constante mantém a referência sem saltos | |
| B6 | Tempos distintos | Exemplo `rampas-tempos-distintos.recipe.json` | Cada linha termina no próprio tempo e mantém o alvo; o bloco termina após a última confirmação | |
| B7 | pH | Referência 6,8 → 7,0 em 15 min com a banda configurada | Banda e bombas preservadas; dosagem não é ativada implicitamente | |
| B8 | Pressão | Pequena variação dentro do limite do equipamento | Confirmação pela leitura de pressão | |
| B9 | O₂ da cascata | Exemplo `cascata-kla-periodico-rampa.recipe.json` sem o ramo de kLa, ou grafo Controle de O₂ ∥ Rampa O₂ | Rampa altera apenas a referência do controlador associado; a cascata segue regulando | |

## Interrupções

| # | Caso | Ação | Resultado esperado | OK? |
|---|---|---|---|---|
| I1 | Pausa | Pausar no meio de B1 por 5 min e retomar | Referência congela; ao retomar, continua do mesmo ponto, sem salto | |
| I2 | Pausas repetidas | Três pausas curtas em B3 | Tempo ativo total igual ao configurado | |
| I3 | Cancelar, manter referências | Parar a receita no meio de B4 com "Manter últimas referências" | `Cancelled` + `HeldLastReferences`; nenhum comando após a parada | |
| I4 | Cancelar, restaurar | Repetir I3 com "Restaurar referências iniciais" | `Cancelled` + `RestoredSnapshot` após confirmação dos valores iniciais | |
| I5 | Perda de comunicação | Desconectar o cabo USB / Wi-Fi do Hub no meio de B3 | `EmergencyStopped` + `SuppressedForEmergency`; nenhum comando ao reconectar; anote o que o Hub fez (Q2) | |
| I6 | Assumir manualmente | Tentar comando manual durante B1 | Comando manual recusado enquanto a receita tem a posse; após parada, posse volta ao Manual | |
| I7 | Parada de emergência | Emergência durante B4 | Saídas seguras; nenhum religamento; `EmergencyStopped` | |
| I8 | Alvo inalcançável | Rampa de vazão acima do que o fluxômetro consegue entregar | Falha por prazo de confirmação, sem relatar conclusão | |
| I9 | Fim da cascata durante rampa de O₂ | Encerrar o Controle de O₂ no meio de B9 | Rampa encerrada após retorno gravado; ramo termina sem executar o bloco seguinte | |

## Critério de aprovação

Um destino é aprovado quando o caso B correspondente e as interrupções aplicáveis (I1, I3/I4, I5, I7) passam. Registre o resultado em um recibo `RECIBO_BANCADA_RAMPAS.md` neste diretório. Um destino reprovado deve ser bloqueado no ambiente físico por decisão posterior a D-058.
