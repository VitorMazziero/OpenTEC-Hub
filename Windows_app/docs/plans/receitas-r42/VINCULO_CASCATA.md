# R4.2 — validação do vínculo à cascata

A validação do grafo rejeita periodicidade vinculada a identificador inexistente, bloco de outro tipo ou cascata inacessível a partir do Início. A configuração sem vínculo permanece válida como rascunho; isso não habilita sua execução. A integração operacional dos blocos continua bloqueada até existir um provedor qualificado.

Cinco cenários verificam referências ausentes, tipo incorreto, cascata inacessível, cascata acessível e agenda sem vínculo. Os testes focados de configuração, domínio de receitas e concorrência de salvamento passaram: 61 aprovados, zero falhas ou ignorados. Evidência: `evidence/recipes-r42-bindings-settings.trx`.

O erro da imagem já foi corrigido em `b83bd07`; os testes de salvamento foram repetidos nesta validação. Não foi realizada uma nova validação visual do executável instalado.

Próximas entregas, em ordem:

1. Definir e validar o alvo único da agenda e seu fluxo paralelo, impedindo execução adicional pelo percurso comum e vínculos sem coexistência com a cascata.
2. Integrar perfis qualificados e seleção da cascata no editor; montar requests com captura fresca após suspensão dos produtores.
3. Conectar execução única/matriz e agenda ao engine e ao aplicativo, mantendo recuperação e persistência antes de liberar recursos.
4. Apresentar progresso, qualidade independente de kLa/OUR, tentativas recusadas, recuperação e navegação aos resultados comuns.
5. Validar o grafo integrado e a interface antes de avançar às rampas R5.1/R5.2. R6.2 continua exigindo bancada.

R4.2 permanece em implementação. Estes testes não comprovam operação física autônoma.
