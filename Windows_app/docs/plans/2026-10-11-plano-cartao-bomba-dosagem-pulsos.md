# Plano de execução — bomba externa: correções de integração, dosagem por pulsos, cartão e calibração

**Data:** 2026-10-11 (revisão 2: auditoria de integração do firmware e fases detalhadas)
**Estado:** fases 0–7 implementadas em software (2026-10-11/12) (bomba 3.12.1 e 3.13 compiladas; Hub 10.9.0 compilado; app com testes verdes); fase 8 concluída no código e na documentação (2026-10-12); portões G0–G3 aguardam a bancada (`External-Devices/bomba-peristaltica/docs/BANCADA_3.13.md`)
**Escopo:** firmware da bomba peristáltica, Hub ESP32-S3, aplicativo Windows (cartão *Controle › Bomba Externa* e *Calibrações › Bomba Externa*), simulador, testes e documentação
**Versões-alvo:** bomba 3.12.1 (correções, contrato inalterado) → bomba 3.13 (contrato novo) · Hub 10.9.0 · app 0.32.0

---

## 0. Sumário executivo

1. **Defeito crítico encontrado (F-01).** A bomba lê o `cmd_id` do Hub como `float` (`HubClient.h:230`). Desde a 10.x, o Hub numera os comandos a partir de uma base aleatória entre 100 000 000 e 999 999 000 (`Mailboxes.h:6-14`), e um `float` só representa inteiros exatos até 16 777 216. Duas consequências:
   - o ACK que a bomba devolve **nunca** bate com o número do Hub, então `PumpCommandPending` fica preso;
   - comandos seguidos arredondam para o **mesmo** número e são descartados como "já aplicados".

   A conferência numérica está na §3.1. Os outros nós (distância, biomassa, agitador, fluxômetro, banho) leem o `cmd_id` como inteiro; só a bomba tem o defeito. É a primeira correção do plano e não depende do Hub nem do app.
2. A auditoria achou outros 14 pontos (§3). Os principais:
   - o volume de sessão zera em qualquer reinício fora de perfil;
   - um perfil interrompido por calibração volta a rodar sozinho depois de um reinício;
   - a dosagem aparece como "Parada" no app;
   - o sensor de líquido bloqueia o motor sem que ninguém veja;
   - recusas do nó são confirmadas como se tivessem sido aplicadas.
3. **Execução em 9 fases e 4 portões** (§8):
   - **Fase 0:** bomba 3.12.1, só correções; grava-se já, com o Hub e o app atuais.
   - **Fases 1 e 2:** contrato novo, bomba 3.13 e Hub 10.9.0.
   - **Fases 3 a 7:** app. A fase 5 (visual do cartão) pode começar logo depois da 0.
   - **Fase 8:** bancada e liberação.
4. Dosagem por pulsos com deslizante de granularidade (§4), cartão com dois gráficos (§5) e ponto de dosagem em *Calibrações* (§6), como na revisão 1.

---

## 1. Objetivo

1. Corrigir a integração bomba ↔ Hub ↔ app: entrega e confirmação de comandos, recusas visíveis, volume que sobrevive a reinícios, estados reais do motor e reinícios visíveis.
2. Reorganizar o cartão da bomba externa para que o operador veja, sem abrir nada, **o que a bomba está fazendo, quanto já entregou e se está atrasada**, com dois gráficos legíveis em vez de um gráfico de dois eixos.
3. Levar a **dosagem por pulsos** (hoje só por USB ou `POST /command` no AP `FeedPump`) para o app, passando pelo Hub, com confirmação por eco.
4. Trocar a digitação de volume por pulso e período por um **deslizante de granularidade**. Para uma vazão média escolhida, ele troca pulsos menores e mais frequentes por pulsos maiores e mais espaçados, mostrando o custo de cada lado.
5. Permitir **pulsos mais curtos** que os 0,5 s atuais, com a precisão medida e não presumida.
6. Criar em *Calibrações* uma seção própria para o **ponto único de dosagem**, independente da curva polinomial, com verificação opcional em pulsos.

---

## 2. Diagnóstico do código atual

### 2.1 Cartão da bomba (`ControlView.xaml:1924-2235`, `PumpControlViewModel.cs`)

| Observação | Onde | Consequência |
|---|---|---|
| A linha principal põe a vazão em **Valor Lido** e o **volume** em **Setpoint** | `ControlView.xaml:1973-1974`; cabeçalho em `:165-166` | O operador lê "0,000 / 0,000" sem saber que a segunda coluna é volume. |
| A coluna 7 mostra o texto fixo "5 perfis + gás prop." | `ControlView.xaml:1997` | Espaço nobre sem informação. |
| O modo aparece duas vezes (linha principal e gaveta) | `:1976` e `:2052` | Duplicação. |
| A gaveta abre com "Alvo no nó / Modo no nó / Parada" em texto cinza miúdo | `:2034-2047` | O estado real tem menos destaque que os campos de entrada. |
| Gráfico único com **dois eixos Y** | `PumpPreviewChart.xaml.cs` (`Redraw`) | As curvas se cruzam num ponto sem significado e as escalas competem. |
| A prévia é só o **planejado**, sem o que a bomba entregou | `PumpPreviewChart` | Desvio só aparece no número "Alvo no nó". |
| Os perfis contínuos dependem da curva polinomial, hoje **zerada** no nó | firmware: sem `pump_poly_cal`, `mlminToSpeedUnits` devolve 0 | O cartão deixa enviar um perfil que **não gira o motor**, sem aviso. Sem curva, o Hub não publica `PumpCalCrc` (`Telemetry.h:459`). |
| Gás proporcional e PID ocupam a gaveta mesmo desligados | `:2119-2185` | Ruído visual; o PID só vale para perfis contínuos. |
| `PumpControlViewModel` tem 1 143 linhas; `ControlView.xaml`, 2 840 | — | Extrair antes de acrescentar a dosagem. |

### 2.2 Calibração (`CalibrationView.xaml:361+`, `PumpCalibrationViewModel.cs`)

- Tudo gira em torno da curva polinomial; não há lugar para "uma velocidade, uma vazão".
- O ensaio automático mede o tempo **no relógio do PC** e para a bomba mandando `speed 0`. O prazo do nó leva o tempo pedido **mais 3 s** (`StartRun`, `:1339-1369`). A latência da parada pelo Hub (até ~2 s de consulta) pode dar ~2 % de erro num ensaio de 60 s.
- *Controle manual · preencher mangueira* envia `pump_speed` **sem prazo** (`StartManual`, `:1308`). Se o app, o PC ou o Hub caírem, o motor gira até alguém intervir (F-07).
- `PumpCalibrationProfile` (esquema 2) não tem campo para ponto de dosagem.

### 2.3 Dosagem no firmware (`DoseController.h`, build 3.12+dose2)

- Parâmetros: `dose_ml` (0–100], `dose_period_s` [2, 86 400], `dose_speed` [1, 1000], `dose_rate_mlmin` (0, 1000], `dose_total_ml` ≥ 0 e `dose:1/0`.
- Pulso mínimo de 500 ms (`MIN_MOTOR_ON_TIME_MS`, `FirmwareApp.cpp:76`) e `t_on + 1 s ≤ período`.
- O fim do pulso é aplicado no core 0 (`PwmRuntime.h`, `g_doseCutoffMs`). Por isso a trava de 500 ms do loop (`Lifecycle.h:202`) não estica o pulso, e **baixar o mínimo é uma mudança só no validador**.
- O volume entregue é `dose_rate_mlmin × tempo com o driver ligado`, sem partida, inércia, posição do rolete nem desgaste.
- O push ao Hub não leva nenhum campo `dose_*`.
- Defeitos abertos (F-12, F-13, F-14): corrida no contador entre os cores, `reset_volume` desfeito no reinício (`DoseController.h:107`) e gravação na NVS a cada pulso.
- `start`, `stop`, `mode` e `speed` cancelam a dosagem. É bom para a parada segura (`{"mode":0}`), mas o ensaio de calibração também a cancela (§6.4).

### 2.4 Hub (`Commands.h:875-980`, `HttpServer.h:523-570`, `Telemetry.h:431-459`)

- Repassa só as chaves de `simpleKeys` (`Commands.h:885-890`); remove o prefixo `pump_` (`:928`). Assim, `pump_dose_ml` chegaria ao nó como `dose_ml` sem tradução.
- `getValueFromJson` busca a chave entre aspas (`Mailboxes.h:326`), então `"pump_dose"` não casa com `"pump_dose_ml"`.
- Comandos só entram na fila com `pumpCommOn` (`Commands.h:978`).
- Entrega comandos **só** em `GET /pumpCommand` (`HttpServer.h:778`). O `/pumpData` responde com texto fixo, e a bomba consulta a cada 2 s (`HUB_POLL_PERIOD_MS`).
- O padrão de **recusa** já existe para o banho: `rej_cmd_id`/`rej_err` no push, com o Hub encerrando o pendente (`BathCommandCoordinator.cpp:106-117`). A bomba não usa.
- O proxy de diagnóstico guarda até **511 bytes** do `/diag` de cada nó (`AppContext.h:558`).

### 2.5 App: outros pontos de contato

