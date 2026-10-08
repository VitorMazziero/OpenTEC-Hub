# R4.2 — Escolha dos motivos de repetição no editor

O editor permite usar os motivos herdados do perfil qualificado ou selecionar janela insuficiente, ruído excessivo e condição instável. Os campos próprios aparecem quando a herança é desativada. A guarda de visibilidade agora lê booleanos JSON além das opções de texto, preservando as guardas existentes.

A configuração aplica somente a seleção ativa, valida tipos booleanos e exige que todos os motivos escolhidos estejam autorizados pelo perfil. Desativar a herança e deixar todos os motivos desmarcados não injeta motivos do perfil no request: nenhuma repetição científica é implicitamente autorizada. A repetição por pausa exige seu recibo operacional próprio, retorno e gravação, e continua consumindo os mesmos limites por réplica/cultivo/bloco.

Receitas antigas sem as opções novas mantêm a herança de perfil. O teste compara requests congelados antes/depois de remover os campos novos e verifica a igualdade da serialização. Outros testes cobrem os três motivos, seleção vazia explícita, motivo fora do perfil e atualização de visibilidade ao alternar o editor.

O diagnóstico inicial confundiu a validação do total de tentativas com exigência de motivos: o contrato permite lista vazia; uma configuração de duas tentativas por réplica também precisa de orçamento total suficiente. A implementação conserva esse contrato.

Validação final conjunta: 2352 testes aprovados, zero falhas; Release sem erros. A evidência da regressão está em evidence/recipes-r42-engine-pause-final-full.trx.
