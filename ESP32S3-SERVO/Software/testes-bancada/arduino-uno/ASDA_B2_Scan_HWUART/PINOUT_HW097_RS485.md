# Pinout — HW-097 (RS-485) + Arduino UNO ↔ ASDA-B2 CN3

Ligações para a **Fase 3** do plano: subir o mestre Modbus RTU no
microcontrolador, com controle **manual** de direção (DE/RE), contra um drive
que já sabemos bom.

> **Contexto.** Em 2026-09-01 o dongle USB-RS485 (CH340, COM12) conversou com o
> drive na primeira combinação — 9600 8N2, endereço 1 — e leu todos os
> parâmetros. Ou seja: **o drive, a fiação do CN3 (5 = D+, 6 = D−) e o
> protocolo estão provados.** O único elo nunca validado é o nosso próprio
> microcontrolador fazendo half-duplex: temporização de UART + chaveamento de
> direção. É exatamente isso que o HW-097 vem fechar.

---

## O módulo HW-097

Transceptor RS-485 baseado em **MAX485**, **5 V**. Diferente do HW-519
(direção automática), ele expõe os quatro pinos TTL, o que nos dá controle
explícito do sentido — a razão de tê-lo trazido.

| Pino TTL | Nome | Sentido | Função |
|----------|------|---------|--------|
| `RO` | Receiver Output | saída → MCU | dados recebidos do barramento |
| `RE` | Receiver Enable | entrada ← MCU | **ativo em BAIXO**: LOW habilita a recepção |
| `DE` | Driver Enable | entrada ← MCU | **ativo em ALTO**: HIGH habilita a transmissão |
| `DI` | Driver Input | entrada ← MCU | dados a transmitir no barramento |
| `VCC` | — | — | 5 V |
| `GND` | — | — | terra lógico |

Lado do barramento: `A` (D+) e `B` (D−).

### A jogada de direção: amarrar RE e DE

`RE` é ativo-baixo e `DE` é ativo-alto. **Junte os dois num único fio** e ligue
a **um GPIO** (a "linha de direção"):

```
direção = LOW   ->  DE=0 (transmissor desligado) + RE=0 (receptor LIGADO)  = ESCUTANDO
direção = HIGH  ->  DE=1 (transmissor LIGADO)     + RE=1 (receptor desligado) = FALANDO
```

Repouso = **LOW** (escutando). O sketch só levanta a linha durante o quadro e
a baixa logo após o `Serial.flush()` — é o pino `DE_PIN`.

---

## Arduino UNO ↔ HW-097 (lado TTL)

| HW-097 | UNO | Observação |
|--------|-----|------------|
| `RO`  | `D0` (RX) | **soltar antes de cada upload** (é o RX do bootloader) |
| `DI`  | `D1` (TX) | UART de hardware |
| `DE` + `RE` (juntos) | `D2` | linha de direção — `DE_PIN = 2` no sketch |
| `VCC` | `5V` | |
| `GND` | `GND` | |

## HW-097 ↔ ASDA-B2 CN3 (lado RS-485)

| HW-097 | CN3 | Confirmado |
|--------|-----|------------|
| `A` (D+) | `CN3-5` | ✔ dongle conversou nesta polaridade |
| `B` (D−) | `CN3-6` | ✔ |
| `GND` (⇒ UNO GND) | terra da placa do **CN1** | referência de modo comum |

> **O fio de terra até o CN1 é obrigatório na bancada.** O UNO alimentado pelo
> USB do notebook flutua em relação ao drive; sem essa referência, o modo comum
> do RS-485 fica indefinido. (Na instalação final isso some — ver adiante.)

---

## Diagrama

