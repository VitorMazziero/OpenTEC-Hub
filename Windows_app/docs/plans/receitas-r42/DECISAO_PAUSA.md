# R4.2 — Decisão durável de interrupção por pausa

O checkpoint de seleção agora pode registrar PauseId, InvocationId, RequestId e instante da solicitação. A política verifica correspondência com a invocação e pulso cancelado, intervalo entre início e término e motivo estruturado acquisition_cancelled. Sem essa evidência, cancelamento continua Aborted. Uma interrupção por pausa nunca se torna Selected: pode autorizar Retry somente com retorno confirmado ao snapshot e recibo terminal, tentativas restantes por réplica/cultivo, tempo de bloco disponível e deadline absoluto ainda futuro. A tentativa interrompida permanece no histórico e consome os limites existentes.

O recibo fica dentro do checkpoint imutável, protegido pelo hash e confirmado contra o resultado terminal comum. A validação recalcula a decisão incluindo o recibo; remover o recibo invalida a decisão. A propriedade ausente não é serializada: os bytes usados no hash de checkpoints antigos permanecem iguais, com teste explícito contra a estrutura anterior.

22 testes focados aprovados, incluindo leitura após reabertura do armazenamento, ausência de evidência, limite de tempo/tentativas, ausência de recibo terminal, IDs/instante incompatíveis e compatibilidade de serialização.

Esta alteração não conecta a pausa ao engine. Próxima integração: emitir evidência na barreira por invocação, cancelar aquisição ativa e aguardar retorno/gravação; manter a matriz e seu deadline enquanto espera Resume. Resolver primeiro a espera pré-aquisição, que ainda pode terminar antes de existir uma corrida durável. Não converter falha de persistência ou retorno desconhecido em repetição automática.

Regressão completa: 2331 testes aprovados, zero falhas, 56 s. Compilação Release: zero erros.
