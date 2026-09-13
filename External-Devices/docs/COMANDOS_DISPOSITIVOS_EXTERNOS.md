# Comandos e procedimentos dos dispositivos externos

**Data:** 2026-09-12  
**Propósito:** um único lugar que responda, por dispositivo, a três perguntas: *que interações o nó pode receber*, *o que o firmware faz com cada uma* e *o que o hardware deve fazer em consequência*. Cada afirmação abaixo foi conferida no código ativo (firmware do nó, `Commands.h`/`HttpServer.h` do Hub 10.2 e `CommandBuilders`/ViewModels do aplicativo), não nos documentos anteriores. Onde o código diverge da documentação do nó, este arquivo prevalece e a divergência está marcada com ⚠.

**Fontes por dispositivo:** o contrato de fio detalhado continua em `<dispositivo>/docs/PROTOCOL.md` e no `ESP32S3-HUB/docs/WIRE_CONTRACT_V9.md`; este documento não os substitui — ele explica o comportamento. Estado de implementação e pendências de bancada: `Windows_app/docs/PONTOS_DE_MELHORIA_EXPOSICAO_NOS.md`.

**Estado desta revisão:** §1 (bomba peristáltica), §2 (sensor de distância, firmware v11) e §3 (fluxômetro) completos. §4–§6 são esqueletos com ponteiros, a preencher um dispositivo por vez com a mesma anatomia.

---

## Visão Geral de Prontidão dos Dispositivos Externos

| Dispositivo | Firmware Ativo | Hub 10.2 | App Windows | Integração Software | Ensaio em Bancada Física |
|---|:---:|:---:|:---:|:---:|:---:|
| **Bomba Peristáltica** | v3.10 | 🟢 Total | 🟢 Total | 🟢 100% Integrado | 🟡 Pendente (§1.11) |
| **Sensor de Distância** | v11 | 🟢 Total | 🟢 Total | 🟢 100% Integrado | 🟡 Pendente (§2.11) |
| **Fluxômetro de Ar** | V10/v11 ⚠ | 🟢 Comandos principais | 🟡 Integrado com lacunas | 🟡 Funcional com pendências (§3.10) | 🟡 Pendente (§3.11) |
| **Sensor de Biomassa** | v11 | 🟢 Total | 🟢 Total | 🟢 100% Integrado | 🟡 Pendente |
| **Agitador de Frascos** | v10 | 🟢 Total | 🟢 Total | 🟢 100% Integrado | 🟡 Em andamento |
| **Servo Drive (RPM)** | v2.0 | 🟢 Total | 🟢 Total | 🟢 100% Integrado | 🟡 Em andamento |

## 1. Bomba peristáltica externa (`bomba-peristaltica`, firmware 3.10)

### 1.0 Painel de Navegação Rápida — Estado de Prontidão e Integração (3.10)

> **Como navegar:** Esta matriz resume o estado real de cada funcionalidade da bomba, separando claramente o que já funciona no software integrado, o que aguarda validação com hardware/líquido na bancada, e o que foi deliberadamente adiado.

#### 🟢 Totalmente Implementado e Integrado de Ponta a Ponta (Nó ↔ Hub ↔ App)
*Código compilado, verificado estaticamente, repassado pelo Hub, exposto na interface do Windows App e aprovado na suíte de testes automatizados.*

| Funcionalidade | Nó (v3.10) | Hub (10.2) | App Windows | Onde Opera na UI | Testes Automatizados |
|---|:---:|:---:|:---:|---|---|
| **Parada de perfil sem zerar volume** | `startCycle()` mantém `vol`; `cyc_vol` isola o ciclo | Repassa `mode:0`; ecoa `PumpCycleVol` | `PumpStopProfile()` emite `{"mode":0}`; preserva `PumpVol` | *Controle › Bomba Externa* (botão Parar / interruptor) | `PumpStopProfile_DoesNotContainSpeedKey`, `TelemetryParserTests` |
| **Zerar volume acumulado** | `reset_volume` zera `vol` e `cyc_vol` | Whitelist em `allowedPumpCommands` | Botão *Zerar volume acumulado*; confirmação visual `< 0,05 mL` | *Controle* e *Calibrações* | `PumpResetVolume_MatchesFrozenWireKey`, `PumpResetVolume_RequiresConfirmation` |
| **Controle dos potenciômetros físicos** | `pot:1` (devolve aos knobs), `pot:0` (trava); ecoa `pot` | Traduz `pump_pot` → `pot`; ecoa `PumpPotEnabled` | Botão *Potenciômetros*; reflete estado do eco | *Controle › Bomba Externa* | `test_pump_speed_ms_and_pot_are_forwarded_without_prefix`, `BiomassPumpTests` |
| **Parada autônoma por timeout** | `speed_ms`: laço desliga motor se prazo expirar | Traduz `pump_speed_ms` → `speed_ms` | Calibração volumétrica envia $\Delta t + 3\text{ s}$ como segurança | *Calibrações › Bomba Externa* | `CommandBuildersTests`, `PumpCommandTests` |
| **Sintonia e eco de PID de volume** | Aceita `pid_kp/ki/kd`; ecoa `kp, ki, kd` no push | Traduz `pumpPidK*`; ecoa `PumpPidKp/Ki/Kd` | Expansor *PID de volume do nó*; digitação, validação e persistência após eco | *Controle › Bomba Externa* | `PumpPidTuning_ValidatesAndDispatchesWhenEchoPresent`, `test_hub_echoes_pump_pid_pot_and_cycle_volume` |
| **Calibração por coeficientes** | Aplica `pumpSlope` e `pumpIntercept`; ecoa no push | Repassa `pumpSlope`/`pumpIntercept`; ecoa no quadro | Edição manual, prévia gráfica de vazão, gravação de recibo JSON após eco | *Calibrações › Bomba Externa* | `PumpCalibrationTests` |
| **Calibração volumétrica assistida** | Acionamento via `speed` com rampa de duty 155..1023 | Repassa `pump_speed` | Assistente multiponto ($S = 250, 500, 1000$), cálculo de $R^2$, resíduos, aplicação e recibo | *Calibrações › Bomba Externa* | `PumpCalibrationViewModelTests` |
| **Proteção contra comandos perigosos** | `clear_nvs` só opera localmente | Hub bloqueia `clear_nvs`, `save_config`, etc. com `ESP32_AVISO` | App nunca emite comandos destrutivos à flash | N/A (proteção de infra) | `test_pump_command_whitelist_blocks_clear_nvs_and_config_verbs` |
| **Saúde e Diagnóstico do Nó** | `/diag` com RSSI, heap, uptime, falhas, estado | Proxy assíncrono `/nodeDiag` a cada 30 s | Tabela de nós de rede com métricas ao vivo | *Configurações › Rede* | `NodeDiagTaskTests` |

---

#### 🟡 Implementado no Software, Aguardando Ensaio Físico na Bancada
*O código está 100% pronto e aprovado em testes unitários/contrato. Falta executar com motor real acoplado, líquido, proveta, balança e cronômetro.*

| Ensaio Físico a Executar | O que será comprovado na bancada | Item no Checklist (§1.11) | Status de Bancada |
|---|---|:---:|:---:|
| **Sentido físico de rotação** | Conferir se $S > 0$ impulsiona o líquido no sentido correto em direção ao vaso (motor DC escovado em ponte H). | Item 1 | 🟡 Pendente de ensaio |
| **Tempo de resposta e detecção de queda** | Validar se ao desligar a alimentação do nó o app dispara o alarme *Bomba externa offline* em $\le 4\text{ s}$. | Item 2 | 🟡 Pendente de ensaio |
| **Curva real vs volumetria coletada** | Executar calibração volumétrica real ($S=250, 500, 1000 \times 60\text{ s}$) com proveta graduada; verificar resíduos e conferir 10 min de dosagem contínua. | Item 3 | 🟡 Pendente de ensaio |
| **Liberação de knobs de bancada** | Acionar a bomba pelo app e depois clicar em *Potenciômetros*; verificar se os knobs físicos voltam a operar o motor suavemente. | Item 4 | 🟡 Pendente de ensaio |
| **Corte autônomo de emergência** | Iniciar um acionamento com `speed_ms` e desconectar o cabo de rede/USB no meio do tempo; confirmar se o motor para sozinho ao expirar o prazo. | Item 5 | 🟡 Pendente de ensaio |
| **Preservação de volume entre perfis** | Rodar perfil curto (5 min, `final_t=5`), verificar parada automática, conferir se `PumpVol` permanece retido e `PumpCycleVol` indica o ciclo. Em seguida rodar segundo perfil e verificar acúmulo contínuo de `PumpVol`. | Itens 6 e 7 | 🟡 Pendente de ensaio |
| **Persistência NVS do PID** | Enviar novos ganhos de PID pelo app, desligar e religar o nó da bomba, conferir se os ganhos retornam no push sem intervenção. | Item 8 | 🟡 Pendente de ensaio |
| **Retomada de emergência pós-queda** | Cortar energia do nó com motor girando em perfil contínuo; religar energia e confirmar retomada autônoma via checkpoint NVS `s_cvol`. | Item 9 | 🟡 Pendente de ensaio |
| **Comando pós-reboot do Hub** | Reiniciar o Hub mantendo a bomba ligada; enviar *Zerar volume* e confirmar aplicação imediata (semeadura de `cmd_id`). | Item 10 | 🟡 Pendente de ensaio |

---

#### 🔵 Diretrizes de Segurança e Decisões de Arquitetura Fechadas
*Decisões de engenharia aprovadas que definem o comportamento seguro do sistema.*

