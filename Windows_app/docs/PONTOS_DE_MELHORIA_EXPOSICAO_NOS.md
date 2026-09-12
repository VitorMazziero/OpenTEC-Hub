# Pontos de Melhoria e Observações — Exposição de Configurações dos Nós Externos

**Data:** 2026-09-12  
**Contexto:** Levantamento cumulativo de oportunidades arquiteturais, resiliência e boas práticas identificadas durante a execução das Etapas 1 a 8 do plano `2026-09-12-plano-exposicao-config-nos-externos.md` (Hub `10.2.0-dev`, nós externos e aplicativo Windows).

---

## 1. Hub (`ESP32S3-HUB`)

### 1.1 Desacoplamento entre Ecos de Configuração e Filtro de Estagnação
- **Situação atual:** No Hub (`Telemetry.h`), as chaves de eco da distância (`DistanceOffsetMm`, `DistanceSamplePeriodMs`, `DistanceSendPeriodMs`) são emitidas sob a condição:
  ```cpp
  if (validDistance) {
    jsonResponse += ",\"Distance\":" + String(snapDistanceValue, 2);
    if (snapDistanceEchoSeen) {
      jsonResponse += ",\"DistanceOffsetMm\":" + String(snapDistanceOffsetMm, 2);
      ...
    }
  }
  ```
  `validDistance` é verdadeira apenas quando o valor da distância não está estagnado (`distanceSensorValue >= 0.0f`). Se o sensor passar 5 leituras sem alteração física (por exemplo, mira fixa em bancada estática), o filtro de estagnação marca `-1.0f`.
- **Impacto:** O valor do offset e períodos aplicados pelo nó deixam de ser emitidos no `/readData` durante a estagnação, voltando para `"—"` no app, embora o nó continue online e enviando pushes com ecos perfeitamente saudáveis.
- **Melhoria sugerida (Etapa 3 ou revisão do Hub):** Condicionar os ecos de configuração à presença de comunicação (`now - distanceSensorLastUpdate <= DISTANCE_PRESENCE_TIMEOUT`), mantendo `validDistance` restrita apenas à publicação do valor de leitura `Distance`.

### 1.2 Monitoramento do Tamanho do Quadro Agregado (`HUB_TELEMETRY_JSON_RESERVE = 3072`)
- **Situação atual:** Com a adição dos ecos da distância (Etapa 1) e dos futuros ecos de fluxômetro, bomba e biomassa (Etapa 3), o tamanho da string JSON agregada cresce em ~200–350 bytes.
- **Impacto:** O buffer pré-alocado de 3072 bytes comporta com folga o pior caso atual (~2050 B $\rightarrow$ ~2400 B), mas aproxima-se do limite de segurança. Se ultrapassar 3072 bytes, a heap do ESP32 sofrerá realocações dinâmicas a cada ciclo de telemetria, gerando fragmentação.
- **Melhoria sugerida:** Medir em bancada o `Content-Length` real do `/readData` ao final da Etapa 3/4 com todos os periféricos conectados e ecoando simultaneamente (§7.3.5 do plano). Se exceder 2600 bytes, priorizar a poda de campos de depuração pouco usados (`FlowOutput`, `FlowSetpointCorrected`).

### 1.3 Limite de Linha Serial USB (1024 B)
- **Situação atual:** O contrato USB em `WIRE_CONTRACT_V9.md` delimita 1024 bytes por comando app $\rightarrow$ Hub. No sentido inverso (telemetria broadcast Hub $\rightarrow$ PC), a linha emitida por `Serial.println(lastSensorJson)` pode exceder 2000 bytes.
- **Verificação necessária:** Confirmar que os buffers de recepção serial no `Windows_app` (`ConnectionManager` / `System.IO.Ports.SerialPort`) não impõem truncamento em 1024 B ou 2048 B para linhas recebidas.

