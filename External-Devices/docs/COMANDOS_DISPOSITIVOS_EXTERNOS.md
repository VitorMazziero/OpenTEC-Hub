# Comandos e procedimentos dos dispositivos externos

**Data:** 2026-09-12  
**Propósito:** um único lugar que responda, por dispositivo, a três perguntas: *que interações o nó pode receber*, *o que o firmware faz com cada uma* e *o que o hardware deve fazer em consequência*. Cada afirmação abaixo foi conferida no código ativo (firmware do nó, `Commands.h`/`HttpServer.h` do Hub 10.2 e `CommandBuilders`/ViewModels do aplicativo), não nos documentos anteriores. Onde o código diverge da documentação do nó, este arquivo prevalece e a divergência está marcada com ⚠.

**Fontes por dispositivo:** o contrato de fio detalhado continua em `<dispositivo>/docs/PROTOCOL.md` e no `ESP32S3-HUB/docs/WIRE_CONTRACT_V9.md`; este documento não os substitui — ele explica o comportamento. Estado de implementação e pendências de bancada: `Windows_app/docs/PONTOS_DE_MELHORIA_EXPOSICAO_NOS.md`.

**Estado desta revisão:** §1 (bomba peristáltica) completo. §2–§6 são esqueletos com ponteiros, a preencher um dispositivo por vez com a mesma anatomia.

---

## 1. Bomba peristáltica externa (`bomba-peristaltica`, firmware 3.9)

### 1.1 O que o firmware assume do hardware

| Elemento | Pino / recurso | O que o firmware faz | Observação |
|---|---|---|---|
| Ponte H com PWM duplo | `R_EN`=25, `L_EN`=26 (sempre HIGH após o boot); `R_PWM`=14, `L_PWM`=27 (LEDC 7,5 kHz, 10 bits) | Velocidade = duty numa das saídas; a outra fica em 0. Sentido = qual saída recebe o PWM (`s ≥ 0` → `R_PWM`) | Comportamento de motor DC escovado em ponte H tipo BTS7960/IBT-2. ⚠ O CAD (`hardware/cad/source/watson-marlow-integration`) lista um **NEMA 17HS4401** (motor de passo), que não se aciona assim. Confirmar na bancada qual motor está montado; o firmware só faz sentido com DC |
| Cabeçote | CAD Watson-Marlow: rotor de 5 roletes, mancais LM6UU | — | A relação duty → mL/min é inteiramente empírica (calibração) |
| Potenciômetro de intensidade | `POT_INT`=34 (ADC 12 bits, filtro passa-baixas α=0,10) | Magnitude 0..1 × `V_MAX` | Só vale em `OP_IDLE` e sem `speed` recebido (ver §1.3) |
| Potenciômetro de "ganho" | `POT_GAIN`=35 | ⚠ É o **sentido**, não um ganho: `(ADC − centro)/centro` ∈ [−1, 1] multiplica a magnitude. Centro = parado, extremos = sentido pleno | Nome herdado; a UI local chama de gain |
| Sensor de líquido | `SENSOR_PIN`=15 (`INPUT_PULLUP`, debounce 50 ms; **LOW = molhado**) | Porta lógica: com `sensorEnable && !sensorBypass`, o motor só gira se molhado | **Não é medidor de vazão.** Suporte em `suporte_sensor_liquido/` |
| Botão do sensor | `SENSOR_ENABLE_BUTTON_PIN`=32 (pull-up) | A cada laço, `sensorEnable = !botão` **enquanto `sensorButtonOverride == false`** | ⚠ O padrão de fábrica é `sensorButtonOverride = true`, ou seja, **o botão físico é ignorado até alguém enviar `sensorButtonOverride:0`**, e `sensorEnable` nasce `false` (sem porta) |
| LED de estado do sensor | 33 | Acende com `sensorEnable` | — |
| Wi-Fi | STA para `ModuloTECNAL_1`/`_2` (canal 6) + AP próprio `FeedPump` em 192.168.6.1 | Push a 1 s, poll a 2 s, hello a 30 s; backoff até 15 s; queda forçada após 8 falhas | Servidor local na porta 80 (§1.5) |
| Núcleos | Core 0: tarefa `pwmTask` (2 ms) — escreve LEDC e integra volume. Core 1: `loop()` — rede, comandos, máquina de estados, WDT 15 s | Comunicação por `volatile` (`g_cmdSpeed`, `g_driverEnabled` → Core 0; `g_cumulativeVolumeMl`, `g_actualPwmDuty` → Core 1) | — |

