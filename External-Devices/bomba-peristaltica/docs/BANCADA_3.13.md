# Bancada — bomba 3.12.1 / 3.13 · Hub 10.9.0 · app 0.32.0

Registro dos portões G0–G3 do plano
[`2026-10-11-plano-cartao-bomba-dosagem-pulsos.md`](../../../Windows_app/docs/plans/2026-10-11-plano-cartao-bomba-dosagem-pulsos.md)
(§8.4 e roteiro §10). Preencha as colunas **Resultado** e **OK** à mão e guarde as evidências
(relatórios do harness, fotos da proveta, CSVs) em `tests/evidence/bancada-3.13/`.

Material: proveta graduada, água, a mangueira do cultivo, cronômetro, PC com o app 0.32 e o
harness (`Windows_app/src/OpenTECHub.Harness`).

---

## 0. Gravação (nesta ordem; cada passo é compatível com o anterior)

| # | Equipamento | Binário | Tamanho | SHA-256 | Gravado em | OK |
|---|---|---|---:|---|---|:---:|
| 1 | Bomba **3.12.1** | `tests/evidence/fw-3.12.1/peristaltic-pump.ino.bin` | 1 106 640 B | `0c78816e3837c7b0bffd93907f632c22479cca8fb7c1adfe32b40cd28f6bce02` | | ☐ |
| 2 | Hub **10.9.0** | `ESP32S3-HUB/build/ota/ESP32S3-HUB.ino.bin` | 1 202 176 B | `910a95b9740bdb1c769fd7d5bf6bb69e489b9f7e1d08a898d0b461de386baa37` | | ☐ |
| 3 | Bomba **3.13** | `tests/evidence/fw-3.13/peristaltic-pump.ino.bin` | 1 111 360 B | `c13340d95ce71de562799525bea10bd4eb38fa93a4610513f94aabefc3713a2e` | | ☐ |
| 4 | App **0.32.0** | instalador gerado por `Windows_app` (ver `DOWNLOADS.md`) | | | | ☐ |

Conferir o SHA antes de gravar: `Get-FileHash <arquivo> -Algorithm SHA256`.
A bomba se grava pela página OTA do AP `FeedPump` ou por USB; o Hub pelo procedimento de `DOWNLOADS.md`.
Depois de cada gravação: o banner/`/diag` mostra a versão (`build` 3.12.1 ou 3.13; Hub `10.9.0-dev`).

**G0 roda depois do passo 1** (Hub 10.8.1 ainda) e **G1–G3 depois do passo 4**.

---

## G0 — bomba 3.12.1, Hub 10.8.1, app 0.31.1

| # | Ensaio | Critério | Resultado | OK |
|---|---|---|---|:---:|
| G0.1 | 10 comandos seguidos pelo app (perfil, PID, zerar, potenciômetros, perfil) | cada um confirmado; `PumpCommandPending` apaga em ≤ 3 s | | ☐ |
| G0.2 | Harness `bench-test <COMx> --suites B3 --pump` | B3.12 PASS (5/5, ≤ 3000 ms) | | ☐ |
| G0.3 | Reinício da bomba em repouso | `PumpVol` igual antes e depois | | ☐ |
| G0.4 | Perfil rodando → *Controle manual* (`speed`) → reinício | o perfil **não** volta | | ☐ |

## G1 — bomba 3.13, Hub 10.9.0, app 0.32.0 (integração)