### 1.4 Regra de "Um `command` por Revisão" na Biomassa e Despacho no App
- **Situação identificada (Etapa 3):** No Hub (`Commands.h`), os comandos de configuração da biomassa (`biomassIt`, `biomassPwm`, `biomassGear`, `biomassEma`, `biomassProbePeriodMs`) montam uma estrutura com chave `"command":"<nome>","value":<valor>`. Como o protocolo JSON do nó aceita apenas uma chave `"command"` por objeto, o Hub enfileira apenas a primeira chave encontrada e descarta quaisquer outras presentes no mesmo quadro, registrando `ESP32_EVT`.
- **Requisito para o App (Etapas 5 e 7):** O aplicativo não deve agrupar alterações de biomassa num único payload JSON. Os `CommandBuilders` e ViewModels devem despachar os comandos sequencialmente, aguardando que `BiomassCommandPending` retorne a `false` antes de enviar o próximo parâmetro.

---

## 2. Sensor de Distância (`External-Devices/sensor-distancia`)

### 2.1 Alinhamento da Faixa de `offset_mm`
- **Situação identificada:** No baseline de `ConfigCodec.cpp`, a validação era `if (offsetVal >= 0.0f)`. No entanto, o Hub e os requisitos de calibração física aceitam offset negativo (faixa $[-50.0, 200.0]$ mm), útil quando a face do sensor é instalada atrás da linha de referência do vaso.
- **Resolução na Etapa 2:** O nó foi ajustado para validar `offsetVal >= -50.0f && offsetVal <= 200.0f`, sincronizando com a validação do Hub.

### 2.2 Política de Proteção da Memória Flash (NVS Wear Leveling)
- **Situação atual:** Toda chamada a `processConfigUpdate` que altere parâmetros chama `saveNvsConfig()`, gravando na memória flash interna do ESP32 via Preferences API.
- **Impacto:** Embora o ESP32 utilize *wear leveling* no partição NVS, gravações em alta frequência degradam a vida útil da flash.
- **Recomendação para o App (Etapa 6):** Assegurar que o app nunca envie comandos de configuração em modo "reativo contínuo" (por exemplo, `TextChanged` sem confirmação ou sem botão explícito). O operador deve preencher os campos e clicar em um botão explícito *"Enviar configuração do nó"*, prevenindo dezenas de escritas em flash a cada ajuste.

### 2.3 Resiliência de Comunicação sob Backoff
- **Situação atual:** Em caso de perda de conexão com o Hub, o nó adota backoff exponencial progressivo até 15 segundos (`MAX_HUB_BACKOFF_MS = 15000`).
- **Comportamento do Piggyback:** Se o operador enviar uma alteração de offset durante esse intervalo, o comando aguarda na `distanceBox` do Hub até o próximo push do nó (podendo levar até 15 s).
- **Tratamento:** O mecanismo de re-entrega (`takeReliable`) reenvia a configuração em cada push subsequente até receber o `ack_cmd_id`. No aplicativo, o indicador `DistanceCommandPending = true` deve manter a mensagem informativa "Aguardando confirmação do nó" para evitar que o operador tente reenviar repetidamente durante o backoff.

### 2.4 Isolamento de Tarefas de Rede (FreeRTOS)
- **Situação atual:** O sensor de distância executa no modelo *single-loop* Arduino (`FirmwareApp.cpp`), onde `httpGet()` possui timeout de 2500 ms. Em redes muito congestionadas ou com pacotes perdidos, o laço de leitura do sensor I2C VL53L0X pode sofrer jitter durante o bloqueio da requisição HTTP.
- **Melhoria futura (diferida):** Migrar a comunicação HTTP e OTA para uma tarefa FreeRTOS secundária em core separado (`xTaskCreatePinnedToCore`), isolando a taxa de amostragem do sensor óptico da pilha Wi-Fi (padrão já utilizado com sucesso no firmware da biomassa).

---

## 3. Sensor de Biomassa e Bomba Peristáltica

