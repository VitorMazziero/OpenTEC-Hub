# Software da integração de potência

Esta pasta separa o firmware que será instalado do material de diagnóstico.

## Estrutura

- `firmware-producao/ASDA_B2_Servo_Node/`: único candidato destinado ao
  ESP32-S3 ligado ao Delta ASDA-B2.
- `testes-bancada/esp32-s3/`: autoscan, scan, loopback, pin probe e outros
  sketches de diagnóstico para ESP32-S3. `ASDA_B2_Monitor_ESP32/` é o que fecha
  o Gate A: lê os registradores dinâmicos (P0-09, P0-10, P0-44, P0-46 e P0-01)
  pelo mesmo caminho Modbus do firmware de produção, sem subir Wi-Fi, e imprime
  as duas ordens de word possíveis para conferência contra o painel.
- `testes-bancada/arduino-uno/`: tentativas e diagnósticos feitos com UART de
  placas Arduino/UNO.
- `testes-bancada/pc/`: scanner Modbus executado no PC com dongle USB-RS485.
- `testes-bancada/legado-hw519/`: testes do transceptor HW-519 substituído; não
  representam a montagem final.
- `documentacao/`: diário de bring-up, configuração Modbus e plano histórico.

## Montagem vigente

```text
HW-097 novo
DI       <- GPIO17
RO       -> divisor 1 kΩ/2 kΩ -> GPIO18
DE + /RE <- GPIO16
A        -> CN3-5
B        -> CN3-6
VCC      -> 5 V da placa
GND      -> terra comum em estrela
```

Contrato serial confirmado: **9600 baud, 8N2, slave 1**.

Antes de gravar qualquer arquivo, confirme que ele está em
`firmware-producao`. Os arquivos em `testes-bancada` não são versões para
instalação. O status e os gates estão em
`../PLANO_INTEGRACAO_POTENCIA_OPENTECHUB.md`. A sequência de gravação das duas
placas e os critérios de aprovação estão em
`../PLANO_TESTES_DOIS_ESP32S3.md`.

Antes de qualquer leitura dinâmica, ajustar no painel do drive `P0-17 = 7`,
`P0-18 = 12` e `P0-19 = 11`. São registradores de monitor: sem esse mapeamento
eles respondem Modbus normalmente e devolvem outra grandeza.

**`P0-45` não se ajusta no painel** — ou melhor, ajusta, mas não adianta: ele é
volátil e volta a `0x0` a cada religamento do drive. Quem o mantém em 54 é o
firmware, por escrita `06H` confirmada por leitura, a cada amostra. Descoberto
em 2026-09-02; ver o diário em `documentacao/`.
