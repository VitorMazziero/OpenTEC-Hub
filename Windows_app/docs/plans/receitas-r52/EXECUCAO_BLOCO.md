# R5.2 — Ciclo da rampa e integração ao motor

`RecipeRampBlockRunner` reúne preparação coordenada, captura inicial durável, trajetória com reservas por quadro, confirmação dos destinos e resultado terminal durável. Mantém uma única identidade de invocação e é de uso único. A pausa da receita suspende o tempo ativo; o encerramento usa prazo próprio, independente do token cancelado.

Cancelamento ou falha segue a política gravada: restauração usa o quadro e as confirmações reais para construir a evidência versão 2; manutenção das últimas referências suspende produtores, drena comandos e grava o encerramento sem enviar outra referência. A coordenação só retoma a cascata após o recibo durável. Emergência antes da gravação invalida o retorno; erro de armazenamento mantém produtores parados e reserva não resolvida.

O motor chama esse executor para `LinearSetpointRamp` quando recebe `RecipeRampExecutionConfiguration`, com armazenamento e políticas explícitas dos destinos. O bloco seguinte só pode executar após um resultado Completed; cancelamento ou falha impede avanço. A composição atual do aplicativo ainda não fornece essa configuração e continua bloqueando receitas com rampas.

Testes verificam conclusão, cancelamento com ambas as políticas, uso único, retorno misto O₂/temperatura pela coordenação completa e gravação anterior ao avanço para o próximo bloco. Os cenários da coordenação também cobrem evidência incompleta, captura inicial alterada, emergência durante retorno e falha de armazenamento.

Pendências para fechar R5.2: configuração das políticas no aplicativo, validação de pausas repetidas e ensaio concorrente no ciclo completo, encerramento da cascata, produtor já parado por falha de drenagem, recibos de emergência/falha de recuperação, rotas alternativas e exposição dos resultados no aplicativo. A aprovação desses testes não comprova retorno físico.

Regressão completa: 2451 testes aprovados, zero falhas (`evidence/recipes-r52-block-runner-full.trx`).
