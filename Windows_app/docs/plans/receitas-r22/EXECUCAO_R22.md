# R2.2 — execução automática da matriz

Data: 07/10/2026. Base anterior: `062816b`; continua as duas entregas parciais desta etapa.

O orquestrador executa Abiótico/Biótico × Único/Múltiplos sobre um único diário E6 e o runner científico comum. Cada pulso reserva os recursos e recaptura o estado anterior; a recuperação confirma e persiste o retorno antes de devolver o controle. A espera entre tentativas ocorre depois dessa devolução. A seleção automática é persistida antes de avançar, sem aceitar/rejeitar como operador.

O resultado terminal vincula cada tentativa à sua solicitação e ao seu próprio snapshot. O armazenamento verifica a sessão, seleções, dados brutos e leitura final. Falha de gravação interrompe o avanço e preserva separadamente a evidência conhecida de retorno. Não há retomada automática de sessão interrompida após reinício.

Foram corrigidos dois problemas encontrados na matriz: a janela de estabilidade biótica passou a terminar na última amostra fresca, evitando perder a primeira amostra durante a preparação durável; o número científico da tentativa não é mais alterado pelo sufixo usado para evitar colisão de pastas de condições com N/Q iguais.

## Validação

Comando: `dotnet test Windows_app/tests/OpenTECHub.Tests/OpenTECHub.Tests.csproj -p:SelfContained=false --no-restore -v quiet --logger "trx;LogFileName=recipes-r22-full.trx" --results-directory Windows_app/docs/plans/receitas-r22/evidence`

Resultado: **2147 aprovados, 0 falhas, 0 ignorados**, incluindo 24 cenários integrados do orquestrador. Evidência: [regressão completa](evidence/recipes-r22-full.trx).

Os cenários cobrem os quatro modos, repetição seguida de sucesso, esgotamento, cancelamento durante espera/aquisição, prazo monotônico do bloco com recuperação independente, orçamento cumulativo, recusa padrão de qualidade condicional, condições com N/Q iguais e falhas de gravação da seleção/resultado. Os testes usam transporte e tempo controlados, runner, análise e armazenamento comuns; não constituem bancada física nem uma nova verificação visual do executável.

## Limites e próxima etapa

Execução continua restrita ao perfil isolado qualificado pelo fixture/playback. Montagem deve estar confirmada antes da partida, importação de mapa permanece desabilitada por padrão e decisões condicionais exigem razões permitidas explícitas. O resultado agregado não acrescenta estimativas científicas ausentes; valores detalhados de OUR e intervalos continuam nas revisões comuns de análise.

R4.1 deve integrar agenda monotônica e duração do grupo paralelo ao engine, aguardando recuperação ao encerrar a cascata. R4.2 deve conectar blocos, editor e apresentação de resultados. Rampas e verificação de bancada continuam pendentes.
