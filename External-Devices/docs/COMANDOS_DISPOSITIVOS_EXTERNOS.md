# Comandos e procedimentos dos dispositivos externos

**Data:** 2026-09-12  
**Propósito:** um único lugar que responda, por dispositivo, a três perguntas: *que interações o nó pode receber*, *o que o firmware faz com cada uma* e *o que o hardware deve fazer em consequência*. Cada afirmação abaixo foi conferida no código ativo (firmware do nó, `Commands.h`/`HttpServer.h` do Hub 10.2 e `CommandBuilders`/ViewModels do aplicativo), não nos documentos anteriores. Onde o código diverge da documentação do nó, este arquivo prevalece e a divergência está marcada com ⚠.

**Fontes por dispositivo:** o contrato de fio detalhado continua em `<dispositivo>/docs/PROTOCOL.md` e no `ESP32S3-HUB/docs/WIRE_CONTRACT_V9.md`; este documento não os substitui — ele explica o comportamento. Estado de implementação e pendências de bancada: `Windows_app/docs/PONTOS_DE_MELHORIA_EXPOSICAO_NOS.md`.

**Estado desta revisão:** §1 (bomba peristáltica) completo e **já refletindo o firmware 3.10** (as decisões tomadas sobre a auditoria do 3.9 estão em §1.10, com o que mudou); §3 (fluxômetro) completo. §2 e §4–§6 são esqueletos com ponteiros, a preencher um dispositivo por vez com a mesma anatomia.

---

## Visão Geral de Prontidão dos Dispositivos Externos

| Dispositivo | Firmware Ativo | Hub 10.2 | App Windows | Integração Software | Ensaio em Bancada Física |
|---|:---:|:---:|:---:|:---:|:---:|
| **Bomba Peristáltica** | v3.10 | 🟢 Total | 🟢 Total | 🟢 100% Integrado | 🟡 Pendente (§1.11) |
| **Sensor de Distância** | v11 | 🟢 Total | 🟢 Total | 🟢 100% Integrado | 🟡 Pendente |
| **Fluxômetro de Ar** | v11 | 🟢 Total | 🟢 Total | 🟢 100% Integrado | 🟡 Pendente |
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

#### 🔴 Lacunas Técnicas e Decisões de Escopo (Não Integradas ou Adiadas)
*Funcionalidades que NÃO estão no App/Hub ou operam apenas localmente no nó.*

