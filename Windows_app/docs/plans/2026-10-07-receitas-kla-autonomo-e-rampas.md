# Receitas: determinação autônoma de kLa e rampas lineares

Data: 07/10/2026. Base inspecionada: `main`, commit `84d246d`.
Estado: plano de implementação; não representa funcionalidade já entregue ou qualificação de bancada.

## 1. Situação atual e escopo

Há uma base reutilizável independente da tela: `IKlaTestRunner`, `IKlaTestStore`, `IKlaDeterministicAnalysisEngine` e definições de ensaio que separam protocolo (`Abiotic`/`Biotic`) de captura (`Single`/`Multiple`). Ambos os modos usam a mesma unidade de corrida. O modo único exige uma condição e uma réplica planejada.

Isso ainda não equivale à API autônoma de receitas. O plano de 06/10 mantém E5 (sequências) e E6 (contrato para receitas) pendentes. O runner chega a `Reviewing`, expõe aceitar/rejeitar/repetir, e `KlaAssayCoordinator.Validate` rejeita posse `Recipe`. Não existe `KlaAssay` no catálogo ou no executor de receitas.

Outros pontos relevantes verificados:

- A receita assume todos os atuadores; seus ramos podem executar em paralelo. O proprietário `Recipe` sozinho não distingue dois blocos concorrentes.
- `Pause` atua entre blocos; não interrompe automaticamente o trabalho em andamento.
- `SafeStopAndRelease` atualmente libera a posse; seu nome não garante restauração física ou envio de comandos de recuperação.
- O armazenamento de kLa já inclui dados brutos, eventos, análises revisionadas e resumo. As escritas são enfileiradas; existem `FlushAsync` e `WriteFailed`.
- Qualidade de kLa, qualidade de OUR, decisão do operador e restauração já são dimensões separadas. A decisão automática precisará de autoria própria.
- O catálogo de receitas admite listas e campos condicionais por `VisibleWhen`. Não há bloco de rampa.
- O setpoint de O₂ de `SetSetpoint` escreve `OxygenMonitor`; isso não altera, por si só, a referência do controlador da cascata.

Este plano complementa E5/E6 com dois blocos, preservando o runner, o núcleo científico e o armazenamento comuns. A API proposta é interna ao aplicativo; servidor HTTP não é requisito.

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

Introduzir `IKlaAssayService` acima do runner existente, com operações equivalentes a:

- `ValidateRequest`: validação sem atuação.
- `StartOrGetAsync(request)`: cria ou recupera a mesma execução por chave idempotente.
- `Observe/GetStatus`: progresso, condição, réplica, tentativa, fase, limites e caminhos dos artefatos.
- `CancelAndRestoreAsync`: cancela aquisição e executa recuperação limitada por prazo próprio.
- `GetResult`: resultado terminal persistido e agregado por condição.

O request imutável inclui definição do ensaio, políticas de qualidade/repetição/falha, deadlines, contexto do cultivo, versão/hash da receita e IDs da execução, bloco e invocação. A identidade da invocação inclui ciclo quando houver repetição da receita. A mesma chave com payload diferente é erro; uma tentativa nova recebe ID novo ligado à mesma réplica. Repetição científica não pode ser confundida com retransmissão de comando.

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
| O₂ | Distinguir referência de monitoramento de referência da cascata; oferecer rampa de controle apenas quando houver controlador ativo e integração explícita com seu setpoint |

Para O₂, a implementação deve extrair uma via comum para atualizar a referência da cascata da receita; enviar apenas `OxygenMonitor` não cumpre uma rampa de controle de OD. Sem controlador compatível, bloquear essa modalidade com explicação. A interface deve nomear o destino quando a referência for apenas de monitoramento.

Reservas são por parâmetro e bloco. Uma rampa de N/Q conflita com kLa e com cascata que manipula os mesmos atuadores. Uma rampa da referência de O₂ pode coexistir com sua própria cascata mediante contrato explícito. O validador e a execução devem verificar ambos os casos.

Pausa: manter os últimos setpoints e congelar o tempo ativo da rampa. Ao retomar, revalidar estado/posse e continuar sem salto. Essa semântica exige extensão explícita do engine, cuja pausa atual só atua entre blocos. Cancelar: cessar novos comandos e aplicar política predefinida de manter referências ou restaurar snapshot; emergência sempre tem prioridade.

