# R5 — Produtor de recursos da rampa

RecipeRampResourceProducer implementa o contrato utilizado pelo coordenador de ensaios. A suspensão congela o relógio ativo e fecha a barreira de despacho, aguardando os comandos em andamento antes de permitir captura e cessão. O destino deve manter a lease durante amostragem e despacho do quadro completo.

Cancelamento da reserva desfaz apenas sua própria suspensão. Pausa de receita sobreposta continua ativa; retorno antigo não abre uma reserva nova; parada mantém relógio e despacho encerrados. Recursos são copiados para impedir alteração posterior da lista declarada.

Validação: 24 testes focados de rampas/barreira/coordenação aprovados, incluindo dois novos cenários de suspensão, cancelamento, retorno antigo e parada. Isto qualifica a infraestrutura de software, não confirma recuperação física.

Integração ainda pendente: registrar o produtor no engine, adquirir a lease no destino real, capturar referências confirmadas e persistir execução/retorno. O bloco continua impedido de iniciar até essas garantias estarem completas.
