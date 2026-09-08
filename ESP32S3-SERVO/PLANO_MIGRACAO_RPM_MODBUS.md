# Migração do comando de rotação para Modbus — ASDA-B2

**Data:** 2026-09-04  
**Estado:** implementação de software concluída; validação física pendente  
**Alvo:** Módulo TECNAL 2 (`ModuloTECNAL_2`)

## 1. Objetivo e diagnóstico

Manter `motorSetpoint` como referência de 0 a 1000 rpm no aplicativo, mas retirar
o comando de velocidade da cadeia proprietária:

```text
antes: aplicativo -> Hub -> UART "<N>A" -> placa intermediária -> CN1 analógico
depois: aplicativo -> Hub -> HTTP revisionado -> ESP32S3-driver -> Modbus -> ASDA-B2
```

No ensaio `Ensaio_2026-09-04_1204-servo-power.tsv`, o patamar solicitado de
1000 rpm ficou em aproximadamente 971,6 rpm no intervalo estável de 145,50 a
151,40 s. O PI incorporado ao Hub não removeu o teto porque sua saída continuava
atravessando a mesma placa intermediária. A migração elimina essa conversão
analógica e escreve a referência interna do próprio drive.

## 2. Decisões congeladas

1. O aplicativo continua enviando `{"motorSetpoint":N}`. Não há mudança na API
   aplicativo→Hub nem na faixa 0..1000 rpm.
2. O Hub não envia mais `1V`, `0V` ou `<N>A` para controlar o motor e não executa
   PI externo de rotação.
3. Velocidade é estado desejado **latest-wins**, nunca FIFO. Reset de energia e
   período de amostragem continuam na fila de eventos.
4. O Hub gera `motor_cmd_id`, repete o comando até ACK e continua repetindo-o
   como heartbeat. O lease é de 3000 ms; o driver consulta a cada 500 ms.
5. O ESP32S3-driver é o único mestre Modbus e serializa leitura e escrita na
   mesma tarefa. P1-09 é escrito atomicamente com função `10H`.
6. Um reboot nunca retoma um setpoint persistido. Hub e driver começam impondo
   parada; novo movimento exige novo comando do aplicativo.
7. A parada de emergência física permanece independente. Modbus não é função
   de segurança certificada.

## 3. Contrato Hub ↔ ESP32S3-driver

### `GET /servoCommand`, a cada 500 ms

O Hub sempre devolve os quatro campos de movimento:

```json
{
  "motor_cmd_id": 305419897,
  "motor_rpm": 1000,
  "motor_enable": 1,
  "motor_lease_ms": 3000
}
```

- `motor_cmd_id`: inteiro sem sinal, diferente de zero; muda a cada novo estado;
- `motor_rpm`: 0..1000;
- `motor_enable`: `1` somente quando `motor_rpm > 0`;
- `motor_lease_ms`: 1000..10000; nesta versão, 3000 ms.

Uma resposta válida repetida renova o lease sem reescrever o drive. Uma resposta
incompleta, inválida ou uma falha HTTP não renova o lease.

### `GET /servoData`, a cada 1 s

Além da telemetria já existente, o driver envia:

```text
control_capable       perfil do drive foi confirmado
motor_ack             último cmd_id efetivamente aplicado
motor_applied_rpm     referência confirmada por readback
motor_control_active  SON comandado pelo driver
motor_control_fault   0=ok, 1=perfil, 2=aplicação, 3=lease,
                      4=comando inválido, 5=drive indisponível
```

O Hub expõe os equivalentes `ServoControlCapable` e `ServoMotor*` em
`GET /readData`. `ServoCommandPending` agora cobre a fila de eventos **ou** um
comando de motor sem ACK; `ServoCommandQueueDepth` continua contando somente a
fila de eventos.

## 4. Mapa Modbus e guardas

| Parâmetro | Endereço | Uso | Política |
|---|---:|---|---|
| P1-01 | `0x0102` | modo de controle | deve ser `0x0002`; somente leitura |
| P1-09 | `0x0112–0x0113` | velocidade interna 1 | `10H`, signed 32-bit, 0,1 rpm, word baixa primeiro |
| P2-10..P2-17, P2-36 | `0x0214`+2/DI, `0x0248` | função de DI1..DI9 | varridos para localizar SON, SPD0 e SPD1; **somente leitura** |
| P2-30 | `0x023C` | política de escrita | `5`, escritas cíclicas somente em RAM |
| P3-06 | `0x030C` | fonte das DIs | máscara derivada do mapa descoberto |
| P4-07 | `0x040E` | estado das DIs por software | estados derivados do mapa descoberto |

