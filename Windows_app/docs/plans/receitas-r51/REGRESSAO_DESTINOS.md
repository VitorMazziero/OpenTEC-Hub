# R5 — Regressão de destinos e compatibilidade

Regressão completa final: **2367 aprovados, zero falhas, zero ignorados**, em 1 min 24 s. Evidência: [TRX final](evidence/recipes-r51-guarded-engine-verified-full.trx).

Comando: `dotnet test Windows_app/tests/OpenTECHub.Tests/OpenTECHub.Tests.csproj --no-restore -p:EnableSourceLink=false -p:SelfContained=false -v quiet --logger "trx;LogFileName=recipes-r51-guarded-engine-verified-full.trx" --results-directory Windows_app/docs/plans/receitas-r51/evidence`.

Release final: `dotnet build Windows_app/src/OpenTECHub/OpenTECHub.csproj -c Release -p:EnableSourceLink=false --no-restore -v quiet`; zero erros, 1397 avisos de compilação/análise, 15,74 s. Os avisos não foram tratados como aprovação de operação física.

A primeira regressão detectou duas pendências do catálogo de rampa: descrição ausente no manual do aplicativo e teste antigo que classificava o novo tipo reconhecido como desconhecido. O manual agora descreve campos, tempo ativo, destino de O₂ e a execução ainda bloqueada. O teste de rejeição conserva sua finalidade com tipo futuro `LinearSetpointRampV2`; importação e roundtrip do tipo implementado são cobertos pelos testes do bloco.

No teste do despacho misto, o primeiro quadro da cascata é drenado antes de contar comandos da rampa. A validação confirma separação de referência do controlador/monitor, recusa de todo o quadro sob cessão ou tomada manual, ausência de atualização local quando o árbitro recusa o comando direto e manutenção do envelope físico de referência apesar do máximo de vazão salvo maior.

R5 ainda em implementação: execução do bloco, captura inicial confirmada, persistência, política de cancelamento e confirmações finais de cada destino permanecem necessárias. Esta evidência não habilita operação física nem encerra R6.2.
