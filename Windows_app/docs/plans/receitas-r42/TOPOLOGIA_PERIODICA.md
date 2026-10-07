# R4.2 — alvo periódico e fluxo paralelo

`RecipePeriodicTopology.ReadBinding` resolve a agenda, seu alvo e a cascata coordenada em um vínculo comum para o futuro provedor de execução. A validação do grafo usa a mesma interpretação.

Nesta entrega, kLa é o primeiro alvo qualificado pelo contrato de cancelamento/recuperação. A saída da Periodicidade, rotulada **Alvo periódico**, exige exatamente uma conexão para a entrada de Determinar kLa. O ensaio pertence exclusivamente à agenda e não tem continuação própria: ao concluir uma invocação, retorna ao agendador. A continuação da receita fica na saída normal da cascata. Outros tipos de alvo exigem seu contrato de recursos e cancelamento antes de serem integrados, conforme o escopo do plano.

O vínculo à cascata exige ramos paralelos alcançáveis pelo fluxo normal. Uma agenda que depende da conclusão da cascata, inclusive por uma junção, é recusada. Ligações pela condição de saída continuam inválidas. Junções internas ao ramo periódico são permitidas quando não dependem do encerramento da cascata. A validação existente de ciclos permanece aplicada ao grafo inteiro.

Cobertura: salvar/reabrir o vínculo; ausência, multiplicidade e tipo do alvo; compartilhamento do alvo; continuação indevida; portas de laço; dependência sequencial; junção aguardando a cascata; condição de saída legada; junção válida dentro do ramo periódico; rótulo e cardinalidade do conector.

R4.2 continua em implementação. Esta entrega não conecta o provedor no aplicativo nem habilita atuação. Ainda faltam validação dos conflitos de recursos nos ramos, perfis/editor, execução pelo grafo, apresentação dos resultados e CSV. A agenda precisa começar na entrada do bloco Periodicidade; a integração R4.1 existente inicia seus trabalhos na entrada da cascata e deve ser adaptada ao novo fluxo, preservando o encerramento aguardável e os testes legados.

Validação final: regressão completa `OpenTECHub.Tests`, `SelfContained=false`, sem restauração de dependências; **2200 aprovados, zero falhas ou ignorados**, 56 s. Evidência em `evidence/recipes-r42-topology-final.trx`.