### 1.2 Como a velocidade final é decidida (a cada laço, ~2 ms + rede)

```
requestedSpeed =
    OP_RUNNING → mlminToSpeedUnits(Q_alvo + PID)        (nunca negativo)
    OP_IDLE    → hasUsbSpeed ? usbSpeedSteps : potSpeed   (pode ser negativo = sentido inverso)
    OP_WAITING → 0
allowRun   = !(sensorEnable && !sensorBypass) || sensorMolhado
finalSpeed = allowRun ? clamp(requestedSpeed, −1000, +1000) : 0
```

- **Unidade S (0..1000)** é a "velocidade interna". `pwmTask` converte: `|S| < 1` → duty 0; senão `duty = 155 + (|S| − 1)/999 · (1023 − 155)`. Ou seja, **duty útil 155..1023** (`PWM_BREAKAWAY` = 155: abaixo disso o motor não vence o atrito do cabeçote). S = 1 já é 155/1023 ≈ 15 % de duty, não "quase parado".
- **Trava de 500 ms** (`MIN_MOTOR_ON_TIME_MS`): ao ligar, o motor mantém a velocidade de partida por ao menos 500 ms antes de aceitar uma nova. Evita "tremer" quando o alvo oscila perto de S = 1.
- **Conversão para vazão** é a reta de calibração em ambos os sentidos: `Q [mL/min] = slope · S + intercept` e `S = (Q − intercept)/slope`. Com `|slope| < 1e-6` o firmware devolve S = 0 (não divide por zero) — uma calibração com slope zero **para a bomba em qualquer perfil**.

### 1.3 Volume: estimado, não medido

`g_cumulativeVolumeMl += pwmDutyToMlmin(duty_aplicado)/60 · dt` a cada 2 ms no Core 0. O duty é convertido **de volta** para S e então para mL/min pela mesma reta de calibração. Consequências que o operador precisa saber:

1. `PumpVol` e `PumpFlow` são **previsões da curva de calibração**, não leituras de um sensor. Se a curva estiver errada, o volume acumulado está errado na mesma proporção, e nenhum campo de telemetria denuncia isso.
2. O "PID de volume" (`pid_kp/ki/kd`) compara `V_alvo(t)` (integral analítica do perfil) com esse mesmo volume estimado. Ele corrige apenas erros de **execução** (trava de 500 ms, quantização de duty, tempo em `OP_WAITING`), nunca erros de **calibração**. Padrões: kp 0,5, ki 0,05, kd 0,001; integrador limitado a ±100 mL·min.
3. A única forma de fechar a malha de verdade é a calibração volumétrica (§1.8) feita com recipiente graduado.

### 1.4 Máquina de estados

