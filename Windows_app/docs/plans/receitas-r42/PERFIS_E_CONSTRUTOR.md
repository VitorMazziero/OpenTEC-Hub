# R4.2 — perfis operacionais e construção da invocação

`KlaRecipeOperationalProfileRegistry` registra perfis explícitos por instalação, protocolo, identificador e versão. O perfil inclui evidência, validade, confirmações prévias da montagem, definição científica, qualidade, limites de repetição/exposição, reserva e retorno. O registro é restrito ao ambiente isolado; não libera atuação física. Uma versão já registrada admite repetição idêntica e rejeita conteúdo diferente. Registro, resolução e listagem retornam cópias independentes.

A resolução não usa fallback entre protocolos, versões ou instalações. Perfis vencidos deixam de aparecer na lista e são recusados ao resolver. A receita pode reduzir os limites qualificados, mas não aumentar tentativas, exposição ou duração, reduzir o intervalo mínimo, acrescentar motivos de repetição não qualificados ou impor um orçamento menor que a exposição do protocolo congelado. Montagem biótica exige isolamento confirmado e critérios completos de proteção/recuperação; a faixa de retorno não pode ficar abaixo do OD mínimo do protocolo.

`KlaRecipeRequestBuilder` constrói a definição e os contratos a partir do perfil e de um snapshot coordenado. Confere comandos de retorno completos e identidade da execução. Condição atual usa as referências desejadas de N/Q, não as observações medidas; condição explícita e matriz usam somente suas entradas ativas. IDs das condições derivam da invocação e posição na matriz, preservando identidade ao reconstruir a mesma solicitação. Cada nova invocação deve receber sua própria identidade.

O construtor mantém perfil/versão e critérios científicos, aplica os motivos de repetição qualificados, preserva uma exigência de OUR do perfil mesmo quando o bloco não a solicita, limita aquisição pela validade do perfil e sempre usa o snapshot como destino de recuperação. Recusa mistura de cultivo quando o contexto do perfil identifica outro cultivo. As solicitações são congeladas pelo serializer comum e não enviam comandos.

O provedor deverá manter a primeira reserva, capturada após suspensão dos produtores, até preparar o primeiro pulso. Liberar essa reserva para depois recapturar permitiria que a cascata alterasse N/Q entre a definição da condição atual e o início da corrida. Pulsos seguintes continuam usando a recaptura de R2.2; a definição da matriz permanece congelada.

## Verificação e próximos passos

Os testes cobrem qualificação e expiração, instalação/ambiente/protocolo incorretos, conflito de versão, limites da receita, proteção biótica, parâmetros legados concorrentes e seis combinações de protocolo/modo. O construtor foi exercitado com snapshots de reservas reais do coordenador de software, incluindo referências N/Q diferentes das observações medidas. Verifica IDs estáveis, retorno preservado, ausência de comandos, cultivo/execução incorretos, prazo de validade e OUR obrigatório.

Regressão completa: **2253 aprovados, zero falhas ou ignorados**, 57 s. Evidência final: `evidence/recipes-r42-profile-builder-full.trx`. A chamada usa `SelfContained=false`, `EnableSourceLink=false`, sem restauração de dependências, devido à falha de acesso do OneDrive aos metadados gerados de SourceLink. Essa opção não altera a configuração do produto.

R4.2 permanece em implementação. Ainda necessários: provedor concreto com a primeira reserva preservada; router/API comuns suportando os perfis disponíveis sem dividir orçamento do cultivo por perfil; armazenamento/seleção de perfis e cascatas no editor; registro do provedor no aplicativo; progresso/resultados/CSV e validação visual. R5 e R6 continuam na sequência aprovada.
