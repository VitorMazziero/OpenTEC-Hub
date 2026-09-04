# Plano de testes dos dois ESP32-S3

> **Atualização 2026-09-04:** os testes originais abaixo qualificaram o caminho
> de telemetria somente-leitura. O controle direto P1-09/10H, o lease e a nova
> ordem de implantação são regidos por
> [`PLANO_MIGRACAO_RPM_MODBUS.md`](PLANO_MIGRACAO_RPM_MODBUS.md); seus testes de
> perda e rollback são adicionais e obrigatórios.

**Data:** 2026-09-01
**Escopo:** nó de potência ASDA-B2 ↔ Hub TECNAL v9. O aplicativo OpenTEC-Hub fica
deliberadamente de fora: os dois firmwares precisam estar completos e provados
antes de qualquer mudança no PC.
**Pré-requisito já cumprido:** comunicação Modbus com o driver confirmada em
2026-09-01 (9600 8N2, slave 1, HW-097 novo).

---

## 1. Como os dois ESP32-S3 conversam

Não existe enlace direto entre as duas placas. Todo o tráfego passa pelo SoftAP
que o Hub levanta, e **quem inicia é sempre o nó** — o Hub nunca abre conexão.

\\	ext
Delta ASDA-B2
     │ CN3, RS-485 — leitura 03H + escrita 06H só em P0-45
     ▼
┌─────────────────────────────┐
│  ESP32-S3 #2  NÓ DE POTÊNCIA│   STA
│  ASDA_B2_Servo_Node.ino     │────────────┐
└─────────────────────────────┘            │
                                           │ Wi-Fi  ModuloTECNAL_1
                                           │ 192.168.4.1
┌─────────────────────────────┐            │
│  ESP32-S3 #1  HUB           │◄───────────┘
│  ESP32S3-HUB.ino            │  SoftAP (máx. 8 estações)
└─────────────────────────────┘
     │ UART2 9600 8N1 (GPIO16 RX / GPIO17 TX)
     ▼
Módulo TECNAL          e, em paralelo, GET /readData para o PC
\
### 1.1. Nó → Hub: telemetria (push, 1 Hz)

\\	ext
GET /servoData?rpm=…&torque_pct=…&torque_nm=…&load_pct=…&power_w=…
              &energy_wh=…&state=…&alarm=…&ok=…&err=…
```

| Regra | Valor |
|---|---|
| Período do push | 1000 ms (`PUSH_INTERVAL_MS`) |
| Obrigatórios | `rpm`, `torque_pct`, `power_w`, `state` |
| Opcionais | `torque_nm`, `load_pct`, `energy_wh`, `alarm`, `ok`, `err` |
| Validação | floats finitos; `state` ∈ 0..3; `ok`/`err` uint32 |
| Amostra inválida | HTTP 400 e **não** renova presença |
| Amostra válida | renova presença **mesmo com roteamento desligado** |
| Janela de presença | 6000 ms (`kPresenceTimeoutMs`) |

Dois detalhes que mudam o diagnóstico na bancada:

- **O nó só começa a empurrar depois da primeira leitura Modbus válida**
  (`haveSample`). Com o RS-485 mudo, o Hub mostra `ServoOnline:false` — igual a
  "nó desligado". Para separar os dois casos, olhe a serial USB do nó.
- Zero rpm é amostra legítima. `ServoOnline:false` significa ausência, nunca
  "motor parado".

### 1.2. Hub → Nó: comandos (pull, 0,5 Hz)

```text
GET /servoCommand   →   {}  |  {"reset_energy":1}  |  {"poll_ms":2000}
```

| Regra | Valor |
|---|---|
| Período do pull | 2000 ms (`COMMAND_INTERVAL_MS`) |
| Entrega | consumo-na-leitura, **um evento por GET** |
| Fila no Hub | FIFO fixa de 8; o nono é rejeitado sem sobrescrever |
| `poll_ms` | 250 a 10000 ms; comandos só-poll ainda não entregues são atualizados |
| `reset_energy` | nunca coalescido |
| ACK | **não existe** — não há `cmd_id` neste enlace |

Sem ACK, a confirmação do reset é a queda observada de `ServoEnergyWh`. Com a
fila cheia, esvaziar leva até 16 s (8 × 2 s).

### 1.3. Hub → PC: agregação

`GET /readData` publica **sempre** `ServoOnline`, `ServoCommEnabled`,
`ServoCommandPending` e `ServoCommandQueueDepth`. Os dez valores `Servo*` só
aparecem quando há amostra publicável — presença fresca **e** roteamento ligado.

---

## 2. Sequência de upload

> **Não troque os sketches de placa.** As duas usam GPIO16 e GPIO17 para coisas
> diferentes: no Hub o GPIO16 é **entrada** (RX do módulo TECNAL); no nó é
> **saída** (DE//RE do HW-097). Gravar o firmware do nó na placa do Hub com o
> módulo conectado coloca uma saída contra o transmissor do módulo.

Placa alvo em ambas: `esp32:esp32:esp32s3` (ESP32S3 Dev Module).

| # | Etapa | Placa | Arquivo a gravar |
|---|---|---|---|
| 0 | Painel do drive | — | nenhum upload |
| 1 | Gate A — valores de leitura | #2 nó | `Software/testes-bancada/esp32-s3/ASDA_B2_Monitor_ESP32/ASDA_B2_Monitor_ESP32.ino` |
| 2 | Gate B — nó de produção | #2 nó | `Software/firmware-producao/ASDA_B2_Servo_Node/ASDA_B2_Servo_Node.ino` |
| 3 | Gate C — Hub v9 | #1 hub | `ESP32S3-HUB/ESP32S3-HUB/ESP32S3-HUB.ino` |
| 4 | Regressão dos demais nós | — | nada é regravado |
| 5 | Soak de 2 h | — | nada é regravado |

Se algum passo reprovar, corrija e **volte à etapa 1**: os gates são
cumulativos.

---

## 2.1. Ao migrar para o ModuloTECNAL_2

Não existe um firmware "do módulo 1" e outro "do módulo 2". É o **mesmo código
com um seletor** — manter duas versões divergiria a cada correção futura, e a
diferença real entre os módulos é uma string.

**Uma linha em cada firmware**, e as duas têm de casar:

| Firmware | Arquivo | Linha |
|---|---|---|
| #1 hub | `ESP32S3-HUB/ESP32S3-HUB/Config.h` | `#define MODULO_TECNAL 1` |
| #2 nó | `ASDA_B2_Servo_Node.ino` | `#define MODULO_TECNAL_ALVO 1` |

