# Receitas: determinação autônoma de kLa e rampas lineares

Data: 07/10/2026. Segunda revisão após chegada de E5/E6/E7. Base inspecionada: `main`, HEAD `237c254`, com base E5/E6/E7 consolidada e entregas R0.2/R1.1/R1.2. Há imagens/evidências locais modificadas ou não rastreadas, inclusive duplicatas de sincronização; não as incorporar nem excluir automaticamente.
Estado (08/10/2026): R0.1–R6.1 entregues e auditadas em software conforme [registro de execução](EXECUCAO_RECEITAS.md) e [auditoria final](AUDITORIA_FINAL_RECEITAS.md), com ressalvas A-04/A-05/A-06/A-07/A-08. R6.2 depende de bancada. Próximas etapas: [plano de finalização](PLANO_FINALIZACAO.md). Esta revisão não libera atuação ou qualificação de bancada. Recibos E7 de outras revisões não identificam automaticamente este conjunto de fontes. Os diagnósticos abaixo preservam a auditoria original; consultar os recibos para as correções posteriores.

## 1. Situação atual e escopo

Entrega adicional solicitada pelo autor: commits separados por escopo, push para GitHub em `main`, resolução de conflitos antes de finalizar e compilação Release final. Preservar as correções concomitantes do aplicativo e registrar sua reconciliação em pacote próprio quando necessário. Essas obrigações acompanham as 14 etapas e não substituem os critérios de qualificação física.

Há uma base reutilizável independente da tela: `IKlaTestRunner`, `IKlaTestStore`, `IKlaDeterministicAnalysisEngine` e definições de ensaio que separam protocolo (`Abiotic`/`Biotic`) de captura (`Single`/`Multiple`). Ambos os modos usam a mesma unidade de corrida. O modo único exige uma condição e uma réplica planejada.

E5 agora fornece fila de condições/réplicas, contadores e limites em `KlaSequence`; E6 fornece `IKlaAssayApi`/`KlaAssayApi`, diário persistente, idempotência, limites cumulativos e recuperação de solicitações interrompidas. A API E6 aceita somente uma condição/uma réplica por solicitação: múltiplos exigem orquestração acima dela. `IKlaAssayExecution` ainda não tem adaptador de produção registrado. O runner chega a `Reviewing`, expõe aceitar/rejeitar/repetir, e `KlaAssayCoordinator.Validate` rejeita posse `Recipe`. Não existe `KlaAssay` no catálogo ou no executor de receitas.

Outros pontos relevantes verificados:

- A receita assume todos os atuadores; seus ramos podem executar em paralelo. O proprietário `Recipe` sozinho não distingue dois blocos concorrentes.
- Na implementação atual, `Saída Loop` de `CascadeControl` é somente uma condição de encerramento (Monitorar Variável, Temporizador ou Intervenção Manual). O validador rejeita outros blocos. O temporizador existente encerraria a cascata após 2 h; não agenda kLa nem repete o ensaio.
- `Pause` atua entre blocos; não interrompe automaticamente o trabalho em andamento.
- `SafeStopAndRelease` atualmente libera a posse; seu nome não garante restauração física ou envio de comandos de recuperação.
- O armazenamento de kLa já inclui dados brutos, eventos, análises revisionadas e resumo. As escritas são enfileiradas; existem `FlushAsync` e `WriteFailed`.
- Qualidade de kLa, qualidade de OUR, decisão do operador e restauração já são dimensões separadas. A decisão automática precisará de autoria própria.
- O catálogo de receitas admite listas e campos condicionais por `VisibleWhen`. Não há bloco de rampa.
- O setpoint de O₂ de `SetSetpoint` escreve `OxygenMonitor`; isso não altera, por si só, a referência do controlador da cascata.

Este plano integra E5/E6 com três blocos: Determinar kLa, Periodicidade e Rampa linear, preservando runner, núcleo científico e armazenamento comuns. A API é interna ao aplicativo; servidor HTTP não é requisito.

### 1.1 Auditoria inicial da revisão E5/E6/E7 — histórico

A tabela abaixo documenta a inspeção em `c51253f`. Não representa todos os problemas ainda abertos em `237c254`; a reconciliação atual está na seção 1.2.

Prioridades abaixo referem-se à habilitação autônoma. São incompatibilidades verificadas com este plano, não evidência de atuação insegura já habilitada: o caminho de receitas continua fechado.

| Prioridade | Achado e evidência no código atual | Alteração necessária |
|---|---|---|
| Bloqueante | E6 não possui adaptador de produção; `KlaAssayApiContracts.cs` restringe request a `Single`; `IsValidated` impede partida não qualificada | Adaptador sobre o runner comum e orquestração de matriz acima da API; qualificação explícita por protocolo/instalação |
| Bloqueante | `KlaAssayApi.ExecuteAsync` aceita retorno abiótico `NotRequired`; `KlaTestRunner` libera revisão/terminal abiótico com `Release(true, 0, 0, ...)` | Restaurar snapshot anterior também no abiótico. Encerrar com gás fechado e posse manual não atende ao retorno exigido pela receita |
| Bloqueante | `RecipeEngine.Cascade` calcula PID e despacha comandos sem barreira de cessão; o gate novo está isolado, sem integração | Reserva por bloco, pausa confirmada antes da transferência, exclusão de comandos concorrentes e retomada confirmada; não basta evento de início/fim |
| Alta | `KlaSequence.IsAccepted`, revisão da fila, contadores e importação dependem de `OperatorDecision` | Decisão com autoria automática e política versionada; adaptar contagem/seleção sem simular aprovação humana |
| Alta | E6 marca qualidade `Conditional` como `Completed`; `MayContinueRecipe` pode liberar continuação, enquanto R0 exige política explícita | Separar término do pulso de aceitação da réplica e autorização de avanço; aplicar `Valid` por padrão e códigos condicionais permitidos |
| Alta | E6 `KlaPeriodicSchedule.Latest` usa UTC e oferece o último slot vencido; R0 pede relógio monotônico e descarte de slots perdidos durante indisponibilidade | Um único agendador de receitas com semântica R0; não ligar o helper E6 diretamente ao bloco |
| Alta | `BackgroundFileWriter.Execute` informa erro do item por evento; um `FlushAsync` posterior pode concluir normalmente. Runner já escuta `WriteFailed`, mas isso não constitui recibo durável por tentativa | Barreira por sessão/tentativa que carregue falhas anteriores, manifesto atômico e vínculo entre diário E6 e dados brutos |
| Alta | Falha de persistência terminal em E6 é registrada em memória como `RestorationFailed` | Separar erro de disco da evidência física de retorno; ambos bloqueiam nova atuação, preservando diagnósticos distintos |
| Alta | `AtCurrentCondition` apenas congela N/Q fornecidos pelo chamador; R0 exige origem/confirmado/medido e snapshot completo | Capturar no coordenador após cessar comandos concorrentes, validar e persistir antes da perturbação |
| Integração | `RecipeEngine`/`RecipeEngine.Cascade` receberam consumo de quadros em fila e avaliação das condições de saída; só o quadro mais recente comanda | Integrar suspensão sem regredir avaliação dos quadros, debounce e saída legada; descartar histórico de controle do período de ensaio na retomada |
| Liberação | E7 mantém biótico físico bloqueado em `KlaActuationRelease`; corpus de 92 curvas foi recusado por eventos ausentes | Preservar bloqueio. Recusa correta e testes simulados não qualificam estimador em novos cultivos nem operação autônoma |

