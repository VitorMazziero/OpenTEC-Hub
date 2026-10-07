# R4.2 — executor dos blocos e grupo do grafo

O engine aceita os novos blocos quando recebe `IRecipeAutonomousWorkSource` e um coordenador de recursos. Sem esse provedor, a partida continua recusada. O aplicativo ainda não registra o provedor; a seleção de perfis e a construção concreta das invocações permanecem na próxima entrega de R4.2.

Antes de emitir comandos, o engine resolve um plano sobre uma cópia da receita e verifica cobertura de todos os ensaios/agendas, perfil/versionamento/protocolo, evidência e capacidade isolada. A agenda deve corresponder exatamente ao alvo, cascata e período do grafo. Planos desconhecidos, duplicados ou físicos não são aceitos por este caminho. O adaptador comum e sua qualificação independente permanecem necessários: o provedor é um componente interno confiável, não uma API de autorização externa.

Determinar kLa recebe uma invocação única ou um slot periódico, e seu resultado é congelado e verificado contra execução, bloco, hash e slot. Todos os resultados retornados pelo provedor ficam acessíveis em `IRecipeEngine.AutonomousResults`, incluindo inconclusivos e falhas. A continuação sem resultado só admite estado inconclusivo, com retorno e gravação confirmados, e política explícita. Falhas operacionais, de recuperação ou persistência interrompem a receita. Cancelamento mantém o resultado recebido e não autoriza continuação.

As agendas do grafo usam entrada diferida: o relógio começa quando Periodicidade é alcançado, inclusive se há um temporizador anterior no seu ramo. Antes de a cascata coordenada entrar, slots são pulados e registrados. O fim da cascata cancela e aguarda todos os membros, inclusive a recuperação ativa; agendas ainda não alcançadas terminam sem disparar. A saída do bloco periódico não percorre seu alvo como continuação comum. Falha de um ramo antes da entrada da cascata cancela a agenda e não deixa o grupo esperando indefinidamente. Agendas externas legadas e agendas do grafo não podem disputar o mesmo grupo.

O validador recusa produtores paralelos que escrevam N/Q/O₂ fora do grupo coordenado: setpoints individuais/múltiplos, malha de aeração, outra cascata, outro ensaio e reset de variáveis. Ensaios periódicos da mesma cascata podem compartilhar o coordenador, que serializa as cessões. Referências independentes de temperatura/pH permanecem permitidas. R5 deverá ampliar a identificação de recursos para as rampas.

## Verificação

79 testes focados aprovados. Incluem execução única, política de inconclusivo, falhas de retorno/gravação/operação, plano inválido antes de comandos, proveniência do resultado, cadence 2/6/10, entrada atrasada, cascata atrasada e falha do ramo anterior à cascata.

Os seis cenários existentes de runner comum foram mantidos e repetidos pelo novo grafo: abiótico/biótico × saída, pausa e emergência durante recuperação. Usam orquestrador, API E6, runner, núcleo científico, armazenamento, diário, árbitro e controlador reais de software com dispositivo/relógio de teste. Confirmam devolução e persistência antes do fim do grupo; emergência não emite comandos tardios. Não são evidência de bancada.

Regressão completa: **2226 aprovados, zero falhas ou ignorados**, 57 s. Evidência: `evidence/recipes-r42-runtime-full.trx`. Comando: `dotnet test Windows_app/tests/OpenTECHub.Tests/OpenTECHub.Tests.csproj -p:SelfContained=false -p:EnableSourceLink=false --no-restore -v quiet`. SourceLink foi desabilitado somente nesta chamada porque o OneDrive recusou acesso ao arquivo gerado de metadados em `obj`; nenhuma configuração do produto foi alterada por isso.

## Próxima execução

1. Criar registro de perfis operacionais por instalação/protocolo, com limites de exposição, critérios científicos/de retorno e confirmação prévia da montagem; integrar sua seleção e a seleção da cascata no editor.
2. Implementar o provedor concreto com captura coordenada fresca, incluindo condição atual; registrar no aplicativo apenas para o ambiente qualificado. Cada slot deve construir nova invocação e sessão comum, preservando orçamento do cultivo.
3. Integrar progresso, qualidades independentes, autoria, restauração, sessões e resumo CSV no visualizador comum. Completar a política de pausa/cancelamento para o bloco único e verificar a interface renderizada.
4. Fechar R4.2 antes de avançar às rampas R5.1/R5.2, regressão/UI R6.1 e bancada R6.2.

R4.2 continua em implementação. A integração do engine não libera atuação física nem conclui os critérios do editor/resultados.
