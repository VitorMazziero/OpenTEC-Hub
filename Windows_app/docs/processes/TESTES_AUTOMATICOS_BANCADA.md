# Testes automáticos de bancada — Hub 10.2 + nós v11/3.9 + app

**Data:** 2026-09-12
**Escopo:** o que pode ser verificado **sem operador na frente da tela** uma vez que o PC está
ligado ao Hub (USB ou Wi-Fi), os cinco nós estão energizados e a frota está regravada
(Hub `10.2.0-dev`; distância `v11`, fluxômetro `v11`, bomba `3.9`, biomassa `v11`, agitador `v10`).
Cobre os recibos de bancada pendentes dos planos de 11/09 e 12/09 ([D-051](../DECISIONS.md),
[D-052](../DECISIONS.md), `ROADMAP.md › Field readiness`).
**O que este documento não é:** validação de processo (kLa, potência, cultivo). Aqui só se prova
que o **enlace e o contrato** se comportam como o código e os testes de unidade assumem.

Cada teste tem: pré-condição, o que é executado, o critério de aprovação **numérico** e o arquivo
de evidência que produz em `docs/evidence/bench/<data>/`. Um teste sem número de aprovação não é
teste — é inspeção.

---

## 0. Como isto se encaixa no que já existe

| Camada | Já existe | Cobre | Falta |
|---|---|---|---|
| Unidade/contrato (sem hardware) | `dotnet test` (1497), `python -m unittest discover ESP32S3-HUB/tests/contracts` (72), `Test-HubDeviceContracts.ps1`, `Test-FirmwareBaselines.ps1` | Vocabulário, parsers, golden strings, invariantes de fonte | Nada do que abaixo depende de rádio, heap ou tempo real |
| Enlace Wi-Fi do Hub | `opentec-harness wifi-test [ip]` (7 passos: ambiente, HTTP cru, ETag, transporte, latência, soak, reconexão) | `/readData`, `/command`, RTT, perdas por 10 min | Não conhece os nós, os ecos, `/nodes`, `/nodeDiag` |
| Enlace USB | `opentec-harness usb [COM] --for <s> --probe` + `reset-test` | Handshake, período de emissão, o pulso DTR | Idem |
| Smoke do app | `OpenTECHub.exe --workspace <dir> --no-workspace-prompt --nav <página> --exit-after-ms <ms>` | Página abre sem exceção | Não conecta ao Hub |

Os testes abaixo estão organizados em cinco suítes (B1–B5). **Estado em 12/09/2026 — tudo o que
é automatizável sem relé está implementado:**

| Suíte | Onde vive | Como rodar | Cobertura |
|---|---|---|---|
| Pré-condições, B1, B2, B3, B5 | `opentec-harness bench-test` (`Windows_app/src/OpenTECHub.Harness/BenchTestSuite.cs`) | §7 | 28 testes automáticos + 13 que exigem relé/cabo/manual (marcados SKIP com o motivo no relatório) |
| B4 | `Windows_app/tests/OpenTECHub.Tests/BenchArbiterTests.cs` (xUnit, `CommandArbiter` real) | `dotnet test --filter BenchArbiterTests` | B4.1–B4.5 — o árbitro é determinístico e não precisa de hardware; a metade de fio do mesmo contrato é B3 |
| B5.1 | `opentec-harness wifi-test <ip>` (já existia) | manual | linha de base do enlace |

Ensaio a seco contra o simulador (`opentec-simulator http --port 8080 --all-nodes`, alvo
`127.0.0.1:8080`): **28 PASS · 1 WARN · 13 SKIP · 0 FAIL**. O WARN é B3.2 (o PC não alcança o IP
fictício do nó), esperado fora da rede do Hub. Escrever a suíte já rendeu um defeito real antes de
qualquer placa ser ligada: o leitor USB drenava para a linha mais nova e teria descartado quatro das
cinco respostas `NodeDiag` (corrigido em `SerialLineCoalescer`, `PROTOCOL.md` §1.2).

---

## 1. Pré-condições comuns (verificadas automaticamente antes de qualquer suíte)

