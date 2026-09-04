# Delta ASDA-B2 (ASD-B2-0421-B) + ESP32-S3
## RS-485 / Modbus RTU: ligação, configuração e primeiro teste

> **Documento histórico de bring-up.** A montagem vigente usa **HW-097 novo**,
> 9600 8N2, slave 1, GPIO17/18/16 e divisor 1 kΩ/2 kΩ no RO. As seções antigas
> sobre HW-519 e 38400 baud foram preservadas como registro de diagnóstico e
> não devem orientar a montagem final. Consulte
> `../../PLANO_INTEGRACAO_POTENCIA_OPENTECHUB.md` e
> `../firmware-producao/ASDA_B2_Servo_Node/README.md`.

> **Decisão atual do projeto:** manter o módulo já existente no **CN1** comandando a velocidade e usar o **CN3 apenas para telemetria**, via **RS-485 + módulo HW-097 + ESP32-S3**.

---

# 1. Arquitetura escolhida

A solução principal passa a ser:

```text
Módulo atual
     |
     | comando de velocidade
     v
    CN1
     |
     v
  ASDA-B2
     |
     | RS-485 / Modbus RTU
     v
    CN3
     |
     v
 HW-519 TTL↔RS485
     |
     | UART TTL 3,3 V
     v
  ESP32-S3
```

A razão principal para preferir RS-485 neste projeto é a maior imunidade a ruído elétrico em ambiente com servo drive e motor.

O RS-232 com MAX3232 continua sendo uma alternativa válida, mas **não é mais a solução principal**.

---

# 2. Pinagem CN3 confirmada

Vista frontal conforme o manual:

```text
             CN3

       (6) RS485-      (5) RS485+

       (4) RS232_RX    (3) Reserved

       (2) RS232_TX    (1) GND
```

Identificação confirmada:

```text
CN3-1  GND
CN3-2  RS232_TX
CN3-3  Reserved
CN3-4  RS232_RX
CN3-5  RS485+
CN3-6  RS485-
```

Para a solução RS-485 usaremos:

```text
CN3-5 = RS485+
CN3-6 = RS485-
CN3-1 = GND
```

---

# 2.1. Cabo IEEE-1394 (FireWire) para o CN3

O CN3 é um **IEEE-1394 fêmea de 6 pinos** (FireWire 400 / 1394a). Os contatos
da fêmea são molas recuadas, projetadas para raspar contra lâminas planas de um
plugue macho de verdade. Fio nu prensado num casulo impresso em 3D faz contato
intermitente ou nenhum, mesmo com boa pressão mecânica — e não há como
verificar com multímetro depois de inserido.

Solução: comprar um cabo FireWire 400 **6 pinos ↔ 6 pinos** e cortá-lo ao
meio. Isso dá dois plugues legítimos e, no lado cortado, acesso para mapear
cada condutor.

```text
IDEAL:       FireWire 400 / IEEE-1394a, 6 pinos nas DUAS pontas
UTILIZÁVEL:  6 pinos -> 4 pinos (i.LINK), com rework  -> ver 2.1.6
NÃO SERVE:   4 pinos nas duas pontas   -> não tem os pinos 1 e 2
NÃO SERVE:   9 pinos (FW 800)          -> a menos que seja a variante 9-para-6
```

**Cabo em uso neste projeto:** 6 pinos → 4 pinos, com o plugue de 6 pinos
reaproveitado conforme a seção 2.1.6.

## 2.1.1. O que significa "GND na malha"

Nota do manual, seção 3.5:

> *Two kinds of communication wire of IEEE1394 are commercially available. One
> of the internal ground terminals (Pin 1) will short circuit with the
> shielding and will damage the drive. Do not connect GND to the shielding.*

Decodificando:

- **Malha** = a blindagem trançada em volta do cabo. Num plugue FireWire ela é
  normalmente soldada à **carcaça metálica** do conector.
- A carcaça do CN3 no drive está ligada ao **chassi**, que está ligado ao
  **terra de proteção (PE)** da instalação.
- O **CN3-1 (GND)** é o **terra de sinal interno** do drive, referenciado à
  fonte isolada de +5 V dele. **Não** é terra de proteção.

Existem cabos 1394 dos dois tipos: uns em que o pino 1 é isolado da carcaça, e
outros em que o pino 1 é soldado à carcaça dentro do plugue moldado.

Se você usar o segundo tipo, ao plugar você **amarra o terra de sinal do drive
ao chassi aterrado**. Num servo acionando motor por PWM, correntes de modo
comum passam a circular por uma trilha interna que nunca foi dimensionada para
isso. É esse o caminho que queima o circuito de comunicação.

O curto está **dentro do plugue moldado**: não dá para desfazer. Por isso o
teste tem que ser feito **antes de plugar**.

## 2.1.2. Teste da malha — antes de plugar, antes de cortar

Multímetro em modo continuidade (bipe).

```text
1. Carcaça metálica do plugue  <->  cada um dos 6 contatos
   TODOS os seis têm que dar circuito ABERTO.
   Qualquer bipe = descartar o cabo.

2. Carcaça de uma ponta  <->  carcaça da outra ponta
   Continuidade aqui é NORMAL e esperada (a malha atravessa o cabo).
   O problema é carcaça-para-PINO, não carcaça-para-carcaça.
```

Os contatos do plugue macho são lâminas finas e recuadas. Use uma ponta de
prova fina: agulha de costura, pedaço de fio wire-wrap ou grampo esticado,
preso com fita na ponta do multímetro.

## 2.1.3. Mapeamento dos condutores

Depois de aprovado no teste da malha, corte o cabo ao meio e mapeie:

```text
3. Uma ponta de prova em cada condutor descascado
   Outra ponta em cada contato do plugue
   Anotar: condutor -> contato

4. Identificar o fio de dreno / malha. Ele NÃO vai a lugar nenhum.
   Cortar rente e isolar no lado do HW-519.
```