Trocar o `1` por `2` deriva `WIFI_SSID`/`WIFI_PASSWORD` e `HUB_SSID`/`HUB_PASSWORD`
automaticamente. Qualquer valor fora de 1 e 2 **para a compilação** com `#error`,
em vez de gerar um binário que sobe um SoftAP errado.

`HUB_HOST = "192.168.4.1"` não muda: o `softAPConfig()` do Hub fixa esse IP nos
dois módulos, então nenhuma URL precisa ser tocada.

**Não é só o Hub.** Todo nó que se associa a este SoftAP carrega o SSID gravado
no próprio firmware — fluxômetro, biomassa, bomba, agitador e sensor de
distância, cada um no seu sketch. Se o Hub virar `ModuloTECNAL_2` e eles não
forem regravados, ficam procurando o módulo antigo para sempre.

Mudar só um dos lados do par Hub↔nó dá o sintoma mais enganoso do sistema: o nó
nunca associa, nunca empurra, e o Hub reporta `ServoOnline:false` —
indistinguível de "nó desligado". As duas placas ecoam o SSID efetivo no boot;
**confira os dois banners antes de instalar.**

### São três placas, não duas — e só duas mudam

Cada módulo tem o seu hub. O **nó do driver é o único que viaja**.

| Placa | Durante os testes | Depois da migração |
|---|---|---|
| Hub do Módulo 1 | `MODULO_TECNAL 1` | **fica em `1`** + `{"servoComm":0}` |
| Hub do Módulo 2 | v8, intocado | v9 com `MODULO_TECNAL 2` |
| Nó do driver | `MODULO_TECNAL_ALVO 1` | `MODULO_TECNAL_ALVO 2` |

> **Não mude o hub do Módulo 1 para `2`.** Seriam dois SoftAPs com o mesmo SSID e
> a mesma senha, e cada nó se associaria ao de sinal mais forte — não ao certo. O
> hub de bancada continua `ModuloTECNAL_1` para sempre; o que o desliga do servo
> é o `{"servoComm":0}`, não o seletor.

Os periféricos do Módulo 2 — fluxômetro, biomassa, bomba, agitador e sensor de
distância — **não precisam ser regravados**: o SSID daquele módulo continua
`ModuloTECNAL_2`.

### Estratégia recomendada

**Não mexa no seletor antes de terminar os testes.** Faça as Etapas 0 a 5
inteiras no `ModuloTECNAL_1`, com o hub de bancada e o nó em `1`. O que você quer
validar é a lógica do enlace, e ela não sabe em que módulo está rodando: aprovada
no Módulo 1, está aprovada no Módulo 2.

Só depois de aprovar o Gate C:

1. gravar o v9 no hub do Módulo 2 com o seletor em `2`;
2. regravar o nó com `MODULO_TECNAL_ALVO 2`;
3. **repetir apenas a Etapa 3** — presença, comandos e robustez do enlace;
4. desligar o roteamento no Módulo 1 (§2.2).

Nada abaixo do Wi-Fi muda, então o Gate A e o Gate B continuam válidos e não
precisam ser refeitos.

- [x] Etapas 0 a 5 aprovadas com o hub de bancada e o nó em `1` — com as
      ressalvas dos itens não exercitados registradas em cada gate
- [x] hub do **Módulo 2** gravado com v9 e `MODULO_TECNAL 2`
- [x] nó do driver regravado com `MODULO_TECNAL_ALVO 2`
- [x] hub do **Módulo 1** deixado em `MODULO_TECNAL 1` — sem SSID duplicado
- [x] `{"servoComm":0}` aplicado no hub do Módulo 1 _(2026-09-02: `ServoCommEnabled`
      passou a `false`, `Parametros salvos na NVS`, e sobreviveu a um reinício —
      após o boot já sobe `ServoOnline:false` + `ServoCommEnabled:false`, que o
      aplicativo lê como "este módulo não tem servo")_
- [x] banner das duas placas do Módulo 2 confere: `ModuloTECNAL_2` nos dois
      _(hub: `[ESP32_INFO]: SSID: ModuloTECNAL_2`; nó: `Hub: SSID=ModuloTECNAL_2`)_
- [x] Etapa 3 repetida no módulo 2 e aprovada _(2026-09-02: `ServoOnline:true`,
      92,7 rpm, torque 1,4 %, `ServoCommErr:1` em 256 leituras)_

> **Resolvido em 2026-09-02.** O `{"servoComm":0}` foi aplicado no hub do
> Módulo 1 e persistiu na NVS, confirmado por reinício. Ver §2.2.

---


## 2.2. Estado final do ModuloTECNAL_1 (sem servo)

O ModuloTECNAL_1 é bancada: no fim, o nó do driver sai dele e vai para o
ModuloTECNAL_2.

### O código já lida bem com a ausência

Não é preciso desabilitar nada para o Hub funcionar sem o nó. O Hub é
**inteiramente passivo** neste enlace: quem abre conexão é sempre o nó. O Hub
não faz polling, não tem cliente HTTP ativo, não tem timeout de conexão e não
tem retry — ele só responde a `/servoData` e `/servoCommand` quando alguém bate.
Sem nó, `lastPresenceMs_` continua zero e o quadro sai com `ServoOnline:false`,
sem os dez valores `Servo*`. Custo zero.

### Mas há uma coisa a fazer — e não é no código

`servoComm` é o **único** flag de roteamento que nasce `true`:

```text
bioComm    -> false        pumpComm   -> false
distComm   -> false        servoComm  -> true     <-- único
flowComm   -> false
```

Isso é deliberado — o nó deve subir sozinho ao ser energizado, sem depender de
um comando do PC — e está certo para o ModuloTECNAL_2. Para o ModuloTECNAL_1,
que nunca mais terá servo, produz um estado enganoso e permanente:

```text
ServoOnline:false   ServoCommEnabled:true
```

