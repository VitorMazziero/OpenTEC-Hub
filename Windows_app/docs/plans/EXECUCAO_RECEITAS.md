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
| R4.2 | Em implementação | [catálogo](receitas-r42/CATALOGO_E_CONFIGURACAO.md), [topologia](receitas-r42/TOPOLOGIA_PERIODICA.md), [executor](receitas-r42/EXECUTOR_E_GRUPO_DO_GRAFO.md), [perfis e construtor](receitas-r42/PERFIS_E_CONSTRUTOR.md); [provedor concreto](receitas-r42/PROVEDOR_CONCRETO.md); [armazenamento e seleção](receitas-r42/ARMAZENAMENTO_E_SELECAO_PERFIS.md); [resultados automáticos](receitas-r42/RESULTADOS_AUTOMATICOS.md); [navegação](receitas-r42/NAVEGACAO_SESSOES.md); [ligação ao aplicativo](receitas-r42/LIGACAO_APLICATIVO.md); [progresso ao vivo](receitas-r42/PROGRESSO_AO_VIVO.md); [barreira de pausa](receitas-r42/BARREIRA_PAUSA.md) e [decisão durável](receitas-r42/DECISAO_PAUSA.md); [cancelamento da preparação](receitas-r42/CANCELAMENTO_PREPARACAO.md); [pausa na matriz](receitas-r42/PAUSA_MATRIZ.md); [pausa no engine/provedor](receitas-r42/PAUSA_ENGINE_E_PROVEDOR.md) e [motivos de repetição](receitas-r42/MOTIVOS_REPETICAO_EDITOR.md); 2352 testes na regressão completa e Release sem erros; verificação visual dos modos restantes pendente |
| R5.1 | Em implementação | [trajetória](receitas-r51/TRAJETORIA.md), [tempo ativo](receitas-r51/TEMPO_ATIVO.md), [referência da cascata](receitas-r51/REFERENCIA_CASCATA.md), [destinos diretos](receitas-r51/DESTINOS_DIRETOS.md), [cadência](receitas-r51/EXECUTOR_CADENCIA.md), [reserva e suspensão](receitas-r51/RESERVA_E_SUSPENSAO.md), [destino protegido](receitas-r51/DESTINO_PROTEGIDO.md) e [despacho no engine](receitas-r51/DESPACHO_ENGINE.md); 2367 testes na regressão completa; ciclo de vida do bloco e confirmação específica ainda pendentes |
| R5.2 | Em implementação | [bloco e configuração](receitas-r52/BLOCO_E_CONFIGURACAO.md), [captura inicial](receitas-r52/CAPTURA_INICIAL.md), [confirmação da cascata](receitas-r52/CONFIRMACAO_CASCATA.md), [motor](receitas-r52/CONFIRMACAO_MOTOR.md), [temperatura](receitas-r52/CONFIRMACAO_TEMPERATURA.md) e [vazão](receitas-r52/CONFIRMACAO_VAZAO.md); registros inicial/terminal duráveis e componentes de cascata/motor/temperatura/vazão disponíveis; 2395 testes na regressão completa; execução bloqueada até integrar ciclo de vida, consumo dos recibos, cancelamento e destinos diretos restantes |
| R6.1 | Pendente | Regressão final, exemplos e UI |
| R6.2 | Pendente — exige bancada | Retorno físico e habilitação por instalação/protocolo |

Incrementos de R5.2: [captura inicial durável](receitas-r52/GRAVACAO_INICIAL.md) e [resultado terminal durável](receitas-r52/GRAVACAO_TERMINAL.md). Os armazenamentos estão implementados; consumo dos recibos pelo ciclo de vida do bloco e confirmação dos destinos permanecem pendentes. A revisão do editor está descrita em [edição guiada](receitas-r42/EDICAO_GUIADA.md).

Contagens não se somam: incluem testes repetidos. Aprovação de software não habilita protocolos físicos pendentes. Nenhuma etapa posterior é dada como concluída apenas porque seus contratos já existem.