| Estado | Entrada | Saída | Motor |
|---|---|---|---|
| `OP_IDLE` | boot limpo; `stop`; `mode:0`; `speed`; fim de `final_t` | perfil (`mode ≥ 1` por parâmetro) → `OP_WAITING` se `init_t > 0`, senão `OP_RUNNING`; `start` → `OP_RUNNING` | potenciômetro **ou** `speed` (prioridade em §1.2; pegajosidade de `speed` em §1.6) |
| `OP_WAITING` | perfil com `init_t > 0` | `t ≥ init_t` → `OP_RUNNING` (zera volume e PID, salvo recuperação) | parado (`Q = 0`) |
| `OP_RUNNING` | acima; recuperação após reset | `final_t > 0 && t ≥ final_t` → `OP_IDLE`, `mode = 0` persistido, volume zerado, checkpoint limpo | `Q(t) + PID` |

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
| `start` | `init_t = final_t = 0`, `OP_RUNNING` imediato com o `mode` vigente; zera volume e PID; limpa checkpoint | Motor segue o perfil **sem fim** (`final_t = 0`). Com `mode = 0`, fica "active" com Q = 0 — parado, mas marcado como rodando ⚠ | sim | não usa |
| `stop` | `OP_IDLE`, `mode = 0` persistido, zera volume e PID, limpa checkpoint | Motor para (salvo potenciômetro/`speed` em IDLE) | sim | não usa (usa `mode:0`, abaixo) |
| `reset_volume` | zera volume e PID; **não** muda estado nem `mode` | Nenhuma ação no motor | sim | **Zerar volume** (Controle e Calibrações); confirmação não-otimista por `PumpVol < 0,05` |
| `save_config` / `load_config` | grava/relê o blob de configuração na NVS | — | sim | não usa |
| `print_config` | imprime na serial o JSON com arrays do perfil | — | sim (inócuo) | não usa |
| `clear_nvs` | **apaga toda a NVS e reinicia o ESP** — perde calibração, perfil, PID e checkpoint | Motor para no reboot | ⚠ **sim, o Hub repassa** | não usa. Risco: qualquer cliente Wi-Fi do Hub pode disparar |

#### Perfil e temporização (disparam **reset do ciclo** quando enviados sem `command`)

| Chave | Faixa aceita | Firmware | Hardware | Hub | App |
|---|---|---|---|---|---|
| `mode` | 0..5 (fora disso: ignorado com log) | Define o perfil; **mesmo valor repetido conta como mudança** → zera volume/PID, limpa checkpoint, reinicia `t`; `mode ≥ 1` entra em `WAITING`/`RUNNING`; `mode = 0` vai a IDLE | 1 constante, 2 linear, 3 exponencial, 4 polinômio (grau ≤ 20), 5 linear por partes | sim | Enviar perfil; `{"mode":0}` = **parar perfil** (`PumpStopProfile`) — ⚠ e zera o volume acumulado |
| `init_t` | min, qualquer float | Atraso antes de bombear (`OP_WAITING`) | Parado até `t ≥ init_t` | sim | Tempo inicial |
| `final_t` | min; `0` = sem fim | Ao atingir: IDLE, `mode = 0` persistido, volume zerado | Motor para sozinho | sim | Tempo final (padrão 60 min no app) |
| `lambda_const` | mL/min | `Q = λ` | — | sim | Perfil Constante |
| `lambda_linear`, `phi_linear` | mL/min, mL/min² | `Q = λ + φ·t` | — | sim | Perfil Linear |
| `lambda_exp`, `phi_exp` | mL/min, 1/min | `Q = λ·e^{φt}` | — | sim | Perfil Exponencial |
| `p0`..`p20` | double | `Q = Σ p_i t^i` (Horner); integral analítica para o alvo de volume | — | sim (`polyKeys`) | Perfil Polinomial (app limita o grau) |
| `num_segments`, `t0..t99`, `q0..q99` | 2..100 segmentos; `t0` forçado a 0 quando `mode = 5` | Interpolação linear; após o último ponto mantém `q_último`; volume por trapézios | — | sim (o Hub varre `t0..t99`/`q0..q99` só quando vê `pump_command`, `num_segments`, `t0` ou `q0`) | Perfil por partes (2..N pontos) |

**Regra de aplicação (importante para o app):** qualquer chave desta tabela **sem** `"command"` no mesmo JSON dispara `resetOperationState()` + `clearRuntimeState()` + novo `t = 0`. Um quadro com `mode` **e** `command` juntos deixa a decisão para o `command` (`start`/`stop`), não para o `mode`.

#### Velocidade manual e portas de segurança (não disparam reset de ciclo)

