# Diagnóstico dos toques fantasmas — C404

Criado em 2026-09-25. Sintoma: `▼` e `*` acionam sozinhos (SP muda, entra em menus), **sem
ESP32 e sem HW-280 no circuito**, com as chaves táteis removidas e pull-downs de 1 k/5,1 k nos
`CH`. Causa ainda não identificada.

## Contexto da placa

- CPU `034.00206 C504`: PIC16F76 (20 MHz), EEPROM 24LC16B (guarda SP e parâmetros),
  LM324 + HEF4051 na entrada do sensor. Etiqueta `C404-4PPS-0`: saídas de pulso 0/24 Vcc (SSR).
- CN2 (20 pinos, CPU ↔ display): `+5V`, 4 × `CH`, `PD A D F G B C E` (100 Ω, R46–R53),
  `1A 1B 1C 1D 1L` (Q9–Q13), `2DISP`, `0V`.
- Cada `CH` tem 1,5 kΩ em série (R54–R57) e 10 kΩ de pull-down (R58–R60; só três vistos).
- Provável: teclas compartilham pinos do PIC com a varredura do display. A tensão DC no `CH`
  medida com multímetro é média de onda quadrada e **não** indica o estado da tecla.
- O SP fica na EEPROM: sem os `CH`, o C404 continua controlando no último SP gravado.

## Hipóteses

1. Fuga na placa do display (umidade, resíduo de fluxo, laminado queimado, corrosão entre
   pads `+5V` e `CH`). Mais provável.
2. Pull-down de 10 kΩ aberto ou trincado na CPU (pino flutuando). Conserta-se com um resistor.
3. Pino do PIC danificado (ESD). Menos provável: 1,5 kΩ em série protege o pino.

## Passos

### 1. Secar
- C404 desligado e aberto, 24 h em lugar seco, ou secador em ar morno alguns minutos no CN2
  e nos pads das teclas.
- Se limpar de novo: isopropílico ≥ 99 % em abundância, escova macia, enxaguar com mais álcool,
  secar antes de ligar. Não usar álcool 70 % nem 92,8°.
- Ligar e observar 15–30 min sem tocar. Sumiu → era umidade; fim.

### 2. Inspecionar com lupa
Entre pads `+5V` e `CH`, dos dois lados do CN2: depósitos brancos/verdes/escuros, filamentos,
laminado escurecido.

### 3. Cortar os 4 pinos `CH` no CN2
- Tomada fora. Identificar os `CH` pela serigrafia da CPU; confirmar por continuidade com os
  pads das teclas.
- Cortar no meio do trecho exposto entre as placas, deixando toco dos dois lados.
- **Não cortar** `+5V` nem `PD` (vizinhos); conferir continuidade deles depois.

### 4. Medir (desligado)

| Medida | Esperado | Se diferente |
|---|---|---|
| Toco CPU ↔ `0V` | ~11–12 kΩ, igual nas 4 | aberto: 10 kΩ ao `0V` antes de ligar |
| Toco display ↔ `+5V` | aberto (OL) | fuga encontrada na placa do display |
| Toco display ↔ `0V` (sem pull-downs extras) | aberto (OL) | idem |

Colocar 1 kΩ de cada toco da CPU ao `0V`.

### 5. Observar ligado (15–30 min, sem tocar)
- **Toques somem** → problema na placa do display. Soldar relés nos tocos da CPU
  (`COM` no `+5V` do CN2, `NO` no toco). Firmware não muda.
- **Continuam** → problema na CPU. Medir R54–R57 e os 10 kΩ, comparando com `ENTER`/`▲`;
  modo diodo no pad do lado do PIC contra `0V` e `+5V` nas 4 linhas. Resistores bons e diodo
  diferente → pino do PIC.

## Se for o PIC

Não dá para trocar só o chip (firmware da Contemp, protegido). Alternativas para enviar SP:

- **Controlador 48×48 com RS-485/Modbus** no lugar do C404 (recorte 45×45 padrão): ESP32 +
  MAX485 escreve SP e lê PV. Mesmo sensor e SSR.
- **ESP32 como controlador**: sensor próprio + PID, comando em série com a saída do C404, que
  fica como display e limite (SP alto, `loC = 2`). Mexe na potência do banho.
- Reparo na Contemp (asstec@contemp.com.br, 11 4223-5125; contato do manual).

## Após qualquer resultado
Conferir no `ConF` os parâmetros que os toques fantasmas podem ter alterado: `in.tY`,
`in.L`/`in.H`, `P`/`I`/`d`, `C.t`, `A.C`, alarme, `loC`.
