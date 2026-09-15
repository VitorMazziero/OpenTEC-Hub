# Protocolo — Bomba Peristáltica (Firmware v3.12)

Especificação completa do protocolo de comunicação, telemetria, rotas HTTP e vocabulário de comandos da **Bomba Peristáltica** com motor DC e ESP32.

> **Comportamento explicado:** este arquivo é o contrato de fio. O que cada chave faz no firmware, o que o hardware faz em consequência, o que é estimado e não medido, e as pegadinhas operacionais (`mode:0` zera o volume, `speed` é pegajoso, recuperação automática após queda de energia) estão em `../../docs/COMANDOS_DISPOSITIVOS_EXTERNOS.md` §1, conferido no código em 2026-09-12.

---

## 1. Identidade e Registro

- **Dispositivo**: Bomba Peristáltica (`peristaltic-pump` / `pump`)
- **Versão do Firmware**: `3.12` (primeira implantação; somente contrato polinomial atual)
- **Protocolo de Rede**: HTTP REST / Query params (compatibilidade de fio protocolo 10)
- **Topologia**: Nó periférico que se anuncia ao Hub e envia telemetria periódica (push) enquanto consome comandos (piggyback ou pull).

### Registro Automático (`/nodeHello`)
Ao conectar-se ao Wi-Fi, o nó anuncia sua presença ao Hub Central:
```http
GET /nodeHello?dev=pump&ver=3.12&mac=AA:BB:CC:DD:EE:FF HTTP/1.1
Host: 192.168.4.1
```
- `dev`: `pump`
- `ver`: `3.12`
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

- `speed` (`float`, −1000..1000): velocidade interna manual em modo ocioso (negativo = sentido inverso); `0` para. Trava os potenciômetros até `pot:1`. **3.10:** opcional `speed_ms` (`float`, ms > 0) no mesmo JSON — o nó zera a velocidade sozinho ao expirar (parada autônoma). Pelo Hub: `pump_speed`, `pump_speed_ms`.
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

### Resposta de `GET /diag`
```json
{
  "device": "peristaltic-pump",
  "version": "3.9",
  "uptime_s": 3600,
  "free_heap": 184500,
  "wifi_status": 3,
  "ssid": "TECNAL-WIFI",
  "rssi": -58,
  "ip": "192.168.4.15",
  "mac": "24:6F:28:XX:XX:XX",
  "hub_fail_streak": 0,
  "ota": false,
  "op_state": 1,
  "mode": 1,
  "flow": 1.250,
  "vol": 12.300
}
```
