# Encerramento da cascata com rampas associadas

Cada invocação de rampa registra seu ciclo de vida no engine antes da preparação. O vínculo usa a identificação da cascata da configuração, sem interferir em rampas de outros destinos.

Ao sair do loop ou receber cancelamento, a cascata encerra seu grupo periódico e solicita interrupção das rampas associadas. Aguarda o término dos respectivos ciclos antes de remover o controlador, o produtor de recursos e o gate. Durante essa espera, os destinos de recuperação ainda podem validar e restaurar o controlador suspenso. Novas invocações para a cascata em encerramento são recusadas.

O retorno continua usando o cancelamento independente e o prazo definido anteriormente. O registro do ciclo só é removido depois da gravação terminal ou da falha, da liberação do destino e do encerramento do produtor da rampa. A próxima execução limpa as marcações de cascatas encerradas.

Dois testes integrados executam uma rampa mista de temperatura e referência de O₂ junto ao loop da cascata: saída normal por condição manual e parada da receita. Ambos aguardam o comando real de restauração, verificam ausência de registro terminal antes do feedback e então confirmam a recuperação durável do controlador e da temperatura. Os testes exercitam o runner e o engine, mas ainda não cobrem uma receita completa com rampa em ramo paralelo e ensaio kLa concorrente.

A composição do aplicativo, os exemplos e a matriz concorrente completa permanecem pendentes, assim como a qualificação física.
