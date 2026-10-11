# Changelog — Bomba peristáltica

## 3.12+dose2 — 2026-10-11 (dosagem por pulsos, watchdog e timeout HTTP)

- **Dosagem por pulsos** para volumes muito pequenos (ex.: 50 mL em 12 h), sem usar a calibração polinomial: o operador mede a vazão (mL/min) numa velocidade fixa e o nó liga o motor por `volume / vazão` a cada período. Comando: `{"dose_ml":0.35,"dose_period_s":300,"dose_speed":120,"dose_rate_mlmin":5.4,"dose_total_ml":50}` e `{"dose":1}` / `{"dose":0}`.
- O fim do pulso é aplicado pela tarefa do motor no core 0, então o HTTP bloqueante do loop (Hub mudo) não alonga o pulso. O volume entregue conta o tempo em que o motor girou de fato (sensor de líquido seco não conta).
- Estado em chaves NVS próprias (`d_*`); após reinício a dosagem retoma 5 s depois do boot, mantendo o volume já entregue. `start`, `stop`, `mode`, `speed` e a gravação OTA cancelam ou interrompem a dosagem.
- Telemetria USB e `/readData`: `dose_active`, `dose_pulse`, `dose_ml`, `dose_period_s`, `dose_speed`, `dose_rate_mlmin`, `dose_total_ml`, `dose_delivered_ml`, `dose_pulses`, `dose_next_s` e `fw_build`. O push ao Hub e o contrato 3.12 não mudam (o Hub e o app ainda não repassam os comandos `dose_*`: use USB ou `POST /command` no AP `FeedPump`).
- O volume dosado entra no contador de sessão (`vol` / `cum_volume_ml`), que o Hub republica como `PumpVol` e o app mostra; `cyc_vol` conta desde o início da dosagem. Soma-se por pulso (a integração pela calibração polinomial fica desligada durante a dosagem, para não contar duas estimativas) e `reset_volume` continua zerando só o contador de sessão.
- Corrigido: o watchdog da tarefa principal valia 5 s (o core 3.x já inicia o TWDT e `esp_task_wdt_init` falhava), então hello + consulta + envio com o Hub lento reiniciavam a bomba. Agora o limite de 15 s é reaplicado com `esp_task_wdt_reconfigure` e a conexão HTTP tem limite de 1 s (`setConnectTimeout`).
## 3.12 — 2026-09-13 (calibração polinomial em duas faixas)

- Novo registro NVS `pump_poly_cal` com os oito coeficientes, `St` e CRC32, sem alterar o layout `PumpConfig` 3.10.
- Conversões `S → Q` usam quarto grau abaixo de `St` e quadrático acima, com C0+C1; `Q → S` usa bisseção.
- Aplicação atômica exige os oito coeficientes e `St`, curva monotônica/não negativa e bomba ociosa.
- Primeira implantação sem calibração presumida: não há migração nem fallback linear. Sem `pump_poly_cal` atual e válido, as conversões Q↔S ficam bloqueadas até a primeira curva aplicada.
- Push, `/readData` e Hub 10.4 expõem os nove ecos e `cal_crc`/`PumpCalCrc`.
- Perfis de mangueira são arquivos locais do aplicativo; somente a curva explicitamente enviada fica ativa no nó.
- Integração automatizada aprovada; calibração com mangueiras e recipiente graduado permanece pendente.

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
