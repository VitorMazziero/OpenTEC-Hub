# Protocolo — Banho termostático (`banho-termostatico`)

**Versão do firmware:** `r2` (`BathClient r2`)
**Rede:** AP `Banho Termostatico` (aberto, canal 6), `192.168.8.1`; STA para o Hub opcional
**Compatibilidade com Hub:** nenhuma ainda (`hub_enabled = 0`); rota `/bath` proposta, ver §6

---

## 1. Rotas HTTP locais

| Rota | Método | Função |
|---|---|---|
| `/` e `/status` | GET | Estado (§2) |
| `/diag` | GET | Estado + `free_heap`, `ssid`, `rssi`, `mac`, `ap_ip`, `hub_fail_streak` |
| `/config` | GET | Configuração vigente (§4) |
| `/config`, `/command` | POST JSON | Ações e configuração (§3, §4); mesmo parser |
| `/setpoint?sp=31.5` | POST | Atalho para `{"setpoint":31.5}` (aceita também corpo JSON) |
| `/display` | GET | Leitor de display: `alive`, `frames`, `text`, `pv`, `sp`, `sp_live`, `live_frames`, `raw[8]` |
| `/ui` | GET | Página HTML mínima para celular |
| `/update` | GET/POST | OTA (mesma página e regras dos outros nós: só `.ino.bin`) |

Códigos: `200` ação aceita ou configuração aplicada; `409` ação reconhecida mas recusada
(`busy`, `sp_unknown`, `range`, …); `400` nenhuma chave válida. O corpo é sempre JSON
`{"ok":true,"action":"…"}` ou `{"ok":false,"error":"…"}`.

A serial (115200) aceita os mesmos JSONs, mais `status` e `config`.

## 2. Estado (`GET /status`)

```json
{"device":"bath","version":"BathClient r2 …","uptime_s":120,
 "mode":"manual","guard":"off","deviation_c":null,"guard_corrections":0,"arrows_held_ms":0,
 "sp_shadow":30.00,"sp_known":true,"sp_target":31.50,"sp_source":0,
 "seq_state":"running","seq_kind":"setpoint","seq_phase":"presses","seq_error":"",
 "presses_done":7,"presses_total":17,"presses_unconfirmed":0,
 "hold_ms":0,"hold_rounds":0,"hold_rate":0.0,
 "display_alive":false,"display_pv":null,"display_sp":null,"display_text":"",
 "manual_presses":0,"manual_age_s":-1,
 "wifi_status":6,"ip":"0.0.0.0","ota":false,"last_cmd_id":0}
```

| Campo | Significado |
|---|---|
| `mode` | `manual` ou `auto` (§3.2); persistido |
| `guard` | Guarda do modo automático: `off`, `watch`, `pending` (desvio visto, esperando o painel parar), `correcting`, `suspended` |
| `deviation_c` | `display_sp − sp_target` em °C; `null` sem display legível. É o "desvio" que o modo manual só reporta |
| `guard_corrections` | Reversões feitas pelo guarda desde o boot |
| `arrows_held_ms` | Há quanto tempo `▲`+`▼` estão pressionadas manualmente (0 fora do gesto; exige sensoriamento) |
| `sp_shadow` | Setpoint que o firmware acredita estar no C404 (°C, quantizado em `step_c`) |
| `sp_known` | `false` quando a sombra deixou de ser confiável: toque manual visto, reboot no meio de uma sequência, abort, ou display inválido no modo display |
| `sp_target` | Último setpoint **comandado** (persistido): referência do desvio e do modo automático. Uma mudança manual no painel não o altera |
| `seq_state` | `idle`, `running`, `settling`, `done`, `error`, `aborted` |
| `seq_kind` | `none`, `setpoint`, `home`, `raw` |
| `seq_phase` | Sub-fase de uma sequência `setpoint` em curso: `enter`, `plan`, `hold`, `hold_settle`, `presses`; vazio fora delas |
| `seq_error` | `sp_mismatch`, `display_invalid_after_sequence`, `queue_full`, `too_many_presses`, `aborted` ou vazio |
| `presses_*` | Progresso da perna a toques em curso; `unconfirmed` conta toques que o sensoriamento não viu |
| `hold_ms` | Há quanto tempo a tecla está mantida (0 fora de um hold) |
| `hold_rounds` | Holds feitos na última sequência `setpoint` |
| `hold_rate` | Taxa de auto-repetição medida no display no último hold (toques/s) |
| `display_pv` / `display_sp` | `null` quando o display não está vivo ou mostra texto (menus) |
| `manual_presses` | Toques físicos detectados desde o boot; `manual_age_s = -1` se nenhum |