- O catálogo de versões aceita `3.9`, `3.10` e `3.12` (`NodeFirmwareCatalog.cs:39`).
- `TelemetryHistory.GetSeries(canal, janela, maxPontos)` (`TelemetryHistory.cs:340`) já guarda `PumpFlow` e `PumpVolume`.
- `ShellViewModel.OpenCalibration(target)` (`:966`) já existe para atalhos.
- O simulador emula o JSON do Hub para o app, mas **não** o caminho Hub ↔ nó. O F-01 não aparece em nenhum teste.
- Os testes do firmware da bomba são textuais (procuram trechos no fonte). O banho já tem simulação no PC com os fontes reais (`banho-termostatico/tests/host-sim`, MSVC); a bomba pode seguir o mesmo modelo.

---

## 3. Auditoria de integração do firmware da bomba

Severidade: **C** crítico, **A** alto, **M** médio, **B** baixo. A coluna "Fase" aponta onde se corrige (§8).

| # | Sev. | Achado | Evidência | Efeito na integração | Correção | Fase |
|---|:---:|---|---|---|---|:---:|
| F-01 | C | `cmd_id` lido como `float` | `HubClient.h:230`; base do Hub ≥ 10⁸ em `Mailboxes.h:6-14` | ACK nunca bate (`PumpCommandPending` preso); comandos seguidos descartados como duplicados | ler como `uint32` (`strtoul`); teste com `999999001` | 0 |
| F-02 | A | Comando recusado recebe ACK igual ao aceito | `HubClient.h:237-238`; recusas em `OperationController.h:361-388` e `doseValidate` | App não distingue "aplicado" de "recusado"; calibração ou dosagem recusada parece aceita até o eco não vir | validar o quadro antes de aplicar; recusa com `rej_cmd_id`/`rej_err`, padrão do banho | 1, 2 |
| F-03 | A | Reinício fora de perfil **zera** o volume de sessão | `RuntimeStateStore.h:34-37` (`resetOperationState` no boot limpo); checkpoint só com `OP_RUNNING` (`Lifecycle.h:132`) | Contradiz "só `reset_volume` zera"; gás proporcional cai para `V₀·vvm`; registro e gráfico com degrau falso | persistir o contador de sessão sempre, com limite de frequência, e restaurar em todo boot | 0 |
| F-04 | A | `speed` (calibração, encher mangueira) e OTA param o perfil **sem limpar o checkpoint** | `OperationController.h:301-317`; `HubClient.h:91-101` | Após um reinício, o perfil antigo **volta a bombear sozinho** | `clearRuntimeState()` ao sair do perfil por `speed`; decisão D9 para OTA | 0 |
| F-05 | M | Eco enganoso durante a dosagem: `mode=0`, `active=0`, `pot=1` | `HubClient.h:176-188` | App mostra "Parada" e "Potenciômetros no comando" enquanto doseia | campo `drv` (quem comanda o motor); `pot=0` durante a dosagem | 1 |
| F-06 | M | Sensor de líquido invisível; quando bloqueia, perfil e dosagem não entregam | `Lifecycle.h:178-185`; contagem do pulso só com driver ligado | Volume menor que o planejado sem aviso | campos `sens`, `wet` e `d_short` | 1 |
| F-07 | M | `speed` vindo do Hub sem `speed_ms` gira indefinidamente | `OperationController.h:308-316`; app `StartManual` sem prazo | Queda do PC ou do Hub com a mangueira enchendo = motor girando | firmware: prazo padrão para `speed` do Hub (D12); app: prazo + renovação | 1, 3 |
| F-08 | M | Reinícios invisíveis: sem uptime, contador de boots nem motivo | push e `/diag` | App não separa reinício de `reset_volume`; o watchdog de 5 s de 11/10 só foi achado pela serial | `up`, `boot`, `rst` no push e no `/diag` | 1 |
| F-09 | B | Versão "3.12" literal em quatro lugares; `/diag` sem build, CRC, último comando, dosagem | `Lifecycle.h:4`, `HubClient.h:8,42,145` | Build gravado não identificável pela rede | macro única; `/diag` ampliado e ≤ 511 bytes | 1 |
| F-10 | B | Comando só sai no poll de 2 s; `/pumpData` responde texto fixo | `HttpServer.h:778`, `Lifecycle.h:118-126` | Latência de até ~3 s por comando; uma requisição HTTP a mais por ciclo | o Hub devolve o comando pendente na resposta do `/pumpData`; o nó processa (o poll fica de reserva) | 1, 2 |
| F-11 | B | Push repete os nove coeficientes a 1 Hz (~230 caracteres) | `HubClient.h:174` | URL perto do limite com os campos novos | coeficientes a cada 10 pushes ou quando o CRC muda (D13) | 1 |
| F-12 | M | `taskDISABLE_INTERRUPTS` não protege o contador entre os dois cores | `PwmRuntime.h:59-62` e demais | Soma perdida rara; janela no início da dosagem | spinlock `portMUX` em todos os acessos | 0 |
| F-13 | A | `reset_volume` desfeito ao reiniciar durante a dosagem | `DoseController.h:107` | Contador volta e o gás salta | resolvido junto com F-03 (fonte única de restauração) | 0 |
| F-14 | B | Estado da dosagem gravado na NVS a cada pulso | `doseSaveState` | Com pulsos curtos, milhares de gravações por dia | no máximo uma por minuto, e sempre ao cancelar ou concluir | 0 |
| F-15 | B | Push sem o tempo do perfil no relógio do nó; após queda, o tempo retoma do último checkpoint (≤ 60 s) | `RuntimeStateStore.h:25` | App não alinha o gráfico ao vivo com o planejado sem adivinhar | campo `t_min` no push | 1 |

### 3.1 F-01 em números

Ids consecutivos a partir de bases sorteadas pelo Hub e o valor que a bomba usa depois de `(uint32_t)strtof(...)`:

| Base | Hub envia | Bomba entende |
|---:|---|---|
| 100 000 000 | …001, …002, …003, …004, …005 | 100 000 000 ×4, depois 100 000 008 |
| 123 456 000 | …001 … …005 | 123 456 000 ×4, depois 123 456 008 |
| 512 345 000 | …001 … …005 | 512 344 992 ×5 |
| 999 999 000 | …001 … …005 | 999 998 976 ×5 |

O Hub limpa a caixa só quando `ack_cmd_id == revision` (`Mailboxes.h:135`), o que quase nunca acontece. O comando seguinte só é aplicado quando o id cruza o próximo valor representável (a cada 8 a 64 comandos). O resto é descartado como reentrega. Isso explica um `PumpCommandPending` que não apaga e comandos do app que "não pegam" na bomba real. No simulador nada disso aparece, porque ele não emula o caminho Hub ↔ nó.

---

## 4. Modelo da dosagem por pulsos e o deslizante

### 4.1 Grandezas

| Símbolo | Significado | Origem |
|---|---|---|
| `Q̄` | vazão média desejada (mL/min) | operador: digitada, ou "V mL em D h" |
| `S_d` | velocidade fixa do pulso (1–1000) | ponto de dosagem calibrado |
| `Q_d` | vazão medida em `S_d` (mL/min) | ponto de dosagem calibrado |
| `t₀` | atraso efetivo de partida (ms), opcional | verificação em pulsos (§6.3) |
| `v` | volume por pulso (mL) | **deslizante** |
| `T` | período entre inícios de pulso (s) | `T = 60·v / Q̄` |
| `t_on` | tempo de motor por pulso (s) | `t_on = 60·v / Q_d + t₀` |
| `d` | fração de tempo ligado | `d = Q̄ / Q_d` (não depende do deslizante) |

**O ponto-chave:** com `Q̄` e o ponto de dosagem fixos, `d` é fixo. O deslizante só escolhe **como dividir** o mesmo volume no tempo, e por isso é um controle de um grau de liberdade só.

### 4.2 O que cada lado custa

| ← Muitos pulsos pequenos | Poucos pulsos grandes → |
|---|---|
| Alimentação quase contínua; picos de concentração menores | Bolos maiores a cada intervalo |
| Gás proporcional em degraus finos | Degraus maiores de `Q_g` |
| Erro relativo de partida maior (`t₀ / t_on`) e mais sensível à posição do rolete | Cada pulso mais exato |
| Mais partidas do motor | Menos partidas |

### 4.3 Limites

- **`T_min`** é o maior entre:
  - `2 s`;
  - `t_on_min / d`, com `t_on_min` = o maior entre o mínimo do firmware (200 ms, §4.7) e o mínimo recomendado (§4.5);
  - `t_on + 0,5 s`.
- **`T_max`** é o menor entre:
  - o intervalo máximo do operador (padrão 30 min);
  - `86 400 s`;
  - `60·100 / Q̄` (`v ≤ 100 mL`).
- **Inviável** com `d > 0,5`: o app propõe um ponto de dosagem com velocidade menor, ou o modo contínuo se houver curva.

### 4.4 Mapeamento

- `T = T_min · (T_max / T_min)^p`, com `p ∈ [0, 1]`: escala logarítmica.
- Arredondar `T` para 1 s abaixo de 2 min, 5 s até 10 min e 15 s acima, e recalcular `v` e `t_on` a partir do `T` arredondado: o que se vê é o que se envia.
- A posição é salva como **período**; se ele sair da faixa depois de uma mudança de meta, é recortado e o app avisa.

### 4.5 Zonas e erro estimado