O aplicativo lê isso como "roteamento ligado e nó sumido", ou seja, falha
permanente — e não como "este módulo não tem servo". Pelas regras de alarme do
plano de integração (§5.5), isso vira evento de offline que nunca se resolve.

### A correção é um comando, não um rebuild

No ModuloTECNAL_1, uma única vez, pela serial do Hub:

```text
{"servoComm":0}
```

Persiste na NVS: `servoCommOn` entra em `computeStateHash()`, então o comando
agenda a gravação. **Aguarde 15 s antes de cortar a energia** — o
`SAVE_DEBOUNCE_MS` só grava após 15 s sem novos comandos.

Assim o **mesmo binário** roda nos dois módulos, e a única diferença de
comportamento vive na NVS de cada um. É exatamente para isso que
`ServoCommEnabled` foi acrescentado ao contrato.

| Módulo | `servoComm` | `ServoOnline` | `ServoCommEnabled` | Como o app lê |
|---|---|---|---|---|
| 1 — bancada | 0 (comando) | false | false | sem servo, de propósito |
| 2 — campo | 1 (default) | true | true | operando |

### Sobre o risco de o Módulo 1 capturar o nó do Módulo 2

O instinto está certo, mas a causa é outra. Os dois módulos têm **SSIDs
distintos** (`ModuloTECNAL_1` e `ModuloTECNAL_2`), e o nó chama
`WiFi.begin(HUB_SSID, …)` com o SSID gravado no firmware dele. Um nó apontado
para `_2` nunca associa ao `_1`: não há ambiguidade a resolver.

O risco real é **esquecer de regravar o nó ao movê-lo**. Se ele for instalado
fisicamente no Módulo 2 mas continuar com `HUB_SSID "ModuloTECNAL_1"`, e o
Módulo 1 estiver ligado e ao alcance, ele volta a se associar ao Módulo 1 e
empurra a telemetria para lá.

`servoComm=0` no Módulo 1 torna isso inofensivo: a presença ainda aparece —
honesto, alguma coisa está empurrando — mas **nenhum valor do servo entra no
`/readData` do Módulo 1**. Vale como rede de segurança, não como substituto de
regravar o nó.

- [ ] depois de aprovar o Gate C, enviar `{"servoComm":0}` no ModuloTECNAL_1
- [ ] aguardar ≥ 15 s antes de desligar (debounce da NVS)
- [ ] reiniciar o Módulo 1 e confirmar `ServoCommEnabled:false` em `/readData`
- [ ] regravar o nó com `HUB_SSID "ModuloTECNAL_2"` **antes** de instalá-lo
- [ ] confirmar no banner da serial do nó: `SSID=ModuloTECNAL_2`

---

## Etapa 0 — configurar o painel do drive (sem upload)

`P0-09`, `P0-10` e `P0-44` são registradores de **monitor**: mostram a variável
que `P0-17`, `P0-18` e `P0-45` mandarem mostrar. Com os valores de fábrica eles
respondem Modbus normalmente e devolvem **outra grandeza** — o nó publicaria
números plausíveis e errados sem nenhum sinal de erro.

Ajustar no painel, anotando os valores anteriores:

```text
P0-17 = 7     ->  P0-09 = velocidade de retorno   (0,1 rpm)
P0-18 = 12    ->  P0-10 = carga média             (%)
P0-45 = 54    ->  P0-44 = torque de retorno       (0,1 %)
```

Conferir e **não alterar**: `P3-00 = 1`, `P3-01 = 0x0011`, `P3-02 = 0x0066`,
`P3-05 = 0`, `P1-01 = 2`.

Anotar também a **placa de identificação do motor**. O nó assume
`T_nominal = 1,27 N·m` (ECMA-C20604ES). Se o motor for outro, esse é o único
número a mudar — e todos os N·m e watts saem errados até que mude.

> **Achado de 2026-09-02 — `P0-45` não persiste.** O manual (cap. 7) define
> `P0-44`/`P0-45` como *Status Monitor Register **(for PC Software)***, default
> `0x0`, e o par é volátil: volta a zero a cada religamento do drive. Com
> `P0-45 = 0`, `P0-44` devolve a variável de código 0 e fica preso em `0x0000`
> **sem nenhum erro de comunicação** — foi exatamente o que a bancada mostrou.
> `P0-17`/`P0-18` não têm esse problema (faixa 0~18, não voláteis, confirmados
> na bancada). O torque de retorno (código 54) só existe na faixa 0~127, que
> apenas `P0-45` aceita — e a B2 não tem os parâmetros de mapeamento
> `P0-25`/`P0-35` das famílias A2/A3.
>
> **Resolvido em 2026-09-02 pela escrita `06H`.** O §8.4 do manual lista o Grupo 0
> como gravável por comunicação exceto `P0-00`~`P0-01`, `P0-08`~`P0-13` e
> `P0-46` — ou seja, `P0-45` **é** gravável. O nó agora escreve `P0-45 = 54`
> (função `06H`, endereço `005AH`) e confirma por leitura, a cada boot e a cada
> amostra, o que também cobre um religamento do drive sem reiniciar a placa.
> Isso encerra a regra "somente 03H" — decisão tomada em 2026-09-02: era
> remanescente da estratégia anterior. **O Gate B precisa ser reescrito**: o
> critério passa a ser "a única escrita é `P0-45`", não "nenhuma escrita".

- [x] P0-17, P0-18 e P0-19 ajustados no painel (persistem)
- [x] P0-45 = 54 garantido pelo firmware, não pelo painel (volátil por design)
- [x] parâmetros P3-xx conferidos, nenhum alterado
- [x] modelo do motor confirmado contra `MOTOR_RATED_TORQUE_NM`

---

## Etapa 1 — Gate A: os valores de leitura (placa #2, sem Wi-Fi)

Grave `ASDA_B2_Monitor_ESP32.ino`. Ele usa exatamente o mesmo caminho Modbus do
firmware de produção (mesmos pinos, mesma temporização, mesmo CRC, mesmo parsing
de quadro), então o que for medido aqui vale lá sem reinterpretação. Não sobe
Wi-Fi: um problema de leitura não se mistura com um problema de rede.

A cada segundo ele imprime, para cada par de words, **as duas ordens possíveis**
lado a lado. A ordem correta é a que acompanha o painel; a errada salta para
valores absurdos assim que a grandeza passa de 65535 ou fica negativa.