## 2.1.4. Confirmação funcional da numeração

Não confie em tabela de pinagem de cabo genérico — confirme usando o próprio
drive. O **CN3-2 é RS-232_TX**, e um transmissor RS-232 em repouso (estado
*mark*) fica entre **-5 V e -12 V** em relação ao GND.

```text
5. Passar todos os 6 condutores. Plugar no CN3. Energizar o drive.

6. Multímetro em DC. Escolher um condutor como referência (ponta preta)
   e medir os outros cinco.

   Quando aparecer EXATAMENTE UM condutor entre -5 V e -12 V:
       a referência escolhida  = CN3-1 (GND)
       o condutor negativo     = CN3-2 (RS-232_TX)

   No pior caso são 6 tentativas.
```

Isso é identificação **funcional**: imune a convenção de numeração, e de
quebra prova que o conector faz contato e que a eletrônica do CN3 está viva.

Com os pinos 1 e 2 identificados, a vista frontal do manual fixa toda a
orientação — 1 e 2 são a fileira de baixo:

```text
        (6) RS-485(-)  ┌──────────┐  (5) RS-485(+)
        (4) RS-232_RX  │   CN3    │  (3) Reservado
        (2) RS-232_TX  └───┐  ┌───┘  (1) GND
```

Logo `5` fica acima de `1`, e `6` fica acima de `2`.

**Verificação de sanidade:** um cabo 1394 traz dois pares trançados mais dois
condutores de alimentação. Os pinos que o Delta chama de 5 e 6 devem cair sobre
um **par trançado** — é exatamente por isso que o fabricante escolheu esses dois
para o sinal diferencial. Se o seu mapa colocar 5 e 6 sobre condutores que não
formam par, o mapeamento está errado.

## 2.1.5. Alternativa

Existem cabos prontos vendidos como *"Delta ASDA CN3 IEEE1394 6 pinos com
pontas livres"*. Eles poupam o corte e o mapeamento, mas **o teste da malha da
seção 2.1.2 continua obrigatório**.

## 2.1.6. Rework de um cabo 6-para-4 (procedimento adotado)

Um cabo 6 → 4 pinos serve, desde que o plugue de 6 pinos seja reaproveitado.
A ponta i.LINK de 4 pinos não tem os condutores de alimentação, que são
exatamente os pinos que o Delta usa como `CN3-1 (GND)` e `CN3-2 (RS-232_TX)`.
A solução é soldar fios novos nesses dois contatos.

### Pré-requisito — dispensado se o GND vier da placa do CN1

Adotando a alternativa da seção 5.0 (GND tirado da placa controladora),
**só são necessários os pinos 5 e 6**, e todo o rework abaixo é dispensado.

No padrão IEEE-1394, os pinos `5` e `6` são o par `TPA`, que uma ponta de
4 pinos i.LINK carrega obrigatoriamente. Um cabo 6→4 leva os dois pares de
sinal por definição. Logo:

```text
A contagem de lâminas NÃO é bloqueante nesse caminho.
Mesmo com apenas 4 contatos populados, são exatamente os 4 de interesse
(os pinos 3, 4, 5 e 6 do padrão).
```

Vantagem colateral de segurança: num cabo 6→4 os pinos `1` e `2` não têm
condutor, então o **`RS-232_TX` do drive — a saída de ±12 V, a única
perigosa — nem está presente**. Testar as 4 combinações de par/polaridade
(passo 8) passa a ser de baixo risco.

### Pré-requisito — apenas se for soldar o GND no conector

```text
Olhar a face de contato do plugue de 6 pinos, com lupa, e CONTAR as lâminas.
Alguns cabos baratos populam só 4 contatos dentro da carcaça de 6 pinos.
Se houver apenas 4 -> não há onde soldar o GND; usar a alternativa da 5.0.
```

O **teste da carcaça** (2.1.2) continua obrigatório nos dois caminhos: se o
fabricante amarrou algum contato à carcaça, plugar aterra o terra de sinal do
drive, independentemente de quais condutores você usa.

### Não troque todos os fios

Os pinos `5` e `6` caem sobre um **par trançado blindado** do cabo — é por
isso que o fabricante os escolheu para o sinal diferencial. Fio solto soldado
à mão, correndo ao lado de um servo, é eletricamente pior.

```text
pinos 5 e 6   -> MANTER os condutores originais do cabo
pinos 1 e 2   -> soldar dois fios novos (os dois contatos sem fio)
```

Solda-se em dois pontos em vez de seis, preserva-se o par trançado e cai muito
o risco de ponte entre contatos. Soldam-se **os dois** contatos livres porque
ainda não se sabe qual é o `1` e qual é o `2` — o teste funcional (2.1.4)
separa depois, e o pino `2` ainda serve como prova de contato.

### Sequência

```text
1. Cortar fora a ponta de 4 pinos.

2. Mapear por continuidade cada condutor do cabo -> cada um dos 6 contatos.
   Resultado esperado: 4 condutores em 4 contatos, e 2 contatos SEM fio.
   Os dois sem fio são os pinos 1 e 2.

3. Cortar o overmold para trás apenas o suficiente para alcançar as caudas
   dos contatos. NÃO desmontar a carcaça inteira: o formato externo do
   plugue é o que encaixa e alinha no soquete, e a carcaça costuma vir
   crimpada — reabrir e fechar raramente fica confiável.

4. Soldar os dois fios novos. Ponta fina, 300-320 °C, rápido.
   O bloco isolante é plástico: calor demais deforma e desalinha os
   contatos, produzindo exatamente o contato intermitente que se quer
   evitar. Estanhar fio e contato separados. Fita Kapton entre contatos.

5. TESTE ELÉTRICO, antes de plugar:
       cada contato <-> cada outro contato  (15 pares) = ABERTO
       cada contato <-> carcaça metálica    (6 medidas) = ABERTO
       dreno/malha  <-> qualquer contato               = ABERTO

   Uma ponte entre o pino 2 (saída RS-232_TX) e o pino 1 (GND)
   curto-circuita o driver do drive.

6. Alívio de tração com epóxi ou cola quente, e REFAZER a medição de
   isolação depois — cola quente empurra fios.

7. Identificação funcional dos pinos 1 e 2, conforme 2.1.4.
   Fazer isso ANTES de conectar o HW-519.

8. Achar o par 5/6 por tentativa: 2 pares candidatos x 2 polaridades =
   4 combinações. Rodar o teste 3 do ASDA_B2_RS485_Scan em cada uma.
```