### 3.1 Semântica de `set_it`, `set_pwm`, `set_gear`, `ema` e `probe_period` na Biomassa (`CommandCodec.h`)
- **Situação identificada (Etapa 3):** No firmware anterior da biomassa, `set_it` exigia `index` (0–3) e `code` (0–5), `set_pwm` exigia `index` e `value`, e `set_gear` exigia ambos `it` e `pwm`. No entanto, o Hub e o aplicativo enviam formas mais compactas baseadas em `value` ou índice linear.
- **Resolução aplicada na Etapa 4 (v11):**
  - `set_gear`: aceita tanto o par `{"it": i, "pwm": j}` quanto o índice linear direto `{"value": N}` ou `{"gear": N}` ($0 \le N \le 31$, onde $\text{gear} = \text{IT} \times 8 + \text{PWM}$).
  - `set_pwm`: aceita `{"value": V}` diretamente (aplicando ao nível de PWM corrente quando `index` é omitido).
  - `set_it`: aceita `{"code": C}` ou `{"value": C}` diretamente (aplicando ao slot de IT corrente quando `index` é omitido).
  - `probe_period`: quando enviado com `{"value": ms}`, atualiza com segurança o período de amostragem respeitando o limite térmico do LED (`minSafeRefreshMs`); quando enviado sem valor em `IDLE`, dispara a rotina de diagnóstico de tempo de conversão do sensor.
  - `ema`: aceita tanto o comando explícito `{"command":"ema","value":0.8}` quanto o parâmetro de nível superior `{"ema":0.8}`.

### 3.2 Padronização de Nomenclatura nas Chaves da Bomba
- **Situação identificada:** O firmware da bomba espera `pumpSlope` e `pumpIntercept` para calibração, mas `pid_kp`, `pid_ki`, `pid_kd` para PID.
- **Resolução no Hub (Etapa 3):** O Hub faz a ponte traduzindo `pumpPid*` $\rightarrow$ `pid_*` e mantendo `pumpSlope`/`pumpIntercept` como *pass-through*.
- **Oportunidade futura:** Em versões posteriores da bomba, permitir que todas as chaves aceitem o prefixo `pump_` de forma consistente.

### 3.3 Verificação de Headroom dos Nós Externos após Inclusão dos Ecos (Etapa 4)
- **Fluxômetro v11:** O firmware utiliza 1 141 407 B de flash (87%), restando **169 313 B livres** (> 169 KB), superando com folga o limite de segurança mínimo estabelecido de 160 KB.
- **Bomba peristáltica 3.9:** Utiliza 1 095 067 B (83%) de flash e 55 060 B (16%) de RAM estática. Headroom livre de 215 KB.
- **Sensor de biomassa v11:** Utiliza 1 129 728 B (86%) de flash e 69 984 B (21%) de RAM estática. Headroom livre de 181 KB.

---

---

## 5. Aplicativo Windows (`Windows_app`) — Camada de Fio e Simulador (Etapa 5)

### 5.1 Remoção do Campo Vestigial `speed` em `PumpStopProfile()`
- **Decisão (§3.7 do plano):** O comando `PumpStopProfile()` emitia historicamente `{"mode":0,"speed":0}` por paridade com o v.6. No entanto, o firmware do nó de bomba v3.9 tratava qualquer presença da chave de velocidade como solicitação de armar/operar modo de velocidade.
- **Implementação:** O builder foi alterado para emitir estritamente `{"mode":0}`. Todos os testes de formato de fio, parada de emergência e cenários de receitas foram atualizados para validar esse comportamento seguro.

### 5.2 Semântica de Ecos Não-Sticky vs Identidade Sticky
- **Arquitetura:** `TelemetryParser` foi estruturado para que todos os novos ecos de configuração de nós externos (`Distance*`, `Flow*`, `Pump*`, `Biomass*`) sejam estritamente **não-sticky**: na ausência da chave no quadro JSON (ex.: nó desconectado ou modo de dropout), a leitura no snapshot do app é imediatamente redefinida para `null`.
- **Exceção Confiável:** `FlowmeterBootId` é mantido intencionalmente **sticky** (`long?`), pois o `boot_id` identifica o ciclo de inicialização do nó e deve permanecer visível para auditoria de reinicializações espúrias durante a sessão.

### 5.3 Sequenciamento de Comandos de Ajuste da Biomassa
- **Implementação:** Para aderir à restrição de "um `command` por revisão" da caixa de correio do Hub, o método `CommandBuilders.BiomassTuning(...)` retorna `IReadOnlyList<OpenTECCommand>`.
- **Recomendação para Etapa 7:** O ViewModel da Biomassa deve despachar esses comandos de maneira serializada, monitorando `BiomassCommandPending` antes de submeter o próximo elemento da lista.

