# Correção do erro observado durante o teste

O relatório `crash_20261007_100646_858a42.log` aponta `SettingsService.ScheduleSave`, linha 199: a tarefa de debounce lia `CancellationTokenSource.Token` após outra atualização descartar a fonte. A exceção escapava da tarefa e chegava a `TaskScheduler.UnobservedTaskException`.

A tarefa agora recebe o token antes da possibilidade de cancelamento, registra o atraso imediatamente e mantém a responsabilidade de descartar sua própria fonte ao terminar. Atualização/cancelamento usam a mesma trava; falhas inesperadas da tarefa são registradas. Salvamentos compartilham uma fila exclusiva, capturam as configurações atuais após adquirir essa fila e usam arquivo temporário exclusivo. A substituição admite até cinco tentativas, com intervalo cancelável de 20 ms, para bloqueios transitórios do Windows. Arquivos temporários são removidos ao terminar.

Testes adicionados cobrem 200 alterações sucessivas e 100 alterações concorrentes com salvamento imediato e encerramento. A primeira regressão completa identificou o bloqueio transitório de substituição; sua evidência foi preservada e levou à repetição limitada. A correção não altera controles físicos ou contratos do ensaio.

R3.1 foi retomada: barreira durável com falhas acumuladas por sessão e checkpoints antes da atuação/finais associados ao request, reserva, snapshot e corrida comum. Os testes verificam idempotência, reabertura, conflito de payload, dados brutos ausentes e adulterados. Reconciliação completa com E6 e consumo do recibo na devolução permanecem pendentes; a etapa não está concluída nem habilita operação física autônoma.

Validação final: executar o projeto completo `OpenTECHub.Tests`, com `SelfContained=false`, sem restaurar dependências. Resultado em `evidence/recipes-r31-regression-final.trx`.

Resultado final: 2066 testes aprovados, zero falhas, 37 s. A contagem inclui a base de R3.1 em andamento; não representa aceite completo dessa etapa.
