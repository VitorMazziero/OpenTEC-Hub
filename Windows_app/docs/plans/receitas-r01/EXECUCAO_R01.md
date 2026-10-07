# R0.1 — Base de integração E5/E6/E7

Data: 07/10/2026. Parent: `c51253f`. Árvore das fontes e documentos antes deste recibo: `b273d6246e2183ce87fd7298f8cf979681afd022`.

Base recebida consolidada com sequência E5, API de pulso E6, bloqueio operacional/documentação E7 e regressão de quadros da cascata. A lista exata está em `baseline-files.txt`. Recibos históricos E7 preservam suas revisões de origem e não são certificados desta compilação.

Comando executado:

```powershell
dotnet test Windows_app/tests/OpenTECHub.Tests/OpenTECHub.Tests.csproj -p:SelfContained=false --no-restore -v quiet --logger "trx;LogFileName=recipes-baseline.trx" --results-directory Windows_app/docs/plans/receitas-r01/evidence
```

Resultado: **1959 aprovados, 0 falhas, 0 ignorados**. Evidência: `evidence/recipes-baseline.trx`. Inclui o protótipo isolado do gate de suspensão e seus testes; não prova integração do gate com o engine, atuação autônoma ou retorno físico.

Capturas WPF alteradas e duplicatas OneDrive permaneceram fora deste commit de fontes. Não foram apagadas nem selecionadas como evidência desta etapa. Sua triagem visual pertence à verificação de apresentação; não afetam a compilação dos serviços.

R0.1 concluído para a base de software reproduzível. Próxima etapa: R0.2, vínculo entre invocação R0 e pulso E6, migração e capacidades por protocolo/instalação. A atuação biótica física segue bloqueada.