- **Vermelha:** `t_on` abaixo do mínimo do firmware.
- **Âmbar:** `t_on` abaixo do mínimo recomendado.
  - Com verificação em pulsos em `t_c` e CV `CV_c`: `CV(t_on) ≈ CV_c · t_c / t_on`; o mínimo recomendado é o ponto em que isso dá a meta de precisão (padrão 5 %).
  - Sem verificação: 2 s fixos.
- **Verde:** o resto.
- **Leitura ao lado do deslizante:** `v` · `t_on` · "a cada mm:ss" · pulsos/h · total · erro de partida (`t₀ / t_on`, ou "não medido").

### 4.6 Exemplo: 50 mL em 12 h, `Q_d` = 5,40 mL/min em `S_d` = 120

`Q̄` = 0,0694 mL/min, `d` = 1,29 %. O `t₀` = 0,15 s é **hipotético**, só para dar a ordem de grandeza.

| `t_on` | `v` (mL) | `T` | pulsos/h | pulsos em 12 h | erro sem compensar |
|---:|---:|---:|---:|---:|---:|
| 0,2 s | 0,018 | 15,6 s | 231 | 2 778 | 75 % |
| 0,5 s | 0,045 | 38,9 s | 93 | 1 111 | 30 % |
| 1,0 s | 0,090 | 1:18 | 46 | 556 | 15 % |
| 3,9 s (atual) | 0,350 | 5:02 | 12 | 143 | 3,9 % |
| 23,3 s | 2,083 | 30:00 | 2 | 24 | 0,6 % |

Encurtar o pulso só faz sentido **com `t₀` medido e compensado** (§6.3, `dose_dead_ms`).

### 4.7 Pulsos mais curtos: mudanças no firmware

1. `DOSE_MIN_ON_MS` = 200 ms, separado de `MIN_MOTOR_ON_TIME_MS` (que continua valendo para o PID). Folga entre pulsos de 0,5 s.
2. `dose_dead_ms` (0–2000):
   - tempo ligado = `60 000·v / Q_d + t₀`;
   - volume contado = `Q_d · max(0, run_ms − t₀) / 60 000`.
3. Prazo do pulso armado **no core 0 quando o duty sobe**, não no loop. Em 200 ms, os 2 a 4 ms de diferença pesam 1 a 2 %.
4. Checkpoint na NVS no máximo uma vez por minuto quando `T < 60 s` (F-14). O custo: numa queda de energia, até 1 min de volume entregue fica sem contar.

---

## 5. Desenho do cartão

### 5.1 Linha principal

| Coluna | Hoje | Proposto |
|---|---|---|
| Valor Lido | vazão | vazão instantânea (mL/min) |
| Setpoint | **volume** | **vazão-alvo agora**: `Q(t)` do perfil, ou `Q̄` em pulsos |
| Novo setpoint | combo do modo | seis modos: Const, Linear, Exp, Poli, Segmentos, **Pulsos** |
| Resumo | "5 perfis + gás prop." | dinâmico: `3,50 / 50,0 mL · próx. 2:13`, `12 / 60 min · 11,8 mL`, `Parada · 3,50 mL`, `Bloqueada: sensor seco` |

### 5.2 Gaveta

```
┌ Estado ──────────────────────────────────────────────────────────────────────────────────┐
│ ● Dosando · pulso 10 de 143 │ Sessão 3,500 mL [Zerar] │ Dosagem 3,50 / 50,0 mL (7 %)      │
│   motor: dosagem (drv)      │ (estimado)              │ Desvio −0,02 mL                   │
│ Calibração: curva ✗ (perfis contínuos indisponíveis) · ponto ✓ S 120 → 5,40 mL/min (2 d)  │
│ Sensor de líquido: desligado · Último reinício: há 3 h (energia)                          │
└──────────────────────────────────────────────────────────────────────────────────────────┘
  [ Contínuo (perfil) | Pulsos ]
┌ Parâmetros ───────────────────────────┐ ┌ Gráficos ──────────────────────────────────────┐
│ Meta  (•) [50,0] mL em [12,0] h        │ │ ① Vazão (mL/min) — primeiros 30 min            │
│       ( ) [0,0694] mL/min              │ │   █   █   █   █   █   █ … ─ ─ média 0,069       │
│ Ponto [S 120 → 5,40 mL/min ▾] Calibrar…│ ├────────────────────────────────────────────────┤
│ Granularidade                          │ │ ② Volume acumulado (mL) — 12 h                 │
│ muitos e pequenos ◄━━━━●━━━━► poucos e grandes │ planejado (escada) · real ● · agora │       │
│ ░░▒▒▒▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓ (zonas)       │ └────────────────────────────────────────────────┘
│ 0,35 mL · 3,9 s · a cada 5:02 · 12/h   │
│ 143 pulsos · erro de partida ≈ 1 %     │
│ ☑ Parar ao atingir 50,0 mL             │
│ ▸ Editar valores exatos                │
└────────────────────────────────────────┘
▸ Gás proporcional — desligado · Q_g agora 0,50 L/min · no fim 0,53 L/min
▸ Avançado — PID do nó (só contínuo) · potenciômetros · rede
Status: Dosagem confirmada pelo nó (eco).       [Reverter] [Parar dosagem] [Iniciar dosagem]
```

- **Faixa de estado** sempre visível, em blocos:
  - estado pelo `drv`;
  - volume da sessão com "Zerar" e a marca *estimado*;
  - volume do ciclo ou da dosagem com o desvio;
  - calibração: curva pelo `PumpCalCrc`, ponto de dosagem com a idade;
  - sensor de líquido (`sens`/`wet`);
  - último reinício (`boot`, `rst`).
- **Seletor *Contínuo / Pulsos*.** Sem curva no nó, o *Contínuo* mostra "Sem curva no nó: o motor não gira em perfil. Calibre a curva ou use Pulsos" e **Enviar perfil fica desabilitado**.
- **Dois gráficos empilhados**, com X compartilhado e um Y único em cada:
  - **① Vazão:** perfil planejado e `PumpFlow`; em pulsos, trem de pulsos numa janela de detalhe e a média `Q̄`;
  - **② Volume:** planejado (linha ou escada), real (`PumpCycleVol` ou `PumpDoseDelivered`), `PumpTargetVol` no contínuo e o marcador "agora".
- **Gás proporcional e Avançado** recolhidos. O gás mostra "Q_g no fim do plano" = `(V₀ + V_total/1000)·vvm`. O PID fica oculto em *Pulsos*.
- **Rodapé em *Pulsos*:** *Iniciar dosagem* (parâmetros e `dose:1` num quadro) e *Parar dosagem* (`dose:0`). O interruptor da linha continua sendo a parada segura (modo 0, depois roteamento off), que também cancela a dosagem.

### 5.3 Estados que o cartão distingue

| Estado | Como se detecta | O que o cartão mostra |
|---|---|---|
| Nó ausente | `PumpOnline` falso | tudo desabilitado; gráficos só com o planejado |
| Firmware sem os campos 3.13 | nenhum `PumpDrive`/`PumpDose*` | *Pulsos* desabilitado ("requer bomba 3.13"); estado pelo `PumpActive` antigo |
| Comando pendente | `PumpCommandPending` | chip "pendente"; com a 3.12.1 ele apaga em ≤ 3 s |
| Comando recusado | `PumpRejCmdId` muda | motivo traduzido de `PumpRejErr` (§7.3) |
| Dosando entre pulsos / pulso | `PumpDrive` = 5 / 6 | "próximo em mm:ss" / ponto pulsante |
| Bloqueado pelo sensor | `PumpLiquidOk` falso com motor pedido | faixa âmbar "Sensor de líquido seco: motor bloqueado"; `d_short` acumulado |
| Concluída | ativa → inativa com `entregue ≥ total` | "Concluída: 50,0 mL em 143 pulsos" |
| Interrompida | ativa → inativa antes do total | "Interrompida em 21,3 mL" e causa provável |
| Reiniciou | `PumpBootCount` muda | evento em *Eventos*, com `PumpResetReason`; gás proporcional segura o último valor |

### 5.4 Origem do tempo para a sobreposição ao vivo

- **Com bomba 3.13:** usar `PumpProfileMin` (`t_min`), o tempo do perfil no relógio do próprio nó. O gráfico fica alinhado mesmo depois de reabrir o app ou de uma queda de energia do nó.
- **Em pulsos:** `agora − (pulsos − 1)·T − (T − próximo)`.
- **Sem os campos novos:** `RunStartedUtc`, gravado quando o início é confirmado. Se não fechar com o estado do nó, mostrar só a série ao vivo, sem alinhar, e dizer isso no gráfico.
- As séries vêm de `TelemetryHistory.GetSeries(...)`, a 1 Hz.

---

## 6. Calibrações › Bomba Externa › Ponto de dosagem

### 6.1 Lugar e independência

- Card próprio na coluna direita da aba, acima de *Ensaio automático*, com o título **Ponto de dosagem (pulsos)**.
- Não depende da curva polinomial: um perfil de mangueira pode ter só o ponto (`HasFittedCurve = false`).
- Os pontos aparecem no gráfico S × Q como marcador próprio (losango).

