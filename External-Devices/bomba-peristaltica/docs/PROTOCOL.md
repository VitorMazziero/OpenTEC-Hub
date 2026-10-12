# Protocolo — Bomba Peristáltica (Firmware v3.13)

Especificação completa do protocolo de comunicação, telemetria, rotas HTTP e vocabulário de comandos da **Bomba Peristáltica** com motor DC e ESP32.

> **Comportamento explicado:** este arquivo é o contrato de fio. O que cada chave faz no firmware, o que o hardware faz em consequência, o que é estimado e não medido, e as pegadinhas operacionais (`speed` é pegajoso por USB, recuperação automática após queda de energia) estão em `../../docs/COMANDOS_DISPOSITIVOS_EXTERNOS.md` §1, conferido no código em 2026-09-12.

---

## 1. Identidade e Registro

- **Dispositivo**: Bomba Peristáltica (`peristaltic-pump` / `pump`)
- **Versão do Firmware**: `3.13` (calibração polinomial da 3.12 + contrato de integração de 2026-10-11: recusas, estado de acionamento, sensor, reinícios e dosagem no push)
- **Protocolo de Rede**: HTTP REST / Query params (compatibilidade de fio protocolo 10)
- **Topologia**: Nó periférico que se anuncia ao Hub e envia telemetria periódica (push) enquanto consome comandos (piggyback ou pull).

### Registro Automático (`/nodeHello`)
Ao conectar-se ao Wi-Fi, o nó anuncia sua presença ao Hub Central:
```http
GET /nodeHello?dev=pump&ver=3.13&mac=AA:BB:CC:DD:EE:FF HTTP/1.1
Host: 192.168.4.1
```
- `dev`: `pump`
- `ver`: `3.13` (a 3.12.1 ainda anuncia `3.12`)
- `mac`: Endereço MAC do ESP32 da bomba.

---

## 2. Telemetria Periódica (`GET /pumpData`)

A cada período de telemetria (~1000 ms), o nó envia seus dados operacionais ao Hub:
```http
GET /pumpData?mode=1&pwm=128&speed=45.2&flow=1.265&vol=15.420&v_tgt=15.500&active=1&waiting=0&ack_cmd_id=42&a1=0&b1=0&k1=0&f1=0.028&c1=0&k2=0&f2=0.028&c2=0&transition_speed=500&cal_crc=12AB34CD&kp=0.5000&ki=0.0500&kd=0.0010&pot=1&cyc_vol=15.420 HTTP/1.1
Host: 192.168.4.1
```

### Campos do Push

| Parâmetro | Tipo | Unidade / Formato | Descrição |
| :--- | :--- | :--- | :--- |
| `mode` | `int` | 0 a 5 | Modo de dosagem ativo (ver seção Modos) |
| `pwm` | `int` | 0 a 255 | Ciclo de trabalho PWM aplicado à ponte H |
| `speed` | `float` | passos/unid. | Velocidade comandada do motor |
| `flow` | `float` | mL/min | Vazão instantânea calculada |
| `vol` | `float` | mL | Volume total cumulativo bombeado |
| `v_tgt` | `float` | mL | Volume teórico acumulado esperado para o tempo decorrido |
| `active` | `int` | 0 ou 1 | `1` se em execução ativa (`OP_RUNNING`) |
| `waiting` | `int` | 0 ou 1 | `1` se aguardando retardo inicial `init_t` (`OP_WAITING`) |
| `ack_cmd_id` | `uint32` | inteiro | ID do último comando recebido e aplicado com sucesso |
| `a1`, `b1`, `k1`, `f1`, `c1` | `float` | coeficientes em S | Segmento inferior de quarto grau |
| `k2`, `f2`, `c2` | `float` | coeficientes em S | Segmento superior quadrático |
| `transition_speed` | `float` | S | Velocidade de transição `St`; `Qt=Q(St)` é derivado |
| `cal_crc` | hex uint32 | 8 dígitos | CRC32 do registro `pump_poly_cal` vigente |
| `kp`, `ki`, `kd` | `float` | — | **3.10** Eco dos ganhos do PID de volume (`pid_kp/ki/kd`) |
| `pot` | `int` | 0 ou 1 | **3.10** `1` = potenciômetros de bancada no comando; `0` = travados por `pot:0` ou por um `speed` recebido |
| `cyc_vol` | `float` | mL | **3.10** Volume entregue pelo ciclo de perfil corrente (`vol − volume no início do ciclo`). `vol` passou a ser o contador da sessão |

