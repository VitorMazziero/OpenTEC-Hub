# R3.1 — persistência como requisito de avanço

Infraestrutura concluída e validada em software. Commits anteriores: `3015355`, `89e30a3`, `796b2d5`; esta entrega fecha os testes de fronteiras e endurece a geração da cessão.

## Critérios e evidências

- `BackgroundFileWriter.FlushDurableAsync` propaga falhas acumuladas da sessão, fecha streams e força gravação dos arquivos conhecidos. Falhas de fechamento também são preservadas. A barreira legada não é apresentada como garantia durável. `DurableWriterTests` cobre consumidores síncronos/assíncronos e isolamento entre sessões.
- `KlaTestStore` persiste request, snapshot e reserva antes da atuação; resultado e decisão no checkpoint terminal. Recibo só retorna após barreira e leitura de confirmação. A tentativa tem uma única fonte de dados brutos, cujo hash é conferido. Checkpoints são congelados antes de espera assíncrona, possuem exclusão entre instâncias e leitura estrita.
- Conclusão exige proprietário `KlaAssay` e geração correspondente à preparação. Reserva, execução, bloco e recursos não podem mudar. `KlaAttemptPersistenceTests` verifica identidades, conflito de payload, geração, raw ausente/alterado, JSON ambíguo e escritor concorrente.
- Falhas de escrita antes do checkpoint, durante aquisição e na gravação terminal não produzem recibo. Preparação existente é preservada. Reabertura após essas fronteiras preserva a cobrança conservadora e não repete atuação. Testes com obstáculo real de filesystem e falha injetada no writer cobrem os dois modos de escrita.
- `IKlaAssayApi.ReconcileRecipeAttempt` cruza diário E6 e sessão comum, grava a reconciliação sem executor ativo e conserva início/orçamento. Resultado histórico não confirma recuperação atual: estado incerto permanece `Interrupted`. Os testes E6 também cobrem at-most-once, limites persistidos e migração sem inventar evidência.
- `ReturnPersistedAsync` consome o recibo real e confere a cessão ativa antes de retomar produtores. Recuperação física permanece um requisito independente. Testes da cascata real e dos dois protocolos usam o armazenamento comum; recibo fictício é recusado para snapshot capturado.

## Validação

Projeto completo `OpenTECHub.Tests`, `SelfContained=false`, sem restaurar dependências: **2077 aprovados, zero falhas, 36 s**. Evidência: `evidence/recipes-r31-boundaries-full.trx`. Os 50 testes da execução direcionada anterior estão em `evidence/recipes-r31-write-boundaries.trx`; a regressão completa inclui o endurecimento posterior da geração.

## Limites e próxima etapa

R2.1 deve chamar esses caminhos ao conectar o runner ao executor de receitas: preparação antes do primeiro comando, conclusão antes de devolução e reconciliação quando reencontrar uma tentativa. Essa conexão não foi antecipada em R3.1. R2.2 produzirá a decisão automática estruturada, cujo JSON é preservado pelo checkpoint.

Testes simulam interrupção nas fronteiras de gravação; não qualificam perda real de energia, filesystem remoto ou comandos físicos. Não há liberação autônoma de equipamento; R6.2 permanece pendente. O objetivo completo continua ativo, com R2.1 como próxima etapa.