### 5.4 Precisão de Ponto Flutuante no Simulador (`PumpVolume`)
- **Observação:** O acumulador de volume no simulador é baseado no tempo contínuo de uptime (`model.UptimeSeconds`). Em testes de ciclo rápido, o reset de volume e leitura imediata podem apresentar resíduos infinitesimais ($\sim 10^{-6}$ mL).
- **Tratamento:** As asserções de teste utilizam tolerância de precisão (`< 0.001 mL`) para garantir robustez independente do jitter de temporização do sistema operacional.

---

## 6. Aplicativo Windows (`Windows_app`) — Controle › Distância e Vazão de Ar (Etapa 6)

### 6.1 Confirmação Destrutiva de NVS do Sensor de Distância
- **Decisão:** A restauração de parâmetros de fábrica do sensor de distância (`distanceResetNvs`) sobrescreve diretamente a memória flash NVS do ESP32 remoto.
- **Implementação:** O comando foi vinculado a `IDialogService.ConfirmDestructive`, exigindo confirmação explícita do operador ("Restaurar") e alertando expressamente sobre a reversão para o offset de 20 mm e períodos de 1000 ms.
- **Ponto de Melhoria:** Adicionar na UI um indicador transitório de sincronização (ex: "Gravando NVS...") enquanto o nó reinicializa/aplica e o novo eco não é refletido na telemetria.

### 6.2 Precisão de Feedforward do Fluxômetro (`FormatTuning`)
- **Problema Detectado:** O offset de feedforward do medidor de vazão (`flowFfOffset`) possui o valor de calibração padrão `0.01033` (5 casas decimais). Máscaras convencionais de formatação (`"F2"` ou `"F4"`) truncavam a exibição visual para `0.0103`.
- **Implementação:** Foi introduzido o método auxiliar `FormatTuning` com máscara `"0.#####"` em `FlowControlViewModel`, garantindo que tanto o valor staged quanto o eco aplicado preservem a precisão original sem sufixar zeros espúrios.

### 6.3 Preservação dos Contratos de Layout Unificado no XAML
- **Contrato:** O layout da tela `Controle` possui verificações rígidas de regressão de ergonomia (`ControlWorkspaceContractTests`), monitorando contagens exatas de gatilhos e estilos compartilhados (como 11 instâncias de `ConnectedExternalDeviceEntryStyle`).
- **Implementação:** Os novos expanders de *"Configuração do nó"* e *"Sintonia do controlador"* foram encapsulados de forma auto-contida, utilizando `MultiDataTrigger` de habilitação nos próprios painéis e vinculações de comando com feedback de validação, sem interferir nos marcadores e contadores globais da tabela.

### 6.4 Arbitragem e Concorrência de Sintonia via `ActuatorId.Aeration`
- **Segurança:** O ajuste dos parâmetros de malha (`Kp`, `Ki`, `FfGain`, `FfOffset`, `RampRate`) altera a resposta física da válvula de aeração.
- **Implementação:** Todos os comandos de sintonia foram mapeados para `ActuatorId.Aeration` no `CommandActuators.KeyToActuator`. Com isso, o árbitro central recusa imediatamente qualquer tentativa de envio de sintonia durante ensaios automatizados de $k_L a$, ensaios de potência ou execução de receitas, informando o operador via `DispatchRefusal.Describe`.
- **Ponto de Melhoria Futura:** Prover predefinições (presets) de sintonia do controlador para diferentes faixas operacionais de vazão (ex.: baixa vazão 0.1–1.0 L/min vs alta vazão > 5.0 L/min).

---

## 7. Aplicativo Windows — Bomba Externa e Aquisição da Biomassa (Etapa 7)

### 7.1 Assistente gravimétrico multiponto para a bomba peristáltica

