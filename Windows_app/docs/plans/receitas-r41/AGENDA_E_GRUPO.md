# R4.1 — agenda e barreira do grupo paralelo

Primeira entrega de R4.1, sobre `05e1465`. A etapa permanece em implementação.

`RecipePeriodicExecutor` usa somente tempo monotônico para os slots da agenda R0: 2/6/10 h, sem deslocar a cadência após o alvo. Slots vencidos durante atraso, pausa, aquisição ou recuperação são registrados e pulados. A tolerância de despacho é explícita e menor que o período; atraso acima dessa tolerância não dispara o slot. O alvo deve terminar sua recuperação antes de retornar. A pausa durante o alvo cancela aquisição e aguarda esse retorno, antes de estacionar. A gravação do início deve concluir antes de despachar o alvo; falhas encerram a agenda.

`RecipeParallelGroup` cancela irmãos quando um membro falha e aguarda todos, incluindo recuperação independente. Pode vincular a duração dos seguidores a um proprietário do grupo. O fan-out legado do engine já usa a barreira em erro/cancelamento; o término normal de ramos legados mantém sua semântica anterior.

Ainda necessários: vínculo real da agenda a uma cascata no engine; diário durável dos slots; pausa do engine ligada ao executor; encerramento da cascata aguardando o ensaio **antes** de encerrar o gate/controlador. Encerrar o gate primeiro invalidaria o snapshot do controlador durante a devolução. O helper genérico de proprietário, sozinho, não resolve essa ordem. R4.2 acrescentará catálogo/editor, usando esses caminhos de R4.1.

Os testes novos verificam cadência sob mudanças UTC, pausa cobrindo slots, atraso, alvo atravessando slot, recusa de despacho sem registro, cancelamento/pausa durante alvo e barreira de recuperação em fim de grupo, falha e emergência. Transporte físico e qualificação de instalação não são demonstrados por esses testes.

O teste comum de R2.1 teve sua limpeza corrigida para cancelar a aquisição antes de aguardar recuperação e fechar a API; deixou de ignorar exceção de espera. O limite de espera do teste passou de 3 para 10 segundos para incluir processamento sob carga. Nenhum deadline físico foi alterado.

Validação final: `dotnet test Windows_app/tests/OpenTECHub.Tests/OpenTECHub.Tests.csproj -p:SelfContained=false --no-restore -v quiet --logger "trx;LogFileName=recipes-r41-base-pause-full.trx" --results-directory Windows_app/docs/plans/receitas-r41/evidence`. **2157 aprovados, 0 falhas, 0 ignorados**, incluindo 10 casos novos. [Evidência](evidence/recipes-r41-base-pause-full.trx). Uma execução anterior teve falha de limpeza do teste biótico; a execução final inclui a correção e o caso adicional de pausa durante alvo.
