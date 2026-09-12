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

## 3. Matriz de Prioridades e Rastreamento

| Item | Componente | Impacto | Prioridade | Tratamento |
|---|---|---|---|---|
| Desacoplar eco de distância de estagnação | Hub | Telemetria | Baixa/Média | Sugerido para revisão pós-Etapa 3 |
| Validação [-50, 200] mm no nó | Nó Distância | Consistência | Alta | **Aplicado na Etapa 2** |
| Botão explícito de envio (sem auto-save) | App UI | Hardware (Flash NVS) | Alta | Regra para a Etapa 6 |
| Medição Content-Length `/readData` | Hub / Infra | Confiabilidade | Média | Executar na bancada da Etapa 4/7 |
| FreeRTOS multi-core no sensor | Nó Distância | Desempenho | Baixa | Diferido no ROADMAP |