- **Entregue nesta etapa:** A aba **Calibrações › Bomba Externa** permite editar `slope` e `intercept`, visualizar $Q = slope \cdot S + intercept$ em $S = 250, 500, 1000$, enviar o par pelo contrato atual e somente persistir/gravar o recibo quando os dois valores forem ecoados pelo nó. O recibo inclui pedido, eco aplicado, firmware do Hub e identidade da bomba. No firmware 3.9, $S$ é a unidade interna 0–1000 e é convertida em duty PWM 155–1023; usar diretamente PWM 64/128/255, como no rascunho original da Etapa 7, produziria uma calibração incompatível e foi corrigido.
- **Referência legada auditada:** A pasta `Windows_app/_old/_Windows App/v.6` contém `ui/pump_mode_window.py`, uma janela modal de configuração e simulação dos perfis da bomba, mas não contém um procedimento gravimétrico multiponto nem cálculo de calibração da bomba. Foram aproveitados apenas os princípios de fluxo isolado, prévia antes do envio e persistência explícita; seus payloads v3.7 não são autoridade para o contrato atual.
- **Melhoria futura — janela dedicada:** Implementar um assistente modal com etapas e possibilidade de retomar/cancelar: identificação da bomba, tubo e fluido; densidade e temperatura; seleção de PWMs; tara; acionamento cronometrado; massa inicial/final; repetições; ajuste linear; revisão; aplicação.
- **Aquisição sugerida:** Para cada comando de velocidade interna $S$, registrar também o duty PWM ecoado, duração, massa coletada e densidade usada; calcular $V = \Delta m / \rho$ e $Q = V / \Delta t$; exigir ao menos três níveis de $S$ e permitir réplicas. Nunca remover um ponto automaticamente: mostrar resíduos e exigir decisão explícita do operador.
- **Qualidade do ajuste:** Exibir $R^2$, resíduos, intervalo de PWM coberto e alerta de extrapolação. Impedir aplicação com `slope <= 0`, tempos/massas não positivos ou matriz insuficiente; um limiar mínimo de $R^2$ deve ser definido por validação de bancada, não presumido no software.
- **Segurança e rastreabilidade:** O assistente deve parar a bomba ao cancelar/fechar, respeitar a arbitragem de `ActuatorId.ExternalPump`, confirmar a parada por telemetria e salvar os pontos brutos, unidade da balança, densidade, temperatura, operador, identificação do tubo, versões de firmware e coeficientes pedidos/aplicados no recibo.
- **Validação pendente:** O fluxo atual e o futuro assistente não tornam a calibração fisicamente validada. É necessário ensaio de bancada com balança e vidraria rastreáveis, repetibilidade por PWM e verificação independente de vazão após aplicar a curva.

### 7.2 Semântica real dos parâmetros ópticos da biomassa

- **Correção aplicada:** `BiomassIT` é ecoado em milissegundos, porém `set_it` recebe um código discreto 0–5. A UI mantém 25/50/100/200/400/800 ms para o operador e converte para 0/1/2/3/4/5 no fio. A marcha `gear` é o índice óptico combinado 0–31, e não um ganho TIA 1–7. Como `set_it` e `set_pwm` modificam os slots atualmente selecionados, a fila envia primeiro `set_gear`, depois IT e PWM; a ordem inversa alteraria outra posição da tabela.
- **Efeito operacional:** Alterar a tabela de IT ou PWM invalida o branco no firmware v11. A UI avisa que a captura de branco deve ser repetida antes de medir.
- **Período da sonda:** O firmware aceita até 3 600 000 ms e eleva solicitações abaixo do piso térmico calculado. O app expõe essa faixa e inicia em 25 000 ms, em vez do valor de 1 000 ms do rascunho, que seria automaticamente corrigido pelo nó na configuração padrão.
- **Melhoria futura:** Substituir os campos livres de IT e Gear por seletores que mostrem simultaneamente tempo, índice de PWM e PWM efetivo, evitando que o operador precise calcular `gear = IT_idx × 8 + PWM_idx`.

### 7.3 Confirmações e temporização

- **Entregue nesta etapa:** O zeramento de volume é não-otimista e só é declarado concluído após `PumpVol < 0,05 mL`; a calibração só gera recibo após eco compatível; a aquisição óptica envia um comando por vez e permite cancelamento.
- **Melhoria futura:** Centralizar os timeouts de comandos externos num serviço de relógio/ACK acionado mesmo quando a telemetria cessa por completo. Hoje os avisos de 5 s (zeramento), 10 s (biomassa) e 15 s (calibração) são avaliados na chegada de um quadro subsequente; uma queda total do link é indicada pelo estado offline, mas não produz o texto específico de timeout até haver nova telemetria.

---

## 8. Hub e aplicativo — Saúde dos nós (Etapa 8)

### 8.1 Proxy assíncrono e contrato multimeio

