# R6.1 — Curva da tentativa consultada

O teste de histórico acessa o `WpfPlot` que a própria `KlaDeterminationView` colocou em `ChartDoHost`, depois de executar o botão “Abrir tentativa” e o ciclo normal de carregamento/renderização. Não cria um gráfico substituto e não chama métodos privados de desenho.

Verifica uma única série visível de OD, extensão temporal 0–39 s e extremos 20% até o último valor reaberto do arquivo (78,79%). Os 40 pontos carregados e o modo de revisão somente leitura já são verificados no mesmo teste. A precisão do arquivo é preservada: o valor original sintético 78,785485… é armazenado/reaberto como 78,79%.

O teste exporta o próprio objeto Plot da tela para `evidence/ui-results/curve-<protocolo>-<tema>.png`. Inspeção direta da curva biótica clara confirmou subida suave, eixo OD (%) e limites do protocolo. A evidência prova composição da série e renderização pelo componente gráfico; não prova a composição final de pixels da superfície nativa dentro de uma janela visível. A captura WPF da revisão continua sem essa superfície. Não foi alterado o aplicativo.

Validação: **4 casos aprovados, zero falhas**, em `evidence/recipes-r61-actual-curves-final.trx`, ambos os protocolos/temas. Comando: `dotnet test Windows_app/tests/OpenTECHub.Tests/OpenTECHub.Tests.csproj --no-restore -p:EnableSourceLink=false -p:SelfContained=false --filter FullyQualifiedName~AutomaticHistoryRenders --logger trx --results-directory Windows_app/docs/plans/receitas-r61/evidence -v quiet` (nome do TRX especificado na execução).

Dados são sintéticos; não é validação física nem científica da determinação de kLa/OUR. Auditoria final de R5/R6.1 e qualificação R6.2 permanecem pendentes.
