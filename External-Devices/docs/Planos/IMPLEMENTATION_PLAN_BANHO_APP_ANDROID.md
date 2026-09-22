# PLANO DE IMPLEMENTAÇÃO: APLICATIVO ANDROID PRÓPRIO DO BANHO EXTERNO (C404)
## `External-Devices/banho-termostatico/apps/flutter/` — cliente direto do nó, sem o Hub

**Data de Emissão:** 2026-09-19
**Atualizado:** 2026-09-21
**Status:** **implementado e validado em software**; validação contra o dispositivo real pendente
**Versão implementada:** 1.2.0+3 (posse do Hub, firmware r3.2 — K10 do plano de correções)
**Referência Normativa:** `../../banho-termostatico/docs/PROTOCOL.md` (r2/r3: rotas, `/status`, `/config`, ações, §3.1 hold, §3.2 modos), `../../banho-termostatico/docs/VALIDATION.md` (gates G1–G9)
**Modelos a seguir:** `frasco-agitador/apps/flutter` (estrutura `models/pages/services/widgets/theme`, HTTP por `package:http`, *badge* de conexão, folha de diagnóstico, botão de emergência) e `bomba-peristaltica/apps/flutter` (`fl_chart`, `shared_preferences` para o host)
**Relação com o outro plano:** independente de `IMPLEMENTATION_PLAN_BANHO.md`. Fala com o nó pelo AP `Banho Termostatico` (`192.168.8.1`) ou pelo IP que o nó receber na rede do Hub; não passa pelo Hub. É o equivalente Android do `bath_app.py` e da página `/ui`, e o que a bancada usa nos gates G1–G9 quando não há PC.

---

## 1. Objetivo e Escopo

Cada dispositivo externo com painel próprio tem um aplicativo Flutter local (bomba, fluxômetro,
agitador); o servo é a exceção porque não tem operação local. O banho ganha o seu, com quatro
funções:

1. **Operar**: enviar setpoint absoluto, ajustes relativos, ver PV/SP do display, sequência em
   curso (fase, hold, taxa), abortar.
2. **Modos**: ver e trocar manual/automático, ver o desvio e o estado do guarda, sincronizar a
   sombra (`sync_sp`) quando o SP é desconhecido.
3. **Bancada**: teclas cruas, tecla mantida por tempo (`hold_ms`, mede a auto-repetição — gate
   G3b), `home`, leitura do `/display` (texto, `sp_live`, quadros), configuração completa
   (`/config`) com aplicar/recarregar/`reset_nvs`.
4. **Diagnóstico**: `/diag` (heap, RSSI, streak do Hub, OTA), versão do firmware, log das
   respostas.

Fora do escopo: OTA pelo app (fica na página `/update` do nó, como nos outros), gráficos de
longo prazo (o nó não tem histórico; um traço de 10 min de PV/SP em memória basta), qualquer
função que só faça sentido pelo Hub (via de temperatura, receitas).

---

## 2. Contrato consumido (r2/r3)

| Rota | Uso no app | Período |
|---|---|---|
| `GET /status` | estado principal (sombra, `sp_known`, alvo, `seq_*`, `hold_*`, `mode`, `guard`, `deviation_c`, `display_*`, `manual_presses`, `wifi_status`, `ota`) | 1 s (poll) |
| `GET /config` | aba de configuração | ao abrir a aba e após aplicar |
| `POST /command` | todas as ações e a configuração (`{"setpoint":x}`, `{"delta":x}`, `{"sync_sp":x}`, `{"mode":"auto"}`, `{"abort":1}`, `{"key":"up","count":n}`, `{"key":"up","hold_ms":n}`, `{"home":1,"setpoint":x}`, `{"reset_nvs":1}`, chaves de configuração) | sob demanda |
| `GET /display` | aba do display (`alive`, `frames`, `live_frames`, `text`, `pv`, `sp`, `sp_live`, `raw[8]`) | 500 ms enquanto a aba está aberta |
| `GET /diag` | folha de diagnóstico | 5 s enquanto aberta |

Códigos: `200` ok, `409` recusado (`busy`, `sp_unknown`, `display_invalid`, `range`,
`too_many_presses`, `ota_in_progress`), `400` sem chave válida. O corpo é sempre JSON com
`ok`/`action`/`error`; o app **mostra o `error` literal** e nunca o traduz para um valor
padrão (regra de v.6 do app Windows: entrada inválida não vira número plausível).