## 3. Ações (`POST /command`)

Uma ação por payload, nesta prioridade. Toda ação que aciona relés é recusada com `busy`
enquanto outra sequência corre e com `ota_in_progress` durante upload.

| Chave | Exemplo | Efeito |
|---|---|---|
| `abort` | `{"abort":1}` | Abre todos os relés, limpa a fila. No modo sombra, `sp_known = false`. Abortar uma correção do guarda o suspende (§3.2) |
| `mode` | `{"mode":"auto"}`, `{"mode":"manual"}` (ou `1`/`0`) | Troca o modo (§3.2); aceito mesmo com sequência em curso. Resposta traz `mode` |
| `sync_sp` | `{"sync_sp":30.0}` | Declara o SP lido no painel; `sp_known = true`. Não toca em relé |
| `home` | `{"home":1}` ou `{"home":1,"setpoint":35.0}` | `*`, `▼` × (`(sp_max−sp_min)/step_c + home_margin`), `▲` × até o alvo, `ENTER`. Torna o SP conhecido sem leitura, se o C404 travar em `in.L` |
| `setpoint` | `{"setpoint":31.5}` | Sequência `enter_key → pernas de ▲/▼ → confirm_key`. No modo sombra (0) uma perna só, de N toques a partir da sombra. No modo display (1) com `hold_enabled` e distância ≥ `hold_min_steps`, a primeira perna **mantém** a tecla e lê o display ao vivo até chegar perto do alvo; o resto vai a toques (§3.1). Recusa `sp_unknown`, `display_invalid`, `range`, `too_many_presses`. Resposta: `target`, `presses` (distância em toques de seta), `hold` (se a sequência vai manter a tecla) |
| `delta` | `{"delta":-0.5}` | `setpoint = base + delta` |
| `key` | `{"key":"up","count":3}` | Toques crus (`star`, `up`, `down`, `enter`); não atualizam a sombra e, no modo sombra, deixam `sp_known = false` se envolverem `*`, `▲` ou `▼` |
| `key` + `hold_ms` | `{"key":"up","hold_ms":2000}` | Mantém a tecla fechada por `hold_ms` (1–20000) em vez de tocar: mede na bancada o atraso e a taxa da auto-repetição do C404. Mesmas regras de sombra dos toques crus. Manter `*` devolve o C404 à tela principal (manual §7.1) |
| `reset_nvs` | `{"reset_nvs":1}` | Padrões de fábrica; sombra volta a 30.0 conhecida |

`cmd_id` (opcional) segue a regra dos outros nós: mesma revisão não é reaplicada, e
`last_cmd_id` só avança quando a ação foi aceita ou uma chave de configuração válida existia.

Sequência de uma mudança de 30.0 para 31.5 com os padrões (`enter_key=1`, `confirm_key=2`):

```text
* (150 ms) … 400 ms … ▲ ×15 (150 ms fechado / 150 ms aberto) … ENTER (150 ms) … 1500 ms
→ modo sombra: sp_shadow = 31.5, sp_known = true
→ modo display: sp_shadow = display_sp; erro sp_mismatch se |display_sp − 31.5| > step_c/2
```

A sombra é incrementada a cada toque de `▲`/`▼` concluído, então um abort no meio deixa o
valor coerente com o que foi enviado (mas `sp_known = false`, porque não se sabe se o C404
descartou ou aplicou a edição não confirmada).

### 3.1 Tecla mantida (modo display)

O C404 auto-repete a seta mantida, bem mais rápido do que 1 toque a cada `press_ms + gap_ms`.
Como só o display sabe quantos incrementos a auto-repetição produziu, o hold existe apenas
com `sp_source = 1`, e a sequência passa a ser uma malha fechada:

