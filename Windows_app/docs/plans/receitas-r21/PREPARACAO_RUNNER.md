# R2.1 — preparação antes da primeira atuação

O runner comum oferece callback assíncrono `beforeActuation`, após criar a corrida e seu diretório, antes de adquirir/comandar atuadores. O adaptador de receita usará esse ponto para persistir o checkpoint R3.1 com os IDs reais da corrida.

Enquanto o callback aguarda, telemetria atualiza leituras, mas não aciona fases; watchdog e eventos de desconexão também não iniciam transições. Troca de sessão e edição de configurações são recusadas. Ao retornar, o runner verifica cancelamento, armazenamento, conexão, leitura nova, calibração, modalidade de início e autoridade, e repete pré-voo biótico. Falha encerra a preparação sem emitir comandos de aquisição.

81 testes direcionados aprovados, incluindo espera sem comandos, falha de checkpoint, leitura inválida durante espera, bloqueio de edição e início normal após confirmação. Evidência: `evidence/recipes-r21-preparation-final.trx`. Fluxo legado sem callback permanece coberto pelos testes existentes.

Entrega parcial de R2.1: falta implementar o executor completo, recuperação, análise, persistência final, cancelamento/deadlines e registro por capacidades. Não habilita receitas autônomas nem atuação física.
