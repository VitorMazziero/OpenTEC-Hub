# R1.3 — captura e recuperação do estado anterior

Data: 07/10/2026. Base: `34b86eb`; alterações entregues no commit que contém este recibo. Ambiente: testes locais de software, sem atuação de bancada.

O arbiter mantém comandos desejados completos por atuador e registra separadamente os comandos aceitos pelo transporte. A captura exige reserva atual e drenagem confirmada; não substitui referência ausente por leitura instantânea. Snapshot inclui rota/modo, N/Q, configuração de gás, referências auxiliares e estado auditável do PID/alocação preservados na instância suspensa. Campos opcionais novos não mudam hashes de contratos antigos quando ausentes.

O runner comum recebe uma autoridade reservada de receita, sem tomar posse por Claim/Release comum. Uma barreira sela a aquisição antes da recuperação e impede comandos tardios do runner. Metadados de retorno usam referências do snapshot nos dois protocolos. O fluxo manual permanece separado.

`RecipeAssayRestoration` restaura gás/configurações e ordena parada, rota e referência do motor. Aguarda amostras novas, ACK de gás posterior ao início da recuperação, rota confirmada, leitura de velocidade/vazão e estabilidade configurada; no biótico os critérios de OD devem ser fornecidos pelo perfil. Configurações com eco são verificadas; maxFlow e referência de monitoramento de OD permanecem explicitamente como aceitação de transporte. Ausência de leitura/eco necessário produz falha, não confirmação fictícia. Retorno a OFF/zero e rotas alternativas são cobertos.

Emergência revoga autoridades e interrompe produtores uma única vez. A fila prioritária de segurança descarta comandos antigos de atuação, preserva a ordem de parada/desativação e impede replay de um frame em voo que falhou. Uma escrita já submetida ao transporte termina antes da parada; não é possível desfazer bytes já enviados nem cancelar a fila interna do equipamento por esse mecanismo. Comprovação física permanece em R6.2.

Recuperação usa prazo próprio, independente da aquisição. A evidência de retorno é vinculada ao snapshot e exigida na devolução de uma reserva capturada. Ela não equivale a recibo durável: produção ainda depende de R3.1, adaptador R2.1 e encerramento aguardável do grupo R4.1. Os recibos usados nos testes de devolução são explicitamente controlados pelo fixture, não emitidos pelo armazenamento de produção.

## Verificação

- Recorte final: **211 aprovados**, zero falhas/ignorados; [TRX](evidence/recipes-r13-focused-pass.trx).
- Regressão completa: **2058 aprovados**, zero falhas/ignorados; [TRX](evidence/recipes-r13-full.trx).
- Comando completo: `dotnet test Windows_app/tests/OpenTECHub.Tests/OpenTECHub.Tests.csproj -p:SelfContained=false --no-restore -v quiet --logger "trx;LogFileName=recipes-r13-full.trx" --results-directory Windows_app/docs/plans/receitas-r13/evidence`.
- Matriz do runner: Abiótico/Biótico, saída por cancelamento/falha/revisão nas fases de preparação, remoção, aquisição e retorno; restauração sob reserva sem janela Manual. Teste adicional usa cascata real do engine e verifica estado preservado.
- Falhas: ACK/rota/velocidade/gain ausentes ou incorretos, intervalos de telemetria excessivos, ausência de OD novo, prazo, desconexão, emergência, snapshot alterado e resultado de retorno fabricado.
- Os TRX intermediários registram duas correções de desenvolvimento: parada duplicada do produtor após revogação e fixture da cascata sem configuração anterior de maxFlow. O resultado final acima usa ambas corrigidas.

Não habilita execução física autônoma. R3.1 é a próxima etapa; R6.2 permanece pendente.
