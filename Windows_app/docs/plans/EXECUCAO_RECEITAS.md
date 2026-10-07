# Execução — receitas autônomas kLa, periodicidade e rampas

Plano: [14 etapas](2026-10-07-receitas-kla-autonomo-e-rampas.md). Atualização: 07/10/2026.

| Etapa | Estado | Evidência |
|---|---|---|
| R0.1 | Concluída para base de software | `be213dc`; [recibo](receitas-r01/EXECUCAO_R01.md); 1959 testes aprovados |
| R0.2 | Implementada e validada | [recibo](receitas-r02/EXECUCAO_R02.md); 61 testes focados aprovados |
| R1.1 | Implementada e validada | [recibo](receitas-r11/EXECUCAO_R11.md); 58 testes focados aprovados |
| R1.2 | Implementada e validada | [recibo](receitas-r12/EXECUCAO_R12.md); suspensão integrada ao engine/PID; 1996 testes na regressão completa |
| R1.3 | Implementada e validada em software | [recibo](receitas-r13/EXECUCAO_R13.md); captura completa, recuperação reservada e prioridade de segurança; 2058 testes na regressão completa |
| R3.1 | Infraestrutura implementada e validada em software | [recibo](receitas-r31/EXECUCAO_R31.md); barreira, checkpoints, reconciliação E6 e devolução verificada; 2077 testes; conexão ao runner em R2.1 |
| R2.1 | Implementada e validada em software | [recibo](receitas-r21/EXECUCAO_R21.md); adaptador comum dos dois protocolos, recuperação independente e recibo antes da devolução; 2093 testes na regressão completa |
| R2.2 | Implementada e validada em software | [execução da matriz](receitas-r22/EXECUCAO_R22.md); 24 cenários integrados e 2147 testes na regressão completa; espera com controle devolvido, recaptura, decisão e resultado duráveis |
| R4.1/R4.2 | Pendente | Agenda, grupo paralelo, blocos/editor/resultados |
| R5.1/R5.2 | Pendente | Rampas e destinos no controle |
| R6.1 | Pendente | Regressão final, exemplos e UI |
| R6.2 | Pendente — exige bancada | Retorno físico e habilitação por instalação/protocolo |

Contagens não se somam: incluem testes repetidos. Aprovação de software não habilita protocolos físicos pendentes. Nenhuma etapa posterior é dada como concluída apenas porque seus contratos já existem.
