# Changelog — Sensor de distância

## 2026-09-13 — auditoria D01–D09 (v11)

Plano: `../docs/Planos/IMPLEMENTATION_PLAN_DISTANCIA.md`; catálogo: `../docs/COMANDOS_DISPOSITIVOS_EXTERNOS.md` §2.10.

- **D01** Amostragem e envio ao Hub viram laços independentes; o push leva a última
  distância válida e o `time` do envio.
- **D02** `cmd_id` só é confirmado depois que ao menos uma chave válida foi aplicada
  (ou `reset_nvs:1`); payload sem chave conhecida não gera `ack_cmd_id`.
- **D04** Página OTA mostra `BoardConfig::FirmwareTag` em vez de `r10` fixo.
- **D05** `l1_reinit` 1–50, `l2_clear` 1–100, `l3_xshut` 1–200.
- **D06** `POST /config` responde 400 quando nenhuma chave válida é reconhecida.
- **D09** Escada de recuperação I²C: o degrau é escolhido pelo `failStreak`
  (L3 → L2 → L1) e o cooldown aplicado é o desse degrau. Antes L1 interceptava sempre;
  só inverter a ordem também não bastava (L2 recarregava o `lastRecovery` antes de L3
  vencer). Log serial passa a indicar nível e streak.
- Compilado (ESP32 core 3.3.11): 1 100 796 B de flash (83%), 50 680 B de RAM (15%).
- Contrato de fio inalterado; `docs/PROTOCOL.md` atualizado (`ota` booleano, D02, D05, D06).
- D03 (janela de presença do Hub vs `send_period`) e D08 (`lastGoodRawMm`) ficaram
  como decisão de projeto.

## 2026-09-12 — endurecimento do canal de configuração (v11)

- `processConfigUpdate` ignora reentrega da mesma `cmd_id` (o Hub reenvia até ver o
  ack no push seguinte); antes cada reentrega reaplicava e regravava a NVS, e para
  `reset_nvs` reexecutava o reset.
- Grava na NVS só quando algum valor mudou; comando com valores já vigentes é sucesso
  sem escrita em flash.
- `sample_period`/`send_period` passam a exigir $[100, 60000]$ ms também por
  `POST /config` e serial, como o `PROTOCOL.md` §4.2 já afirmava e o Hub já aplicava;
  antes o nó aceitava qualquer valor $> 0$ por esses caminhos.
- Compilado (ESP32 core 3.3.11): 1 100 488 B de flash (83%), 50 680 B de RAM (15%).
- Fora do escopo, por decisão: mover HTTP/OTA para tarefa FreeRTOS em outro core. O
  bloqueio de até 2,5 s do `httpGet` só ocorre com o Hub inalcançável, situação em que
  nada consome as amostras além do `/` local; com o Hub respondendo o push leva dezenas
  de ms. O custo (mutex em ~20 globais compartilhados) não paga o benefício hoje.

## Reorganização de 2026-09-11

- Selecionada e nomeada a versão ativa v10.
- Separados firmware, aplicativo, hardware, testes/evidências e histórico.
- Preservado o monólito ativo anterior em `archive/active-baseline`.
- Removidos cabeçalhos extensos e históricos embutidos do código ativo; decisões foram transferidas para Markdown.
- Criada estrutura modular por responsabilidade sem alteração intencional do contrato de fio.
- Compilado no ESP32 core 3.3.11 usando as bibliotecas compartilhadas da máquina de upload.

Validação de hardware permanece pendente.
