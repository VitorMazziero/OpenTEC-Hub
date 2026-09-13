# Changelog — Sensor de biomassa

## v11.1 — 2026-09-13 (auditoria B01–B15)

Plano: `../docs/Planos/IMPLEMENTATION_PLAN_BIOMASSA.md`; catálogo: `../docs/COMANDOS_DISPOSITIVOS_EXTERNOS.md` §4.10.

- **B03/B04** `set_gear` passa a ser marcha travada: desliga o auto-range (persistido),
  zera o modo de alta densidade, apaga o LED imediatamente (antes ficava aceso até o
  próximo pulso em MEASURING) e reprograma a próxima leitura pelo piso térmico.
  `start` só executa Smart Start com auto-range ligado ou marcha sem branco válido.
- **B05** `low`/`high`/`opt` e `probe_period` gravam NVS quando o valor muda.
- **B07** Versão `v11.1` no serial, `/nodeHello`, `/diag` e página OTA.
- Contrato de fio inalterado (o Hub 10.2 passou a rotear `auto`/`manual`, que o nó já
  entendia). Compilado (ESP32 core 3.3.11): 1 129 932 B (86 %), 69 984 B de RAM.
- Fora do escopo por decisão: B02 (rotinas bloqueantes servirem o Hub) e B14 (ecos
  `hd_mode`/`manual`/`boot_id`) — custo de flash a 5,1 kB do piso.

## Reorganização de 2026-09-11

- Selecionada e nomeada a versão ativa v5.3.
- Separados firmware, aplicativo, hardware, testes/evidências e histórico.
- Preservado o monólito ativo anterior em `archive/active-baseline`.
- Removidos cabeçalhos extensos e históricos embutidos do código ativo; decisões foram transferidas para Markdown.
- Criada estrutura modular por responsabilidade sem alteração intencional do contrato de fio.
- Compilado no ESP32 core 3.3.11 usando as bibliotecas compartilhadas da máquina de upload.

Validação de hardware permanece pendente.
