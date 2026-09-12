# Pontos de Melhoria e Observações — Exposição de Configurações dos Nós Externos

**Data:** 2026-09-12  
**Contexto:** Levantamento de oportunidades arquiteturais, resiliência e boas práticas identificadas durante a execução das Etapas 1 e 2 do plano `2026-09-12-plano-exposicao-config-nos-externos.md` (Hub `10.2.0-dev` e nós externos).

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

## 6. Matriz de Prioridades e Rastreamento

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
| Botão explícito de envio (sem auto-save) | App UI | Hardware (Flash NVS) | Alta | Regra para a Etapa 6 |
| Medição Content-Length `/readData` | Hub / Infra | Confiabilidade | Média | Executar na bancada da Etapa 4/7 |
| FreeRTOS multi-core no sensor | Nó Distância | Desempenho | Baixa | Diferido no ROADMAP |
