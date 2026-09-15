# Inventário de Especificações: Bomba Peristáltica Externa (Firmware 3.10)
**Documento de Engenharia e Mapeamento de Requisitos Técnicos**  
**Data:** 2026-09-13  
**Autor:** Agente Minerador de Especificações (`spec_miner_1`)  
**Fontes Autoritativas:**  
1. `External-Devices/docs/COMANDOS_DISPOSITIVOS_EXTERNOS.md` (§1.0 a §1.11)  
2. `External-Devices/bomba-peristaltica/firmware/peristaltic-pump/src/` (Código-fonte Firmware 3.10)  
3. `ESP32S3-HUB/ESP32S3-HUB/src/` (`Commands.h`, `HttpServer.h`, `Telemetry.h`)  
4. `Windows_app/` (`CommandBuilders.cs`, `PumpCalibrationViewModel.cs`, `PumpControlViewModel.cs`, `PONTOS_DE_MELHORIA_EXPOSICAO_NOS.md`)  
5. `ORIGINAL_REQUEST.md` (Instrução Superior e Critérios de Aceitação)

---

## 1. Sumário Executivo e Objetivos

Este inventário tem como propósito extrair, sistematizar e catalogar exaustivamente todas as especificações, limitações, decisões de engenharia, restrições físicas de hardware e itens de verificação em bancada referentes ao subsistema da **Bomba Peristáltica Externa** (`bomba-peristaltica`, firmware v3.10).

O inventário serve como referência técnica absoluta para a auditoria cruzada do código-fonte (Firmware do nó, Hub ESP32-S3 e Aplicativo Windows) e fundamenta a elaboração do plano formal de implementação (`IMPLEMENTATION_PLAN_BOMBA.md`), garantindo rastreabilidade total desde o nível elétrico e mecânico até as interfaces de usuário e testes automatizados.

### Convenção de Nomenclatura e Identificação de Itens
- **§1.10 — Limitações, Riscos e Decisões:** Identificados pelos prefixos `F-110-01` a `F-110-12` (correspondendo aos itens 1 a 11 e o item adicional de potenciômetro da tabela 1.10), complementados por decisões arquiteturais fechadas (`DEC-SEC-xx`) e riscos sistêmicos (`RISK-SYS-xx`).
- **§1.11 — Checklist de Bancada:** Identificados pelos prefixos `CHK-111-01` a `CHK-111-11` (correspondendo aos 11 passos do checklist experimental).
- **Subseções de Contexto (§1.1 a §1.9):** Identificadas com seus respectivos números e nomes temáticos.

---

## 2. Contexto de Arquitetura, Fio e Protocolo (§1.1 a §1.9)

### 2.1 O que o firmware assume do hardware (§1.1)

| Elemento | Pinos / Recurso | Comportamento no Firmware 3.10 | Restrições Físicas e Observações |
|---|---|---|---|
| **Ponte H (PWM Duplo)** | `R_EN` = GPIO 25, `L_EN` = GPIO 26 (fixados em `HIGH` no boot). `R_PWM` = GPIO 14, `L_PWM` = GPIO 27. | LEDC configurado a 7,5 kHz, resolução de 10 bits (`PWM_MAX_DUTY = 1023`). Velocidade define o duty em uma das saídas; a outra permanece em nível 0. Sentido positivo ($S \ge 0$) ativa `R_PWM`; sentido negativo ($S < 0$) ativa `L_PWM`. | **Motor DC escovado** confirmado em ponte H BTS7960/IBT-2. O modelo de motor de passo NEMA 17HS4401 presente em arquivos CAD legados é referência mecânica dimensional, **não** o atuador eletromecânico montado. |
| **Cabeçote Peristáltico** | CAD Watson-Marlow: rotor com 5 roletes e mancais lineares LM6UU. | Não há relação física analítica a priori entre duty e vazão. Toda dosagem depende de calibração empírica por reta ($Q = \text{slope} \cdot S + \text{intercept}$). | O esmagamento da mangueira impõe torque resistivo elevado na partida. |
| **Potenciômetro de Velocidade** | `POT_INT` = GPIO 34 (ADC 12 bits, atenuação 11 dB). | Filtro passa-baixas digital IIR de primeira ordem ($\alpha = 0,10$). Mapeia magnitude normalizada $0..1 \times V_{\text{MAX}}$ ($V_{\text{MAX}} = 1000$). | Opera estritamente no modo manual local de bancada quando `disablePot == false` e `hasUsbSpeed == false`. |
| **Potenciômetro de Sentido e Ganho** | `POT_GAIN` = GPIO 35 (ADC 12 bits, atenuação 11 dB). | Fator de ganho e polaridade: $\text{fator} = \frac{\text{ADC} - \text{centro}}{\text{centro}} \in [-1, +1]$. Multiplica a magnitude do primeiro potenciômetro. Ponto central ($\sim 2047$) define parada; extremos definem rotação máxima horária ou anti-horária. | Ajuste local exclusivo de bancada física. O sinal do fator determina qual ramo da ponte H é pulsado. |
| **Sensor de Presença de Líquido** | `SENSOR_PIN` = GPIO 15 (`INPUT_PULLUP`). | Debounce por software de 50 ms. Nível lógico `LOW` indica molhado. Opera como chave de contato: quando a porta do sensor está habilitada, o motor só tem autorização para girar se o sensor estiver imerso em líquido. | Não é fluxômetro nem sensor de volume; é um gate binário de contato. Aciona o motor na velocidade ajustada pelos potenciômetros. |
| **Botão de Ativação do Sensor** | `SENSOR_ENABLE_BUTTON_PIN` = GPIO 32 (`INPUT_PULLUP`). | Quando `sensorButtonOverride == false`, a leitura deste botão físico controla a ativação da porta do sensor (`sensorEnable = !digitalRead(...)`). | Permite chavear localmente na bancada o modo manual de operação por líquido. |
| **LED Indicador do Sensor** | `SENSOR_STATUS_LED_PIN` = GPIO 33 (`OUTPUT`). | Espelha o estado booleano de `sensorEnable` (acende quando a porta de sensor por líquido está ativa). | Sinalização visual física para o operador de bancada. |
| **Interface Wi-Fi Dual** | Rádio ESP32 em modo dual `WIFI_AP_STA`. | **STA:** Conecta ao Hub nos SSIDs `ModuloTECNAL_1` ou `ModuloTECNAL_2` (canal 6 fixo). Push a 1 s (`DATA_PUSH_PERIOD_MS`), poll a 2 s (`HUB_POLL_PERIOD_MS`), hello a 30 s. Backoff exponencial até 15 s. Link Watchdog: 8 falhas consecutivas forçam `WiFi.disconnect()`.<br>**AP:** Rede aberta local `FeedPump` (192.168.6.1, canal 6). | Servidor web local HTTP na porta 80 ativo em ambas as interfaces (`/readData`, `/diag`, `/status`, `/command`, `/update`). |
| **Distribuição Multi-Core (FreeRTOS)** | ESP32 Dual Core (Core 0 e Core 1). | **Core 0:** Tarefa de tempo-real estrito `pwmTask` (período de 2 ms, prioridade 1), dedicada a escrever no hardware LEDC e integrar numericamente o volume dosado.<br>**Core 1:** `loop()` principal do Arduino, processando servidor web, Wi-Fi, polling de comandos do Hub, máquina de estados de perfis e reset do Watchdog TWDT (timeout de 15 s). | Comunicação entre núcleos via variáveis `volatile` sem concorrência bloqueante (`g_cmdSpeed`, `g_driverEnabled` do Core 1 para o Core 0; `g_cumulativeVolumeMl`, `g_actualPwmDuty` do Core 0 para o Core 1). |

---

### 2.2 Determinação da Velocidade Final e Dinâmica do Motor (§1.2)

A cada passagem pelo laço do firmware no Core 1 (~2 ms + processamento de rede), a velocidade comandada ao motor é calculada conforme a seguinte máquina de decisão:

```text
requestedSpeed =
    OP_RUNNING → mlminToSpeedUnits(Q_alvo + PID)           (sempre não-negativo em perfil)
    OP_IDLE    → hasUsbSpeed ? usbSpeedSteps : potSpeed      (pode ser negativo = sentido reverso; potSpeed = 0 com pot travado)
    (Firmware 3.10) hasUsbSpeed && speed_ms vencido → usbSpeedSteps = 0
    OP_WAITING → 0.0

allowRun   = !(sensorEnable && !sensorBypass) || sensorMolhado
finalSpeed = allowRun ? clamp(requestedSpeed, −1000.0, +1000.0) : 0.0
```

#### Regras de Quantização e Transição Físico-Mecânica
1. **Unidade Interna $S \in [-1000, +1000]$:**
   - A tarefa `pwmTask` no Core 0 recebe $S = \text{g\_cmdSpeed}$.
   - Se $|S| < 1.0$ (`ENABLE_EPS`), o duty é zerado ($0$).
   - Se $|S| \ge 1.0$, o duty PWM de 10 bits é mapeado linearmente na faixa ativa:
     $$\text{duty} = \text{PWM\_BREAKAWAY} + \frac{|S| - 1}{999} \cdot (\text{PWM\_MAX\_DUTY} - \text{PWM\_BREAKAWAY})$$
     onde $\text{PWM\_BREAKAWAY} = 155$ e $\text{PWM\_MAX\_DUTY} = 1023$.
   - **Implicação Física:** O duty mínimo útil de saída é $155/1023 \approx 15,15\%$. Abaixo desse patamar, o motor DC não vence o atrito estático e o esmagamento dos roletes no cabeçote Watson-Marlow. Logo, $S = 1$ não produz um giro infinitesimal lento, mas sim a partida direta no patamar mínimo de arraste.
2. **Trava Mínima de Acionamento de 500 ms (`MIN_MOTOR_ON_TIME_MS`):**
   - Ao transicionar de motor desligado para ligado, o firmware trava a velocidade inicial por no mínimo 500 ms (`g_motorOnLatchTimeMs`). Durante esse intervalo, novas oscilações do controlador ou variações do alvo são ignoradas.
   - **Objetivo:** Eliminar trepidações e instabilidades mecânicas na partida quando o setpoint oscila próximo ao limiar de corte $S = 1$.
3. **Conversão Bidirecional para Vazão:**
   - Reta de conversão: $Q\ [\text{mL/min}] = \text{slope} \cdot S + \text{intercept}$.
   - Conversão inversa: $S = \frac{Q - \text{intercept}}{\text{slope}}$.
   - **Guarda de Divisão por Zero:** Se $|\text{slope}| < 10^{-6}$, o firmware força $S = 0$. Uma parametrização acidental de slope nulo **imobiliza a bomba em todos os modos de perfil**.

---

### 2.3 Integração de Volume e Controle em Malha Fechada por PID (§1.3)

O volume entregue é calculado numericamente no Core 0 por integração trapezoidal de Euler a cada 2 ms:
$$\Delta V = \frac{Q_{\text{efetivo}}\ [\text{mL/min}]}{60} \cdot \Delta t\ [\text{s}]$$
$$\text{g\_cumulativeVolumeMl} \leftarrow \text{g\_cumulativeVolumeMl} + \Delta V$$
onde $Q_{\text{efetivo}}$ é obtido convertendo o duty aplicado de volta para unidades $S$ e aplicando a reta de calibração ativa (`pwmDutyToMlmin`).

#### Princípios Operacionais Vitais para Engenharia e Operação
1. **Volume e Vazão são Inferências Numéricas:** Nem o nó nem o Hub possuem sensor de fluxo físico acoplado à mangueira. `PumpVol` e `PumpFlow` são predições matemáticas deduzidas exclusivamente da curva de calibração empírica. Se a reta estiver descalibrada, todo o volume publicado estará sistematicamente errado sem que nenhum alarme de telemetria denuncie a discrepância.
2. **Arquitetura de Contador de Sessão (Inovação do Firmware 3.10):**
   - No firmware 3.9, qualquer comando `stop`, `mode:0` ou alteração de perfil zerava sumariamente o volume acumulado.
   - No firmware 3.10, `g_cumulativeVolumeMl` atua como **contador de sessão ininterrupto**, sendo zerado **estritamente pelo comando explícito `reset_volume`**.
   - Cada ciclo de perfil armazena seu marco inicial (`g_cycleStartVolumeMl`), gravado no checkpoint NVS como `s_cvol`. A telemetria publica o volume acumulado da sessão (`vol`) e o volume isolado do ciclo vigente (`cyc_vol` = `vol` - `g_cycleStartVolumeMl`).