### Carcaça metálica

Com o rework, o cuidado da seção 2.1.1 passa a ser responsabilidade de quem
monta — o que é uma vantagem, porque deixa de depender do fabricante.

```text
Deixar a carcaça metálica FLUTUANTE. Não ligar em nada deste lado.
Confirmar isolação carcaça <-> contatos no passo 5 e de novo no passo 6.
```

### Não deduzir a orientação pela geometria

O diagrama do manual é a **vista frontal do soquete fêmea**. A face de contato
do **plugue macho é espelhada** em relação a ele. É armadilha clássica: use a
identificação funcional (2.1.4) e a tentativa do passo 8, não o desenho.

### Risco de ligar A/B nos pinos errados

Os pinos `3` e `4` são as linhas RS-232. Se o `A`/`B` do HW-519 for ligado ali
por engano, o `RS-232_TX` do drive joga ±12 V na entrada do transceptor — fora
da faixa de -7 V a +12 V do RS-485. O módulo tem TVS, mas é risco evitável:
**identificar os pinos 1 e 2 antes de conectar o módulo**.

Num cabo **6→4**, esse risco não existe: o pino `2` não tem condutor, então a
saída de ±12 V nem chega ao cabo. É o que torna a tentativa das 4 combinações
(T5, seção 2.2) segura.

---

# 2.2. Roteiro de bancada — o que está ligado em cada teste

Os testes abaixo respondem a perguntas **diferentes** e têm montagens
**diferentes**. A confusão mais comum é misturar T1 (propriedade do cabo) com
T2 (propriedade do drive).

## T1 — Só o cabo

Pergunta: *"este cabo é seguro para plugar, e qual condutor vai em qual
contato?"*

```text
cabo FireWire, ponta de 4 pinos JÁ CORTADA
      |
      +-- nada plugado em lugar nenhum
          drive: irrelevante
          multímetro: continuidade / ohms
```

| #   | Ponta A            | Ponta B                  | Esperado |
|-----|--------------------|--------------------------|----------|
| 1.1 | carcaça metálica   | cada um dos 6 contatos   | **aberto** — qualquer bipe: descartar o cabo |
| 1.2 | cada condutor      | cada um dos 6 contatos   | o mapa: 4 condutores em 4 contatos, 2 contatos sem fio |
| 1.3 | cada condutor      | cada outro condutor      | **aberto** — sem curtos internos |
| 1.4 | dreno / malha      | cada condutor            | **aberto** |
| 1.5 | —                  | —                        | desenrolar e anotar quais dois condutores formam cada par trançado |

## T2 — Só o drive

Pergunta: *"a Delta ligou o terra de sinal interno na carcaça, ou deixou
flutuando?"*

Isso foi decidido na fábrica e está soldado dentro do drive. **Não se está
criando nem ligando nada — está se descobrindo o que já existe.** Por isso não
há cabo envolvido: o cabo não influencia esta resposta.

```text
cabo: NÃO plugado
drive: DESENERGIZADO, fora da tomada
multímetro: ohms

ponta A ---> GND da placa do CN1   (mesmo nó do CN3-1)
ponta B ---> chassi / parafuso de terra do drive
```

| Leitura           | Significado                    | Ação |
|-------------------|--------------------------------|------|
| aberto (megohms)  | terra de sinal isolado do PE   | notebook na bateria, 100 Ω em série |
| ~0 ohm            | já bonded                      | nada a fazer |

Ressalva: se a placa do CN1 tiver fonte própria aterrada, um `0 Ω` pode vir
dela e não do drive. **Não muda a ação** — de um jeito ou de outro o vínculo já
existe, e não há como criar um novo.

Serve para uma coisa só: saber se **as suas** ligações podem criar um vínculo
que não existia.

## T3 — Cabo plugado, drive desligado (opcional, indicativo)

Pergunta: *"o conector está fazendo contato?"*

```text
cabo: PLUGADO no CN3
drive: DESENERGIZADO
HW-519: NÃO conectado
multímetro: ohms

ponta A ---> GND da placa do CN1
ponta B ---> cada um dos 4 condutores
```

Procura-se resistência **finita** (kΩ a dezenas de kΩ), vinda das redes de bias
internas do drive. Leitura finita é bom sinal de contato; tudo aberto levanta
suspeita. Indicativo, não conclusivo.

## T4 — Cabo plugado, drive ligado — porta de segurança

Pergunta: *"posso conectar o HW-519 sem risco?"*

```text
cabo: PLUGADO no CN3
drive: ENERGIZADO
HW-519: AINDA NÃO conectado
multímetro: volts DC

ponta preta   ---> GND da placa do CN1
ponta vermelha---> cada um dos 4 condutores
```

Todos devem ficar **dentro de ±5 V**. Fora disso, parar.

Não se vai encontrar os -5 a -12 V do `RS-232_TX`: num cabo 6→4 o pino `2` não
tem condutor. Isso é esperado, não é falha.

## T5 — Ligar o módulo e varrer