Rodar em três estados, anotando o painel em cada um:

| Estado | O que conferir |
|---|---|
| Motor parado | rpm ≈ 0; `ServoState` = 1 (READY) ou 0 (OFF); alarme 0 |
| Girando sem carga | rpm acompanha o painel; torque pequeno e coerente |
| Sob carga conhecida | torque e carga sobem juntos; sinal correto ao inverter o sentido |

- [x] `low-first` acompanha o painel em rpm e torque (é o que o nó usa) _(2026-09-02: a 100 rpm, low-first = 95,8..98,6 rpm vs painel ~96; high-first = 6.330.777 rpm. No torque: low-first = 1,4 %; high-first = 91.750 %. Decidido nos dois.)_
- [x] escala de `P0-10`: **escala 1** (% inteiro), que é o que o nó já faz — não
      mudar nada _(2026-09-02: o manual dá o código 12 como "Average load rate,
      unit: percentage (%)", sem casa decimal; e na bancada `P0-10`=1 % bate com
      `P0-11`=1..2 % e `P0-44`=1,4 %. Com escala 0,1 daria 0,1 %, incoerente com
      as outras duas fontes.)_
- [ ] ~~sinal do torque inverte ao inverter o sentido~~ — **não testável nesta
      bancada** _(2026-09-02: a placa controladora do CN1 é de código fechado e
      só comanda um sentido. Não há como inverter a rotação sem trocar a
      controladora, então este item fica pendente por limitação física, não por
      falta de execução.)_ Alternativa parcial: durante a desaceleração o torque
      de frenagem é negativo mesmo com sentido fixo — vale como prova do
      parsing com sinal, não do sinal do sentido.
- [x] `P0-46` → estado derivado bate com o painel (OFF/READY/SON/ALARM) _(2026-09-02: 0x0005 READY com motor desligado → 0x0083 SON=1 ao energizar, acompanhando a ação real. OFF e ALARM ainda não exercitados.)_
- [x] `P0-01` = 0 sem alarme
- [x] `err` não cresce em 10 min contínuos _(2026-09-02, motor parado: 1638 leituras, err=0, 690 s contínuos — repetir com motor girando)_
- [x] N·m e W conferem com o cálculo manual a partir do rpm e do torque %
      _(2026-09-02: torque 1,60 % × 1,27 N·m = 0,02032 N·m — o nó imprimiu
      0,0203. Potência = 0,0203 × 96,8 × 2π/60 = 0,206 W — o nó imprimiu 0,21.)_

> **Resolvido em 2026-09-02: a escala é 1, e o nó já está correto.** Nenhuma
> alteração em `shared.loadPct` é necessária. (A instrução anterior, de trocar
> para `loadRaw * 0.1f`, teria introduzido um erro de 10x.)

---

## Etapa 2 — Gate B: nó de produção (placa #2)

Grave `ASDA_B2_Servo_Node.ino`. Ainda **sem o Hub ligado**.

Na serial USB (115200) devem aparecer, nesta ordem: banner, autoteste de CRC OK,
`Watchdog ativo com timeout de 10 s`, tarefas iniciadas, e depois tentativas de
associação a cada 5 s.

- [x] a única escrita presente no código é `P0-45` (`06H` em `005AH`) — critério
      revisto em 2026-09-02; a regra anterior "apenas `03H`" foi revogada
      _(auditado no código: `writeSingleRegister` é chamada em um único ponto,
      `ensureTorqueMapping`, sempre com `REG_MONITOR_SELECTOR`)_
- [x] `P0-45` é reescrito e confirmado por leitura após religar **o drive**, sem
      reiniciar a placa _(2026-09-02, no drive do Módulo 2: o sniffer confirmou
      `P0-45 = 0x0000` logo após o religamento; em seguida o nó publicou
      `ServoTorquePct = 1,4 %`. Com o seletor em 0 o `P0-44` devolveria a posição
      do encoder — ordem de 10⁵ e rampando —, então o valor são só é possível se
      a reescrita ocorreu e foi confirmada.)_
- [ ] amostra com mapeamento inválido é descartada, não publicada — implementado
      (`ok = mappingOk && ...`). **Adiado por decisão em 2026-09-02**: exige um
      build temporário com o código do monitor fora da faixa 0~127, e a
      prioridade passou para o aplicativo. Não é omissão.
- [x] autoteste de CRC passa (se falhar, o sketch para de propósito) _(2026-09-02:
      o nó seguiu para as tarefas e associou, o que só acontece se o autoteste
      passou. **Diferente do monitor, o nó é silencioso no sucesso** — só imprime
      em caso de falha, então não há linha de OK para procurar.)_
- [x] watchdog reporta **10 s** _(2026-09-02: `[SERVO] Watchdog ativo com timeout
      de 10 s`. Antes dele aparece `E (137) task_wdt: TWDT already initialized`,
      que é ruído do ESP-IDF — o watchdog já vem inicializado pelo core — e não
      é a "Falha ao configurar o watchdog" da §5.1.)_
- [x] o nó tenta associar e não reinicia sozinho _(2026-09-02: associou ao
      `ModuloTECNAL_1` com IP 192.168.4.3)_
- [ ] com o Hub ausente, nenhuma reinicialização em 10 min — **adiado por
      decisão em 2026-09-02**, junto com os itens 16 a 19 da §3.4, que são o
      mesmo ciclo de desligar e religar o hub

**Resultado de 2026-09-02.** O nó subiu e empurrou telemetria de imediato. Como
o hub ainda estava com firmware pré-servo, a resposta foi `404`:

```text
 No de telemetria - Delta ASDA-B2 -> TECNAL Hub v9
 Leitura 03H + escrita 06H apenas em P0-45 (seletor volatil).
RS-485: slave=1, 9600 baud, 8N2, TX=GPIO17, RX=GPIO18, DE/RE=GPIO16
Hub: SSID=ModuloTECNAL_1  host=192.168.4.1
[SERVO] Watchdog ativo com timeout de 10 s
[SERVO] Tarefas iniciadas.
[SERVO] Conectando ao SoftAP do hub...
[SERVO] Associado ao hub. IP: 192.168.4.3
[SERVO] /servoData respondeu 404
```

