# R6.1 — Resultado terminal e consulta de tentativa

Dois casos WPF reais (claro/escuro) mostram resultado de falha de restauração na página Receitas. Verificam separadamente retorno não confirmado, gravação confirmada e mensagem de falha. O comando do botão real “Abrir sessão e tentativas”, com seu parâmetro ligado pelo XAML, encaminha a pasta correta ao evento de navegação. O engine é substituído; este teste não executa aquisição nem confirma restauração física.

Os quatro casos de histórico foram ampliados: gravam 40 pontos sintéticos por tentativa e uma análise armazenada, reabrem a sessão, executam o comando do botão real “Abrir tentativa” e comprovam 40 pontos carregados, revisão aberta e decisão humana desabilitada. A análise científica armazenada (77 h⁻¹ neste cenário) não substitui a decisão automática inconclusiva da primeira tentativa. Dados e valores são artificiais, apenas para verificar navegação e preservação.

Validação: **6 casos aprovados, zero falhas**, em `evidence/recipes-r61-results-navigation.trx`. Imagens: `evidence/ui-terminal/` e `evidence/ui-results/review-*.png`. Inspeção direta confirmou as mensagens terminais no tema claro e a abertura da revisão biótica.

Limitação encontrada: a captura WPF não apresenta a curva do componente gráfico, apesar dos pontos carregados no modelo. O componente possui atualização temporizada e superfície própria. Portanto, as imagens de revisão não comprovam a curva; essa verificação permanece aberta. Não foi alterado o comportamento do gráfico neste incremento.

Comando: `dotnet test Windows_app/tests/OpenTECHub.Tests/OpenTECHub.Tests.csproj --no-restore -p:EnableSourceLink=false -p:SelfContained=false --filter 'FullyQualifiedName~AutomaticHistoryRenders|FullyQualifiedName~TerminalRestorationFailure' --logger trx --results-directory Windows_app/docs/plans/receitas-r61/evidence -v quiet` (nome do TRX especificado na execução). A regressão completa anterior tem 2502 aprovações; não foi repetida neste incremento de testes. Auditoria final, gráfico e R6.2 permanecem pendentes.
