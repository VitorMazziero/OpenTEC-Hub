# R6.1 — Motivos compreensíveis no histórico

A lista de tentativas automáticas agora reutiliza as explicações em português já usadas no diagnóstico científico. Acrescenta mensagens para resultado qualificado, pausa da receita, cancelamento e falha de aquisição. Por exemplo, `insufficient_confirmed_recovery` aparece como “Recuperação confirmada insuficiente”, e `qualified_result` como “Resultado atende aos critérios do perfil”.

Os códigos originais permanecem nos objetos persistidos e no CSV. O comportamento da política de decisão não mudou. Diagnósticos desconhecidos preservam o código com a indicação “Diagnóstico adicional”, permitindo rastrear registros de versões diferentes.

Os testes de resumo comparam a explicação exibida e confirmam os códigos originais no CSV nos dois protocolos. Os quatro casos de renderização do histórico também estão incluídos na regressão desta revisão e regeneram as imagens em `evidence/ui-results/`.

Este incremento não encerra R6.1: apresentação de resultado terminal na página Receitas, consulta de tentativa com curvas e auditoria final permanecem pendentes. R6.2 exige bancada.

Validação desta revisão: regressão completa com **2502 aprovados, zero falhas**, em `evidence/recipes-r61-readable-results-full.trx`. Release em `D:/Temp/OpenTECHub-readable-results-release/`, zero erros e 1881 avisos. Comandos: `dotnet test Windows_app/tests/OpenTECHub.Tests/OpenTECHub.Tests.csproj --no-restore -p:EnableSourceLink=false -p:SelfContained=false --logger trx --results-directory Windows_app/docs/plans/receitas-r61/evidence -v quiet`; `dotnet build Windows_app/src/OpenTECHub/OpenTECHub.csproj -c Release -p:EnableSourceLink=false --no-restore -p:OutputPath=D:/Temp/OpenTECHub-readable-results-release/ -v quiet`. Nome do TRX especificado na execução. Inspeção direta da imagem biótica escura confirmou as novas mensagens e preservação dos estados de qualidade.
