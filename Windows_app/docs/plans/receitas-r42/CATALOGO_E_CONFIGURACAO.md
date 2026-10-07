# R4.2 — catálogo e configuração dos blocos

Primeira entrega parcial de R4.2, sobre `6400d26`. A etapa continua em implementação.

O catálogo comum declara `Determinar kLa` e `Periodicidade`. O editor gerado recebe protocolo abiótico/biótico, condição atual/única explícita/matriz, N/Q/réplicas, perfil/versionamento, limites e política de inconclusivo. OUR aparece somente no biótico; campos de N/Q ou matriz aparecem conforme o modo. Periodicidade mantém defaults de primeiro disparo em 2 h e período de 4 h. Os novos tipos são acrescentados ao enum, preservando valores dos tipos existentes e JSON versão 1.

`RecipeAutonomousBlockConfiguration` interpreta somente entradas ativas, recusa opções numéricas/desconhecidas, unidades inválidas, matrizes vazias, N/Q fora da faixa e quantidades fracionárias. Inteiros digitados no editor e números reabertos do JSON usam a leitura numérica comum. Condição atual permanece sem valores planejados até a captura coordenada pelo futuro construtor de request. Limites obrigatórios não recebem valores universais de exposição: zero é um rascunho incompleto, recusado na validação. Configuração válida não constitui qualificação operacional.

Os blocos podem ser editados e salvos, mas o engine recusa sua partida enquanto o provedor dos blocos não estiver conectado. O executor também recusa os novos tipos explicitamente, evitando conclusão silenciosa sem executar o ensaio. A documentação interna informa esse estado. Receitas legadas mantêm seu caminho atual.

A regressão revelou uma disputa entre retomada e conclusão: `Resume` podia abrir o gate, permitir o fim do fluxo e depois sobrescrever `Completed` com `Running`. Pausa/retomada e estado terminal agora usam a mesma proteção; propagação da pausa às agendas participa dessa transição, com notificações posteriores.

Ainda necessários para R4.2: validar topologia/recursos e vínculo à cascata; selecionar perfis e cascatas no editor; construir requests após captura coordenada; registrar provedor qualificado e executar kLa único/matriz/periódico pelo grafo; apresentar progresso, qualidade, autoria, restauração e sessões/resultados/CSV no visualizador comum. Rampas permanecem em R5. Nenhuma habilitação física foi acrescentada.

Validação: `dotnet test Windows_app/tests/OpenTECHub.Tests/OpenTECHub.Tests.csproj -p:SelfContained=false --no-restore -v quiet --logger "trx;LogFileName=recipes-r42-catalog-state-full.trx" --results-directory Windows_app/docs/plans/receitas-r42/evidence`. **2183 aprovados, 0 falhas, 0 ignorados**. [Regressão completa](evidence/recipes-r42-catalog-state-full.trx). Os testes novos cobrem seis combinações de protocolo/condições, reabertura, visibilidade do editor gerado, entradas inválidas e recusa de atuação sem runtime. A aparência renderizada e a execução dos novos blocos pelo grafo permanecem pendentes.