| Decisão / Recurso | Onde Opera | Comportamento e Justificativa Técnica | Estado |
|---|---|---|:---:|
| **Proteção de NVS (`clear_nvs`, `save_config`)** | Hub 10.2 / Nó | O Hub bloqueia ativamente comandos de formatação e escrita bruta de NVS pela rede. Apenas comandos de processo (`reset_volume`, `start`, `stop`) são repassados. Acesso a `clear_nvs` apenas por USB local de bancada. | 🟢 Fechado (§1.10 #5) |
| **Arbitragem dos potenciômetros de bancada** | Firmware 3.10 / App | Os potenciômetros operam exclusivamente em controle manual local. O app supervisiona e comuta quem está no comando através da variável interna `pot` ecoada pelo nó (`PumpPotEnabled`). Não há telemetria nem sentido em ler ângulos brutos de ADC. | 🟢 Fechado (§1.10 #3) |
| **Modo de ativação física por presença de líquido** | Hardware local da bomba | Modo de segurança e operação física ativado por botão no hardware. Com o botão acionado, a bomba opera exclusivamente em **modo manual local** (liga/desliga por contato com líquido, na velocidade e sentido dos potenciômetros) e **não deve receber comandos externos de perfil**. No software, uma futura integração seria apenas telemetria passiva de identificação de estado travado. | 🟢 Fechado (§1.10 #7) |
| **Curvas de calibração (Linear vs Multiponto)** | Firmware 3.10 / App | A bomba peristáltica tem resposta predominantemente linear ($R^2 > 0,99$). A calibração assistida do app calcula a reta e resíduos sobre múltiplos pontos ($S = 250, 500, 1000$). Pequenos desvios dinâmicos são corrigidos pelo PID de volume do nó. **Diretriz:** manter calibração linear atual; só evoluir para tabela de lookup se a bancada física demonstrar $R^2 < 0,98$. | 🟢 Fechado (§1.3) |

---

### 1.1 O que o firmware assume do hardware

| Elemento | Pino / recurso | O que o firmware faz | Observação |
|---|---|---|---|
| Ponte H com PWM duplo | `R_EN`=25, `L_EN`=26 (sempre HIGH após o boot); `R_PWM`=14, `L_PWM`=27 (LEDC 7,5 kHz, 10 bits) | Velocidade = duty numa das saídas; a outra fica em 0. Sentido = qual saída recebe o PWM (`s ≥ 0` → `R_PWM`) | **Motor DC escovado** (confirmado pelo operador em 2026-09-12) em ponte H tipo BTS7960/IBT-2. O NEMA 17HS4401 do CAD é referência mecânica, não o motor montado |
| Cabeçote | CAD Watson-Marlow: rotor de 5 roletes, mancais LM6UU | — | A relação duty → mL/min é inteiramente empírica (calibração) |
| Potenciômetro de velocidade | `POT_INT`=34 (ADC 12 bits, filtro passa-baixas α=0,10) | Magnitude 0..1 × `V_MAX` | Só vale no modo manual local de bancada (`pot=1`, §1.6) |
| Potenciômetro de sentido + ganho | `POT_GAIN`=35 | Fator `(ADC − centro)/centro` ∈ [−1, 1] que multiplica a velocidade: o sinal dá o sentido e o módulo escala a velocidade (centro = parado, extremos = velocidade plena no sentido escolhido) | Velocidade manual = magnitude × fator; ajuste local exclusivo de bancada |
| Sensor de presença de líquido | `SENSOR_PIN`=15 (`INPUT_PULLUP`, debounce 50 ms; **LOW = molhado**) | Chave de contato: quando ativado pelo botão físico, o motor só gira na presença de líquido | **Modo de contato físico.** Não é medidor de vazão. Aciona a bomba na velocidade dos potenciômetros |
| Botão do modo de contato | `SENSOR_ENABLE_BUTTON_PIN`=32 (pull-up) | Ativa o modo manual de acionamento por líquido | Quando ligado, o modo manual local assume e comandos remotos de perfil são ignorados |
| LED de estado do sensor | 33 | Acende quando o modo de contato por líquido está ativo | — |
| Wi-Fi | STA para `ModuloTECNAL_1`/`_2` (canal 6) + AP próprio `FeedPump` em 192.168.6.1 | Push a 1 s, poll a 2 s, hello a 30 s; backoff até 15 s; queda forçada após 8 falhas | Servidor local na porta 80 (§1.5) |
| Núcleos | Core 0: tarefa `pwmTask` (2 ms) — escreve LEDC e integra volume. Core 1: `loop()` — rede, comandos, máquina de estados, WDT 15 s | Comunicação por `volatile` (`g_cmdSpeed`, `g_driverEnabled` → Core 0; `g_cumulativeVolumeMl`, `g_actualPwmDuty` → Core 1) | — |

### 1.2 Como a velocidade final é decidida (a cada laço, ~2 ms + rede)

```
requestedSpeed =
    OP_RUNNING → mlminToSpeedUnits(Q_alvo + PID)        (nunca negativo)
    OP_IDLE    → hasUsbSpeed ? usbSpeedSteps : potSpeed   (pode ser negativo = sentido inverso; potSpeed = 0 com pot travado)
    (3.10) hasUsbSpeed && speed_ms vencido → usbSpeedSteps = 0
    OP_WAITING → 0
allowRun   = !(sensorEnable && !sensorBypass) || sensorMolhado
finalSpeed = allowRun ? clamp(requestedSpeed, −1000, +1000) : 0
```

- **Unidade S (0..1000)** é a "velocidade interna". `pwmTask` converte: `|S| < 1` → duty 0; senão `duty = 155 + (|S| − 1)/999 · (1023 − 155)`. Ou seja, **duty útil 155..1023** (`PWM_BREAKAWAY` = 155: abaixo disso o motor não vence o atrito do cabeçote). S = 1 já é 155/1023 ≈ 15 % de duty, não "quase parado".
- **Trava de 500 ms** (`MIN_MOTOR_ON_TIME_MS`): ao ligar, o motor mantém a velocidade de partida por ao menos 500 ms antes de aceitar uma nova. Evita "tremer" quando o alvo oscila perto de S = 1.
- **Conversão para vazão** é a reta de calibração em ambos os sentidos: `Q [mL/min] = slope · S + intercept` e `S = (Q − intercept)/slope`. Com `|slope| < 1e-6` o firmware devolve S = 0 (não divide por zero) — uma calibração com slope zero **para a bomba em qualquer perfil**.

### 1.3 Volume: estimado, não medido

`g_cumulativeVolumeMl += pwmDutyToMlmin(duty_aplicado)/60 · dt` a cada 2 ms no Core 0. O duty é convertido **de volta** para S e então para mL/min pela mesma reta de calibração. **3.10:** esse acumulador é o **contador da sessão** — só `reset_volume` o zera; cada ciclo de perfil guarda o valor em que começou (`g_cycleStartVolumeMl`, no checkpoint como `s_cvol`) e publica a diferença como `cyc_vol`. Consequências que o operador precisa saber:

1. `PumpVol` e `PumpFlow` são **previsões da curva de calibração**, não leituras de um sensor. Se a curva estiver errada, o volume acumulado está errado na mesma proporção, e nenhum campo de telemetria denuncia isso.
2. O "PID de volume" (`pid_kp/ki/kd`) compara `V_alvo(t)` (integral analítica do perfil) com o **volume do ciclo** (`vol − início do ciclo`). Ele corrige apenas erros de **execução** (trava de 500 ms, quantização de duty, tempo em `OP_WAITING`), nunca erros de **calibração**. Padrões: kp 0,5, ki 0,05, kd 0,001; integrador limitado a ±100 mL·min. **3.10:** os três ganhos são ecoados (`kp/ki/kd` no push → `PumpPidKp/Ki/Kd`), e o app só libera a edição quando vê o eco.
3. A única forma de fechar a malha de verdade é a calibração volumétrica (§1.8) feita com recipiente graduado.

### 1.4 Máquina de estados

| Estado | Entrada | Saída | Motor |
|---|---|---|---|
| `OP_IDLE` | boot limpo; `stop`; `mode:0`; `speed`; fim de `final_t` | perfil (`mode ≥ 1` por parâmetro) → `OP_WAITING` se `init_t > 0`, senão `OP_RUNNING`; `start` → `OP_RUNNING` | potenciômetros (se `pot=1`) **ou** `speed` (até `speed_ms` vencer, `pot:1` ou novo comando) |
| `OP_WAITING` | perfil com `init_t > 0` | `t ≥ init_t` → `OP_RUNNING` (novo ciclo: marca o início, zera PID; **não** zera `vol`) | parado (`Q = 0`) |
| `OP_RUNNING` | acima; recuperação após reset | `final_t > 0 && t ≥ final_t` → `OP_IDLE`, `mode = 0` persistido, checkpoint limpo; `vol` **mantido** | `Q(t) + PID` |

`t` é `(millis − g_opTriggerTimeMs)/60000` em minutos; `t_rel = t − init_t` alimenta o perfil. Com `final_t = 0` **o perfil não termina sozinho**.

### 1.5 Por onde um comando pode chegar

| Canal | Como | `cmd_id` | Observações |
|---|---|---|---|
| **Hub → nó** (o caminho do aplicativo) | o nó faz `GET http://192.168.4.1/pumpCommand` a cada 2 s (até 15 s em backoff) e recebe a caixa `pumpBox` inteira, `{"cmd_id":N,…}` ou `{}` | sim; `cmd_id == g_lastAppliedHubCommandId` é ignorado; o `ack_cmd_id` sai no push seguinte | O Hub só enfileira se `pumpComm` estiver ligado (`pumpCommOn`), e **enfileira um quadro por vez (latest-wins)**: um segundo comando antes do ack substitui o primeiro. Latência típica 0–2 s + 1 s para o ack chegar ao app |
| `POST /command` no AP local (192.168.6.1) | corpo JSON | não — aplicado sempre | Sem autenticação; útil na bancada |
| Serial USB 115200 | uma linha JSON `{…}\n` (buffer 4096 B) | não | Mesmo parser dos demais canais |
| `GET /update` (OTA) | página HTML + upload `.bin` | — | Laço principal para durante o upload; watchdog de 90 s sem chunk aborta |
| Físico | dois potenciômetros; botão do sensor | — | Só em `OP_IDLE` e com as restrições da §1.1 |

Todos os canais chegam a `processJsonCommand()`; a tabela seguinte vale para qualquer um deles. As colunas **Hub** e **App** dizem se o Hub encaminha a chave (whitelist de `Commands.h`) e se o aplicativo tem um caminho de UI/receita que a emite.

### 1.6 Catálogo de chaves e o que cada uma faz

**Convenção do Hub:** o app envia chaves com prefixo `pump_` ou os nomes `pumpSlope`/`pumpIntercept`/`pumpPidK*`; o Hub remove `pump_`, traduz `pumpPidKp→pid_kp` (etc.) e repassa o resto tal qual. Chaves fora da whitelist **são descartadas em silêncio**.

#### Comandos por string (`"command":"…"`)

| Valor | Firmware | Hardware | Hub (`pump_command`) | App |
|---|---|---|---|---|
| `start` | `init_t = final_t = 0`, `OP_RUNNING` imediato com o `mode` vigente; novo ciclo (marca o início, zera PID); limpa checkpoint | Motor segue o perfil **sem fim** (`final_t = 0`). Com `mode = 0`, fica "active" com Q = 0 — parado, mas marcado como rodando ⚠ | sim | não usa |
| `stop` | `OP_IDLE`, `mode = 0` persistido, novo ciclo (PID), limpa checkpoint; **`vol` mantido** (3.10) | Motor para (salvo potenciômetros/`speed` em IDLE) | sim | não usa (usa `mode:0`, abaixo) |
| `reset_volume` | **único** que zera `vol` (e o início do ciclo); **não** muda estado nem `mode` | Nenhuma ação no motor | sim | **Zerar volume** (Controle e Calibrações); confirmação não-otimista por `PumpVol < 0,05` |
| `save_config` / `load_config` | grava/relê o blob de configuração na NVS | — | **não** (recusado com `ESP32_AVISO`, 2026-09-12) | não usa |
| `print_config` | imprime na serial o JSON com arrays do perfil | — | **não** | não usa |
| `clear_nvs` | **apaga toda a NVS e reinicia o ESP** — perde calibração, perfil, PID e checkpoint | Motor para no reboot | **não** — o Hub encaminha só `reset_volume`, `start`, `stop` | não usa. Só por `POST /command` local ou serial |

#### Perfil e temporização (disparam **reset do ciclo** quando enviados sem `command`)

| Chave | Faixa aceita | Firmware | Hardware | Hub | App |
|---|---|---|---|---|---|
| `mode` | 0..5 (fora disso: ignorado com log) | Define o perfil; **mesmo valor repetido conta como mudança** → novo ciclo (início marcado, PID zerado), limpa checkpoint, reinicia `t`; `mode ≥ 1` entra em `WAITING`/`RUNNING`; `mode = 0` vai a IDLE. **`vol` mantido** (3.10) | 1 constante, 2 linear, 3 exponencial, 4 polinômio (grau ≤ 20), 5 linear por partes | sim | Enviar perfil; `{"mode":0}` = **parar perfil** (`PumpStopProfile`), sem zerar |
| `init_t` | min, qualquer float | Atraso antes de bombear (`OP_WAITING`) | Parado até `t ≥ init_t` | sim | Tempo inicial |
| `final_t` | min; `0` = sem fim | Ao atingir: IDLE, `mode = 0` persistido; `vol` mantido | Motor para sozinho | sim | Tempo final (padrão 60 min no app) |
| `lambda_const` | mL/min | `Q = λ` | — | sim | Perfil Constante |
| `lambda_linear`, `phi_linear` | mL/min, mL/min² | `Q = λ + φ·t` | — | sim | Perfil Linear |
| `lambda_exp`, `phi_exp` | mL/min, 1/min | `Q = λ·e^{φt}` | — | sim | Perfil Exponencial |
| `p0`..`p20` | double | `Q = Σ p_i t^i` (Horner); integral analítica para o alvo de volume | — | sim (`polyKeys`) | Perfil Polinomial (app limita o grau) |
| `num_segments`, `t0..t99`, `q0..q99` | 2..100 segmentos; `t0` forçado a 0 quando `mode = 5` | Interpolação linear; após o último ponto mantém `q_último`; volume por trapézios | — | sim (o Hub varre `t0..t99`/`q0..q99` só quando vê `pump_command`, `num_segments`, `t0` ou `q0`) | Perfil por partes (2..N pontos) |

**Regra de aplicação (importante para o app):** qualquer chave desta tabela **sem** `"command"` no mesmo JSON dispara `startCycle()` (início de ciclo marcado no contador, PID zerado) + `clearRuntimeState()` + novo `t = 0`. Um quadro com `mode` **e** `command` juntos deixa a decisão para o `command` (`start`/`stop`), não para o `mode`.

#### Velocidade manual e portas de segurança (não disparam reset de ciclo)

| Chave | Firmware | Hardware | Hub | App |
|---|---|---|---|---|
| `speed` | `usbSpeedSteps = clamp(valor, ±1000); hasUsbSpeed = true; OP_IDLE; mode = 0` (`mode = 0` não é persistido; `vol` intocado) | Motor a S imediatamente, **inclusive negativo = sentido inverso**; `0` para. Fica até o próximo `speed`, perfil, `pot:1` ou o prazo de `speed_ms` | sim, como **`pump_speed`** (`speed` sem prefixo é rejeitado por desenho) | **Acionamento volumétrico** (Calibrações › Bomba Externa): S inteiro 1..1000, parada primária pelo relógio do app; queda de link → parada na reconexão |
| `speed_ms` (3.10) | com `speed ≠ 0`: prazo em ms; ao vencer, `usbSpeedSteps = 0` (log "speed_ms elapsed") | **Parada autônoma** do motor sem depender do cliente | sim, `pump_speed_ms` | a calibração envia duração + 3 s como rede de segurança (nunca antes da parada do app) |
| `pot` (3.10) | `1`: `disablePot = false; hasUsbSpeed = false; speed = 0` — devolve o motor aos knobs; `0`: `disablePot = true` | Knobs voltam a comandar / ficam mortos | sim, `pump_pot` | botão **Potenciômetros** (Controle › Bomba Externa) alterna a partir do eco `PumpPotEnabled` |
| `disablePot` | grafia 3.9 (`1` = travar) | — | **não** (fora da whitelist; use `pump_pot`) | — |
| `sensorEnable` | liga/desliga a porta do sensor de líquido (sobrescrito pelo botão se `sensorButtonOverride = 0`) | Com porta ligada e tubo seco, o motor **não gira** em nenhum estado | **não** | — |
| `sensorBypass` | `1` ignora a porta mesmo com `sensorEnable` | — | **não** | — |
| `sensorButtonOverride` | `0` faz o botão físico mandar em `sensorEnable`; `1` (padrão) ignora o botão | — | **não** | — |

#### Calibração e PID (persistem, não resetam o ciclo, não passam pela porta do sensor)

| Chave | Firmware | Hub | App |
|---|---|---|---|
| `pumpSlope`, `pumpIntercept` | Atualiza a reta e marca `g_configDirty` → blob gravado no próximo laço. **Efeito imediato** na conversão S↔Q, inclusive num perfil em execução e na integração de volume | sim (pass-through) | **Aplicar calibração**: só persiste no PC e grava recibo após o eco `slope`/`intercept` bater com o pedido (tolerância 1e-4) |
| `pid_kp`, `pid_ki`, `pid_kd` | Atualiza ganhos; efeito imediato; **ecoados no push** (3.10) | sim (`pumpPidKp`→`pid_kp` etc.) | Expansor **PID de volume do nó**: liberado só com eco presente; **Enviar PID** aguarda o eco igual (tolerância 5e-4) para persistir no PC; padrões 0,5/0,05/0,001 já nas configurações |

### 1.7 Persistência e recuperação

- **Blob de configuração** (`PumpConfig`, ≈1,03 kB: modo, tempos, λ/φ, 21 coeficientes, 100+100 pontos, calibração, PID) gravado inteiro em `feed_pump/config` sempre que `g_configDirty`. ⚠ O "CRC32" é uma soma simples de bytes; detecta corrupção grosseira, não troca de layout. Um blob de tamanho diferente (outro firmware) volta aos padrões: **slope 0,0280188148, intercept 1,7601988934**, PID 0,5/0,05/0,001, `mode 0`.
- **Checkpoint** (`s_active`, `s_vol`, `s_time`, `s_mode`, **`s_cvol`** desde 3.10) a cada 60 s **só em `OP_RUNNING` e `mode ≠ 0`**. `stop`, `mode`/parâmetro novo, `start` e fim de `final_t` limpam o flag.
- **Boot com checkpoint ativo:** o firmware **retoma `OP_RUNNING`** com o volume e o tempo salvos (até 60 s atrás), sem esperar comando — isto é, **após uma queda de energia no meio de um perfil a bomba volta a bombear sozinha ao religar**. Deliberado ("Robust Recovery"); o operador precisa saber.

### 1.8 Telemetria: o que sai do nó e como chega ao app

Push `GET /pumpData?…` a cada 1 s (`DATA_PUSH_PERIOD_MS`), mesma linha impressa na serial a 1 Hz:

| Parâmetro do push | Origem no firmware | Chave no quadro do Hub | No app |
|---|---|---|---|
| `mode` | `g_config.mode` | `PumpMode` | modo em execução |
| `pwm` | duty realmente aplicado (0 ou 155..1023) | `PumpPWM` | — (drawer) |
| `speed` | `g_cmdSpeed` (S comandado ao Core 0, com sinal) | `PumpSpeed` | eco durante a calibração |
| `flow` | `g_currentFlowRateMlMin` (alvo + PID, ou reta(S) em IDLE) | `PumpFlow` | Vazão atual — **estimativa** |
| `vol` | volume integrado (§1.3) | `PumpVol` | Volume acumulado — **estimativa**; base do gás proporcional |
| `v_tgt` | `V_alvo(t_rel)` analítico | `PumpTargetVol` | — |
| `active` / `waiting` | `OP_RUNNING` / `OP_WAITING` | `PumpActive` / `PumpWaiting` | estado do perfil |
| `ack_cmd_id` | último `cmd_id` do Hub aplicado | fecha `pumpBox` → `PumpCommandPending=false` | chip "aguardando" some |
| `slope`, `intercept` | reta vigente | `PumpSlope`, `PumpIntercept` (após 1.º eco) | "Slope/Intercepto aplicado (nó)"; confirmação da calibração |
| `kp`, `ki`, `kd` (3.10) | ganhos vigentes | `PumpPidKp/Ki/Kd` | "Nó: Kp · Ki · Kd" no expansor; libera a edição |
| `pot` (3.10) | `!disablePot && !hasUsbSpeed` | `PumpPotEnabled` | texto e botão **Potenciômetros** |
| `cyc_vol` (3.10) | `vol − início do ciclo` | `PumpCycleVol` | — (disponível no snapshot) |

O Hub deriva ainda `PumpOnline` (= `pumpComm` ligado **e** push há ≤ 4 s) e `PumpCommEnabled` (eco do interruptor do Hub), mais `PumpIP`/`PumpNodeVer`/`PumpNodeMac` pelo `/nodeHello` (a cada 30 s) e o `/diag` local via `/nodeDiag` (uptime, heap, RSSI, `hub_fail_streak`, `op_state`, `mode`, `flow`, `vol`).

**Não sai do nó** (e portanto o app não pode mostrar): estado da porta do sensor (`sensorEnable`/molhado), sentido do potenciômetro, tempo `t` corrente pelo Hub (só na serial/`/readData`).

### 1.9 Procedimentos do operador no aplicativo (e o que acontece no fio)

| Procedimento | Onde | Quadros (ordem) | O que confirmar |
|---|---|---|---|
| Habilitar a bomba | Controle › Bomba Externa, interruptor | `{"pumpComm":1}` | `PumpCommEnabled=true`; `PumpOnline` em ≤ 4 s se o nó estiver empurrando |
| Enviar perfil | idem, **Enviar perfil** | um quadro: `mode`, `init_t`, `final_t` + parâmetros do modo | `PumpMode` ecoa; `PumpWaiting`/`PumpActive`; `PumpCommandPending` cai no push seguinte ao poll (≤ 3 s) |
| Parar perfil | interruptor desliga, ou **Parar** | 1.º `{"mode":0}` (com `pumpComm` ainda ligado — senão o Hub descarta), 2.º quadro separado `{"pumpComm":0}` | `PumpActive=false`; `PumpVol` mantido (3.10) |
| Parada segura global | E-stop / árbitro | `PumpStopProfile()` = `{"mode":0}` | idem |
| Zerar volume | **Zerar volume acumulado** | `{"pump_command":"reset_volume"}` | `PumpVol < 0,05` em ≤ 5 s; senão aviso |
| Calibração por coeficientes | Calibrações › Bomba Externa | `{"pumpSlope":a,"pumpIntercept":b}` | eco igual em ≤ 15 s → recibo JSON em `Calibracoes/` |
| **Calibração volumétrica** | idem, cartão "Acionamento volumétrico" | `{"pump_speed":S,"pump_speed_ms":(Δt+3 s)}` → espera Δt pelo relógio do app → `{"pump_speed":0}`; operador informa V; repete; **Usar ajuste** → **Aplicar** (quadro acima) | pontos (S, Δt, V, Q) na tabela e no recibo; R². Ao terminar, **Potenciômetros** devolve os knobs |
| PID de volume | Controle › Bomba Externa, expansor | `{"pumpPidKp":a,"pumpPidKi":b,"pumpPidKd":c}` | eco igual em ≤ 15 s → persistido no PC |
| Potenciômetros de bancada | Controle › Bomba Externa, botão | `{"pump_pot":1}` / `{"pump_pot":0}` | `PumpPotEnabled` ecoa |
| Receita — nó Bomba Externa | Receitas | `Enable` = `{"pumpComm":1}`; `Profile` = perfil; `Stop` = `{"mode":0}` + `{"pumpComm":0}` separado | o motor de receitas exige o eco de `PumpCommEnabled` antes de prosseguir |
| Gás proporcional | Controle › Bomba Externa | nenhum quadro **para a bomba**; usa `PumpVol` para comandar a aeração `Q_g = (V₀ + Vol/1000)·vvm` | recusado se a cascata de O₂ detiver a aeração |

**Arbitragem:** todas as chaves acima pertencem a `ActuatorId.ExternalPump`; enquanto uma receita ou um ensaio detiver a bomba, o envio manual é recusado com o motivo (`DispatchRefusal`).

### 1.10 Limitações, riscos e decisões (auditoria do 3.9 → firmware 3.10)

Decisões do operador em 2026-09-12 sobre a auditoria; o que foi feito em cada uma:

| # | Achado no 3.9 | Decisão | Feito em 3.10 / Hub / app |
|---|---|---|---|
| 1 | Volume e vazão são estimados pela curva | Manter; calibrar por volume | Documentado (§1.3); calibração volumétrica no app |
| 2 | `mode:0` zerava o volume | **Parar sem zerar; zerar só com `reset_volume`** | `startCycle()` substitui o zeramento em `stop`, `mode`, troca de parâmetro, `WAITING→RUNNING` e fim de `final_t`; `vol` é contador de sessão, `cyc_vol` é o do ciclo; checkpoint guarda `s_cvol` |
| 3 | `speed` pegajoso: knobs mortos até reboot | **Comando para habilitar/desabilitar os potenciômetros** | `pot:1/0` no nó (`pump_pot` no Hub); eco `pot` → `PumpPotEnabled`; botão **Potenciômetros** no app |
| 4 | `speed` sem temporizador | **`speed_ms` no firmware (parada autônoma)** | `speed_ms` opcional; laço zera a velocidade ao vencer; a calibração envia Δt + 3 s (a parada primária continua no app, para o Δt medido ser o do app) |
| 5 | `clear_nvs` passava pelo Hub | **Filtrar no Hub: só `reset_volume`, `start`, `stop`** | `allowedPumpCommands[]` em `Commands.h`; recusa com `ESP32_AVISO`; teste de contrato |
| 6 | Retomada automática após queda de energia | Mantida (batelada longa); registrar no procedimento de energização | `s_cvol` entra no checkpoint para a retomada não quebrar o ciclo |
| 7 | Porta do sensor invisível ao app | Adiado | Sem mudança; `sensorEnable`/`sensorBypass` continuam só locais |
| 8 | PID sem eco, UI travada | **Ecoar e receber PID; padrões no app** | `kp/ki/kd` no push e no `/readData`; Hub `PumpPidKp/Ki/Kd`; app libera edição com eco, persiste ao eco, padrões 0,5/0,05/0,001 |
| 9 | Latência 0–2 s + latest-wins na `pumpBox` | Manter; app já serializa por `PumpCommandPending` | — |
| 10 | `start` com `mode = 0` marca `active` sem bombear | Manter (app não usa `start`) | — |
| 11 | Motor: CAD com stepper | **É DC** (confirmado) | Doc corrigida (§1.1) |
| — | Potenciômetro "gain" | É **sentido e ganho** sobre a velocidade do outro knob | Doc corrigida (§1.1) |

**Compatibilidade:** tudo é aditivo. Um Hub anterior ignora `kp/ki/kd/pot/cyc_vol`; uma bomba 3.9 ignora `speed_ms` e `pot` (nela `speed` continua pegajoso e `mode:0` continua zerando — o app mostra o PID travado e "estado dos potenciômetros desconhecido" nesse caso).

### 1.11 Checklist de bancada (fecha os itens acima)

- [ ] Conferir sentido positivo = fluxo para o vaso (motor DC confirmado).
- [ ] `pumpComm:1` → `PumpOnline` em ≤ 4 s; desligar o nó → `PumpOnline=false` em ≤ 4 s; alarme "Bomba externa offline" no app.
- [ ] Calibração volumétrica: S = 250/500/1000 × 60 s, réplica em 500; R² e resíduos; aplicar; conferir 10 min de perfil constante contra o recipiente (volume real × `PumpVol`).
- [ ] Após a calibração, **Potenciômetros** no app devolve os knobs (`PumpPotEnabled=true`) e a velocidade manual é esquecida; `pump_pot:0` trava.
- [ ] Acionamento com `pump_speed_ms` e app desconectado a meio: o nó para sozinho ao vencer o prazo.
- [ ] Perfil constante 5 min com `final_t` = 5: parada autônoma, `mode` volta a 0, `PumpVol` **mantido**, `PumpCycleVol` = volume do ciclo; `reset_volume` zera os dois.
- [ ] Dois perfis em sequência sem `reset_volume`: `PumpVol` acumula; PID do segundo ciclo não reage ao volume do primeiro.
- [ ] Enviar PID pelo app: eco em ≤ 3 s, persistido; reboot do nó mantém os ganhos (blob NVS).
- [ ] Desligar energia no meio de um perfil e religar: confirmar retomada automática (item 6) e decidir se é o comportamento desejado.
- [ ] Reiniciar o Hub com a bomba ligada e enviar `reset_volume`: deve ser aplicado (semeadura de `cmd_id`, Hub 2026-09-12).
- [ ] Com `sensorEnable:1` por `POST /command` local e tubo seco: motor parado; molhar: parte em ≤ 50 ms + 500 ms de trava.

---

## 2. Sensor de distância (`sensor-distancia`, firmware v11)

### 2.0 Painel de Navegação Rápida — Estado de Prontidão e Integração (v11)

> **Como navegar:** Esta matriz resume o estado real de cada funcionalidade do sensor de distância laser ToF (VL53L0X), separando o que já opera de ponta a ponta no software integrado, o que depende de ensaios com bancada e anteparo físico, e as inconsistências/lacunas identificadas na auditoria técnica do firmware v11.

#### 🟢 Totalmente Implementado e Integrado de Ponta a Ponta (Nó ↔ Hub ↔ App)
*Código compilado no nó (ESP32 core 3.3.11), roteado pelo Hub 10.2 via carona no push, exposto na interface do Windows App e aprovado na suíte de testes de contrato.*

| Funcionalidade | Nó (v11) | Hub (10.2) | App Windows | Onde Opera na UI | Testes Automatizados |
|---|:---:|:---:|:---:|---|---|
| **Comando por carona (piggyback)** | Processa corpo JSON da resposta de `GET /distance` | Enfileira na `distanceBox`; anexa na resposta HTTP 200 do push | Emite comandos ao alterar parâmetros; aguarda baixa de pendência | *Controle › Gaveta Distância* e *Controle de Espuma* | `test_piggyback_cycle_with_distance_push`, `test_distance_handler_calls_ack_and_take_reliable` |
| **Deduplicação de comando (`cmd_id`)** | Descarta reentregas com `cmd_id == g_lastCmdId`; ecoa `ack_cmd_id` | Reenvia quadro pendente até receber `ack_cmd_id` correspondente | Bloqueia novo envio com `DistanceCommandPending` ativo | *Controle › Gaveta Distância* | `test_matching_ack_clears_box`, `test_queue_and_take_reliable_delivers_payload_with_cmd_id`, `Distance_node_config_dispatches_command_and_persists_settings` |
| **Calibração de offset de montagem** | `offset_mm`: aplica compensação $d = \max(0, \text{raw} - \text{offset})$; persiste NVS | Traduz `distanceOffsetMm` $\rightarrow$ `offset_mm` com faixa $[-50, 200]\text{ mm}$ | Campo de digitação, validação de faixa e eco de confirmação | *Controle › Gaveta Distância* (Offset mm) | `test_offset_range_limits`, `Distance_node_config_validates_range_and_updates_status` |
| **Ajuste de amostragem e envio** | `sample_period` e `send_period`: $[100, 60000]\text{ ms}$; persiste NVS | Traduz `distanceSamplePeriodMs` e `distanceSendPeriodMs` | Configuração com validação de limites | *Controle › Gaveta Distância* | `test_period_range_limits`, `Distance_node_config_validates_range_and_updates_status` |
| **Restauração de padrões de fábrica** | `reset_nvs: 1`: limpa `dist_cfg` na NVS e restaura variáveis padrão | Traduz `distanceResetNvs: 1` | Comando de recuperação com confirmação | *Controle › Gaveta Distância* | `test_reset_nvs_command`, `Distance_node_config_reset_confirms_destructive_and_sends_command`, `Simulator_models_distance_node_config_and_echoes_in_telemetry` |
| **Gravação seletiva na NVS** | `saveNvsConfig()` acionada **apenas se houver mudança real** | N/A (transparente) | App recebe confirmação pelo eco | N/A (proteção de hardware) | Auditoria de código (`ConfigCodec.cpp:140`); ensaio em bancada (§2.11 item 8) |
| **Sinalização segura de falha (sentinela -1)** | Em falha de leitura I²C, envia `distance=-1` no push | `validDistance = (newDistance >= 0.0f)`; não quebra presença | Exibe aviso de sensor inválido sem desconectar nó | *Controle › Gaveta Distância* e *Alarme Antifoam* | `TelemetryParserTests.Implausible_distances_are_rejected`, `Distance_is_held_until_the_timeout_then_aged_out` |
| **Escada autônoma de recuperação I²C** | 3 níveis automáticos: soft re-init, bus clear com pulsos de SCL, power-cycle via XSHUT ⚠ D09 | N/A (tratamento autônomo local no nó) | Monitora retorno de leitura válida | Firmware local (`DistanceSensor.cpp`) | `i2cBusClear`, `maybeRecover` |
| **Registro e presença no Hub** | `GET /nodeHello?dev=distance&ver=v11&mac=...` a cada 30 s | Registra em `g_deviceRegistry`; publica `DistanceIP`, `DistanceNodeVer`, `DistanceNodeMac` | Exibe IP, MAC e versão no painel de rede da gaveta | *Controle › Gaveta Distância (Rede)* | `test_node_registry.py`, `NodeNetworkActionsTests` |
| **Diagnóstico HTTP local** | Servidor Web na porta 80 (`/diag`, `/status`, `/config`) | Proxy sob demanda em `/nodeDiag?dev=distance` | Botão "Abrir diagnóstico" abre `http://<ip>/diag` no navegador | *Controle › Gaveta Distância (Rede)* | `test_node_diag.py`, `NodeNetworkActionsTests` |

---

#### 🟡 Implementado no Software, Aguardando Ensaio Físico na Bancada
*O código está 100% integrado e aprovado estaticamente e em testes de contrato. Falta executar com o sensor VL53L0X conectado a fios reais, anteparo milimetrado, líquido e espuma.*

| Ensaio Físico a Executar | O que será comprovado na bancada | Item no Checklist (§2.11) | Status de Bancada |
|---|---|:---:|:---:|
| **Curva de linearidade e precisão do ToF** | Posicionar anteparo em distâncias conhecidas ($50, 100, 200, 500, 1000\text{ mm}$); conferir leitura reportada vs trena óptica. | Item 1 | 🟡 Pendente de ensaio |
| **Compensação de offset físico** | Medir distância física real flange-alvo, enviar `distanceOffsetMm`, conferir se a leitura exibida reflete exatamente $d = \text{raw} - \text{offset}$. | Item 2 | 🟡 Pendente de ensaio |
| **Piso de saturação (clamping em zero)** | Aproximar anteparo a uma distância menor que o offset calibrado ($\text{raw} < \text{offset}$); verificar se o nó reporta rigorosamente `0` e nunca valor negativo. | Item 3 | 🟡 Pendente de ensaio |
| **Transição e recuperação de sentinela -1** | Bloquear totalmente o feixe óptico ou desconectar o cabo I²C; verificar se a telemetria reporta imediatamente `-1`, se o Hub preserva `DistanceOnline=true` e se o app oculta a leitura sem disparar queda de conexão. | Item 4 | 🟡 Pendente de ensaio |
| **Comprovação dos 3 níveis da escada I²C** | Provocar travamento de linha (forçar SDA em LOW) e conferir no osciloscópio/analisador lógico: Nível 1 (tentativa de init), Nível 2 (16 pulsos de clock em SCL) e Nível 3 (pulso LOW de 10 ms no pino XSHUT GPIO 5). | Item 5 | 🟡 Pendente de ensaio |
| **Retenção de parâmetros após reboot** | Ajustar offset para $35,5\text{ mm}$, períodos para $500\text{ ms}$, cortar alimentação do nó, religar e verificar se os ecos retornam com os mesmos valores da NVS. | Item 6 | 🟡 Pendente de ensaio |
| **Comando de reset de fábrica (`reset_nvs`)** | Enviar `distanceResetNvs: 1` pelo app; verificar restauração imediata para offset $20,0\text{ mm}$ e períodos $1000\text{ ms}$ e confirmação do ACK. | Item 7 | 🟡 Pendente de ensaio |
| **Detecção de espuma real no vaso** | Gerar espuma no reator e verificar a resposta do feixe infravermelho de 940 nm na interface líquido-gás e o acionamento do intertravamento de dosagem de antiespumante. | Item 8 | 🟡 Pendente de ensaio |
| **Desconexão de rede e Link Watchdog** | Desligar AP do Hub; verificar se após 8 tentativas falhas de push o nó desfaz a associação e entra em reconexão não-bloqueante. | Item 9 | 🟡 Pendente de ensaio |
| **Atualização de firmware via OTA local** | Realizar upload de `.bin` compilado através do endpoint `/update` no AP `Distance Sensor` (192.168.5.1) e validar preservação da partição NVS. | Item 10 | 🟡 Pendente de ensaio |

---

#### 🔴 Lacunas, Inconsistências e Diretrizes de Arquitetura (§2.10)
*Resumo navegável dos desvios identificados na auditoria técnica entre firmware, contrato `PROTOCOL.md` e Hub 10.2.*

| ID | Tema | Inconsistência Identificada | Impacto Operacional | Correção Proposta |
|---|---|---|---|---|
| **D01** | **Acoplamento de laço** | O envio HTTP está aninhado dentro do laço de amostragem (`FirmwareApp.cpp`). | Se `sample_period > send_period`, o nó não respeita a taxa de envio pedida. | Desacoplar os temporizadores de amostragem e envio no `firmwareLoop()`. |
| **D02** | **Avanço de `cmd_id`** | `g_lastCmdId` é atualizado antes de validar o conteúdo das chaves (`ConfigCodec.cpp`). | Comandos inválidos ou desconhecidos são confirmados com ACK falso. | Atualizar `g_lastCmdId` somente quando `seen == true` ou `reset_nvs == 1`. |
| **D03** | **Faixa de envio vs Presença** | Hub aceita `send_period` até 60 s, mas derruba presença em 3 s (`DISTANCE_PRESENCE_TIMEOUT`). | Configurar envio $> 2,5\text{ s}$ faz o nó alternar ciclicamente entre online e offline. | Limitar o teto de envio para 2500 ms ou tornar o timeout do Hub proporcional a `send_period`. |
| **D04** | **Identidade OTA** | Página HTML `/update` hardcodeia `DistanceClient r10` (`LocalHttpApi.cpp`). | Inconsistência visual para o operador em bancada que acredita rodar v10. | Utilizar `BoardConfig::FirmwareTag` dinamicamente no HTML. |
| **D05** | **Validação de inteiros** | `applyInt` para parâmetros de recuperação I²C aceita qualquer valor $> 0$ sem teto. | Permite valores astronômicos via serial ou `POST /config` local. | Definir faixas lógicas plausíveis (ex: L1: 1..50, L2: 1..100, L3: 1..200). |
| **D06** | **Código HTTP em POST** | `handleConfig()` sempre responde HTTP 200 "Config Updated" mesmo em caso de erro. | Cliente local não sabe se as chaves foram aceitas ou rejeitadas. | Retornar HTTP 400 Bad Request se nenhuma chave válida for processada. |
| **D07** | **Tipo do campo `ota`** | Código emite booleano `{"ota":false}` enquanto `PROTOCOL.md` §5.3 documentava string `"false"`. | Discrepância de schema estrito em parsers externos. | Atualizar o schema de `PROTOCOL.md` para refletir o booleano real emitido. |
| **D08** | **Variável residual** | `lastGoodRawMm` é atualizada a cada leitura boa, mas nunca é exposta ou consumida. | Código morto que consome RAM e gera falsa expectativa de deglitch. | Documentar resquício ou integrar em filtro de mediana/filtro passa-baixas. |
| **D09** | **Sombreamento da escada I²C** | `maybeRecover()` avalia `failStreak >= L1` primeiro com `return;` sob cooldown compartilhado (`DistanceSensor.cpp`). | L1 intercepta sempre: Nível 2 (Bus Clear) e Nível 3 (XSHUT) são código inalcançável (morto). | Inverter a ordem de avaliação para decrescente (`L3 -> L2 -> L1`) ou individualizar cooldowns. |

---

### 2.1 O que o firmware assume do hardware

| Elemento | Pino / recurso | O que o firmware faz | Observação |
|---|---|---|---|
| **Sensor de distância laser** | STMicroelectronics VL53L0X | Medição de tempo de voo (Time-of-Flight) via laser VCSEL 940 nm classe 1 (invisível e seguro para os olhos). Endereço I²C fixo `0x29`. Modo single-shot disparado a cada `SAMPLE_PERIOD_MS`. | Mede a distância física do sensor até a superfície do líquido ou camada densa de espuma no vaso. |
| **Barramento I²C (SDA / SCL)** | `SDA = 21`, `SCL = 22` | Configura pinos como `INPUT_PULLUP` e inicia barramento a 50 kHz (`I2cFrequencyHz`). Timeout de transação fixado em 80 ms (`WireTimeoutMs`). | Frequência conservadora de 50 kHz foi escolhida para alta imunidade a ruídos eletromagnéticos de motores e cabos longos na bancada. |
| **Pino de Reset Físico (XSHUT)** | `SensorXshutPin = 5` (GPIO 5) | Inicializado como saída digital em nível `HIGH`. No nível 3 da escada de recuperação, é pulsado em nível `LOW` por 10 ms para desligar o silício do VL53L0X. | Pino ativo em nível baixo. Quando colocado em `LOW`, consome $< 5\ \mu\text{A}$ e limpa os registradores internos de hardware do sensor. |
| **Budget de medição óptico** | Configuração interna do VL53L0X | Ajustado em 200.000 µs (200 ms) via `sensor.setMeasurementTimingBudget(200000)`. | Aumenta sensibilidade e repetibilidade para alvos de baixa refletância (líquidos escuros, espumas). Impõe piso físico de 200 ms por conversão. |
| **Wi-Fi dual (AP + STA)** | Rádio ESP32 (modo `WIFI_AP_STA`) | **STA:** Conecta no canal 6 às redes do Hub (`ModuloTECNAL_1` / `ModuloTECNAL_2`).<br>**AP:** Rede aberta própria `Distance Sensor` no canal 6, IP fixo `192.168.5.1`. | Permite configuração direta por bancada via notebook/celular mesmo se o Hub estiver desligado ou em manutenção. |
| **Watchdogs de sistema** | Task WDT e Link Watchdog | **Task WDT:** Watchdog do ESP-IDF ajustado em 15 s (`WDT_TIMEOUT_S`).<br>**Link Watchdog:** Se `g_hubFailStreak >= 8`, força `WiFi.disconnect()` e reinicia varredura. | Previne travamentos permanentes por interrupções bloqueadas ou enlaces Wi-Fi "zumbis". |

---

### 2.2 Como a distância final é calculada (a cada ciclo de amostragem)

A cada passagem pelo laço de amostragem (`now - lastSampleMs >= SAMPLE_PERIOD_MS`), o firmware executa o seguinte fluxo:

```text
raw_mm = sensor.readRangeSingleMillimeters();
is_timeout = sensor.timeoutOccurred();
valid_reading = (!is_timeout && raw_mm > 0 && raw_mm < 4000 && raw_mm < 8190);

if (valid_reading) {
    failStreak = 0;
    compensated = static_cast<float>(raw_mm) - g_offsetMm;
    distance = (compensated < 0.0f) ? 0.0f : compensated;
} else {
    failStreak++;
    maybeRecover();
    distance = -1.0f;
}
```

#### Regras fundamentais do cálculo:
1. **Filtro de integridade de hardware:** leituras brutas iguais a 0, maiores ou iguais a 4000 mm (limite máximo do sensor sob condições ideais escuras) ou iguais a 8190/8191 (código de erro interno da biblioteca Pololu para overflow/fase inválida) são sumariamente rejeitadas.
2. **Compensação por offset:** a distância útil é $d = \text{raw} - \text{offset}$. O offset compensa o comprimento do flange de montagem e a distância da sonda até o nível de transbordamento.
3. **Piso de saturação em zero ($\max(0, \dots)$):** se o nível do líquido ou da espuma ultrapassar a linha de referência do offset ($\text{raw} < \text{offset}$), o valor calculado resulta negativo. O firmware trava explicitamente em `0.0f`. O app e o controle nunca receberão uma distância negativa válida.
4. **Sentinela de falha `-1.0f`:** qualquer erro de leitura, timeout de I²C ou bloqueio óptico faz o firmware atribuir `-1.0f` a `distance`. Esse valor é propagado no push HTTP como `distance=-1`.

---

### 2.3 Dinâmica de medição do VL53L0X e compensação de offset

O sensor VL53L0X opera pelo princípio de emissão de pulsos de fótons laser infravermelhos (940 nm) e contagem do tempo de retorno através de uma matriz de diodos de avalanche de fóton único (SPAD - *Single Photon Avalanche Diode*). Consequências diretas para a operação no biorreator:

1. **Independência de cor e refletância:** diferentemente de sensores ópticos baseados em intensidade refletida, a medição ToF calcula o tempo de voo real da luz. Variações na cor do meio de cultura (ex: meio translúcido vs turvo) afetam a intensidade do eco, mas não falseiam a distância em milímetros, desde que o número mínimo de fótons atinja os detectores SPAD dentro do budget de 200 ms.
2. **Comportamento diante de espuma líquida:** a espuma biológica em biorreatores atua como uma barreira física difusa. Inicialmente, com pouca espuma translúcida, o feixe laser pode atravessar e refletir no menisco do líquido. Com o adensamento da camada de espuma, a reflexão difusa passa a ocorrer no topo da espuma, reduzindo abruptamente a distância medida e permitindo ao controle Antifoam detectar a ascensão de espuma muito antes do transbordamento físico pelo condensador de exaustão.
3. **Compensação de instalação física (Offset):**
   - Em tanques de bancada, o flange de montagem posiciona o sensor alguns centímetros acima da borda superior do vaso de vidro (ex: $20\text{ mm}$).
   - O offset calibrado (`g_offsetMm`, padrão $20,0\text{ mm}$) desconta essa altura mecânica de fixação, de modo que a distância útil de líquido/espuma corresponda diretamente à folga restante até o topo do vaso.
   - A calibração de offset é editada pelo operador na interface Windows (*Gaveta Distância*) e persistida na NVS do nó.

---

### 2.4 Máquina de estados e ciclo de vida

O firmware opera em cooperação cíclica não-bloqueante no Core 1 do ESP32 (`firmwareLoop()`), com estados operacionais bem definidos:

```
               ┌──────────────┐
               │  BOOT / INIT │
               └──────┬───────┘
                      │
                      ▼
         ┌──────────────────────────┐
         │ Redes: Inicia AP + STA   │◄─────────────────────────┐
         └────────────┬─────────────┘                          │
                      │                                        │
           ┌──────────┴──────────┐                             │
           │                     │                             │
           ▼                     ▼                             │
    ┌─────────────┐       ┌─────────────┐                      │
    │ WF_SCANNING │       │ WF_CONNECT  │                      │
    └──────┬──────┘       └──────┬──────┘                      │
           │                     │                             │
           └──────────┬──────────┘                             │
                      │ Conectado ao Hub                       │
                      ▼                                        │ Link Watchdog
          ┌───────────────────────┐                            │ (8 falhas seguidas)
          │   REGISTRO /nodeHello │                            │
          └───────────┬───────────┘                            │
                      │                                        │
                      ▼                                        │
┌───────────────────────────────────────────────┐              │
│                 LAÇO OPERACIONAL              │              │
│                                               │              │
│  1. Atende requisições Web locais (Porta 80)  │              │
│  2. Trata comandos seriais USB (se houver)    │              │
│  3. Amostragem VL53L0X a cada SAMPLE_PERIOD_MS │              │
│     ├── Sucesso: distance compensada          │              │
│     └── Falha: distance = -1, maybeRecover()  │              │
│  4. Push HTTP a cada SEND_PERIOD_MS (carona)  │──────────────┘
│     └── Recebe JSON -> processConfigUpdate()  │
└───────────────────────────────────────────────┘
```

| Estado | O que está ativo | Condição de saída | Comportamento das saídas / rede |
|---|---|---|---|
| `BOOT / INIT` | Leitura NVS, `i2cInit()`, `sensorInit()`, criação do AP 192.168.5.1 | Inicialização concluída | Sensor alimentado; serial emite banner `DistanceClient r11`. |
| `WF_SCANNING` | Varredura assíncrona por `ModuloTECNAL_1` ou `ModuloTECNAL_2` | Redes encontradas ou timeout | AP local ativo para acesso direto; sem comunicação com o Hub. |
| `WF_CONNECTING` | Tentativa de associação no canal 6 | `WL_CONNECTED` ou timeout de conexão | Tenta estabelecer enlace IP com o Hub. |
| `OP_NORMAL` | Amostragem a cada `SAMPLE_PERIOD_MS` e push HTTP a cada `SEND_PERIOD_MS` | Falha de hardware ou queda de rede | Executa leituras, atualiza telemetria local e empurra dados para o Hub. |
| `RECOVERING` | Escada de 3 níveis acionada por falhas consecutivas (`failStreak`) | Sucesso na leitura ou saturação de tentativas | Executa recuperação em degraus sem travar o laço principal. |
| `OTA_UPDATE` | Gravação multipart na partição OTA via `/update` | Sucesso ou aborto por watchdog (90 s) | Suspende temporariamente o laço de medição para proteger gravação flash. |

---

### 2.5 Por onde um comando pode chegar

| Canal | Formato / Endereço | `cmd_id` | Deduplicação | Observações |
|---|---|:---:|:---:|---|
| **Carona no Push (Hub $\rightarrow$ Nó)** | Corpo JSON da resposta HTTP 200 de `GET /distance` | **Sim** | Sim: se `cmd_id == g_lastCmdId`, ignora reentrega. | **Canal operacional padrão do sistema.** Não exige porta cliente aberta no nó; aproveita a resposta do push já emitido a cada segundo. |
| **HTTP POST direto (`/config`)** | `POST http://192.168.5.1/config` (ou IP STA do nó) com corpo JSON | Não | Não: aplica imediatamente. | Sem autenticação. Útil para comissionamento inicial ou ensaios diretos de bancada. |
| **Serial USB** | `115200 bps`, linha JSON iniciada por `{` e terminada em `\n` | Opcional | Sim se `cmd_id > 0`; ignora deduplicação se omitido. | Buffer de linha lido a cada ciclo de `firmwareLoop()`. |
| **Interface Web OTA** | `POST /update` na porta 80 do nó | Não | N/A | Upload multipart de arquivo `.bin`. Intertravamento bloqueia upload de imagens espúrias (`merged`, `bootloader`, `partitions`). |

---

### 2.6 Catálogo de chaves e o que cada uma faz

#### 2.6.1 Comandos e parâmetros roteados pelo Hub (Carona no Push)

As chaves abaixo são emitidas pelo Windows App ou por scripts REST no Hub. O Hub valida as faixas, empacota na `distanceBox` e entrega no próximo push do nó:

| Chave no Hub / App | Chave no Nó | Tipo | Faixa permitida | Padrão | O que o firmware faz | Efeito no Hardware | Persistência e Eco |
|---|---|:---:|:---:|:---:|---|---|:---:|
| `distanceOffsetMm` | `offset_mm` | float | `[-50.0, 200.0]` mm | `20.0` | Atualiza `g_offsetMm`. Aplica no cálculo: $d = \max(0, \text{raw} - \text{offset})$. | Altera a compensação de nível em relação ao flange. | Persiste em NVS (`dist_cfg/offset_mm`) se mudou. Volta no push como `&offset=%.2f`. |
| `distanceSamplePeriodMs` | `sample_period` | uint32 | `[100, 60000]` ms | `1000` | Atualiza `SAMPLE_PERIOD_MS`. Define o intervalo entre disparos single-shot do VL53L0X. | Altera a frequência de chaveamento do laser ToF. | Persiste em NVS (`dist_cfg/sample_ms`) se mudou. Volta no push como `&sample_ms=%lu`. |
| `distanceSendPeriodMs` | `send_period` | uint32 | `[100, 60000]` ms ⚠ | `1000` | Atualiza `SEND_PERIOD_MS`. Define o intervalo base entre requisições HTTP GET ao Hub. | Altera a periodicidade de rádio e tráfego na rede. | Persiste em NVS (`dist_cfg/send_ms`) se mudou. Volta no push como `&send_ms=%lu`. ⚠ Valores $> 2500\text{ ms}$ conflitam com a presença no Hub (§2.10). |
| `distanceResetNvs` | `reset_nvs` | int | Somente `1` | — | Apaga todo o namespace `dist_cfg` na NVS e restaura variáveis padrão na RAM. Mantém `g_lastCmdId`. | Reinicia parâmetros sem reiniciar o microcontrolador. | NVS limpa; parâmetros restaurados para $20,0\text{ mm}$ e $1000\text{ ms}$. Ecoa novos valores no push seguinte. |

#### 2.6.2 Parâmetros avançados de barramento I²C (Canais locais: POST /config e Serial USB)

> [!NOTE]
> Estas chaves **não passam pelo Hub** (são descartadas pelo filtro de comandos do Hub). Elas existem no nó para permitir ajuste fino das tolerâncias elétricas da linha I²C durante a caracterização em laboratório:

| Chave no Nó | Tipo | Faixa | Padrão | Comportamento do Firmware |
|---|:---:|:---:|:---:|---|
| `cooldown_soft` | uint32 | $\ge 1\text{ ms}$ | `15000` ms | Tempo mínimo de espera entre tentativas consecutivas de Nível 1 (Soft Re-init). |
| `cooldown_bus` | uint32 | $\ge 1\text{ ms}$ | `15000` ms | Tempo mínimo de espera entre tentativas consecutivas de Nível 2 (Bus Clear). |
| `cooldown_xshut` | uint32 | $\ge 1\text{ ms}$ | `30000` ms | Tempo mínimo de espera entre tentativas consecutivas de Nível 3 (Power-Cycle via XSHUT). |
| `l1_reinit` | int | $\ge 1$ | `5` | Número de falhas consecutivas de leitura (`failStreak`) para disparar o Nível 1. |
| `l2_clear` | int | $\ge 1$ | `10` | Número de falhas consecutivas de leitura (`failStreak`) para disparar o Nível 2. |
| `l3_xshut` | int | $\ge 1$ | `20` | Número de falhas consecutivas de leitura (`failStreak`) para disparar o Nível 3. |

---

### 2.7 Persistência e recuperação

#### 2.7.1 Persistência em Flash NVS (`dist_cfg`)
- O firmware utiliza a biblioteca `Preferences` do ESP32 sob o namespace `"dist_cfg"`.
- **Gravação inteligente em mudança:** o código compara cada parâmetro recebido com o valor vigente na RAM antes de persistir. Apenas se pelo menos um parâmetro tiver valor novo o método `saveNvsConfig()` abre a partição NVS para escrita (`Preferences::begin("dist_cfg", false)`). Isso elimina o desgaste prematuro da flash NOR decorrente de reenvios cíclicos de comandos idênticos.
- **Carregamento no Boot:** em `loadNvsConfig()`, o namespace é aberto em modo somente leitura. Se uma chave específica não existir (primeira energização ou pós-reset), o valor padrão pré-compilado em RAM é preservado.

#### 2.7.2 Escada de Recuperação de Barramento I²C (3 Níveis)
Se o sensor VL53L0X parar de responder ou corromper transações I²C durante a operação, a rotina `maybeRecover()` executa um plano de resgate escalonado:

```text
failStreak
   ▲
20 ┼─────────────────────────────────────────────► Nível 3: XSHUT Reset (GPIO 5 LOW 10ms -> HIGH)
   │                                               Cooldown: 30 s
10 ┼───────────────────────► Nível 2: I2C Bus Clear (16 pulsos SCL) + Wire.begin()
   │                         Cooldown: 15 s
 5 ┼─► Nível 1: Soft Re-init (sensorInit())
   │   Cooldown: 15 s
 0 ┴─────────────────────────────────────────────► Operação Normal (failStreak zerado a cada sucesso)
```

1. **Nível 1 — Soft Re-init (`failStreak >= 5`):** Chama `sensorInit()`, reexecutando até 3 vezes a rotina de inicialização lógica da biblioteca Pololu e reconfigurando os timeouts.
2. **Nível 2 — Bus Clear + Re-init (`failStreak >= 10`):** Se o barramento estiver travado (escravo retendo SDA em nível baixo), o firmware configura SCL como saída e emite até 16 pulsos manuais de clock para forçar o escravo a liberar a linha de dados. Em seguida, gera uma condição de STOP, encerra o driver com `Wire.end()`, reinicializa a porta I²C com `Wire.begin(21, 22, 50000)` e chama `sensorInit()`.
3. **Nível 3 — Hardware Power-Cycle via XSHUT (`failStreak >= 20`):** Se o sensor estiver com a máquina de estados interna em latch-up, o firmware puxa o pino GPIO 5 (`SensorXshutPin`) para `LOW` por 10 ms (forçando o silício do sensor em shutdown), religa com `HIGH` por 10 ms, reinicia o periférico I²C do ESP32 e reexecuta `sensorInit()`.

> [!WARNING]
> **⚠ Inconsistência Crítica de Implementação no Firmware v11 (D09):**  
> No código ativo de `DistanceSensor.cpp:57-93`, a rotina `maybeRecover()` avalia os níveis em ordem ascendente (`if (failStreak >= L1) ... return;` seguido de L2 e L3) utilizando a variável única de tempo `lastRecovery`. Como qualquer sequência de falhas $\ge 10$ ou $\ge 20$ é estritamente maior que 5, e `COOLDOWN_SOFT_MS` (15 s) $\le$ `COOLDOWN_BUS_MS` (15 s) $<$ `COOLDOWN_XSHUT_MS` (30 s), a condição de L1 é **sempre** satisfeita primeiro, executando o `return;`.  
> Consequência: no firmware v11 atual, **L2 (Bus Clear) e L3 (XSHUT Power-Cycle) são código morto/inalcançável**. Travamentos elétricos de barramento que exijam pulsos de clock em SCL ou reset físico de hardware no pino XSHUT nunca são recuperados autonomamente. A correção (prevista no plano) requer avaliar os degraus em ordem decrescente de severidade (`L3 -> L2 -> L1`) ou segregar cronômetros de cooldown individuais.

#### 2.7.3 Resiliência de Rede (Link Watchdog e Backoff)
- **Backoff exponencial:** a cada falha de envio HTTP para o Hub, o intervalo entre tentativas dobra gradativamente ($1\text{ s} \rightarrow 2\text{ s} \rightarrow 4\text{ s} \rightarrow 8\text{ s} \rightarrow 15\text{ s}$), com teto máximo de $15\text{ s}$ (`MAX_HUB_BACKOFF_MS`), evitando saturar a CPU e a rede.
- **Link Watchdog:** se ocorrerem 8 falhas consecutivas de comunicação com o Hub (`g_hubFailStreak >= 8`), o firmware detecta enlace fantasma/zumbi, desconecta explicitamente o rádio Wi-Fi (`WiFi.disconnect(true, false)`), zera flags de presença e reinicia o processo assíncrono de varredura e reconexão.

---

### 2.8 Telemetria: o que sai do nó e como chega ao app

A telemetria é transmitida pelo nó via requisição HTTP GET periódica para `http://192.168.4.1/distance?...`:

| Parâmetro no Push | Origem no Firmware | Campo no JSON do Hub | No Windows App | Significado Real |
|---|---|---|---|---|
| `distance` | `static_cast<int>(distance)` | `Distance` | `Readings.Distance` | Distância compensada em mm: $\max(0, \text{raw} - \text{offset})$. Sentinela `-1` se o sensor falhar. |
| `time` | `millis() / 1000.0f` | — | — | Tempo de atividade (*uptime*) do nó em segundos. O Hub recebe e guarda na variável interna C++ `distanceSensorTime`, mas não expõe esta chave no JSON de `/readData`. |
| `offset` | `g_offsetMm` | `DistanceOffsetMm` | `AppliedOffsetText` | Eco do offset em mm atualmente aplicado e gravado em NVS. |
| `sample_ms` | `SAMPLE_PERIOD_MS` | `DistanceSamplePeriodMs` | `SamplePeriodMsText` | Eco do período de aquisição óptica atual em milissegundos. |
| `send_ms` | `SEND_PERIOD_MS` | `DistanceSendPeriodMs` | `SendPeriodMsText` | Eco do período de envio HTTP atual em milissegundos. |
| `ack_cmd_id` | `g_lastCmdId` | Fecha pendência na `distanceBox` | `DistanceCommandPending` cai para `false` | Recibo de confirmação da última revisão de comando processada. |

#### Chaves sintetizadas pelo Hub e pelo App:
- `DistanceOnline`: derivada no Hub como `true` se `distanceSensorCommOn == true` e o último push tiver ocorrido há $\le 3000\text{ ms}$ (`DISTANCE_PRESENCE_TIMEOUT`).
- `DistanceCommEnabled`: eco no quadro de telemetria do interruptor geral da porta de comunicação de distância no Hub.
- `validDistance`: derivada internamente no Hub; a chave `Distance` **só é incluída no JSON do Hub se `newDistance >= 0.0f`**. Se o nó enviar `distance=-1`, a chave `Distance` é omitida do quadro, mas `DistanceOnline` e os ecos de configuração (`DistanceOffsetMm`, etc.) continuam normalmente presentes.
- `DistanceIP`, `DistanceNodeVer`, `DistanceNodeMac`: registrados no Hub a partir do handshake periódico `/nodeHello` (a cada 30 s) e expostos na tabela de nós de rede.

---

### 2.9 Procedimentos do operador no aplicativo (e o que acontece no fio)

| Procedimento | Onde no App | Quadro JSON emitido (App $\rightarrow$ Hub) | O que confirmar na tela |
|---|---|---|---|
| **Habilitar sensor de distância** | Controle › Linha Distância (Interruptor de Comunicação) | `{"distanceSensorCommOn":1}` | Chip `DistanceOnline=true` em até 3 s; exibição do valor numérico de distância em mm. |
| **Ajustar configuração do nó (Offset / Amostragem / Envio)** | Controle › Gaveta Distância › Expander *Configuração do nó* $\rightarrow$ botão *Enviar configuração do nó* | `{"distanceOffsetMm":25.5,"distanceSamplePeriodMs":500,"distanceSendPeriodMs":1000}` (ou as chaves alteradas via `CommandBuilders.DistanceConfig`) | Chip `DistanceCommandPending` acende e apaga em $\le 2\text{ s}$; campos de telemetria ecoam os novos valores aplicados. ⚠ Não configurar `send_period > 2500 ms`. |
| **Restaurar padrões de fábrica** | Controle › Gaveta Distância › Expander *Configuração do nó* $\rightarrow$ botão *Restaurar padrões do nó* (confirmação modal) | `{"distanceResetNvs":1}` | Offset retorna para `20.0 mm`; períodos retornam para `1000 ms`; status confirma sincronismo. |
| **Acessar diagnóstico de rede** | Controle › Gaveta Distância, cartão *Rede* $\rightarrow$ botão *Abrir diagnóstico* | N/A (navegador abre `http://<DistanceIP>/diag`) | Página JSON direta do nó exibindo heap livre, RSSI, uptime, `hub_fail_streak` e `last_cmd_id`. |
| **Controle de Espuma (Antifoam)** | Controle › Gaveta Distância (campos de processo: Atraso, Pulso, Intervalo, Referência) | Parâmetros de processo do Hub (`foamStartDelay_s`, `foamPulse_s`, `foamInterval_s`, `distanceReference`) | Se a distância cair abaixo da referência, o Hub aciona o ciclo antiespumante pulsado na bomba de nutrientes ou quebra mecânica. |

---

### 2.10 Limitações, inconsistências e decisões (auditoria do firmware v11 vs contrato)

Esta seção documenta formalmente os desvios encontrados entre o código-fonte ativo (`FirmwareApp.cpp`, `ConfigCodec.cpp`, `LocalHttpApi.cpp`), o contrato de protocolo (`PROTOCOL.md`) e a integração no Hub 10.2:

| # | Achado na Auditoria | Consequência no Sistema | Justificativa / Decisão Técnica | Ação Corretiva Proposta |
|---|---|---|---|---|
| **D01** | **Aninhamento do envio na amostragem** (`FirmwareApp.cpp:118,151`) | O bloco de verificação `now - lastSendMs >= currentSendInterval` está indentado dentro de `now - lastSampleMs >= SAMPLE_PERIOD_MS`. Se o operador configurar `sample_period = 5000` e `send_period = 1000`, o nó **só transmitirá a cada 5 segundos**. | Erro de controle de fluxo de laço: o envio de telemetria ficou escravo do ciclo do sensor óptico em vez de ser um timer independente. | Desacoplar os dois temporizadores no `firmwareLoop()`, tornando a máquina de amostragem e a máquina de envio laços paralelos independentes. |
| **D02** | **Avanço prematuro de `g_lastCmdId`** (`ConfigCodec.cpp:94`) | `g_lastCmdId = static_cast<uint32_t>(cmdId)` é executado antes de processar as chaves do payload. Se um payload contiver `cmd_id:5` com chaves desconhecidas ou inválidas, o nó devolve `ack_cmd_id=5` no push seguinte. | O Hub considera o comando aprovado e remove da caixa (`distanceBox`), mas nenhuma alteração física foi aplicada no nó. | Atualizar `g_lastCmdId` apenas no final do processamento, condicionando a `seen == true` (ao menos uma chave válida aplicada) ou `reset_nvs == 1`. |
| **D03** | **Incompatibilidade de teto de `send_period` vs Presença** | Nó e Hub aceitam `send_period` de até $60000\text{ ms}$, porém o Hub derruba a presença (`DistanceOnline=false`) se ficar $> 3000\text{ ms}$ sem push (`DISTANCE_PRESENCE_TIMEOUT`). | Se o operador configurar `send_period` para $5000\text{ ms}$, o nó passará 2 segundos offline a cada ciclo de 5 segundos, gerando falsos alarmes e perda dos ecos de telemetria. | Documentar limitação operacional ($\le 2500\text{ ms}$). No Hub/App, restringir a validação para $[100, 2500]\text{ ms}$, ou tornar o timeout do Hub proporcional a $2,5 \times \text{send\_period}$. |
| **D04** | **Versão hardcoded no template HTML OTA** (`LocalHttpApi.cpp:17`) | A página Web servida em `GET /update` contém fixo o texto `<p>Running: <b>DistanceClient r10</b></p>`, enquanto o nó roda `v11` (`DistanceClient r11`). | Induz o operador em bancada a supor que o upload do firmware v11 falhou ou não foi gravado. | Substituir a literal estática pela constante já definida `BoardConfig::FirmwareTag`. |
| **D05** | **Falta de teto superior em `applyInt`** (`ConfigCodec.cpp:70`) | Parâmetros de recuperação `l1_reinit`, `l2_clear`, `l3_xshut` validam apenas `value <= 0 return false;`. Aceitam valores arbitrários como $2 \times 10^9$. | Não há risco imediato de crash, mas valores aberrantes desabilitam na prática a escada de recuperação do sensor. | Definir tetos seguros: `l1_reinit` $\in [1, 50]$, `l2_clear` $\in [1, 100]$, `l3_xshut` $\in [1, 200]$. |
| **D06** | **HTTP 200 incondicional no POST local** (`LocalHttpApi.cpp:52`) | `handleConfig()` sempre responde `HTTP 200 "Config Updated"`, sem avaliar o retorno booleano de `processConfigUpdate()`. | Clientes HTTP ou scripts de calibração locais não detectam envio de JSON inválido ou valores fora de faixa. | Responder `HTTP 400 "Bad Request - Invalid Keys or Range"` quando `processConfigUpdate()` retornar `false`. |
| **D07** | **Discrepância de tipo no campo `ota` em `/diag`** | `snprintf` formata `"\"ota\":%s"` com `true/false`, gerando JSON booleano `{"ota":false}`. O documento `PROTOCOL.md` §5.3 mostrava `"ota":"false"` (string). | Inconsistência de documentação formal de contrato. O App e simuladores consomem sem falha. | Atualizar o exemplo em `PROTOCOL.md` para refletir o booleano emitido no código real. |
| **D08** | **Variável residual sem consumo** (`FirmwareApp.cpp:28,125`) | A variável `lastGoodRawMm` é alimentada a cada leitura válida, mas nunca é transmitida no push, nem exposta em `/diag`, nem usada para deglitch. | Resquício de versões legadas de caracterização em bancada. | Documentar o estado inócuo da variável no plano de implementação e mantê-la ou integrá-la a filtro de deglitch futuro. |
| **D09** | **Sombreamento da escada de recuperação I²C** (`DistanceSensor.cpp:57-93`) | `maybeRecover()` avalia `failStreak >= L1` primeiro e chama `return;`. Sob parâmetros padrão com `lastRecovery` compartilhado, a condição de L1 é sempre satisfeita antes de L2 e L3. | **Nível 2 (Bus Clear) e Nível 3 (XSHUT Power-Cycle) são inalcançáveis (código morto)**. Se a linha I²C travar ou o sensor entrar em latch-up, o nó nunca recupera. | Inverter a ordem de avaliação para decrescente (`L3 -> L2 -> L1`) ou segregar cronômetros de cooldown individuais para cada degrau. |

---

### 2.11 Checklist de bancada (fecha os itens acima)

- [ ] **Identificação e Boot:** Conectar o nó à porta serial a 115200 bps; certificar exibição do banner `DistanceClient r11 (AP+STA, Configurable, Non-Blocking)`.
- [ ] **Presença no Hub:** Ligar `distanceSensorCommOn: 1`; confirmar no aplicativo `DistanceOnline=true` e `DistanceCommandPending=false` em $\le 3\text{ s}$.
- [ ] **Leitura Nominal de Bancada:** Posicionar anteparo plano branco a $200\text{ mm}$ do sensor; verificar se `Distance` estabiliza em $180\text{ mm}$ ($\pm 5\text{ mm}$) com offset padrão de $20\text{ mm}$.
- [ ] **Piso Zero (Clamping):** Posicionar o anteparo a $10\text{ mm}$ do sensor (menor que o offset de $20\text{ mm}$); confirmar se `Distance` exibe exatamente `0 mm` e nunca valor negativo.
- [ ] **Sinalização de Falha (-1):** Bloquear a janela óptica do VL53L0X com fita opaca; confirmar no log serial e no push a emissão de `distance=-1`, e no app a supressão segura da leitura sem queda de `DistanceOnline`.
- [ ] **Escada de Recuperação I²C (Níveis L1, L2, L3) ⚠ D09:** Com o sensor em falha induzida, monitorar no log serial os disparos. *Nota de bancada:* no firmware v11 original (sem patch D09), apenas o Nível 1 dispara devido ao sombreamento; após aplicação do patch D09, verificar o escalonamento nominal: `[RECOVER] soft re-init` (L1, 5 falhas), `[RECOVER] bus clear` (L2, 10 falhas) e `[RECOVER] XSHUT power-cycle` (L3, 20 falhas).
- [ ] **Deduplicação de Comandos:** Disparar comando `{"distanceOffsetMm":25.5}` duas vezes consecutivas; confirmar no monitor serial que a segunda requisição emite `[CMD] cmd_id=... ja aplicado; ignorando reentrega.`
- [ ] **Desgaste de Flash (NVS idempotente):** Enviar novo comando com o mesmo offset já vigente ($25,5\text{ mm}$); conferir log `[CMD] Parametros ja vigentes; nada persistido.` e ausência de ciclos de escrita flash.
- [ ] **Persistência Pós-Queda de Energia:** Alterar offset para $32,0\text{ mm}$, desconectar cabo de alimentação do nó, aguardar 10 s, religar e confirmar se o push HTTP reenvia `offset=32.00` automaticamente.
- [ ] **Restauração de Fábrica (`reset_nvs`):** Enviar `{"distanceResetNvs":1}`; validar retorno imediato de `offset=20.00`, períodos para `1000 ms` e limpeza do namespace `dist_cfg`.
- [ ] **Desacoplamento de Períodos (D01):** Configurar `sample_period = 500 ms` e `send_period = 1000 ms`; comprovar com analisador de tráfego/Wireshark se os pushes ocorrem na cadência regular de 1 Hz.
- [ ] **Link Watchdog:** Desconectar o ponto de acesso Wi-Fi do Hub; verificar se após 8 falhas de push o nó força `WiFi.disconnect()` e reinicia varredura não-bloqueante mantendo o AP `Distance Sensor` (192.168.5.1) ativo.
- [ ] **Atualização OTA:** Acessar `http://192.168.5.1/update` via navegador no AP local, selecionar `distance-sensor.ino.bin` e validar upload, integridade da imagem e reinicialização autônoma.

---

## 3. Fluxômetro (`fluxometro`, firmware ativo com identificação divergente V10/v11)

### 3.0 Painel de Navegação Rápida — Estado de Prontidão e Integração

> **Como navegar:** esta matriz resume o estado real do fluxômetro. 🟢 significa que o caminho de software existe e está integrado; 🟡 significa que o código está pronto, mas a resposta do equipamento real ainda precisa ser ensaiada; 🔴 identifica lacuna, inconsistência ou decisão de escopo registrada para correção futura.

#### 🟢 Implementado e Integrado de Ponta a Ponta (Nó ↔ Hub ↔ App)

*Caminhos presentes no firmware ativo, roteados pelo Hub 10.2, expostos no App Windows e cobertos por testes automatizados. Isso comprova o contrato de software, não a resposta física.*

| Funcionalidade | Nó | Hub 10.2 | App Windows | Onde opera | Evidência automatizada |
|---|---|---|---|---|---|
| **Comando confiável com ACK** | Deduplica `cmd_id`; ecoa `ack_cmd_id`, origem e instante de aplicação | Mantém o estado desejado e reenvia até ACK | Bloqueia nova atuação enquanto `FlowCommandPending` está ativo | Controle, receitas e ensaios | `FlowmeterV05SyncTests`, `ConnectionManagerTests`, `test_node_commands.py` |
| **Controle de vazão** | Rampa + feedforward + PI comandam o MCP4725; ADS1115 fecha a malha | Traduz `flowSetpoint`/`maxFlow` para o contrato do nó | Setpoint, teto e estado aplicado com confirmação | *Controle › Vazão de Ar* | `FlowmeterV05SyncTests`, `ControlViewModelTests` |
| **Roteamento A/B/C** | Aciona GPIO 17/16 e corte geral GPIO 5 | Sempre entrega setpoint e os três bits como estado completo | Traduz intenção Reator/Descarga para o par de saídas configurado | *Controle › Válvulas* e *Configurações › Gás e válvulas* | `GasRoutingTests`, `GasRouteProducersTests`, `GasRouteUiTests` |
| **Parada segura completa** | Aceita alvo zero, v1/v2=0 e `v_Flow=1` | Enfileira mesmo offline ou com a malha desabilitada | Construtor único usado por controle, receitas, kLa, potência e parada global | Todos os fluxos que detêm a aeração | `FlowSafeStop_is_unchanged_and_reads_as_Closed`, `KlaRunnerSimulatorTests` |
| **Sintonia PI/FF/rampa** | Aceita, persiste e ecoa Kp, Ki, ganho/offset FF e rampa | Traduz e publica os ecos | Valida, envia e mostra valores aplicados | *Controle › Sintonia do controlador* | `test_mailboxes_flow_tuning_serialization_and_queueing`, testes de `FlowControlViewModel` |
| **Monitoramento e presença** | Push a cada 500 ms, `boot_id`, `/diag` e `/status` | Publica vazão, tensão, setpoint, válvulas, ACK, versão/MAC/IP e offline após 6 s | Telemetria, chips de pendência/offline, alarmes e tabela de nós | *Controle*, *Sinótico* e *Configurações › Rede* | `TelemetryParserTests`, `AlarmServiceTests`, `HubNodesViewModelTests` |
| **Calibração assistida em dois segmentos** | Aplica curva baixa quartic e alta quadrática; grava EEPROM | Repassa oito coeficientes e `maxFlow` | Captura média de tensão, ajusta curva, salva pontos e envia | *Calibrações › Fluxômetro* | `CalibrationTests`, `GasRigSettingsTests` |
| **Arbitragem entre operadores** | Executa o quadro recebido | Preserva o último estado desejado | Impede comando manual concorrente com receita/ensaio que detenha a aeração | Controle, receitas, kLa e potência | `CommandArbiterTests`, testes dos runners |

---

#### 🟡 Implementado no Software, Aguardando Ensaio Físico na Bancada

*As rotinas existem, mas ainda não demonstram polaridade, vazão, temporização, persistência ou segurança no conjunto real MFC + DAC + ADC + solenoides.*

| Ensaio físico | O que deve ser comprovado | Checklist (§3.11) | Estado |
|---|---|:---:|:---:|
| **Identificação do binário** | Registrar hash e confirmar qual identidade V10/v11 corresponde ao firmware gravado | Item 1 | 🟡 Pendente |
| **Polaridade e rota A/B/C** | Confirmar GPIO 5/17/16, *Valve Off*, qual entrada aciona A e qual aciona B+C | Item 2 | 🟡 Pendente |
| **Boot em estado seguro** | Verificar corte geral fechado, rotas desenergizadas e DAC zero antes da comunicação | Item 3 | 🟡 Pendente |
| **Latência e recuperação do comando** | Medir app → Hub → aplicação → ACK → resposta mecânica com perda/reconexão | Item 4 | 🟡 Pendente |
| **Parada segura real** | Partindo de vazão alta e de cada rota, comprovar fechamento e vazão física zero | Item 5 | 🟡 Pendente |
| **Retomada com `dac_hold`** | Medir retenção do DAC, pico de retomada e comportamento do integrador em 1 e 0 | Item 6 | 🟡 Pendente |
| **Faixa 0–0,1 L/min** | Caracterizar a permanência do DAC e impedir uso operacional até decisão sobre F04 | Item 7 | 🟡 Pendente |
| **Calibração e persistência** | Ajustar com padrão externo, usar réplicas e validar pontos independentes após reboot | Itens 8 e 9 | 🟡 Pendente |
| **Falha de ADS1115/MCP4725** | Desconectar cada módulo e registrar telemetria, alarme e estado físico resultante | Item 10 | 🟡 Pendente |
| **Reboots nó/Hub** | Confirmar quando o Hub reimpõe o estado anterior e o que ocorre no primeiro contato | Item 11 | 🟡 Pendente |
| **OTA segura** | Testar imagem válida/rejeitada, interrupção, watchdog e permanência das saídas | Item 12 | 🟡 Pendente |

---

#### 🔴 Lacunas e Inconsistências para Correção Futura

*Resumo navegável dos achados detalhados e numerados em §3.10. Nenhum deles foi corrigido nesta revisão documental.*

| Tema | O que não está consistente ou integrado | Referência |
|---|---|---|
| **Versão do firmware** | Build/OTA dizem V10; endpoints e protocolo dizem v11 | F01 |
| **Fonte da calibração** | Firmware, App Windows e Flutter carregam curvas “padrão” diferentes; curva e `maxFlow` não têm readback completo | F02, F09–F11 |
| **Unidades na interface** | `FlowOutput` é equivalente L/min, mas o Windows mostra `V` | F03 |
| **Região de baixo setpoint** | 0 < alvo ≤ 0,1 L/min não fecha a linha e não executa PI | F04 |
| **Controle direto** | Setpoint positivo não abre o corte; “Flow Valve ON” no Flutter significa linha fechada | F05 |
| **Intertravamento e confirmação física** | Nó aceita rota morta/dupla e ecoa apenas o bit comandado, sem readback da válvula | F06, F16 |
| **Validação de comandos** | Parâmetros podem aceitar valores perigosos; parser permissivo pode aplicar quadro parcial | F07, F08 |
| **Saúde de I²C** | Falha de ADC/DAC não gera estado seguro nem alarme supervisório | F12 |
| **Segurança de rede e OTA** | AP, comando e OTA não têm autenticação; OTA pode manter a saída ativa | F13, F14 |
| **Recuperação Wi-Fi** | `reconnect_wifi` pode ser desligado, mas o Windows apenas monitora e não oferece reabilitação | F15 |

---

### 3.1 Hardware assumido pelo firmware

| Elemento | Pino / recurso | Ação do firmware | O que o hardware deve fazer |
|---|---|---|---|
| MFC Omega FMA-5400 | setpoint analógico pelo DAC | Converte a saída de controle, em equivalente de L/min, para `0..4095` proporcionalmente a `maxFlowRate` | Modular a vazão do gás; com `maxFlowRate=50`, 50 L/min corresponde ao fundo de escala do DAC |
| MCP4725 | I²C `0x60` | Escreve o setpoint; não salva na EEPROM interna do DAC | Produzir a tensão de comando do MFC. `flow_output` é equivalente de L/min, **não volts** |
| ADS1115 | I²C `0x48`, A3, ganho ±2/3, 128 SPS | Média de 16 conversões (~125 ms) e passa-baixas α=0,5 | Ler o retorno analógico do MFC; taxa efetiva aproximada de 7 leituras/s |
| Corte geral *Valve Off* | GPIO 5 | `1/HIGH` aciona o corte; `0/LOW` libera | Fechar/abrir a linha geral. A função e a polaridade reais ainda exigem bancada |
| Saída de válvula 1 | GPIO 17 | `1/HIGH` energiza; `0/LOW` desenergiza | Acionar a entrada 1; no arranjo padrão do app, B+C (N₂ + descarga) |
| Saída de válvula 2 | GPIO 16 | `1/HIGH` energiza; `0/LOW` desenergiza | Acionar a entrada 2; no arranjo padrão do app, A (ar para o reator) |
| LED receptor | GPIO 19 | Quatro alternâncias a cada 200 ms após comando reconhecido | Indicar aceitação pelo parser; não comprova resposta física |
| Wi-Fi | AP+STA no canal 6 | AP aberto `Floxometro_AP`, `192.168.10.1`; STA tenta `ModuloTECNAL_1` e `_2`, usando o SSID também como senha | Permitir controle direto e via Hub com um único rádio, sem varredura ampla |

`Wire.begin()` não declara pinos: SDA/SCL seguem o padrão da placa ESP32. Escala elétrica do MFC, terra comum, alimentação e polaridade/mapeamento das solenoides são pré-condições de bancada.

⚠ **Versão inconsistente:** o banner serial e a página OTA usam `FW_VERSION "V10"`; `/nodeHello`, `/diag`, `/status` e `fluxometro/docs/PROTOCOL.md` anunciam `v11`; `README.md` e `CURRENT_STATUS.md` dizem v10. Aqui, “firmware ativo” significa exatamente `fluxometro/firmware/flowmeter`, sem inferir o binário gravado pelo rótulo.

### 3.2 Como o comando produz a saída

```text
targetFlowSetpoint = pedido real limitado a 0..maxFlowRate
rampedTarget       = referência que caminha até o pedido a rampRate L/min/s
flowFeedforward    = clamp(ffGain · rampedTarget + ffOffset, 0, maxFlowRate)
flowSetpoint       = clamp(flowFeedforward + Kp·erro + Ki·integral, 0, maxFlowRate)
DAC                = flowSetpoint/maxFlowRate · 4095
```

- A malha roda a cada 100 ms, mas só regula com `targetFlowSetpoint > 0,1` e `v_Flow=0`.
- O erro é `rampedTarget − readFlowRate`; dentro de ±0,01 L/min, PI e DAC não são atualizados.
- `ramp_rate=0` aplica degrau; valor positivo limita a variação em L/min/s.
- Com alvo >0,1 e `v_Flow=1`, DAC, rampa e integrador ficam congelados.
- Com `dac_hold=1` (padrão), alvo exatamente zero fecha `v_Flow`, mas preserva DAC/rampa/PI. Com `dac_hold=0`, também zera DAC e rampa.
- Setpoint positivo **não abre** `v_Flow` no nó. O Hub infere a abertura apenas quando recebe `flowSetpoint` sem `v_Flow`; cliente direto precisa comandá-la.

### 3.3 Todas as interações que o fluxômetro pode receber

| Canal | Formato / endereço | Capacidade | Confirmação |
|---|---|---|---|
| Hub → nó | o nó faz `GET http://192.168.4.1/flowCommand`; 250 ms, com backoff até 15 s | JSON com `cmd_id`, estado completo de vazão/válvulas e configuração pendente | `ack_cmd_id` no `/flowData`; Hub retém e reenvia até ACK |
| WebSocket direto | `ws://192.168.10.1/ws`, texto em um frame completo | Todas as chaves do parser | `command_ack` imediato e telemetria a 1 Hz |
| Serial USB | 115200; `{...}\n`; máximo 512 caracteres | Todas as chaves do parser | log e telemetria JSON a 1 Hz; sem resposta transacional exclusiva |
| HTTP de leitura | `GET /diag` ou `/status` | Somente saúde/rede/vazão/alvo | JSON HTTP |
| OTA | `GET /update`; `POST /update` multipart `.bin` | Grava partição OTA e reinicia se a imagem validar | HTTP 200 + reboot; rejeita pelo nome `merged`, `bootloader` e `partitions` |
| Entrada analógica | ADS1115 A3 | Atualiza `flow_voltage` e `flow_rate` usados pelo PI | telemetria; sem bit de sensor desconectado |
| Energia/reset | energização, reset ou OTA | Estado seguro inicial e recarga da configuração persistida | novo `boot_id`; Hub pode reimpor o estado desejado |

**Não existe `POST /command`** neste firmware: controle local Wi-Fi é somente WebSocket. O AP é aberto e não há autenticação para comando, diagnóstico ou OTA.

### 3.4 Catálogo completo de comandos aceitos

#### Metadados

| Chave | Canal | Comportamento |
|---|---|---|
| `cmd_id` | Hub | ID igual ao último aplicado é duplicata e não reaplica o quadro. Sozinho, sem chave reconhecida, não avança o ACK |
| `direct_session_id` + `direct_cmd_id` | direto | No mesmo `session`, ID igual/menor é ignorado. A deduplicação só existe se ambos estiverem presentes |

#### Setpoint e saídas

| Chave no nó | Valor aceito | Execução e hardware | Caminho pelo Hub/app |
|---|---|---|---|
| `flow_setpoint` ou `flowSetpoint` | número, limitado a `0..maxFlowRate` | Atualiza alvo; zero exato força GPIO 5 HIGH e, se `dac_hold=0`, zera DAC/rampa | Hub recebe `flowSetpoint`; apps centrais usam; Flutter direto usa `flow_setpoint` |
| `v1` ou `valve_1` | zero=0; não zero=1 | GPIO 17 imediato; energiza/desenergiza saída 1 | Hub recebe `valve_1`; apps têm controle |
| `v2` ou `valve_2` | zero=0; não zero=1 | GPIO 16 imediato; energiza/desenergiza saída 2 | Hub recebe `valve_2`; apps têm controle |
| `v_Flow` ou `valveFlow` | zero=0; não zero=1 | GPIO 5 imediato; `1` fecha e `0` libera a linha | Hub recebe `v_Flow`. No Flutter direto, “Flow Valve ON” envia `1` e portanto **fecha** |
| `max_flow` ou `maxFlow` | somente `>0,01` | Muda teto, limita alvo e altera escala L/min→DAC; **não persiste** | Hub recebe `maxFlow`; Windows envia nos quadros normais; Flutter direto usa `max_flow` |

O firmware aceita ambas as rotas abertas, ambas fechadas com setpoint positivo e `v_Flow=0` em zero. Intertravamento de rota existe nos construtores oficiais do app, não no nó.

#### Sintonia e comportamento do DAC

| Chave | Efeito | Persistência e acesso |
|---|---|---|
| `kp_flow` | define Kp | EEPROM imediata; Hub recebe `flowKp`; Windows envia/ecoa |
| `ki_flow` | define Ki; `<=0` desliga a integral durante regulação | EEPROM; Hub `flowKi`; Windows envia/ecoa |
| `ff_gain` | ganho de `FF=gain·target+offset` | EEPROM; Hub `flowFfGain`; Windows envia/ecoa |
| `ff_offset` | offset; alvo zero sempre dá FF zero | EEPROM; Hub `flowFfOffset`; a UI Windows restringe a 0..5 apesar do padrão do firmware ser `-0,05` |
| `ramp_rate` | L/min/s; negativo vira 0; 0=degrau | EEPROM; Hub `flowRampRate`; UI Windows aceita >0..100 |
| `dac_hold` | 1 preserva DAC/PI ao zerar; 0 zera DAC/rampa | EEPROM; **não passa pelo Hub**, só WebSocket/serial |
| `debug_pi` | linha serial `[PI]` a cada ciclo ativo | RAM; **não passa pelo Hub**, só WebSocket/serial |

O nó não impõe faixa/finitude a Kp, Ki, feedforward ou coeficientes. As faixas da UI Windows são proteções do aplicativo.

#### Calibração de leitura e rede

| Chave | Efeito | Persistência e acesso |
|---|---|---|
| `a1`, `b1`, `k1`, `f1`, `c1` | para `V≤0,0545`: `Q=a1V⁴+b1V³+k1V²+f1V+c1` | EEPROM; Hub repassa; Windows envia quartic completa; Flutter direto só expõe k1/f1/c1 |
| `k2`, `f2`, `c2` | para `V>0,0545`: `Q=k2V²+f2V+c2` | EEPROM; Hub/Windows/Flutter direto enviam |
| `reconnect_wifi` | 1 permite continuar buscando Hub; 0 interrompe novas associações, mantendo o AP | RAM; Hub recebe `reconnectWifi` e ecoa, mas Windows não tem comando/UI; direto aceita |

Compatibilidade: receber qualquer `k1/f1/c1` sem `a1/b1` **no mesmo quadro** zera `a1/b1`, convertendo a curva baixa para quadrática.

### 3.5 Aplicação, ACK e concorrência

1. O parser não usa biblioteca JSON; chaves desconhecidas são ignoradas. Quadro sem chave conhecida recebe `command_ack:false` no WebSocket.
2. Chaves são aplicadas na ordem recebida. `flow_setpoint:0` fecha `v_Flow`, mas `v_Flow:0` posterior no mesmo quadro volta a liberá-la. Os construtores oficiais terminam a parada com `v_Flow:1`.
3. ACK/LED significam que o ramo de software executou; não provam movimento de válvula ou vazão.
4. O Hub mantém estado desejado completo (`flow_setpoint`, `v1`, `v2`, `v_Flow`) e anexa configuração pendente. Nova ordem cria nova revisão; leitura não consome a caixa.
5. `flowmeterComm`/`FlowControlEnabled` fica apenas no Hub. Não chega ao nó nem bloqueia ordens explícitas, inclusive parada segura offline.
6. Hub, WebSocket e serial usam as mesmas variáveis: último comando aplicado vence. Se uma revisão Hub ainda estiver pendente, ela pode sobrescrever intervenção direta.
7. Flutter direto tenta até cinco vezes a cada 400 ms; Hub reenvia sem limite até o ACK correspondente.

### 3.6 Boot, persistência e recuperação

- A primeira ação do boot é GPIO 5 HIGH; depois GPIO 17/16 LOW e DAC zero.
- EEPROM guarda oito coeficientes, Kp/Ki, `ff_gain/offset`, `ramp_rate` e `dac_hold`. Não guarda setpoint, válvulas, `maxFlowRate`, reconexão, debug, integral ou rampa atual.
- Registro ausente/incompatível carrega curva `FACTORY_*`, Kp=0,4, Ki=2,0, FF=0,85·alvo−0,05, rampa=3 e hold ligado.
- Registro v2/v3/v4 é migrado: curva e PI são substituídos pelos padrões atuais; `ff_*` só é preservado a partir de v3.
- Novo `boot_id` permite ao Hub detectar reboot do nó e reimpor seu último estado. Portanto o nó nasce fechado, mas **pode retomar sozinho o estado anterior** ao reconectar.
- Após reboot do próprio Hub, o primeiro `boot_id` apenas estabelece a sessão; o Hub não reimpõe estado no primeiro contato.

### 3.7 Telemetria e monitoramento

WebSocket/serial saem a 1 Hz; `/flowData` ao Hub, a cada 500 ms. O Hub marca offline após 6 s sem push válido.

| Campo | Significado real |
|---|---|
| `flow_voltage` | tensão filtrada do ADS1115 A3 |
| `flow_rate` | vazão calculada pela curva armazenada; piso zero, sem teto superior |
| `flow_setpoint` | alvo pedido, não saída DAC |
| `flow_setpoint_corrected` | feedforward; pode ficar no valor anterior em zero com hold |
| `flow_output` | comando final PI em equivalente L/min. ⚠ Windows mostra sufixo `V` incorretamente |
| `valve1State/valve2State/valveFlowState` | bits escritos nos GPIOs, sem readback físico |
| `ack_cmd_id`, `ack_direct_*`, `last_apply_ms`, `command_source` | recibo de aplicação no software |
| `Kp`, `Ki`, `ff_gain`, `ff_offset`, `ramp_rate`, `dac_hold`, `reconnect_wifi` | configuração vigente; Hub publica Kp/Ki/FF/rampa/reconexão |
| `boot_id` | sessão de energização, somente no push ao Hub |

`/diag` e `/status` mostram uptime, heap, Wi-Fi, sequência de falhas HTTP, OTA, vazão e alvo. Não mostram válvulas, DAC, coeficientes, ACKs nem falha de inicialização do ADC/DAC.

### 3.8 Procedimentos de operação

| Procedimento | Quadro | O que confirmar |
|---|---|---|
| Habilitar malha/alarme | `{"flowmeterComm":1}` | Só altera `FlowControlEnabled` no Hub; não abre gás. Presença real é `FlowmeterOnline` |
| Ar ao reator | `flowSetpoint`, `maxFlow`, A aberta, B/C fechada, `v_Flow:0` | `FlowCommandPending=false`, ecos de setpoint/válvulas e vazão real estabilizada |
| Descarga B/C | mesmo, com B/C aberta | Gás sai por C; manter N₂ fechado na fonte se não desejado |
| Fechar linha preservando ponto | setpoint vigente + `v_Flow:1` | GPIO 5 fecha; DAC/PI congelam com hold; `v_Flow:0` retoma |
| Parada segura | `flowSetpoint:0`, `maxFlow`, `valve_1:0`, `valve_2:0`, `v_Flow:1` | ACK, três ecos e `FlowRate` caindo a zero |
| Reset global Hub | `resetVariables` | Hub cria internamente a parada completa e desliga `FlowControlEnabled` |
| Controle direto | comandos separados no Flutter | Abrir com `v_Flow=0`; o toggle “Flow Valve ON” faz o oposto do que o nome sugere |
| Sintonia | `flowKp/Ki/FfGain/FfOffset/RampRate` | ACK e ecos aplicados antes de testar resposta |
| OTA | `/update`, `.ino.bin` simples | Fazer parada segura antes; comunicação pausa durante upload |

### 3.9 Procedimento de calibração da medição

1. Confirmar A/B/C, gás, fundo de escala e padrão externo certificado. Em descarga C, fechar N₂ na fonte; usar A somente com reator seguro.
2. Em **Calibrações › Fluxômetro**, selecionar a rota, enviar setpoint, esperar ACK/ecos e estabilidade externa.
3. Digitar na linha a **vazão real do padrão externo**, nunca copiar setpoint ou `FlowRate` do nó.
4. Capturar tensão: o app tira a média de 10 quadros distintos (configurável 1..100).
5. Cobrir ambos os lados de 0,0545 V. Curva completa exige ≥2 pontos altos (reta; ≥3 = quadrática) e ≥1 baixo com curva alta; preferir ≥3 baixos, incluir zero e replicar perto do limiar.
6. **Salvar pontos** persiste somente no PC. Revisar equações, cobertura e salto no limiar.
7. **Salvar e enviar curva** transmite `maxFlow` e os segmentos disponíveis; o nó grava EEPROM.
8. O ACK não é readback dos coeficientes: eles não voltam na telemetria do Hub. Reiniciar e testar pontos independentes para confirmar persistência, resíduos, monotonicidade e continuidade.
9. Encerrar com parada segura completa e confirmar vazão física zero.

### 3.10 Inconsistências, Decisões e Status da Implementação (F01 a F16)

| ID | Inconsistência | Consequência | Resolução Implementada | Status |
|---|---|---|---|---|
| F01 | Identidade V10 no build/OTA e v11 no protocolo/endpoints | Binário não identificado com clareza pela UI | Unificada constante `FW_VERSION "v11.0"` em build, serial, `/diag`, `/status` e `/nodeHello`; catálogo Windows App valida `v11` e `v11.0` | **Resolvido** |
| F02 | Discrepância entre curvas `FACTORY_*`, certificadas e defaults Flutter | “Restaurar padrão” alternava entre curvas | Nó congelado como base autoritativa; curvas sincronizadas na documentação; ensaio físico de bancada agendado | **Alinhado (Bancada pendente)** |
| F03 | `FlowOutput` é equivalente L/min, mas Windows mostrava `V` | Diagnóstico de DAC enganoso | Windows App atualizado para exibir `" L/min"` e rótulo `"Saída do controlador: "` | **Resolvido** |
| F04 | $0 < \text{alvo} \le 0,1$ não fechava a linha nem rodava PI | DAC residual mantido | Corte mecânico obrigatório em $\le 0.10$ L/min; respeita `dacHold`; Windows App rejeita $(0.00, 0.10)$ | **Resolvido** |
| F05 | Setpoint positivo direto não abria `v_Flow`; toggle Flutter invertido | Comando sem vazão; semântica invertida | Nó auto-abre `v_Flow` (0 = liberado) se setpoint $> 0.10$ recebido sem `v_Flow`; UI Flutter invertida | **Resolvido** |
| F06 | Sem intertravamento de rota no nó | Ambas abertas ou duto fechado aceitos | Decisão de Projeto: Hardware não possui limitação física; nó e Hub liberam qualquer combinação lógica; governança e avisos visuais no Windows App | **Resolvido (Decisão de Projeto)** |
| F07 | Kp/Ki/FF/curva sem validação de faixa/finitude | `NaN` ou overflow corrompia controle | Parser `parseBoundedFloat()` com rejeição de `NaN`/`Inf`, faixa quártica $[-10^7, 10^7]$, ganhos $[0, 100]$ | **Resolvido** |
| F08 | Parser permissivo aplicava parcialmente JSON malformado | Estado parcial em caso de erro | Parser transacional em 2 fases sob `commandMutex`; `parseJsonBool()` case-insensitive; rejeição integral do quadro inválido | **Resolvido** |
| F09 | `max_flow` não persistia na EEPROM | Escala do DAC revertia para 50 após reboot | Schema EEPROM v6 com migração segura de v5; salvamento atômico; retransmissão de `pendingMaxFlow` pelo Hub | **Resolvido** |
| F10 | Gravação parcial de curva zerava termos quárticos `a1/b1` | Atualização parcial degradava curva baixa | Removido zeramento automático de `a1/b1`; coeficientes preservados a menos que explicitamente comandados | **Resolvido** |
| F11 | ACK de calibração sem readback dos coeficientes | App confirmava parser, não persistência | Cálculo de CRC32; endpoint `GET /calibration`; telemetria `&cal_crc` no Hub e WebSocket | **Resolvido** |
| F12 | ADS/DAC podiam falhar no boot sem alarme | App mostrava valor plausível sem hardware | Supervisão contínua em boot e loop; corte seguro e telemetria `hw_status` (vazão $-1.0$ em falha) | **Resolvido** |
| F13 | SoftAP e OTA sem autenticação | Conexão aberta a clientes próximos | Decisão de Projeto: SoftAP e OTA mantidos intencionalmente abertos para facilitar manutenção e comissionamento em campo | **Aberto por Decisão de Projeto** |
| F14 | OTA pausava rede, mas podia manter última saída ativa | Gás continuava fluindo durante gravação | Parada segura atômica sob `commandMutex` antes do upload OTA; trava `otaSafeLatch` em caso de stall | **Resolvido** |
| F15 | `reconnect_wifi` desligado sem comando no App | Nó ilhado exigia intervenção física | Comando `FlowmeterReconnectWifi` no App; watchdog temporal de 15 min no nó restaura `true` | **Resolvido** |
| F16 | Válvulas ecoavam bit sem confirmação de posição física | Sem detecção de solenoides travadas | Algoritmo de diagnóstico cruzado de plausibilidade implementado no Windows App (`FlowPlausibilityWarning`) | **Resolvido** |

### 3.11 Checklist de bancada

- [ ] Registrar hash/binário gravado e resolver F01 antes dos ensaios.
- [ ] Conferir GPIO 5/17/16 e polaridade física de A, B, C e *Valve Off*.
- [ ] Confirmar boot seguro: GPIO 5 fechado, GPIO 17/16 desenergizados e DAC zero.
- [ ] Medir app → entrega → `last_apply_ms` → ACK → resposta mecânica, inclusive com perda/reconexão.
- [ ] Parada segura a partir de vazão alta e de cada rota; confirmar vazão real zero.
- [ ] Testar `dac_hold=1/0`, pico de retomada e integral.
- [ ] Proibir/testar 0 < setpoint ≤ 0,1 antes de uso operacional.
- [ ] Calibrar com padrão externo, réplicas e validação independente após reboot.
- [ ] Comparar as três curvas de F02 e eleger uma fonte autoritativa.
- [ ] Desconectar ADS1115 e MCP4725 separadamente e registrar telemetria/estado físico.
- [ ] Reiniciar só o nó durante fluxo e verificar reimposição pelo Hub; reiniciar só o Hub e verificar o primeiro contato.
- [ ] OTA somente após parada segura: imagem válida, nomes rejeitados, interrupção e watchdog de 90 s.

## 4. Sensor de biomassa (`sensor-biomassa`, v11) — a preencher

Contrato: `sensor-biomassa/docs/PROTOCOL.md`; regra do Hub: **um `command` por revisão** (`set_it`, `set_pwm`, `set_gear`, `ema`, `probe_period`); headroom de flash a 5,3 kB do piso (não acrescentar eco sem remedir).

## 5. Agitador de frasco (`frasco-agitador`) — a preencher

Contrato: `frasco-agitador/docs/PROTOCOL.md`. Particularidade: o potenciômetro de bancada **sobrepõe** o app (`AgitatorPotActive`).

## 6. Servo drive (`ESP32S3-SERVO`, driver 2.0) — a preencher

Contrato: `ESP32S3-HUB/docs/WIRE_CONTRACT_V9.md` (§ Estado desejado do motor) e `ESP32S3-SERVO/`. Rota Modbus direta com `cmd_id`, ACK por readback e falha zero como pré-condição de movimento.