```text
{"setpoint":60.0} com o display em 30.0 (300 toques)
  enter_key (se houver) … menu_ms
  plan:   lê o display (estável, confirmado pela leitura ao vivo) → faltam 300 ≥ hold_min_steps
  hold:   ▲ mantida; a cada quadro coerente do display: sombra = SP ao vivo, taxa = toques/s
          solta quando faltam ≤ hold_stop_steps + taxa × hold_lag_ms
          (ou: display ilegível por 300 ms, display parado por hold_stall_ms, teto de segurança)
  hold_settle_ms … plan: relê o display → faltam 3 → perna a toques: ▲ × 3, confirm_key
  settle_ms → verificação: display_sp == alvo → done
```

O `plan` repete até três holds por sequência (segundo hold se ainda falta muito, por
exemplo após passar do alvo), e uma diferença residual de até 20 toques na verificação
final dispara **uma** correção automática (`enter_key → toques → confirm_key → verificação`)
antes de reportar `sp_mismatch`. Sem auto-repetição (display parado por `hold_stall_ms`) ou
com o display ilegível durante o hold, o resto da sequência vai a toques, sem novo hold.
Um hold nunca dura mais do que os toques que substitui durariam (mais 5 s), e nunca ocorre
em `*` ou `ENTER`: mantê-las é o gesto de "voltar à tela principal" do C404.

Tempos com o C404 simulado a 10 toques/s após 0,6 s (bancada confirma): 30,0 → 31,5 em
4,6 s (6,0 s a toques); 30,0 → 60,0 em 33 s (90 s a toques).

### 3.2 Modos manual e automático

Os relés ficam em paralelo com as teclas: nada impede o operador de mudar o SP no painel
depois de o nó tê-lo ajustado. O nó guarda o último SP **comandado** (`sp_target`, persistido)
e reage ao desvio `display_sp − sp_target` conforme o modo:

| Modo | Mudança manual no painel | O nó faz |
|---|---|---|
| `manual` (padrão) | Permitida | Só reporta: `deviation_c` em `/status` e `dev` no push ao Hub. `sp_shadow` segue o painel; `sp_target` fica |
| `auto` | "Não permitida": revertida | O guarda espera o painel **parar** — display estável e nenhuma tecla manual pressionada por `guard_delay_ms` (5 s) — e executa `{"setpoint": sp_target}` com a sequência normal (§3.1, com hold). Reverte quantas vezes for preciso |

O guarda só age no modo display (`sp_source = 1`); no modo sombra não há como ver a mudança,
e o modo `auto` fica em `guard = watch` sem agir. Ele se **suspende** (`guard = suspended`)
após 3 correções seguidas com erro ou quando o operador aborta uma correção, e volta a
`watch` com qualquer comando de setpoint aceito (`setpoint`, `delta`, `sync_sp`, `home`) ou
troca de modo. Um novo `setpoint` comandado passa a ser o alvo que o guarda defende.

**Troca de modo pelo painel:** `▲`+`▼` pressionadas juntas, fisicamente, por `mode_hold_ms`
(3 s) alternam o modo — uma troca por pressionamento, solte antes de repetir. Só o
sensoriamento das teclas enxerga o gesto (`sense_enabled = 1`, `sense_mask` com bits 1 e 2;
WIRING.md §3b); `arrows_held_ms` mostra a contagem. O manual do C404 não atribui função ao
par de setas; o que o C404 faz com as duas pressionadas é o gate G7b do `VALIDATION.md` — se
o SP andar durante o gesto, no modo `auto` o guarda reverte em seguida. O LED externo no
GPIO 40 (WIRING.md §5) acende no modo `auto` e apaga no `manual`, atualizado na hora da troca.
Alternativas sem tocar no painel: o comando `mode`, ou um botão próprio na caixa
(`ModeButtonPin` no `BoardConfig.h`, desligado por padrão).

## 4. Configuração (`GET /config`, `POST /config`)