**3.13** — os nove coeficientes (`a1`…`c2`, `trans_speed`) saem a cada 10 pushes ou quando `cal_crc` muda; `cal_crc` vai em todo push. Campos novos:

| Parâmetro | Tipo | Descrição |
| :--- | :--- | :--- |
| `drv` | `int` | Quem comanda o motor: 0 parado · 1 potenciômetros · 2 velocidade manual (`speed`) · 3 perfil aguardando `init_t` · 4 perfil rodando · 5 dosagem entre pulsos · 6 pulso · 7 OTA |
| `sens` | `0/1` | Sensor de líquido habilitado (botão ou `sensorEnable`, sem `sensorBypass`) |
| `wet` | `0/1` | Motor liberado pelo sensor de líquido (ou sensor desligado) |
| `t_min` | `float` | Tempo do perfil no relógio do nó (min) |
| `up` | `uint32` | Uptime (s) |
| `boot` | `uint32` | Contador de inicializações (NVS `boot_n`) |
| `rst` | `int` | `esp_reset_reason()` do último boot |
| `rej_cmd_id` | `uint32` | Último comando do Hub recusado (0 = nenhum, ou recusa de comando local) |
| `rej_err` | texto | Motivo da recusa (§3.1); `none` sem recusa |
| `d_act`, `d_pulse` | `0/1` | Dosagem ativa; pulso em curso |
| `d_ml`, `d_per`, `d_spd`, `d_rate`, `d_tot`, `d_dead` | `float` | Eco dos parâmetros da dosagem (mL, s, S, mL/min, mL, ms) |
| `d_done`, `d_n`, `d_next` | `float`, `uint32`, `uint32` | Volume entregue (mL), pulsos, segundos até o próximo |
| `d_short` | `float` | Volume que não saiu porque o sensor de líquido segurou o motor (mL) |

`pot` passa a valer `0` também durante a dosagem (os potenciômetros não comandam o motor).

---

## 3. Entrega Confiável de Comandos

O Hub responde à requisição `/pumpData` entregando o próximo comando pendente da fila, ou o nó consulta `GET /pumpCommand`:

### Resposta do Hub com Comando
```json
{
  "cmd_id": 42,
  "command": "start"
}
```
ou calibração polinomial dupla atômica:
```json
{
  "cmd_id": 43,
  "a1": 0.0,
  "b1": 0.0,
  "k1": 0.0,
  "f1": 0.0280,
  "c1": 0.0,
  "k2": 0.0,
  "f2": 0.0280,
  "c2": 0.0,
  "transition_speed": 500
}
```

Quando o comando é processado com sucesso:
1. Os parâmetros são atualizados em memória e gravados na NVS (quando marcado `g_configDirty`).
2. O nó atualiza seu `g_lastAppliedHubCommandId = cmd_id`.
3. No próximo envio de `/pumpData`, o campo `ack_cmd_id=43` é emitido, confirmando a entrega e aplicação.

**`cmd_id` é inteiro de 32 bits** (3.12.1). O Hub numera a partir de uma base aleatória ≥ 100 000 000 por boot; até a 3.12 o nó lia o id como `float`, o ACK nunca batia e ids vizinhos eram descartados como reentrega.

**3.13 — resposta do push:** se a resposta de `/pumpData` for um JSON com `cmd_id` (Hub 10.9), o nó o processa pelo mesmo caminho do poll, com a mesma deduplicação. O poll de `/pumpCommand` (2 s) continua de reserva.