3. **Escopo do PID de Volume:**
   - O PID (`pid_kp`, `pid_ki`, `pid_kd`) atua em unidades de volume acumulado no ciclo:
     $$e(t) = V_{\text{alvo}}(t) - V_{\text{ciclo}}(t)$$
     onde $V_{\text{alvo}}(t)$ é a integral analítica exata da curva teórica de perfil selecionada.
   - **Limitação Intrínseca:** O PID corrige apenas atrasos de execução física (patamar de partida de 155, latência do laço, atraso de transição em `OP_WAITING`). Ele **não corrige** erros de calibração mecânica da mangueira. O termo integrador é saturado em $\pm 100\text{ mL}\cdot\text{min}$.
   - Ganhos nominais de fábrica: $K_p = 0,5$, $K_i = 0,05$, $K_d = 0,001$. O firmware 3.10 ecoa esses valores no push (`kp`, `ki`, `kd`), permitindo validação e desbloqueio seguro da interface no Windows App.

---

### 2.4 Máquina de Estados de Operação do Nó (§1.4)

O controlador do nó opera com base em três estados formais:

```text
       ┌───────────────┐
       │   OP_IDLE     │◄────────────────────────────────┐
       └──────┬────────┘                                 │
              │ Perfil com init_t > 0                    │
              ▼                                          │
       ┌───────────────┐                                 │ Parada:
       │  OP_WAITING   │                                 │ - final_t atingido
       └──────┬────────┘                                 │ - comando stop
              │ Tempo t >= init_t                        │ - comando mode:0
              ▼                                          │
       ┌───────────────┐                                 │
       │  OP_RUNNING   │─────────────────────────────────┘
       └───────────────┘
```

| Estado | Condições de Entrada | Condições de Saída | Ação Física no Motor |
|---|---|---|---|
| **`OP_IDLE`** | Inicialização sem checkpoint ativo; recepção de `stop`; recepção de `{"mode":0}`; novo comando `speed`; término do tempo `final_t`. | Envio de perfil (`mode` $\ge 1$) com `init_t > 0` $\rightarrow$ `OP_WAITING`.<br>Envio de perfil com `init_t == 0` $\rightarrow$ `OP_RUNNING`.<br>Comando `start` $\rightarrow$ `OP_RUNNING`. | Motor responde aos potenciômetros manuais (se `disablePot == false`) **ou** à velocidade manual recebida via `speed` (até expirar `speed_ms`, ser enviado `pot:1` ou novo comando). |
| **`OP_WAITING`** | Envio de perfil com parâmetro `init_t > 0`. | Temporização atinge $t \ge \text{init\_t}$ $\rightarrow$ transita para `OP_RUNNING`. Marca início de ciclo (`startCycle()`), reseta integrador do PID. Preserva o acumulador de volume `vol`. | Motor completamente parado ($Q = 0$). |
| **`OP_RUNNING`** | Transição de `OP_WAITING`; início imediato de perfil; recuperação autônoma pós-queda de energia via checkpoint NVS. | Temporização atinge $\text{final\_t} > 0 \land t \ge \text{final\_t}$ $\rightarrow$ transita para `OP_IDLE`, persiste `mode = 0`, limpa checkpoint NVS. **Volume acumulado é preservado**. | Motor é comandado a $Q(t) + \text{PID}$ convertido para unidades $S$ e duty PWM correspondente. |

*Nota de Temporização:* $t$ é contabilizado em minutos: $t = \frac{\text{millis}() - \text{g\_opTriggerTimeMs}}{60000}$. Se $\text{final\_t} = 0$, **o perfil executa indefinidamente** até intervenção manual do operador.

---

### 2.5 Canais de Ingestão de Comandos e Roteamento (§1.5)

O nó pode receber comandos por quatro vias concorrentes, todas convergindo para o interpretador `processJsonCommand()`:

1. **Canal Central: Hub $\rightarrow$ Nó (Via Aplicativo Windows):**
   - O nó consulta periodicamente `GET http://192.168.4.1/pumpCommand` a cada 2 s (com backoff progressivo até 15 s se houver falhas). O Hub responde entregando o conteúdo da caixa postal `pumpBox` em formato JSON (`{"cmd_id": N, ...}`).
   - **Política de Deduplicação e Latest-Wins:** O nó compara `cmd_id` com `g_lastAppliedHubCommandId`. Se idêntico, descarta sem reexecutar. O Hub enfileira apenas um quadro por vez: a chegada de um novo comando antes do consumo do anterior **sobrescreve** o quadro precedente. O `ack_cmd_id` é devolvido pelo nó no push seguinte de `/pumpData`.
   - **Filtro de Comunicação:** O Hub só enfileira comandos se o roteamento estiver ativado (`pumpComm == 1`).
2. **Canal Local: Ponto de Acesso Wi-Fi Próprio (`FeedPump`):**
   - Requisição `POST /command` no IP local `192.168.6.1`. Sem autenticação ou validação de `cmd_id`. Executado imediatamente. Essencial para bancada e manutenção isolada.
3. **Canal Físico Local: Serial USB UART (115200 bps):**
   - Linhas de texto delimitadas por `\n` contendo strings JSON (buffer de linha de 4096 bytes). Prioridade operacional total na bancada de desenvolvimento.
4. **Canal de Firmware: Atualização OTA (`/update`):**
   - Página HTML e endpoint de upload multipart `.bin`. Durante o upload do binário, a execução do laço de controle é suspensa; o watchdog aborta o processo se nenhum bloco for recebido em 90 s.

---

### 2.6 Catálogo Completo de Chaves e Contrato de Fio (§1.6)

Convenção de prefixação: O aplicativo Windows emite comandos com prefixo `pump_` (ex: `pump_speed`, `pump_pot`, `pump_command`) ou nomes explícitos de calibração/PID (`pumpSlope`, `pumpIntercept`, `pumpPidKp`). O Hub decompõe o envelope, remove `pump_`, traduz chaves de PID e repassa ao nó. Chaves fora da lista branca de `Commands.h` **são sumariamente descartadas em silêncio**.

#### A. Comandos de Controle Textual (`"command":"..."`)

| Comando | Efeito no Firmware 3.10 | Efeito Mecânico no Hardware | Whitelist Hub (`pump_command`) | Caminho no Windows App |
|---|---|---|:---:|---|
| `start` | Força `init_t = 0`, `final_t = 0`, transita para `OP_RUNNING` com o `mode` corrente. Dispara `startCycle()`, limpa checkpoint. | Motor gira em perfil contínuo sem parada programada. **Atenção:** Se `mode == 0`, fica marcado como ativo porém com $Q = 0$ (motor parado). | Sim | Não utilizado diretamente pela UI. |
| `stop` | Transita para `OP_IDLE`, persiste `mode = 0`, dispara `startCycle()`, limpa checkpoint NVS. **Volume acumulado `vol` é preservado.** | Motor cessa o perfil (para imediatamente, salvo comandos em IDLE). | Sim | Não utilizado (o App envia `{"mode":0}`). |
| `reset_volume` | **Único comando que zera o acumulador de sessão `vol`** e o marco `g_cycleStartVolumeMl`. Não altera a máquina de estados nem o modo. | Nenhuma ação no motor. | Sim | Botão **Zerar volume acumulado** (*Controle* e *Calibrações*). Exige confirmação não-otimista de `PumpVol < 0,05 mL`. |
| `save_config` | Grava o blob `PumpConfig` atual na NVS do ESP32. | Nenhum. | **Não** (Bloqueado com aviso `ESP32_AVISO`). | Inacessível via Hub. |
| `load_config` | Recarrega as configurações salvas da NVS. | Nenhum. | **Não** (Bloqueado com aviso `ESP32_AVISO`). | Inacessível via Hub. |
| `print_config`| Emite pela serial USB a estrutura JSON completa com arrays de interpolação. | Nenhum. | **Não** (Bloqueado). | Inacessível via Hub. |
| `clear_nvs` | **Apaga integralmente o namespace NVS `feed_pump` e reinicia o ESP32.** Perde calibrações, perfil e PID. | Motor desliga durante o reboot da placa. | **Não** (Bloqueado no Hub). | Exclusivo de bancada física por Serial ou POST local. |

#### B. Definição de Perfil e Temporização (Disparam `startCycle()` se sem `"command"`)

| Chave de Fio | Faixa Válida | Ação no Firmware 3.10 | Ação no Hardware | Repasse no Hub | Uso no Aplicativo |
|---|---|---|---|:---:|---|
| `mode` | $0..5$ | Define o tipo de perfil de dosagem. Mesma numeração repetida força novo ciclo (`startCycle()`), zerando PID e reiniciando $t=0$. Se $0$, vai para `OP_IDLE`; se $\ge 1$, entra em `OP_WAITING` ou `OP_RUNNING`. | Modo 1: Constante.<br>Modo 2: Linear.<br>Modo 3: Exponencial.<br>Modo 4: Polinomial ($\le 20^\circ$ grau).<br>Modo 5: Linear por partes ($2..100$ pts). | Sim | Botão **Enviar perfil**; `{"mode":0}` utilizado para parar o perfil retendo volume. |
| `init_t` | Minutos (float) | Tempo de espera ociosa em `OP_WAITING` antes de bombear. | Motor imóvel até $t \ge \text{init\_t}$. | Sim | Campo "Tempo inicial" na UI. |
| `final_t` | Minutos (float) | Duração total do perfil. Ao atingir, transita autonomamente para `OP_IDLE` e persiste `mode = 0`. Se $0$, roda sem término. | Motor desliga sozinho ao vencer o prazo. | Sim | Campo "Tempo final" (padrão 60 min). |
| `lambda_const`| mL/min | Vazão alvo do perfil constante ($Q = \lambda$). | Rotação estável. | Sim | Perfil Constante. |
| `lambda_linear`, `phi_linear` | mL/min, mL/min² | Coeficientes do perfil em rampa linear ($Q = \lambda + \phi \cdot t$). | Aceleração contínua. | Sim | Perfil Linear. |
| `lambda_exp`, `phi_exp` | mL/min, 1/min | Coeficientes do perfil exponencial ($Q = \lambda \cdot e^{\phi t}$). | Curva de crescimento. | Sim | Perfil Exponencial. |
| `p0` a `p20` | Double | Coeficientes do polinômio ($Q(t) = \sum p_i t^i$). | Dosagem não-linear. | Sim (`polyKeys`) | Perfil Polinomial. |
| `num_segments`, `t0..t99`, `q0..q99` | $2..100$ segm. | Interpolação linear entre pontos tabelados. $t_0$ é forçado a $0$ no modo 5. Mantém último valor após fim da tabela. | Dosagem segmentada. | Sim | Perfil Linear por partes. |

*Regra Arquitetural Rigorosa:* Qualquer envio contendo chaves desta tabela **sem** a presença conjunta de `"command"` dispara a rotina `startCycle()` (novo ciclo, zera PID, limpa checkpoint NVS e zera relógio relativo $t = 0$).

#### C. Acionamento Manual e Portas de Segurança (Não Disparam Reset de Ciclo)