Verificação desta revisão: `dotnet test Windows_app/tests/OpenTECHub.Tests/OpenTECHub.Tests.csproj -p:SelfContained=false --no-restore --filter "FullyQualifiedName~RecipeExecutionContractTests|FullyQualifiedName~RecipeCascadeSuspensionGateTests|FullyQualifiedName~KlaAssayApiTests|FullyQualifiedName~KlaSequenceTests" -v quiet`: **55 aprovados, 0 falhas, 0 ignorados**. A compilação atual passou; os erros transitórios de campos ausentes durante a chegada dos arquivos não permanecem neste recorte. Não foi reexecutada a regressão completa E7 nesta auditoria.

Evidência E7 consultada: [execução e limites](kla-e7/EXECUCAO_E7.md). O recibo relata 397 verificações distintas em execuções diferentes, builds candidatas vinculadas a outras revisões e bancada pendente; não somar esse número aos 55 testes atuais nem apresentá-lo como aprovação integral deste checkout.

### 1.2 Reconciliação com o checkout atual e riscos restantes

Inspeção estática em `237c254`: os contratos E6 agora exigem retorno confirmado, vínculo ao snapshot e recibo para receitas; distinguem `PersistenceFailed` e exigem capacidades por instalação/protocolo. A cessão usa reservas e barreira de transporte, e a cascata já suspende Update+Dispatch e retoma somente com amostra nova. Não repetir essas implementações. A regressão completa de R1.2 registrada no recibo teve 1996 testes aprovados; esta atualização documental não reexecutou a suíte.

| Prioridade | Situação atual verificada | Execução necessária |
|---|---|---|
| Bloqueante | `KlaTestRunner` ainda chama `Release(true, 0, 0, ...)` na revisão/terminal abióticos; coordenador do runner não usa a reserva de receita | R1.3: introduzir contexto de atuação reservado no runner comum, sem alterar o fluxo manual; devolver snapshot completo, não valores medidos ou zero |
| Bloqueante | `RecipeEngine.Safety.SafeStopAndRelease` apenas libera `Recipe`; não aguarda recuperação de ensaio nem confirma parada | R1.3 define recuperação; R4.1 integra encerramento de todo o grupo. Nenhum sucesso/avanço antes da conclusão desse caminho |
| Alta | Snapshot R0 possui JSON de comandos, mas falta captura de produção do estado desejado completo, eco e medição separados | R1.3: inventário dos campos de motor/gás/controle, ledger de comandos autorizados e captura após suspensão+drenagem; ausência de evidência bloqueia modalidade que a exige |
| Alta | Revogar a reserva invalida tokens; ainda é necessário provar que comandos já enfileirados não religam saídas após emergência | R1.3: reconciliar fila de transporte e prioridade de segurança; testar corrida entre enqueue, drenagem, transferência e emergência |
| Alta | Recibo exigido no contrato ainda não é produzido pelo armazenamento de tentativas; `WriteFailed` não equivale a barreira durável | R3.1: recibos reais, falhas acumuladas, persistência pré-atuação e reconciliação após queda |
| Alta | Não há adaptador de produção E6 sobre o runner nem orquestração automática completa da matriz | R2.1/R2.2: serviço sem diálogos, decisão automática com autoria própria e orçamento persistido |
| Alta | Fim da cascata durante suspensão é observado, mas ainda falta vínculo ao cancelamento/recuperação do ensaio paralelo | R4.1: grupo com encerramento aguardável, recuperação independente do token cancelado e agenda monotônica |
| Integração | Catálogo/executor/editor de kLa, periodicidade e rampas continuam pendentes | R4.2/R5: adicionar somente após serviços e caminhos de falha comprovados |
| Liberação | Evidência simulada não confirma retorno físico; bloqueio biótico E7 permanece aplicável | R6.2: qualificação por instalação, protocolo e perfil; manter produção fechada sem evidência correspondente |

## 2. Decisões de produto

1. Um bloco **Determinar kLa**, com seletor **Abiótico / Biótico** e seletor **Único / Múltiplos**. A biblioteca pode oferecer dois atalhos pré-configurados, ambos serializados como `KlaAssay`.
2. Um bloco **Rampa linear de setpoints**, serializado como `LinearSetpointRamp`, com uma ou várias linhas. Cada parâmetro tem seu próprio alvo e tempo final.
3. Durante uma execução autônoma, nenhum dos novos blocos abre diálogo ou aguarda classificação humana. Todas as decisões e limites são definidos antes de iniciar a receita.
4. Padrão de kLa: aceitar automaticamente apenas qualidade `Valid`; repetir somente causas explicitamente recuperáveis; esgotamento interrompe a receita após recuperação. Continuar sem resultado é uma política opcional explícita.
5. Padrão de rampa: início no setpoint efetivo capturado ao entrar no bloco; tempo final relativo ao início desse bloco. Horário de relógio e tempo absoluto da receita ficam fora desta primeira versão.
6. Após qualquer teste, restaurar obrigatoriamente o estado operacional anterior: referências, rotas, modos, configuração dos controles e proprietários. Isso vale para Abiótico/Biótico e Único/Múltiplos, inclusive no encerramento por falha ou cancelamento quando a recuperação for fisicamente possível. Falha de restauração impede sucesso e avanço. Parada de emergência tem prioridade e impede religamento automático.

## 3. Bloco Determinar kLa

### 3.1 Configuração contextual