| Chave | Firmware | Hardware | Hub | App |
|---|---|---|---|---|
| `speed` | `usbSpeedSteps = valor; hasUsbSpeed = true; OP_IDLE; mode = 0` (⚠ `mode = 0` **não** é persistido nem zera o volume aqui) | Motor a S imediatamente, **inclusive negativo = sentido inverso**; `0` para. **Sem temporizador**: fica até o próximo `speed` ou perfil. ⚠ `hasUsbSpeed` **nunca volta a `false`**: depois do primeiro `speed`, o potenciômetro é ignorado até o reboot | sim, como **`pump_speed`** (`speed` sem prefixo é rejeitado por desenho) | **Acionamento volumétrico** (Calibrações › Bomba Externa): S inteiro 1..1000, parada automática pelo relógio do app; queda de link → parada na reconexão |
| `disablePot` | `1` ignora os potenciômetros em IDLE | Motor deixa de responder aos knobs | **não** (fora da whitelist) | — |
| `sensorEnable` | liga/desliga a porta do sensor de líquido (sobrescrito pelo botão se `sensorButtonOverride = 0`) | Com porta ligada e tubo seco, o motor **não gira** em nenhum estado | **não** | — |
| `sensorBypass` | `1` ignora a porta mesmo com `sensorEnable` | — | **não** | — |
| `sensorButtonOverride` | `0` faz o botão físico mandar em `sensorEnable`; `1` (padrão) ignora o botão | — | **não** | — |

#### Calibração e PID (persistem, não resetam o ciclo, não passam pela porta do sensor)

| Chave | Firmware | Hub | App |
|---|---|---|---|
| `pumpSlope`, `pumpIntercept` | Atualiza a reta e marca `g_configDirty` → blob gravado no próximo laço. **Efeito imediato** na conversão S↔Q, inclusive num perfil em execução e na integração de volume | sim (pass-through) | **Aplicar calibração**: só persiste no PC e grava recibo após o eco `slope`/`intercept` bater com o pedido (tolerância 1e-4) |
| `pid_kp`, `pid_ki`, `pid_kd` | Atualiza ganhos; efeito imediato | sim (`pumpPidKp`→`pid_kp` etc.) | `CommandBuilders.PumpPid` existe; **UI desabilitada** porque o firmware 3.9 **não ecoa** os ganhos — o app não teria como confirmar |

### 1.7 Persistência e recuperação

- **Blob de configuração** (`PumpConfig`, ≈1,03 kB: modo, tempos, λ/φ, 21 coeficientes, 100+100 pontos, calibração, PID) gravado inteiro em `feed_pump/config` sempre que `g_configDirty`. ⚠ O "CRC32" é uma soma simples de bytes; detecta corrupção grosseira, não troca de layout. Um blob de tamanho diferente (outro firmware) volta aos padrões: **slope 0,0280188148, intercept 1,7601988934**, PID 0,5/0,05/0,001, `mode 0`.
- **Checkpoint** (`s_active`, `s_vol`, `s_time`, `s_mode`) a cada 60 s **só em `OP_RUNNING` e `mode ≠ 0`**. `stop`, `mode`/parâmetro novo, `start` e fim de `final_t` limpam o flag.
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

O Hub deriva ainda `PumpOnline` (= `pumpComm` ligado **e** push há ≤ 4 s) e `PumpCommEnabled` (eco do interruptor do Hub), mais `PumpIP`/`PumpNodeVer`/`PumpNodeMac` pelo `/nodeHello` (a cada 30 s) e o `/diag` local via `/nodeDiag` (uptime, heap, RSSI, `hub_fail_streak`, `op_state`, `mode`, `flow`, `vol`).

**Não sai do nó** (e portanto o app não pode mostrar): ganhos PID vigentes, estado da porta do sensor (`sensorEnable`/molhado), `hasUsbSpeed`, sentido do potenciômetro, tempo `t` corrente pelo Hub (só na serial/`/readData`).

### 1.9 Procedimentos do operador no aplicativo (e o que acontece no fio)

