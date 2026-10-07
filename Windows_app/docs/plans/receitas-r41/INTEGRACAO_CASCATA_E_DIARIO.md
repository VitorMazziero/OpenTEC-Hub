# R4.1 — vínculo à cascata e diário de slots

Segunda entrega de R4.1, após `44a7775`. Base de validação inclui as correções de testes `b7aa3ce` e `e582615`.

O engine resolve os trabalhos periódicos por `IRecipePeriodicWorkSource`, valida e congela a lista antes de tomar os atuadores. Ao entrar na cascata, inicia seu grupo. Pausa/retomada do engine chegam às agendas. Falha de um trabalho cancela o grupo e desperta a cascata, inclusive durante pausa global.

O encerramento agora aguarda os trabalhos **antes** de parar o gate, remover o controlador ou desregistrar seu produtor. Assim, a recuperação e a devolução podem verificar o mesmo estado capturado, sem tentar retomar um controlador já removido. A liberação final da receita ocorre depois desse encerramento. Emergência mantém precedência e não autoriza restauração que reative o equipamento.

Se uma cessão desta execução continuar retida por kLa depois desse encerramento, a receita não pode anunciar sucesso: o coordenador identifica a autoridade ainda atual e a segurança aplica parada explícita de N/Q/O₂, revogando a reserva. Essa exceção não altera o encerramento normal com devolução confirmada e não emite novos comandos após revogação por emergência. Dois testes cobrem falha explícita do alvo e retorno do callback sem devolução.

`RecipePeriodicJournal` grava transições imutáveis por execução/agenda/slot, com hash da receita, UTC do registro e tempo monotônico decorrido. Usa exclusão entre processos, barreira durável por pasta e leitura de confirmação. Rejeita alteração de definição, transição fora de ordem, campos desconhecidos/duplicados e mistura de execução/hash. Um `Started` existente recusa novo despacho mesmo com payload igual; a reabertura lê o histórico e não reexecuta um slot interrompido. Transições terminais iguais podem ser confirmadas novamente sem nova atuação.

## Evidência e limites

Os testes de ciclo do engine verificam saída/pausa com reserva anterior à atuação e falha de diário com receita ativa/pausada. Os testes integrados usam **o runner kLa, E6, coordenador, análise e armazenamento comuns**, com cascata real do engine e transporte/tempo controlados. Abiótico/Biótico × saída/pausa/emergência totalizam seis cenários: saída e pausa restauram e persistem antes do fim; emergência mantém ausência de restauração e não produz comandos tardios. O diário registra início e cancelamento após o retorno do alvo.

Cadência 2/6/10 h, mudança UTC, slots perdidos, alvo atravessando slot e falha de registro antes de despachar têm evidência na primeira entrega. A segunda acrescenta persistência/reabertura, recusa de redisparo, vínculo efetivo ao engine e recuperação com runner comum.

Os testes legados foram corrigidos sem alterar prazos físicos: o teste de redesenho espera o evento do dispatcher com limite, em vez de presumir despacho em 50 ms; o caso de deadline do adaptador usa temporizadores virtuais junto ao relógio avançado. As execuções intermediárias com falha permanecem locais para diagnóstico; não são recibos de aprovação.

O driver biótico legado passou a ceder a execução entre seus quadros, mantendo os mesmos valores e intervalos simulados. Isso permite que preparação/recuperação assíncronas processem os quadros, em vez de enviar toda a sequência antes do próximo trecho do runner.

O provedor de trabalhos está disponível no construtor do engine. **Catálogo, leitura dos parâmetros dos blocos, registro desse provedor no aplicativo e apresentação dos resultados pertencem a R4.2 e continuam pendentes.** O aplicativo ainda não oferece o bloco periódico/kLa autônomo ao operador. Esta entrega qualifica integração de software em ambiente isolado, não operação física ou bancada.

Validação final: `dotnet test Windows_app/tests/OpenTECHub.Tests/OpenTECHub.Tests.csproj -p:SelfContained=false --no-restore -v quiet --logger "trx;LogFileName=recipes-r41-safety-release-full.trx" --results-directory Windows_app/docs/plans/receitas-r41/evidence`. **2172 aprovados, 0 falhas, 0 ignorados**. [Regressão completa](evidence/recipes-r41-safety-release-full.trx). R4.1 está entregue para a base de software; próxima etapa: R4.2.
