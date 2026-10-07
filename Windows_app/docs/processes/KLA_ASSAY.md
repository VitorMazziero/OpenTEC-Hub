# Determinar kLa — procedimento comum

## Preparação e arquivos

Selecione Abiótico/Biótico e Único/Múltiplos. Único usa N/Q e uma réplica; Múltiplos usa a matriz. Os dois modos guardam corrida, eventos, série bruta calibrada, revisão e contexto. Nenhum deles exige criar um mapa. A origem equipamento/simulação, meio, cultivo e janela são congelados por corrida.

N de 50–1000 rpm, Q de 0,5–16 L/min e OD usual de cultivo de 30–100% são os padrões informados para este cultivo. Remoção a 100 rpm, alvo de OD de 5–20%, prazos, limites e intervalo podem ser alterados na preparação. O alvo temporário do ensaio não substitui a faixa usual de controle nem representa tolerância biológica universal.

Confira a leitura calibrada da sonda e a chegada de novas amostras de O₂. Quadros de outros sensores não contam como novos pontos de OD. Não atribua um tempo de resposta à sonda sem medição; qualquer tecnologia é admitida, mas a identificação da taxa pode ficar condicionada ou ser recusada.

## Abiótico

1. Estabilize no equilíbrio inicial em meio sem consumo biológico.
2. Confirme disponibilidade de N₂. Retire O₂ pelo arranjo de válvulas definido, com a rotação de remoção, até o alvo.
3. Aguarde OD estável. Estabilize o ar no escape pelo procedimento de pré-estabilização já configurado; confirme vazão e comutação antes de registrar o início da reoxigenação.
4. Registre a recuperação em N/Q da condição até o critério de término. Revise a janela e o equilíbrio; a inclinação de ln(Ceq−OD) estima kLa sob as hipóteses declaradas.
5. Conclua a finalização física antes da revisão/continuação.

## Biótico

**Execução física ainda não liberada:** E7 exige validação do arranjo e do retorno em bancada. A build candidata permite revisar arquivos e usar simulação isolada; não inicia ensaio biótico no equipamento.

Procedimento contratado para essa validação:

1. Estabilize o cultivo no equilíbrio inicial, com ar e condições de controle conhecidas. Confirme N₂ fechado e isolado na fonte.
2. Mantenha o fluxômetro em funcionamento e v_flow aberto. Desvie o ar para o escape e coloque a agitação na rotação de remoção. A curva de queda fornece consumo respiratório até o alvo ou um critério de retorno antecipado.
3. Com vazão já estável, comute a válvula para recolocar ar no reator; registre a confirmação desse instante. Não desligue/religue o fluxômetro como no abiótico.
4. Registre a recuperação em N/Q da condição. Use OUR no balanço dC/dt = kLa(C*−C)−OUR. Com consumo constante, Ceq = C*−OUR/kLa: o patamar respiratório não é automaticamente C*=100%.
5. Restaure ar, agitação e controle do cultivo; confirme vazão, agitação medida, OD estável e retorno do controlador. Só depois revise ou continue. Falha de retorno conserva dados e bloqueia a fila.

## Revisão, fila e mapas

Confira Oxigênio, Regressão e Diagnóstico. Dados brutos permanecem intactos; escolhas manuais geram uma nova revisão. Qualidade científica, decisão do operador e retorno físico são independentes. IC95 é condicional a Ceq, janela e erros, não incerteza total do método. Consumo variável, OTR residual, resposta lenta e força motriz insuficiente precisam aparecer como condicionamento/recusa.

Aceitar torna a réplica utilizável; rejeitar conserva a tentativa. Uma tentativa seguinte pertence à mesma réplica. “Próxima corrida” respeita retorno, intervalo, tentativas e exposição; “Avançar após aceitar” é opcional. “Encerrar fila” conserva histórico e cancela avanço/espera; parar a aquisição continua exigindo finalização/retorno.

Importar para Mapeamento kLa é opcional. Aceitos devem compartilhar protocolo, meio, cultivo, janela e origem. Tentativa/revisão posterior substitui a contribuição anterior da mesma réplica, sem duplicar sua média. Um ponto não identifica uma superfície. Salvar mudanças de mapa publicado cria outro rascunho, preservando a publicação utilizada pelo controle.

## OUR do cultivo e retorno

O sensor virtual de OUR utiliza kLa do mapa ativo e condições aproximadamente estacionárias. Ele não é referência independente para medir novamente o mesmo kLa. Durante o pulso, é suspenso e não integra através da perturbação; a retomada física deve preceder sua continuidade. Receita/cascata concorrente precisa respeitar a posse compartilhada dos atuadores.

Reabrir histórico, recalcular, exportar ou importar não inicia atuadores. Uma execução interrompida não autoriza repetir o pulso por reconexão. A API futura conserva IDs e bloqueia reservas sem retorno confirmado.