`cmd_id`: o app envia `cmd_id` crescente (persistido em `shared_preferences`) em toda ação que
aciona relés e repete a mesma requisição até 3× em caso de timeout de rede; o nó responde
`duplicate` a reentregas e nunca reaplica toques. Sem `cmd_id` uma segunda tentativa de
`{"delta":0.5}` moveria o SP duas vezes.

---

## 3. Estrutura do projeto

```text
External-Devices/banho-termostatico/apps/flutter/
  pubspec.yaml            name: bath_app  (http, provider, shared_preferences, fl_chart)
  README.md               como rodar; AP do nó; relação com bath_app.py e /ui
  lib/
    main.dart             MaterialApp + Provider(BathService) + shell com 4 abas
    theme/app_theme.dart  copiado do agitador (mesma identidade dos apps locais)
    models/
      bath_status.dart    BathStatus.fromJson(/status) — enums SeqState, SeqKind, SeqPhase,
                          BathMode, GuardState; campos nullable onde o nó manda null
      bath_config.dart    BathConfig.fromJson(/config) + toCommandJson(diff) — só chaves alteradas
      display_frame.dart  DisplayFrame.fromJson(/display)
      node_diag.dart      NodeDiag.fromJson(/diag)
    services/
      bath_service.dart   ChangeNotifier: host, poll de /status, fila de comandos com cmd_id
                          e reentrega, último erro, log circular (50 linhas), estado de conexão
      command_ids.dart    contador persistido
    pages/
      operate_page.dart   aba Operação
      modes_page.dart     aba Modos (ou secção da Operação em tela pequena)
      bench_page.dart     aba Bancada (teclas, hold, home, display)
      settings_page.dart  aba Configuração (+ diagnóstico)
    widgets/
      connection_badge.dart      (agitador) presença/latência/versão
      setpoint_card.dart         sombra grande, "desconhecido", alvo, campo + Enviar, ±0,1/±0,5/±1,0
      sequence_card.dart         estado/fase/erro; barra de progresso de toques; hold: ms e taxa
      mode_card.dart             manual/auto, guarda, desvio, sincronizar
      display_card.dart          PV/SP do painel, texto dos 8 dígitos, sp_live, frames
      raw_keys_row.dart          *, ▲, ▼, ENTER × n; ▲/▼ mantidas por N ms
      config_form.dart           todas as chaves do PROTOCOL §4, com faixa e dica
      diagnostics_sheet.dart     (agitador) /diag + versão + log
      emergency_abort_button.dart (agitador) → {"abort":1}, sempre visível
  test/
    bath_status_test.dart       fixtures de /status (r2 completo; sem display; hold em curso)
    bath_config_test.dart       diff só com chaves alteradas; conversão int/float
    bath_service_test.dart      reentrega com o mesmo cmd_id; 409 vira erro visível; timeout
    widget_test.dart            seletor de modo, campo de setpoint recusa texto inválido
```

---

## 4. Telas

### 4.1 Operação (padrão ao abrir)
- *Badge* de conexão (host, `version`, `uptime_s`, latência do último `/status`).
- `setpoint_card`: `sp_shadow` em fonte grande; chip vermelho "SETPOINT DESCONHECIDO —
  sincronize" quando `sp_known = false` (bloqueia Enviar/ajustes até `sync_sp` ou `home`); alvo;
  campo numérico (vírgula aceita) + **Enviar**; botões ±0,1 / ±0,5 / ±1,0 (`delta`). A faixa
  `sp_min`–`sp_max` vem do `/config` e é validada antes de enviar.
- `sequence_card`: `seq_state/seq_kind/seq_phase`; em `presses`: `presses_done/total`; em
  `hold`: "tecla mantida há X ms, Y toques/s, rodada N"; `seq_error` literal; **Abortar**.
- Leituras do painel: `display_pv` / `display_sp` / texto, com "sem sinal" quando
  `display_alive = false`.
- Traço de 10 min (`fl_chart`) de `display_pv` e `sp_shadow`, em memória.

### 4.2 Modos
- Interruptor **Manual / Automático** (`{"mode":…}`) com texto explicando: manual = mudanças no
  painel só são reportadas; automático = revertidas após `guard_delay_ms`.