```
┌ Ponto de dosagem (pulsos) ─────────────────────────────────────────────────────┐
│ Usado pela dosagem por pulsos. Não depende da curva polinomial.                │
│ Mangueira: Silicone 2 mm (perfil ativo)                                        │
│ 1  Encha a mangueira (Controle manual, abaixo)                                 │
│ 2  Velocidade S [120]   Duração [120] s   [Iniciar ensaio]   ▓▓▓▓▓░░░ 48 s     │
│ 3  Volume lido na proveta [10,80] mL                                          │
│    → 5,40 mL/min                              [Registrar repetição]            │
│    Repetições: 5,40 · 5,36 · 5,43 → média 5,40 mL/min · CV 0,6 %               │
│ ▸ Verificação em pulsos (recomendada para pulsos < 2 s)                        │
│    Pulso [1,0] s × [20]  intervalo [5] s  [Iniciar]   medido [1,72] mL         │
│    → 0,086 mL/pulso (esperado 0,090) · t₀ ≈ 40 ms · mínimo recomendado 0,8 s   │
│ [Salvar no perfil]  [Usar na dosagem]  [Enviar ao nó]                          │
│ Nó: S 120 → 5,40 mL/min · t₀ 40 ms (eco)                                       │
└────────────────────────────────────────────────────────────────────────────────┘
```

### 6.2 Ensaio contínuo, cronometrado pelo nó

- `pump_speed = S` com `pump_speed_ms` = **exatamente** a duração. O nó para sozinho, então a duração é a do nó.
- O app conta o tempo na tela e manda `speed 0` de reserva em `duração + 3 s`.
- Medida pelo volume lido na proveta, como em todas as calibrações da bomba: tempo definido → volume naquela velocidade → vazão (decidido pelo autor em 2026-10-11; sem balança).
- **Repetições:** 3 recomendadas; com CV > 3 % o app pede outra antes de salvar.

### 6.3 Verificação em pulsos (opcional, mede `t₀`)

- Usa a máquina de dosagem do nó: `dose_ml = Q_d·t_on/60`, período curto, `dose_total_ml = N·dose_ml`, `dose_dead_ms = 0` e `dose:1`.
- Ao terminar (`PumpDoseActive` cai com `pulsos = N`), o operador informa o volume lido na proveta.
- Cálculo: `v_real = V/N`; `t₀ = t_on − 60·v_real/Q_d`.
- Opcional: 2 ou 3 durações e regressão `v_real = (Q_d/60)·(t_on − t₀)`, que confirma a linearidade e dá o CV para a zona âmbar.
- `t₀` < 0 ou > 2 s: o app recusa e explica.

### 6.4 Intertravamentos

- Ensaio e verificação ficam bloqueados com dosagem ou perfil em execução (pelo `PumpDrive`), porque o `speed` os cancelaria.
- Medir, salvar no perfil e enviar ao nó continuam sendo atos separados.

### 6.5 Modelo de dados

```csharp
public sealed record PumpDosePoint
{
    public string Id { get; init; }
    public double SpeedUnits { get; init; }
    public PumpDoseRepetition[] Repetitions { get; init; }   // segundos (do nó), mL (proveta)
    public double RateMlMin { get; init; }
    public double CvPercent { get; init; }
    public PumpPulseCheck[] PulseChecks { get; init; } = [];  // OnMs, Count, VolumeMl
    public double? DeadTimeMs { get; init; }
    public double? RecommendedMinOnMs { get; init; }
    public string CapturedAtUtc { get; init; }
}
```

- `PumpCalibrationProfile` passa ao esquema 3, com `DosePoints[]`; a migração da v2 entrega lista vazia.
- `PumpControlSettings.Dose` guarda uma cópia do ponto em uso.

---

## 7. Contrato de fio (bomba 3.13 · Hub 10.9.0)

### 7.1 Comandos (app → Hub → nó)

| App → Hub | Hub → nó | Faixa | Observação |
|---|---|---|---|
| `pump_dose` | `dose` | 0 / 1 | iniciar / parar |
| `pump_dose_ml` | `dose_ml` | (0, 100] mL | |
| `pump_dose_period_s` | `dose_period_s` | [2, 86 400] s | |
| `pump_dose_speed` | `dose_speed` | [1, 1000] | |
| `pump_dose_rate_mlmin` | `dose_rate_mlmin` | (0, 1000] mL/min | |
| `pump_dose_total_ml` | `dose_total_ml` | ≥ 0 (0 = sem limite) | |
| `pump_dose_dead_ms` | `dose_dead_ms` | [0, 2000] ms | novo |

O Hub só acrescenta essas chaves a `simpleKeys`; a remoção do prefixo `pump_` já existe. A parada segura não muda: `{"mode":0}` já cancela a dosagem.

### 7.2 Push `/pumpData` (nó → Hub) e JSON do Hub (Hub → app)

Campos novos na 3.13; os atuais continuam iguais.

| Push | JSON do Hub | Tipo | Publicado |
|---|---|---|---|
| `drv` | `PumpDrive` | int: 0 parado · 1 potenciômetros · 2 velocidade manual · 3 perfil aguardando · 4 perfil rodando · 5 dosagem entre pulsos · 6 pulso · 7 OTA | com roteamento |
| `sens` | `PumpLiquidSensor` | bool: sensor de líquido habilitado | com roteamento |
| `wet` | `PumpLiquidOk` | bool: motor liberado pelo sensor (ou sensor desligado) | com roteamento |
| `t_min` | `PumpProfileMin` | min: tempo do perfil no relógio do nó | com roteamento |
| `up` | `PumpUptimeS` | s | sempre que online |
| `boot` | `PumpBootCount` | int: contador de boots na NVS | sempre que online |
| `rst` | `PumpResetReason` | int: `esp_reset_reason()` | sempre que online |
| `rej_cmd_id` | `PumpRejCmdId` | uint32: último comando recusado (0 = local) | sempre que online |
| `rej_err` | `PumpRejErr` | texto curto (§7.3) | sempre que online |
| `d_act` | `PumpDoseActive` | bool | com roteamento |
| `d_pulse` | `PumpDosePulse` | bool | com roteamento |
| `d_ml`, `d_per`, `d_spd`, `d_rate`, `d_tot`, `d_dead` | `PumpDoseMl`, `PumpDosePeriodS`, `PumpDoseSpeed`, `PumpDoseRate`, `PumpDoseTotal`, `PumpDoseDeadMs` | eco dos parâmetros | com roteamento |
| `d_done`, `d_n`, `d_next` | `PumpDoseDelivered`, `PumpDosePulses`, `PumpDoseNextS` | mL, int, s | com roteamento |
| `d_short` | `PumpDoseShortfall` | mL não entregues por bloqueio do sensor | com roteamento |

- Coeficientes da curva: a cada 10 pushes ou quando `cal_crc` muda (`cal_crc` vai em todo push). O Hub mantém o último valor visto, como já faz.
- Os campos de dosagem só são publicados depois do primeiro `d_act` visto (`pumpExtSeen`, zerado com `PUMP_TIMEOUT`). Com uma bomba 3.12, o JSON do Hub não muda.
- O buffer do push passa de `url[768]` para `url[1024]`, e o firmware confere o retorno do `snprintf`. Pior caso estimado: ~650 caracteres, com coeficientes e todos os campos novos.

### 7.3 Recusa (padrão do banho r3.2)

1. O nó **valida o quadro inteiro antes de aplicar**: faixa do modo, `num_segments`, calibração (atômica e forma C0+C1 numa cópia; ocupado), dosagem (`doseValidate`; ocupado) e `dose_dead_ms`.
2. Se qualquer parte falha, **nada é aplicado**:
   - o nó não dá ACK;
   - publica `rej_cmd_id=N&rej_err=<código>`;
   - guarda o id recusado para não reprocessar as reentregas.
3. O Hub, ao ver `rej_cmd_id == revision`, encerra o pendente sem reenviar e publica `PumpRejCmdId`/`PumpRejErr`.
4. Comandos locais (USB, `POST /command`) recusados publicam `rej_cmd_id=0` com o código.

Códigos de `rej_err`:

| Grupo | Códigos |
|---|---|
| Perfil | `mode_range`, `seg_range` |
| Calibração | `cal_busy`, `cal_incomplete`, `cal_st_range`, `cal_shape` |
| Dosagem | `dose_ml`, `dose_period`, `dose_speed`, `dose_rate`, `dose_total`, `dose_short`, `dose_fit`, `dose_busy`, `dose_dead` |

O app conhece seus próprios comandos, mas não o `cmd_id` que o Hub atribui. Por isso trata **cada mudança de `PumpRejCmdId`** como "o último comando foi recusado", e o eco esperado não chega.

### 7.4 Entrega na resposta do push (F-10)

- O Hub 10.9 responde ao `/pumpData` com `takeReliable(pumpBox)` em JSON (`{}` sem pendente).
- A bomba 3.13 processa essa resposta pelo mesmo caminho do poll, com a mesma deduplicação por `cmd_id`.
- O poll de 2 s fica de reserva.
- A bomba 3.12 ignora o corpo da resposta, então a troca é compatível.

### 7.5 Confirmação no app

- **Parâmetros:** ecos dentro de 0,0005 relativos em até 15 s. **Início:** `PumpDoseActive` sobe. **Recusa:** `PumpRejCmdId` muda.
- O ACK sozinho não confirma (§2.3). Com a 3.13, ele passa a significar "aceito e aplicado".

---