| Procedimento | Onde | Quadros (ordem) | O que confirmar |
|---|---|---|---|
| Habilitar a bomba | Controle › Bomba Externa, interruptor | `{"pumpComm":1}` | `PumpCommEnabled=true`; `PumpOnline` em ≤ 4 s se o nó estiver empurrando |
| Enviar perfil | idem, **Enviar perfil** | um quadro: `mode`, `init_t`, `final_t` + parâmetros do modo | `PumpMode` ecoa; `PumpWaiting`/`PumpActive`; `PumpCommandPending` cai no push seguinte ao poll (≤ 3 s) |
| Parar perfil | interruptor desliga, ou **Parar** | 1.º `{"mode":0}` (com `pumpComm` ainda ligado — senão o Hub descarta), 2.º quadro separado `{"pumpComm":0}` | `PumpActive=false`; ⚠ `PumpVol` volta a 0 |
| Parada segura global | E-stop / árbitro | `PumpStopProfile()` = `{"mode":0}` | idem |
| Zerar volume | **Zerar volume acumulado** | `{"pump_command":"reset_volume"}` | `PumpVol < 0,05` em ≤ 5 s; senão aviso |
| Calibração por coeficientes | Calibrações › Bomba Externa | `{"pumpSlope":a,"pumpIntercept":b}` | eco igual em ≤ 15 s → recibo JSON em `Calibracoes/` |
| **Calibração volumétrica** | idem, cartão "Acionamento volumétrico" | `{"pump_speed":S}` → espera Δt pelo relógio do app → `{"pump_speed":0}`; operador informa V; repete; **Usar ajuste** → **Aplicar** (quadro acima) | pontos (S, Δt, V, Q) na tabela e no recibo; R²; ver riscos abaixo |
| Receita — nó Bomba Externa | Receitas | `Enable` = `{"pumpComm":1}`; `Profile` = perfil; `Stop` = `{"mode":0}` + `{"pumpComm":0}` separado | o motor de receitas exige o eco de `PumpCommEnabled` antes de prosseguir |
| Gás proporcional | Controle › Bomba Externa | nenhum quadro **para a bomba**; usa `PumpVol` para comandar a aeração `Q_g = (V₀ + Vol/1000)·vvm` | recusado se a cascata de O₂ detiver a aeração |

**Arbitragem:** todas as chaves acima pertencem a `ActuatorId.ExternalPump`; enquanto uma receita ou um ensaio detiver a bomba, o envio manual é recusado com o motivo (`DispatchRefusal`).

### 1.10 Limitações, riscos e lacunas (para decidir depois da bancada)

1. **Volume é estimado** (§1.3). Nenhum campo indica desvio entre curva e realidade. Mitigação: calibrar por volume e conferir o volume dosado ao fim de cada batelada.
2. **`mode:0` zera o volume** — "parar" e "zerar" são a mesma coisa no firmware. O gás proporcional e o registro de sessão perdem o acumulado ao parar o perfil. Candidato a mudança de firmware (parar sem zerar; zerar só com `reset_volume`).
3. **`speed` é pegajoso**: após qualquer acionamento volumétrico, os potenciômetros ficam mortos até reiniciar o nó. Candidato: `speed` com valor ausente/`null` ou uma chave `pot:1` para devolver o controle.
4. **`speed` sem temporizador**: o app é o único relógio. Queda de link → o app envia a parada ao reconectar; se o link não voltar, parar no nó (`POST /command` no AP local, botão de energia). Candidato: `speed_ms` no firmware (parada autônoma).
5. **`clear_nvs` passa pelo Hub** como `pump_command`. O app não o emite, mas qualquer cliente do SoftAP pode. Candidato: filtrar no Hub (aceitar só `reset_volume`, `start`, `stop`).
6. **Recuperação automática após queda de energia** retoma o bombeio sem comando. Aceitável em batelada longa; perigoso em bancada. Documentar no procedimento de energização: religar **com o tubo no vaso correto**.
7. **Porta do sensor invisível ao app**: se `sensorEnable` estiver ligado e o tubo seco, o motor não gira e a telemetria mostra `speed = 0` sem dizer por quê. Candidatos: `sensorEnable`/`sensorBypass` na whitelist do Hub e um campo `gate` no push.
8. **PID sem eco** — a UI está travada por isso; ou o firmware ecoa `kp/ki/kd`, ou a UI fica assim.
9. **Latência de comando** 0–2 s (poll) + latest-wins na `pumpBox`: dois cliques rápidos (p. ex. "zerar" e "enviar perfil") fazem o segundo substituir o primeiro antes do poll. O app já serializa pelo `PumpCommandPending`; receitas devem esperar o ack entre passos.
10. **`start` com `mode = 0`** marca `active` sem bombear. O app não usa `start`; não expor.
11. **Motor**: CAD com stepper, firmware para DC — confirmar antes de qualquer conclusão sobre torque/velocidade mínima (`PWM_BREAKAWAY = 155` foi ajustado para o motor real).