```text
   CN3 --cabo-- par candidato --> A+ / B-  HW-519
                                            |
placa CN1 GND --[100 ohm]----------------> GND
                                            |
                              VCC <-- 3V3 do ESP32-S3
                              TXD --------> GPIO18
                              RXD <-------- GPIO17
                                            |
                         R0 aberto . malha isolada
                                            |
                        ESP32-S3 --USB-- notebook NA BATERIA
```

4 combinações: par 1 normal, par 1 invertido, par 2 normal, par 2 invertido.
Teste 3 do `ASDA_B2_RS485_Scan` em cada uma, cerca de 30 s por tentativa.

**A prova de contato é funcional.** Com cabo 6→4 e GND vindo da placa do CN1,
não há como provar contato por multímetro antes de tentar. Se o scanner
responder, está provado do jeito que importa.

---

# 3. Módulo HW-519 TTL↔RS485

O módulo disponível possui:

```text
lado TTL:
VCC
TXD
RXD
GND

lado RS485 (bloco de 3 vias):
A+
B-
GND
```

O terceiro borne, antes não identificado, é o **GND** do módulo. Ele deve ser
ligado ao **CN3-1** — ou à placa do CN1, conforme a seção 5.0.

## 3.1. Os dois GNDs do HW-519 — CORRIGIDO em 31/08/2026

O módulo tem um pino `GND` no header TTL e outro no bloco parafusado do lado
`A`/`B`.

> **A versão anterior desta seção afirmava que os dois são o mesmo net e que a
> continuidade "deve bipar". Está errado.** Medido em bancada: os dois não
> apitam — e o mesmo acontece em **duas placas diferentes**, então não é
> defeito de uma unidade, é projeto.

A explicação mais provável é a mais banal: o borne do lado RS-485 chega ao
terra lógico através de um **resistor em série, tipicamente de 100 Ω**. É o que
a TIA/EIA-485 recomenda para ligar terras de sinal entre nós, limitando a
corrente de circulação. O apito de continuidade dispara em torno de 50 Ω, então
fica mudo num resistor de 100 Ω sem que haja nada de errado.

**Medir em ohms, não no apito.** Entre o `GND` do lado TTL e o borne do lado
RS-485:

```text
0 ... 5 ohm        mesmo cobre (o que esta secao supunha)
100 ... 1000 ohm   resistor em serie. Normal. Usar o borne.
OL / megaohms      modulo isolado, ou aquele borne nao e GND
```

Módulo isolado tem um bloco DC-DC preto marcado `B0505S` e um rasgo fresado
atravessando a PCI. Sem esses dois sinais, `OL` significa que o borne não é o
terra.

**Medir também o borne contra o `VCC` do lado TTL.** Perto de zero significa
que o borne é `VCC`: ligado ao `CN3-1` estaria injetando 5 V no terra de sinal
do drive.

Enquanto a leitura em ohms não confirmar o borne, **usar o `GND` do header
TTL** — o mesmo pino onde está o `GND` do ESP32. É a referência que se sabe
boa. A preferência pelo borne parafusado (menor área de laço, referência do
mesmo lado do par diferencial) só vale depois que ele estiver confirmado.

Consequência a ter em mente: ao ligar o GND da placa controladora no módulo,
ele fica ligado ao GND do ESP32 também. Isso é intencional — é o que cria a
referência comum — e é a origem de toda a discussão de terra da seção 5.0.

Com um fio curto (ordem de 15 cm), a preocupação de área de laço da 5.0 deixa
de ser fator.

O anúncio do módulo informa compatibilidade de alimentação e sinais com **3,3 V e 5 V**.

Para o ESP32-S3, a decisão é:

```text
HW-519 VCC -> 3V3 do ESP32-S3
HW-519 GND -> GND do ESP32-S3
```

## Verificação antes de conectar os GPIOs

Mesmo com a especificação do anúncio, como o módulo é genérico, confirmar com multímetro:

```text
TXD do HW-519 em relação ao GND
```

O nível TTL deve permanecer aproximadamente dentro de:

```text
0 ... 3,3 V
```

Se aparecer algo próximo de 5 V ou tensão negativa, **não conectar ao ESP32**.

---

# 4. Ligação ASDA-B2 CN3 -> HW-519 -> ESP32-S3

## 4.1. Lado RS-485

```text
ASDA-B2 CN3-5  RS485+  ---------->  A+   HW-519
ASDA-B2 CN3-6  RS485-  ---------->  B-   HW-519
ASDA-B2 CN3-1  GND     ---------->  GND  HW-519  (terceiro borne)
                                    R0   HW-519  (não conectar)
```

Não cruzar A/B:

```text
RS485+ -> A+
RS485- -> B-
```

---

## 4.2. Lado TTL / UART

No lado ESP32, TX e RX são cruzados:

```text
HW-519 TXD  --------------------->  GPIO18 (RX) do ESP32-S3
HW-519 RXD  <---------------------  GPIO17 (TX) do ESP32-S3

HW-519 VCC  --------------------->  3V3 do ESP32-S3
HW-519 GND  --------------------->  GND do ESP32-S3
```

Portanto:

```text
TXD do módulo -> RX do ESP32
RXD do módulo <- TX do ESP32
```

Não usar TX->TX.

## 4.3. Por que NÃO usar GPIO43/GPIO44

Esta é a correção mais importante do projeto.

No ESP32-S3, `GPIO43` e `GPIO44` são `U0TXD` e `U0RXD`. Na maioria das placas
de desenvolvimento eles estão **ligados fisicamente ao chip USB-serial
(CH340/CP2102) da própria placa** — é esse chip que dá origem à entrada
`UART/COM`.

Esse chip fica alimentado junto com a placa e mantém a saída dele empurrando o
`GPIO44` continuamente. Duas saídas push-pull disputando o mesmo nó: o `TXD` do
HW-519 não consegue puxar a linha para baixo.