## 8. Plano de execução

### 8.1 Visão geral

| Fase | Entrega | Repositórios | Depende de | Versão | Tamanho |
|---|---|---|---|---|:---:|
| 0 | Correções críticas da bomba | bomba | — | bomba 3.12.1 | M |
| 1 | Contrato de integração da bomba | bomba | 0 | bomba 3.13 | G |
| 2 | Hub: repasse, publicação, recusa, entrega no push | Hub | 1 (contrato fechado) | Hub 10.9.0 | M |
| 3 | App: protocolo, domínio, simulador, correções de integração | app | 2 (contrato) | — | G |
| 4 | App: *Calibrações › Ponto de dosagem* | app | 3 | — | M |
| 5 | App: cartão reorganizado | app | 0 (pode começar já) | — | M |
| 6 | App: painel de pulsos com deslizante | app | 3, 4, 5 | — | G |
| 7 | App: sobreposição ao vivo e desvio | app | 3, 5 | — | P |
| 8 | Bancada integrada, documentação e liberação | todos | 0–7 | app 0.32.0 | M |

Tamanhos: P ≈ 1 dia · M ≈ 2–3 dias · G ≈ 4–6 dias de trabalho.

### 8.2 Dependências

```
Fase 0 ──► Fase 1 ──► Fase 2 ──► Fase 3 ──┬──► Fase 4 ──┐
   │                                      ├──► Fase 7   ├──► Fase 6 ──► Fase 8
   └──────────────► Fase 5 ───────────────┴─────────────┘
```

A fase 5 usa os campos 3.13 se existirem e cai no comportamento antigo se não. Pode ir para o usuário antes das fases 1–4.

### 8.3 Compatibilidade e ordem de gravação

| Bomba \ Hub | Hub 10.8.1 (atual) | Hub 10.9.0 |
|---|---|---|
| 3.12+dose2 (atual) | F-01 ativo: ACK preso, comandos perdidos | igual; `dose_*` chegam ao nó, mas sem eco o app não habilita *Pulsos*; resposta do push ignorada |
| **3.12.1** | **F-01 corrigido**, contrato igual: tudo que existe passa a funcionar | igual à esquerda; resposta do push ignorada |
| 3.13 | funciona; o Hub ignora os campos novos e não repassa `dose_*`. Comando recusado fica pendente até ser substituído (o nó não reprocessa) | **alvo** |

O app 0.32 com Hub 10.8 ou bomba 3.12.x: *Pulsos* desabilitado, cartão novo com estado pelo `PumpActive`. O prazo do controle manual funciona com qualquer Hub, porque `pump_speed_ms` existe desde o Hub 10.2.

**Ordem de gravação:** (1) bomba 3.12.1, já; (2) Hub 10.9.0; (3) bomba 3.13; (4) app 0.32.0. Cada passo é compatível com o anterior.

### 8.4 Portões

| Portão | Quando | Critério para seguir |
|---|---|---|
| **G0** | fim da fase 0 | Com Hub 10.8.1 e app 0.31.1 reais: (a) 10 comandos seguidos (perfil, PID, zerar, potenciômetros) confirmados, com `PumpCommandPending` apagando em ≤ 3 s; (b) reinício em repouso mantém `PumpVol`; (c) perfil, depois calibração por `speed`, depois reinício: o perfil **não** volta |
| **G1** | fim das fases 1–2 | Simulação no PC da bomba, contratos do Hub e da bomba verdes. Com hardware: campos novos no JSON do Hub; recusa provocada aparece em `PumpRejErr`; latência de comando medida pelo harness (`echo-latency.csv`) menor que antes |
| **G2** | fim das fases 3–7 | Testes do app verdes; harness contra o simulador e contra o Hub real, incluindo dosagem, recusa e reinício |
| **G3** | fase 8 | Roteiro de bancada da §10 cumprido e evidências arquivadas |

---

### Fase 0 — Correções críticas na bomba (3.12.1, contrato inalterado)

**Objetivo.** Fazer o que já existe funcionar com o Hub e o app atuais. O hello continua `ver=3.12`, então o catálogo do app não muda.

**Tarefas**

| # | Arquivo(s) | Mudança |
|---|---|---|
| 0.1 | `network/HubClient.h` (`pollHubForCommands`, `findJsonValueStart`) | **F-01:** função `parseJsonUint32(json, key, out)` com `strtoul`; recusa sinal, valor vazio e estouro. `cmd_id` passa a ser `uint32_t` do começo ao fim. Log do id aplicado. |
| 0.2 | `control/OperationController.h:301-317` | **F-04:** se `speed` chega com `g_opState` em `RUNNING` ou `WAITING`, chamar `clearRuntimeState()`. |
| 0.3 | `network/HubClient.h:91-101` | **F-04 (OTA):** ao iniciar o upload, `clearRuntimeState()` e `doseCancel("ota")`, conforme D9. |
| 0.4 | `storage/RuntimeStateStore.h`, `core/Lifecycle.h`, `control/OperationController.h`, `control/DoseController.h` | **F-03 e F-13:** chave `s_sess` com o contador de sessão. Gravar quando variar ≥ 0,05 mL e tiverem passado ≥ 30 s desde a última gravação, e sempre em `stop`, fim de `final_t`, `reset_volume` (grava 0 na hora), cancelamento ou conclusão da dosagem e início de OTA. Restaurar em **todo** boot, antes de `checkAndRecoverState`. O boot limpo zera só o PID. `doseRestore` deixa de escrever `vol`. Na recuperação de perfil, `vol = s_sess` e o início do ciclo = `s_cvol`. |
| 0.5 | `PwmRuntime.h`, `DoseController.h`, `OperationController.h`, `HubClient.h`, `TelemetryCodec.h`, `RuntimeStateStore.h`, `Lifecycle.h` | **F-12:** `portMUX_TYPE g_volMux` e auxiliares `volRead()`/`volAdd()`/`volSet()`; nenhum acesso direto a `g_cumulativeVolumeMl` fora deles. Em `doseHandleCommand`, ligar `g_doseActive` antes de ler a sessão. |
| 0.6 | `control/DoseController.h` | **F-14:** `doseSaveState` com limite de 60 s quando o período é menor que 60 s; sempre ao cancelar ou concluir. |
| 0.7 | `core/FirmwareApp.cpp` | `PUMP_FW_BUILD "3.12.1"`; banner e `fw_build` no JSON USB. |
| 0.8 | `tests/test_firmware_dose_contract.py`, `tests/test_firmware_v312_contract.py` | Asserções: `cmd_id` não passa por `getJsonFloatValue`; `clearRuntimeState` no caminho do `speed`; `s_sess` restaurado no boot; nenhum `g_cumulativeVolumeMl` fora dos auxiliares. |
| 0.9 | `CHANGELOG.md`, `docs/CURRENT_STATUS.md` | 3.12.1; `CURRENT_STATUS` sai de "3.10". |

**Commits**
1. `fix(pump): read cmd_id as an integer so the Hub's acks match`
2. `fix(pump): clear the recovery checkpoint when speed or OTA ends a profile`
3. `fix(pump): keep the session volume across reboots and reset_volume`
4. `fix(pump): guard the volume counter with a spinlock on both cores`
5. `fix(pump): checkpoint dosing state at most once a minute`
6. `docs(pump): 3.12.1 changelog and current status`

**Aceite.** Compila (registrar tamanho e SHA-256 em `tests/evidence`). Portão **G0**. Rodar o grupo B3 do harness contra o Hub real **antes e depois** e arquivar os dois `echo-latency.csv`, para registrar o efeito do F-01.

**Reversão.** Regravar o `.bin` da 3.12+dose2 arquivado. A chave `s_sess` é ignorada por ele.

---

### Fase 1 — Bomba 3.13: contrato de integração

**Objetivo.** Dar ao Hub e ao app o que eles precisam para mostrar e comandar a bomba sem adivinhar: recusas, quem comanda o motor, sensor, reinícios, tempo do perfil e dosagem.

**Tarefas**