| Grupo | Campos e comportamento |
|---|---|
| Identificação | Rótulo do ensaio; contexto do cultivo/experimento herdado da execução; protocolo; captura |
| Condições | Único: N e Q atuais confirmados ou valores explícitos. Múltiplos: lista ordenada de N, Q e réplicas desejadas; importação opcional de condições de mapa |
| Preparação comum | Critérios de estabilidade, calibração, descrição/resposta da sonda, janelas e limites suportados pelo contrato científico |
| Abiótico | Remoção por N₂, alvo/estabilidade de OD, agitação de remoção, pré-estabilização do ar e prazos por fase |
| Biótico | Consumo por respiração com ar desviado para escape, piso de OD, queda máxima, duração máxima sem ar no reator, recuperação e intervalo entre ensaios |
| Qualidade | Perfil versionado de análise e política de aceitação; necessidade ou não de OUR válido para o objetivo do bloco |
| Repetição | Máximo de tentativas por réplica, intervalo mínimo, máximo de duração/exposição do bloco e orçamento cumulativo do cultivo |
| Falha científica | Interromper após recuperação; ou continuar com resultado indisponível e advertência registrada |
| Retorno | Estado e proprietário a restaurar; tolerâncias e prazo de confirmação; estratégia operacional aprovada para falha |

Os campos inativos não participam da execução nem da validação do protocolo selecionado. Valores editados podem ser preservados ao alternar o seletor, mas o snapshot executável inclui somente o contexto aplicável.

Condição atual significa capturar referências e evidências disponíveis na entrada do bloco. Registrar separadamente setpoint solicitado, setpoint confirmado e valor medido; não converter uma leitura instantânea em setpoint por conveniência.

Antes da partida da receita, validar a configuração de gás, pré-condições físicas exigidas, calibração, dispositivos e destino de armazenamento. Confirmações de preparação feitas pelo usuário devem ter contexto e validade definidos. Uma declaração de N₂ isolado não se transforma em sensor: quando não houver observabilidade suficiente, a modalidade autônoma correspondente não será habilitada até qualificação da instalação. Durante a receita, revalidar automaticamente tudo que for observável.

### 3.2 API de orquestração proposta

Introduzir `IKlaAssayService` como orquestrador da invocação R0 acima da API E6 existente. Cada tentativa usa um request Single E6; não duplicar diário, mecanismo de pulso ou núcleo científico. Operações equivalentes a:

- `ValidateRequest`: validação sem atuação.
- `StartOrGetAsync(request)`: cria ou recupera a mesma execução por chave idempotente.
- `Observe/GetStatus`: progresso, condição, réplica, tentativa, fase, limites e caminhos dos artefatos.
- `CancelAndRestoreAsync`: cancela aquisição e executa recuperação limitada por prazo próprio.
- `GetResult`: resultado terminal persistido e agregado por condição.

O request imutável inclui definição do ensaio, políticas de qualidade/repetição/falha, deadlines, contexto do cultivo, versão/hash da receita e IDs da execução, bloco e invocação. A identidade da invocação inclui ciclo quando houver repetição da receita. A mesma chave com payload diferente é erro; uma tentativa nova recebe ID novo ligado à mesma réplica. Repetição científica não pode ser confundida com retransmissão de comando.

R0 é o contrato externo da receita; E6 é o contrato interno de um pulso. Mapear explicitamente invocação/condição/réplica/tentativa para `RequestId`, sessão/corrida e reserva de exposição persistida. `StartAsync` chamado novamente pode retornar `Running`; o adaptador deve observar/aguardar término, nunca tratar o retorno dessa chamada como pulso concluído. Reservar uma única vez o orçamento de cada tentativa; reenvio não incrementa contadores. UTC identifica eventos e deadlines persistidos; intervalos em processo usam relógio monotônico injetável, inclusive cancelamentos temporizados testáveis.

Estados da orquestração: `Validating → AcquiringOwnership → Preparing → Running → Restoring → Analyzing → Persisting → Deciding → WaitingRetry/NextRun/Completed`. A implementação pode analisar dados em paralelo à recuperação, mas não libera a próxima corrida ou bloco antes de confirmar retorno e persistência.

Resultados terminais distinguem: concluído, concluído com ressalvas, inconclusivo, cancelado, falha operacional, falha de persistência e falha de restauração. O status do bloco não substitui as qualidades científicas de cada tentativa.

### 3.3 Posse, ramos paralelos e cancelamento

Criar reserva por execução/bloco para N, Q e rotas de gás. Fazer transferência autorizada `Recipe → KlaAssay → Recipe`, vinculada a essa reserva, sem janela intermediária de controle manual. Validar o proprietário anterior e a identidade da transferência na devolução.

Antes da cessão, suspender os produtores concorrentes desses recursos, inclusive a cascata executada dentro da receita e ramos de rampa/setpoint. Preservar referências, rotas, estado do controlador e política de retomada. Não basta permitir `Recipe` no método `Validate`: o fluxo concorrente também precisa respeitar a reserva.

Ramos independentes, como registro e controle de temperatura, podem continuar quando a validação de recursos demonstrar compatibilidade. Dois blocos conflitantes aguardam com timeout ou são rejeitados na validação; nunca escrevem simultaneamente e nunca aguardam indefinidamente. Adquirir o conjunto de recursos em ordem determinística para evitar deadlock.

Cancelamento, erro de ramo e encerramento da receita aguardam a recuperação antes de liberar a posse. O token já cancelado da receita não pode impedir essa recuperação: usar uma operação de encerramento com prazo próprio. Uma parada de emergência tem precedência sobre a restauração normal e impede reativação posterior por tarefas atrasadas.

Pausa durante kLa: concluir a transição física necessária para recuperação e estacionar antes da próxima corrida; não congelar o relógio de exposição nem deixar o cultivo sem aeração esperando `Resume`. Registrar a tentativa interrompida. A retomada depende de nova validação e da política de repetição.

### 3.4 Qualidade e repetição automática

Executar o núcleo determinístico E3 com eventos de gás confirmados e amostras qualificadas. Aplicar os critérios existentes e versionar qualquer extensão. Não substituir a avaliação por um corte isolado de R², nem alterar limiares para obter aprovação.

| Resultado | Decisão automática proposta |
|---|---|
| kLa válido e demais critérios obrigatórios atendidos | Salvar e contar a réplica como atendida |
| kLa condicional | Salvar como condicional; padrão não atende a meta. Aceitação somente por perfil explícito, com motivos permitidos |
| Inconclusivo por motivo recuperável | Salvar; restaurar; esperar recuperação e intervalo mínimo; repetir dentro dos orçamentos |
| Pré-condição persistente ausente, calibração inválida ou hipótese não sustentada | Não repetir apenas para tentar melhorar o ajuste; aplicar política de falha |
| Falha de atuação, perda de telemetria, falha de escrita ou restauração não confirmada | Bloquear novas tentativas, executar recuperação possível e terminar com falha identificada |