- **Entregue no Hub:** Uma tarefa FreeRTOS independente consulta `GET /diag` dos cinco nós registrados a cada 30 s, com timeout de 500 ms, intervalo de 200 ms entre placas e corpo limitado a 511 bytes. Os handlers `/nodeDiag` apenas serializam o cache sob mutex; nenhuma chamada HTTP externa ocorre dentro de callback do servidor.
- **Entregue por USB:** `{"nodeDiag":"all"}` produz uma linha `{"NodeDiag":{...}}` por nó. Essa linha possui resultado próprio no parser do app: não vira telemetria, log ou falha consecutiva de parse e não toma posse de nenhum atuador no árbitro.
- **Entregue no app:** A tabela **Nós na rede do Hub** recebe RSSI, heap livre, uptime, falhas consecutivas com o Hub, estado OTA e métricas específicas. Em Wi-Fi, `/nodes` e `/nodeDiag` são consultados juntos a cada 10 s; em USB, a solicitação serial ocorre a cada 30 s enquanto a seção Conexão está visível.
- **Semântica operacional:** `code = 0` significa que o Hub ainda nunca consultou aquele nó; código negativo representa erro do cliente HTTP do ESP32; outros códigos preservam a resposta HTTP. `age_ms` é a idade da tentativa armazenada no cache, não substitui a idade da última telemetria da coluna **Visto há**.

### 8.2 Medição física de heap ainda necessária

- **Instrumentação aplicada:** O Hub registra na serial `heap antes` e `heap depois` imediatamente ao criar a tarefa `NodeDiag`, permitindo preencher evidência reprodutível sem estimativa manual.
- **Bloqueio de bancada:** Não havia Hub conectado nesta execução. Portanto, o critério de mais de 150 kB livres depois da criação da tarefa permanece **não validado fisicamente** e nenhum valor sintético foi registrado como medição.
- **Próxima ação:** Após gravar o Hub 10.2, capturar o banner de boot e uma varredura completa com os cinco nós em `docs/evidence/hub-node-diag-heap-<data>.md`; registrar heap antes/depois, mínimo observado por 30 min, versões/placas e resultado do limite de 150 kB.

### 8.3 Truncamento, validade JSON e evolução do esquema

- **Situação atual:** Todos os `/diag` atuais cabem em 512 bytes. O Hub sempre termina o buffer em NUL e só incorpora o corpo quando ele ainda possui delimitadores `{...}`; um corpo futuro truncado no meio não corrompe o documento externo, mas aparece como `diag: null` mantendo o código HTTP.
- **Melhoria recomendada:** Acrescentar `truncated: true` e `body_bytes` ao cache se algum firmware ampliar `/diag`. Isso separa explicitamente “HTTP 200 sem métricas” de “resposta maior que o contrato” e permite que o app dê uma orientação precisa de atualização.
- **Compatibilidade:** As métricas comuns são tipadas; campos desconhecidos ficam no dicionário `Extra`. Novos campos de um nó podem ser exibidos progressivamente sem alterar o quadro agregado nem quebrar versões anteriores do app.

### 8.4 Segurança e carga de diagnóstico

- **Superfície exposta:** `/nodeDiag` replica IP, MAC, SSID e estado operacional que já existem no `/diag` local. Hoje a rede do Hub é um SoftAP protegido, mas a rota não possui autenticação própria.
- **Melhoria futura:** Se o Hub passar a operar numa LAN compartilhada, filtrar `ssid`/`mac` por padrão e exigir autenticação para diagnóstico detalhado. Não reutilizar o endpoint para comandos ou OTA.
- **Monitoramento de carga:** Confirmar em soak de bancada que a varredura de 30 s não aumenta perdas de push, jitter do quadro agregado ou `hub_fail_streak`. Se houver contenção no rádio, elevar o intervalo ou escalonar por atividade; não mover as consultas de volta para o handler HTTP.

---

## 9. Matriz de Prioridades e Rastreamento