| # | Verificação | Como | Aprovação |
|---|---|---|---|
| P1 | Hub responde | `GET http://<hub>/ping` (Wi-Fi) ou linha de telemetria em ≤ 5 s (USB) | 200 / 1 quadro |
| P2 | Versão do Hub | `HubFirmwareVersion` no quadro | `== "10.2.0-dev"` (ou a versão alvo passada por parâmetro) |
| P3 | Protocolo | `HubProtocolVersion` | `== 10` |
| P4 | Frota regravada | `GET /nodes` | cinco entradas `registered:true`, `version` ∈ {`v11`,`v10`(agitator),`3.9`,`v11`,`v11`} — qualquer outro valor **reprova** (não há retrocompatibilidade) |
| P5 | Presença | quadro agregado | `DistanceOnline`, `AgitatorOnline`, `PumpOnline`, `FlowmeterOnline`, `BiomassOnline` todos `true` por 30 s consecutivos |
| P6 | App fechado | `Get-Process OpenTECHub` | nenhum (a porta USB é exclusiva) |
| P7 | Estado seguro | quadro | `flowSetpoint == 0`, motor 0, `PumpCommEnabled` como estava (anotado para restaurar) |

Se P4 falhar, a suíte para e imprime **qual nó** está na versão errada e o comando
`Publish-OtaFirmware.ps1 -Device <nó> -Compile` que resolve.

---

## 2. Suíte B1 — Quadro agregado e identidade (D-051)

Objetivo: fechar o recibo §6.3 do plano de identidade.

| # | Teste | Execução | Aprovação | Evidência |
|---|---|---|---|---|
| B1.1 | Tamanho do quadro com tudo ecoado | 60 `GET /readData` a 1 Hz depois de P5; anotar `Content-Length` máx/mediana | máx **≤ 2600 B**; se > 2600 e ≤ 3072, *aviso* com a lista de cortes (`FlowOutput`, `FlowSetpointCorrected`); > 3072 reprova (reserva estoura) | `frame-size.csv` |
| B1.2 | Quadro parseável em todos os 60 | `TelemetryParser.Parse` de cada corpo | 60/60 `ParseOutcome.Telemetry`, 0 `Malformed` | idem |
| B1.3 | Identidade completa | `*IP` ≠ `0.0.0.0`, `*NodeVer` e `*NodeMac` presentes nos cinco | 15/15 chaves | `identity.json` |
| B1.4 | `/nodes` coerente com o quadro | comparar `ip`/`version`/`mac` de `/nodes` com as chaves do quadro | 0 divergências | idem |
| B1.5 | `age_ms` honesto | `hub_time_ms − max(last_hello_ms,last_data_ms)` vs `age_ms` | igual ± 50 ms nos cinco | idem |
| B1.6 | Reboot de um nó é visto | (opcional, precisa de relé ou OTA) `Publish-OtaFirmware -Device distance` → esperar | `last_hello_ms` novo em ≤ 30 s; `DistanceOnline` volta em ≤ 60 s | `reboot-distance.log` |
| B1.7 | Período USB | 120 linhas por serial | período mediano 2000 ± 100 ms; nenhum gap > 5 s | `usb-period.csv` |

---

## 3. Suíte B2 — Saúde dos nós e heap do Hub (Etapa 8, Q8)

| # | Teste | Execução | Aprovação | Evidência |
|---|---|---|---|---|
| B2.1 | Heap depois da tarefa | ler a linha `NodeDiag task criada; heap antes=… depois=…` no serial de boot (reiniciar o Hub por `{"restart":1}` não basta — é reset físico ou DTR: `opentec-harness reset-test`) | `depois` **> 150 000 B**; `antes − depois` < 12 000 B | `hub-heap.txt` |
| B2.2 | `/nodeDiag` completo | `GET /nodeDiag` após ≥ 40 s de P5 | cinco entradas `code:200`, `diag` ≠ `null`, `age_ms` < 35 000 | `nodediag.json` |
| B2.3 | Métricas comuns presentes | em cada `diag`: `uptime_s`, `free_heap`, `rssi`, `hub_fail_streak`, `ota` | 5 × 5 chaves; `rssi` entre −90 e −20; `ota:false` | idem |
| B2.4 | Corpo dentro do teto | `len(json.dumps(diag))` | **≤ 480 B** por nó (folga de 31 B para o 511) — acima disso o próximo campo do firmware vira `diag:null` | idem |
| B2.5 | `?dev=` filtra | `GET /nodeDiag?dev=pump` | 1 entrada, `dev == "pump"` | — |
| B2.6 | Serial `nodeDiag all` | enviar `{"nodeDiag":"all"}\n`; ler 3 s | exatamente 5 linhas `{"NodeDiag":…}`, cada uma < 1024 B, parseáveis, telemetria continua entre elas | `nodediag-serial.log` |
| B2.7 | Serial `nodeDiag` desconhecido | `{"nodeDiag":"xyz"}` | 1 linha com `code:404`; o quadro seguinte chega no período normal | idem |
| B2.8 | Pedido não muda estado | hash: `GET /readData` antes e depois de B2.6 — comparar todos os setpoints/rotas | 0 diferenças | — |
| B2.9 | Nó desligado (opcional, relé) | desligar a biomassa; esperar 90 s | `/nodeDiag`: `biomass.code` ∈ {−1, 0…} ≠ 200 **ou** `age_ms` crescendo > 60 000; `BiomassOnline:false`; os outros quatro continuam `200` | `node-off.json` |
| B2.10 | A varredura não custa push | comparar `hub_fail_streak` dos cinco e o número de gaps > 5 s em `*Online` durante 30 min com a varredura (normal) — não há como desligá-la em runtime, então o comparativo é com o Hub `10.1` gravado antes (B5.1 guarda a linha de base) | `hub_fail_streak` máx ≤ 2 em todos; gaps = 0 | `soak-30min.csv` |