```
   ARDUINO UNO                    HW-097 (MAX485, 5V)              DELTA ASDA-B2
   (USB do PC)                                                         CN3
  ┌───────────┐                  ┌────────────────┐              ┌────────────┐
  │       D1  │───── TX ────────▶│ DI          A ●│────── D+ ───▶│ 5  (D+)    │
  │       D0  │◀──── RX ─────────│ RO          B ●│────── D− ───▶│ 6  (D−)    │
  │       D2  │───── dir ───┬───▶│ DE             │              │            │
  │           │             └───▶│ RE̅  (ativo-baixo)             │            │
  │       5V  │─────────────────▶│ VCC            │              │            │
  │      GND  │──────────┬──────▶│ GND            │              │            │
  └───────────┘          │       └────────────────┘              │            │
                         └───────────────── terra p/ CN1 ───────▶│ CN1 GND    │
                                                                 └────────────┘
  ▲ soltar o fio de D0 antes de cada upload; recolocar depois.
```

---

## Terminação de 120 Ω

Cabo curto de bancada: **dispensável** — comece sem. Se a Fase 3 vier com
"lixo" ou bytes intermitentes (nunca com silêncio), aí sim ponha 120 Ω entre
`A` e `B` no lado do módulo. Muitos HW-097 já trazem esse resistor embutido;
confira antes de somar outro.

---

## Sequência de bring-up

1. **`DE_PIN = 2`** já está no sketch `ASDA_B2_Scan_HWUART.ino`.
2. **Solte o fio de D0**, faça o upload, **recoloque D0**. (D0/D1 são
   compartilhados com o USB — é o preço da UART de hardware no UNO.)
3. Abra o monitor serial a **115200**.
4. **Teste 1 (eco):** com `A`/`B` **soltos** do CN3. Agora é conclusivo,
   porque nós controlamos o `RE` — deve dar **silêncio** (sem eco espúrio).
   Se aparecer eco com A/B no ar, a direção está errada (RE/DE trocados).
5. Ligue `A→CN3-5`, `B→CN3-6`, terra→CN1.
6. **Teste 3 (baud × formato):** deve travar em **9600 8N2**, endereço 1,
   `P3-00 = 0x0001` — reproduzindo o que o dongle já fez.
7. **Teste 5:** confirma os parâmetros. Bateu com o dongle → a cadeia
   embarcada está validada, e a Fase 3 do plano de integração está liberada.

Alvo: fazer o UNO reproduzir exatamente o que o COM12 fez.

---

## Quando for para o nó final (ESP32-S3)

O plano de integração (`ASDA_B2_Integracao_v7_PLANO.md`) mira um parser
ESP32-S3 em **UART1 GPIO17/18**. O mapeamento é o mesmo, com **uma ressalva de
tensão**:

| Sinal | ESP32-S3 | Cuidado |
|-------|----------|---------|
| `DI`  | `GPIO17` (TX1) | 3,3 V aciona o DI do MAX485 sem problema |
| `RO`  | `GPIO18` (RX1) | ⚠ **RO sai em 5 V** — excede o limite de 3,3 V do ESP32 |
| `DE`+`RE` | `GPIO16` (ex.) | linha de direção |
| `VCC` | `5V`/`VIN` | o MAX485 precisa de ≥ 4,75 V |
| `GND` | `GND` | compartilhado com a placa (vem de graça — ver abaixo) |

> **O HW-097 é 5 V.** Para o UNO ele encaixa direto. Para o ESP32-S3, o `RO`
> em 5 V pode danificar a entrada de 3,3 V: use um divisor (ex. 1 kΩ/2 kΩ) no
> `RO`, ou troque por um transceptor de 3,3 V (MAX3485 / SP3485). Não ligue o
> `RO` de 5 V direto no GPIO18.

> **Terra na instalação final.** Alimentando o ESP32-S3 pelos 5 V da placa
> controladora (Fase 1 do plano), o GND do ESP32 já é o mesmo nó do `CN3-1`.
> A referência de modo comum vem **pelo próprio fio de alimentação** — sem o
> fio de terra separado que a bancada exige.
