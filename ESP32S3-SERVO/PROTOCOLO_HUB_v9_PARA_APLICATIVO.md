# Protocolo do Hub v9 — o que o aplicativo precisa saber

> **Atualização 2026-09-04:** `motorSetpoint` permanece idêntico para o
> aplicativo, mas no Hub 10 ele é aplicado pelo ESP32S3-driver diretamente em
> P1-09 via Modbus. O contrato interno Hub↔driver e os campos `ServoMotor*` estão
> em [`PLANO_MIGRACAO_RPM_MODBUS.md`](PLANO_MIGRACAO_RPM_MODBUS.md). As regras
> v9 abaixo permanecem válidas para as demais chaves.

**Data:** 2026-09-02
**Escopo:** comunicação entre o aplicativo OpenTEC-Hub (PC) e o ESP32-S3 Hub v9.
**Complementa** `ESP32S3-HUB/docs/WIRE_CONTRACT_V9.md`, que é o contrato
formal dos endpoints. Aqui está a *semântica* — o que cada campo significa, o que
o hub faz sozinho, e as armadilhas que apareceram na bancada em 2026-09-02.

---

## 1. Topologia e quem inicia

O hub levanta um SoftAP e **nunca abre conexão**. Todo tráfego é iniciado por
outra parte: os nós empurram telemetria e puxam comandos; o PC lê e comanda.

```text
       Modulo TECNAL                    nos (fluxometro, biomassa, bomba,
            ^                            agitador, distancia, servo)
            | UART2 9600 8N1                        |
            | GPIO16=RX  GPIO17=TX                  | Wi-Fi, sempre iniciado pelo no
            |                                       v
      +-----------------------------------------------------+
      |  ESP32-S3 HUB v9   SoftAP 192.168.4.1   canal 6      |
      |  SSID ModuloTECNAL_1 ou _2, max 8 estacoes           |
      +-----------------------------------------------------+
                            ^
                            | HTTP, iniciado pelo PC
                            v
                   Aplicativo OpenTEC-Hub
```

O PC precisa estar associado ao SoftAP. O IP `192.168.4.1` é fixo por
`softAPConfig()` nos dois módulos — nenhuma URL muda entre eles.

---

## 2. Leitura: `GET /readData`

Devolve um único objeto JSON com o estado agregado. O hub também o emite na
serial USB, no mesmo formato, a cada `dataDelay` ms (padrão 2000).

### Identidade — verifique sempre

| Campo | Valor | Uso |
|---|---|---|
| `HubFirmwareVersion` | `"9.0.0-dev"` | diagnóstico |
| `HubProtocolVersion` | `9` | **negociação de versão** |

Use `HubProtocolVersion` para decidir o que esperar. Um hub v7/v8 não publica os
campos `Servo*`, e o aplicativo não deve tratar a ausência deles como falha.

### Presença e roteamento — são coisas diferentes

Para cada dispositivo há **dois** conceitos ortogonais, e confundi-los é o erro
mais provável do aplicativo:

- **`<Dev>Online`** — o nó está presente, ou seja, empurrou algo recentemente.
  Independe de roteamento.
- **`<Dev>CommEnabled`** — o roteamento daquele dispositivo está ligado no hub.
  É configuração, persistida em NVS.

As quatro combinações são todas legítimas e devem aparecer diferentes na
interface:

| `Online` | `CommEnabled` | Significado |
|---|---|---|
| `true` | `true` | operação normal |
| `true` | `false` | **nó presente, roteamento desligado** — não é falha |
| `false` | `true` | nó ausente com roteamento ligado — **isto sim é falha** |
| `false` | `false` | módulo não tem esse dispositivo — não alarmar |

O `false`/`false` é o estado do servo no ModuloTECNAL_1, deliberadamente
configurado com `{"servoComm":0}`. Se o aplicativo alarmar nesse caso, vai
gerar um evento que nunca se resolve.

### Janelas de presença, por dispositivo

Cada `Online` tem sua própria janela, medida desde o último push válido:

| Dispositivo | Janela | Constante |
|---|---:|---|
| Fluxômetro | 6000 ms | `FLOWMETER_TIMEOUT` |
| Biomassa | 10000 ms | `BIOMASS_TIMEOUT` |
| Bomba | 4000 ms | `PUMP_TIMEOUT` |
| Agitador | 3000 ms | `AGITATOR_TIMEOUT` |
| Distância (exibição) | 3000 ms | `DISTANCE_PRESENCE_TIMEOUT` |
| Distância (intertravamento de espuma) | 1200 ms | `DISTANCE_TIMEOUT` |
| **Servo** | **6000 ms** | `kPresenceTimeoutMs` |

O sensor de distância tem **duas** janelas de propósito diferente: a curta
protege o intertravamento de espuma, a longa é o que o operador vê. Não as
misture.

### Campo ausente não é campo zero

Os dez valores do servo — `ServoRpm`, `ServoTorquePct`, `ServoTorqueNm`,
`ServoLoadPct`, `ServoPowerW`, `ServoEnergyWh`, `ServoState`, `ServoAlarm`,
`ServoCommOk`, `ServoCommErr` — **só aparecem quando há amostra publicável**,
isto é, presença fresca **e** roteamento ligado. Nos demais casos as chaves
simplesmente não existem no JSON.

O aplicativo deve renderizar traço, não zero. `ServoRpm: 0.0` é uma medida
legítima de motor parado; a **ausência** da chave significa "sem dado".

Quatro campos do servo são publicados **sempre**, com nó presente ou não:
`ServoOnline`, `ServoCommEnabled`, `ServoCommandPending`, `ServoCommandQueueDepth`.

### `ServoState`

| Valor | Estado |
|---|---|
| 0 | OFF — drive não pronto |
| 1 | READY — pronto, servo não energizado |
| 2 | SON — servo energizado |
| 3 | ALARM |

`ServoAlarm` traz o código bruto de `P0-01`. **Cuidado com a codificação:** o
valor `0x0011` corresponde ao `AL011` mostrado no painel — os dígitos hexadecimais
espelham o número exibido, não são decimal. Verificado na bancada com um
`AL011` real (encoder desconectado).

### Potência e energia são **mecânicas estimadas**

`ServoPowerW` e `ServoEnergyWh` derivam de `torque % × T_nominal × ω`. Não é
potência elétrica, não inclui perdas do drive nem do motor, e depende de
`MOTOR_RATED_TORQUE_NM` estar certo para o motor instalado. **Rotule na
interface**, senão viram número de consumo elétrico na cabeça do operador.

`ServoEnergyWh` é integrado **no nó**, não no hub. Ele zera quando o nó reinicia
e quando recebe `resetServoEnergy`. A integração rejeita lacunas: se o nó perde
amostras, ele não integra por cima do buraco. Consequência para o aplicativo:
**a energia pode voltar para trás**, e o gráfico precisa aguentar isso.

---

## 3. Comando: `POST /command`

Corpo é um objeto JSON. Várias chaves podem ir no mesmo frame.

```bash
curl -s -X POST http://192.168.4.1/command -d "{\"motorSetpoint\":500}"
```

| Resposta | Significado |
|---|---|
| `200` | frame aceito e enfileirado |
| `400` | JSON inválido |
| `413` | payload acima de 2048 bytes |
| `503 Command queue full` | fila de oito frames cheia; **o frame não foi aplicado** |

O `503` não é erro de rede — é contrapressão. O aplicativo deve tentar de novo,
não descartar em silêncio.

### Comandos principais

