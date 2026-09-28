# Banho termostático (Contemp C404 + ESP32-S3)

Versão ativa: **r3.2** (`BathClient r3.2`). Placa de compilação: **ESP32-S3** (`esp32:esp32:esp32s3`).

**Estado (2026-09-28): funcionando na bancada.** O nó comanda o SP pelas teclas, lê PV e SP do
display, e no modo automático reverte mudanças manuais sem reagir a leituras ruins. Resultados,
configuração em uso e pendências (G4, G5, G10, teste de ruído longo) estão em
`docs/CURRENT_STATUS.md`.

- Firmware: `firmware/thermostatic-bath`
- Aplicativos de bancada (falam HTTP direto com o nó, sem o Hub):
  - Desktop Python: `apps/desktop-python/bath_app.py` (Tkinter + urllib, sem dependências)
  - Android (Flutter): `apps/flutter` (`bath_app`) — mesmas funções em celular
- Plano de hardware original: `plano_controle_remoto_C404_ESP32S3.md`
- Documentação: `docs/` (arquitetura, protocolo, hardware, validação, estado atual)

## O que o nó faz

O ESP32-S3 "aperta" as teclas `*`, `▲`, `▼` e `ENTER` do C404 por meio de quatro relés
(HW-280) ligados em paralelo aos botões físicos. O usuário envia um setpoint absoluto
(ou um ajuste relativo) por Wi-Fi; o firmware calcula quantos toques faltam a partir do
setpoint que acredita estar no C404 (o **setpoint-sombra**) e executa a sequência
`* → ▲/▼ × N → ENTER` sem bloquear o servidor HTTP nem o watchdog. No modo display, uma
distância grande é vencida **mantendo** a seta enquanto o display é lido ao vivo (o C404
auto-repete a tecla mantida, muito mais rápido do que toques), soltando perto do alvo e
terminando a toques; ver `docs/PROTOCOL.md` §3.1.

Como os relés ficam em paralelo com as teclas, o operador continua podendo mudar o SP no
painel. Dois modos (`docs/PROTOCOL.md` §3.2): **manual** só reporta o desvio em relação ao
último SP comandado; **automático** reverte a mudança depois que um toque físico é visto e o
painel fica parado (10–20 s, com o valor confirmado em duas leituras). O modo
troca pelo app, pelo Hub ou mantendo `▲`+`▼` por 1 s no painel (com o sensoriamento das setas,
`docs/WIRING.md` §3b).

Duas fontes da verdade para o setpoint, selecionáveis por `sp_source`:

| `sp_source` | Fonte | Quando usar |
|---|---|---|
| `0` sombra | Contagem de toques persistida na NVS, validada pelo usuário (`sync_sp`) ou pelo `home` | Antes de caracterizar o display; depende de ninguém mexer nas teclas sem o nó ver |
| `1` display | Decodificação do display inferior do C404 (7 segmentos multiplexados) | Modo definitivo: absorve intervenção manual, reboot e toques perdidos; verifica cada sequência |

O sensoriamento opcional das teclas (`sense_enabled`) detecta toques manuais e, no modo
sombra, invalida o setpoint-sombra; no modo display serve para confirmar que cada toque do
relé realmente fechou a linha da tecla.

## Começar

1. Compile e grave por USB **com o ESP32 desconectado do C404** (`docs/HARDWARE.md` explica
   por que o USB e o GND do C404 não podem coexistir):

   ```powershell
   External-Devices\tools\Publish-OtaFirmware.ps1 bath -Compile
   ```

   gera `tools\.build\bath\thermostatic-bath.ino.bin`; na primeira gravação use a
   Arduino IDE/`arduino-cli upload` pela USB. A partir daí, OTA em `http://192.168.8.1/update`.
2. Conecte ao AP `Banho Termostatico` (sem senha) e abra `http://192.168.8.1/ui` no celular,
   rode `python apps\desktop-python\bath_app.py` no PC, ou instale o app Android
   (`apps\flutter`, veja o README de lá).
3. Siga `docs/VALIDATION.md` na ordem: relés fora do C404 → um relé em paralelo a uma tecla →
   sequência completa → caracterização do display.

Leia `docs/CURRENT_STATUS.md` antes de gravar hardware e `docs/PROTOCOL.md` antes de tocar
em comunicação. O firmware r3.2 envia `/bathData` por uma tarefa própria e aceita comandos
idempotentes por carona. O Hub 10.6 possui o handler e exige `ver=r3.2` ou superior;
enquanto a cascata do Hub é dona do banho, a API local só aceita `abort`/`stop`;
`hub_enabled = 0` permanece como padrão seguro.
As mudanças futuras estão detalhadas em `../docs/Planos/IMPLEMENTATION_PLAN_BANHO_HUB.md` e
`../docs/Planos/IMPLEMENTATION_PLAN_BANHO_WINDOWS_APP.md`. O aplicativo Android próprio do nó
já está implementado em `apps/flutter`; seu plano/registro é
`../docs/Planos/IMPLEMENTATION_PLAN_BANHO_APP_ANDROID.md`.