Motivos recuperáveis devem ser códigos estruturados, não correspondência em mensagens. A lista permitida e os limites serão definidos e testados por perfil operacional. Nenhuma falha será presumida recuperável apenas porque o número de tentativas ainda permite outra corrida.

Uma réplica é uma medição planejada; uma tentativa é uma execução para atendê-la. O modo único permanece uma condição/uma réplica, mesmo quando exige nova tentativa. As tentativas rejeitadas permanecem no histórico. Política de seleção: primeira tentativa que satisfaz o critério predefinido; nunca escolher o maior kLa ou melhor R² entre resultados.

Limites obrigatórios: tentativas por réplica, prazo total do bloco, prazo por fase, intervalo mínimo, número/exposição cumulativa de ensaios por cultivo. Persistir os contadores para que reinício ou novo bloco não zere o orçamento. O deadline de aquisição interrompe novas perturbações; a recuperação usa seu prazo reservado e continua mesmo depois desse deadline.

OUR tem qualidade própria. No abiótico, apresentar não aplicável; no biótico, mostrar unidade, hipótese e validade. OUR condicional não invalida ou valida automaticamente kLa: aplicar os requisitos explícitos do perfil. Conversão para unidade molar exige os metadados necessários.

### 3.5 Persistência automática e retomada após falha

Reutilizar a raiz e os contratos de `KlaTestStore`. Cada invocação do bloco cria uma sessão independente, identificada por IDs estáveis e rótulo legível. O registro da execução da receita referencia essa sessão; não criar uma segunda cópia autoritativa dos dados de kLa.

Salvar automaticamente:

1. Antes da atuação: request imutável, IDs, versão/hash da receita, políticas, estado de retorno e manifesto inicial, com confirmação de gravação.
2. Durante a corrida: dados brutos, telemetria contextual, transições de gás, fases, eventos e contadores de exposição.
3. Ao encerrar cada tentativa, inclusive abortada: dados selados, análise/revisão quando possível, qualidade de kLa e OUR, motivos, estado físico e decisão automática.
4. Antes de avançar: resumo atualizado, decisão de fila, estado de recuperação e recibo de persistência durável.

Adicionar autoria de decisão (`AutomaticPolicy`/`Operator`), versão da política, momento, motivos e tentativa selecionada. Não preencher `OperatorDecision.Accepted` como se uma pessoa tivesse revisado. Reanálise posterior cria nova revisão e não altera retroativamente a decisão usada pela receita.

Integrar `WriteFailed` ao resultado da operação. Verificar a semântica do escritor: esvaziar a fila com `FlushAsync` não deve ser interpretado como sucesso se uma escrita anterior falhou. Introduzir recibo/barreira de gravação por sessão/tentativa, propagar erros e usar gravação atômica nos manifestos. Falha de disco impede nova atuação; preservar os dados recuperáveis e a sinalização de execução incompleta.

Na reabertura do aplicativo, reconciliar sessões não terminadas com o diário da receita. Nunca retomar automaticamente uma fase de remoção ou emitir novo pulso por ausência de resposta. Reconsultar estado físico, preservar a tentativa interrompida e impedir repetição da mesma chave. A primeira entrega suporta recuperação de histórico e encerramento seguro; retomada integral da receita após queda do processo depende de contrato próprio e não é pressuposta.

### 3.6 Apresentação dos resultados

No bloco em execução: protocolo, condição i/n, réplica, tentativa/max, fase, tempo, motivo de espera e estado de salvamento. Depois: resultado resumido, qualidade e ação **Abrir resultados**.

Na execução da receita: tabela por sessão/condição/réplica/tentativa, com N/Q solicitados e confirmados, kLa em h⁻¹, intervalo de confiança quando disponível, OUR/unidade, qualidade, motivos, decisão e restauração. Valores ausentes aparecem como “—”, nunca como zero. Cores acompanham texto e ícones.

Abrir a sessão no visualizador comum de kLa com curvas, eventos, janela selecionada e versões. Não exigir mapa para salvar, exportar ou consultar. Exportar o resumo CSV automaticamente junto à sessão; permitir exportação posterior do pacote completo.

Múltiplas condições: apresentar resultados e réplicas por condição. Agregação usa somente tentativas selecionadas e compatíveis, informa n e dispersão, e não oculta falhas anteriores. Para biótico, preservar tempo e estado do cultivo: não agregar medições de estados distintos sem critério explícito. Atualização de mapa ou do mapa ativo da cascata fica desabilitada por padrão e exige política separada de compatibilidade.

### 3.7 Bloco de periodicidade em ramo paralelo ao Controle de O₂

Caso de referência: `Início → [Controle de O₂ em operação contínua] ∥ [Periodicidade → Determinar kLa]`. O bloco **Periodicidade** possui `Primeira execução em: 2 h` e `Repetir a cada: 4 h`. Os disparos nominais ocorrem em 2 h, 6 h, 10 h, 14 h… desde a entrada no bloco de periodicidade, inclusive o tempo gasto nos ensaios. O ramo periódico não encerra a cascata; o bloco de kLa termina uma invocação e devolve o controle ao agendador. `Saída Loop` da cascata conserva exclusivamente seu significado atual de condição de encerramento.

O agendador é um bloco reutilizável para alvos compatíveis, com uma saída `Executar` ligada ao bloco alvo e retorno explícito ao agendador, mais uma saída de término/erro para controle do fluxo. A primeira implementação aceita kLa e outros alvos somente quando houver contrato de recursos, idempotência, cancelamento e recuperação apropriado. O validador deve impedir ciclos inesperados, duas agendas que manipulem N/Q simultaneamente e ramos sem condição de encerramento coerente. A agenda tem identidade própria (`ScheduleRunId`, `SchedulerNodeId`, `TargetNodeId`, índice de slot) e pode apontar para uma cascata coordenada sem ficar fisicamente dentro dela.

O disparador usa tempo monotônico desde a entrada em **Periodicidade**, incluindo a duração dos ensaios. Concluir um teste não reinicia o período. Slots vencidos enquanto o ensaio estava ativo, a receita estava pausada ou o sistema indisponível são **pulados e registrados**, sem fila de perturbações atrasadas. Ao retomar, calcula-se o primeiro slot futuro. Antes de cada disparo, aplicar intervalo mínimo, orçamento de cultivo, pré-condições e deadlines; registrar `adiado` ou `pulado` com motivo quando não puder executar com segurança. Cada novo slot recebe uma invocação distinta.