| Chave | Faixa | Efeito |
|---|---|---|
| `motorSetpoint` | 0..1000 | rpm do agitador, via Módulo TECNAL → CN1 |
| `tempSetpoint` | 0..100 | referência de temperatura |
| `pHSetpoint`, `pHError`, `pHOperation`, `pHMix`, `pHIntensity` | — | controle de pH (aplicados em conjunto) |
| `nutriOperation`, `nutriMix`, `nutriOpCycle`, `nutriMixCycle`, `nutriIntensity` | — | nutriente |
| `antifoamOperation`, `antifoamMix`, `antifoamIntensity` | — | antiespumante |
| `agitatorAuto`, `agitatorReEnablePot`, `agitatorPercent`, `agitatorDir`, `agitatorOn` | — | agitador de espuma |
| `foamStartDelay_s`, `foamPulse_s`, `foamInterval_s` | — | tempos do intertravamento de espuma |
| `distanceSensorReference`, `pressureReference` | — | referências |
| `dataDelay` | ≥ 100 ms | período do quadro agregado |
| `oxygenMonitor` | 0/1 | liga a leitura de O₂ |
| `<dev>Comm` | 0/1 | roteamento: `flowmeterComm`, `biomassComm`, `distanceSensorComm`, `pumpComm`, `servoComm` |
| `resetVariables` | 1 | zera variáveis e **limpa as filas de comando** |
| `restart` | 1 | reinicia o hub — leia a §5 antes de usar |
| `comTest` | 1 | frame JSON inócuo, útil para sair do modo bypass |

### Comandos do servo

| Chave | Faixa | Notas |
|---|---|---|
| `resetServoEnergy` | 1 | zera `ServoEnergyWh` no nó; **nunca é coalescido** |
| `servoPollMs` | 250..10000 | período de amostragem Modbus; fora da faixa é **recusado**, nada é enfileirado |

---

## 4. O enlace hub→nó do servo não tem ACK

Este é o ponto que mais afeta a lógica do aplicativo.

- O nó **puxa** comandos a cada 2 s em `GET /servoCommand`, com **consumo na
  leitura**: um evento por requisição.
- A fila no hub é **FIFO fixa de 8**. O nono comando é recusado
  (`[ESP32_ERRO] Comando Servo rejeitado: fila cheia`) sem sobrescrever nada.
- Comandos **só de `poll_ms`** ainda não entregues são **coalescidos** — o valor
  mais novo substitui o anterior. `resetServoEnergy` nunca é coalescido.
- **Não existe `cmd_id` nem ACK neste enlace.**

Como o aplicativo confirma que um comando pegou:

| Comando | Confirmação observável |
|---|---|
| `resetServoEnergy` | `ServoEnergyWh` cai a ~0 |
| `servoPollMs` | a taxa de crescimento de `ServoCommOk` muda |

Acompanhe `ServoCommandPending` e `ServoCommandQueueDepth`: eles mostram o
comando entrar na fila e ser consumido. Com a fila cheia, esvaziar leva até
**16 s** (8 comandos × 2 s de pull), então não conclua falha antes disso.

**O fluxômetro é diferente:** ele tem entrega confiável com `FlowCommandId`,
`FlowCommandAck`, `FlowCommandDeliveries`, `FlowCommandAgeMs` e
`FlowCommandSource`. Não generalize o modelo do servo para ele, nem o contrário.

---

## 5. O hub é autoritativo no boot — e isso pode parar o motor

**Verificado na bancada em 2026-09-02.**

Ao subir, o hub executa `syncAllSensorSettings()`, que **força** o reenvio de
toda a configuração guardada na NVS ao Módulo TECNAL — incluindo `motorRPM`,
temperatura, pH, nutriente e antiespumante. O reenvio é forçado (`flagDirty`
levantado à mão), então acontece mesmo que nada tenha mudado.

Isso é deliberado e correto para queda de energia: o hub volta comandando o que
comandava antes. Mas tem duas consequências que o aplicativo precisa tratar:

1. **O hub sobrescreve setpoints ajustados fora dele.** Se alguém ajustar a
   rotação pelo painel do Módulo TECNAL, o próximo boot do hub desfaz. Na
   bancada, um reinício do hub derrubou a rotação de ~93 rpm para 0, porque a
   NVS daquele hub tinha `motorRPM = 0` — ele nunca havia comandado o motor.
2. **`{"restart":1}` não é inócuo em processo em andamento.** O aplicativo deve
   confirmar com o operador antes de enviar, e nunca usar reinício como
   estratégia de recuperação automática.
