# R5.2 — Gravação durável da captura inicial

`RecipeRampCheckpointStore` grava a configuração e a captura inicial congeladas por execução e invocação. A identidade do nó fica no registro; caminhos usam exclusivamente GUIDs. A gravação atômica só retorna depois da barreira durável do escritor e da leitura de confirmação.

A mesma invocação aceita novamente o mesmo conteúdo; uma captura diferente é recusada. Uma trava de arquivo impede dois escritores de alterar simultaneamente o registro. Cancelamento antes de adquirir a trava não grava; após enfileirar a escrita, o método conclui a confirmação durável para não devolver um sucesso ambíguo.

A leitura valida versão, identidades, configuração, referências exigidas, evidências, comandos aceitos e snapshot da cascata. Referências transportadas não são apresentadas como medições físicas. O registro não permite reinício automático, nem autoriza atuação.

Seis testes focados aprovados: captura direta e explícita, identidade persistida, gravação idempotente, conflito de captura, ausência de referências de retorno, divergência entre referência e comando aceito, cancelamento prévio, armazenamento indisponível e identidade alterada em disco. Release compilado em `D:/Temp/OpenTECHub-ramp-checkpoint-release/`, com 0 erros e 1451 avisos. A regressão completa não foi repetida neste incremento.

Ainda falta consumir este recibo no ciclo de vida do bloco, persistir seu término, integrar cancelamento e confirmação dos destinos. A execução de rampas permanece bloqueada enquanto essas fronteiras não forem integradas e verificadas.