Na hora do ensaio, o bloco periódico pede ao coordenador da receita uma cessão temporária de N/Q, rota e demais recursos necessários. O coordenador pede à cascata que suspenda; a cascata interrompe suas atualizações e comandos, confirma que nenhum passo está em andamento e devolve um recibo de pausa. **Somente após esse recibo** o coordenador transfere a posse ao ensaio e permite seu início. Um aviso direto do bloco de kLa à cascata, sem confirmação/barreira de posse, permitiria corrida entre o último comando PID e o primeiro comando do teste. Cada etapa do handoff tem ID, revisão e confirmação registrados.

Durante o ensaio, o relógio `dt` da cascata e suas memórias de erro, integral e derivada não podem incorporar o intervalo do ensaio. Guardar o controlador da receita em execução (modo, SP, ganhos, janelas, mapa/alocação, estado de controle e setpoints efetivos) e capturar o estado operacional anterior de todos os recursos afetados. Se a cascata paralela usar O₂ como referência de comando, coordenar também essa posse; nenhuma atualização concorrente pode atravessar a suspensão.

Após o teste, o coordenador restaura rotas, N/Q, referências, modos, controladores e proprietário anteriores com confirmação compatível com a observabilidade de cada dispositivo. **Somente após confirmação de restauração** reativa a mesma instância lógica da cascata e registra seu recibo de retomada. Rebasear o tempo e o histórico de derivada na amostra atual sem integrar erro durante a suspensão nem provocar um salto por `dt` acumulado. O núcleo atual de `RecipeEngine.Cascade` calcula `dt` desde o último passo, e `CascadeTwoLoopPidController` conserva janelas; isso exige uma operação explícita de suspender/retomar e testes de ausência de windup/transiente. `CascadeService.SuspendForKlaAssay` é o caminho da cascata geral e não suspende, por si só, o controlador da receita.

Enquanto a cascata espera o ensaio, outros ramos da receita seguem somente se não concorrerem pelos recursos reservados. Se a restauração falhar, registrar a falha, bloquear o próximo slot e o avanço do fluxo e preservar o estado seguro. Definir explicitamente o ciclo de vida do grupo paralelo: término/cancelamento da cascata cancela a agenda, e uma falha irrecuperável da agenda termina o grupo conforme a política da receita. `AND`/`OR` existentes não substituem esse vínculo de duração. O temporizador comum de saída da cascata mantém sua semântica legada.

## 4. Bloco Rampa linear de setpoints

### 4.1 Interface e significado do tempo

Uma tabela contém: parâmetro, origem do valor inicial (atual confirmado ou explícito), valor inicial quando aplicável, setpoint final, tempo para atingir o final e unidade de tempo. Mostrar ao configurar uma previsão da inclinação. Rejeitar linhas duplicadas para o mesmo parâmetro.

Todas as linhas começam no mesmo instante lógico de início do bloco, após reserva de recursos e validação dos valores iniciais. Cada uma termina no seu próprio prazo e mantém o alvo enquanto as demais continuam. O bloco avança depois do maior prazo e da confirmação final exigida para todas as linhas.

Exemplo: temperatura de 30 para 37 °C em 60 min e agitação de 200 para 500 rpm em 20 min. Aos 20 min, a temperatura programada é 32,33 °C e a agitação já permanece em 500 rpm; o bloco termina aos 60 min, sujeito às confirmações de comando.

Para cada linha i: `SP_i(t) = SP0_i + (SPfim_i − SP0_i) × clamp(t_ativo / T_i, 0, 1)`.

`T_i` deve ser finito e positivo. Subida, descida e alvo igual ao inicial são válidos. Uma mudança instantânea continua pertencendo ao bloco de setpoint. Valor inicial explícito diferente do atual representa um degrau inicial: mostrar isso no editor e exigir que esteja deliberadamente configurado.

### 4.2 Execução e confirmação

- Usar relógio monotônico e calcular a partir do tempo transcorrido, evitando erro acumulado por somar incrementos a cada tick.
- Cadência e resolução devem respeitar o dispositivo; enviar apenas mudanças representáveis, limitar taxa de comandos e garantir o alvo final. Não reproduzir uma fila de ticks atrasados após travamento.
- Reusar construtores, rotas, validação e arbitragem existentes. Snapshot de limites reais do equipamento e rota ativa antes do início; rejeitar trajetória inviável em vez de limitá-la silenciosamente.
- Registrar trajetória ideal, comandos efetivamente enviados/aceitos e confirmações disponíveis. Aceitação de transporte não prova resposta física.
- Não aguardar que a variável medida alcance cada setpoint intermediário. Por padrão, conclusão significa programação do alvo e confirmação suportada; estabilização física adicional é uma opção com tolerância, janela e timeout próprios.
- Onde não houver eco confiável, declarar o nível de confirmação disponível. Não oferecer a opção de estabilização física sem leitura adequada.
- Falha de comunicação/posse interrompe a rampa e ativa a política de encerramento. Reconexão não envia diretamente um alvo vencido sem reconciliação.
- Persistir valores iniciais, alvos, tempos, pausa, progresso, últimos comandos e desfecho, vinculados à execução do bloco.

### 4.3 Parâmetros e integração com controle

| Parâmetro | Tratamento |
|---|---|
| Temperatura | Reusar caminho ativo módulo/banho, inclusive confirmação de alvo no banho |
| Agitação | Reusar rota do motor, limites, resolução e feedback disponível |
| Vazão | Reusar desired state completo de gás, confirmação e limites do fluxômetro |
| pH | Alterar referência mantendo configuração da banda e bombas; não ativar dosagem implicitamente |
| Pressão | Reusar referência e limites existentes, informando capacidade de confirmação |
| O₂ | Oferecer rampa apenas para a referência da cascata, com controlador ativo e integração explícita com seu setpoint; `oxygenMonitor` é um interruptor, sem referência numérica |

Para O₂, a implementação deve extrair uma via comum para atualizar a referência da cascata da receita. No firmware atual, `oxygenMonitor` é convertido para booleano e não representa setpoint. Sem controlador compatível, bloquear a rampa de O₂ com explicação. Configurações antigas de rampa com destino `MonitorReference` devem ser recusadas, sem conversão silenciosa para uma cascata.

Reservas são por parâmetro e bloco. Uma rampa de N/Q conflita com kLa e com cascata que manipula os mesmos atuadores. Uma rampa da referência de O₂ pode coexistir com sua própria cascata mediante contrato explícito. O validador e a execução devem verificar ambos os casos.

Pausa: manter os últimos setpoints e congelar o tempo ativo da rampa. Ao retomar, revalidar estado/posse e continuar sem salto. Essa semântica exige extensão explícita do engine, cuja pausa atual só atua entre blocos. Cancelar: cessar novos comandos e aplicar política predefinida de manter referências ou restaurar snapshot; emergência sempre tem prioridade.

