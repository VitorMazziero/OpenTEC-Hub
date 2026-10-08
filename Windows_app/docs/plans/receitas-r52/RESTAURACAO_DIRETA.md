# R5.2 — Recuperação dos destinos diretos

O quadro de retorno é construído a partir das referências anteriores e configurações capturadas. Exige a mesma execução, bloco, proprietário e recursos da reserva. Compara configurações desejadas com as aceitas pelo transporte, incluindo valores textuais, e rejeita comandos fora do escopo do atuador.

As referências são quantizadas pela rota capturada. O retorno preserva os ajustes de temperatura, motor, gás, pH e pressão presentes no estado aceito. Não repete ações pontuais do banho (`bathSync`, `bathAbort`, `bathCascadeReset`); a atualização exata de temperatura é aplicada apenas como opção de envio. O canal de confirmação de pH precisa usar a banda capturada.

O engine recusa execução/autoridade inválida, troca de rota e conflito de comandos diretos N/Q com cascata ativa. O destino envia um quadro completo e passa as referências anteriores aos mesmos canais de confirmação usados pela rampa. Aceitação do envio não encerra a recuperação: o chamador mantém a reserva, espera feedback, drena os comandos e registra a evidência terminal.

11 testes focados e 2438 testes na regressão completa passaram. Os novos cenários verificam retorno conjunto de temperatura, motor e vazão, preservação de ajuste de gás, confirmação das referências anteriores, modo textual do banho, exclusão de ações pontuais, configuração sem aceite e reserva de outra execução. Evidência: `evidence/recipes-r52-direct-restoration-full.trx`.

O aplicativo compilou em Release com zero erros, em `D:/Temp/OpenTECHub-direct-restoration-release/`.

A recuperação coordenada da cascata e dos comandos diretos no mesmo bloco, o cancelamento do ciclo completo e a habilitação no engine permanecem pendentes. Evidência de software não qualifica o retorno físico.
