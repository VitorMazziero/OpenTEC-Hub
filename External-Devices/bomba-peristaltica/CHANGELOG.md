# Changelog — Bomba peristáltica

## 3.10 — 2026-09-12 (PID ecoado, parada sem zerar, potenciômetros, speed_ms)

Decisões do operador registradas em `../docs/COMANDOS_DISPOSITIVOS_EXTERNOS.md` §1.10.

- Push e `/readData` ecoam `kp`, `ki`, `kd`, `pot` e `cyc_vol`; o Hub 10.2 republica como
  `PumpPidKp/Ki/Kd`, `PumpPotEnabled`, `PumpCycleVol` e o app libera a edição do PID quando o eco existe.
- `stop`, `mode:0`, troca de perfil e fim de `final_t` **não zeram mais `vol`**: o volume passou
  a ser um contador de sessão; cada ciclo guarda o volume em que começou (`g_cycleStartVolumeMl`,
  também no checkpoint `s_cvol`) e o PID fecha sobre `vol − início`. Só `reset_volume` zera.
- `pot:1` devolve o motor aos potenciômetros e esquece `speed`; `pot:0` trava. Antes, um `speed`
  deixava os potenciômetros mortos até o reboot.
- `speed_ms`: prazo opcional para `speed`; ao expirar o nó zera a velocidade sozinho.
- `speed` passa a ser limitado a ±1000.
- Compilado (ESP32 core 3.3.11): 1 096 479 B (83%), 55 076 B de RAM.

Compatibilidade: chaves novas são opcionais; um Hub anterior ignora os parâmetros extras do push.
Bancada pendente: o comportamento de não zerar ao parar e a retomada após queda de energia com
`s_cvol` precisam de ensaio antes de operar com receitas longas.

## Reorganização de 2026-09-11

- Selecionada e nomeada a versão ativa v4.
- Separados firmware, aplicativo, hardware, testes/evidências e histórico.
- Preservado o monólito ativo anterior em `archive/active-baseline`.
- Removidos cabeçalhos extensos e históricos embutidos do código ativo; decisões foram transferidas para Markdown.
- Criada estrutura modular por responsabilidade sem alteração intencional do contrato de fio.
- Compilado no ESP32 core 3.3.11 usando as bibliotecas compartilhadas da máquina de upload.

Validação de hardware permanece pendente.