## 5. Pacotes de implementação e aceite

| Pacote | Entrega | Evidência de aceite |
|---|---|---|
| R0 — Contratos | Requests/resultados, IDs, políticas, unidades, autoria automática, migração, snapshots e agenda periódica de bloco | Round-trip; compatibilidade de receitas antigas; desconhecidos rejeitados; 2 h + 4 h sem backlog; campos contextuais validados |
| R1 — Coordenação | Reservas por bloco, cessão/retorno Recipe–KlaAssay, suspensão de produtores, cancelamento e emergência | Nenhuma escrita concorrente; recuperação em cada fase; sem reativação depois de emergência; ramos independentes preservados |
| R2 — Serviço autônomo kLa | API idempotente, fila de condições/réplicas/tentativas, análise e política limitada | Matriz Abiótico/Biótico × Único/Múltiplos; mesma chave não duplica atuação; limites e deadlines verificáveis |
| R3 — Persistência e resultados | Diário, recibos de escrita, autoria, resumo, abertura no visualizador comum | Tentativas ruins/abortadas preservadas; falha de disco bloqueia avanço; reconciliação após queda sem repetir pulso |
| R4 — Blocos e editor | Catálogo e executor de kLa; bloco de Periodicidade, ciclo de vida do grupo paralelo, campos contextuais, validador, progresso e política de falha | Disparos em 2/6/10 h; kLa retorna ao agendador e a cascata retoma os comandos; condição de saída legada continua válida; falha de restauração nunca passa como sucesso |
| R5 — Rampas | Modelo de linhas, interpolação, cadência, destinos, pausa e confirmação | Tempos distintos, subida/descida, final exato na resolução do dispositivo, relógio virtual e conflito com kLa/cascata |
| R6 — Integração e qualificação | Documentação, exemplos, regressão, renderização WPF e ensaios de bancada | Evidências de software e de equipamento separadas; liberação autônoma apenas para combinações qualificadas |

Ordem executável revisada: **R0.1 → R0.2 → R1.1 → R1.2 → R1.3 → R3.1 → R2.1 → R2.2 → R4.1 → R4.2 → R5.1 → R5.2 → R6.1 → R6.2**. Persistência antecede o fluxo autônomo: nenhuma próxima tentativa pode ser liberada sem recibo. Cada etapa deve ter commit isolado e recibo com revisão, comando, resultado e limites; os nomes de classes novas abaixo são propostas, não componentes já entregues.

### 5.1 Etapas explícitas de execução

As nove primeiras etapas da ordem revisada são entregas registradas, não tarefas a repetir. R3.1 está conectada ao adaptador R2.1; a próxima execução é R4.2. Cada etapa subsequente depende do aceite da anterior; uma falha de recuperação ou escrita nunca autoriza seguir para a próxima tentativa.

**R0.1 — Consolidar a base recebida (primeira etapa).** Inventariar alterações E5/E6/E7, identificar arquivos de conflito/screenshot duplicados sem excluir evidências automaticamente e consolidar fontes, contratos e testes correspondentes. Conferir vínculos dos recibos aos commits/builds de origem. Preservar as correções de quadros em `RecipeEngine.cs`, `RecipeEngine.Cascade.cs`, `TestClock.cs` e testes associados. Executar regressão de E5/E6, contratos R0, persistência e receitas; registrar baseline com commit e estado local. Aceite: checkout reproduzível, dependências presentes e falhas residuais discriminadas. Não usar `git add` global para misturar material paralelo.

**R0.2 — Conciliar contratos R0/E6 e migração.** Alterar `KlaRecipeContracts.cs`, `RecipeExecutionContracts.cs`, `KlaAssayApiContracts.cs` e serializadores conforme necessário; adicionar mapeador de invocação para pulsos. Definir versão e migração do diário E6, autoria automática, recibos, distinção de falha de persistência/retorno e vínculo ao snapshot. Manter leitura de sessões antigas sem inventar confirmações. Definir contrato de capacidades qualificadas por protocolo/instalação no lugar de depender apenas de um booleano global. Aceite: round-trip/migração, chave repetida com payload diferente recusada, Multiple decomposto em Single com IDs estáveis e nenhuma atuação nesta etapa.

**R1.1 — Reserva e cessão por bloco.** Criar coordenador de recursos da receita; ajustar `CommandArbiter`, `KlaAssayCoordinator` e caminhos de atuação do engine. Reservar atomicamente N/Q/rotas e demais recursos declarados; verificar proprietário esperado e geração da cessão. Fazer `Recipe → KlaAssay → Recipe` sem passagem por Manual. Todos os produtores, incluindo setpoint, rampa, cascata e comandos manuais, devem respeitar a autoridade vigente. Aceite: corrida entre dois blocos/ensaios, timeout e cancelamento sem posse parcial; temperatura independente continua; emergência invalida tokens antigos.

**R1.2 — Suspensão real da cascata e retomada do PID.** Revisar e integrar `RecipeCascadeSuspensionGate` ao ciclo Update+Dispatch em `RecipeEngine.Cascade`; vincular pausa/retomada ao recibo e à geração de R1.1. O gate isolado atual não conclui R1: `Resume` público e liberação após cancelamento de pausa precisam ficar subordinados ao coordenador para não reabrir uma cessão em andamento. Adicionar operação explícita de retomada em `CascadeController`/`CascadeTwoLoopPidController`: preservar configuração e saída coerente, não acumular integral, rebasear tempo e memórias de derivada/janelas na amostra atual. Preservar avaliação da condição de saída durante a suspensão; término solicita recuperação e encerra o grupo. Aceite: pausa espera dispatch em voo, nenhum Update durante ensaio, primeiro passo sem dt acumulado, sem replay de comandos de quadros antigos, saída/debounce existentes mantidos.

**R1.3 — Captura e restauração completas.** Ajustar runner abiótico/biótico e coordenador para usar `KlaReturnSnapshot` capturado após quiescência, com referências solicitadas/confirmadas e leitura separadas. Restaurar rotas, N/Q, controles e proprietário em sucesso, falha e cancelamento; confirmação deve corresponder ao dispositivo. Não reutilizar retorno abiótico a zero como restauração do snapshot. Recuperação tem prazo próprio e não usa token já cancelado da aquisição. Em Multiple, restaurar entre tentativas e antes de devolver a cascata; nova tentativa recaptura estado após adquirir recursos. Aceite: matriz dos dois protocolos em todas as fases, falha de confirmação bloqueia retomada, emergência nunca reativa saídas. Testar em simulação; bancada permanece R6.2.

