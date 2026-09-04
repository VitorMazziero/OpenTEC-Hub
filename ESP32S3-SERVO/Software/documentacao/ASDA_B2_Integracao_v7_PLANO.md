# Plano de integração — telemetria do ASDA-B2 no TECNAL Hub v7

> **Documento histórico.** O plano corrente, incluindo as alterações do
> aplicativo e a estrutura reorganizada, está em
> `../../PLANO_INTEGRACAO_POTENCIA_OPENTECHUB.md`. As instruções antigas para
> HW-519 ou 38400 baud abaixo não representam a montagem vigente.

Estado: a comunicação Modbus básica foi comprovada em 2026-09-01 com o
ESP32-S3 e um HW-097 novo, a 9600 8N2, endereço 1. Ainda faltam a validação dos
registradores dinâmicos, o enlace Wi-Fi/HTTP e a validação de campo.

> **Atualização 2026-09-01.** A Fase 0 avançou: o dongle USB-RS485 (COM12)
> conversou com o drive na primeira tentativa — 9600 8N2, endereço 1 — e leu
> todos os parâmetros (`../testes-bancada/pc/PC_Modbus_Scan/asda_scan.py`). **O drive, a
> fiação do CN3 e o protocolo estão provados.** Falta apenas repetir isso a
> partir do microcontrolador. O módulo de transceptor passou a ser o **HW-097**
> (MAX485, direção manual DE/RE) no lugar do HW-519 — pinout em
> `../testes-bancada/arduino-uno/ASDA_B2_Scan_HWUART/PINOUT_HW097_RS485.md`.

Artefatos produzidos:

```text
../firmware-producao/ASDA_B2_Servo_Node/ASDA_B2_Servo_Node.ino   candidato do nó
../firmware-producao/ASDA_B2_Servo_Node/README.md                protocolo e validação
.../\_ESP32S3_firmware/TECNAL_ESP32_v8/              hub com a integração
```

O v8 é cópia do v7 mais 135 linhas aditivas: não altera a máquina de ack do
flowmeter, nem o `sensorSerial`, nem os mutexes existentes.

Alvo do hub:
`D:\OneDrive\Doutorado_CNPq\_Automacao_Controle\_devices\TECNAL_control\_Wifi Hub\Software\_ESP32S3_firmware\TECNAL_ESP32_v7`

---

# 1. Arquitetura final

```text
 DELTA ASDA-B2
   |  CN1 --> comando de velocidade (módulo existente, INALTERADO)
   |  CN3 --> RS-485 / Modbus RTU (somente leitura)
   v
 HW-519 TTL<->RS485
   v
 ESP32-S3 "parser"          alimentado pelo 5 V da placa controladora
   |                        UART1 GPIO17/18 = RS-485
   |                        WiFi STA
   |  HTTP sobre o SoftAP do hub
   v
 ESP32-S3 "v7"  (SoftAP ModuloTECNAL_1, canal 6)
   |
   |  GET /readData  ->  JSON agregado
   v
 PC / usuário
```

O parser **nunca** comanda o motor. Só função Modbus `03H`.

---

# 2. Decisão de transporte: HTTP, não ESP-NOW

O v7 **já tem um padrão de casa** para nós periféricos, usado por quatro
dispositivos (`flowmeter`, `biomass`, `agitator`, `pump`):

```text
periférico  --> GET /xxxData?p1=..&p2=..     empurra telemetria
periférico  <-- GET /xxxCommand              puxa comando pendente (JSON)
v7          --> GET /readData                agrega tudo para o PC
```

O nó do servo é mais um caso desse mesmo molde. Motivos para seguir o padrão:

| | |
|---|---|
| **Canal resolvido** | O v7 é `WIFI_MODE_AP` em **canal 6 fixo** (`WiFi.softAP(SSID, PASS, 6, 0, 8)`). O parser só associa. Sem roteador, sem negociação de canal. |
| **Cliente já entende** | O PC já consome `/readData`. Campos novos no mesmo JSON = **zero conceito novo** do lado do usuário. |
| **Superfície de falha** | ESP-NOW seria uma **segunda via de ingresso paralela** num firmware que já equilibra dois mutexes, watchdog de 10 s e servidor assíncrono. Callback de ESP-NOW roda no contexto da task de WiFi: seria preciso enfileirar para o loop de qualquer jeito. |
| **Sem ganho real** | A 1 Hz, telemetria não é limitada por latência nem por banda. As vantagens do ESP-NOW não compram nada aqui. |