### 3.1 Recusa (3.13, padrão do banho r3.2)

O nó valida **o quadro inteiro antes de aplicar** (`control/FrameValidate.h`). Se qualquer parte que pode ser recusada falhar, nada é aplicado, o nó **não** dá ACK e publica `rej_cmd_id=<id>&rej_err=<código>` até o próximo quadro aceito. Reentregas do id recusado não são reprocessadas. Um comando local (USB, `POST /command`) recusado publica `rej_cmd_id=0` com o código; `POST /command` responde `422 Rejected: <código>`.

| Código | Recusa |
| :--- | :--- |
| `mode_range` | `mode` fora de 0..5 |
| `seg_range` | `num_segments` fora de 2..100 |
| `cal_busy` | calibração com perfil rodando ou aguardando |
| `cal_incomplete` | calibração sem os nove campos finitos |
| `cal_st_range` | `transition_speed` fora de (0, 1000) |
| `cal_shape` | curva sem continuidade C0+C1 em `St`, negativa ou não monotônica |
| `dose_ml`, `dose_period`, `dose_speed`, `dose_rate`, `dose_total`, `dose_dead` | parâmetro da dosagem fora da faixa |
| `dose_short` | parte útil do pulso menor que 200 ms |
| `dose_fit` | pulso + 0,5 s não cabe no período |
| `dose_busy` | `dose:1` com perfil em execução |
| `dose_range` | `dose` diferente de 0 e 1 |

Com o Hub 10.8, que não conhece `rej_cmd_id`, um comando recusado fica pendente no Hub até ser substituído (o nó não o reprocessa).

---

## 4. Vocabulário de Comandos e Parâmetros

Comandos aceitos tanto via Hub (`/pumpCommand` ou piggyback) quanto localmente via `POST /command`:

### 4.1 Comandos de Controle (`command`)

| Valor de `command` | Descrição |
| :--- | :--- |
| `"start"` | Inicia o ciclo de bombeamento (`OP_RUNNING`), reinicia o tempo e marca o início do ciclo (**3.10:** não zera `vol`; o controlador fecha sobre `cyc_vol`). |
| `"stop"` | Interrompe o bombeamento imediatamente (`OP_IDLE`), define `mode=0` e para o motor (**3.10:** `vol` é mantido). |
| `"reset_volume"` | **Único** comando que zera o volume cumulativo (`vol = 0.0 mL`), mantendo o modo e estado operacional atuais. |
| `"save_config"` | Força a gravação imediata da configuração atual na memória flash NVS. |
| `"load_config"` | Recarrega as configurações salvas da memória flash NVS. |
| `"print_config"`| Imprime no log serial a configuração completa em formato JSON. |
| `"clear_nvs"` | Apaga todas as preferências da NVS e reinicia o microcontrolador ESP32. **Não passa pelo Hub** (2026-09-12: o Hub encaminha apenas `reset_volume`, `start` e `stop`); só por `POST /command` local ou serial. |

### 4.2 Configuração de Modos de Operação (`mode`)

| `mode` | Nome | Equação / Descrição | Parâmetros associados |
| :---: | :--- | :--- | :--- |
| `0` | **IDLE / Manual** | Motor parado ou controlado via potenciômetro/USB direto. | - |
| `1` | **Constante** | Vazão fixa: $Q(t) = \lambda_{\text{const}}$ | `lambda_const` (mL/min) |
| `2` | **Linear** | Rampa de vazão: $Q(t) = \lambda_{\text{lin}} + \phi_{\text{lin}} \cdot t$ | `lambda_linear`, `phi_linear` |
| `3` | **Exponencial** | Crescimento exponencial: $Q(t) = \lambda_{\text{exp}} \cdot e^{\phi_{\text{exp}} \cdot t}$ | `lambda_exp`, `phi_exp` |
| `4` | **Polinomial** | Polinômio de grau até 5: $Q(t) = \sum_{i=0}^5 p_i t^i$ | `p0`, `p1`, `p2`, `p3`, `p4`, `p5` |
| `5` | **Linear por Partes** | Interpolação linear de múltiplos segmentos $(t_i, q_i)$ | `num_segments`, `t0`..`tN`, `q0`..`qN` |

