# E5 — Sequências e mapas opcionais

Implementação em software de 07/10/2026. Validação física e distribuição pertencem a E7.

## Fila e tentativas

Uma condição contém N, Q e quantidade de réplicas. Cada réplica pode ter tentativas distintas, com pasta, definição e análise preservadas. Rejeitar uma tentativa não transforma a próxima tentativa em outra réplica. Uma réplica aceita conta uma vez; a tentativa aceita mais recente representa esse ponto na importação para o mapa.

Abiótico e biótico usam a mesma fila. “Próxima corrida” avança explicitamente. “Avançar após aceitar” é opcional: só continua depois do aceite e dos critérios de retorno e intervalo. Uma rejeição não inicia uma cadeia automática de repetições. “Encerrar fila” cancela uma espera agendada e conserva os dados e a corrida atual; interromper a corrida continua usando a ação de parada com finalização/restauração.

Corridas bióticas anteriores devem ter restauração confirmada. Qualquer restauração pendente ou falha bloqueia a próxima corrida. A verificação também está no runner, antes de emitir comandos, e não depende apenas dos botões da tela.

## Limites editáveis

Os padrões são três tentativas por réplica, 100 corridas por sessão, 3600 s de remoção/consumo acumulados e intervalo adicional de zero segundos. Esses números são limites operacionais da fila, não limites biológicos universais. Estão no expander “Limites da fila”, na preparação compartilhada.

Antes do início, a fila reserva o prazo máximo da próxima remoção, a pré-estabilização abiótica quando aplicável e duas confirmações de comando. A exposição registrada inclui as fases de desvio/remoção e comutação ainda sem confirmação de ar no reator. Uma exposição anterior desconhecida ou não finita bloqueia a continuação. O retorno estável e as condições de pré-voo de E2 continuam obrigatórios.

A espera automática é cancelada ao encerrar a fila, desativar o avanço, trocar de sessão ou fechar o ViewModel. Reabrir o aplicativo não inicia pulsos nem retoma uma fila automaticamente. Periodicidade de receitas não é uma réplica imediata; seu contrato pertence a E6.

## Contexto e importação

“Contexto das medições” registra meio, cultivo, janela do cultivo e origem (equipamento, simulação ou não informado). Cada corrida congela esse contexto na definição. Os instantes reais permanecem nos dados brutos e no registro importado. Alterar o contexto da sessão não modifica retroativamente uma corrida anterior.

A aquisição pode ser salva sem contexto completo e sem mapa. Novos pontos enviados ao mapa exigem meio e origem; pontos bióticos também exigem cultivo e janela. Uma corrida sem contexto completo permanece disponível para revisão/exportação, mas não deve ser usada para preencher retrospectivamente um mapa com outro contexto. Importação histórica abiótica sem contexto exige uma escolha explícita e não pode ser misturada com pontos que tenham contexto definido.

A seleção mostra compatibilidade. O envio verifica novamente a revisão atual, o aceite, kLa utilizável, restauração biótica e contexto. Um grupo não mistura protocolos, meios, cultivos, janelas ou origem física/simulada. A verificação é feita para todo o lote antes de inserir pontos.

Revisões e tentativas posteriores da mesma réplica substituem sua contribuição à média; o registro anterior é conservado como excluído. Réplicas independentes podem ser agregadas por N/Q dentro do mesmo contexto. Um ponto não identifica uma superfície: o validador do mapa continua exigindo um desenho experimental suficiente.

Salvar mudanças de um mapa publicado cria um novo rascunho, preservando o arquivo publicado e seu uso pelo controle. O contexto acompanha o snapshot e o payload da publicação. Importar, salvar ou medir não publica um mapa automaticamente.

## Verificação

Os testes cobrem contagem de réplicas/tentativas, limites independentes, reserva de exposição, intervalo, retorno físico e recusa antes de comandos. A integração da tela cobre continuação dos dois protocolos e cancelamento da espera. A importação é exercitada com arquivos temporários: persistência de contexto, substituição de tentativa, recusa de outro meio, ponto único como rascunho e preservação do arquivo publicado.

Verificação integrada: 298 aprovados, zero falhas, incluindo kLa, receitas e renderização compacta. Escopo e arquivo TRX registrados em `validation-receipt.json`. Os testes não comandam equipamento real nem modificam arquivos de cultivo originais.