| # | Arquivo(s) | Mudança |
|---|---|---|
| 1.1 | novo `src/protocol/JsonScan.h` | Extrair `findJsonValueStart`, `parseJsonUint32` e os leitores de número para um cabeçalho sem dependência de Arduino (testável no PC). |
| 1.2 | novo `src/control/CommandValidate.h`; `OperationController.h` | **F-02:** `CommandVerdict validateFrame(const char* json)` com as verificações da §7.3, sobre cópias. `processJsonCommand(json, CommandSource src)` só aplica se o veredito for aceito. Origens: `Hub`, `Usb`, `LocalHttp`. |
| 1.3 | `HubClient.h` (`pollHubForCommands`) | Recusa: guardar `g_lastRejectedHubCommandId` e `g_lastRejectCode`; não dar ACK; ignorar reentregas do mesmo id. Aceito: ACK como hoje. |
| 1.4 | `HubClient.h` (`sendDataToHub`) | Push com os campos da §7.2. `url[1024]`; conferir o `snprintf` e contar truncamentos (vai para o `/diag`). |
| 1.5 | `Lifecycle.h`, `FirmwareApp.cpp` | **F-05:** `g_drive` calculado no loop (enum da §7.2). **F-06:** `sens` = `sensorEnable && !sensorBypass`, `wet` = `allowRun`. Eco `pot=0` durante a dosagem. |
| 1.6 | `Lifecycle.h` (setup) | **F-08:** `boot` incrementado na NVS a cada inicialização; `rst = esp_reset_reason()`; `up = millis()/1000`. |
| 1.7 | `HubClient.h` | **F-15:** `t_min = g_current_t_min`. |
| 1.8 | `DoseController.h`, `PwmRuntime.h`, `FirmwareApp.cpp` | Dosagem 3.13: `dose_dead_ms` (NVS `d_dead`); `DOSE_MIN_ON_MS = 200`; folga de 0,5 s; prazo armado no core 0 quando o duty sobe (`g_doseArmMs` → `g_doseCutoffMs`); `d_short` acumulando `Q_d·(onMs − runMs)` quando o sensor bloqueia. |
| 1.9 | `OperationController.h` (`speed`) | **F-07:** `speed` com origem `Hub` e sem `speed_ms` recebe o prazo padrão `HUB_SPEED_DEFAULT_MS` (D12). USB e HTTP local continuam sem prazo, para a bancada. |
| 1.10 | `HubClient.h` | **F-11:** coeficientes a cada 10 pushes ou quando `cal_crc` muda. |
| 1.11 | `HubClient.h` (`sendDataToHub`) | **F-10:** se a resposta do push for JSON com `cmd_id`, processar pelo caminho do poll. |
| 1.12 | `FirmwareApp.cpp`, `Lifecycle.h`, `HubClient.h` | **F-09:** `PUMP_FW_VERSION "3.13"` usado no banner, hello, `/diag` e página OTA. `/diag` ganha `build`, `boot`, `rst`, `last_cmd_id`, `rej_cmd_id`, `rej_err`, `cal_crc`, `drv`, `dose_active`, `dose_done` e `push_trunc`, com teto de 511 bytes conferido. |
| 1.13 | novo `tests/host-sim/` (modelo do banho: `build.cmd` + MSVC + `stubs/`) | Cenários: `cmd_id` 999 999 001 e 4 294 967 295; quadro misto com calibração inválida não aplica nada; tabela do `doseValidate`; compensação `t₀`; prazo padrão do `speed` do Hub; reentrega de id recusado ignorada. |
| 1.14 | `tests/test_firmware_dose_contract.py` | Chaves do push, códigos `rej_err`, macro de versão. |
| 1.15 | `docs/PROTOCOL.md`, `CHANGELOG.md`, `docs/VALIDATION.md`, `docs/CURRENT_STATUS.md` | Contrato 3.13 (tabelas da §7), códigos, compatibilidade. |

**Commits**
1. `refactor(pump): move JSON scanning into an Arduino-free header`
2. `feat(pump): validate a whole frame before applying it and report refusals`
3. `feat(pump): report who drives the motor, the liquid sensor and reboots`
4. `feat(pump): publish the profile clock and dosing state to the Hub`
5. `feat(pump): pulse dead-time compensation and 200 ms minimum pulse`
6. `feat(pump): default deadline for Hub speed commands`
7. `perf(pump): send calibration echoes only on change or every tenth push`
8. `feat(pump): apply a command returned in the push response`
9. `feat(pump): single version macro and a fuller /diag`
10. `test(pump): host simulation of command parsing, validation and dosing`
11. `docs(pump): 3.13 wire contract`

**Aceite.** Compila; simulação no PC verde; dosagem por USB com `dose_dead_ms = 0` igual à 3.12.1; `/diag` ≤ 511 bytes; com o Hub 10.8.1, os comandos existentes continuam funcionando (§8.3).

---

### Fase 2 — Hub 10.9.0

**Objetivo.** Repassar a dosagem, publicar os campos novos, encerrar comandos recusados e entregar comandos na resposta do push.

**Tarefas**

| # | Arquivo(s) | Mudança |
|---|---|---|
| 2.1 | `src/protocol/Commands.h:885` | `simpleKeys` += `pump_dose`, `pump_dose_ml`, `pump_dose_period_s`, `pump_dose_speed`, `pump_dose_rate_mlmin`, `pump_dose_total_ml`, `pump_dose_dead_ms`. |
| 2.2 | `src/core/AppContext.h` | Variáveis de estado: drive, sensor, liquid, profile min, uptime, boot, reset reason, rej id/err, dose (12 campos) e `pumpExtSeen`. |
| 2.3 | `src/network/HttpServer.h:523-570` | `/pumpData` lê os campos novos sob `stateMutex`; `pumpExtSeen = true` no primeiro `drv` ou `d_act`. Recusa: `rejectReliable(pumpBox, rejId, err)`, que encerra o pendente se `rejId == revision`. Resposta: `takeReliable(pumpBox)` em `application/json`. |
| 2.4 | `src/protocol/Mailboxes.h` | `rejectReliable(...)`, ao lado de `ackReliable`, com registro `ESP32_EVT`. |
| 2.5 | `src/sensor/Telemetry.h:170-200, 431-459` | Instantâneo e publicação conforme a coluna "Publicado" da §7.2; zerar `pumpExtSeen` com `PUMP_TIMEOUT`, como `pumpEchoSeen`. |
| 2.6 | `Config.h`, `CHANGELOG.md` | `HUB_FIRMWARE_VERSION "10.9.0-dev"`. |
| 2.7 | `tests/contracts/test_pump_dose.py` (novo) | Repasse sem prefixo; `pump_dose` não casa com `pump_dose_ml`; nada sem `pumpCommOn`. |
| 2.8 | `tests/contracts/test_pump_reject.py` (novo) | `rej_cmd_id` igual à revisão encerra o pendente; diferente não; publicação de `PumpRejCmdId`/`PumpRejErr`. |
| 2.9 | `tests/contracts/test_pump_push_response.py` (novo) | `/pumpData` devolve o pendente; `{}` sem pendente. |
| 2.10 | `tests/contracts/test_node_commands.py`, `tools/verify_contract.py`, `docs/WIRE_CONTRACT_V9.md` | Atualizar listas e o documento de contrato. |

**Commits**
1. `feat(hub): forward pump dosing commands`
2. `feat(hub): read and publish the pump's drive, sensor, reboot and dosing fields`
3. `feat(hub): close a pump command the node refuses`
4. `feat(hub): return the pending pump command in the push response`
5. `test(hub): pump dosing, refusal and push-response contracts`
6. `docs(hub): 10.9.0 wire contract and changelog`

**Aceite.** Testes do Hub verdes. Com a bomba 3.12.1, o JSON do Hub é idêntico ao da 10.8.1, exceto o tipo da resposta do `/pumpData`, que ela ignora. Portão **G1** junto com a fase 1.

---

### Fase 3 — App: protocolo, domínio, simulador e correções de integração

**Objetivo.** O app entende o contrato novo e corrige os pontos de integração do lado dele, ainda sem a interface nova.

**Tarefas**

| # | Arquivo(s) | Mudança |
|---|---|---|
| 3.1 | `OpenTECHub.Protocol/CommandKeys.cs`, `CommandBuilders.cs` | Chaves da §7.1; `PumpDoseConfigure(plan)`, `PumpDoseStart(plan)` e `PumpDoseStop()`. |
| 3.2 | `CommandKeys.cs` (telemetria), `SensorReadings.cs`, `TelemetryParser.cs` | Campos da §7.2 como anuláveis (`null` = firmware antigo), invalidados quando a bomba some (como `PumpVolume`, `TelemetryParser.cs:635`). |
| 3.3 | novo `Services/Control/PumpDoseMath.cs` | Plano, faixa, zonas, deslizante ↔ período com arredondamento, `Validate` espelho do `doseValidate` 3.13, prévia (trem de pulsos e escada). |
| 3.4 | `Services/Communication/NodeFirmwareCatalog.cs:39` | Aceitar "3.13". A 3.12.1 anuncia "3.12" e já é aceita. |
| 3.5 | `ViewModels/PumpControlViewModel.cs` (`UpdateNodeStateReadouts`) | **F-05:** texto de estado pelo `PumpDrive`, se presente; senão, o atual. |
| 3.6 | `PumpControlViewModel.cs`, alarmes ou eventos | **F-06:** aviso "Sensor de líquido seco: motor bloqueado" quando `PumpLiquidOk` é falso com motor pedido (`drv` 2–6) por mais de 5 s. |
| 3.7 | `PumpControlViewModel.cs` (`OnTelemetryReceived`, `MaybeSendProportionalGas`) | **F-03 e F-08 no app:** reinício = `PumpBootCount` mudou, ou `PumpVolume` caiu > 0,05 mL sem `reset_volume` pedido. Gás proporcional segura o último `Q_g` até o volume voltar a subir; evento em *Eventos* com o motivo (`PumpResetReason` traduzido). |
| 3.8 | `PumpControlViewModel.cs`, `ExternalDeviceStatus.cs` | **F-02 no app:** mudança de `PumpRejCmdId` → mensagem com `PumpRejErr` traduzido, encerrando qualquer espera de eco em andamento. |
| 3.9 | `ViewModels/PumpCalibrationViewModel.cs` (`StartManual`, `StopManual`) | **F-07 no app:** `PumpManualSpeed(speed, 60000)` e renovação a cada 20 s enquanto `IsManualRunning`; parar no `Dispose` e ao perder o Hub. |
| 3.10 | `OpenTECHub.Simulator/DeviceModel.cs`, `WireCodec.cs`, `HttpEndpoint.cs` | Máquina de dosagem com os limites e códigos da 3.13; `drv`, sensor, `boot`/`rst`, recusa; comando para simular reinício do nó. |
| 3.11 | `OpenTECHub.Harness/BenchTestSuite.cs` (B3) | Casos: dosagem (configurar → eco → iniciar → N pulsos → fim), recusa (`dose_period` inválido), reinício (volume mantido, evento gerado), manual com prazo. |
| 3.12 | `tests/OpenTECHub.Tests` | `PumpDoseMathTests` (tabela da §4.6, bordas, `d > 0,5`, arredondamento ida e volta); parser com e sem campos novos; strings douradas dos comandos; VM: estado pelo `drv`, recusa, reinício e gás, prazo do manual. |