| Chave | Padrão | Faixa | Uso |
|---|---|---|---|
| `press_ms` | 150 | 20–2000 | Relé fechado por toque |
| `gap_ms` | 150 | 20–5000 | Relé aberto entre toques |
| `menu_ms` | 400 | 0–10000 | Espera após `enter_key` |
| `settle_ms` | 1500 | 0–60000 | Espera após `confirm_key` antes de verificar |
| `step_c` | 0.1 | 0.01–10 | Graus por toque (C404 com `d.P = 1`) |
| `sp_min`, `sp_max` | 5.0, 90.0 | −200–900, `min < max` | Devem ser os `in.L`/`in.H` do C404: limitam pedidos e dimensionam o `home` |
| `enter_key` | 1 (`*`) | 0–2 | 0 nenhuma, 1 `*`, 2 `ENTER` |
| `confirm_key` | 2 (`ENTER`) | 0–2 | idem |
| `sp_source` | 0 | 0–1 | 0 sombra, 1 display |
| `sense_enabled` | 0 | 0–1 | Leitura das teclas |
| `sense_mask` | 6 | 0–15 | Linhas de sensoriamento ligadas: bit0 `*`, bit1 `▲`, bit2 `▼`, bit3 `ENTER` (6 = só as setas, montagem atual) |
| `hub_enabled` | 0 | 0–1 | STA + push para o Hub |
| `disp_seg_low`, `disp_dig_low`, `disp_seg_lead` | 1, 1, 0 | 0–1 | Polaridade e fase do display (HARDWARE.md §3) |
| `home_margin` | 20 | 0–1000 | Toques extras de `▼` no `home` |
| `send_period` | 1000 | 100–60000 | Período do push ao Hub (ms) |
| `hold_enabled` | 1 | 0–1 | Tecla mantida com o display fechando a malha (só com `sp_source = 1`) |
| `hold_min_steps` | 15 | 1–1000 | Distância mínima (toques) para valer a pena manter a tecla |
| `hold_stop_steps` | 3 | 0–100 | Soltar a esta distância do alvo, mais `taxa × hold_lag_ms` |
| `hold_lag_ms` | 80 | 0–2000 | Atraso display → decisão → relé aberto, compensado pela taxa medida |
| `hold_settle_ms` | 600 | 0–10000 | Espera após soltar antes de reler o display (> 300 ms do filtro) |
| `hold_stall_ms` | 2500 | 200–20000 | Display parado com a tecla mantida por este tempo = soltar (sem auto-repetição) |
| `mode_hold_ms` | 3000 | 0–20000 | `▲`+`▼` (ou o botão da caixa) mantidas por este tempo alternam o modo; 0 desliga o gesto |
| `guard_delay_ms` | 5000 | 1000–60000 | Modo `auto`: painel parado e teclas soltas por este tempo antes de reverter |

Configuração é recusada (`busy`) com sequência em andamento. Persistida em `bath_cfg`;
o estado (`sp_shadow`, `sp_known`, `seq_busy`, `sp_target`, `mode`) em `bath_st`.

## 5. Persistência e reboot

`seq_busy` é gravado antes do primeiro toque de cada sequência e limpo ao terminar. Um
boot que encontra `seq_busy = 1` marca `sp_known = false`. No modo display a sombra é
reconstruída do painel nos primeiros ~300 ms após o display ficar vivo.

## 6. Integração futura com o Hub (não implementada no Hub — plano em `../../docs/Planos/IMPLEMENTATION_PLAN_BANHO.md`)

Com `hub_enabled = 1` o nó procura `ModuloTECNAL_1/2`, envia
`GET /nodeHello?dev=bath&ver=v1&mac=…` e a cada `send_period`:

```text
GET http://192.168.4.1/bath?sp=<sombra>&known=<0|1>&target=<alvo>&state=<seq_state>&pv=<display_pv|-1>&pv_ok=<0|1>&mode=<0|1>&dev=<display−alvo|0>&dev_ok=<0|1>&time=<s>&ack_cmd_id=<n>
```

`mode` (0 manual, 1 auto) e `dev`/`dev_ok` são a telemetria do modo (§3.2); o Hub pode trocar
o modo pelo canal por carona com `{"mode":"auto"}`.

Resposta `200` com corpo JSON iniciado por `{` é passada ao mesmo `processCommand` (§3/§4),
o que dá ao Hub o canal por carona dos outros nós. O push nunca ocorre com toques em
andamento, porque o `httpGet` bloqueia até 2,5 s e alongaria um toque — e isso inclui um
hold, que pode durar dezenas de segundos; o Hub verá o nó calado durante esse tempo.