**R3.1 — Persistência como requisito de avanço.** Estender `IKlaTestStore`, `KlaTestStore` e o caminho de `BackgroundFileWriter` sem regredir outros consumidores. Implementar recibo por tentativa que propague falhas de itens anteriores; distinguir flush de fila e garantia de durabilidade. Persistir request, snapshot e reserva antes da atuação, resultado e decisão antes do avanço. Ajustar `KlaAssayApi` para preservar estado físico quando falhar escrita terminal. Definir reconciliação por IDs entre diário E6 e sessão comum; dados brutos têm uma única fonte autoritativa. Aceite: falhas injetadas antes/durante/depois da corrida, reinício nos intervalos entre gravações, nenhuma repetição de pulso e orçamento não zerado.

**R2.1 — Adaptador de pulso E6 para o runner comum.** Implementar `IKlaAssayExecution` sobre R1/R3, sem dependência da tela ou diálogos. Reutilizar fases, análise e dados existentes; `Completed` do pulso não significa réplica aceita. Exigir retorno confirmado para ambos os protocolos e recibo de persistência. Revisar `MayContinueRecipe` e contratos de resultado para não liberar avanço por qualidade condicional ou `NotRequired`. Registrar dependências em `App.xaml.cs` somente com capacidades apropriadas: simulador isolado para testes, produção fechada enquanto não qualificada. Aceite: execução/observação/cancelamento real do serviço em simulador, request duplicado em Running/terminal, deadline e recuperação com relógio controlável, zero aprovação humana fictícia.

**R2.2 — Matriz e decisão automática.** Implementar orquestrador R0 sobre E6; extrair/reutilizar regras puras de `KlaSequence` e adaptar `KlaSessionModels`, contadores e seleção à autoria automática. Manter fluxo manual legado e importação de mapa desabilitada por padrão para receitas. Aplicar primeira tentativa aceitável, razões estruturadas permitidas, limites de réplica/bloco/cultivo e intervalo mínimo; não usar defaults interativos como perfil autônomo qualificado. Restaurar e devolver a cascata durante espera de repetição; reacquirir antes da próxima perturbação. Aceite: Abiótico/Biótico × Único/Múltiplos, condicional recusado por padrão, esgotamento termina automaticamente, contadores persistidos sem dupla reserva e nenhuma espera por operador.

**R4.1 — Periodicidade e duração do grupo paralelo.** Implementar executor de `PeriodicBlockSchedule` com tempo monotônico, IDs por slot, retorno do alvo e grupo vinculado à cascata. Usar a agenda R0 como única regra da receita; manter helper E6 apenas onde sua semântica esteja explicitamente desejada ou substituí-lo com migração/testes. Definir término/cancelamento/erro do grupo em `RecipeEngine.Flow/State/Safety`: fim da cascata cancela agenda e aguarda recuperação. Slots perdidos são registrados e pulados; nenhuma execução simultânea do próprio alvo. Aceite: 2/6/10 h, ensaio atravessando slot, pausa cobrindo slots, mudança UTC, atraso, condição de saída durante ensaio e emergência. Intervalos mínimos podem pular slots, nunca criar rajada compensatória.

**R4.2 — Blocos, editor e resultados automáticos.** Alterar `RecipeEnums`, `RecipeNodeCatalog`, schema, validador, serializer e executores; adicionar `Periodicidade` e `Determinar kLa`, campos contextuais e referência à cascata coordenada. Validar recursos, ciclos e encerramento dos ramos antes de iniciar. Integrar progresso, qualidade, autoria, restauração e caminhos ao visualizador comum; exportar resumo CSV e abrir sessão sem mapa. Aceite: salvar/reabrir receitas antigas e novas, campos inativos fora do request, fluxo sem diálogo, tentativas ruins visíveis e navegação receita→sessão. UI deve informar capacidades indisponíveis sem habilitar atuação física prematuramente.

**R5.1 — Motor de rampa e destinos.** Implementar serviço de interpolação baseado em `LinearSetpointRampDefinition`; centralizar atualização de referência da cascata, distinta de `OxygenMonitor`. Reusar rotas, resolução e limites reais, validar trajetória inteira inclusive intervalo OFF proibido. Integrar reservas R1. Aceite: linhas com tempos diferentes, subida/descida/constante, alvo final quantizado, taxas de comando limitadas e destino O₂ correto, sem hardware.

**R5.2 — Rampa no engine/editor e conflito com ensaio.** Adicionar catálogo/executor, captura inicial, registro, confirmação e pausa durante bloco. O tempo ativo da rampa congela em pausa; agenda periódica segue a regra própria de slots perdidos. Durante kLa, também suspender rampa da referência de O₂ da cascata coordenada e retomar do ponto preservado, para não alterar silenciosamente o snapshot. Rampas diretas N/Q incompatíveis devem ser recusadas ou aguardar reserva com prazo conforme configuração explícita. Aceite: pausa repetida, cancelamento, perda de posse/comunicação, banho/motor nas rotas existentes, nenhum salto após espera, confirmação final por parâmetro.

**R6.1 — Regressão integrada e documentação.** Executar suíte completa aplicável após todas as integrações; manter regressões E5/E6/E7, armazenamento, arbitragem e quadros da cascata. Criar receitas de exemplo para kLa único/matriz, agenda 2 h/4 h paralela e rampas de tempos distintos. Verificar renderização e interação dos campos/progresso/resultados e produzir recibo vinculado à revisão final. Aceite: toda a execução autônoma demonstrada em simulador com falhas injetadas; artefatos identificados como software, sem atribuir qualificação física.

**R6.2 — Qualificação operacional e habilitação restrita.** Executar roteiro de bancada de E7 ampliado para cessão/retomada da receita, protocolos habilitáveis, exposição, falhas e persistência. Registrar instalação, dispositivos/firmware, calibrações, perfil e evidência de cada retorno. Validar também rampas em rotas reais. Habilitar apenas combinações comprovadas; `KlaActuationRelease` continua bloqueando biótico físico até satisfazer critérios específicos. Aceite: confirmação física e autorização operacional documentadas; se não disponíveis, entrega de software fica explicitamente concluída apenas no ambiente simulado e R6.2 permanece pendente.

### 5.2 Sequência operacional detalhada para as próximas entregas

**R1.3, executar nesta ordem:**

