# R6.1 — Interface dos blocos de receita

Evidência de software, com transporte substituído e perfil sintético isolado. Nenhum ensaio físico foi iniciado. O teste `RecipeEditorRenderingTests` usa os controles WPF reais do editor, sem iniciar a receita.

## Cobertura

- Abiótico e biótico, nas três modalidades: condições atuais, condição explícita e matriz; temas claro e escuro (12 casos).
- Seletores reais de protocolo e modalidade alteram o modelo e reconstruem o contexto dos campos. ID e versão do perfil não aparecem como entradas livres.
- Matriz com duas condições, réplicas e agitação/vazão distintas; rolagem até os limites de proteção.
- Rampa de temperatura e O₂ nos dois temas (4 casos). O seletor de controle aparece somente para O₂; selecionar uma opção grava o identificador do bloco na configuração consumida pelo executor.

As imagens estão em `evidence/ui/`. Inspeção direta de imagens representativas confirmou o painel completo, texto contextual dos limites, associação de O₂ e ausência dessa associação na temperatura. Essa inspeção não cobre cada combinação de tamanho de janela ou escala do Windows.

## Captura corrigida

A janela usada para inicializar os controles deixava um recorte nativo no contêiner, cortando a lateral direita nas capturas a 125%. Depois de inicializar os controles, o capturador agora os coloca em um novo contêiner independente, organiza o tamanho lógico solicitado e renderiza por desenho com área explícita. A verificação dos quatro cantos opacos detecta a faixa transparente que existia no defeito anterior.

## Validação

16 testes focados aprovados em `evidence/recipes-r61-editor-rendering-fresh.trx`, antes da inclusão da verificação dos cantos. A regressão completa e compilação Release desta revisão serão registradas abaixo após conclusão. Progresso, resultados e auditoria final dos critérios continuam necessários para encerrar R6.1. R6.2 continua dependente de bancada.

A primeira regressão completa deste incremento encontrou uma corrida no teste de paridade de temas: `XamlReader.Load` dos pincéis compartilhados concorria com recursos da aplicação WPF. O teste agora integra uma coleção sem paralelismo, preservando todas as verificações de cores/pincéis. O TRX inicial registrou 2497 aprovações e uma falha; não constitui aprovação da regressão.

Regressão completa final: **2498 aprovados, zero falhas**, incluindo os 16 casos do editor e a verificação dos cantos opacos. Evidência: `evidence/recipes-r61-editor-full-final.trx`. Comando: `dotnet test Windows_app/tests/OpenTECHub.Tests/OpenTECHub.Tests.csproj --no-restore -p:EnableSourceLink=false -p:SelfContained=false --logger trx --results-directory Windows_app/docs/plans/receitas-r61/evidence -v quiet` (nome do TRX especificado na execução).

Release: `dotnet build Windows_app/src/OpenTECHub/OpenTECHub.csproj -c Release -p:EnableSourceLink=false --no-restore -p:OutputPath=D:/Temp/OpenTECHub-recipe-editor-release/ -v quiet`; **zero erros, 1881 avisos**. Saída: `D:/Temp/OpenTECHub-recipe-editor-release/`. A revisão que contém este recibo e os testes constitui a entrega deste incremento; R6.1 ainda não está encerrada.