| Item | Componente | Impacto | Prioridade | Tratamento |
|---|---|---|---|---|
| Desacoplar eco de distância de estagnação | Hub | Telemetria | Baixa/Média | Sugerido para revisão pós-Etapa 3 |
| Validação [-50, 200] mm no nó | Nó Distância | Consistência | Alta | **Aplicado na Etapa 2** |
| Whitelist e tradução de chaves (Bomba/Fluxômetro/Biomassa) | Hub | Comunicação | Alta | **Aplicado na Etapa 3** |
| Regra "um command por revisão" na biomassa | Hub / App | Confiabilidade | Alta | **Aplicado no Hub (Etapa 3); builders em lista (Etapa 5); regra para App (Etapa 7)** |
| Suporte a `value` direto em `set_it`/`set_pwm`/`set_gear` | Nó Biomassa | Protocolo | Alta | **Aplicado na Etapa 4** |
| Eco de `kp`/`ki`/`ramp` no push do fluxômetro (v11) | Nó Fluxômetro | Telemetria / Malha | Alta | **Aplicado na Etapa 4** |
| Eco de `slope`/`intercept` no push da bomba (3.9) | Nó Bomba | Telemetria / Calibração | Alta | **Aplicado na Etapa 4** |
| Eco de `gear`/`ema`/`probe_ms` no push da biomassa (v11)| Nó Biomassa | Telemetria / Óptica | Alta | **Aplicado na Etapa 4** |
| Remoção de `speed` no `PumpStopProfile()` | App / Hub / Nó | Segurança de Acionamento | Alta | **Aplicado na Etapa 5** |
| Ecos não-sticky e `FlowmeterBootId` sticky | App Protocol | Integridade de Dados | Alta | **Aplicado na Etapa 5** |
| Botão explícito de envio (sem auto-save) | App UI | Hardware (Flash NVS) | Alta | **Aplicado na Etapa 6 (Distância e Vazão)** |
| Formatação com precisão 5 casas decimais para `flowFfOffset` | App UI | Exibição / Calibração | Média | **Aplicado na Etapa 6** |
| Arbitragem de comandos de sintonia via `ActuatorId.Aeration` | App Árbitro | Segurança de Processo | Alta | **Aplicado na Etapa 6** |
| Confirmação destrutiva no reset de NVS do sensor de distância | App UI | Prevenção de Falha | Alta | **Aplicado na Etapa 6** |
| Zeramento não-otimista do volume da bomba | App / Nó Bomba | Integridade operacional | Alta | **Aplicado na Etapa 7; validação física pendente** |
| Calibração linear manual com recibo após eco | App / Nó Bomba | Rastreabilidade | Alta | **Aplicado na Etapa 7; assistente gravimétrico diferido** |
| Assistente gravimétrico multiponto da bomba | App / Bancada | Calibração física | Alta | **Diferido; especificado em §7.1** |
| Conversão IT ms → código e Gear 0–31 | App / Nó Biomassa | Contrato de fio | Alta | **Corrigido na Etapa 7** |
| Fila cancelável de aquisição da biomassa | App / Hub / Nó | Confiabilidade | Alta | **Aplicado na Etapa 7** |
| Serviço central de timeout independente de telemetria | App | Diagnóstico | Média | **Diferido; especificado em §7.3** |
| Proxy `/nodeDiag` fora do handler HTTP | Hub | Responsividade / Rede | Alta | **Aplicado na Etapa 8; soak físico pendente** |
| Linha serial `NodeDiag` fora da telemetria | Hub / App Protocol | Integridade de Dados | Alta | **Aplicado na Etapa 8** |
| Saúde dos cinco nós em Wi-Fi e USB | App UI | Diagnóstico | Alta | **Aplicado na Etapa 8; validação física pendente** |
| Medição de heap do Hub após tarefa NodeDiag | Hub / Bancada | Memória / Estabilidade | Alta | **Instrumentada; pendente de bancada conforme §8.2** |
| Sinalizador explícito de corpo `/diag` truncado | Hub / App | Diagnóstico | Média | **Diferido; especificado em §8.3** |
| Autenticação/filtragem da rota `/nodeDiag` em LAN compartilhada | Hub | Segurança | Média | **Condicional; especificado em §8.4** |
| Medição Content-Length `/readData` | Hub / Infra | Confiabilidade | Média | Executar na bancada da Etapa 4/7 |
| FreeRTOS multi-core no sensor | Nó Distância | Desempenho | Baixa | Diferido no ROADMAP |