| Chave de Fio | Firmware 3.10 | Ação no Motor | Hub | Aplicativo Windows |
|---|---|---|:---:|---|
| `pump_speed` (Hub) $\rightarrow$ `speed` (Nó) | Clampa em $[-1000, +1000]$. Força `hasUsbSpeed = true`, `OP_IDLE`, `mode = 0`. Volume não é tocado. | Motor gira imediatamente no valor $S$, inclusive negativo (sentido inverso). $S=0$ para o motor. | Sim (`pump_speed`) | Acionamento manual no cartão **Acionamento volumétrico** (Calibrações › Bomba Externa). |
| `pump_speed_ms` (Hub) $\rightarrow$ `speed_ms` (Nó) | Estabelece prazo limite em milissegundos para o comando `speed`. Ao vencer, o laço do firmware zera a velocidade autonomamente. | **Parada autônoma do motor sem depender do aplicativo.** | Sim (`pump_speed_ms`) | Calibração volumétrica envia $\Delta t + 3\text{ s}$ como margem de corte de segurança. |
| `pump_pot` (Hub) $\rightarrow$ `pot` (Nó) | Se `1`: desativa trava manual (`disablePot = false`), esquece velocidade USB (`hasUsbSpeed = false`, `speed = 0`). Se `0`: trava potenciômetros (`disablePot = true`). | Se 1: knobs físicos reassumem o comando do motor. Se 0: knobs ficam inoperantes. | Sim (`pump_pot`) | Botão de alternância **Potenciômetros** em *Controle › Bomba Externa*. |
| `disablePot` | Grafia legada da versão 3.9 (`1` para travar). | Igual a `pot:0`. | **Não** (Fora da whitelist; usar `pump_pot`). | Não utilizado. |
| `sensorEnable` | Habilita o intertravamento do sensor físico de líquido. | Com a porta ativa e mangueira seca, o motor não gira sob nenhuma hipótese. | **Não** | Não exposto no Hub/App. Exclusivo local. |
| `sensorBypass` | Força bypass da porta do sensor mesmo com `sensorEnable == true`. | Motor liberado para girar mesmo com mangueira seca. | **Não** | Não exposto no Hub/App. |
| `sensorButtonOverride` | Se `false`: botão físico manda no sensor. Se `true` (padrão): ignora o botão físico. | Permite travamento do botão externo. | **Não** | Não exposto no Hub/App. |

#### D. Calibração e Sintonia de PID (Efeito Imediato, Persistem na NVS)

| Chave de Fio | Firmware 3.10 | Hub | Aplicativo Windows |
|---|---|:---:|---|
| `pumpSlope`, `pumpIntercept` | Atualiza a reta linear e aciona `g_configDirty = true`. Persiste na NVS no laço seguinte. Efeito instantâneo na conversão $S \leftrightarrow Q$ e na integração do volume no Core 0. | Sim (Pass-through transparente) | Cartão de Calibração: envia os valores e só persiste no PC/grava recibo após constatar eco idêntico na telemetria (tolerância $10^{-4}$). |
| `pumpPidKp`, `pumpPidKi`, `pumpPidKd` | Atualiza os ganhos do controlador de volume. Efeito imediato na dosagem. Ecoados na telemetria do push do nó. | Sim (Traduz para `pid_kp`, `pid_ki`, `pid_kd`) | Expansor de PID de Volume: edição liberada somente após receber o eco do nó. Persiste configuração após eco idêntico (tolerância $5 \cdot 10^{-4}$). |

---

### 2.7 Persistência de Dados e Recuperação Autônoma (§1.7)

1. **Blob de Configuração (`PumpConfig`, tamanho estático $\approx 1,03\text{ kB}$):**
   - Gravado na partição NVS (`feed_pump/config`) sempre que a flag `g_configDirty` é ativada.
   - Armazena modo, tempos, parâmetros de perfil linear/exponencial, 21 coeficientes polinomiais, 100 pontos tabelados, coeficientes de calibração e os três ganhos do PID.
   - **Mecanismo de Validação:** Utiliza uma soma aditiva simples de 32 bits como checksum (`calculateCRC32`). Caso a leitura aponte tamanho discrepante ou checksum inválido, o nó restaura silenciosamente os padrões de fábrica:
     $$\text{slope} = 0,0280188148,\quad \text{intercept} = 1,7601988934,\quad K_p = 0,5,\ K_i = 0,05,\ K_d = 0,001,\ \text{mode} = 0$$
2. **Mecanismo de Checkpoint em Execução (Robust Recovery):**
   - A cada 60 s (`STATE_SAVE_INTERVAL_MS`), **exclusivamente se a bomba estiver em `OP_RUNNING` e com `mode != 0`**, o firmware grava o estado volátil na NVS:
     - `s_active`: flag booleana indicando perfil em andamento.
     - `s_vol`: volume acumulado da sessão até o momento.
     - `s_time`: tempo decorrido do perfil em minutos ($t$).
     - `s_mode`: código do perfil ativo.
     - `s_cvol`: volume inicial do ciclo corrente (`g_cycleStartVolumeMl`, introduzido na versão 3.10).
   - O checkpoint é cancelado e limpo (`s_active = false`) ao receber `stop`, novo perfil/parâmetro, comando `start` ou quando $t \ge \text{final\_t}$.
3. **Comportamento de Retomada Pós-Reset Inesperado:**
   - Ao ligar a alimentação (`firmwareSetup()`), o firmware inspeciona a chave `s_active`.
   - Se ativa, o nó deduz queda brusca de energia durante uma dosagem e **retoma automaticamente o estado `OP_RUNNING`**.
   - O volume de sessão (`g_cumulativeVolumeMl`) e o marco do ciclo (`g_cycleStartVolumeMl`) são reconstituídos, o relógio do perfil é ajustado retroativamente e a dosagem reinicia imediatamente no ponto onde foi interrompida (com defasagem máxima de 60 s).

---

### 2.8 Telemetria Emitida e Mapeamento de Quadro (§1.8)

O nó dispara a cada 1 s (`DATA_PUSH_PERIOD_MS`) uma requisição HTTP `GET /pumpData` com query string serializada, espelhando os mesmos dados pela UART serial a 1 Hz:

| Parâmetro no Push do Nó | Origem Interna no Firmware 3.10 | Propriedade no JSON Agregado do Hub | Propriedade no Windows App | Interpretação no Sistema |
|---|---|---|---|---|
| `mode` | `g_config.mode` | `PumpMode` | `PumpMode` | Modo de perfil em execução ($0..5$). |
| `pwm` | `g_actualPwmDuty` | `PumpPWM` | — (Gaveta de diagnóstico) | Duty PWM real entregue à ponte H ($0$ ou $155..1023$). |
| `speed` | `g_cmdSpeed` | `PumpSpeed` | `PumpSpeed` | Velocidade interna comandada $S \in [-1000, +1000]$. |
| `flow` | `g_currentFlowRateMlMin` | `PumpFlow` | `PumpFlow` | Vazão volumétrica corrente estimada ($\text{mL/min}$). |
| `vol` | `g_cumulativeVolumeMl` | `PumpVol` | `PumpVol` | Volume acumulado na sessão ($\text{mL}$). |
| `v_tgt` | `calculateTargetVolume(t_rel)` | `PumpTargetVol` | `PumpTargetVol` | Volume analítico teórico esperado para o instante $t$. |
| `active` | `(g_opState == OP_RUNNING) ? 1 : 0` | `PumpActive` | `PumpActive` | Indica motor dosando ativamente em perfil. |
| `waiting` | `(g_opState == OP_WAITING) ? 1 : 0` | `PumpWaiting` | `PumpWaiting` | Indica perfil aguardando expirar tempo inicial `init_t`. |
| `ack_cmd_id` | `g_lastAppliedHubCommandId` | Fecha `pumpBox` | `PumpCommandPending = false` | Confirmação de recebimento do comando confiável. |
| `slope` | `g_config.pumpSlope` | `PumpSlope` | `PumpSlope` | Coeficiente angular ativo (confirmação de calibração). |
| `intercept` | `g_config.pumpIntercept` | `PumpIntercept` | `PumpIntercept` | Coeficiente linear ativo (confirmação de calibração). |
| `kp`, `ki`, `kd` | Ganhos do PID em `g_config` | `PumpPidKp/Ki/Kd` | `PumpPidKp/Ki/Kd` | Ganhos ativos no nó; liberam e sincronizam a UI. |
| `pot` | `(disablePot \|\| hasUsbSpeed) ? 0 : 1` | `PumpPotEnabled` | `PumpPotEnabled` | Estado de liberação dos potenciômetros físicos. |
| `cyc_vol` | `vol - g_cycleStartVolumeMl` | `PumpCycleVol` | `PumpCycleVol` | Volume dosado exclusivamente no ciclo de perfil corrente. |

#### Métricas Adicionais Derivadas pelo Hub
- `PumpOnline`: Calculado como `pumpComm && (millis() - pumpLastUpdate <= 4000)`. Se o nó cessar o push por mais de 4 s, o Hub publica `false` e o app aciona o alarme de dispositivo offline.
- `PumpCommEnabled`: Reflete o estado do roteamento no Hub (interruptor `pumpComm`).
- `/nodeDiag?dev=pump`: O Hub consulta assincronamente a cada 30 s o endpoint `/diag` da bomba, extraindo RSSI, heap livre, uptime, contador de falhas com o Hub (`hub_fail_streak`), estado operacional e versão (`3.10`).

#### Grandezas Ausentes na Telemetria Externa (Invisíveis ao Hub e App)
- Estado físico da porta do sensor de presença de líquido (`sensorEnable` e contato de mangueira molhada).
- Posição angular bruta ou sentido dos potenciômetros manuais de bancada.
- Tempo relativo de execução $t$ em minutos (emitido apenas pela serial local ou no `/readData` do AP).

---

### 2.9 Procedimentos do Operador no Aplicativo Windows (§1.9)

| Procedimento | Localização na Interface | Sequência Exata de Quadros JSON Enviados | Critério de Aceitação e Confirmação |
|---|---|---|---|
| **Habilitar Bomba** | *Controle › Bomba Externa*, interruptor mestre. | `{"pumpComm":1}` | `PumpCommEnabled = true`. `PumpOnline = true` em $\le 4\text{ s}$ se o nó estiver ligado. |
| **Enviar Perfil** | *Controle › Bomba Externa*, botão **Enviar perfil**. | Quadro único com `mode`, `init_t`, `final_t` e coeficientes matemáticos do modo selecionado. | Eco de `PumpMode`; ativação de `PumpWaiting` ou `PumpActive`; `PumpCommandPending` retorna a `false` em $\le 3\text{ s}$. |
| **Parar Perfil** | Interruptor mestre desliga, ou botão **Parar**. | **1.º Quadro:** `{"mode":0}` (com `pumpComm` ainda ativo no Hub).<br>**2.º Quadro:** `{"pumpComm":0}` (desativação do roteamento no Hub). | `PumpActive = false`; motor cessa giro; **`PumpVol` é estritamente preservado**. |
| **Parada Segura Global (E-Stop)** | Botão E-Stop ou Árbitro Central de Segurança. | `PumpStopProfile()` $\rightarrow$ despacha `{"mode":0}`. | Parada imediata sem zeramento de acumulador. |
| **Zerar Volume** | Botão **Zerar volume acumulado** (*Controle* e *Calibrações*). | `{"pump_command":"reset_volume"}` | Diálogo de confirmação; asserção não-otimista: `PumpVol < 0,05 mL` confirmada em telemetria em $\le 5\text{ s}$. |
| **Calibração por Coeficientes** | *Calibrações › Bomba Externa*. | `{"pumpSlope":A, "pumpIntercept":B}` | Ecos idênticos em `PumpSlope`/`PumpIntercept` em $\le 15\text{ s}$; gravação do recibo JSON em `Calibracoes/`. |
| **Calibração Volumétrica Assistida** | Cartão "Acionamento volumétrico" em *Calibrações*. | 1. Despacha `{"pump_speed":S, "pump_speed_ms":(Δt + 3000)}`.<br>2. Aguarda $\Delta t$ nominal pelo relógio do PC.<br>3. Despacha parada `{"pump_speed":0}`.<br>4. Operador insere o volume medido na proveta graduada.<br>5. Repete para $S=250, 500, 1000$.<br>6. Ajuste linear calcula $R^2$; botão **Usar ajuste** $\rightarrow$ **Aplicar**. | Geração de recibo de calibração volumétrica contendo pontos ensaiados, resíduos e $R^2$. Ao final, o operador pressiona **Potenciômetros** para devolver os controles à bancada. |
| **Sintonia de PID** | *Controle › Bomba Externa*, expansor "PID de volume do nó". | `{"pumpPidKp":A, "pumpPidKi":B, "pumpPidKd":C}` | UI só libera edição se houver ecos no snapshot. Ao enviar, aguarda eco em $\le 15\text{ s}$ para persistir localmente. |
| **Devolver Potenciômetros** | *Controle › Bomba Externa*, botão **Potenciômetros**. | `{"pump_pot":1}` (ou `0` para travar). | Eco de `PumpPotEnabled` atualiza o estado visual do botão. |
| **Alocação por Árbitro** | Barramento interno do App (`CommandActuators`). | Todas as chaves da bomba mapeadas para `ActuatorId.ExternalPump`. | Rejeição imediata (`DispatchRefusal`) se ensaios de $k_L a$, potência ou receitas automáticas detiverem a posse do periférico. |