O mapa de DIs **não é constante do projeto**. P3-06 e P4-07 endereçam as DIs por
posição (bit 0 = DI1), mas qual função mora em qual DI é configuração do drive, e
cada instalação acomoda SPD0 no pino que sobra do CN1. O driver varre as nove DIs,
identifica SON (`0x01`), SPD0 (`0x14`) e SPD1 (`0x15`) pelo byte baixo de `P2-1x`,
aceita apenas contato tipo A (byte alto `0x01`) e monta máscara e estados a partir
do que encontrou. Exigir DI1/DI3/DI4 fixas obrigaria a remapear pinos que a placa
original já usa no CN1.

O drive do Módulo 2 tem `SON=DI1, SPD0=DI2, SPD1=DI3` — daí `P3-06=0x0007`,
`P4-07` com `0x0002` parado e `0x0003` rodando. A configuração documentada
anteriormente (DI3=SPD0, DI4=SPD1) descrevia apenas uma instalação possível.

SPD1 só entra na máscara quando existe: uma DI sem função já vale zero, mas uma DI
com SPD1 no CN1 selecionaria P1-10/P1-11 sem o driver saber. Antes de trocar
P3-06, o driver pré-carrega P4-07 com SON=0, SPD0=1 e SPD1=0.

`P4-07` tem leitura e escrita distintas: a leitura mostra o estado das DIs **após a
combinação** com P3-06; a escrita define apenas as SDI de software. Por isso a
confirmação compara somente os bits que P3-06 delega ao software — comparar a
palavra inteira reprova escritas corretas.

Exemplo: 1000 rpm = 10000 décimos de rpm = `0x00002710`; o quadro `10H`
escreve `[0x2710, 0x0000]` a partir de P1-09. Toda escrita é seguida de leitura
de confirmação. Se P1-01 não for `0x0002`, ou se faltar SON ou SPD0 em contato
tipo A, o driver recusa a tomada de controle, lista as nove DIs no log e reporta
falha 1; ele não corrige silenciosamente parâmetros persistentes.

Sem SPD0 atribuído o par SPD1/SPD0 fica em `00`, que em modo S seleciona o
comando analógico do CN1 e nunca P1-09 (manual, pág. 6-14). Nesse caso o motor
não gira por mais correta que seja a escrita, e a única saída é comissionar o
drive — decisão de instalação, nunca automática.

P2-30 e P3-06 são voláteis e são reaplicados após religamento do drive. P0-45=54
continua sendo mantido e confirmado para a leitura correta de torque.

## 5. Estados e falhas

```text
driver novo + Hub antigo -> passivo; CN1 continua no comando
Hub v10 envia parada      -> valida perfil -> assume DIs -> P1-09=0 -> SON off -> ACK
comando N>0 válido        -> P1-09=N -> readback -> SON on -> ACK
comando 0                 -> P1-09=0 -> SON off -> ACK
sem heartbeat por 3 s     -> P1-09=0 -> SON off -> falha 3
reboot do driver          -> se P3-06 indicar sessão direta anterior, SON off antes do Wi-Fi
falha de escrita/readback -> tenta parada, não confirma o cmd_id, falha 2
```

Há uma segunda camada necessária para o caso em que o próprio ESP32S3-driver
trava ou perde fisicamente o RS-485: configurar e ensaiar o timeout de
comunicação do ASDA-B2 (`P3-10`, quando disponível na revisão do drive) e o modo
de parada por falha (`P3-03 = 1`, desaceleração). Esses valores não são gravados
automaticamente pelo firmware porque são parâmetros persistentes de segurança e
devem ser aprovados na bancada. Sem esse gate, não liberar para operação sem
supervisão.

## 6. Ordem obrigatória de implantação

### Preparação

1. Deixar o eixo sem carga, área livre e E-stop físico acessível.
2. Registrar pelo painel/Modbus: P1-01, P3-03, P3-10 e a função de todas as DIs
   (P2-10..P2-17 e P2-36) — o driver imprime esse mapa no log ao validar o
   perfil, e é ele que define a máscara P3-06 desta instalação.
