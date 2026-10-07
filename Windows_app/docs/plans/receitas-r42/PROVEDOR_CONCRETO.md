# R4.2 — Provedor concreto de ensaios autônomos

O provedor constrói uma solicitação imutável a partir do perfil qualificado e do estado comandado capturado com os produtores suspensos. A primeira reserva permanece sob sua responsabilidade até ser consumida pelo primeiro pulso; os pulsos seguintes capturam novamente as referências após a retomada do controle. Uma reserva não utilizada é devolvida ao encerrar ou aguardar intervalo.

Todos os perfis compartilham uma única API e seu diário persistido de orçamento do cultivo. A consulta do intervalo ocorre antes da reserva e novamente depois dela. Trocar de perfil não reinicia os limites acumulados. O roteador verifica a qualificação concreta e recusa alteração de evidência ou da configuração científica congelada.

A integração usa o runner, dados brutos, análise e armazenamento comuns, tanto para ensaio único quanto matriz e execução periódica. Os testes exercitam protocolos abiótico e biótico, referências atualizadas entre pulsos, saída da cascata, pausa e emergência. São ensaios de software com transporte isolado, sem qualificação física.

Pendências de R4.2: armazenamento e seleção dos perfis, configuração no editor, apresentação dos resultados, ligação ao aplicativo e tratamento da pausa no bloco independente. A operação física permanece indisponível até a qualificação correspondente.

Evidência final: `evidence/recipes-r42-provider-full.trx`.
Regressão completa: 2265 testes aprovados, zero falhas. Inclui os testes de concorrência do salvamento que reproduzem o erro da captura de tela.
