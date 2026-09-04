# Diagnóstico da camada física RS-485 — Delta ASDA-B2

Sketch de bancada para descobrir **onde** a comunicação está parando. Ele não
assume endereço, baud nem formato — varre os três.

É somente-leitura: o único código Modbus transmitido é `03H`. Nunca escreve
parâmetro, nunca habilita o servo, nunca comanda o motor.

Use este sketch **antes** de `ASDA_B2_RS485_First_Test`. Aquele reporta toda
falha como `TIMEOUT`; este mostra os bytes crus, o que separa três
diagnósticos completamente diferentes:

```text
silêncio   nenhum byte chegou ao GPIO de RX
eco        voltaram os 8 bytes do próprio pedido
lixo       chegaram bytes, mas não formam quadro válido
```

## Configuração da Arduino IDE

```text
Board:              ESP32S3 Dev Module
USB Mode:           Hardware CDC and JTAG
USB CDC On Boot:    Disabled
Upload Mode:        UART0 / Hardware CDC
Monitor Serial:     115200 baud
```

Computador no conector **`UART` / `COM`** da placa.

## Ligações

```text
ESP32-S3 GPIO17  -> RXD do HW-519
ESP32-S3 GPIO18  <- TXD do HW-519
ESP32-S3 3V3     -> VCC do HW-519
ESP32-S3 GND     -> GND do HW-519

HW-519 A+   -> CN3-5 RS-485(+)
HW-519 B-   -> CN3-6 RS-485(-)
HW-519 GND  -> CN3-1 GND
HW-519 R0   -> não conectar (terminador de 120 Ω)
malha/dreno -> não conectar
```

`UART0` (`GPIO43`/`GPIO44`) fica reservado ao console. **Não use esses dois
pinos para o RS-485**: eles estão ligados ao chip USB-serial da placa, que
briga com o `TXD` do HW-519 e mata a recepção.

## Menu

Digite a tecla no Monitor Serial.

| Tecla | Teste |
|-------|-------|
| `1` | eco — com `A`/`B` desconectados do drive |
| `2` | varre os endereços `0x01`..`0x7F` no baud/formato atual |
| `3` | varre 6 bauds × 3 formatos nos endereços `0xFF`, `0x01` e `0x7F` |
| `4` | sondagem repetida — para inverter `A`/`B` com o teste rodando |
| `5` | lê `P3-00`, `P3-01`, `P3-02`, `P3-05`, `P3-07`, `P1-01`, `P3-06` |
| `b` | próximo baud rate |
| `f` | próximo formato (`8N2` / `8E1` / `8O1`) |
| `a` | próximo endereço de slave |
| `w` | endereço curinga `0xFF` |
| `v` | liga/desliga o despejo de bytes na varredura |
| `m` | mostra o menu |

Todos os testes usam `P3-00` (`0x0300`) como registrador de sondagem: toda
unidade ASDA-B2 responde a ele, e o valor lido **confirma em que endereço o
drive está configurado**.

## O endereço curinga `0xFF`

A seção 8.2 do manual, na descrição do `P3-00`, diz textualmente que com o
pedido endereçado a `0xFF` o drive responde **seja qual for** o endereço
configurado nele. Isso tira o endereço da lista de incógnitas: se `0xFF` calar,
o problema não é endereço, ponto final.

O manual não promete com que endereço o drive responde nesse caso, então a
classificação aceita qualquer byte de endereço na resposta.

## Um falso positivo que foi corrigido

O eco do próprio pedido é um quadro **CRC-válido** quando lido como resposta: o
pedido `03H` de 1 registrador tem 8 bytes e, interpretado como resposta, dá
`byte count = 0x03`, quadro de 8 bytes, CRC nos bytes 6 e 7 — exatamente o CRC
do pedido. Confere sempre.

A versão anterior de `classifyReply()` procurava o quadro antes de checar o
eco, então um módulo que ecoasse teria reportado `RESPOSTA P3-00=0x0000`. Agora
o eco é descartado primeiro. Isso não afeta os resultados já obtidos — todos
foram `silêncio`, e silêncio nunca passou por esse caminho.

## Ordem sugerida

### Comece pelo teste 3

Ele cobre de uma vez as duas hipóteses de padrão de fábrica, sem exigir
nenhuma alteração no drive:

```text
P3-00 vem de fábrica em 0x007F (127), não em 0x0001
P3-05 vem de fábrica em 1 (RS-232 dedicado ao ASDA-Soft), não em 0
```

Baud e formato já saem de fábrica nos valores deste projeto (`0x0033` e
`0x0066`), então se algo responder, vai responder em 38400 8N2.

### Se o teste 3 der silêncio em tudo

Rode o teste 1 com `A`/`B` **desconectados do drive**. Ele corta o espaço de
busca ao meio:

- **Voltaram bytes** → o caminho `ESP32 ↔ HW-519` está bom. O problema está no
  drive, no conector CN3 ou na polaridade `A`/`B`.
- **Não voltou nada** → o problema está entre o `TXD` do módulo e o `RX` do
  ESP32. Verifique os GPIOs, a alimentação de 3,3 V e a solda.

Ressalva: nem todo módulo com direção automática ecoa. Se este não ecoar, o
teste 1 é inconclusivo — e o teste 4 com inversão de `A`/`B` vira o próximo
passo.

### Depois que algo responder

O teste 5 despeja os parâmetros de comunicação do drive. Anote-os, ajuste
`P3-00 = 0001` e `P3-05 = 0` pelo painel, faça power-cycle (`P3-00` só entra
em vigor ao reenergizar) e volte para `ASDA_B2_RS485_First_Test`.