1. Inventariar cada campo comandado e sua confirmação disponível: rota/modo do motor, referência N, vazão Q, válvulas/desvio de gás e configurações dos controladores. Definir estado desejado, eco confirmado e leitura como registros distintos, com origem e timestamp. Não deduzir referência da leitura instantânea.
2. Estender `CommandArbiter` e snapshot/serialização para manter e capturar o estado desejado completo dos recursos reservados. Capturar após suspensão e drenagem; validar proprietário, execução, geração e cobertura dos campos. Registrar estado do controlador suspenso sem recriar uma instância que perca sua configuração.
3. Introduzir atuação reservada em `KlaAssayCoordinator`, runner e coordenador de rotas. Todas as escritas do ensaio passam pela autoridade vigente; o fluxo de receita não executa `Claim/Release` comum nem transição para Manual. Manter comportamento manual separado e testado.
4. Implementar recuperação comum aos dois protocolos, com prazo próprio: aplicar rotas/modos em ordem apropriada, restaurar referências/configurações e aguardar ecos/amostras novas e estabilidade exigida pelo perfil. Cancelar aquisição não cancela recuperação. Não religar se a autoridade foi revogada por emergência.
5. Resolver comandos pendentes na parada de segurança, incluindo frames ordenados já enfileirados. Provar que nenhum frame antigo pode executar depois da parada e reativar motor/gás; não basta testar recusa de um dispatch novo com token vencido.
6. Produzir resultado de recuperação vinculado ao snapshot e às evidências de confirmação. A devolução efetiva à receita depende também do recibo durável de R3.1; até lá testar com armazenamento controlado, sem fabricar recibo em produção. Falha mantém produtores suspensos e termina explicitamente.
7. Validar ambos os protocolos nas fases de preparação, remoção, aquisição e retorno: sucesso, cancelamento, timeout, telemetria obsoleta, desconexão e emergência. Incluir retorno a OFF/zero quando esse era o estado anterior, rotas alternativas e término da cascata durante o ensaio. Emitir recibo e commit isolado.

**R3.1:** primeiro criar barreira durável que propague erros anteriores; depois persistir request/snapshot/reserva antes da atuação e resultado/decisão antes da devolução; por fim testar queda em cada fronteira entre diário E6 e sessão comum. Reinício não retoma perturbação automaticamente nem reinicia orçamento. Aceite inclui recibo real consumido pela devolução R1.3.

**R2.1 → R2.2:** primeiro ligar um pulso ao runner comum e observar seu término, recuperação e persistência; depois adicionar fila de condições/réplicas, seleção da primeira tentativa aceitável e repetição limitada. Espera entre tentativas ocorre com cascata retomada. Registrar capacidades apenas para o ambiente comprovado.

**R4.1 → R4.2:** primeiro implementar duração do grupo paralelo e encerramento aguardável em sucesso/erro/pausa/cancelamento; depois agenda monotônica 2/6/10 h, slots descartados e IDs estáveis; finalmente expor os blocos, campos e resultados no editor. O fim da cascata cancela novos disparos, recupera o ensaio em curso e só então encerra o grupo.

**R5.1 → R5.2:** primeiro implementar interpolação e destinos comuns, incluindo referência da cascata de O₂; o interruptor de monitoramento não admite rampa numérica. Depois integrar reservas, pausa do tempo ativo, confirmação final e editor. Validar cada trajetória inteira antes de comandar, inclusive intervalos não representáveis entre OFF e operação.

**R6.1 → R6.2:** executar regressão integrada e exemplos no simulador; verificar UI e gerar recibo da revisão final. A habilitação física é uma entrega posterior dependente da bancada, com evidência de instalação/protocolo/perfil e recuperação. Software aprovado não encerra R6.2.

### 5.3 Estado de partida e critério de conclusão

- R0 original e extensão de periodicidade/handoff: commits `b8b5f2e` e `c51253f`; seus contratos não provam execução autônoma.
- R0.1/R0.2/R1.1/R1.2: commits `be213dc`, `5669f3a`, `52cee5a`, `237c254`; gate integrado ao engine/PID. R1.3 foi validada em software; integração de produção depende de R3.1/R2.1/R4.1.
- E5/E6/E7: reutilizar o que foi entregue; não refazer núcleo/diário/visualizador nem remover bloqueios para fazer o exemplo funcionar.
- Próxima alteração de código: pacotes F2–F7 do [plano de finalização](PLANO_FINALIZACAO.md). Preservar qualificação por perfil e instalação.
- Pacote só termina com alterações, testes relevantes e recibo; aprovação simulada não fecha bancada. Rever este plano se o checkout receber nova alteração concorrente antes da etapa seguinte.

Arquivos existentes envolvidos: `Services/KlaTesting/{IKlaTestRunner,KlaTestRunner,KlaTestRunner.Biotic,KlaAssayCoordinator,KlaSessionModels,IKlaTestStore,KlaTestStore}.cs`; `Services/Recipes/{RecipeEnums,RecipeNodeCatalog,RecipeSchema,RecipeValidator,RecipeSerializer,RecipeEngine,RecipeEngine.Flow,RecipeEngine.State,RecipeEngine.Safety,RecipeEngine.Actuation,RecipeEngine.Cascade}.cs`; editor/visualizadores de receitas e kLa. Novos serviços devem permanecer fora de ViewModels e compartilhar o núcleo científico atual.

Testes necessários incluem falha em cada fase, cancelamento durante escrita/recuperação, orçamento persistido entre blocos, duplicação/reconexão, duas receitas/ensaios concorrentes, fan-out AND/OR, suspensão da cascata, plano inválido de N/Q, dado de OD obsoleto, disco indisponível e parada de emergência. Rampas devem cobrir relógio de parede alterado, atraso do scheduler, quantização, alvo não alcançável, banho externo, fallback do motor e pausas repetidas.

Na bancada, comprovar rotas de gás e retorno, limites de exposição e recuperação do cultivo, perda de comunicação e atuação real dos setpoints. Aprovação de testes e renderizações não substitui essas verificações. Não adotar valores universais de OD, exposição ou repetição sem perfil validado para o sistema.

Entrega de R0: [contrato e recibo](receitas-r0/CONTRATO_R0.md). Os contratos foram implementados; cessão de posse, captura automática, atuação, recuperação e blocos no editor permanecem nos pacotes seguintes.

## 6. Limites desta entrega planejada

O objetivo é receita autônoma durante a execução normal, inclusive classificação e repetição limitada. Situações irrecuperáveis terminam automaticamente com estado e diagnóstico preservados; não ficam esperando um usuário ausente. Procedimentos manuais pertencem à preparação anterior ou à recuperação posterior.

O bloco genérico de Periodicidade em paralelo à cascata faz parte desta entrega, com kLa como primeiro alvo. Outros tipos de bloco entram gradualmente após definirem contrato de recursos e cancelamento. A recuperação integral da receita após reinício do Windows e a atualização automática do mapa usado pelo controlador exigem entregas específicas.