**Decisão: HTTP.**

## 2.1. Quando o ESP-NOW passaria a valer (plano B)

O SoftAP está limitado a **8 estações**. Uso atual:

```text
PC + flowmeter + biomass + agitator + pump   = 5
+ parser do servo                            = 6      (folga: 2)
```

ESP-NOW não consome slot de associação. Se o orçamento de estações apertar no
futuro, é aí que ele vira a escolha certa — e só então.

---

# 3. Fases

## Fase 0 — pré-requisito

Modbus provado na bancada, conforme `ASDA_B2_ESP32S3_Modbus_Setup_v2_RS485.md`
seções 2.2 (T1–T5) e 10.0. **Sem isso, nada de integração:** não se depura
RS-485 e WiFi ao mesmo tempo.

Critério de saída: `ASDA_B2_RS485_First_Test` imprimindo a tabela com rpm
coerente e contador de erros estável.

## Fase 1 — alimentação e aterramento definitivos

Parser alimentado pelo **5 V da placa controladora**.

### Consequência excelente: o problema de terra desaparece

Com a alimentação vindo da placa controladora, o GND do ESP32 passa a ser o
mesmo nó da placa — que é o mesmo nó do `CN3-1`. A referência de modo comum do
RS-485 vem **de graça, pelo próprio fio de alimentação**.

```text
SOME:  o fio de GND separado até a placa do CN1
SOME:  o resistor de 100 ohm em série
SOME:  o notebook, e com ele todo o vínculo com o PE
```

Toda a discussão de terra das seções 2.1.1 e 5.0 era **artefato da bancada**,
não da instalação final. Na configuração definitiva ela não se aplica.

### O que passa a importar

```text
Pico de transmissão do WiFi ~ 300 mA.
- Conferir folga da fonte de 5 V da placa controladora.
- Capacitor de bulk 470-1000 uF junto ao ESP32, mais 100 nF cerâmico.
- Medir o rail de 5 V COM O WIFI TRANSMITINDO.
  Rail afundando -> brownout -> ESP em loop de reboot.
```

Manter `R0` aberto e a malha do cabo isolada.

## Fase 2 — contrato de dados (fechar antes de codar)

### 2.a. Push do parser para o hub

```text
GET /servoData
    rpm          float    velocidade (P0-09)
    torque_pct   float    torque (P0-44), %
    torque_nm    float    torque convertido
    load_pct     float    carga média (P0-10), %
    power_w      float    potência mecânica no eixo
    energy_wh    float    energia acumulada
    state        int      0=OFF 1=READY 2=SON 3=ALARM  (de P0-46)
    alarm        int      código de P0-01
    ok           uint32   transações Modbus válidas
    err          uint32   transações Modbus inválidas
```

Query params, como `/pumpData` e `/biomassData`. Não inventar POST/JSON aqui.

### 2.b. Estado no v7

```c
float    servoRpm, servoTorquePct, servoTorqueNm;
float    servoLoadPct, servoPowerW, servoEnergyWh;
int      servoState, servoAlarm;
uint32_t servoCommOk, servoCommErr;
uint32_t servoLastUpdate;
bool     servoCommOn;                    // habilita/desabilita pelo PC
```

Staleness espelhando o idioma de `flowmeterCommOn` / `flowmeterLastUpdate`:
marcar offline após `SERVO_STALE_MS` sem atualização.

### 2.c. Saída para o PC, em `/readData`

Seguindo a convenção PascalCase do `jsonResponse`, e guardado por
`if (servoOnline)`, como já é feito para `pump` e `biomass`:

```text
ServoOnline, ServoRpm, ServoTorquePct, ServoTorqueNm, ServoLoadPct,
ServoPowerW, ServoEnergyWh, ServoState, ServoAlarm,
ServoCommOk, ServoCommErr
```