### 4.3 Temporização de Ciclo

- `init_t` (`float`, minutos): Tempo de espera antes de iniciar o bombeamento efetivo (`OP_WAITING`).
- `final_t` (`float`, minutos): Duração máxima total do ciclo; ao atingir, transiciona para `OP_IDLE` e para o motor.

### 4.4 Parâmetros de Calibração

O firmware 3.12 exige os nove campos no mesmo quadro e somente em `OP_IDLE`: `a1`, `b1`, `k1`, `f1`, `c1`, `k2`, `f2`, `c2` e `transition_speed`, com `0 < St < 1000`.

$$Q_1(S)=a_1S^4+b_1S^3+k_1S^2+f_1S+c_1,\quad S\leq S_t$$

$$Q_2(S)=k_2S^2+f_2S+c_2,\quad S>S_t$$

O parser exige continuidade de valor e derivada em `St`, vazão não negativa e monotonicidade em `0..1000`. A inversa `Q -> S` é calculada por bisseção. Não existe comando, eco ou migração linear. Sem registro polinomial atual e válido, as conversões retornam zero até a primeira calibração ser aplicada.

Pelo Hub 10.4, o aplicativo usa `pumpA1..pumpC2` e `pumpTransitionSpeed`; o Hub traduz para os nomes acima. A confirmação requer ACK concluído, os nove ecos e `PumpCalCrc`.

### 4.5 Parâmetros de Controle em Malha Fechada (PID)

- `pid_kp` (`float`): Ganho proporcional do compensador de volume.
- `pid_ki` (`float`): Ganho integral do compensador de volume.
- `pid_kd` (`float`): Ganho derivativo do compensador de volume.

### 4.6 Opções de Sensores e Hardware

- `speed` (`float`, −1000..1000): velocidade interna manual em modo ocioso (negativo = sentido inverso); `0` para. Trava os potenciômetros até `pot:1`. **3.10:** opcional `speed_ms` (`float`, ms > 0) no mesmo JSON — o nó zera a velocidade sozinho ao expirar (parada autônoma). Pelo Hub: `pump_speed`, `pump_speed_ms`. **3.13:** um `speed` vindo do Hub sem `speed_ms` para sozinho em 300 s; por USB ou `POST /command` continua sem prazo. **3.12.1:** sair de um perfil por `speed` apaga o checkpoint de recuperação.
- `pot` (`float`, 1.0 ou 0.0) **3.10**: `1` devolve o motor aos potenciômetros de bancada e esquece qualquer `speed`; `0` trava os potenciômetros. Pelo Hub: `pump_pot`. Ecoado como `pot` no push.
- `disablePot` (`float`, 1.0 ou 0.0): grafia 3.9 de `pot:0`/`pot:1` invertida; mantida para clientes locais.
- `sensorEnable` (`float`, 1.0 ou 0.0): Habilita/desabilita o sensor de gotas/vazão óptico.
- `sensorBypass` (`float`, 1.0 ou 0.0): Modo bypass para calibração sem interrupção de sensor.
- `sensorButtonOverride` (`float`, 1.0 ou 0.0): Permite sobreposição do botão físico.

---

## 5. Endpoints HTTP Locais do Nó

O nó executa um servidor Web assíncrono na porta 80:

| Método | Rota | Descrição |
| :--- | :--- | :--- |
| `GET` | `/` | Interface Web HTML local para visualização de status e controle |
| `GET` | `/readData` | Retorna o JSON mais recente de telemetria da bomba |
| `GET` | `/diag` | Informações completas de diagnóstico (JSON) |
| `POST`| `/command` | Recebe payload JSON de comando |
| `GET` | `/update` | Página de atualização OTA de firmware via browser |
| `POST`| `/update` | Endpoint de upload do binário compilado (`peristaltic-pump.ino.bin`) |