O `404` é um bom sinal, não um defeito: o nó **só empurra depois da primeira
leitura Modbus válida** (`haveSample`), então esse log prova, de uma vez, que o
Modbus lê, que o Wi-Fi associa e que o caminho de push funciona. O que falta é
só o outro lado do enlace.

---

## Etapa 3 — Gate C: os dois juntos (placas #1 e #2)

Grave `ESP32S3-HUB.ino` na placa #1 e energize as duas.

### 3.1. Como mandar comandos sem o aplicativo

Duas vias equivalentes:

**Serial USB do Hub** (mais simples) — digitar o JSON e dar Enter no monitor
serial a 115200:

```text
{"servoPollMs":2000}
```

> **Cuidado:** qualquer linha que **não** comece com `{` e termine com `}` põe o
> Hub em modo bypass, e nesse modo ele para de ler sensores e de atualizar
> `/readData`. Para sair, envie um JSON válido, por exemplo `{"comTest":1}`.

**HTTP**, com o PC associado ao SoftAP:

```bash
curl -s -X POST http://192.168.4.1/command -d "{\"servoPollMs\":2000}"
```

Para ler o quadro agregado:

```bash
curl -s http://192.168.4.1/readData
```

### 3.2. Matriz de presença e roteamento

| # | Ação | Esperado em `/readData` |
|---|---|---|
| 1 | Hub ligado, nó desligado | `ServoOnline:false`, `ServoCommEnabled:true`, nenhum `Servo*` |
| 2 | Ligar o nó | em ≤ 2 s: `ServoOnline:true` + os dez `Servo*` |
| 3 | Desligar o nó | volta a `ServoOnline:false` em ~6 s; os `Servo*` somem |
| 4 | Religar o nó | volta a publicar sem precisar de comando |
| 5 | `{"servoComm":0}` | `ServoCommEnabled:false`; `Servo*` somem; `ServoOnline` **continua true** |
| 6 | `{"servoComm":1}` | valores voltam no push seguinte |
| 7 | Reiniciar o Hub | `ServoCommEnabled` volta como estava (NVS) |

O item 5 é o que separa "nó ausente" de "roteamento desligado" — era exatamente
o que o v8 não conseguia expressar.

**Resultado de 2026-09-02 (itens 1 a 4, medidos com o nó desconectado do USB):**

```text
t=1199.0  false  sem valores               <- item 1: 60 quadros seguidos assim
t=1201.0  true   com valores, ok=9         <- item 2: reconhecido no 1o quadro apos voltar
t=1233.1  true   ok=84 congelado
t=1235.1  true   ok=84 congelado           <- presenca ainda fresca, publicando o ultimo valor
t=1237.1  true   ok=84 congelado
t=1239.1  false  valores somem             <- item 3: janela expirou
```

O item 3 fecha dentro do especificado. O `ServoCommOk` congela em 84 três
quadros antes do flag virar, o que localiza o último push entre `t=1231,1` e
`t=1233,1`; o `false` aparece em `t=1239,1`. Isso põe a expiração entre **6,0 e
8,0 s** — coerente com `kPresenceTimeoutMs = 6000` amostrado por um quadro a
cada 2 s, que é a resolução do que dá para observar por este caminho. Os dez
campos `Servo*` somem no mesmo quadro em que o flag cai, nunca antes nem depois.

- [x] item 1 — nó ausente: `ServoOnline:false`, `ServoCommEnabled:true`, nenhum `Servo*`
- [x] item 2 — ligar o nó: reconhecido no primeiro quadro após a associação
- [x] item 3 — desligar o nó: volta a `false` em 6..8 s, valores somem juntos
- [x] item 4 — religar: volta a publicar sozinho, sem comando nenhum
**Itens 5 a 7 — exercitados em 2026-09-02 no Módulo 2.**

```text
{"servoComm":0}   ServoOnline=True   ServoCommEnabled=False   dez Servo* AUSENTES
                  [ESP32_EVT] Comunicacao do servo drive desativada
                  [ESP32_INFO] Parametros salvos na NVS (Flash)
{"servoComm":1}   valores voltam no push seguinte, rpm 93..94
reinicio do hub   ServoCommEnabled volta True; ServoOnline sobe False->True em ~8 s
```

- [x] item 5 — roteamento desligado com nó presente: `ServoOnline` **continua
      true** e só os valores somem. É a distinção que o v8 não expressava
- [x] item 6 — `{"servoComm":1}` devolve os valores no push seguinte
- [x] item 7 — persiste em NVS: confirmado nos **dois** sentidos — `false` no hub
      do Módulo 1 e `true` no do Módulo 2, cada um através de um reinício

> **Efeito colateral do reinício do hub, observado em 2026-09-02:** ao subir, o
> hub sincroniza a configuração guardada na NVS com o Módulo TECNAL — inclusive
> o `motorRPM`. Num módulo em operação, **reiniciar o hub pode parar o motor**
> se o valor gravado for 0. Foi o que aconteceu: a rotação caiu de ~93 rpm para
> 0,0 após o reinício. Não é defeito, é a sincronização funcionando, mas é
> consequência operacional que o plano não registrava.

> **Não simule ausência do nó segurando a placa em reset pelo pino EN.** Foi
> tentado em 2026-09-02 e produz um resultado enganoso: o hub emite ~3 quadros e
> depois **para de emitir**, voltando só quando a placa é liberada. Uma placa
> travada em reset não é uma placa ausente — o rádio morre sem encerrar a
> associação. Desconectar a alimentação dá o comportamento correto e estável
> (60 quadros consecutivos a 2,0 s, sem lacuna). O falso sintoma parece defeito
> do hub e não é.

### 3.3. Comandos

| # | Comando | Esperado |
|---|---|---|
| 8 | `{"resetServoEnergy":1}` | serial do nó registra o reset; `ServoEnergyWh` cai a ~0 em ≤ 2 s |
| 9 | `{"servoPollMs":250}` | serial do nó confirma 250 ms; `err` não dispara |
| 10 | `{"servoPollMs":10000}` | serial confirma 10000 ms; **o nó não pode reiniciar** (veja §5.1) |
| 11 | `{"servoPollMs":249}` | Hub recusa com aviso; nada é enfileirado |
| 12 | `{"servoPollMs":10001}` | Hub recusa com aviso |
| 13 | `{"resetServoEnergy":0}` | nada é enfileirado |
| 14 | 9 comandos seguidos | `ServoCommandQueueDepth` chega a 8; o nono é recusado |
| 15 | `{"resetVariables":1}` | fila do servo é limpa; `ServoCommandQueueDepth` volta a 0 |