3. Confirmar os dois seletores de build em `2`:
   `MODULO_TECNAL_ALVO` no driver e `MODULO_TECNAL` no Hub.
4. Manter `motorSetpoint=0` e desligar o servo antes de gravar.

### Etapa A — gravar primeiro o ESP32S3-driver

Gravar `Software/firmware-producao/ASDA_B2_Servo_Node/ASDA_B2_Servo_Node.ino`.
Com o Hub antigo, o driver novo deve registrar `P3-06 ainda fisico; driver
permanece passivo` e a placa antiga/CN1 deve continuar funcionando. Confirmar
que a telemetria continua chegando. Não avançar se houver escrita de controle
ou mudança de estado nesta etapa.

### Etapa B — gravar o Hub

Gravar `ESP32S3-HUB/ESP32S3-HUB/ESP32S3-HUB.ino`. O banner deve mostrar firmware
`10.0.0-dev` e protocolo 10. No primeiro contato, o Hub envia uma parada
revisionada; o driver valida o perfil, assume P3-06 e responde ACK.

Antes de qualquer movimento, exigir em `/readData`:

```text
ServoOnline=true
ServoControlCapable=true
ServoMotorRequestedRpm=0
ServoMotorAppliedRpm=0
ServoMotorEnabled=false
ServoMotorControlActive=false
ServoMotorCommandId == ServoMotorCommandAck
ServoMotorControlFault=0
```

### Etapa C — ensaio crescente

1. Comandar 100 rpm; confirmar P1-09=1000, ACK e sentido correto.
2. Repetir 250, 500, 750, 950 e 1000 rpm, aguardando regime em cada patamar.
3. Em 1000 rpm, confirmar readback `[0x2710, 0x0000]` e comparar `ServoRpm` com
   o painel. O critério do problema original é atingir 1000 rpm dentro da
   tolerância do drive/medição, sem correção PI do Hub.
4. Comandar zero e confirmar P1-09=0, SON removido e eixo parado.

### Etapa D — testes de perda

Com baixa rotação e possibilidade de parada imediata:

1. interromper Wi-Fi do driver por mais de 3 s: deve ocorrer falha 3 e SON off;
2. reiniciar o Hub: não pode haver retomada automática;
3. reiniciar o driver durante uma sessão direta: ele deve remover SON antes do Wi-Fi;
4. religar o drive: P2-30/P3-06 devem ser reaplicados somente com heartbeat válido;
5. ensaiar o timeout interno P3-10 removendo/travando o mestre RS-485;
6. executar soak mínimo de duas horas, observando `commErr`, alarmes e rail de 5 V.

## 7. Rollback

1. Comandar zero e confirmar eixo parado.
2. Desenergizar o drive.
3. Regravar no Hub a versão anterior e, se necessário, o driver somente-leitura.
4. Religando o drive, confirmar `P3-06=0x0000` antes de voltar ao CN1.
5. Nunca deixar a placa intermediária e o comando direto disputando a seleção de
   velocidade.

## 8. Evidência de software e gate final

- Hub v10: compilação `esp32:esp32:esp32s3` concluída.
- ESP32S3-driver 2.0: compilação `esp32:esp32:esp32s3` concluída.
- Contratos automatizados cobrem latest-wins, ACK, heartbeat, parada ao desligar
  roteamento, word order de P1-09 e presença dos guardas de segurança.

Isso demonstra consistência de software, não comportamento físico. O campo só
pode ser liberado depois das etapas C e D assinadas no registro de bancada.

## 9. Referências técnicas

- Delta, *ASDA-B2 Series User Manual*, revisão 2023:
  <https://filecenter.deltaww.com/Products/download/06/060201/Manual/DELTA_IA-ASD_ASDA-B2_UM_EN_20230421.pdf>
- Delta Industrial Automation EMEA, esclarecimento de que o timeout RS-485 do
  ASDA-B2 usa P3-10, enquanto P3-04 corresponde ao RS-232:
  <https://delta-ia-tips.com/2023/10/13/how-can-i-set-modbus-communication-time-out-value-for-the-rs-485-port-of-the-asda-b2-servo-drive-even-if-i-set-it-in-p3-04-i-see-the-time-out-alarm-al020/>
