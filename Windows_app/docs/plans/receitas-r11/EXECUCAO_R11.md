# R1.1 — Reservas e cessão de recursos

Base: `5669f3a`. Data: 07/10/2026.

O árbitro comum agora distingue autorização por execução/bloco de proprietário global. Uma reserva adquire o conjunto inteiro, ordenado, com espera limitada e cancelável; não adquire parcialmente. Comandos sem a autorização são recusados mesmo quando o proprietário também é Recipe. Atuadores independentes continuam disponíveis ao seu proprietário.

Cessão reservada troca Recipe/KlaAssay diretamente e incrementa geração. Tokens antigos não despacham, devolvem posse ou liberam uma reserva nova. Transferências ordinárias para outro proprietário ativo não atravessam reservas; retomada manual e emergência as revogam. A checagem final e o enqueue do comando são serializados com transferências, fechando a janela após callbacks de rastreamento.

Antes de transferir, a geração atual exige barreira do transporte. `ConnectionManager.DrainCommandsAsync` processa os quadros já buffered em ordem e recusa link indisponível/escrita malsucedida; `IDeviceService` exige implementação dessa capacidade. Isso comprova escrita no transporte, não aplicação física. Cancelamento durante uma escrita recoloca o payload na fila. Dispositivos sem suporte não têm barreira fictícia bem-sucedida.

`RecipeResourceCoordinator` suspende produtores registrados que compartilham os recursos, aguarda seus recibos, reserva e drena o transporte. Ensaios N/Q são serializados durante toda a cessão; produtores independentes não são suspensos. A devolução exige snapshot correspondente, retorno confirmado e recibo de persistência. Falha mantém a autoridade bloqueada e para produtores; não devolve nem retoma automaticamente. Captura/restauração reais permanecem R1.3/R3.1. Registro da cascata real é R1.2.

Validação:

```powershell
dotnet test Windows_app/tests/OpenTECHub.Tests/OpenTECHub.Tests.csproj -p:SelfContained=false --no-restore --filter "FullyQualifiedName~CommandAuthorityTests|FullyQualifiedName~CommandArbiterTests|FullyQualifiedName~ConnectionManagerTests|FullyQualifiedName~RecipeResourceCoordinatorTests" -v quiet --logger "trx;LogFileName=recipes-r11.trx" --results-directory Windows_app/docs/plans/receitas-r11/evidence
```

**58 aprovados, 0 falhas, 0 ignorados**. Cobertura: conjunto atômico, mesmo proprietário em blocos diferentes, espera/timeout/cancelamento, barreira de fila, gerações antigas, emergência, pausa antes da atuação, evidência de retorno/persistência e ramos independentes. Evidência em `evidence/recipes-r11.trx`. Nenhum teste atuou em equipamento.

Próxima etapa: R1.2, registrar a cascata da receita como produtor, integrar gate ao PID+dispatch e rebasear tempo/histórico na retomada. Cancelamento de ensaio em andamento e prioridade de parada na fila são verificados na integração R1.3, sem confundir esta barreira de transporte com restauração física.