### 2.d. Canal de comando

O lado do drive é **somente leitura por projeto**, então os únicos comandos que
fazem sentido são locais ao parser:

```text
reset_energy   zera o acumulador de energia (equivalente ao 'R' do sketch)
poll_ms        intervalo de amostragem
```

```text
GET /servoCommand  ->  takePending(pendingServoCommand)
```

**Usar a caixa simples de consumo-na-leitura**, como `biomass`, `agitator` e
`pump` — **não** a máquina revisionada com `cmd_id`/ack do flowmeter. Aquela
complexidade existe porque comandos de flowmeter mudam estado físico de válvula
e não podem se perder. Aqui nada físico muda: consumo-na-leitura é o certo.

## Fase 3 — firmware do parser

Base: `ASDA_B2_RS485_First_Test`. Novo sketch, não editar o de teste.

```text
Núcleo 0   task Modbus     polling RS-485 a 1 Hz, timeouts curtos
Núcleo 1   task rede       associação, push HTTP 1 Hz, pull de comando ~2 s
           mutex protegendo a struct de telemetria compartilhada
```

Razão para separar em tasks: três leituras Modbus com timeout de 250 ms somam
750 ms no pior caso. Num loop único, mais o HTTP, o ciclo de 1 s estoura. O v7
já usa esses idiomas de FreeRTOS — manter a mesma disciplina.

### Regras que não podem ser violadas

```text
1. Falha de WiFi NÃO pode parar o polling Modbus.
   Continuar amostrando, guardar a última amostra, retomar o push ao reconectar.

2. A integração de energia fica NO PARSER, nunca no v7.
   Ela precisa da série contínua a 1 Hz. Se o v7 integrasse a partir dos pushes,
   qualquer queda de WiFi corromperia a integral. O sketch atual já faz
   integração trapezoidal com rejeição de lacuna — preservar isso.

3. Watchdog, como no v7.

4. Continuar somente-leitura: apenas função 03H.
```

## Fase 4 — alterações no v7 (cirúrgicas)

```text
1. globais da seção 2.b + constante SERVO_STALE_MS
2. server.on("/servoData", ...)     copiar a forma de /pumpData (a mais simples)
3. server.on("/servoCommand", ...)  takePending(pendingServoCommand)
4. campos no jsonResponse, dentro de if (servoOnline)
5. processJsonCommand: aceitar "servoComm" (on/off) e "resetServoEnergy",
   espelhando o tratamento de "flowmeterComm"
```

**Não tocar** na máquina de ack do flowmeter, nem no `sensorSerial`, nem nos
mutexes existentes. A integração é aditiva.

## Fase 5 — validação

```text
[ ] /readData mostra ServoOnline:false com o parser desligado
[ ] ligar o parser -> ServoOnline:true dentro do prazo esperado
[ ] desligar o parser -> volta a false após SERVO_STALE_MS
[ ] HubStations = 6
[ ] motor parado    -> ServoRpm ~ 0
[ ] motor girando   -> ServoRpm acompanha a rotação
[ ] sob carga       -> torque e carga sobem coerentemente
[ ] ServoEnergyWh monotônica com potência positiva
[ ] derrubar o WiFi do parser e reconectar -> SEM salto na energia
[ ] CN1 continua comandando velocidade normalmente
[ ] nenhum alarme novo no drive
[ ] rail de 5 V estável com o WiFi transmitindo
```

---

# 4. Riscos em aberto

| Risco | Mitigação |
|---|---|
| Orçamento de 8 estações do SoftAP | 6 em uso; monitorar. Plano B: ESP-NOW (2.1) |
| Fonte de 5 V da placa controladora sem folga para picos de WiFi | Medir na Fase 1; bulk capacitivo; se faltar, fonte dedicada |
| Ruído do servo no RS-485 durante operação real | Par trançado do cabo 1394; cabo curto; `R0` só se necessário |
| Torque nominal do motor para conversão N·m | ECMA-C20604ES, 1,27 N·m — confirmar na placa do motor |