---

## 4. Suíte B3 — Ecos e comandos de configuração (Etapas 1–7, D-052)

Cada teste: enviar **um** comando pelo Hub (nunca direto no nó), esperar o eco no quadro, restaurar
o valor original ao fim. Latência medida do envio ao primeiro quadro com o eco novo.

| # | Nó | Comando (via `/command` ou serial) | Eco esperado | Aprovação | Restaura |
|---|---|---|---|---|---|
| B3.1 | Distância | `{"distanceOffsetMm":25.5}` | `DistanceOffsetMm == 25.5` | ≤ **20 s** em operação normal (o nó só acorda para o push; o plano diz 2 s, mas 15 s é o backoff — o número real vai para o recibo); `DistanceCommandPending` `true` no meio, `false` no fim | offset original |
| B3.2 | Distância | `GET http://<ip-distância>/config` (leitura direta é permitida; escrita não) | `offset_mm` `25.50` | igual ao eco; **sem resposta = WARN** (o PC pode não rotear para a sub-rede do nó) | — |
| B3.3 | Distância | persistência: OTA/reset do nó (B1.6) | `DistanceOffsetMm` continua 25.5 depois do reboot | sim | offset original + reboot |
| B3.4 | Distância | os quatro numa frame (`offset`, `sample`, `send`, sem `reset`) | os três ecos mudam **no mesmo quadro** | sim | originais |
| B3.5 | Fluxômetro | `{"flowKp":<kp+0.01>}` com setpoint 0 e sem ensaio | `FlowKp` novo | ≤ 5 s (poll `/flowCommand`) | kp original |
| B3.6 | Fluxômetro | `{"flowRampRate":…}`, `{"flowFfGain":…}`, `{"flowFfOffset":…}`, `{"flowKi":…}` | cada eco | ≤ 5 s cada | originais |
| B3.7 | Fluxômetro | `FlowmeterBootId` estável | 60 quadros | valor constante; muda **só** em B1.6 | — |
| B3.8 | Bomba | `{"pump_command":"reset_volume"}` com `pumpComm:1`, `mode:0` | `PumpVol` | **< 0.05 mL no quadro seguinte** ao ack | — |
| B3.9 | Bomba | `{"pumpSlope":s,"pumpIntercept":i}` (valores atuais + 1 %) | `PumpSlope`/`PumpIntercept` | ≤ 5 s, iguais com 4 casas | originais |
| B3.10 | Bomba | `{"pumpPidKp":…}` | **nenhum** eco (3.9 não ecoa) | quadro não ganha chave `PumpPidKp`; Hub loga `Applied`; nenhuma falha de parse | — |
| B3.11 | Bomba | quadro de segurança: `{"mode":0}` depois `{"pumpComm":0}` | bomba **parada** (`PumpFlow == 0` por 10 s) e depois `PumpOnline` continua `true` (o nó não some, só a rota) | sim | `pumpComm` como estava |
| B3.12 | Biomassa | três comandos em sequência, um por confirmação: `set_gear`, `set_it`, `set_pwm` | `BiomassGear`, `BiomassIT`, `BiomassPWM` | três `cmd_id` distintos no log do Hub, três acks, ecos em ≤ 10 s cada; `BiomassCommandPending` cai entre eles | originais |
| B3.13 | Biomassa | `{"biomassEma":0.5}`, `{"biomassProbePeriodMs":100}` | `BiomassEma 0.5`; `BiomassProbePeriodMs` **≥ piso térmico** (o nó eleva) | período ecoado ≥ 100 e documentado | originais |
| B3.14 | Todos | eco **não é sticky** | desligar a rota (`*Comm:0`) ou o nó | a chave de eco **some** do quadro em ≤ 2 períodos | — |
| B3.15 | Todos | valores fora de faixa | `{"biomassIt":9}`, `{"pumpSlope":-1}` | Hub recusa ou nó ignora; **nenhum** eco muda; nenhuma falha de parse | — |

