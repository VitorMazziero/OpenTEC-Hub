# Changelog — Sensor de distância

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