**Commits**
1. `feat(protocol): pump 3.13 command and telemetry keys`
2. `feat(control): pulse dosing math`
3. `fix(app): pump state from the node's drive field`
4. `fix(app): hold proportional gas across a pump reboot and log it`
5. `feat(app): show the pump's refusal reason`
6. `fix(app): manual pump speed always carries a deadline and renews it`
7. `feat(simulator): pump 3.13 dosing, refusals and reboots`
8. `test(app): pump dosing, refusal, reboot and manual deadline`

**Aceite.** Testes verdes; harness contra o simulador; com Hub 10.8 e bomba 3.12.1 o app se comporta como a 0.31.1 mais as correções 3.7 e 3.9.

---

### Fase 4 — App: *Calibrações › Ponto de dosagem*

**Tarefas**

| # | Arquivo(s) | Mudança |
|---|---|---|
| 4.1 | `Services/Calibration/PumpCalibrationProfile.cs`, store | `PumpDosePoint` e esquema 3, com migração 2 → 3 e teste de ida e volta. |
| 4.2 | novo `ViewModels/PumpDosePointViewModel.cs`, exposto por `PumpCalibrationViewModel.DosePoint` | Ensaio cronometrado pelo nó (§6.2), repetições com o volume da proveta, verificação em pulsos (§6.3), salvar, usar e enviar com eco. |
| 4.3 | `Views/CalibrationView.xaml` (aba `PumpTab`) | Card da §6.1; marcador dos pontos no gráfico S × Q. |
| 4.4 | `PumpDosePointViewModel.cs`, `PumpCalibrationViewModel.cs` | Intertravamento pelo `PumpDrive` (§6.4); mensagem "Pare a dosagem no Controle antes de calibrar". |
| 4.5 | `PumpCalibrationViewModel.cs` (`StartRun`) | **D6:** ensaio da curva com prazo exato do nó; `Seconds` = duração pedida quando o nó parar no prazo. |
| 4.6 | `tests/OpenTECHub.Tests` | VM com `TimeProvider` falso e dispatcher gravador; perfil só com ponto de dosagem salva e carrega sem curva. |

**Commits**
1. `feat(calibration): dose point model and hose profile schema 3`
2. `feat(calibration): node-timed dose point run, repetitions and pulse check`
3. `feat(calibration): dose point card in the pump tab`
4. `fix(calibration): time pump curve runs on the node`

---

### Fase 5 — App: cartão reorganizado (pode começar logo depois da fase 0)

**Tarefas**

| # | Arquivo(s) | Mudança |
|---|---|---|
| 5.1 | novo `Controls/ExternalPumpPanel.xaml(.cs)`; `Views/ControlView.xaml:2012-2234` | Extrair a gaveta para um UserControl com `DataContext` = `PumpControlViewModel`, sem mudar o comportamento (commit isolado). |
| 5.2 | `ControlView.xaml:1973-1997`, `PumpControlViewModel.cs` | Linha principal da §5.1: `TargetFlowNowText`, `SummaryText`. |
| 5.3 | `ExternalPumpPanel.xaml` | Faixa de estado da §5.2, usando campos 3.13 quando houver. |
| 5.4 | `ExternalPumpPanel.xaml`, `PumpControlViewModel.cs` | Seletor *Contínuo / Pulsos* (*Pulsos* desabilitado até a fase 6); faixa "sem curva no nó"; `CanApply` exige `PumpCalCrc` presente. |
| 5.5 | novo `Controls/PumpPlanChart.xaml(.cs)`; estilo comum extraído de `PumpPreviewChart` | Dois `WpfPlot` empilhados com X sincronizado; substitui o gráfico de dois eixos. |
| 5.6 | `ExternalPumpPanel.xaml` | Gás proporcional e Avançado recolhidos com resumo; "Q_g no fim do plano". |
| 5.7 | testes WPF (host STA) | Painel nos dois temas; capturas claro e escuro em `docs/evidence`. |

**Commits**
1. `refactor(ui): move the external pump drawer into its own control`
2. `feat(ui): pump row shows the target flow and a live summary`
3. `feat(ui): pump status strip with calibration state`
4. `feat(ui): continuous/pulses selector and no-curve guard`
5. `feat(ui): stacked flow and volume charts for the pump`
6. `feat(ui): collapse proportional gas and advanced pump settings`

---

**Feito na fase 5 (2026-10-12), além do previsto:** Calibrações › Bomba Externa dividida em *Curva polinomial · modo contínuo* e *Ponto de dosagem · modo pulsos*, com gráficos próprios do ponto e indicadores `Curva ✓/✗ · Ponto ✓/✗` na biblioteca (D-076); correção dos atalhos de Calibrações, que abriam a aba errada.

### Fase 6 — App: painel de pulsos com deslizante

**Tarefas**

| # | Arquivo(s) | Mudança |
|---|---|---|
| 6.1 | novo `ViewModels/PumpDosingViewModel.cs`; `Services/Persistence/AppSettings.cs` | VM filho exposto por `PumpControlViewModel.Dosing`; `PumpDoseSettings` (meta, modo de entrada, período escolhido, intervalo máximo, meta de precisão, cópia do ponto, `RunStartedUtc`). |
| 6.2 | `ExternalPumpPanel.xaml`, estilo de `Slider` | Deslizante com as três zonas na trilha; leitura ao vivo; expansor "Editar valores exatos". |
| 6.3 | `PumpDosingViewModel.cs` | Iniciar e parar pelo árbitro (`ActuatorId.ExternalPump`); confirmação por eco (§7.5); estados da §5.3; recusas traduzidas. |
| 6.4 | `PumpPlanChart` | Modo pulsos: trem de pulsos (janela de detalhe) e escada de volume. |
| 6.5 | `PumpDosingViewModel.cs` | "Calibrar…" via `ShellViewModel.OpenCalibration`. |
| 6.6 | `tests/OpenTECHub.Tests`, harness | Deslizante ↔ período, recorte ao mudar a meta, eco, recusa, dono ≠ manual; harness contra o simulador. |

**Commits**
1. `feat(app): pulse dosing view model and settings`
2. `feat(ui): granularity slider with precision zones`
3. `feat(app): start and stop pulse dosing with echo confirmation`
4. `feat(ui): pulse train and volume staircase charts`

---

**Feito na fase 6 (2026-10-12):** 6.1–6.5 conforme a tabela (D-078). Diferenças: o plano fica congelado durante a dosagem; uma dosagem encontrada no nó abre o painel sozinha; a faixa de zonas fica sob a trilha do deslizante. 6.6: testes de VM em `PumpDosingTests` (deslizante ↔ período, recorte, eco, recusa, prazo de 15 s, dono ≠ manual, fim concluído/interrompido/parado) e captura `bomba-externa-pulsos.png`; o harness contra o simulador continua com B3.12–B3.14 da fase 3, sem um caso novo de VM.

### Fase 7 — App: sobreposição ao vivo e desvio

**Tarefas**

| # | Arquivo(s) | Mudança |
|---|---|---|
| 7.1 | `PumpPlanChart`, `PumpControlViewModel.cs` | Séries de `TelemetryHistory.GetSeries(PumpFlow / PumpVolume)`, alinhadas por `PumpProfileMin` (3.13) ou por `RunStartedUtc` (§5.4), atualizadas a 1 Hz. |
| 7.2 | `Services/Telemetry/TelemetryHistory.cs` | Se necessário, canal `PumpDoseDelivered` acrescentado **no fim** do enum. |
| 7.3 | `PumpControlViewModel.cs` | Desvio = real − planejado no tempo atual; alerta acima de 5 % por mais de 2 períodos. |

**Commits**
1. `feat(ui): live pump series over the planned curves`
2. `feat(app): pump delivery deviation and alert`

---

**Feito na fase 7 (2026-10-12):** séries ao vivo e desvio com alerta (5 %, dois períodos). 7.1 e 7.2 mudaram: as amostras ficam num buffer do próprio cartão (`PumpLiveSeries`) no relógio do plano, em vez de `TelemetryHistory.GetSeries`, e nenhum canal foi acrescentado ao histórico (D-078 item 6). Sem `t_min` não há alinhamento por `RunStartedUtc`: o cartão só avisa.

### Fase 8 — Bancada integrada, documentação e liberação

**Tarefas**