Sintoma exato:

```text
o LED RXD do módulo pisca em sincronia com o ESP     (transmissão OK)
nenhum byte chega de volta                           (recepção morta)
```

Divisão de UARTs adotada:

```text
UART0  GPIO43 / GPIO44   console de depuração, pela entrada COM
UART1  GPIO17 / GPIO18   barramento RS-485
```

GPIOs a evitar no ESP32-S3:

```text
0, 3, 45, 46      strapping
19, 20            USB nativo
26 ... 32         flash SPI
33 ... 37         PSRAM octal (módulos N8R8 / N16R8)
43, 44            UART0 / chip USB-serial da placa
```

---

# 5. Terceiro borne do HW-519 e o jumper R0

**Resolvido:** o terceiro borne ao lado de `A+` e `B-` é o **GND** do módulo.
Ele deve ser conectado ao **CN3-1**.

Isso não é opcional. RS-485 é diferencial, mas não é livre de terra: os
receptores só funcionam se a tensão de modo comum entre os dois lados ficar
dentro de **-7 V a +12 V**. O ESP32 alimentado por notebook flutua, e o drive
tem capacitores Y para a rede. Sem esse fio de referência os dois lados podem
ficar dezenas de volts AC de diferença e nenhum receptor entende nada.

```text
HW-519 GND -> CN3-1 GND
```

Existe uma origem alternativa para essa referência, medida e adotada neste
projeto — ver 5.0 logo abaixo.

## 5.0. De onde tirar o GND: CN3-1 ou a placa do CN1

**Medido neste projeto:** o GND da placa controladora ligada ao CN1 tem
continuidade com o `CN3-1`. São o mesmo nó elétrico — o terra de sinal interno
do drive.

Logo, a placa do CN1 pode servir de âncora da referência de modo comum, no
lugar do `CN3-1`.

### Ganho

Elimina a solda no conector. Os pinos `5` e `6` já têm o par trançado original
do cabo; some o GND da placa controladora e o link está completo. Todo o rework
da seção 2.1.6 sai do caminho crítico: sem cortar overmold, sem risco de ponte
entre contatos, sem calor no bloco isolante.

Perde-se o teste funcional do pino `2` como prova de contato — mas isso era um
meio, não um fim. Se o scanner achar o drive, o contato está provado.

A busca do par `5`/`6` continua sendo as mesmas 4 tentativas (2.1.6, passo 8).

### MEDIR ANTES: terra de sinal contra o PE

O aviso da seção 2.1.1 existe porque o `CN3-1` é **terra de sinal isolado**, e
amarrá-lo ao PE é o que danifica o drive. Só que o ESP32 fica ligado a um
notebook, e notebook na tomada tem o terra do USB no PE:

```text
GND de sinal do drive -- HW-519 -- ESP32 -- USB -- notebook -- PE
                                                                |
drive: chassi ------------------------------------------------- PE
```

Isso fecha POR FORA o caminho que o manual manda não fechar por dentro do
cabo. Vale igual se o GND viesse do `CN3-1`: não é problema desta alternativa,
é da bancada inteira.

```text
MEDIR: resistência entre o GND da placa controladora e o
       parafuso de terra / chassi do drive.

   aberto (megohms) -> terra de sinal isolado.
                       Ligar em notebook aterrado cria vínculo novo.
                       -> rodar o notebook NA BATERIA durante os testes.

   perto de 0 ohm   -> já bonded de fábrica, questão acadêmica.
```

**Não inverter a leitura.** É contraintuitivo: *isolado* é o caso que pede
cuidado, porque é o que permite a VOCÊ criar um vínculo que não existia — e
é exatamente isso que dá sentido ao aviso do manual. *Em curto de fábrica*
significa que o fabricante já resolveu, e nada que você ligue cria algo novo.

### "Mas a placa controladora já está na mesma tomada"

Objeção natural, e o passo que não fecha é este:

```text
estar na mesma tomada  !=  ter o terra ligado ao PE
```

Quase toda placa de controle roda de fonte chaveada **isolada**: há um
transformador no meio, e o 0 V do secundário flutua em relação ao PE. A placa
pode estar na mesma tomada, no mesmo quadro, no mesmo prédio — se a fonte é
isolada, o 0 V dela não encosta no terra de proteção. *Alimentado pela rede* e
*referenciado ao terra da rede* são coisas diferentes.

**O T2 é exatamente o teste dessa hipótese.** Se a placa controladora
aterrasse o nó, o T2 daria `~0 Ω`. Se der aberto, isso é a prova empírica de
que a fonte dela é isolada. Não é para deduzir, é para medir.

### Calibragem do risco — aberto NÃO proíbe o PC na tomada

| Situação | Impedância do vínculo | Risco |
|---|---|---|
| Aviso do manual: pino 1 em curto com a carcaça, dentro do plugue | **~0 Ω**, no conector | Real |
| Este projeto: GND → 100 Ω → módulo → ESP32 → USB → notebook → fonte → PE | **centenas de Ω + chokes + Y-caps** | Muito baixo |

São ordens de grandeza diferentes. O `100 Ω` em série já garante, por
construção, que o vínculo nunca é de baixa impedância — que é a condição
necessária para o dano.

```text
BANCADA, motor parado, 15 cm de fio, alguns minutos:
   notebook na tomada está OK.
   bateria é grátis e elimina a pergunta -> usar se for conveniente.
   é precaução, não proibição. Não deve travar o trabalho.

PERMANENTE, motor girando sob carga:
   aí sim aparecem as correntes de modo comum do PWM.
   Solução limpa: isolador USB (base ADuM3160) entre notebook e ESP32,
   ou transceptor RS-485 isolado (ADM2483 / ISO3082).
```

### Compromissos

