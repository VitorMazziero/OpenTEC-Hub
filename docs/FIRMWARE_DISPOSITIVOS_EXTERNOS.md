# Firmwares dos dispositivos externos — o que gravar

> **Data:** 2026-08-29 · **Plano:** [PLANO_DISPOSITIVOS_EXTERNOS.md](PLANO_DISPOSITIVOS_EXTERNOS.md) §4
> **Contrato de fio:** [PROTOCOL.md](PROTOCOL.md) §2.0.1, §3.4, §3.5

Os firmwares vivem fora deste repositório, em
`D:\OneDrive\Doutorado_CNPq\_Automacao_Controle\_devices\`. Cada dispositivo alterado ganhou
uma **pasta nova ao lado da anterior**, de modo que a versão que está gravada hoje continua
intacta para comparação. O Hub v8 é a exceção: foi editado no lugar, por ser a versão de
trabalho ainda não finalizada.

## Resumo

| Dispositivo | Estava | Gravar | Obrigatório? |
|---|---|---|---|
| **Hub ESP32-S3** | `OpenTEC_ESP32_v8` | `OpenTEC_ESP32_v8` *(editado no lugar)* | **sim — grave primeiro** |
| **Frasco agitador** | `frasco_agitador_03` | `frasco_agitador_04` | **sim** |
| **Bomba peristáltica** | `v_3_2_DC_motor_peristaltic` | `v_4_DC_motor_peristaltic` | **sim** |
| **Sensor de biomassa** | `biomass_sensor_analog_v04_direct` | `biomass_sensor_analog_v05_hubsync` | **sim** |
| **Fluxômetro** | `flowmeter_OpenTECHUB_V05` | — *sem alteração* | não |
| **Sensor de distância** | `SensorDistanciaOpenTEC_v03_reconnect` | — *sem alteração* | não |

**Ordem de gravação: o Hub primeiro.** Ele é retrocompatível com os três nós antigos — a caixa
de comando revisionada entrega o mesmo JSON com uma chave `cmd_id` a mais, que um nó v03/v04
simplesmente ignora, e a ausência de `ack_cmd_id` apenas faz o comando ser reentregue. Os nós
novos, porém, **não** funcionam plenamente contra o Hub antigo: `/agitatorData` não existe lá, e
o `&idle=1` da biomassa seria contado como amostra fresca.

O app funciona com qualquer combinação: sem as chaves novas ele infere presença por
envelhecimento local, mais lento e mais grosseiro, e o chip `roteamento` fica apagado por não
haver eco para comparar.

---

## Hub ESP32-S3 v8 — editado no lugar

**`_devices\OpenTEC_control\_Wifi Hub\Software\_ESP32S3_firmware\OpenTEC_ESP32_v8\`**

1. **`ReliableMailbox`** — a caixa revisionada do fluxômetro, generalizada para biomassa, bomba
   e agitador. O comando fica retido até o nó devolver o `cmd_id` que aplicou. A caixa v6 era
   limpa na leitura, então um comando perdido no ar sumia sem aviso, e `setPending`
   sobrescrevia um comando ainda não lido — foi assim que um `blank` emitido logo antes de um
   `start` era silenciosamente substituído. O servo continua consumo-na-leitura de propósito:
   aquele nó é somente-leitura, e um comando perdido lá não tem consequência física.

2. **Presença publicada sempre** — `BiomassOnline`, `PumpOnline`, `DistanceOnline` e
   `AgitatorOnline` saem em todo quadro, mesmo falsos. *Sempre* é o ponto: a ausência da chave
   passa a significar "este Hub é anterior a ela", que não é a mesma coisa que `false`. A bomba
   não tinha janela de validade nenhuma e republicava a última amostra de um nó morto para
   sempre (`pumpLastUpdate` + `PUMP_TIMEOUT` de 4 s corrigem isso).

3. **Roteamento ecoado** — `BiomassCommEnabled`, `PumpCommEnabled`, `DistanceCommEnabled`. O Hub
   guarda esses flags na NVS e o PC guarda os dele em disco; depois de um reboot os dois podiam
   divergir, e a partir daí o Hub descartava todo sub-comando daquele dispositivo em silêncio.

4. **Novo handler `/agitatorData`** — recebe `pct`, `dir`, `pot`, `src` e `ack_cmd_id`.

5. **Dois relógios para a biomassa** — `biomassLastUpdate` (presença, qualquer push) e
   `biomassSampleLastUpdate` (só push não-idle). Sem isso o heartbeat do v05 deixaria a última
   absorbância de um sensor parado na tela como se fosse atual — exatamente o defeito que o
   heartbeat existe para corrigir. `BiomassOnline` **não** é condicionado a `biomassCommOn`:
   "o sensor está lá e você está com o roteamento desligado" é um estado real que precisa ser
   visível.

6. **Duas janelas para a distância** — `DISTANCE_TIMEOUT` (1,2 s) continua governando o
   intertravamento de espuma, que atua bombas e não pode agir sobre leitura velha;
   `DISTANCE_PRESENCE_TIMEOUT` (3 s) governa o que o operador vê, para não piscar "offline" a
   cada pacote perdido a 1 Hz.

7. `jsonResponse.reserve()` subiu de 2048 para 2560 bytes (pior caso medido ~1550).

> **Verificar após gravar:** o log de heap a cada 60 s (`[ESP32_INFO]: Heap livre:`) não deve
> cair progressivamente. Os quadros novos acrescentam ~330 bytes por telemetria.

---

## Frasco agitador — `frasco_agitador_04`

**`_devices\Frasco_Agitador\software\Firmware\frasco_agitador_04\`**

O nó **nunca reportou nada ao Hub**. Ele só puxava comando e respondia ao próprio `/read`
local, então o Hub — e portanto o app — não sabia literalmente nada sobre ele: nem presença,
nem velocidade, nem confirmação. O PC comandava esse motor no escuro.

1. **Push para `/agitatorData` a cada 1 s** com magnitude e sentido realmente acionados, se o
   potenciômetro de bancada está ativo, e **qual fonte moveu o motor por último**.
2. **`cmd_id` idempotente** no `pollHub()`, com `ack_cmd_id` na telemetria.

O campo `src` é o que muda a operação. O Hub transforma um comando de desligar em
`ActivePot = agitatorReEnablePot`, e o `loop()` relê o botão de bancada no ciclo seguinte
sempre que isso estiver ligado — uma parada com o botão em 60 % religa o motor em 60 %.
Reportar a fonte é o que permite ao app dizer que o potenciômetro está no comando, em vez de
mostrar um setpoint que o nó não está mantendo.

> Do lado do app, a **parada segura** agora envia `agitatorReEnablePot:0` junto, bloqueando o
> potenciômetro até ser rearmado deliberadamente. O **Desligar** comum mantém a preferência.

---

## Bomba peristáltica — `v_4_DC_motor_peristaltic`

**`_devices\Bomba_Peristaltica\v.03.1\Software\v_4_DC_motor_peristaltic\`**

1. **`cmd_id` idempotente** em `pollHubForCommands()`. A idempotência importa aqui: `mode`,
   `init_t` e `final_t` chamam `resetOperationState()`, então reaplicar um comando reentregue
   zeraria o relógio do perfil no meio de uma dosagem.
2. **`&ack_cmd_id=`** no push de `/pumpData`.

> **Mudança relacionada, do lado do app:** desativar a bomba agora são **dois quadros
> ordenados**, `{"mode":0,"speed":0}` e depois `{"pumpComm":0}`. O quadro único da v.6 não
> parava a bomba — o Hub limpava o próprio flag de roteamento ao parsear e em seguida
> descartava o `mode:0` que viajava ao lado, então o nó continuava dosando e só a telemetria
> silenciava. Nada muda no nó por causa disso; está registrado no cabeçalho do arquivo porque
> explica por que a sequência de desligamento ficou diferente no fio.

---

## Sensor de biomassa — `biomass_sensor_analog_v05_hubsync`

**`_devices\Transmitancia\v.02\software\firmware\biomass_sensor_analog_v05_hubsync\`**
(inclui a cópia de `web_ui.h`, que faz parte do sketch)

1. **Heartbeat de 5 s enquanto IDLE.** `sendDataToHub()` só era alcançável a partir de
   `publishSample()`, então o nó só empurrava enquanto MEDINDO. Em IDLE ficava mudo, a janela
   de 10 s do Hub expirava, e do PC "o operador parou a aquisição" e "o ESP32 caiu da rede"
   ficavam idênticos. O heartbeat leva `&idle=1` para o Hub distinguir batimento de amostra.
2. **`cmd_id` idempotente** e `&ack_cmd_id=` no push. Aqui a idempotência é a mais importante
   das três: `blank` e `start` são rotinas bloqueantes de ~15 s, e refazer um `blank` porque o
   Hub reentregou jogaria fora uma referência boa no meio de um cultivo.

---

## Sem alteração

**Fluxômetro `flowmeter_OpenTECHUB_V05`** — é o dispositivo de referência. Já tinha caixa
revisionada, `ack_cmd_id`, presença publicada e polling a 10 Hz. Todo o trabalho acima consiste
em trazer os outros ao nível dele.

**Sensor de distância `SensorDistanciaOpenTEC_v03_reconnect`** — nada a fazer no nó. Ele não
recebe comandos, e o filtro de estagnação com o sentinela `-1` já dá semântica correta. O ajuste
necessário (a janela de 1,2 s ser apertada demais para um push de 1 Hz) foi feito **no Hub**,
sem alargar o intertravamento de espuma.

> Criar uma `v04` só por simetria seria ruído: um arquivo novo sem mudança funcional torna o
> histórico de versões menos informativo, não mais.

---

## Depois de gravar

Sequência de verificação em bancada, do mais barato ao mais caro:

1. Com tudo ligado, conferir em **Controle → Dispositivos Externos** que os quatro pontos de
   estado estão verdes e nenhum chip aparece.
2. Desligar fisicamente um nó de cada vez. A linha correspondente deve ir a `desconectado`
   em ≤ 3 períodos de telemetria, e o **valor lido deve virar `—`**, não congelar.
3. Desligar o roteamento de um dispositivo pelo interruptor e reiniciar o Hub. O chip
   `roteamento` deve acender quando os dois discordarem.
4. Emitir `branco` e, em seguida, `iniciar` na biomassa. O `branco` não pode ser perdido — o
   segundo botão fica travado até o primeiro ser confirmado.
5. Desativar a bomba com um perfil em curso e confirmar no log do nó que ele **parou**, não
   apenas ficou silencioso.
6. Com o potenciômetro do agitador em ~60 %, executar a **parada segura** e confirmar que o
   motor não volta a girar.