| Item / Recurso | Onde Existe | Por que não está no App/Hub | Decisão Técnica (§1.10) |
|---|---|---|---|
| **Porta lógica do sensor de tubo (`sensorEnable` / `sensorBypass`)** | Apenas no firmware local do nó (`POST /command` ou botão físico) | O sensor óptico de bolha/líquido no tubo de dosagem é uma proteção de hardware local. Não há chave no Hub nem botão no app para ligar/desligar a porta. | **Adiado** (Decisão #7): manter apenas local no nó; avaliar integração futura se houver demanda de processo. |
| **Leitura da posição angular dos potenciômetros** | ADC do ESP32 local (`POT_INT`, `POT_GAIN`) | O nó não transmite a leitura bruta dos ADCs dos potenciômetros; transmite apenas o booleano `pot` (`PumpPotEnabled`). | **Mantido**: o app precisa saber apenas se os knobs têm o controle ou não; posições analógicas não agregam ao controle supervisório. |
| **Curvas não-lineares multiponto de vazão** | Firmware suporta interpolação por partes | A calibração assistida do app modela a bomba pela reta padrão $Q = \text{slope} \cdot S + \text{intercept}$. | **Mantido**: o cabeçote peristáltico opera de forma linear na faixa útil de 155 a 1023 de PWM; desvios são tratados pelo PID de volume. |
| **Comandos de configuração interna (`save_config`, `load_config`, `clear_nvs`)** | Apenas no firmware local | O Hub filtra ativamente essas chaves para impedir que scripts ou clientes remotos corrompam a NVS ou apaguem a calibração da bomba. | **Bloqueado por segurança** (Decisão #5): permitido apenas via USB direta com a bancada. |

---

### 1.1 O que o firmware assume do hardware

| Elemento | Pino / recurso | O que o firmware faz | Observação |
|---|---|---|---|
| Ponte H com PWM duplo | `R_EN`=25, `L_EN`=26 (sempre HIGH após o boot); `R_PWM`=14, `L_PWM`=27 (LEDC 7,5 kHz, 10 bits) | Velocidade = duty numa das saídas; a outra fica em 0. Sentido = qual saída recebe o PWM (`s ≥ 0` → `R_PWM`) | **Motor DC escovado** (confirmado pelo operador em 2026-09-12) em ponte H tipo BTS7960/IBT-2. O NEMA 17HS4401 do CAD é referência mecânica, não o motor montado |
| Cabeçote | CAD Watson-Marlow: rotor de 5 roletes, mancais LM6UU | — | A relação duty → mL/min é inteiramente empírica (calibração) |
| Potenciômetro de velocidade | `POT_INT`=34 (ADC 12 bits, filtro passa-baixas α=0,10) | Magnitude 0..1 × `V_MAX` | Só vale em `OP_IDLE` com os potenciômetros no comando (`pot=1`, §1.6) |
| Potenciômetro de sentido + ganho | `POT_GAIN`=35 | Fator `(ADC − centro)/centro` ∈ [−1, 1] que multiplica a velocidade: o sinal dá o sentido e o módulo escala a velocidade (centro = parado, extremos = velocidade plena no sentido escolhido) | Velocidade final = velocidade × fator; os dois knobs juntos dão sentido e ganho sobre a velocidade |
| Sensor de líquido | `SENSOR_PIN`=15 (`INPUT_PULLUP`, debounce 50 ms; **LOW = molhado**) | Porta lógica: com `sensorEnable && !sensorBypass`, o motor só gira se molhado | **Não é medidor de vazão.** Suporte em `suporte_sensor_liquido/` |
| Botão do sensor | `SENSOR_ENABLE_BUTTON_PIN`=32 (pull-up) | A cada laço, `sensorEnable = !botão` **enquanto `sensorButtonOverride == false`** | ⚠ O padrão de fábrica é `sensorButtonOverride = true`, ou seja, **o botão físico é ignorado até alguém enviar `sensorButtonOverride:0`**, e `sensorEnable` nasce `false` (sem porta). Mantido como está em 3.10 |
| LED de estado do sensor | 33 | Acende com `sensorEnable` | — |
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

## 2. Sensor de distância (`sensor-distancia`, v11) — a preencher

Fontes já auditadas em 2026-09-12: `firmware/distance-sensor/src/protocol/ConfigCodec.cpp` (dedupe de `cmd_id`, faixas, NVS só em mudança), `FirmwareApp.cpp` (push `distance=-1` em falha do VL53L0X, escada de recuperação). Contrato: `sensor-distancia/docs/PROTOCOL.md`. Chaves pelo Hub: `distanceOffsetMm`, `distanceSamplePeriodMs`, `distanceSendPeriodMs`, `distanceResetNvs` (carona na resposta do push).

## 3. Fluxômetro (`fluxometro`, firmware ativo com identificação divergente V10/v11)

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

### 3.10 Inconsistências e correções futuras

| ID | Inconsistência encontrada | Consequência atual | Correção futura candidata |
|---|---|---|---|
| F01 | Identidade V10 no build/OTA e v11 no protocolo/endpoints | binário não é identificável com segurança pela UI | uma constante de versão para todos os canais e documentos |
| F02 | Curva `FACTORY_*` do firmware, `CalibrationMath.FirmwareDefault`/pontos certificados do Windows e defaults do Flutter são diferentes | “restaurar padrão” pode trocar uma curva funcional por outra | eleger ensaio autoritativo, regenerar e sincronizar as três fontes |
| F03 | `FlowOutput` é equivalente L/min, mas Windows mostra `V` | diagnóstico de DAC enganoso | corrigir unidade/nome; publicar DAC bruto ou tensão se necessário |
| F04 | 0 < alvo ≤ 0,1 não fecha a linha nem roda PI | DAC anterior pode permanecer aplicado | normalizar para zero ou regular toda a faixa segura |
| F05 | Setpoint direto positivo não abre `v_Flow`; toggle Flutter “ON” fecha | comando aparente pode não gerar vazão; semântica invertida | enviar estado completo e renomear para “Corte geral fechado” |
| F06 | Sem intertravamento de rota no nó | ambas abertas ou linha morta são aceitas | validar estados atomizados no firmware, preservando parada segura |
| F07 | Kp/Ki/FF/curva sem validação de faixa/finitude | `NaN`, infinito ou coeficiente perigoso pode persistir | validação transacional antes de aplicar/gravar |
| F08 | Parser permissivo aplica parcialmente JSON malformado e `true` vira 0 via `atoi` | estado parcial ou inverso | biblioteca JSON, tipos estritos e rejeição integral do quadro |
| F09 | `max_flow` não persiste | escala DAC muda para 50 após reboot até novo comando | persistir com schema/versionamento ou sempre reimpor explicitamente |
| F10 | Curva parcial é gravada imediatamente; `k1/f1/c1` sem `a1/b1` zera termos quartic | atualização parcial pode degradar o segmento baixo | comando de curva completo, versionado e atômico |
| F11 | ACK de calibração sem readback dos coeficientes | app confirma parser, não conteúdo persistido | ecoar curva/hash e validar após reboot |
| F12 | ADS/DAC podem falhar no boot sem bloquear operação ou gerar telemetria de falha | app pode mostrar valor plausível com hardware ausente | flags de saúde, alarme e estado seguro |
| F13 | AP/controle/OTA sem autenticação | qualquer cliente próximo pode atuar ou regravar | autenticação e modo de manutenção físico |
| F14 | OTA pausa rede, mas pode manter última saída ativa | gás pode continuar durante manutenção | interlock de parada antes de `Update.begin()` |
| F15 | `reconnect_wifi` pode ser desligado, mas Windows só o monitora | recuperação pode exigir acesso direto | ação protegida de reabilitação no app |
| F16 | Válvulas ecoam o bit comandado, sem realimentação | ACK não prova posição/fluxo | readback elétrico/mecânico ou diagnóstico por vazão |

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
