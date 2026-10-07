# Execução — receitas autônomas kLa, periodicidade e rampas

Plano: [14 etapas](2026-10-07-receitas-kla-autonomo-e-rampas.md). Atualização: 07/10/2026.

Requisitos de entrega confirmados pelo autor: preservar suas correções do aplicativo; realizar commits separados por escopo; enviar os commits validados ao GitHub em `main`; resolver conflitos antes da entrega; compilar o aplicativo em Release ao final. Evidências e duplicatas de sincronização não relacionadas continuam fora dos commits desta implementação.

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
| R4.1 | Implementada e validada em software | [agenda e grupo](receitas-r41/AGENDA_E_GRUPO.md), [vínculo à cascata e diário](receitas-r41/INTEGRACAO_CASCATA_E_DIARIO.md); 2172 testes, runner comum em saída/pausa/emergência; registro dos blocos no aplicativo pertence a R4.2 |
| R4.2 | Em implementação | [catálogo](receitas-r42/CATALOGO_E_CONFIGURACAO.md), [topologia](receitas-r42/TOPOLOGIA_PERIODICA.md), [executor](receitas-r42/EXECUTOR_E_GRUPO_DO_GRAFO.md), [perfis e construtor](receitas-r42/PERFIS_E_CONSTRUTOR.md); [provedor concreto](receitas-r42/PROVEDOR_CONCRETO.md); [armazenamento e seleção](receitas-r42/ARMAZENAMENTO_E_SELECAO_PERFIS.md); [resultados automáticos](receitas-r42/RESULTADOS_AUTOMATICOS.md); [navegação](receitas-r42/NAVEGACAO_SESSOES.md); 2308 testes na regressão completa, Release compilado sem erros; progresso, ligação ao aplicativo, pausa independente e verificação visual pendentes |
| R5.1/R5.2 | Pendente | Rampas e destinos no controle |
| R6.1 | Pendente | Regressão final, exemplos e UI |
| R6.2 | Pendente — exige bancada | Retorno físico e habilitação por instalação/protocolo |

Contagens não se somam: incluem testes repetidos. Aprovação de software não habilita protocolos físicos pendentes. Nenhuma etapa posterior é dada como concluída apenas porque seus contratos já existem.