Latência registrada por comando em `echo-latency.csv` (`node,key,sent_ms,echo_ms,latency_ms`).

---

## 5. Suíte B4 — Árbitro e interlocks vistos do fio

O árbitro vive no app e é **determinístico**: não depende de rádio, heap nem tempo real. Por isso
B4 não é um modo de bancada — é `BenchArbiterTests.cs` na suíte xUnit, através do `CommandArbiter`
real com um `RecordingDeviceService` que captura o JSON exato. O que o hardware acrescentaria (o
eco chegar ou não) já é B3. Roda em todo `dotnet test`; para isolar:

```bash
dotnet test Windows_app/tests/OpenTECHub.Tests/OpenTECHub.Tests.csproj --filter "FullyQualifiedName~BenchArbiterTests"
```

| # | Teste | Aprovação |
|---|---|---|
| B4.1 | Sintonia recusada durante ensaio | `Aeration` reivindicada por `Automatic` (o que o runner de potência/kLa faz em captura) → `SendTuning` não escreve nada e `TuningStatusText` diz "recusado" — **implementado** |
| B4.2 | Sintonia aceita fora de ensaio | mesmo comando sem dono → frame exato `{"flowKp":0.9,"flowKi":0.15,"flowFfGain":0.106,"flowFfOffset":0.0,"flowRampRate":1.0}` — **implementado** (o eco é B3.5) |
| B4.3 | `nodeDiag` não toma posse | com Aeração e Agitação sob `Automatic`, `RequestNodeDiag("all")` escreve `{"nodeDiag":"all"}`, nenhum dono muda e `OwnershipChanged` não dispara — **implementado** |
| B4.4 | Safe-stop fecha o gás e para a bomba | `SafetyCoordinator` com os frames do `ControlViewModel`: 1.º frame contém `"mode":0` e `"flowSetpoint":0.0` e **não** contém `speed`; 2.º é exatamente `{"pumpComm":0}`; todos os atuadores voltam a `Manual` — **implementado** (o `PumpFlow == 0` no quadro seguinte é B3.11) |
| B4.5 | Campo desabilitado sem eco | quadro sem `FlowKp` → `CanEditTuning == false` e "Aguardando telemetria do nó"; com eco → habilita; eco some → desabilita no quadro seguinte (não-sticky) — **implementado** (o análogo da distância já existia em `ExternalDeviceTests`) |

---

## 6. Suíte B5 — Soak e regressão de enlace

| # | Teste | Execução | Aprovação | Evidência |
|---|---|---|---|---|
| B5.1 | Linha de base | `opentec-harness wifi-test <ip>` (já existe) | passos 1–7 verdes; relatório copiado | `wifi-test.txt` |
| B5.2 | Soak 30 min USB | `opentec-harness usb COM3 --for 1800` | 0 falhas de parse consecutivas ≥ 3; 0 reconexões; período mediano 2,0 s | `soak-usb.csv` |
| B5.3 | Soak 30 min Wi-Fi com varredura | `wifi --for 1800` + `GET /nodeDiag` a cada 10 s no mesmo processo | RTT p95 < 250 ms; 0 timeouts em `/readData`; `hub_fail_streak` máx ≤ 2 (B2.10) | `soak-wifi.csv` |
| B5.4 | Reconexão USB com estado | derrubar o cabo 10 s (ou `reset-test` sem pulso) | app/harness reconecta em ≤ 15 s; `Time` do Hub **não** volta a zero (DTR suprimido) | `reconnect.log` |
| B5.5 | Comando longo por USB | curva do fluxômetro (~300 B) | ack em ≤ 1 s, `[HubCmd] Applied`, `Params Saved.` (recibo de 11/09) | `flow-curve.log` |
| B5.6 | Rajada de comandos | 10 comandos de setpoint em 2 s por USB | 10 `OK`, 0 falsa perda de enlace, último setpoint vence | `burst.log` |

---

## 7. Como rodar: `opentec-harness bench-test`

Implementado em `Windows_app/src/OpenTECHub.Harness/BenchTestSuite.cs`, no molde do `WiFiTestSuite`.

```bash
dotnet run --project Windows_app/src/OpenTECHub.Harness -c Release -- bench-test COM3 --reset-hub --pump --soak-min 30 --out Windows_app/docs/evidence/bench/2026-09-13
```

