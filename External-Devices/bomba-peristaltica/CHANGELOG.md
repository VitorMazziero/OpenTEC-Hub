# Changelog — Bomba peristáltica

## 3.13 — 2026-10-11 (contrato de integração com o Hub)

Binários prontos para a bancada em `tests/evidence/fw-3.12.1` e `tests/evidence/fw-3.13` (SHA-256 ao
lado). Ordem de gravação: 3.12.1 → Hub 10.9.0 → 3.13 → app 0.32.0. Portões G0–G3 em
`docs/BANCADA_3.13.md`; a versão só é dada como validada depois deles.

Hub 10.9 aproveita tudo; com o Hub 10.8 os comandos existentes continuam funcionando (os campos novos são ignorados e `dose_*` não é repassado).

- **Recusa visível:** o quadro inteiro é validado antes de aplicar (`control/FrameValidate.h`). Recusado, nada é aplicado, não há ACK e o push publica `rej_cmd_id`/`rej_err` (códigos em `docs/PROTOCOL.md` §3.1, padrão do banho r3.2). Reentregas do id recusado não são reprocessadas. `POST /command` responde `422`.
- **Push 3.13:** `drv` (quem comanda o motor), `sens`/`wet` (sensor de líquido), `t_min` (relógio do perfil), `up`/`boot`/`rst` (reinícios) e a dosagem `d_*` com eco dos parâmetros, entregue, pulsos, próximo pulso e `d_short`. `pot` é `0` durante a dosagem. Os coeficientes da curva saem a cada 10 pushes ou quando o CRC muda; buffer de 1024 com descarte e contagem de push cortado.
- **Resposta do push:** um comando devolvido pelo Hub na resposta do `/pumpData` é aplicado pelo mesmo caminho do poll.
- **Dosagem:** `dose_dead_ms` (atraso de partida compensado, NVS `d_dead`); parte útil mínima de 200 ms (antes 500 ms) e folga de 0,5 s (antes 1 s); prazo do pulso armado no core 0 quando o motor liga; pulso perdido para o sensor de líquido vai para `d_short`.
- **`speed` vindo do Hub sem `speed_ms`** para sozinho em 300 s. USB e HTTP local continuam sem prazo.
- **Versão única** (`PUMP_FW_VERSION`) no banner, hello, `/diag` e página OTA. `/diag` ganha `build`, `boot`, `rst`, `drv`, `last_cmd_id`, `rej_*`, `cal_crc`, dosagem e `push_trunc`, com teto de 511 bytes (cache do Hub); saem `wifi_status`, `ota`, `op_state` e `flow`.
- Leitura de JSON, regras da dosagem e validação do quadro em cabeçalhos sem Arduino, testados no PC (`tests/host-sim`, 49 verificações).
- Compilado (ESP32 core 3.3.11): 1 111 207 B (84%), 55 332 B de RAM.

## 3.12.1 — 2026-10-11 (correções de integração com o Hub; contrato inalterado)

- **Corrigido (crítico):** o `cmd_id` dos comandos do Hub era lido como `float`. O Hub numera a partir de uma base aleatória ≥ 100 000 000 por boot, e um `float` só guarda inteiros exatos até 16 777 216: o ACK nunca batia com o Hub (`PumpCommandPending` preso) e ids vizinhos colidiam como "já aplicado", descartando comandos. Agora é lido como inteiro de 32 bits (`parseJsonUint32`).
- **Corrigido:** um reinício fora de perfil zerava o volume de sessão. O contador passa a ser gravado em qualquer estado (`s_sess`; variação ≥ 0,05 mL e ≥ 30 s, e sempre em parada, fim de perfil, `reset_volume`, fim da dosagem e OTA) e é restaurado em todo boot. Só `reset_volume` zera, e o zero é gravado na hora.
- **Corrigido:** `reset_volume` durante a dosagem era desfeito por um reinício (`doseRestore` reescrevia `vol` com o volume dosado). A restauração agora vem só de `s_sess`.
- **Corrigido:** sair de um perfil por `speed` (calibração, encher mangueira) ou por uma gravação OTA não apagava o checkpoint de recuperação, e o perfil antigo voltava a bombear sozinho no próximo reinício. A OTA também cancela a dosagem.
- **Corrigido:** o contador de volume é tocado pelos dois cores e era protegido com `taskDISABLE_INTERRUPTS`, que só vale para o core atual. Agora usa spinlock (`volRead`/`volAdd`/`volSet`). A dosagem desliga a integração pela calibração antes de ler o início do ciclo.
- Estado da dosagem na NVS: a cada pulso só com período ≥ 60 s; abaixo disso, no máximo uma vez por minuto (sempre ao cancelar ou concluir).
- O hello continua `ver=3.12`; Hub e app não mudam. O build aparece como `fw_build` `3.12.1`.
- Compilado (ESP32 core 3.3.11): 1 106 491 B (84%), 55 196 B de RAM; SHA-256 em `tests/evidence/fw-3.12.1`.

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
