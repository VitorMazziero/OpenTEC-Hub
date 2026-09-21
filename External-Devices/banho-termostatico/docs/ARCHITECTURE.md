# Arquitetura — firmware `thermostatic-bath`

Mesma organização dos demais dispositivos externos: um sketch de duas linhas e módulos
`.h/.cpp` por responsabilidade em `src/`.

| Módulo | Responsabilidade |
|---|---|
| `config/BoardConfig.h` | Pinos, SSIDs, URLs do Hub, tag de versão, setpoint inicial |
| `core/AppContext` | Globais compartilhados: `g_cfg` (parâmetros), setpoint-sombra, estado Wi-Fi/OTA/Hub |
| `core/FirmwareApp` | `setup`/`loop`: ordem de inicialização (relés primeiro), serviço dos módulos, serial, Hub |
| `keypad/KeyPresser` | Fila de toques e motor não bloqueante dos relés (fechado `press_ms`, aberto `gap_ms`); passo de *hold* (relé fechado até `keypadRelease()` ou o teto do passo); um relé por vez; callback por toque (não por hold) |
| `keypad/KeySense` | Leitura das linhas das teclas ligadas (`sense_mask`); distingue toque do relé de toque manual; mede `▲`+`▼` mantidas |
| `display/DisplayReader` | ISR nas linhas de dígito amostra os segmentos; decodificação 7 segmentos → PV/SP com filtro de estabilidade (~300 ms) e leitura ao vivo do SP a partir do último quadro coerente (duas varreduras iguais) |
| `setpoint/SetpointManager` | Traduz `setpoint`/`delta`/`home`/`key` em sequências de pernas (hold em malha fechada pelo display, ou toques); mantém sombra, `sp_known`, marca de sequência em curso; verifica pelo display e corrige uma vez |
| `setpoint/SetpointGuard` | Modo manual/automático: gesto `▲`+`▼` (e botão opcional), desvio `display − sp_target`, e no modo automático a reversão de mudanças manuais depois de o painel parar; suspensão após falhas ou abort |
| `protocol/ConfigCodec` | Parser JSON manual, ações, configuração, `/status` e `/config` em JSON |
| `api/LocalHttpApi` | Rotas HTTP, página `/ui`, OTA |
| `network/NetworkManager` | AP sempre ativo; conexão STA solicitada exclusivamente pela tarefa `HubLink` |
| `network/HubLink` | tarefa HTTP no core 0; snapshot protegido; hello/push r3; fila fixa que devolve comandos ao loop principal; medição de pilha |
| `storage/NvsConfig` | `bath_cfg` (parâmetros) e `bath_st` (sombra, confiança, `seq_busy`) |

## Invariantes

- Nenhum `delay()` com relé fechado. `httpGet` pode bloquear por até 2,5 s, mas somente na
  tarefa `HubLink`; a temporização dos relés permanece no loop principal. O watchdog de 15 s
  é alimentado a cada volta do `loop`.
- Os quatro GPIOs de relé são escritos em HIGH **antes** do `pinMode(OUTPUT)`: o registrador
  de saída do ESP32 parte em 0 e a ordem inversa fecharia os relés por alguns µs no boot.
- `sp_known` só vira `true` por `sync_sp`, por sequência concluída sem erro, ou por leitura
  válida do display no modo display. Qualquer dúvida (toque manual, reboot com `seq_busy`,
  abort, display inválido) derruba para `false`, e o firmware recusa novos pedidos até resolver.
- Um payload executa no máximo uma ação; reentrega com o mesmo `cmd_id` nunca reaplica toques.
- Escrita na NVS apenas em início/fim de sequência, em mudança de configuração e quando o
  display muda o valor da sombra — nunca por toque nem por quadro do display durante um hold.
- Um hold só acontece em `▲`/`▼`, só com `sp_source = 1` e só enquanto o display se deixa
  ler: 300 ms sem leitura válida soltam a tecla. O motor tem um teto por passo
  (o tempo que os toques substituídos levariam, mais 5 s) para que nenhum erro de lógica
  deixe um relé fechado. Manter `*` é "voltar à tela principal" no C404 e nunca é gerado
  pelo firmware; só pelo comando cru `{"key":"star","hold_ms":…}`.
- A leitura estável do display guarda o último valor parado; durante e logo após um hold
  esse é o valor de *antes* do hold. O planejamento só a usa quando a leitura ao vivo
  concorda com ela, senão espera (até 1 s) e depois segue com a leitura ao vivo.
- `sp_target` é o último SP comandado e é persistido separado da sombra: no modo display a
  sombra segue o painel, e o alvo não pode ser contaminado por uma mudança manual nem por um
  reboot no meio dela. O guarda nunca inventa alvo: só reexecuta `setpoint = sp_target`.
- O guarda só aperta tecla depois de `guard_delay_ms` sem o display mudar e sem tecla manual
  pressionada, nunca durante uma sequência de outra origem, e para sozinho após 3 falhas
  seguidas ou um abort do operador. Linhas de sensoriamento sem fio (`sense_mask`) não são
  lidas: uma entrada flutuante viraria toques manuais fantasmas.

## Fluxo de uma sequência

```text
POST /command {"setpoint":31.5}
  → SetpointManager: base = sombra (ou display) → N = (alvo − base)/step_c
  → saveNvsState(busy=1)
  → modo sombra, ou hold desligado, ou |N| < hold_min_steps:
      fila: [enter_key ×1, +menu_ms] [▲ ×N] [confirm_key ×1]           (fase presses)
  → modo display com hold:
      fila: [enter_key ×1, +menu_ms]                                    (fase enter)
      plan: lê o display (estável confirmada pela ao vivo) → faltam R
        R ≥ hold_min_steps e ≤ 3 holds: [hold ▲/▼, teto R×(press+gap)+5 s]  (fase hold)
          a cada quadro: sombra = SP ao vivo; taxa (toques/s) a partir da 2ª mudança
          solta quando faltam ≤ hold_stop_steps + taxa × hold_lag_ms/1000,
          ou display ilegível 300 ms, ou parado hold_stall_ms  →  hold_settle_ms  → plan
        senão: [▲/▼ ×R] [confirm_key ×1]                                 (fase presses)
loop: keypadService fecha/abre relés; callback soma step_c à sombra a cada ▲/▼ (toques)
  → fila vazia → SEQ_SETTLING (settle_ms)
  → modo sombra: sp_known = true, SEQ_DONE
  → modo display: sombra = display_sp; SEQ_DONE se display_sp == alvo;
      diferença ≤ 20 toques e nenhuma correção ainda: enter_key → plan (uma correção);
      senão SEQ_ERROR sp_mismatch
  → saveNvsState(busy=0)
```

`tests/host-sim/` compila `SetpointManager.cpp`, `SetpointGuard.cpp`, `KeyPresser.cpp`,
`KeySense.cpp`, `ConfigCodec.cpp` e `AppContext.cpp` no PC contra um C404 simulado (auto-repetição com atraso e
aceleração, toques perdidos, display ilegível, cauda após soltar, operador apertando teclas)
e percorre 34 cenários, incluindo idempotência de `cmd_id`; é a evidência de lógica enquanto
a bancada não roda. O agendamento real entre cores e a rede continuam dependentes de bancada.