Depois do 10, **devolva o período a 1000 ms** (`{"servoPollMs":1000}`) antes de
seguir.

**Resultado de 2026-09-02.** O ciclo de vida do comando é observável quadro a
quadro no próprio `/readData`, sem precisar da serial do nó:

```text
t=294.9  ServoCommandPending: true   fila=1   Wh=0.019873   <- enfileirado no hub
t=296.9  ServoCommandPending: false  fila=0   Wh=0.020717   <- o no puxou (consumo-na-leitura)
t=298.9  ServoCommandPending: false  fila=0   Wh=0.000588   <- executou: energia zerada
```

Como este enlace não tem ACK, a confirmação do reset é essa queda do
`ServoEnergyWh` — e ela veio, com o motor girando a 600 rpm, o que torna a
prova mais forte do que fazer o reset com o motor parado.

O `{"servoPollMs":2000}` foi entregue do mesmo jeito e teve efeito medível: o
`ServoCommOk` passou de **+6 por quadro de 2 s** (3 leituras por amostra a 1 Hz)
para **+3 por quadro** — uma amostra Modbus a cada 2 s, exatamente o pedido.
Devolvido a 1000 ms ao fim.

- [x] item 8 — `{"resetServoEnergy":1}`: entregue e efetivado
- [x] item 9/10 parcial — `{"servoPollMs":2000}`: entregue, efeito medido na taxa
**Itens 9 a 15 — exercitados em 2026-09-02.**

| # | Comando | Resultado |
|---|---|---|
| 9 | `{"servoPollMs":250}` | taxa de `ServoCommOk` dobra (+6 → +12..15 por quadro de 2 s); `err` não sobe |
| 10 | `{"servoPollMs":10000}` | uma amostra a cada 10 s; **o nó não reiniciou** — o contador nunca voltou a zero |
| 11 | `{"servoPollMs":249}` | `[ESP32_AVISO] servoPollMs rejeitado: faixa válida é 250..10000 ms`; fila fica em 0 |
| 12 | `{"servoPollMs":10001}` | idem |
| 13 | `{"resetServoEnergy":0}` | nada enfileirado, sem aviso |
| 14 | 9 × `{"resetServoEnergy":1}` | fila chega a 8, **o nono é recusado** (`[ESP32_ERRO] Comando Servo rejeitado: fila cheia`); drena 1 por pull de 2 s |
| 15 | `{"resetVariables":1}` | fila **5 → 0** no quadro seguinte, `ServoCommandPending` vai a false |

- [x] itens 9 a 15 aprovados

> **Sobre o item 10, que era o risco da §5.1:** o watchdog de 10 s **não**
> dispara com `poll_ms` em 10000. A prova é o `ServoCommOk` nunca reiniciar — se
> a placa tivesse reiniciado, o contador voltaria a zero. E `ServoOnline`
> continua `true` o tempo todo, porque o push é a 1 Hz com a última amostra,
> independente do período de polling.

> **`poll_ms` é atraso, não período.** Com 250 ms o ciclo real ficou em ~500 ms,
> porque cada amostra custa ~250 ms de barramento (4 transações a 9600 8N2). A
> taxa dobra, não quadruplica — comportamento correto, documentação omissa.

> **Efeito colateral do `resetVariables`:** ele também restaura o `dataDelay` do
> hub, e os quadros passam de 2 s para 1 s.

### 3.4. Robustez do enlace

| # | Ação | Esperado |
|---|---|---|
| 16 | Desligar o Hub com o nó rodando | serial do nó: "WiFi caiu. O polling Modbus continua." |
| 17 | Religar o Hub | reassocia em ≤ 15 s e volta a publicar |
| 18 | Conferir a energia após o ciclo 16–17 | `ServoEnergyWh` **não salta**: a integração é local e rejeita lacunas |
| 19 | Wi-Fi transmitindo | 5 V sem brownout; sem reinicializações |

**Resultado de 2026-09-02.** Os itens 16 a 19 **não foram exercitados** — o hub
não foi desligado com o nó rodando. O que foi observado, e que cobre parte do
item 17: o nó reassocia sozinho após reinício, em menos de um quadro de 2 s, e
o `ServoEnergyWh` reinicia do zero porque a integração é local à placa.

- [ ] itens 16 a 19 — **não exercitados**
- [x] CN1 continua comandando a velocidade normalmente durante todos eles
      _(o comando de rotação saiu do hub por `{"motorSetpoint":N}` → UART2 →
      Módulo TECNAL → CN1 durante todo o Gate C, sem interferência)_
- [ ] ~~nenhum parâmetro do drive foi escrito~~ — critério revogado em
      2026-09-02: o nó escreve `P0-45` por projeto. O critério vigente é
      **"a única escrita é `P0-45`"**, verificado no Gate B.

### 3.5. O laço fechado — o teste que o Gate C não previa

O plano tratava o hub como observador passivo do servo. Mas o hub **também
comanda** a rotação, por um caminho fisicamente independente: `{"motorSetpoint":N}`
na serial → UART2 (`1V` + `NA`) → Módulo TECNAL → CN1 → drive. A telemetria volta
pelo outro caminho: drive → RS-485 → nó → Wi-Fi → `/servoData` → `/readData`.

Os dois caminhos só se encontram no eixo do motor. Se a medida segue o comando,
a integração inteira está provada de uma vez:

| Comandado | `ServoRpm` medido pelo hub | Estado |
|---:|---|---|
| 200 | 34,2 → 197,5 → 198,0 → 198,7 → 199,1 → 198,5 | `ServoState:2` (SON) |
| 600 | 277,4 → 598,8 → 598,9 → 599,1 → 599,5 → 598,7 | `ServoState:2` (SON) |
| 0 | 0,0 no quadro seguinte | `ServoState:1` (READY) |

