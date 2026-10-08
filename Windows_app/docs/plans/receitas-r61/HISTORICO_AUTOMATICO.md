# R6.1 — Histórico automático de kLa

Quatro casos WPF reais verificam sessões abióticas/bióticas nos temas claro/escuro. Cada documento sintético é gravado e reaberto por `LoadTest`, o caminho normal do aplicativo. A lista expandida preserva a tentativa recusada e a selecionada, qualidade independente de kLa/OUR, restauração por tentativa e autoria da política. Os seletores de protocolo permanecem desabilitados; os botões de consulta estão ligados ao comando correto. Aprovação humana permanece Pending e não é requisito de seleção automática.

Evidência: `evidence/recipes-r61-history-final.trx` — **4 aprovados, zero falhas**. Imagens: `evidence/ui-results/history-<protocolo>-<tema>.png`. Inspeção direta do histórico biótico escuro confirmou protocolo, duas tentativas e separação entre restauração por tentativa e resultado terminal ainda não confirmado. Não há dados brutos neste cenário: o gráfico vazio é esperado; não constitui verificação de curvas ou análise aberta.

O primeiro cenário atribuía `CurrentTest` diretamente e deixava o cabeçalho no protocolo padrão. Foi corrigida a preparação do teste para usar gravação/reabertura, sem mudar o aplicativo. O caminho normal carrega corretamente o protocolo.

Comando: `dotnet test Windows_app/tests/OpenTECHub.Tests/OpenTECHub.Tests.csproj --no-restore -p:EnableSourceLink=false -p:SelfContained=false --filter FullyQualifiedName~AutomaticHistoryRenders --logger trx --results-directory Windows_app/docs/plans/receitas-r61/evidence -v quiet` (nome do TRX especificado na execução).

Limites: resultados terminais na página Receitas, abertura da tentativa com curvas, exportação pela interface e auditoria final ainda precisam de evidência visual/integrada correspondente. A lista atualmente apresenta códigos técnicos de motivo; a apresentação de mensagens compreensíveis ao usuário também deve ser revisada. A regressão completa anterior tem 2498 aprovações; não foi repetida neste incremento de testes. R6.2 permanece dependente de bancada.
