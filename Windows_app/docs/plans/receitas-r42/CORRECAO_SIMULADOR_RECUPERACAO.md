# Sincronização do simulador de recuperação biótica

Na regressão de progresso, o teste de término normal biótico falhou esperando os comandos de recuperação. O simulador limpava a lista de comandos depois de uma primeira amostra estável da rota de retorno; a recuperação pode iniciar durante essas amostras, e sua sequência inicial podia ser apagada.

Esse cenário agora preserva as mensagens e aguarda três despachos observados durante `RestoringCultivation` antes de alimentar a confirmação de retorno. Comandos anteriores de aquisição não satisfazem essa espera. O restante da recuperação, recibos, análise e reabertura continua sendo exercitado pela mesma API e runner. Limites científicos, tempos do produto e tempo de espera de três segundos foram mantidos.

A regressão seguinte revelou a mesma suposição incorreta no caso de prazo abiótico: comandos de aborto/watchdog satisfaziam a contagem genérica antes da recuperação começar. A confirmação enviada cedo passava a ser o baseline da recuperação e as amostras seguintes repetiam esse ID, sem confirmação nova. A espera por despachos da fase de recuperação agora antecede a confirmação nos dois protocolos, tanto em término normal quanto cancelamento/prazo. A exigência de ID fresco do produto foi preservada. A evidência intermediária está em `evidence/recipes-r42-live-progress-final-full.trx`.

Validação focada: 11 testes aprovados, zero falhas em `evidence/recipes-r42-live-progress-recovery-driver.trx`, incluindo os modos do executor e a apresentação ao vivo. A regressão inicial com a falha está em `evidence/recipes-r42-live-progress-full.trx`; a revisão final recebe evidência separada.

Regressão final sincronizada: 2323 testes aprovados, zero falhas (vidence/recipes-r42-live-progress-synchronized-full.trx). Release: zero erros, 1269 avisos.
