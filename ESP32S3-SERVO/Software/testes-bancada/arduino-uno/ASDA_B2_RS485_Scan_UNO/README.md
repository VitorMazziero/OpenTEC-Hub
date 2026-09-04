# Diagnóstico RS-485 no Arduino UNO — Delta ASDA-B2

Segundo mestre, independente do ESP32-S3: outro silício, lógica de 5 V, outra
biblioteca serial. Existe para responder **"o mestre é o problema?"** sem
reaproveitar nada do que já foi testado.

Somente leitura: o único código Modbus transmitido é `03H`. Nunca escreve
parâmetro, nunca habilita o servo, nunca comanda o motor.

## Ligações

```text
Arduino D10 ---> RXD do HW-519      D10 é a SAÍDA do Arduino
Arduino D11 <--- TXD do HW-519      D11 é a ENTRADA do Arduino
Arduino 5V  ---> VCC do HW-519
Arduino GND ---> GND do HW-519 (lado TTL)

HW-519 A+   -> CN3-5 RS-485(+)
HW-519 B-   -> CN3-6 RS-485(-)
HW-519 R0   -> não conectar (terminador de 120 Ω)
```

Daí `SoftwareSerial rs485(11, 10)`: a assinatura é `(rxPin, txPin)`, e o pino
que alimenta o `RXD` do módulo é o **TX** daqui.

O módulo vai em **5 V**, não em 3,3 V. É de propósito — é a única variável
física que muda em relação ao ESP32-S3. Se o UNO conversar e o ESP32 não, a
suspeita cai sobre a excursão diferencial do driver alimentado em 3,3 V.

> **Ao voltar para o ESP32-S3, devolva o `VCC` do módulo para `3V3`.** Em 5 V
> o `TXD` do módulo entrega 5 V, e o GPIO do ESP32 é de 3,3 V.

Monitor Serial em **115200**.

## Antes de rodar qualquer teste: a referência

Os dois `GND` do módulo — o do header TTL e o do bloco RS-485 — **não apitam na
continuidade**, e o mesmo acontece em duas placas diferentes. Não é defeito de
uma unidade: é projeto. Isso derruba a seção 3.1 do documento de setup, que
afirmava que os dois pinos são o mesmo net e "deve bipar".

A explicação mais provável é a mais banal: o borne do lado RS-485 chega ao terra
lógico através de um **resistor em série, tipicamente de 100 Ω**. Não é
economia de fabricante — é o que a própria TIA/EIA-485 recomenda para ligar
terras de sinal entre nós, limitando a corrente de circulação. O apito de
continuidade dispara em torno de 50 Ω, então ele fica mudo num resistor de
100 Ω sem que haja nada de errado.

**Meça em ohms, não no apito.** Entre o `GND` do lado TTL e o borne do lado
RS-485:

| Leitura | Significado | O que fazer |
|---|---|---|
| 0 – 5 Ω | mesmo cobre | era o que o documento supunha |
| 100 – 1000 Ω | resistor em série | normal; use o borne como está |
| `OL` / megaohms | isolado, ou não é GND | ver abaixo |

Se der `OL`, olhe a placa: módulo isolado tem um **bloco DC-DC preto marcado
`B0505S`** e um rasgo fresado atravessando a PCI. Sem esses dois sinais, aquele
borne não é o terra.

**Meça também o borne contra o `VCC` do lado TTL.** Se der perto de zero, o
borne é `VCC`: ligado ao `CN3-1` ele estaria injetando 5 V no terra de sinal do
drive. Desligue antes de energizar de novo.

Enquanto a dúvida existir, a referência confiável é o **`GND` do lado TTL** — o
mesmo pino onde está o `GND` do Arduino. Leve o `CN3-1` até lá. Sem referência
comum o modo comum entre as duas pontas fica solto, o receptor sai da janela de
entrada, e o sintoma é exatamente o que você está vendo: silêncio absoluto com
`A` e `B` aparentemente corretos.

## O que a SoftwareSerial não faz

Ela transmite **exclusivamente em 8N1**: não gera segundo stop bit nem bit de
paridade. O ASDA-B2 só fala RTU em `8N2`, `8E1` ou `8O1` (`P3-02` = 6, 7 ou 8).

- **8N2** dá para produzir. `writeFrameAs8N2()` insere uma pausa de dois tempos
  de bit com a linha em repouso depois de cada byte; o receptor em 8N2 lê esse
  tempo ocioso como o segundo stop bit. A pausa fica muito abaixo do limite de
  1,5 caractere que encerraria o quadro RTU — 52 µs contra ~430 µs em 38400.
- **8E1 e 8O1** não dão, de jeito nenhum. Se o painel mostrar o dígito RS-485 do
  `P3-02` em 7 ou 8, mude para 6 pelo painel. É parâmetro de comunicação,
  seguro de mexer — ao contrário de `P1-01` e `P3-06`.
- Ela também **desliga as interrupções enquanto transmite**, então é surda
  durante a própria transmissão. Não há teste de eco aqui.
- Acima de 38400 ela fica instável num UNO de 16 MHz. A lista de bauds
  para em 38400.

## Menu

| Tecla | Teste |
|-------|-------|
| `1` | transmite 10 s seguidos — para medir `A`−`B` e ver o LED |
| `2` | varre os endereços `0x01`..`0x7F` no baud atual |
| `3` | varre os bauds nos endereços `0xFF`, `0x01` e `0x7F` |
| `4` | sondagem repetida — para inverter `A`/`B` com o teste rodando |
| `5` | lê `P3-00`, `P3-01`, `P3-02`, `P3-05`, `P3-07`, `P1-01`, `P3-06` |
| `6` | escuta passiva por 5 s — não transmite nada |
| `b` | próximo baud rate |
| `a` | próximo endereço de slave |
| `w` | endereço curinga `0xFF` |
| `v` | liga/desliga o despejo de bytes |
| `m` | mostra o menu |

## O endereço curinga `0xFF`

A seção 8.2 do manual, na descrição do `P3-00`, diz textualmente que com o
pedido endereçado a `0xFF` o drive responde **seja qual for** o endereço
configurado nele. Isso tira o endereço da lista de incógnitas: se `0xFF` calar,
o problema não é endereço, ponto final.

O manual não promete com que endereço o drive responde nesse caso, então a
classificação aceita qualquer byte de endereço na resposta. E o valor lido de
`P3-00` entrega o endereço real do drive de brinde.

## Ordem sugerida

1. **Medir a referência** (seção acima). É a única pendência física conhecida.
2. **Teste 1.** O LED do módulo tem de piscar e `A`−`B` tem de sair de zero.
   Se o LED não piscar, o `D10` não está chegando ao `RXD`.
3. **Teste 3.** Silêncio em `0xFF` em todos os bauds descarta baud e endereço
   de uma vez.
4. **Teste 4** invertendo `A` e `B` com o teste rodando.
5. Se tudo calar, o problema não está mais do lado do mestre: vá ao painel do
   drive e leia `P3-00`, `P3-01`, `P3-02` e `P3-05`. O manual lista esses
   quatro como essenciais para a comunicação, e dois deles saem de fábrica em
   valores que não servem (`P3-00` = `0x7F`, `P3-05` = `1`).

## Comparação com o ESP32-S3

O sketch `ASDA_B2_RS485_Scan` cobre mais: seis bauds contra quatro, três
formatos contra um, e tem teste de eco de verdade porque a UART do ESP32 é
full-duplex por hardware. Este aqui não substitui aquele — serve para
**cruzar** o resultado com outro mestre. Se os dois calarem no mesmo ponto, o
mestre está descartado.