### 1.11 Checklist de bancada (fecha os itens acima)

- [ ] Identificar motor e driver montados; conferir sentido positivo = fluxo para o vaso.
- [ ] `pumpComm:1` → `PumpOnline` em ≤ 4 s; desligar o nó → `PumpOnline=false` em ≤ 4 s; alarme "Bomba externa offline" no app.
- [ ] Calibração volumétrica: S = 250/500/1000 × 60 s, réplica em 500; R² e resíduos; aplicar; conferir 10 min de perfil constante contra o recipiente (volume real × `PumpVol`).
- [ ] Após a calibração, testar potenciômetros (esperado: mortos até reboot — item 3) e registrar.
- [ ] Perfil constante 5 min com `final_t` = 5: parada autônoma, `mode` volta a 0, volume zerado.
- [ ] Desligar energia no meio de um perfil e religar: confirmar retomada automática (item 6) e decidir se é o comportamento desejado.
- [ ] Reiniciar o Hub com a bomba ligada e enviar `reset_volume`: deve ser aplicado (semeadura de `cmd_id`, Hub 2026-09-12).
- [ ] Com `sensorEnable:1` por `POST /command` local e tubo seco: motor parado; molhar: parte em ≤ 50 ms + 500 ms de trava.

---

## 2. Sensor de distância (`sensor-distancia`, v11) — a preencher

Fontes já auditadas em 2026-09-12: `firmware/distance-sensor/src/protocol/ConfigCodec.cpp` (dedupe de `cmd_id`, faixas, NVS só em mudança), `FirmwareApp.cpp` (push `distance=-1` em falha do VL53L0X, escada de recuperação). Contrato: `sensor-distancia/docs/PROTOCOL.md`. Chaves pelo Hub: `distanceOffsetMm`, `distanceSamplePeriodMs`, `distanceSendPeriodMs`, `distanceResetNvs` (carona na resposta do push).

## 3. Fluxômetro (`fluxometro`, v11) — a preencher

Contrato: `fluxometro/docs/PROTOCOL.md`; Hub: caixa confiável própria com `cmd_id` semeado por boot. Lacuna conhecida: `reconnect_wifi` é ecoado mas não tem comando no app.

## 4. Sensor de biomassa (`sensor-biomassa`, v11) — a preencher

Contrato: `sensor-biomassa/docs/PROTOCOL.md`; regra do Hub: **um `command` por revisão** (`set_it`, `set_pwm`, `set_gear`, `ema`, `probe_period`); headroom de flash a 5,3 kB do piso (não acrescentar eco sem remedir).

## 5. Agitador de frasco (`frasco-agitador`) — a preencher

Contrato: `frasco-agitador/docs/PROTOCOL.md`. Particularidade: o potenciômetro de bancada **sobrepõe** o app (`AgitatorPotActive`).

## 6. Servo drive (`ESP32S3-SERVO`, driver 2.0) — a preencher

Contrato: `ESP32S3-HUB/docs/WIRE_CONTRACT_V9.md` (§ Estado desejado do motor) e `ESP32S3-SERVO/`. Rota Modbus direta com `cmd_id`, ACK por readback e falha zero como pré-condição de movimento.