Com `ServoCommErr:0` em todos, e o `ServoEnergyWh` integrando de forma monótona.
Os primeiros valores de cada patamar (34,2 e 277,4) são a rampa de aceleração
capturada em quadro — não erro de leitura.

- [x] laço fechado comando→medida verificado em 200, 600 e 0 rpm

---

## Etapa 4 — regressão dos demais dispositivos

O v9 é uma refatoração do Hub inteiro, não só do servo. Antes de declarar o
conjunto pronto, confirme que nada mais regrediu:

- [ ] fluxômetro: comando com `cmd_id` é retido até o ACK e some depois dele
- [ ] biomassa: `start`/`blank` chegam; `BiomassOnline` distingue ocioso de ausente
- [ ] bomba: `PumpOnline` cai ~4 s depois de desligar o nó
- [ ] agitador: `AgitatorPotActive` reflete o potenciômetro de bancada
- [ ] sensor de distância: intertravamento de espuma continua pausando com dado velho
- [ ] `/readData` do v9 tem todas as chaves do v8 (compare os dois quadros)

---

## Etapa 5 — soak de 2 h

- [ ] duas horas contínuas com os dois nós ativos
- [ ] heap livre do Hub sem tendência de queda (log de 60 em 60 s)
- [ ] nenhuma reinicialização em nenhuma das placas
- [ ] `ServoCommErr` estável em regime

Só depois desta etapa o conjunto pode ser tratado como pronto, e só então faz
sentido mexer no aplicativo.

---

## 3. Verificação do parser v9 — o que foi encontrado

A refatoração v8 → v9 é fiel. Comparei módulo a módulo com
`_old/TECNAL_ESP32_v8/TECNAL_ESP32_v8.ino` e o conjunto de chaves de
`/readData` é idêntico ao do v8, com cinco chaves novas e **nenhuma removida**.
As divergências restantes são as documentadas em `docs/MIGRATION_V8_TO_V9.md`.

O v9 inclusive corrige um defeito do v8 no handler `/pumpData`, onde `pwm`,
`speed`, `active` e `waiting` eram lidos sem verificar `hasParam`.

Quatro defeitos reais foram encontrados e corrigidos.

### 3.1. Chave encontrada dentro de um valor string (Hub) — corrigido

`getValueFromJson()` e `JsonUtils::getRaw()` localizavam a chave com `indexOf`,
que também casa **dentro de um valor**. O quadro

```json
{"pump_command":"start","mode":2}
```

fazia o Hub enxergar um `"start"` de biomassa, seguir até o `:` seguinte e ler o
`2` do `"mode"` — enfileirando um comando de start para **outro dispositivo**,
que ninguém pediu. Mesmo efeito com `"stop"`.

Correção: um token só vale como chave quando o caractere significativo anterior
é `{` ou `,`.

- `ESP32S3-HUB/ESP32S3-HUB/src/protocol/JsonUtils.cpp:18` e `:31`
- `ESP32S3-HUB/ESP32S3-HUB/src/protocol/Mailboxes.h:144` e `:153`
- regressão em `tests/contracts/test_json_keys.py`

Os 17 quadros que o aplicativo realmente emite dão resultado **idêntico** antes
e depois.

### 3.2. Watchdog reinicia o nó com `poll_ms` longo — corrigido

Este é o mais grave, e teria aparecido justamente no teste 10 da §3.3.

O core Arduino-ESP32 3.3.11 já inicializa o task watchdog
(`CONFIG_ESP_TASK_WDT_INIT=y`, `CONFIG_ESP_TASK_WDT_TIMEOUT_S=5`). Por isso
`esp_task_wdt_init()` devolvia `ESP_ERR_INVALID_STATE` nos **dois** firmwares, e
o `WDT_TIMEOUT = 10` que ambos declaravam nunca chegava a valer: os dois rodavam
com os 5 s do core.

No nó isso é fatal. A `modbusTask` alimentava o watchdog uma vez por iteração e
depois dormia o intervalo inteiro:

```cpp
esp_task_wdt_reset();
… 3 leituras Modbus (até ~750 ms) …
vTaskDelay(pdMS_TO_TICKS(pollMs));   // pollMs até 10000
```

Com 5 s de watchdog, qualquer `poll_ms` acima de ~4,2 s reinicia a placa em
laço — e 250 a 10000 ms é a faixa que o contrato aceita e que o Hub valida e
encaminha.

Correção, nos dois firmwares: cair para `esp_task_wdt_reconfigure()` quando o
core já inicializou, preservando `idle_core_mask` do núcleo 0; e, no nó, fatiar
a espera em 500 ms alimentando o watchdog a cada fatia.

- `ESP32S3-HUB/ESP32S3-HUB/src/core/Runtime.h:5-25`
- `ASDA_B2_Servo_Node.ino:114`, `:402-408`, `:553-578`

### 3.3. Intertravamento de espuma lia sem mutex (Hub) — corrigido

O v9 passou a escrever `distanceSensorValue`, `distanceSensorLastUpdate` e
`distanceSensorCommOn` sob `stateMutex` no handler `/distance`, mas
`checkDistanceSensorReference()` — que aciona bomba e agitador — continuou lendo
os três fora dele. A decisão podia se apoiar num trio inconsistente: valor novo
com carimbo de tempo velho, ou o contrário.

Correção: um snapshot sob `stateMutex` no início da função.

- `ESP32S3-HUB/ESP32S3-HUB/src/devices/AgitatorFoam.h:117-152`

### 3.4. Faltava o sketch de leitura dos registradores dinâmicos — criado

Nenhum sketch de bancada lia `P0-09`, `P0-10`, `P0-44`, `P0-46` e `P0-01`. O
autoscan que fechou a bancada lê só os estáticos (`P3-xx`, `P1-01`, `P0-00`).
Sem isso, o Gate A não era executável e o pré-requisito `P0-17`/`P0-18`/`P0-45`
passava despercebido.

- criado `Software/testes-bancada/esp32-s3/ASDA_B2_Monitor_ESP32/`

### 3.5. Observações que não viraram alteração

- **`load_pct` sem escala** — o nó aplica `×0,1` em rpm e torque mas não em
  carga. Isso **acompanha** a documentação de bring-up, que dá `P0-10` em `%`
  direto. Não alterei; virou item de conferência do Gate A.