### Resposta de `GET /diag` (3.13, ≤ 511 bytes: o Hub guarda até 511)
```json
{
  "device": "peristaltic-pump", "version": "3.13", "build": "3.13",
  "uptime_s": 3600, "boot": 12, "rst": 1, "free_heap": 184500,
  "ssid": "ModuloTECNAL_1", "rssi": -58, "ip": "192.168.4.15", "mac": "24:6F:28:XX:XX:XX",
  "hub_fail_streak": 0, "mode": 0, "drv": 5, "vol": 12.300,
  "last_cmd_id": 512345001, "rej_cmd_id": 0, "rej_err": "", "cal_crc": "00000000",
  "dose_active": true, "dose_done": 3.500, "push_trunc": 0
}
```
`wifi_status`, `ota`, `op_state` e `flow` saíram (`drv` cobre OTA e estado). Acima de 511 bytes o nó responde `{"device":…,"version":…,"diag_truncated":true}`.

---

## Dosagem por pulsos (3.13)

Para vazões abaixo da faixa estável do motor (ex.: 50 mL em 12 h = 0,069 mL/min). A vazão **não** vem da calibração polinomial: é o valor medido pelo operador na velocidade fixa `dose_speed`.

| Chave | Faixa | Descrição |
| :--- | :--- | :--- |
| `dose_ml` | (0, 100] mL | Volume por pulso |
| `dose_period_s` | [2, 86400] s | Intervalo entre inícios de pulso |
| `dose_speed` | [1, 1000] | Velocidade do motor durante o pulso (mesma unidade de `speed`) |
| `dose_rate_mlmin` | > 0 | Vazão medida nessa velocidade |
| `dose_total_ml` | >= 0 | Volume total; 0 = sem limite. O último pulso é encurtado |
| `dose_dead_ms` | [0, 2000] ms | **3.13** Atraso de partida compensado (t₀, da verificação em pulsos); 0 = como a 3.12 |
| `dose` | 1 / 0 | Inicia (primeiro pulso imediato, zera o entregue) / para |

- Duração do pulso: `60·dose_ml/dose_rate_mlmin + dose_dead_ms/1000` s. A parte útil (sem o atraso) deve ter ≥ 200 ms e o pulso, mais 0,5 s de folga, deve caber no período. Volume contado: `dose_rate_mlmin · max(0, motor_ms − dose_dead_ms) / 60000`. Parâmetros inválidos recusam o quadro (§3.1).
- O prazo do pulso é armado no core 0 no instante em que o motor liga. Se o sensor de líquido segurar o motor por toda a janela do pulso, ele é dado como perdido; o que deixou de sair soma em `d_short`.
- Estado na NVS a cada pulso com período ≥ 60 s; abaixo disso, no máximo uma vez por minuto (sempre ao cancelar ou concluir).
- Os parâmetros ficam na NVS e podem vir no mesmo quadro de `"dose":1`. Com a dosagem ativa, parâmetros válidos valem a partir do próximo pulso.
- `command` `start`/`stop`, `mode`, `speed` e a gravação OTA cancelam ou interrompem a dosagem. Após reinício a dosagem retoma 5 s depois do boot.
- Telemetria USB e `/readData`: `dose_active`, `dose_pulse`, `dose_ml`, `dose_period_s`, `dose_speed`, `dose_rate_mlmin`, `dose_total_ml`, `dose_delivered_ml`, `dose_pulses`, `dose_next_s`, `dose_dead_ms`, `dose_short_ml`, `drv`, `liquid_sensor`, `motor_allowed`, `boot`, `reset_reason`, `rej_cmd_id`, `rej_err`, `fw_build`. Push ao Hub: campos `d_*` (§2).
- Pelo Hub 10.9: `pump_dose`, `pump_dose_ml`, `pump_dose_period_s`, `pump_dose_speed`, `pump_dose_rate_mlmin`, `pump_dose_total_ml`, `pump_dose_dead_ms` (o Hub tira o prefixo `pump_`).