```bash
dotnet run --project Windows_app/src/OpenTECHub.Harness -c Release -- bench-test 192.168.4.1 --pump --soak-min 30 --out Windows_app/docs/evidence/bench/2026-09-13-wifi
```

| Opção | Efeito |
|---|---|
| `COMx` ou `ip` | USB ou Wi-Fi. Cada suíte pula o que não vale no meio (B1.4/B1.5/B2.2–2.5 só em Wi-Fi; B1.7/B2.1/B2.6–2.8 só em USB) — rodar **os dois** para o recibo completo |
| `--expect-hub 10.2.0-dev` | P2; a versão alvo do Hub |
| `--suites B1,B2,B3,B5` | quais rodar (padrão: todas) |
| `--soak-min N` | B5.2/B5.3 (e B2.10 em Wi-Fi); 0 = pular |
| `--reset-hub` | B2.1: reconecta com o pulso DTR para ler `NodeDiag task criada; heap antes=… depois=…` no boot. Reinicia o Hub — só com o processo em estado seguro |
| `--pump` | B3.8 (`reset_volume`) e B3.11 (`{"mode":0}` → `{"pumpComm":0}` e restaura `pumpComm:1`). Exige `pumpComm` ligado antes; sem a flag, SKIP |
| `--out <dir>` | `report.md` + CSVs/JSON (padrão `docs/evidence/bench/<data_hora>/`) |

Comportamento:

1. Pré-condições (§1) primeiro; se P2/P4/P5/P7 reprovarem, **nada mais roda** e o relatório diz qual nó
   está na versão errada e o `Publish-OtaFirmware.ps1` que resolve.
2. Usa **os mesmos** `CommandBuilders`/`TelemetryParser`/`HubNodeDirectoryClient`/`HubNodeDiagClient`
   do app — prova o contrato que o app usa, não uma reimplementação.
3. **Restaura** todo valor alterado (offset e períodos da distância, cinco ganhos do fluxômetro,
   calibração da bomba, `pumpComm`, gear/PWM/EMA/período da biomassa) num `finally`, na ordem inversa,
   e registra `FAIL` em "restaurar X" se algum não voltar.
4. Nunca liga motor nem bomba; a vazão fica em 0 o tempo todo (P7 recusa começar com setpoint > 0) —
   os testes de gás com válvulas são do plano A/B/C, não deste documento.
5. Código de saída ≠ 0 se houver qualquer `FAIL` (incluindo restauração).

**Ensaio a seco** antes da bancada (valida o próprio harness):

```bash
dotnet run --project Windows_app/src/OpenTECHub.Simulator -c Debug -- http --port 8080 --quiet --all-nodes
```

```bash
dotnet run --project Windows_app/src/OpenTECHub.Harness -c Debug -- bench-test 127.0.0.1:8080 --pump --out /tmp/bench-sim
```

O que fica **fora** da automação, e por quê: B1.6/B3.3 (reboot de um nó — precisa de relé ou OTA
deliberado), B2.9 (nó desligado — relé), B3.14 (derrubar a rota de um nó — muda a configuração do
Hub em NVS; fazer à mão e observar a chave sumir do quadro), B5.4 (puxar o cabo), B5.5 (a curva de
calibração do fluxômetro é do fluxo de Calibrações no app). O relatório lista cada um como SKIP com
esse motivo, para o recibo não parecer completo quando não é.

---

## 8. O que os números fecham

| Número medido | Fecha |
|---|---|
| B1.1 `Content-Length` máx | Q7 do `PROTOCOL.md`; decisão de cortar `FlowOutput`/`FlowSetpointCorrected` |
| B2.1 heap depois | Q8; critério > 150 KB da Etapa 8; decisão 512 → 256 B de cache |
| B2.10/B5.3 `hub_fail_streak` com varredura | se a tarefa `NodeDiag` custa push (subir para 60 s se sim) |
| B3.1 latência do offset | §8 do plano de config: carona vs. rota de poll (limiar 30 s) |
| B3.5 + observação manual do §I.4 | se o controlador de vazão passa a regular para baixo com os ganhos expostos |
| B5.1–B5.6 | recibos de 11/09 e 12/09 no `ROADMAP.md › Field readiness` |

Uma execução completa (sem os opcionais de relé) leva ≈ 75 min, dos quais 60 são soak. O relatório
vai para `docs/evidence/bench/<data>/report.md` e é citado no `PHASE_LOG` como recibo.