3. **Com `motorSetpoint = 0`, o hub desabilita o motor no módulo — e o teclado
   do Módulo TECNAL não reverte.** O protocolo da UART2 usa `V` como flag de
   habilitação: `motorRPM == 0` manda `0V` + `0A`; qualquer valor maior manda
   `1V` + `<rpm>A`. Um hub recém-gravado, com a NVS limpa, entra num módulo em
   operação e **desabilita o motor no primeiro boot**. Foi observado na bancada
   em 2026-09-02: o teclado do módulo parou de aceitar rotação e continuou assim
   mesmo com o hub desconectado, porque o módulo fica latchado no estado em que
   foi deixado. A saída é mandar `{"motorSetpoint":N}` com `N > 0`, que reenvia
   o `1V`.

   Para o aplicativo: **`motorSetpoint = 0` não é "parar o motor", é "desabilitar
   o motor"**, e tem efeito colateral sobre o controle local. Se a interface
   oferecer um botão de parada, deixe isso explícito, e considere avisar que o
   ajuste pelo teclado do módulo ficará indisponível até um novo setpoint
   diferente de zero.

A gravação na NVS é **debounced**: só ocorre após 5 s sem novos comandos, e só
se o hash de estado mudou. Se o aplicativo enviar comandos em rajada contínua, a
persistência é adiada. Depois de uma mudança que precisa sobreviver a queda de
energia, deixe o hub quieto por alguns segundos.

---

## 6. Armadilhas verificadas na bancada

**Modo bypass (só serial).** Qualquer linha na serial USB do hub que **não**
comece com `{` e termine com `}` coloca o hub em modo bypass, e nesse modo ele
**para de ler sensores e de atualizar o `/readData`**. Para sair, envie um JSON
válido, por exemplo `{"comTest":1}`. Não afeta o caminho HTTP, mas afeta
qualquer ferramenta de diagnóstico que fale pela serial.

**`resetVariables` também mexe no `dataDelay`.** Depois dele os quadros passaram
de 2 s para 1 s na bancada. Se o aplicativo depende do período, releia-o.

**`servoPollMs` é atraso, não período.** Cada amostra custa ~250 ms de barramento
(4 transações Modbus a 9600 8N2). Com `poll_ms = 250` o ciclo real ficou em
~500 ms. A taxa dobra, não quadruplica. Não calcule período esperado como
`1000/poll_ms`.

**`HubStations` não é confiável em tempo real.** É `WiFi.softAPgetStationNum()`,
e uma estação que some sem se desassociar permanece na tabela até o tempo de
inatividade do AP — 5 minutos por padrão no ESP-IDF. Foi observado em `1` com o
nó desconectado e em `2` logo após uma reassociação. **Não use este campo para
decidir presença de nada**; use os `<Dev>Online`.

**Um `ServoCommErr` isolado não é ruído.** Na bancada apareceu `err = 1` em 256
leituras, na primeira transação após o boot. Ruído real faz o contador crescer
continuamente. Alarme deve olhar a **taxa**, não o valor absoluto.

---

## 7. Recomendações para o aplicativo

- **Negocie por `HubProtocolVersion`**, não por presença de campo.
- **Renderize as quatro combinações** de `Online` × `CommEnabled` com textos
  distintos. Só `Online:false` + `CommEnabled:true` é falha.
- **Traço, não zero**, quando a chave estiver ausente.
- **Rotule potência e energia como mecânicas estimadas.**
- **Aguente energia que anda para trás** nos gráficos, e mostre lacuna quando o
  nó fica offline em vez de interpolar.
- **Nunca reinicie o hub automaticamente.**
- **Trate `503` como contrapressão**, com retentativa.
- **Não conclua falha de comando do servo antes de 16 s.**
- **Separe setpoint de medida na interface.** `motorSetpoint` é o que foi pedido;
  `ServoRpm` é o que o eixo está fazendo. Na bancada, um comando de 100 rpm
  produziu 96,6 rpm medidos — erro de ganho e offset do comando analógico do
  CN1, não da telemetria. Mostrar os dois como se fossem a mesma grandeza vai
  gerar chamado de suporte.