## 5. Pacotes de implementação e aceite

| Pacote | Entrega | Evidência de aceite |
|---|---|---|
| R0 — Contratos | Requests/resultados, IDs, políticas, unidades, autoria automática, migração e snapshots | Round-trip; compatibilidade de receitas antigas; desconhecidos rejeitados; campos contextuais validados |
| R1 — Coordenação | Reservas por bloco, cessão/retorno Recipe–KlaAssay, suspensão de produtores, cancelamento e emergência | Nenhuma escrita concorrente; recuperação em cada fase; sem reativação depois de emergência; ramos independentes preservados |
| R2 — Serviço autônomo kLa | API idempotente, fila de condições/réplicas/tentativas, análise e política limitada | Matriz Abiótico/Biótico × Único/Múltiplos; mesma chave não duplica atuação; limites e deadlines verificáveis |
| R3 — Persistência e resultados | Diário, recibos de escrita, autoria, resumo, abertura no visualizador comum | Tentativas ruins/abortadas preservadas; falha de disco bloqueia avanço; reconciliação após queda sem repetir pulso |
| R4 — Bloco e editor kLa | Catálogo, campos contextuais, validador, executor, progresso e política de falha | Receita inteira termina sem diálogos; inconclusivo segue a política; restauração falha nunca passa como sucesso |
| R5 — Rampas | Modelo de linhas, interpolação, cadência, destinos, pausa e confirmação | Tempos distintos, subida/descida, final exato na resolução do dispositivo, relógio virtual e conflito com kLa/cascata |
| R6 — Integração e qualificação | Documentação, exemplos, regressão, renderização WPF e ensaios de bancada | Evidências de software e de equipamento separadas; liberação autônoma apenas para combinações qualificadas |

Ordem recomendada: R0 → R1 → R2 → R3 → R4 → R5 → R6, em commits pequenos com validação por pacote. Partes puras da matemática da rampa podem ser desenvolvidas antes, mas sua integração depende das reservas e da pausa.

Arquivos existentes envolvidos: `Services/KlaTesting/{IKlaTestRunner,KlaTestRunner,KlaTestRunner.Biotic,KlaAssayCoordinator,KlaSessionModels,IKlaTestStore,KlaTestStore}.cs`; `Services/Recipes/{RecipeEnums,RecipeNodeCatalog,RecipeSchema,RecipeValidator,RecipeSerializer,RecipeEngine,RecipeEngine.Flow,RecipeEngine.State,RecipeEngine.Safety,RecipeEngine.Actuation,RecipeEngine.Cascade}.cs`; editor/visualizadores de receitas e kLa. Novos serviços devem permanecer fora de ViewModels e compartilhar o núcleo científico atual.

Testes necessários incluem falha em cada fase, cancelamento durante escrita/recuperação, orçamento persistido entre blocos, duplicação/reconexão, duas receitas/ensaios concorrentes, fan-out AND/OR, suspensão da cascata, plano inválido de N/Q, dado de OD obsoleto, disco indisponível e parada de emergência. Rampas devem cobrir relógio de parede alterado, atraso do scheduler, quantização, alvo não alcançável, banho externo, fallback do motor e pausas repetidas.

Na bancada, comprovar rotas de gás e retorno, limites de exposição e recuperação do cultivo, perda de comunicação e atuação real dos setpoints. Aprovação de testes e renderizações não substitui essas verificações. Não adotar valores universais de OD, exposição ou repetição sem perfil validado para o sistema.

Entrega de R0: [contrato e recibo](receitas-r0/CONTRATO_R0.md). Os contratos foram implementados; cessão de posse, captura automática, atuação, recuperação e blocos no editor permanecem nos pacotes seguintes.

## 6. Limites desta entrega planejada

O objetivo é receita autônoma durante a execução normal, inclusive classificação e repetição limitada. Situações irrecuperáveis terminam automaticamente com estado e diagnóstico preservados; não ficam esperando um usuário ausente. Procedimentos manuais pertencem à preparação anterior ou à recuperação posterior.

Agendamento periódico, recuperação integral de receita após reinício do Windows e atualização automática do mapa usado pelo controlador não entram implicitamente nos dois novos blocos. Seus contratos podem reutilizar os IDs, resultados e reservas aqui definidos, mas exigem entregas específicas.
