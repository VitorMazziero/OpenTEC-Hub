# R1.2 — Suspensão e retomada da cascata da receita

Base: `52cee5a`. Data: 07/10/2026.

A cascata em `RecipeEngine.Cascade` registra N/Q/O₂ como recursos no coordenador comum. O gate cobre criação/atualização do controlador e despacho; a cessão aguarda qualquer passo em voo. Edição de sintonia também participa dessa barreira e é recusada durante suspensão. A leitura de quadros e avaliação de condição de saída permanecem ativas enquanto o PID está suspenso.

Recibos de pausa são específicos da suspensão: um recibo antigo não retoma uma pausa nova. Encerramento do produtor é terminal, cancela sua espera e impede retomada por callbacks atrasados. O coordenador verifica a disponibilidade dos produtores antes e depois da barreira de devolução.

O passo do PID usa tempo monotônico. Na retomada, somente uma observação posterior à devolução participa do controle; quadros do ensaio continuam válidos para avaliar condições de saída, mas não para atualizar o PID. A primeira observação nova rebaseia história da sonda/derivada e tempo, mantendo esforço, janela integral, sintonia e alocação. Esse quadro mantém a saída restaurada; integração volta no quadro ativo seguinte, sem incluir a duração do ensaio.

Validação focada: **111 aprovados, 0 falhas, 0 ignorados**, filtro `FullyQualifiedName~RecipeEngineTests|FullyQualifiedName~Cascade|FullyQualifiedName~RecipeResourceCoordinatorTests`, incluindo controlador, gate, integração do engine, debounce/quadros e saídas legadas. Evidência: `evidence/recipes-r12.trx`.

Regressão completa:

```powershell
dotnet test Windows_app/tests/OpenTECHub.Tests/OpenTECHub.Tests.csproj -p:SelfContained=false --no-restore -v quiet --logger "trx;LogFileName=recipes-r12-full.trx" --results-directory Windows_app/docs/plans/receitas-r12/evidence
```

Resultado final: **1996 aprovados, 0 falhas, 0 ignorados**. Evidência: `evidence/recipes-r12-full.trx`; não somar suíte focada e completa. Os testes de integração declaram o retorno no simulador para testar a barreira de retomada; não executam nem comprovam a restauração física.

A execução ampla anterior teve uma falha de acesso do Windows na substituição do diário E6 durante criação de request. Seu TRX foi preservado em `recipes-r12-full-before-replacement-fix.trx`. A substituição agora repete no máximo três vezes bloqueios de compartilhamento/arquivo existente gravável, com espera total de 60 ms; diretórios, destinos somente leitura e demais erros continuam falhando. Gravação permanece requisito de avanço. A causa externa específica do bloqueio não foi atribuída; o resultado posterior inclui também o teste de destino inválido que preserva retorno físico e registra falha de persistência.

Próxima etapa: R1.3, capturar referências/rotas/configurações anteriores sem substituir por medições, restaurá-las em ambos os protocolos e em cancelamento/falha. Vínculo da duração dos ramos e recuperação antes de terminar o grupo serão completados na integração R1.3/R4.1; os blocos autônomos ainda não estão habilitados no editor.