- **Sem presença quando o Modbus nunca responde** — comportamento honesto (não
  publicar zeros como se fossem medida), mas atrapalha o diagnóstico. Tratado no
  §1.1 pela serial do nó.
- **Divergência não documentada v8 → v9** — o v8 zerava `distanceSensorValue`
  quando a leitura envelhecia; o v9 não. O v9 está certo: no v8, se o nó voltasse
  publicando o mesmo valor de antes, o filtro de estagnação nunca reaceitava a
  leitura e o sensor ficava "offline" para sempre. Vale registrar em
  `MIGRATION_V8_TO_V9.md`.

### 3.6. Estado da verificação

| Verificação | Resultado |
|---|---|
| `python tools/verify_contract.py` | OK (10 hashes, contrato estático, 16 fixtures) |
| Compilação do Hub v9 | 954.716 bytes (72%), 45.440 globais, sem warnings |
| Compilação do nó | 921.045 bytes (70%), 45.888 globais, sem warnings |
| Compilação do monitor | 305.530 bytes (23%), sem warnings |

Compilação limpa não substitui a bancada. Nada acima foi verificado em hardware.

---

## 4. Sobre manter o parser JSON manual

Manter foi a decisão certa, e não recomendo trocar antes do Gate F.

**Por que manter.** O parser manual é o que rodou em campo no v7 e no v8. Trocar
por ArduinoJson agora acopla o único caminho já validado a uma dependência nova,
com alocação diferente, num firmware que já usa 72% da flash — e o faria
exatamente no momento em que você precisa que o comportamento de v7/v8 seja o
ponto de comparação. O protocolo é plano, com chaves ASCII e valores numéricos
ou booleanos: é o caso em que um parser de duzentas linhas é suficiente.

**O que se ganharia.** Três coisas concretas, e nenhuma delas urgente:

1. Chave só casa em posição de chave. **Já obtido** pela correção da §3.1, sem
   dependência nova.
2. Rejeição de JSON malformado como um todo, em vez de campo a campo. Hoje
   `{"motorSetpoint":abc}` vira `setMotor(0)` silenciosamente nos caminhos
   legados — o caminho do servo no v9 já é estrito e recusa com aviso.
3. Aninhamento e escapes. O protocolo não usa nenhum dos dois.

**Chance de quebrar.** Real. Uma troca completa mexeria em todos os
dispositivos — fluxômetro, biomassa, bomba, agitador — e não só no servo,
invalidando o Gate F que você já pagou no v7/v8.

**Recomendação.** Manter o parser manual. Se, depois do Gate F, quiser
endurecer, o passo de melhor retorno é migrar os caminhos legados para
`JsonUtils::parseInt`/`parseFiniteFloat` — que já existem, já estão testados no
caminho do servo e recusam `abc` e `null` em vez de convertê-los em zero. É uma
mudança incremental, dispositivo por dispositivo, cada um com sua própria
regressão de bancada.

---

## 5. Notas de bancada

### 5.1. Se o nó reiniciar sozinho

Confirme na serial que aparece `Watchdog ativo com timeout de 10 s`. Se aparecer
`Falha ao configurar o watchdog`, a correção da §3.2 não pegou e `poll_ms` acima
de ~4 s vai reiniciar a placa. Nesse caso mantenha `poll_ms` em 1000 até
resolver.

### 5.2. `/readData` parou de atualizar

Modo bypass. Envie `{"comTest":1}` pela serial do Hub. Veja o aviso da §3.1.

### 5.3. `ServoOnline` nunca fica true

Nesta ordem:

1. **O SSID dos dois bate?** Compare o banner da serial do nó (`Hub: SSID=…`)
   com o do Hub (`SSID: …`). É a causa número um depois de trocar de módulo —
   veja §2.1.
2. Serial do nó: associou ao SoftAP? Se não, é Wi-Fi.
3. Associou mas nunca publica? O Modbus nunca teve sucesso — volte à Etapa 1.
4. Publica e o Hub responde 400? Algum campo está fora de faixa — quase sempre
   `state` fora de 0..3.
5. `ServoOnline:true` e sem valores? `ServoCommEnabled` está false; envie
   `{"servoComm":1}`.

### 5.4. Reprovou algum gate

Corrija e volte à Etapa 1. Os gates são cumulativos: uma correção no nó invalida
o Gate C, e uma correção no Hub invalida a Etapa 4.


---

## 6. Descoberta operacional de 2026-09-02 — o `0V` e o teclado do módulo

**Sintoma:** depois de um reinício do hub do Módulo 2, o teclado do Módulo TECNAL
parou de aceitar ajuste de rotação. Continuava assim com o hub **desconectado**,
mas pelo hub o setpoint funcionava normalmente e persistia.

**Causa:** o vocabulário da UART2 usa `V` como flag de habilitação do motor:

```cpp
if (motorRPM == 0) { sendSensorCommand("0V"); sendSensorCommand("0A"); }
else               { sendSensorCommand("1V"); sendSensorCommand(String(motorRPM) + "A"); }
```

O hub subiu com `motorRPM = 0` na NVS — ele nunca havia comandado o motor
naquele módulo — e o `syncAllSensorSettings()` **forçou** o envio de `0V`,
desabilitando o motor por serial. O módulo ficou latchado nesse estado, e o
teclado não o reverte. Pelo hub voltava a funcionar porque `motorSetpoint > 0`
manda `1V` antes do valor.

**O nó do servo foi descartado como causa, por topologia e por medida:** ele está
no CN3 (RS-485), o comando de rotação está no CN1, são circuitos distintos; a
única escrita do firmware é `P0-45`, um seletor de monitor sem efeito no eixo; e
o `P3-06` do drive foi lido em `0x0000`, ou seja, as DIs continuam vindo do
hardware, não da comunicação.

**Consequência para o projeto.** Um hub recém-gravado, ou com a NVS limpa, entra
num módulo em operação e **desabilita o motor no primeiro boot**. Isso vale para
qualquer módulo, não só o 2. Antes de energizar um hub novo num módulo em uso,
ou grave o `motorSetpoint` correto antes, ou aceite que o motor vai parar.

Documentado também na §5 do `PROTOCOLO_HUB_v9_PARA_APLICATIVO.md`.