**Área de laço.** No `CN3-1` a referência viajaria dentro do mesmo cabo, colada
ao par diferencial. Vindo da placa do CN1 ela faz outro percurso, e o laço entre
o par e seu retorno fica grande — ao lado de um servo. Mitigar: manter o ESP32
fisicamente perto e amarrar o fio de GND junto ao cabo FireWire.

**Acoplamento com o controle.** Fala direto com a premissa do projeto de não
alterar o controle existente. Hoje a placa do CN1 e o ESP32 são circuitos
separados; ligando os GNDs, ruído do lado da telemetria passa a ter caminho
para o retorno da referência analógica de velocidade.

```text
MITIGAÇÃO: 100 ohm, >= 1/2 W, EM SÉRIE no fio de GND.
Preserva a referência de modo comum e limita corrente circulante de laço.
É prática padrão de RS-485.
```

Escolher um ponto de massa limpo — o próprio pino GND do conector CN1 ou um
borne de terra dedicado. Não um ponto no meio de trilha que carrega corrente de
retorno: a queda IR vira offset de modo comum.

### Decisão

```text
AGORA:      GND da placa do CN1, com 100 ohm em série,
            notebook na bateria.  -> desbloqueia o teste de protocolo hoje.

DEFINITIVO: voltar ao CN3-1 (referência dentro do cabo, telemetria
            desacoplada do controle)
            OU, melhor, transceptor RS-485 ISOLADO (ADM2483, ISO3082)
            ou dongle USB-RS485 isolado — com isolação galvânica toda
            esta seção deixa de existir.
```

## 5.1. Jumper R0

O `R0` do módulo **não é um sinal**. É o jumper que insere o resistor de
terminação de **120 Ω** entre `A` e `B`.

```text
R0 -> DEIXAR ABERTO
```

Em cabo curto de bancada, a 38400 baud, reflexão não é problema. E fechar o
R0 carrega a rede de polarização (bias) do módulo, derrubando a tensão
diferencial de repouso — num barramento marginal, isso piora.

Terminação só faz sentido em cabo longo, e aí com 120 Ω **nas duas pontas**
do barramento. O CN3 do ASDA-B2 não tem terminador interno.

---

# 6. CN1 continua responsável pelo controle

O módulo já instalado no CN1 deve continuar comandando a velocidade.

O ESP32 será inicialmente **somente leitor**.

Arquitetura operacional:

```text
CN1 -> comando de velocidade
CN3 -> somente telemetria Modbus
```

Não alterar parâmetros de controle já existentes.

---

# 7. Parâmetros que NÃO devem ser alterados

Especialmente:

```text
P1-01   modo de controle
P3-06   origem das entradas digitais DI
P2-10 ... P2-17
P2-36   atribuição das DIs
parâmetros atuais da referência de velocidade
parâmetros de torque limit
```

`P3-06` é particularmente importante porque define se as DIs vêm do hardware externo ou da comunicação.

---

# 8. Antes de mudar qualquer coisa

Primeiro apenas ler e anotar:

```text
P1-01 = ?
P3-00 = ?
P3-01 = ?
P3-02 = ?
P3-05 = ?
P3-06 = ?
```

Não alterar nada antes de registrar esses valores.

---

# 9. Como navegar no painel

Teclas:

```text
MODE
SHIFT
▲
▼
SET
```

Procedimento:

1. `MODE` alterna Monitor / Parameter / Alarm.
2. Em Parameter Mode, `SHIFT` muda o grupo (`P0`, `P1`, `P2`, `P3`...).
3. `▲` / `▼` escolhem o parâmetro.
4. `SET` mostra o valor e entra em edição.
5. Em edição, `SHIFT` move o dígito piscando.
6. `▲` / `▼` alteram o valor.
7. `SET` grava.
8. O display mostra `SAVED`.

---

# 10. Configuração de comunicação planejada

Depois de anotar os valores atuais, a configuração desejada é:

```text
P3-00 = 01
P3-01 = 0033
P3-02 = 0066
P3-05 = 0
```

## 10.0. Os dois padrões de fábrica que bloqueiam a comunicação

Conferido no manual, revisão de maio/2018:

| Parâmetro | Endereço | Padrão de fábrica | Necessário | Bloqueia? |
|-----------|----------|-------------------|------------|-----------|
| P3-00     | 0300H    | **0x007F (127)**  | `0x0001`   | **Sim** — o drive não atende no endereço 1 |
| P3-01     | 0302H    | 0x0033            | `0x0033`   | Não — já correto |
| P3-02     | 0304H    | 0x0066            | `0x0066`   | Não — já correto |
| P3-05     | 030AH    | **0x0001**        | `0x0000`   | **Sim** — porta dedicada ao ASDA-Soft |

Baud e formato já saem de fábrica nos valores deste projeto. **Endereço e
mecanismo, não.** Se a comunicação está muda e esses dois nunca foram
alterados no painel, é aqui que o problema está.

## 10.1. P3-00 — endereço Modbus

```text
P3-00 = 01
```

Resultado:

```text
Modbus slave address = 1
```

> **Padrão de fábrica: `0x007F` = 127.** Um mestre que pergunta ao endereço 1
> recebe silêncio absoluto de um drive ainda em `007F`. RS-485 não tem NACK:
> endereço errado e fio partido dão exatamente o mesmo sintoma.

O parâmetro entra em vigor após power-cycle.

## 10.2. P3-01 — baud rate

```text
P3-01 = 0033
```

Tabela:

```text
0 = 4800
1 = 9600
2 = 19200
3 = 38400
4 = 57600
5 = 115200
```

Em `0033`:

```text
RS-485 = 38400 baud
RS-232 = 38400 baud
```

## 10.3. P3-02 — protocolo

```text
P3-02 = 0066
```

Código `6`:

```text
8 data bits
No parity
2 stop bits
MODBUS RTU
```

Logo o ESP32 deve usar:

```text
38400 baud
8N2
Modbus RTU
```

## 10.4. P3-05 — mecanismo de comunicação

```text
P3-05 = 0
```

Definição literal do manual (`Communication Mechanism`, endereço 030AH,
faixa 0x00 a 0x01, **padrão 1**):

```text
RS-232 Communication interface selection
  0 = RS-232 via Modbus communication
  1 = RS-232 upon ASDA-Soft software
```

**Correção em relação à versão anterior deste documento:** P3-05 não é um
seletor "RS-232 ou RS-485". Ele seleciona apenas em que modo a interface
**RS-232** opera. O valor `0` continua sendo o que queremos, mas pelo motivo
correto: a seção 8.1 do manual diz que *"RS-485 and RS-232 cannot be used at
the same time"*, e com `P3-05 = 1` a porta CN3 fica dedicada ao protocolo
proprietário do ASDA-Soft.

> **Padrão de fábrica: 1.** Este é o segundo parâmetro que bloqueia o
> Modbus RTU sozinho.

---

# 11. Primeiro teste de comunicação

Configuração do ESP32:

```text
UART:        hardware UART
baud:        38400
data bits:   8
parity:      none
stop bits:   2
protocol:    Modbus RTU
slave:       1
```

Função Modbus principal:

```text
03H = leitura de registradores
```

O manual pede aproximadamente **10 ms de silêncio antes e depois dos frames RTU**.

---

# 12. Primeiro monitor: RPM

Configurar:

```text
P0-17 = 7
```

Isso faz:

```text
P0-09 -> Speed feedback
```

`P0-09` é 32-bit:

```text
0x0012 = low word
0x0013 = high word
```

Leitura:

```text
slave:        01
function:     03
start addr:   0012h
word count:   2
```

Conceitualmente:

```text
01 03 00 12 00 02 CRClo CRChi
```

Montagem:

```text
value32 = (high_word << 16) | low_word
```

Unidade:

```text
0,1 rpm
```

Exemplo:

```text
raw = 6000
RPM = 600,0
```

---

# 13. Segundo monitor: carga média

Depois que RPM funcionar:

```text
P0-18 = 12
```

Resultado:

```text
P0-10 -> Average load rate [%]
```

---

# 14. Torque feedback

O torque real usa uma variável expandida:

```text
code 54 = Torque feedback
```

O manual define:

```text
Current actual motor torque
unit = 0,1 %
```

Configurar:

```text
P0-45 = 54
```

e ler:

```text
P0-44
```

Portanto:

```text
P0-45 = 54
        |
        v
P0-44 = Torque feedback
```

Não colocar `54` em `P0-17`, pois `P0-17 ... P0-21` são destinados às variáveis básicas 0–18.

---

# 15. Configuração final dos monitores

```text
P0-17 = 7
P0-18 = 12
P0-45 = 54
```

Resultado:

```text
P0-09 -> velocidade
P0-10 -> carga média
P0-44 -> torque feedback
```

---

# 16. Conversão do torque

O torque feedback vem em:

```text
0,1 % do torque nominal
```

Exemplo:

```text
raw_torque = 253
```

Então:

```text
torque_percent = 25,3 %
```

Com torque nominal `T_rated`:

```text
T_Nm = (raw_torque / 1000.0) * T_rated
```

Ainda precisamos confirmar o modelo exato do servomotor antes de fixar `T_rated`.

---

# 17. Potência mecânica

```text
omega = 2*pi*RPM/60
Power_W = Torque_Nm * omega
```

ou:

```text
Power_W = Torque_Nm * 2*pi*RPM/60
```

---

# 18. Energia

Usar integração trapezoidal:

```text
E_i = E_(i-1) + (P_i + P_(i-1))/2 * dt
```

Conversões:

```text
J = W*s
Wh = J/3600
```

---

# 19. Sequência atualizada de comissionamento

## Etapa A — sem modificar o drive

1. Confirmar CN3.
2. Rodar o roteiro de bancada da **seção 2.2**, na ordem:
   - `T1` só o cabo — carcaça, mapa condutor → contato, pares trançados;
   - `T2` só o drive — terra de sinal contra chassi;
   - `T3` cabo plugado, drive desligado — indício de contato;
   - `T4` cabo plugado, drive ligado — porta de segurança, ±5 V;
   - `T5` ligar o HW-519 e varrer as 4 combinações.

   Se for soldar GND no próprio conector em vez de usar a placa do CN1,
   ver o rework da seção 2.1.6 antes do `T1`.
3. Usar:
   - CN3-5
   - CN3-6
   - CN3-1
4. Ligar ao HW-519:
   - 5 -> A+
   - 6 -> B-
   - GND (terceiro borne) -> `CN3-1` **ou** GND da placa do CN1, com
     100 Ω em série. Ver 5.0 — inclui a medição terra-de-sinal contra PE
     e a regra de rodar o notebook na bateria.
5. Deixar o jumper `R0` ABERTO.
6. Deixar o dreno/malha do cabo desconectado e isolado.
7. Alimentar HW-519 em 3,3 V.
8. Medir TXD do módulo em relação ao GND.
9. Confirmar aproximadamente 0–3,3 V.
10. Anotar no painel do drive:
    - P1-01
    - **P3-00** (fábrica = `007F`, precisa virar `0001`)
    - P3-01
    - P3-02
    - **P3-05** (fábrica = `1`, precisa virar `0`)
    - P3-06

## Etapa B — configurar comunicação

Se necessário:

```text
P3-00 -> 01
P3-01 -> 0033
P3-02 -> 0066
P3-05 -> 0
```

Não alterar:

```text
P1-01
P3-06
```

Fazer power-cycle se `P3-00` foi alterado.