---

## 3. Inventário Exaustivo da Seção 1.10: Limitações, Riscos e Decisões

Nesta seção são catalogados todos os achados, decisões de engenharia, limitações e riscos formalizados na auditoria do firmware 3.9 para o 3.10.

### F-110-01 — Estimativa de Volume e Vazão por Curva de Calibração (Não Sensor Físico)
- **Texto Autoritativo Original (§1.10 #1):**
  > *"Volume e vazão são estimados pela curva | Manter; calibrar por volume | Documentado (§1.3); calibração volumétrica no app"*
- **Categoria:** Limitação Física e Metrológica / Decisão de Arquitetura Fechada
- **Implicações Técnicas:** A bomba peristáltica não possui medidor de vazão, sensor óptico de rotação nem sensor de pressão na linha. Todo e qualquer valor de vazão (`PumpFlow`) e volume acumulado (`PumpVol`) reflete a avaliação da reta $Q = \text{slope} \cdot S + \text{intercept}$ integrada numericamente sobre o tempo de condução da ponte H. Desgastes na mangueira, alterações na viscosidade do fluido ou calibrações imprecisas propagam erros diretamente para a dosagem do reator sem sinalização de alarme.
- **Subsistemas Impactados:** Firmware do Nó, Hub ESP32-S3, Aplicativo Windows, Bancada Física.
- **Questões para Investigação no Código-Fonte:**
  1. No firmware (`PwmRuntime.h:47-56`), a taxa de integração numérica $\Delta t$ é obtida com precisão de microsegundos (`micros()`)?
  2. No app (`PumpCalibrationViewModel.cs`), a interface adverte expressamente o operador de que o volume exibido é fruto da curva estimada e fornece rastreabilidade por recibo?

---

### F-110-02 — Preservação de Volume Acumulado no Comando de Parada (`mode:0`)
- **Texto Autoritativo Original (§1.10 #2):**
  > *"`mode:0` zerava o volume | Parar sem zerar; zerar só com `reset_volume` | `startCycle()` substitui o zeramento em `stop`, `mode`, troca de parâmetro, `WAITING→RUNNING` e fim de `final_t`; `vol` é contador de sessão, `cyc_vol` é o do ciclo; checkpoint guarda `s_cvol`"*
- **Categoria:** Decisão de Arquitetura / Integridade de Dados Operacionais
- **Implicações Técnicas:** No firmware legado 3.9, qualquer parada de perfil apagava todo o volume bombeado durante a batelada. A versão 3.10 introduziu a separação conceitual entre o contador acumulativo de sessão (`g_cumulativeVolumeMl`) e o acumulador relativo de ciclo (`g_cycleStartVolumeMl`). `stop`, `mode:0` e trocas de setpoints não apagam a memória de sessão. O zeramento ocorre **exclusivamente via `reset_volume`**.
- **Subsistemas Impactados:** Firmware do Nó, Hub ESP32-S3 (`Commands.h`), Aplicativo Windows (`PumpControlViewModel.cs`).
- **Questões para Investigação no Código-Fonte:**
  1. Em `OperationController.h`, `startCycle()` é invocado em todos os caminhos de término de perfil, garantindo que `g_cumulativeVolumeMl` não seja zerado?
  2. O comando `reset_volume` em `OperationController.h:258-262` zera simultaneamente `g_cumulativeVolumeMl` e `g_cycleStartVolumeMl` sob proteção atômica de interrupções (`taskDISABLE_INTERRUPTS()`)?

---

### F-110-03 — Arbitragem e Desbloqueio dos Potenciômetros Físicos (`pot:1/0`, `pump_pot`)
- **Texto Autoritativo Original (§1.10 #3):**
  > *"`speed` pegajoso: knobs mortos até reboot | Comando para habilitar/desabilitar os potenciômetros | `pot:1/0` no nó (`pump_pot` no Hub); eco `pot` → `PumpPotEnabled`; botão Potenciômetros no app"*
- **Categoria:** Decisão de Engenharia / Recuperação de Controle Local de Bancada
- **Implicações Técnicas:** Na versão 3.9, o acionamento por comando remoto `speed` travava permanentemente a placa na condição `hasUsbSpeed = true`, ignorando os potenciômetros físicos até que o ESP32 fosse reiniciado por corte elétrico. Na versão 3.10, foi criado o comando `pot:1`, traduzido pelo Hub como `pump_pot:1`, que desativa a flag de USB, zera o alvo remoto e devolve o motor ao controle dos knobs manuais.
- **Subsistemas Impactados:** Firmware do Nó, Hub ESP32-S3, Aplicativo Windows.
- **Questões para Investigação no Código-Fonte:**
  1. Em `OperationController.h:317-328`, o envio de `pot:1` redefine `disablePot = false`, `hasUsbSpeed = false`, `usbSpeedSteps = 0.0f` e cancela `g_usbSpeedUntilMs`?
  2. A telemetria em `HubClient.h:188` codifica `pot` como `(disablePot || hasUsbSpeed) ? 0 : 1`, informando com precisão quando os knobs estão de fato ativos?

---

### F-110-04 — Temporização de Velocidade Manual e Parada Autônoma (`speed_ms`, `pump_speed_ms`)
- **Texto Autoritativo Original (§1.10 #4):**
  > *"`speed` sem temporizador | `speed_ms` no firmware (parada autônoma) | `speed_ms` opcional; laço zera a velocidade ao vencer; a calibração envia Δt + 3 s (a parada primária continua no app, para o Δt medido ser o do app)"*
- **Categoria:** Decisão de Segurança Operacional / Resiliência Contra Queda de Link
- **Implicações Técnicas:** Caso o aplicativo enviasse um comando de rotação manual (`speed`) e o link Wi-Fi ou a aplicação colapsasse antes do envio de parada (`speed:0`), o motor continuaria girando eternamente até transbordar o recipiente. O firmware 3.10 implementou o parâmetro opcional `speed_ms`. O nó gerencia internamente o deadline (`g_usbSpeedUntilMs`) e corta a velocidade no laço principal se o tempo expirar.
- **Subsistemas Impactados:** Firmware do Nó, Hub ESP32-S3, Aplicativo Windows (`PumpCalibrationViewModel.cs`).
- **Questões para Investigação no Código-Fonte:**
  1. Em `Lifecycle.h:138-142`, a checagem de timeout `(long)(now - g_usbSpeedUntilMs) >= 0` é imune a overflow da contagem de `millis()`?
  2. No app (`PumpCalibrationViewModel.cs`), a rotina de calibração volumétrica anexa $\Delta t + 3000\text{ ms}$ em `CommandKeys.PumpManualSpeedMs` em todos os pulsos de dosagem?

---

### F-110-05 — Proteção de Memória Flash NVS no Hub e Filtragem de Comandos Críticos
- **Texto Autoritativo Original (§1.10 #5):**
  > *"`clear_nvs` passava pelo Hub | Filtrar no Hub: só `reset_volume`, `start`, `stop` | `allowedPumpCommands[]` em `Commands.h`; recusa com `ESP32_AVISO`; teste de contrato"*
- **Categoria:** Decisão de Segurança Sistêmica / Proteção de Integridade de Hardware
- **Implicações Técnicas:** No baseline anterior, comandos perigosos como `clear_nvs`, `save_config` e `load_config` podiam ser injetados pela rede Wi-Fi através do Hub. O Hub 10.2 instituiu a lista branca restritiva `allowedPumpCommands[] = { "reset_volume", "start", "stop" }`. Qualquer outro verbo disparado em `pump_command` é bloqueado com mensagem de aviso `ESP32_AVISO`.
- **Subsistemas Impactados:** Hub ESP32-S3 (`Commands.h`), Firmware do Nó.
- **Questões para Investigação no Código-Fonte:**
  1. Em `Commands.h:596-614`, comandos arbitrários em `pump_command` fora da lista branca são barrados sem serem inseridos na string de despacho para o nó?
  2. Existe teste automatizado de contrato (`test_node_commands.py`) validando expressamente a recusa de `clear_nvs`?

---

### F-110-06 — Retomada Autônoma de Perfil Pós-Queda de Energia (Checkpoint NVS)
- **Texto Autoritativo Original (§1.10 #6):**
  > *"Retomada automática após queda de energia | Mantida (batelada longa); registrar no procedimento de energização | `s_cvol` entra no checkpoint para a retomada não quebrar o ciclo"*
- **Categoria:** Decisão de Engenharia de Processo / Risco Operacional Físico
- **Implicações Técnicas:** Em bioprocessos longos (bateladas de semanas), uma queda transitória de energia no laboratório não deve descartar o cultivo. O firmware 3.10 mantém o comportamento de gravar checkpoints a cada 60 s durante `OP_RUNNING`. Ao religar, a bomba volta a girar sozinha sem esperar intervenção do Hub ou do operador. **Risco:** O operador de bancada pode ser surpreendido pelo motor girando imediatamente ao plugar a fonte na tomada se a bomba foi desenergizada abruptamente durante um ensaio.
- **Subsistemas Impactados:** Firmware do Nó (`RuntimeStateStore.h`), Procedimentos Operacionais de Bancada.
- **Questões para Investigação no Código-Fonte:**
  1. Em `RuntimeStateStore.h:1-39`, a recuperação restaura fidedignamente `s_cvol` em `g_cycleStartVolumeMl` e reconstitui o relógio `g_opTriggerTimeMs`?
  2. O procedimento de segurança e energização em laboratório documenta o risco de rotação autônoma imediata pós-boot?

---

### F-110-07 — Porta de Intertravamento do Sensor de Líquido Invisível ao Aplicativo
- **Texto Autoritativo Original (§1.10 #7):**
  > *"Porta do sensor invisível ao app | Adiado | Sem mudança; `sensorEnable`/`sensorBypass` continuam só locais"*
- **Categoria:** Limitação de Visibilidade / Decisão Diferida (Adiada)
- **Implicações Técnicas:** O hardware da bomba possui uma entrada para sensor físico de presença de líquido (chave booleana que impede giro a seco). No entanto, nem o estado da porta (`sensorEnable`) nem a condição física de mangueira molhada (`sensorWetState`) são empurrados para a telemetria do Hub. Se a bomba estiver com o intertravamento habilitado localmente e a mangueira secar, a bomba para de dosar mas o aplicativo Windows continua reportando perfil ativo, sem diagnosticar o bloqueio.
- **Subsistemas Impactados:** Firmware do Nó, Hub ESP32-S3, Aplicativo Windows.
- **Questões para Investigação no Código-Fonte:**
  1. Em `TelemetryCodec.h` e `HubClient.h`, as variáveis `sensorEnable` e `sensorWetState` foram omitidas da query string `/pumpData`?
  2. Qual é a estratégia futura para expor essa telemetria sem romper a restrição de tamanho do quadro agregado do Hub?

---

### F-110-08 — Eco Bidirecional e Ajuste Dinâmico de PID de Volume
- **Texto Autoritativo Original (§1.10 #8):**
  > *"PID sem eco, UI travada | Ecoar e receber PID; padrões no app | `kp/ki/kd` no push e no `/readData`; Hub `PumpPidKp/Ki/Kd`; app libera edição com eco, persiste ao eco, padrões 0,5/0,05/0,001"*
- **Categoria:** Decisão de Protocolo e Interface / Integridade de Controle
- **Implicações Técnicas:** Na versão 3.9, os parâmetros do PID podiam ser alterados pelo comando `pid_kp/ki/kd`, mas o nó não publicava os ganhos correntes de volta. O aplicativo Windows mantinha o painel travado em cinza ou desconhecido. Na versão 3.10, o firmware ecoa `kp`, `ki` e `kd` no push HTTP e serial. O Hub mapeia para `PumpPidKp/Ki/Kd` e a interface do Windows App desbloqueia os seletores somente após constatar a presença de ecos válidos.
- **Subsistemas Impactados:** Firmware do Nó, Hub ESP32-S3, Aplicativo Windows.
- **Questões para Investigação no Código-Fonte:**
  1. Em `HttpServer.h:376-378`, o Hub lê `kp`, `ki`, `kd` sob mutex e publica no agregador `Telemetry.h:341-343`?
  2. No app (`PumpControlViewModel.cs`), a propriedade de habilitação de edição do PID valida estritamente a não-nulidade dos ecos?

---

### F-110-09 — Latência de Enfileiramento e Política Latest-Wins na Caixa Postal (`pumpBox`)
- **Texto Autoritativo Original (§1.10 #9):**
  > *"Latência 0–2 s + latest-wins na `pumpBox` | Manter; app já serializa por `PumpCommandPending` | —"*
- **Categoria:** Limitação Arquitetural / Comportamento Assíncrono do Canal
- **Implicações Técnicas:** Como o nó consulta o Hub via polling a cada 2 s (`HUB_POLL_PERIOD_MS`), a latência típica para recepção de um comando varia entre 0 e 2 s, acrescida de até 1 s para o retorno do ACK na telemetria. A caixa `pumpBox` opera em regime *latest-wins* (profundidade unitária): se o aplicativo despachar múltiplos comandos em sequência rápida antes da confirmação, os comandos intermediários serão descartados. A serialização rigorosa pelo aplicativo via `PumpCommandPending` é mandatória.
- **Subsistemas Impactados:** Hub ESP32-S3 (`Mailboxes.h`), Aplicativo Windows (`PumpControlViewModel.cs`).
- **Questões para Investigação no Código-Fonte:**
  1. Em `PumpControlViewModel.cs`, todo envio de perfil, velocidade ou parada bloqueia a interface até que `PumpCommandPending` retorne a falso ou ocorra timeout de 20 s?
  2. O simulador (`WireCodec.cs`) modela fielmente a semântica de latência e confirmação de ACK da caixa postal?

---

### F-110-10 — Comportamento de Ativação Ociosa com Comando `start` e `mode=0`
- **Texto Autoritativo Original (§1.10 #10):**
  > *"`start` com `mode = 0` marca `active` sem bombear | Manter (app não usa `start`) | —"*
- **Categoria:** Comportamento Marginal / Decisão de Manutenção de Compatibilidade
- **Implicações Técnicas:** Se for injetado o comando legado `{"command":"start"}` quando a bomba estiver configurada com `mode == 0`, o firmware executa `g_opState = OP_RUNNING`. A telemetria passa a reportar `active = 1` (`PumpActive = true`), porém o cálculo de vazão para o modo 0 resulta em $Q = 0$, mantendo o motor totalmente parado. O Windows App não emite `start`, utilizando exclusivamente o envio de perfis com `mode >= 1` ou parada via `mode:0`.
- **Subsistemas Impactados:** Firmware do Nó (`OperationController.h:242-250`).
- **Questões para Investigação no Código-Fonte:**
  1. Confirmar se `Windows_app` possui algum ponto onde emita `{"command":"start"}` para a bomba.
  2. Avaliar se o firmware deveria validar `if (g_config.mode > 0)` antes de promover o estado para `OP_RUNNING`.

---

### F-110-11 — Esclarecimento de Hardware: Motor DC Escovado em Ponte H vs Stepper
- **Texto Autoritativo Original (§1.10 #11):**
  > *"Motor: CAD com stepper | É DC (confirmado) | Doc corrigida (§1.1)"*
- **Categoria:** Restrição de Hardware e Alinhamento de Documentação
- **Implicações Técnicas:** Documentos antigos e desenhos do CAD Watson-Marlow indicavam a presença de um motor de passo NEMA 17 (17HS4401). Foi confirmado em auditoria física de bancada em 2026-09-12 que a bomba instalada utiliza motor DC escovado comandado por ponte H PWM (BTS7960/IBT-2). Todas as menções a passos por volta, microstepping ou aceleração por pulsos discretos foram invalidadas.
- **Subsistemas Impactados:** Documentação Técnica, Firmware do Nó (`PwmRuntime.h`), Bancada Física.
- **Questões para Investigação no Código-Fonte:**
  1. A documentação em `External-Devices/bomba-peristaltica/docs/` ainda possui arquivos com terminologia de motor de passo que necessitem de expurgo?

---

### F-110-12 — Esclarecimento de Hardware: Potenciômetro "Gain" como Sentido e Escalonamento Bipolar
- **Texto Autoritativo Original (§1.10 #—):**
  > *"Potenciômetro 'gain' | É sentido e ganho sobre a velocidade do outro knob | Doc corrigida (§1.1)"*
- **Categoria:** Restrição de Hardware e Alinhamento de Comportamento Analógico
- **Implicações Técnicas:** O segundo potenciômetro do nó (`POT_GAIN` no GPIO 35) não é um mero ajuste fino de ganho unidirecional. Ele opera como chave bipolar centrada: a posição média ($\text{ADC} \approx 2047$) estipula fator zero (motor parado). O giro para a esquerda inverte o sentido de rotação da bomba (pulsando `L_PWM`), enquanto o giro para a direita impulsiona o sentido direto (pulsando `R_PWM`), escalando linearmente a magnitude dada por `POT_INT`.
- **Subsistemas Impactados:** Firmware do Nó (`OperationController.h:191-204`), Procedimentos de Bancada.
- **Questões para Investigação no Código-Fonte:**
  1. Em `OperationController.h`, a zona morta em torno do centro do ADC ($\pm 50$ counts) é suficiente para prevenir partida espúria do motor por ruído analógico quando o knob está em repouso central?

---

### Diretrizes de Segurança Adicionais e Decisões de Arquitetura Fechadas (§1.0 e §1.1–§1.9)

#### D-SEC-01 — Diretriz de Calibração: Curva Linear vs Tabela de Consulta (Lookup Table)
- **Texto Autoritativo Original (§1.0 🔵 e §1.3):**
  > *"A bomba peristáltica tem resposta predominantemente linear ($R^2 > 0,99$). A calibração assistida do app calcula a reta e resíduos sobre múltiplos pontos ($S = 250, 500, 1000$). Pequenos desvios dinâmicos são corrigidos pelo PID de volume do nó. Diretriz: manter calibração linear atual; só evoluir para tabela de lookup se a bancada física demonstrar $R^2 < 0,98$."*
- **Categoria:** Decisão de Arquitetura de Controle / Modelagem Matemática
- **Implicações Técnicas:** O modelo linear ($Q = \text{slope} \cdot S + \text{intercept}$) possui baixíssimo custo computacional e estabilidade analítica comprovada. A adição de tabelas de interpolação não-lineares no nó foi formalmente congelada até que ensaios físicos em bancada comprovem $R^2 < 0,98$.

#### D-SEC-02 — Modo de Ativação Física por Presença de Líquido e Prioridade Manual Local
- **Texto Autoritativo Original (§1.0 🔵 e §1.1):**
  > *"Modo de segurança e operação física ativado por botão no hardware. Com o botão acionado, a bomba opera exclusivamente em modo manual local (liga/desliga por contato com líquido, na velocidade e sentido dos potenciômetros) e não deve receber comandos externos de perfil."*
- **Categoria:** Decisão de Segurança Operacional / Intertravamento Físico Local
- **Implicações Técnicas:** Prioridade absoluta de segurança do operador local de bancada sobre o software supervisor. Quando acionado o modo de contato por líquido via botão físico, comandos remotos de perfil são suprimidos.

#### R-SYS-01 — Proteção Contra Divisão por Zero com Slope Nulo
- **Texto Autoritativo Original (§1.2):**
  > *"Com `|slope| < 1e-6` o firmware devolve S = 0 (não divide por zero) — uma calibração com slope zero para a bomba em qualquer perfil."*
- **Categoria:** Risco Sistêmico de Travamento / Resiliência Numérica
- **Implicações Técnicas:** Protege o nó contra exceções de ponto flutuante, mas tem como consequência imobilizar a bomba se uma calibração inválida for aplicada.

#### R-SYS-02 — Patamar de Atrito Estático e Descontinuidade de Partida (`PWM_BREAKAWAY = 155`)
- **Texto Autoritativo Original (§1.2):**
  > *"duty útil 155..1023 (PWM_BREAKAWAY = 155: abaixo disso o motor não vence o atrito do cabeçote). S = 1 já é 155/1023 ≈ 15 % de duty, não 'quase parado'."*
- **Categoria:** Limitação Eletromecânica / Risco de Não-Linearidade em Baixas Vazões
- **Implicações Técnicas:** Descontinuidade na partida. Não é possível dosar vazões ultrabaixas que demandem duty inferior a 155 em regime contínuo.

#### R-SYS-03 — Trava Mínima de Estabilidade de Acionamento (`MIN_MOTOR_ON_TIME_MS = 500 ms`)
- **Texto Autoritativo Original (§1.2):**
  > *"Trava de 500 ms (MIN_MOTOR_ON_TIME_MS): ao ligar, o motor mantém a velocidade de partida por ao menos 500 ms antes de aceitar uma nova. Evita 'tremer' quando o alvo oscila perto de S = 1."*
- **Categoria:** Decisão de Amortecimento Eletromecânico
- **Implicações Técnicas:** Introduz um atraso intencional de 500 ms na dinâmica de resposta do motor na partida para proteger os enrolamentos e acoplamentos mecânicos.

#### R-SYS-04 — Verificação de Integridade de Configuração NVS por Soma Simples
- **Texto Autoritativo Original (§1.7):**
  > *"O 'CRC32' é uma soma simples de bytes; detecta corrupção grosseira, não troca de layout. Um blob de tamanho diferente (outro firmware) volta aos padrões: slope 0,0280188148, intercept 1,7601988934, PID 0,5/0,05/0,001, mode 0."*
- **Categoria:** Risco de Persistência NVS / Integridade de Dados
- **Implicações Técnicas:** A função `calculateCRC32` soma os bytes da struct `PumpConfig`. Se o layout da struct sofrer alteração com o mesmo tamanho total, a corrupção estrutural pode não ser detectada.

#### R-SYS-05 — Exposição de Interface Web Local sem Autenticação
- **Texto Autoritativo Original (§1.5):**
  > *"POST /command no AP local (192.168.6.1): corpo JSON; não — aplicado sempre; Sem autenticação; útil na bancada."*
- **Categoria:** Risco de Segurança de Acesso / Interface Aberta
- **Implicações Técnicas:** Qualquer cliente conectado à rede Wi-Fi `FeedPump` pode injetar comandos arbitrários no motor sem controle de sessão ou autenticação.

#### R-SYS-06 — Bloqueio do Laço de Execução Durante Upload OTA e Timeout de WDT
- **Texto Autoritativo Original (§1.5):**
  > *"GET /update (OTA): página HTML + upload .bin; Laço principal para durante o upload; watchdog de 90 s sem chunk aborta."*
- **Categoria:** Risco de Interrupção de Processo / Bloqueio Temporário de Controle
- **Implicações Técnicas:** Durante a atualização de firmware pela interface web local, todo o laço de dosagem e monitoramento é suspenso. A atualização deve ser expressamente vedada durante bateladas ativas.

#### NOTE-COMPAT-01 — Compatibilidade Retroativa e Degradação Graciosa
- **Texto Autoritativo Original (§1.10 Nota de Rodapé):**
  > *"Compatibilidade: tudo é aditivo. Um Hub anterior ignora `kp/ki/kd/pot/cyc_vol`; uma bomba 3.9 ignora `speed_ms` e `pot` (nela `speed` continua pegajoso e `mode:0` continua zerando — o app mostra o PID travado e 'estado dos potenciômetros desconhecido' nesse caso)."*
- **Categoria:** Diretriz de Interoperabilidade e Degradação Graciosa
- **Implicações Técnicas:** O Hub 10.2 e o app suportam nós rodando firmware 3.9 sem falhas fatais, operando em modo degradado transparente.

---

## 4. Inventário Exaustivo da Seção 1.11: Checklist de Bancada Física

Catalogação integral dos 11 testes práticos de bancada estipulados para aprovação física e comissionamento do nó da Bomba Peristáltica.

### CHK-111-01 — Conferência do Sentido Físico de Rotação (Fluxo para o Vaso)
- **Texto Autoritativo Original (§1.11 #1):**
  > *"- [ ] Conferir sentido positivo = fluxo para o vaso (motor DC confirmado)."*
- **Objetivo do Teste:** Assegurar que comando de velocidade com sinal positivo ($S > 0$) e acionamentos de perfis rotacionem o cabeçote peristáltico no sentido que bombeia o líquido em direção ao vaso do biorreator (e não retire líquido do vaso).
- **Pré-requisitos e Arranjo:** Nó conectado à fonte DC, motor em ponte H conectado aos pinos 14, 25, 26, 27, mangueira montada com água destilada ou meio inerte, vaso conectado na extremidade de saída.
- **Passo a Passo:**
  1. Habilitar a bomba pelo aplicativo Windows.
  2. No cartão de Acionamento Volumétrico, configurar $S = 500$, tempo de 10 s e clicar em **Acionar bomba**.
  3. Observar visualmente a rotação do rotor de 5 roletes e o deslocamento da coluna de líquido.
- **Comportamento Esperado e Critério de Aceitação:** O rotor deve girar no sentido horário/anti-horário correspondente à compressão mecânica que empurra o fluido em direção à cânula de alimentação do vaso. O sinal elétrico em `R_PWM` deve estar ativo e `L_PWM` em nível baixo.
- **Subsistemas Impactados:** Firmware do Nó (`PwmRuntime.h`), Hardware (Fiação e Polaridade do Motor).
- **Questões para Investigação no Código:** A polaridade de rotação está hardcodeada em `dirPos = (s >= 0.0f)`? É necessária flag configurável de inversão de sentido na NVS?

---

### CHK-111-02 — Tempo de Resposta e Detecção de Queda de Presença ($\le 4\text{ s}$)
- **Texto Autoritativo Original (§1.11 #2):**
  > *"- [ ] `pumpComm:1` → `PumpOnline` em ≤ 4 s; desligar o nó → `PumpOnline=false` em ≤ 4 s; alarme 'Bomba externa offline' no app."*
- **Objetivo do Teste:** Validar o mecanismo de heartbeat e o limiar do watchdog de presença do Hub e do aplicativo.
- **Pré-requisitos e Arranjo:** Hub 10.2 ativo, aplicativo Windows conectado em tempo real, nó da bomba energizado e conectado ao Wi-Fi.
- **Passo a Passo:**
  1. No app (*Controle › Bomba Externa*), ligar o interruptor mestre (`pumpComm:1`).
  2. Cronometrar o tempo até o chip `PumpOnline` passar para verde.
  3. Desconectar o cabo de alimentação da bomba.
  4. Cronometrar o tempo até o chip passar para vermelho e o alarme disparar.
- **Comportamento Esperado e Critério de Aceitação:**
  - `PumpOnline = true` ativado em no máximo 4 s após `pumpComm:1`.
  - Ao cortar a alimentação, `PumpOnline = false` publicado pelo Hub em no máximo 4 s.
  - Aplicativo Windows exibe de imediato o alarme visual *"Bomba externa offline"*.
- **Subsistemas Impactados:** Hub ESP32-S3 (`Telemetry.h`), Aplicativo Windows (`TelemetryParser.cs`, `Alarms.cs`).
- **Questões para Investigação no Código:** Confirmar o valor exato da constante `PUMP_PRESENCE_TIMEOUT` em `HttpServer.h` / `Telemetry.h` (4000 ms).

---

### CHK-111-03 — Calibração Volumétrica Multiponto e Verificação Contínua de 10 min
- **Texto Autoritativo Original (§1.11 #3):**
  > *"- [ ] Calibração volumétrica: S = 250/500/1000 × 60 s, réplica em 500; R² e resíduos; aplicar; conferir 10 min de perfil constante contra o recipiente (volume real × `PumpVol`)."*
- **Objetivo do Teste:** Comprovar a linearidade da bomba, calibrar a reta experimental e aferir a precisão da dosagem contínua contra a predição da telemetria.
- **Pré-requisitos e Arranjo:** Bancada com proveta graduada (resolução $\le 0,5\text{ mL}$), mangueira preenchida e escorvada, cronômetro, balança (opcional para conferência).
- **Passo a Passo:**
  1. No aplicativo Windows (*Calibrações › Bomba Externa*), realizar pulsos de dosagem de 60 s para $S = 250$, $S = 500$ (com réplica) e $S = 1000$.
  2. Registrar os volumes medidos e verificar se $R^2 \ge 0,99$.
  3. Clicar em **Usar ajuste** e depois em **Aplicar calibração**.
  4. Iniciar um perfil constante de 10 min ($Q = 10\text{ mL/min}$, `final_t = 10`).
  5. Ao término, comparar o volume real acumulado na proveta com o valor reportado por `PumpVol`.
- **Comportamento Esperado e Critério de Aceitação:** O ajuste linear deve gerar $R^2 \ge 0,99$. O volume real medido na proveta após 10 min deve divergir em menos de $3\%$ do `PumpVol` integrado na tela.
- **Subsistemas Impactados:** Aplicativo Windows (`PumpCalibrationViewModel.cs`), Firmware do Nó (`SensorAndConversion.h`).
- **Questões para Investigação no Código:** Como o app lida com os resíduos de cada ponto e formata o recibo JSON gerado?

---

### CHK-111-04 — Liberação e Comutação dos Potenciômetros de Bancada
- **Texto Autoritativo Original (§1.11 #4):**
  > *"- [ ] Após a calibração, **Potenciômetros** no app devolve os knobs (`PumpPotEnabled=true`) e a velocidade manual é esquecida; `pump_pot:0` trava."*
- **Objetivo do Teste:** Validar a devolução do comando do motor aos botões físicos locais após um ciclo de calibração ou comando remoto.
- **Pré-requisitos e Arranjo:** Nó conectado, bancada com operador operando fisicamente os potenciômetros.
- **Passo a Passo:**
  1. Executar um acionamento manual remoto via aplicativo.
  2. Verificar que os botões físicos do nó não alteram a rotação do motor.
  3. No app, clicar no botão **Potenciômetros** (`pump_pot:1`).
  4. Girar o potenciômetro físico `POT_INT` e verificar a resposta do motor.
  5. No app, desativar os potenciômetros (`pump_pot:0`) e verificar se os knobs travam novamente.
- **Comportamento Esperado e Critério de Aceitação:** Ao enviar `pump_pot:1`, a telemetria reporta `PumpPotEnabled = true` em $\le 3\text{ s}$, e o motor passa a responder imediatamente e de forma suave ao giro dos potenciômetros físicos. O envio de `pump_pot:0` congela a rotação.
- **Subsistemas Impactados:** Firmware do Nó (`OperationController.h:317-328`), Hub ESP32-S3, Windows App.
- **Questões para Investigação no Código:** Confirmar a sincronização bidirecional do botão na interface do Windows App com a propriedade `PumpPotEnabled`.

---

### CHK-111-05 — Corte Autônomo de Emergência por Timeout (`pump_speed_ms`)
- **Texto Autoritativo Original (§1.11 #5):**
  > *"- [ ] Acionamento com `pump_speed_ms` e app desconectado a meio: o nó para sozinho ao vencer o prazo."*
- **Objetivo do Teste:** Comprovar que a bomba interrompe a rotação autonomamente por segurança caso o cliente perca a comunicação durante um acionamento manual.
- **Pré-requisitos e Arranjo:** Nó conectado via Wi-Fi ou USB, aplicativo pronto para disparar comando de velocidade com temporizador.
- **Passo a Passo:**
  1. Disparar via aplicativo ou script um comando de rotação manual com deadline: `{"pump_speed":600, "pump_speed_ms":5000}`.
  2. No instante $t = 2\text{ s}$, desconectar bruscamente o cabo de rede/desligar o AP do Hub/fechar o aplicativo.
  3. Observar visualmente o comportamento do motor no instante $t = 5\text{ s}$.
- **Comportamento Esperado e Critério de Aceitação:** O motor deve parar de girar exatamente no instante em que expiram os 5000 ms no relógio interno do nó, mesmo sem receber nenhum comando adicional pela rede.
- **Subsistemas Impactados:** Firmware do Nó (`Lifecycle.h:138-142`).
- **Questões para Investigação no Código:** O laço imprime no log serial `[CMD] speed_ms elapsed; motor stopped.` quando ocorre a parada por prazo expirado?

---

### CHK-111-06 — Parada Autônoma por `final_t`, Retenção de `PumpVol` e Apuração de `PumpCycleVol`
- **Texto Autoritativo Original (§1.11 #6):**
  > *"- [ ] Perfil constante 5 min com `final_t` = 5: parada autônoma, `mode` volta a 0, `PumpVol` **mantido**, `PumpCycleVol` = volume do ciclo; `reset_volume` zera os dois."*
- **Objetivo do Teste:** Validar a finalização autônoma do perfil por tempo, a preservação do volume da sessão e o cálculo do volume do ciclo.
- **Pré-requisitos e Arranjo:** Bomba dosando líquido em regime de teste.
- **Passo a Passo:**
  1. Enviar perfil constante com `init_t = 0`, `final_t = 5` min, $\lambda = 10\text{ mL/min}$.
  2. Aguardar 5 minutos e observar a transição de estado.
  3. Verificar os valores finais de `PumpActive`, `PumpMode`, `PumpVol` e `PumpCycleVol`.
  4. Clicar no botão **Zerar volume acumulado**.
- **Comportamento Esperado e Critério de Aceitação:**
  - Aos 5 min, o motor desliga sozinho; `PumpActive` passa a `false`; `PumpMode` retorna a `0`.
  - `PumpVol` permanece retido exibindo $\approx 50\text{ mL}$.
  - `PumpCycleVol` exibe rigorosamente o volume dosado naquele ciclo ($\approx 50\text{ mL}$).
  - Ao enviar `reset_volume`, tanto `PumpVol` quanto `PumpCycleVol` caem para zero ($< 0,05\text{ mL}$).
- **Subsistemas Impactados:** Firmware do Nó (`OperationController.h`), Hub ESP32-S3, Aplicativo Windows.
- **Questões para Investigação no Código:** Como o Hub lida com `PumpCycleVol` no agregador caso o nó seja v3.9 (ausente no push)?

---

### CHK-111-07 — Execução Sequencial de Múltiplos Perfis sem Zeramento de Volume
- **Texto Autoritativo Original (§1.11 #7):**
  > *"- [ ] Dois perfis em sequência sem `reset_volume`: `PumpVol` acumula; PID do segundo ciclo não reage ao volume do primeiro."*
- **Objetivo do Teste:** Assegurar que o volume acumulado da sessão cresça monotonicamente entre múltiplos perfis e que o PID do segundo perfil opere com erro restrito ao ciclo vigente.
- **Pré-requisitos e Arranjo:** Bomba operando em bancada.
- **Passo a Passo:**
  1. Executar Perfil 1 (ex: 2 min, $10\text{ mL/min}$, entrega $\approx 20\text{ mL}$).
  2. Aguardar a parada do motor. Conferir `PumpVol` ($\approx 20\text{ mL}$).
  3. Sem acionar `reset_volume`, disparar Perfil 2 (ex: 2 min, $10\text{ mL/min}$).
  4. Monitorar o comportamento do PID no log serial do nó (`[DEBUG] TgtV: ... ActV: ...`).
- **Comportamento Esperado e Critério de Aceitação:**
  - `PumpVol` inicia o segundo perfil em $20\text{ mL}$ e atinge $\approx 40\text{ mL}$ ao final.
  - `PumpCycleVol` reinicia em $0\text{ mL}$ no segundo perfil e termina em $\approx 20\text{ mL}$.
  - O cálculo do PID compara $V_{\text{alvo}}$ com $V_{\text{ciclo}}$ e **não** com o acumulador total, impedindo saturação prematura do termo integrador.
- **Subsistemas Impactados:** Firmware do Nó (`OperationController.h:63-75`).
- **Questões para Investigação no Código:** Em `runOperationLogic()`, a subtração `V_actual_ml -= g_cycleStartVolumeMl` é devidamente protegida contra concorrência do Core 0?

---

### CHK-111-08 — Transmissão, Eco e Retenção NVS dos Ganhos de PID
- **Texto Autoritativo Original (§1.11 #8):**
  > *"- [ ] Enviar PID pelo app: eco em ≤ 3 s, persistido; reboot do nó mantém os ganhos (blob NVS)."*
- **Objetivo do Teste:** Comprovar a integridade da comunicação bidirecional de sintonia de PID e a persistência não-volátil na flash.
- **Pré-requisitos e Arranjo:** Nó conectado ao Hub e ao aplicativo Windows.
- **Passo a Passo:**
  1. No app (*Controle › Bomba Externa*), abrir o expansor de PID.
  2. Alterar os ganhos para valores distintos dos padrões (ex: $K_p = 0,85$, $K_i = 0,08$, $K_d = 0,005$).
  3. Clicar em **Enviar PID**.
  4. Observar a confirmação pelo eco na telemetria.
  5. Desligar a alimentação da bomba, aguardar 5 s e religar.
- **Comportamento Esperado e Critério de Aceitação:**
  - Ecos `PumpPidKp/Ki/Kd` atualizam na interface em $\le 3\text{ s}$ refletindo os novos ganhos.
  - O aplicativo salva a configuração local somente após o eco compatível.
  - Após o reboot da bomba, os mesmos ganhos sintonizados são recarregados da NVS e publicados no primeiro push.
- **Subsistemas Impactados:** Firmware do Nó (`ConfigStore.h`), Hub ESP32-S3, Aplicativo Windows (`PumpControlViewModel.cs`).
- **Questões para Investigação no Código:** Como `ConfigStore.h:8-18` valida o checksum CRC32 do blob de configuração ao carregar no boot?

---

### CHK-111-09 — Ensaio de Retomada Autônoma Pós-Queda de Alimentação (Robust Recovery)
- **Texto Autoritativo Original (§1.11 #9):**
  > *"- [ ] Desligar energia no meio de um perfil e religar: confirmar retomada automática (item 6) e decidir se é o comportamento desejado."*
- **Objetivo do Teste:** Validar a funcionalidade de recuperação autônoma de perfil contínuo pós-reset inesperado e colher parecer da engenharia de bancada.
- **Pré-requisitos e Arranjo:** Bomba dosando líquido em bancada sob perfil contínuo de longa duração.
- **Passo a Passo:**
  1. Iniciar um perfil de 30 min ($Q = 10\text{ mL/min}$).
  2. Aguardar transcorrer mais de 60 s (para garantir a gravação do checkpoint em NVS).
  3. Puxar bruscamente o plugue da tomada no instante $t \approx 5\text{ min}$.
  4. Aguardar 10 s e religar o plugue na tomada.
  5. Observar o log serial de boot e a ação física do motor.
- **Comportamento Esperado e Critério de Aceitação:**
  - O firmware exibe no log serial: `>>> DETECTED UNEXPECTED RESET! RECOVERING STATE <<<`.
  - O motor volta a girar imediatamente sem receber nenhum comando do Hub.
  - O volume acumulado e o tempo decorrido retomam com defasagem inferior a 60 s.
  - Avaliar se a equipe de bioprocessos prefere manter a retomada autônoma ou adicionar intertravamento de segurança.
- **Subsistemas Impactados:** Firmware do Nó (`RuntimeStateStore.h:1-39`), Procedimentos de Segurança.
- **Questões para Investigação no Código:** Há risco de corrupção da partição NVS caso o corte de energia ocorra exatamente durante a execução de `g_prefs.putFloat()`?

---

### CHK-111-10 — Injeção de Comando Pós-Reboot do Hub (Semeadura de `cmd_id`)
- **Texto Autoritativo Original (§1.11 #10):**
  > *"- [ ] Reiniciar o Hub com a bomba ligada e enviar `reset_volume`: deve ser aplicado (semeadura de `cmd_id`, Hub 2026-09-12)."*
- **Objetivo do Teste:** Validar a correção do bug de sincronismo de comandos confiáveis após reinicialização espúria da Central Hub.
- **Pré-requisitos e Arranjo:** Nó da bomba ligado e operando, Hub 10.2 conectado e aplicativo sincronizado.
- **Passo a Passo:**
  1. Enviar comandos para a bomba garantindo que `ack_cmd_id` seja maior que zero.
  2. Reiniciar o Hub via botão de reset físico ou comando `/reboot`, mantendo o nó da bomba ligado.
  3. Aguardar o Hub subir a rede e o nó se reconectar.
  4. Pelo app, enviar imediatamente o comando **Zerar volume acumulado** (`reset_volume`).
- **Comportamento Esperado e Critério de Aceitação:** O comando deve ser entregue e aplicado com sucesso no nó da bomba na primeira tentativa. O Hub semeia um `cmd_id` pseudoaleatório ao reiniciar (`seedReliableMailboxes()`), impedindo que o primeiro comando seja falsamente dado como confirmado.
- **Subsistemas Impactados:** Hub ESP32-S3 (`Mailboxes.h`, `Runtime.h`), Firmware do Nó (`HubClient.h:226-237`).
- **Questões para Investigação no Código:** Verificar a implementação de `seedReliableMailboxes()` em `Mailboxes.h` e o teste `test_reliable_mailboxes_are_seeded_per_hub_boot`.

---

### CHK-111-11 — Resposta do Sensor de Presença de Líquido e Travamento de Bancada
- **Texto Autoritativo Original (§1.11 #11):**
  > *"- [ ] Com `sensorEnable:1` por `POST /command` local e tubo seco: motor parado; molhar: parte em ≤ 50 ms + 500 ms de trava."*
- **Objetivo do Teste:** Validar a integridade funcional do circuito elétrico e lógica de debounce do sensor de líquido nos pinos 15, 32 e 33.
- **Pré-requisitos e Arranjo:** Nó da bomba alimentado, terminal de bancada acessando `http://192.168.6.1/command` ou console serial, eletrodo de presença de líquido conectado ao pino 15.
- **Passo a Passo:**
  1. Enviar comando local: `{"sensorEnable":1, "sensorBypass":0, "speed":500}` com o sensor seco.
  2. Verificar se o motor permanece estático e se o LED indicador no pino 33 está aceso.
  3. Imergir o sensor em água destilada/solução aquosa.
  4. Cronometrar a resposta de partida do motor.
- **Comportamento Esperado e Critério de Aceitação:** Com o sensor seco, o motor não gira. Ao entrar em contato com o líquido, o sensor transita para `LOW` e o motor parte suavemente em $\le 50\text{ ms}$ (debounce) mantendo a velocidade inicial pela trava de segurança de 500 ms.
- **Subsistemas Impactados:** Firmware do Nó (`SensorAndConversion.h:1-19`, `Lifecycle.h:163-202`), Hardware (Circuito do Sensor).
- **Questões para Investigação no Código:** Por que `sensorEnable` e `sensorBypass` continuam fora da whitelist de comandos do Hub (`Commands.h`), sendo exclusivos do modo de bancada?

---

## 5. Tabela de Descoberta de Recursos (Features Discovered)

Mapeamento estruturado de todas as funcionalidades descobertas durante a mineração das especificações autoritativas da bomba peristáltica:

| # | Categoria | Recurso (Feature) | Descrição Técnica | Entradas (Inputs) | Saídas (Outputs) | Comportamento de Erro / Falha | Descoberto Via |
|---|---|---|---|---|---|---|---|
| 1 | Controle / Perfil | Parada sem Zerar Volume | Finaliza o perfil ativo e desacelera o motor para IDLE preservando o contador de sessão acumulativo. | `{"mode":0}` ou comando `stop` | `PumpActive=false`, `PumpMode=0`, `vol` mantido | N/A (Comando seguro e idempotente) | Doc §1.0 / Firmware `OperationController.h` |
| 2 | Telemetria / Sessão | Zeramento Não-Otimista de Volume | Zera o acumulador de volume acumulado da sessão (`vol`) e o marco de ciclo (`cyc_vol`). | `{"pump_command":"reset_volume"}` | `vol = 0.0`, `cyc_vol = 0.0` | Se `vol >= 0.05` após 5 s, o App gera aviso de falha de zeramento | Doc §1.0, §1.6 / Hub `Commands.h` / App `PumpControlViewModel.cs` |
| 3 | Controle Local | Arbitragem dos Knobs de Bancada | Habilita ou trava os potenciômetros físicos manuais de velocidade e polaridade/ganho. | `{"pump_pot":1}` ou `0` (Hub) $\rightarrow$ `{"pot":1/0}` | `PumpPotEnabled=true/false`, motor devolvido aos knobs | Se fora da faixa, ignorado com log | Doc §1.0, §1.6 / Firmware `OperationController.h` |
| 4 | Segurança / Acionamento | Parada Autônoma por Prazo (`speed_ms`) | Interrompe a rotação manual comandada após expirar um deadline local em milissegundos. | `{"pump_speed":S, "pump_speed_ms":ms}` | Motor desacelera para 0 ao expirar o tempo | Se link cair, nó para sozinho | Doc §1.0, §1.6 / Firmware `Lifecycle.h` |
| 5 | Segurança / NVS | Proteção de Flash contra Comandos Hostis | Bloqueia ativamente pela rede a injeção de comandos destrutivos à partição NVS (`clear_nvs`, `save_config`). | `pump_command` contendo verbos não autorizados | Descarte de comando e log `ESP32_AVISO` no Hub | Comandos perigosos barrados com código de erro | Doc §1.0, §1.10 #5 / Hub `Commands.h` |
| 6 | Resiliência / Processo | Retomada Autônoma Pós-Queda de Energia | Reconstitui o estado de perfil e acumulador após reinício espúrio via checkpoint NVS salvo a cada 60 s. | Flag NVS `s_active=true` no boot | `OP_RUNNING` retomado imediatamente com `s_cvol` e `s_vol` | Defasagem máxima de 60 s na integração | Doc §1.0, §1.7 / Firmware `RuntimeStateStore.h` |
| 7 | Controle / Malha | Sintonia e Eco de PID de Volume | Ajuste dinâmico dos ganhos de correção volumétrica de perfil com eco garantido em telemetria. | `pumpPidKp`, `pumpPidKi`, `pumpPidKd` | Ecos `PumpPidKp/Ki/Kd` na telemetria | App recusa persistência se eco diferir além de 0,0005 | Doc §1.0, §1.6 / Hub `HttpServer.h` / App `PumpControlViewModel.cs` |
| 8 | Metrologia / Calibração | Calibração por Coeficientes de Reta | Aplicação imediata de ganho e deslocamento linear para conversão analógica de vazão. | `pumpSlope`, `pumpIntercept` | Ecos `PumpSlope`/`PumpIntercept`, gravação de recibo | Se `\|slope\| < 1e-6`, motor imobilizado por guarda | Doc §1.0, §1.6 / Firmware `SensorAndConversion.h` |
| 9 | Metrologia / Assistente | Calibração Volumétrica Assistida | Assistente multiponto em 250, 500 e 1000 que guia dosagem com proveta e ajusta reta linear com $R^2$. | Pulsos de velocidade $S$ com $\Delta t$, leitura de $V$ | Tabela de pontos, resíduos, $R^2$, recibo JSON | Bloqueio de ajuste se $R^2 < 0,98$ ou $\Delta t$ inválido | Doc §1.0, §1.9 / App `PumpCalibrationViewModel.cs` |
| 10 | Diagnóstico / Infra | Monitoramento e Diagnóstico de Saúde | Inspeção periódica assíncrona de uptime, heap livre, RSSI, falhas e estado da máquina operacional. | Requisições HTTP em `/diag` e `/nodeDiag` | Tabela visual de nós de rede no Windows App | Notificação de falha de conexão e timeout | Doc §1.0, §1.8 / Hub `NodeDiagTask.h` / App `HubNodesViewModel.cs` |
| 11 | Segurança / Hardware | Intertravamento por Presença de Líquido | Bloqueia a rotação do motor quando a mangueira estiver seca, operando com botão físico e LED. | Contato analógico no pino 15 (LOW = molhado) | Rotação permitida somente na presença de fluido | Motor imobilizado em qualquer modo se seco | Doc §1.1, §1.10 #7 / Firmware `SensorAndConversion.h` |
| 12 | Rede / Sincronismo | Semeadura Aleatória de `cmd_id` | Inicializa identificador de comando da caixa com valor aleatório no boot do Hub, prevenindo ACK fantasma. | Boot do Hub ESP32-S3 | `pumpBox` inicializada com base randômica | Previne rejeição silenciosa de comando pós-reboot | Doc §1.11 #10 / Hub `Mailboxes.h`, `Runtime.h` |

---

## 6. Tabela de Casos de Borda (Edge Cases)

| # | Recurso (Feature) | Cenário de Entrada / Condição de Teste | Comportamento Observado / Especificado no Código |
|---|---|---|---|
| 1 | Conversão $Q \rightarrow S$ | Parametrização de slope nulo ou infinitesimal ($|\text{slope}| < 10^{-6}$). | A função `mlminToSpeedUnits()` detecta o divisor nulo e retorna rigorosamente $S = 0.0$. **A bomba não divide por zero e não trava, mas fica completamente imobilizada em qualquer perfil.** |
| 2 | Partida do Motor | Envio de velocidade interna $S = 1.0$ (menor valor não nulo aceito). | O motor salta bruscamente para o duty PWM de breakaway $155$ ($\approx 15,15\%$). Abaixo disso o motor não gira por atrito estático. |
| 3 | Dinâmica de Rotação | Oscilação rápida do setpoint de velocidade entre $0$ e $S > 0$ em menos de 500 ms. | A trava de 500 ms (`MIN_MOTOR_ON_TIME_MS`) entra em ação: o motor sustenta a velocidade inicial engatada até que o cronômetro expire, filtrando a oscilação. |
| 4 | Acumulador de Volume | Queda brusca de energia elétrica exatamente no 59.º segundo de uma batelada. | O último checkpoint da NVS foi salvo há até 59 s atrás. Na reinicialização, o nó retoma o volume salvo no último minuto, ocorrendo perda máxima de 59 s de dosagem não registrada. |
| 5 | Enfileiramento de Comandos | Aplicativo dispara `{"pump_speed":500}` e logo em seguida `{"pump_speed":0}` antes do polling de 2 s do nó. | A caixa postal `pumpBox` opera em regime *latest-wins*: o segundo comando sobrescreve o primeiro. O nó busca a caixa e executa diretamente o segundo comando (parado), sem ter girado. |
| 6 | Integridade de NVS | Flash NVS corrompida fisicamente ou gravada por versão incompatível de struct. | `loadConfig()` constata divergência de checksum no `calculateCRC32` e restaura automaticamente os padrões seguros de fábrica (slope 0,0280, intercept 1,7602, PID nominal, mode 0). |
| 7 | Temporizador de Rotação | `speed_ms` configurado para expirar em instante onde a contagem de `millis()` do ESP32 sofre wrap-around (após 49 dias). | A comparação `(long)(now - g_usbSpeedUntilMs) >= 0` em aritmética de inteiros com sinal avalia corretamente a diferença e desliga o motor no tempo exato. |
| 8 | Intertravamento de Líquido | Mangueira vazia (`sensorWetState = false`) com perfil constante de $50\text{ mL/min}$ ativo. | `allowRun` é avaliado como `false`. O motor permanece imóvel no hardware (`finalSpeed = 0`), embora o perfil continue avançando o tempo $t$ e acumulando erro de volume no integrador do PID. |
| 9 | Comando Remoto com Potenciômetro | Envio de comando remoto `speed: 500` enquanto o operador gira os potenciômetros físicos. | O comando remoto força `hasUsbSpeed = true`, e o firmware ignora completamente a leitura analógica dos pinos 34 e 35 até o envio de `pot: 1`. |
| 10 | Reboot da Central Hub | Hub reinicia no meio de um ensaio enquanto a bomba continua ligada na fonte. | O nó detecta ausência temporária de HTTP, incrementa `g_hubFailStreak`, entra em backoff progressivo (até 15 s) e recupera a sincronização assim que o Hub conclui o boot sem derrubar o motor. |

---

## 7. Matriz de Rastreabilidade e Cruzamento de Subsistemas

| Identificador do Item | Tema Central | Firmware do Nó (3.10) | Hub ESP32-S3 (10.2) | Windows App | Bancada Física |
|---|---|:---:|:---:|:---:|:---:|
| **F-110-01** | Volume e vazão estimados | Sim (`PwmRuntime.h`) | Sim (`Telemetry.h`) | Sim (`PumpCalibrationViewModel.cs`) | Sim (Proveta graduada) |
| **F-110-02** | `mode:0` sem zerar volume | Sim (`OperationController.h`) | Sim (`Commands.h`) | Sim (`PumpControlViewModel.cs`) | Sim (Verificação de display) |
| **F-110-03** | Arbitragem dos knobs | Sim (`OperationController.h`) | Sim (`Commands.h`) | Sim (`PumpControlViewModel.cs`) | Sim (Knobs físicos) |
| **F-110-04** | Timeout `speed_ms` | Sim (`Lifecycle.h`) | Sim (`Commands.h`) | Sim (`PumpCalibrationViewModel.cs`) | Sim (Corte sob cabo desconectado) |
| **F-110-05** | Filtro de NVS no Hub | Sim (Semântica local) | Sim (`Commands.h`) | Sim (Bloqueio estrutural) | Sim (Serial USB) |
| **F-110-06** | Retomada pós-queda | Sim (`RuntimeStateStore.h`) | Transparente | Sim (Detecção de estado ativo) | Sim (Corte elétrico) |
| **F-110-07** | Porta do sensor invisível | Sim (`SensorAndConversion.h`) | Não implementado | Não exposto | Sim (Chave física pino 15) |
| **F-110-08** | Ecos e sintonia de PID | Sim (`TelemetryCodec.h`) | Sim (`HttpServer.h`) | Sim (`PumpControlViewModel.cs`) | Sim (Retenção pós-reboot) |
| **F-110-09** | Latência e latest-wins | Sim (`HubClient.h`) | Sim (`Mailboxes.h`) | Sim (Serialização por pending) | Sim (Tráfego de rede) |
| **F-110-10** | `start` com `mode=0` | Sim (`OperationController.h`) | Sim (`Commands.h`) | Transparente (Não usa `start`) | Sim (Console Serial) |
| **F-110-11** | Motor DC vs Stepper | Sim (Ponte H BTS7960) | Transparente | Transparente | Sim (Motor físico montado) |
| **F-110-12** | Potenciômetro bipolar | Sim (`OperationController.h`) | Transparente | Transparente | Sim (Knob pino 35) |
| **CHK-111-01** | Sentido de rotação | Sim (`PwmRuntime.h`) | Transparente | Sim (Acionamento volumétrico) | Sim (Direção do fluido) |
| **CHK-111-02** | Queda em $\le 4\text{ s}$ | Sim (`HubClient.h`) | Sim (`Telemetry.h`) | Sim (Alarme offline) | Sim (Desconexão da fonte) |
| **CHK-111-03** | Calibração volumétrica | Sim (`SensorAndConversion.h`) | Sim (`Commands.h`) | Sim (`PumpCalibrationViewModel.cs`) | Sim (Proveta e balança) |
| **CHK-111-04** | Devolução dos knobs | Sim (`OperationController.h`) | Sim (`Commands.h`) | Sim (`PumpControlViewModel.cs`) | Sim (Giro manual) |
| **CHK-111-05** | Corte por link caído | Sim (`Lifecycle.h`) | Sim (`Commands.h`) | Sim (`PumpCalibrationViewModel.cs`) | Sim (Desconexão Wi-Fi) |
| **CHK-111-06** | Parada autônoma `final_t` | Sim (`OperationController.h`) | Sim (`Telemetry.h`) | Sim (`PumpControlViewModel.cs`) | Sim (Ensaio de 5 min) |
| **CHK-111-07** | Perfis sequenciais | Sim (`OperationController.h`) | Sim (`Telemetry.h`) | Sim (`PumpControlViewModel.cs`) | Sim (Dois perfis seguidos) |
| **CHK-111-08** | Persistência de PID | Sim (`ConfigStore.h`) | Sim (`HttpServer.h`) | Sim (`PumpControlViewModel.cs`) | Sim (Reboot da placa) |
| **CHK-111-09** | Retomada autônoma | Sim (`RuntimeStateStore.h`) | Transparente | Sim (Eco de estado ativo) | Sim (Desligar no meio do ciclo) |
| **CHK-111-10** | Semeadura pós-reboot | Sim (`HubClient.h`) | Sim (`Mailboxes.h`) | Sim (Despacho imediato) | Sim (Reset do Hub) |
| **CHK-111-11** | Gate do sensor de líquido | Sim (`SensorAndConversion.h`) | Não exposto | Não exposto | Sim (Imergir sensor em água) |

---

## 8. Roteiro de Verificação e Perguntas-Chave para a Auditoria do Código

Para os agentes que executarão a auditoria e elaboração do plano de implementação (`IMPLEMENTATION_PLAN_BOMBA.md`), listam-se as verificações pontuais a serem comprovadas:

1. **Firmware do Nó (`External-Devices/bomba-peristaltica`):**
   - Confirmar se existe algum caminho em `OperationController.h` onde `g_cumulativeVolumeMl` seja sobrescrito sem ser pelo comando explícito `reset_volume`.
   - Verificar se a checagem de timeout de `speed_ms` em `Lifecycle.h:138-142` zera `usbSpeedSteps` e se os potenciômetros continuam travados até `pot:1`.
   - Verificar se `calculateCRC32` detecta alterações em structs com mesmo número de bytes e se um algoritmo de CRC32 formal baseado em tabela deve ser adotado.
   - Avaliar se a porta do sensor (`sensorEnable` e `sensorWetState`) deve ser incorporada à query string `/pumpData` para prover telemetria ao App.

2. **Central Hub (`ESP32S3-HUB`):**
   - Confirmar se `Commands.h:588-625` possui mapeamento para todas as chaves de controle da bomba (`pump_speed`, `pump_speed_ms`, `pump_pot`, `pumpPidKp/Ki/Kd`, `pumpSlope`, `pumpIntercept`, `p0..p20`, `t0..t99`, `q0..q99`).
   - Validar se `allowedPumpCommands[]` em `Commands.h:596` rejeita ativamente qualquer verbo diferente de `reset_volume`, `start` e `stop`.
   - Verificar se `seedReliableMailboxes()` em `Mailboxes.h` gera um `cmd_id` verdadeiramente pseudoaleatório e se `takeReliable(pumpBox)` anexa o `cmd_id` correto no corpo JSON entregue em `/pumpCommand`.
   - Confirmar em `Telemetry.h:325-347` se todos os campos da bomba (`PumpOnline`, `PumpCommEnabled`, `PumpCommandPending`, `PumpMode`, `PumpPWM`, `PumpSpeed`, `PumpFlow`, `PumpVol`, `PumpTargetVol`, `PumpActive`, `PumpWaiting`, `PumpSlope`, `PumpIntercept`, `PumpPidKp/Ki/Kd`, `PumpPotEnabled`, `PumpCycleVol`) são emitidos corretamente no snapshot.

3. **Aplicativo Windows (`Windows_app`):**
   - Confirmar em `CommandBuilders.cs` se os métodos `PumpStopProfile()`, `PumpEnable()`, `PumpRoutingDisabled()`, `PumpManualSpeed()`, `PumpPotentiometers()`, `PumpResetVolume()`, `PumpCalibration()` e `PumpPid()` constroem os payloads exatos esperados pelo Hub e firmware.
   - Verificar em `PumpControlViewModel.cs` e `PumpCalibrationViewModel.cs` se a lógica de confirmação do zeramento de volume aguarda não-otimisticamente o retorno de `PumpVol < 0.05 mL`.
   - Validar se o árbitro central de segurança (`CommandActuators.cs`) mapeia todas as chaves da bomba para `ActuatorId.ExternalPump`, garantindo que ensaios automatizados ou receitas impeçam colisões de controle manual.