| # | Ensaio | Critério | Resultado | OK |
|---|---|---|---|:---:|
| G1.1 | Harness `bench-test <COMx> --expect-hub 10.9.0-dev --suites B1,B2,B3 --pump` | B3.12–B3.16 PASS; guardar `report.md` e `echo-latency.csv` | | ☐ |
| G1.2 | JSON do Hub | `PumpDrive`, `PumpBootCount`, `PumpUptimeS`, `PumpRejCmdId` presentes; `PumpDose*` depois da 1ª dosagem | | ☐ |
| G1.3 | Latência de comando (`echo-latency.csv`) | menor que a do G0 (entrega na resposta do push) | | ☐ |
| G1.4 | Reinício da bomba em repouso | `PumpVol` mantido; app mostra "A bomba externa reiniciou (…)" | | ☐ |
| G1.5 | Reinício do Hub com a bomba ligada, depois *Zerar volume* | aplicado na primeira entrega | | ☐ |
| G1.6 | Recusa provocada (harness B3.13, ou dosagem com período < pulso por USB) | motivo no cartão; nada muda no nó | | ☐ |
| G1.7 | Fechar o app com *Encher mangueira* ativo | motor para em ≤ 60 s | | ☐ |
| G1.8 | Sensor de líquido habilitado e seco durante a dosagem | faixa "Sensor de líquido seco" no cartão; `PumpDoseShortfall` cresce | | ☐ |
| G1.9 | Bomba 3.12.1 com app 0.32 (opcional) | segmento *Pulsos* desabilitado ("requer a bomba 3.13") | | ☐ |

## G2 — testes automatizados (já cumprido em software)

| Camada | Comando | Resultado em 2026-10-12 |
|---|---|---|
| App | `dotnet test Windows_app/OpenTECHub.slnx` | 2 639 aprovados |
| Bomba (PC) | `tests/host-sim/build.cmd` | 49 verificações |
| Bomba (contrato) | `python -m pytest External-Devices/bomba-peristaltica/tests` | verdes |
| Hub (contrato) | `python -m pytest ESP32S3-HUB/tests/contracts` | verdes |

## G3 — dosagem (proveta graduada, água, mangueira do cultivo)

| # | Ensaio | Critério | Resultado | OK |
|---|---|---|---|:---:|
| G3.1 | Ponto de dosagem: S = 120, 3 × 120 s (Calibrações › Ponto de dosagem), volume na proveta | CV ≤ 2 % | Q_d = ____ mL/min · CV ____ % | ☐ |
| G3.2 | Linearidade: `t_on` ∈ {0,2; 0,5; 1; 2; 4} s, 30 pulsos cada (Verificação em pulsos) | resíduo ≤ 3 % a partir do mínimo recomendado; anotar `t₀` | t₀ = ____ ms · mínimo rec. ____ s | ☐ |
| G3.3 | Compensação: 0,5 s e 1 s com `t₀` digitado na tabela do ponto e enviado ao nó | erro médio ≤ 3 % | | ☐ |
| G3.4 | Execução longa: 50 mL em 12 h pelo painel *Pulsos* | proveta × `PumpDoseDelivered` × `PumpVol` ≤ 3 % | proveta ____ mL · nó ____ mL | ☐ |
| G3.5 | Queda de energia da bomba no meio | retoma em ~5 s; perda ≤ 1 min de dosagem + 1 pulso | | ☐ |
| G3.6 | `reset_volume` durante a dosagem, depois reinício | `vol` não volta | | ☐ |
| G3.7 | Hub mudo por 2 min | pulsos com a duração certa (contar na proveta) | | ☐ |
| G3.8 | Interruptor da linha durante um pulso | motor para em < 1 s; dosagem cancelada ("Interrompida…") | | ☐ |
| G3.9 | Desvio no cartão durante G3.4 | sem alerta com tudo normal; alerta ao segurar o sensor seco > 2 períodos | | ☐ |

---

## Conclusão

| Portão | Data | Responsável | Resultado |
|---|---|---|---|
| G0 | | | |
| G1 | | | |
| G3 | | | |

Com G0–G3 aprovados: marcar as versões como validadas em `CHANGELOG.md` (bomba e Hub),
`Windows_app/docs/CHANGELOG.md` e na tabela de prontidão de
`External-Devices/docs/COMANDOS_DISPOSITIVOS_EXTERNOS.md`, e só então publicar o release (tag `v0.32.0`).