## Etapa C — ligar ESP32 ao HW-519

```text
HW-519 VCC -> ESP32 3V3
HW-519 GND -> ESP32 GND
HW-519 TXD -> ESP32 GPIO18 (RX)
HW-519 RXD <- ESP32 GPIO17 (TX)
```

Console pela entrada `COM` da placa (UART0, GPIO43/44). Ver seção 4.3.

## Etapa C.1 — provar a camada física antes do protocolo

Carregar `Software/ASDA_B2_RS485_Scan` e rodar, nesta ordem:

```text
teste 1   eco, com A/B desconectados do drive
teste 3   varredura de baud x formato nos endereços 0x01 e 0x7F
teste 2   varredura dos 127 endereços
teste 5   leitura dos P3-xx do drive encontrado
```

O teste 3 é o de maior retorno: cobre as duas hipóteses de padrão de fábrica
sem exigir nenhuma alteração no drive.

## Etapa D — provar Modbus

1. ESP32 = 38400 8N2.
2. Slave = 1.
3. Enviar leitura função 03.
4. Confirmar resposta.
5. Confirmar CRC.
6. Confirmar que o CN1 continua controlando normalmente.

## Etapa E — provar RPM

1. `P0-17 = 7`.
2. Ler `P0-09`.
3. Motor parado -> ~0 rpm.
4. Motor girando -> leitura acompanha a rotação.

## Etapa F — torque

1. `P0-45 = 54`.
2. Ler `P0-44`.
3. Comparar sem carga vs. com carga.
4. Confirmar sinal e escala.
5. Converter para N·m após confirmar torque nominal do motor.

## Etapa G — aquisição científica

Registrar:

```text
timestamp
rpm
torque_raw
torque_percent
torque_Nm
average_load_percent
power_W
energy_J
```

---

# 20. Critérios de aceitação

```text
[ ] Drive sem alarmes novos
[ ] Módulo CN1 continua controlando velocidade
[ ] ESP32 recebe Modbus com CRC válido
[ ] RPM ~0 com motor parado
[ ] RPM acompanha a rotação
[ ] Torque muda coerentemente com a carga
[ ] DIs do CN1 não mudaram de comportamento
[ ] HW-519 opera corretamente em 3,3 V
```

---

# 21. Troubleshooting

## Sem resposta Modbus

Em ordem de probabilidade, com base no que já foi investigado:

```text
1. P3-00 ainda em 007F      (padrão de fábrica; ver 10.0)
2. P3-05 ainda em 1         (padrão de fábrica; ver 10.4)
3. RS-485 em GPIO43/44      (conflito com o USB-serial; ver 4.3)
4. CN3-1 (GND) sem contato  (modo comum fora de faixa; ver 5)
5. Contato mecânico do CN3  (conector impresso; ver 2.1)
6. A/B invertidos           (nomenclatura do fabricante)
```

Sintoma que distingue o item 3: **o LED RXD do módulo pisca em sincronia com o
ESP e nada volta**. Isso prova que a transmissão funciona e isola a falha no
caminho `TXD do módulo -> RX do ESP`.

Verificar também:

```text
P3-01 = baud correto
P3-02 = formato correto
A+ -> CN3-5
B- -> CN3-6
GND -> CN3-1
R0  aberto
HW-519 alimentado em 3,3 V
```

Antes de mexer na fiação, rodar `Software/ASDA_B2_RS485_Scan`. Ele varre
endereço, baud e formato, e imprime os bytes crus — o que separa
**silêncio** de **eco** de **lixo**, três diagnósticos diferentes que o sketch
de telemetria reporta todos como `TIMEOUT`.

Se ainda não houver resposta, testar inverter A/B apenas como diagnóstico de nomenclatura do módulo.

## CRC errado / dados ilegíveis

Verificar:

```text
38400
8N2
10 ms de silêncio
GND
ruído
comprimento do cabo
HW-519
```

## ESP32 reinicia / UART falha

Desconectar e verificar:

```text
HW-519 VCC
tensão TTL
GND
curto em TX/RX
```

## CN1 deixa de controlar corretamente

Não continuar alterando parâmetros.

Verificar:

```text
P1-01
P3-06
P2-10 ... P2-17
P2-36
```

---

# 22. Alternativa secundária: RS-232 + MAX3232

Caso o RS-485 não funcione ou seja necessário testar por outra via:

```text
CN3-2 RS232_TX -> MAX3232 -> ESP32 RX
CN3-4 RS232_RX <- MAX3232 <- ESP32 TX
CN3-1 GND      -> GND
```

O MAX3232 deve ser alimentado em 3,3 V quando compatível.

**Nunca conectar RS-232 diretamente ao ESP32-S3.**

---

# 23. Arquitetura final escolhida

```text
                 +-----------------+
controle atual ->| CN1             |
                 |                 |
                 |   DELTA ASDA-B2 |
                 |                 |
                 | CN3  (1394 6p)  |
                 +--+-----+-----+--+
                    |     |     |
                  1 GND  5 +   6 -
                    |     |     |          malha do cabo: NÃO conectar
                    v     v     v
                   GND   A+    B-
                    \     |     /
                   +---------------+
                   |    HW-519     |   R0 aberto (120 ohm desligado)
                   | RS485 <-> TTL |
                   +---+-------+---+
                    TXD|       |RXD
                       |       |
             GPIO18 (RX)       (TX) GPIO17
                       |       |
                   +---------------+
                   |   ESP32-S3    |
                   |               |
                   | UART0 43/44 --+--> entrada COM (console)
                   +---------------+
```

---

# 24. Próxima informação necessária

Para converter torque % em N·m:

```text
modelo exato do servomotor ECMA
```

Depois disso, o firmware poderá retornar diretamente:

```text
RPM
Torque %
Torque N·m
Power W
Energy J
Energy Wh
```