- Estado do guarda (`off/watch/pending/correcting/suspended`) e `guard_corrections`;
  `deviation_c` com cor (0 = ok).
- `arrows_held_ms` como barra até `mode_hold_ms` (feedback do gesto na bancada).
- **Sincronizar sombra**: campo "SP lido no painel" + botão (`sync_sp`).

### 4.3 Bancada
- Teclas cruas com contador ×n; **▲ / ▼ mantidas por N ms** (100–20000, `hold_ms`), com aviso
  "manter `*` volta à tela principal (manual §7.1)".
- **Home** com diálogo de confirmação (passa por `sp_min`; pode levar minutos).
- `display_card` ao vivo (500 ms): texto dos 8 dígitos, `sp_live` vs `sp`, `frames`/`live_frames`.
- Atalhos dos gates: G3b (hold 2/5/10 s e anotar), G7b (`▲+▼` 5 s e observar).

### 4.4 Configuração e diagnóstico
- Formulário com todas as chaves de `PROTOCOL.md` §4 (r2: `press_ms … send_period`,
  `hold_*`, `sense_enabled`, `sense_mask`, `mode_hold_ms`, `guard_delay_ms`, `hub_enabled`,
  `disp_*`); aplicar envia **só o que mudou** (o nó responde `config_unchanged` quando nada
  muda); recarregar; `reset_nvs` com confirmação dupla.
- Folha de diagnóstico: `/diag`, versão, `last_cmd_id`, log das últimas respostas.

---

## 5. Regras de comportamento (as mesmas do app Windows e do `bath_app.py`)
1. Entrada inválida ou fora da faixa **bloqueia** o envio e mostra o erro; nunca substitui.
2. Nada é reenviado automaticamente sem `cmd_id`; reentrega só de requisições com `cmd_id`.
3. Estado "pendente" visível: entre o `200` e o `seq_state = done`, o cartão mostra "em
   execução" (o nó pode levar dezenas de segundos num hold).
4. `busy` (409) não é erro do operador: mostrar "sequência em andamento — aguarde ou aborte".
5. Trocar de host reinicia o poll e limpa o traço; o último host fica em `shared_preferences`.
6. O botão Abortar nunca é escondido nem desabilitado.

---

## 6. Testes e validação
- `flutter analyze && flutter test` (modelos com fixtures reais copiadas do `PROTOCOL.md`;
  serviço contra um `HttpServer` falso em Dart, como o `check` do `bath_app.py`).
- Bancada: reproduzir com o app os gates G1–G3b e G7b–G9 do `VALIDATION.md`; registrar no
  `CURRENT_STATUS.md` do nó que o app foi validado contra o dispositivo (hoje `bath_app.py`
  consta como "não testado contra o dispositivo").
- Compatibilidade: o app lê `version` e avisa se o firmware não for `r2`/`r3` (campos
  `mode`/`guard`/`hold_*` ausentes → cartões correspondentes mostram "firmware sem suporte").

---

## 7. Entregáveis e ordem — concluídos em software
| Passo | Conteúdo | Fecha com |
|---|---|---|
| 1 | Esqueleto: `pubspec`, tema, `BathService` com poll e `cmd_id`, `BathStatus`, aba Operação | `flutter test` dos modelos e do serviço |
| 2 | Modos + Sincronizar + Abortar | teste de widget do interruptor |
| 3 | Bancada (teclas, hold, home, display ao vivo) | uso nos gates G3b/G7b |
| 4 | Configuração + diagnóstico + `reset_nvs` | teste do diff de configuração |
| 5 | README do app; `banho-termostatico/README.md` e `CURRENT_STATUS.md` (app Android listado ao lado do `bath_app.py`); `External-Devices/README.md` (coluna "Aplicativo" do banho: Desktop Python + Android) | commit único `feat(banho-termostatico): aplicativo Android do banho` |

Sem dependência do Hub nem do plano de integração; pode ser feito antes ou em paralelo ao M0
daquele plano — e é útil justamente nos gates de bancada que o M0 exige.

### Evidência atual

- quatro abas e todos os controles previstos presentes em `apps/flutter`;
- `flutter analyze` sem problemas;
- testes de modelo, serviço e widgets aprovados, incluindo compatibilidade r2/r3 e aviso de
  firmware incompatível;
- APK gerado com sucesso;
- uso real com o ESP32-S3/C404 ainda depende dos gates G1–G9.