| # | Onde | Mudança |
|---|---|---|
| 8.1 | bancada | Roteiro da §10; evidências em `bomba-peristaltica/tests/evidence` e `Windows_app/docs/evidence`. |
| 8.2 | `Windows_app/docs/DECISIONS.md` | D-075 dosagem por pulsos e deslizante · D-076 ponto de dosagem separado da curva · D-077 coluna Setpoint = vazão-alvo · D-078 recusa e entrega no push para a bomba. |
| 8.3 | `Windows_app/docs/CHANGELOG.md`, `Directory.Build.props`, `MANUAL_DO_OPERADOR.md` | 0.32.0; seção "Dosar volumes pequenos" e "Calibrar o ponto de dosagem". |
| 8.4 | `External-Devices/docs/COMANDOS_DISPOSITIVOS_EXTERNOS.md` §1 | Tabela de prontidão (de 21/09) com 3.12.1 e 3.13, dosagem, recusa, campos novos e checklist §1.11 atualizado. |
| 8.5 | `ESP32S3-HUB/CHANGELOG.md`, `bomba-peristaltica/CHANGELOG.md` | Marcar versões liberadas. `DOWNLOADS.md` na raiz, se listar binários. |

---

**Feito na fase 8 (2026-10-12):** 8.2 decisões D-075 a D-079 (a numeração mudou durante as fases: D-078 é o painel de pulsos e a sobreposição; D-079 a tabela do ponto); 8.3 `CHANGELOG` 0.32.0 (a versão sai da tag `v0.32.0` pelo MinVer, criada só no release), manual do operador e documentação do app; 8.4 tabela de prontidão e checklist §1.11 (itens 12–15) em `COMANDOS_DISPOSITIVOS_EXTERNOS.md`; 8.5 changelogs da bomba e do Hub com binários e SHA. `DOWNLOADS.md` não muda: aponta para o release mais recente. 8.1 (bancada): roteiro com campos de resultado em `bomba-peristaltica/docs/BANCADA_3.13.md`; harness ganhou B3.16 (parada segura cancela a dosagem).

---

## 9. Testes automatizados

| Camada | Onde | O que cobre |
|---|---|---|
| Bomba, simulação no PC (novo) | `bomba-peristaltica/tests/host-sim` | parse de `cmd_id` grande; validação de quadro inteiro; `doseValidate`; `t₀`; prazo do `speed` do Hub; reentrega recusada |
| Bomba, contrato textual | `tests/test_firmware_*_contract.py` | chaves do push, `rej_err`, macro de versão, auxiliares do volume, `s_sess` |
| Hub | `tests/contracts/test_pump_*.py` | repasse, prefixo, publicação condicionada, recusa encerra pendente, resposta do push, compatibilidade com 3.12 |
| Protocolo | `OpenTECHub.Tests` | strings douradas; parser com e sem campos 3.13; invalidação |
| Matemática | `PumpDoseMathTests` | tabela da §4.6; bordas; inviabilidade; arredondamento; espelho do validador |
| ViewModels | `PumpDosingViewModelTests`, `PumpDosePointViewModelTests`, `PumpControlViewModelTests` | eco e tempo-limite; recusa; zonas; reinício e gás; prazo do manual; intertravamento |
| Persistência | `PumpCalibrationProfileStoreTests` | esquema 2 → 3; perfil só com ponto |
| Simulador / harness | `BenchTestSuite` B3 | dosagem completa; recusa; reinício; **parada segura cancela a dosagem** |
| Renderização | testes WPF | painel nos dois temas; *Pulsos* desabilitado com firmware antigo |

---

## 10. Roteiro de bancada (proveta graduada, água, mangueira do cultivo)

**Integração (G0 e G1)**
1. 10 comandos seguidos pelo app (perfil, PID, zerar, potenciômetros, perfil): cada um confirmado e `PumpCommandPending` apagando em ≤ 3 s.
2. Reinício da bomba em repouso: `PumpVol` igual antes e depois; evento "reiniciou" no app (3.13).
3. Perfil rodando, depois *Controle manual* (`speed`), depois reinício: o perfil **não** volta.
4. Reinício do Hub com a bomba ligada, depois *Zerar volume*: aplicado na primeira entrega.
5. Recusa provocada (dosagem com período menor que o pulso): o motivo aparece no cartão e nada muda no nó.
6. Fechar o app com *Encher mangueira* ativo: o motor para em ≤ 60 s.
7. Sensor de líquido habilitado e seco durante a dosagem: faixa âmbar e `d_short` crescendo.

**Dosagem (G3)**

8. Ponto de dosagem: S = 120, 3 × 120 s, volume na proveta. Aceite: CV ≤ 2 %.
9. Linearidade: `t_on` ∈ {0,2; 0,5; 1; 2; 4} s, 30 pulsos cada. Ajustar `t₀`. Aceite: resíduo ≤ 3 % a partir do mínimo recomendado.
10. Compensação: 0,5 s e 1 s com `dose_dead_ms = t₀`. Aceite: erro médio ≤ 3 %.
11. Execução longa: 50 mL em 12 h. Medir o volume na proveta no fim e comparar com `PumpDoseDelivered` e `PumpVol`. Aceite: ≤ 3 %.
12. Queda de energia no meio: retoma em ~5 s; perda ≤ 1 min de dosagem + 1 pulso.
13. `reset_volume` durante a dosagem e depois reinício: `vol` não volta.
14. Hub mudo por 2 min: pulsos com a duração certa.
15. Parada segura pelo interruptor durante um pulso: motor para em < 1 s; dosagem cancelada.

---

## 11. Riscos

| Risco | Mitigação |
|---|---|
| Motor DC escovado **sem encoder**: o volume de um pulso curto varia com a posição do rolete | Verificação em pulsos mede o CV real; zona âmbar a partir dele |
| `t₀` muda com mangueira, temperatura e desgaste | `t₀` no perfil, com data visível; sem vencimento (D7); recalibrar é decisão do operador |
| Volume continua **estimado** | Marca "estimado"; recomendação de conferir o volume na proveta no fim |
| Validar antes de aplicar muda o comportamento de quadros mistos | O app nunca mistura tipos num quadro; simulação no PC cobre o caso; documentado em `PROTOCOL.md` |
| Bomba 3.13 com Hub 10.8: comando recusado fica pendente | O nó não reprocessa; o próximo comando substitui; recomendação de atualizar o Hub junto (§8.3) |
| Contador de boots e `s_sess` aumentam as gravações na NVS | `s_sess` com limite de 30 s e ≥ 0,05 mL; `boot` uma vez por inicialização |
| `/diag` maior que 511 bytes seria cortado pelo Hub | Teto conferido na simulação no PC |
| Receita que manda perfil durante a dosagem | O nó cancela a dosagem; o cartão mostra "interrompida por perfil" |
| Arquivos grandes (`ControlView.xaml`, `PumpControlViewModel.cs`) | Extração na fase 5.1 e VM filho na 6.1 antes de acrescentar a dosagem |

---

## 12. Fora de escopo agora

- **Pulsos seguindo um perfil:** o nó integra `V_alvo(t)` e dispara um pulso quando `V_alvo − V_entregue ≥ v`. Daria perfis de alimentação sem curva polinomial. Depende dos resultados da §10.
- **Bloco de receita "Bomba — pulsos"** em `RecipeEngine.ExternalDevices.cs`.
- **Entrega no push e recusa para os outros nós** (distância, biomassa, agitador). Os mesmos ganhos da F-10/F-02, em plano próprio.
- **Coluna da dosagem no registro de sessão:** só no fim, para não deslocar colunas antigas.

---

## 13. Decisões

**2026-10-11:** D7 decidido pelo autor (sem validade); as demais seguem a recomendação abaixo.

| # | Decisão | Recomendação |
|---|---|---|
| D1 | O deslizante troca só a granularidade? | Sim; a velocidade vem do ponto escolhido. Para pulsos mais longos com o mesmo volume, calibra-se um segundo ponto com S menor. |
| D2 | Intervalo máximo padrão entre pulsos | 30 min, editável. |
| D3 | Onde mora o ponto de dosagem | No perfil de mangueira (esquema 3), com cópia em `PumpControlSettings.Dose`. |
| D4 | Mínimo de pulso no firmware | 200 ms, com zona âmbar acima disso enquanto não houver verificação. |
| D5 | Coluna *Setpoint* da linha principal | Vazão-alvo agora; volume no resumo e na faixa de estado. |
| D6 | Ensaio da curva também cronometrado pelo nó | Sim (fase 4.5). |
| D7 | Validade do ponto de dosagem | **Decidido (2026-10-11): sem validade.** O cartão mostra a data do ponto, sem aviso de vencimento. |
| D8 | Meta de precisão da zona âmbar | 5 %, editável em *Avançado*. |
| D9 | OTA no meio de um perfil: retomar depois de gravar? | Não. Limpar o checkpoint e cancelar a dosagem; o operador reinicia explicitamente. |
| D10 | Recusa: quadro inteiro ou parcial? | Quadro inteiro, nada aplicado, sem ACK, com `rej_cmd_id`/`rej_err`: o mesmo padrão do banho. |
| D11 | Entrega do comando na resposta do push | Sim, mantendo o poll de 2 s de reserva; reavaliar o período do poll depois de G1. |
| D12 | Prazo padrão para `speed` vindo do Hub sem `speed_ms` | 300 s. O app sempre manda prazo próprio (60 s, renovado). |
| D13 | Coeficientes da curva no push | A cada 10 pushes ou quando o CRC muda. |
