# Propostas de melhoria da comunicação com o Hub

**Revisão:** 2026-09-13 (conferida contra o código: Hub 10.2, bomba 3.10, fluxômetro v11.0, biomassa v11, distância v11, agitador v10, app Windows).

## Status

Documento de propostas. **Nada abaixo altera o protocolo atual por si.** O contrato vigente continua sendo o código em `ESP32S3-HUB/ESP32S3-HUB` e os firmwares ativos — rotas, query strings, nomes de campos e `cmd_id`/`ack_cmd_id` — descrito em `ESP32S3-HUB/docs/WIRE_CONTRACT_V9.md` e, por dispositivo, em `../COMANDOS_DISPOSITIVOS_EXTERNOS.md`. Itens marcados **Feito** já estão no código citado; os demais continuam propostas.

## Matriz JSON atual

| Componente | Estratégia ativa | Observação (2026-09-13) |
|---|---|---|
| Hub 10.2 | `getValueFromJson()` manual (`Mailboxes.h`), com verificação de posição de chave (`isJsonKeyPosition`) | Sem ArduinoJson; construção por concatenação de `String` |
| Bomba 3.10 | parser manual | idem |
| Fluxômetro v11.0 | parser manual **transacional em 2 fases** sob `commandMutex`, `parseBoundedFloat()` rejeita `NaN`/`Inf` e faixas (F07/F08) | único nó que valida finitude |
| Agitador v10 | parser manual (desacoplado do ArduinoJson) | — |
| Biomassa v11 | parser manual (`findJsonValueStart`, exige `"chave":`) | ACKa payload sem chave reconhecida (por desenho) |
| Distância v11 | parser manual; faixas em todos os numéricos (D05) | **não** ACKa payload sem chave válida (D02) |

## Propostas compatíveis

1. **Publicar versão do firmware.** ~~Propor `protocol_version`/`firmware_version` na telemetria.~~ **Feito de outra forma (10.1+):** cada nó anuncia `ver=` e `mac=` em `GET /nodeHello`; o Hub publica `<Nó>NodeVer`, `<Nó>NodeMac` e `<Nó>IP` no quadro e o app confere a versão contra `NodeFirmwareCatalog` (chip de "versão não validada" em *Configurações › Rede*). Não existe `protocol_version` separado — a versão do firmware é o que o catálogo valida. Pendência: o catálogo precisa acompanhar cada regravação (a bomba 3.10 foi incluída em 2026-09-13).
2. **`boot_id` em todos os nós.** **Parcial:** fluxômetro v11 publica `boot_id` no push e o Hub repassa como `FlowmeterBootId` (sticky) e usa para reimpor o estado desejado após reboot do nó. A biomassa v11 tem `boot_id` no JSON local e em `/api/history`, **mas não no push ao Hub**. Distância, agitador e bomba não têm. Efeito prático documentado em `COMANDOS` §4.10 B06 (reinício silencioso da aquisição de biomassa) e §2.7 (distância nasce em IDLE, Hub não reimpõe). Condição para avançar: remedir o flash da biomassa antes de acrescentar o parâmetro (está a 5,3 kB do piso).
3. ~~Padronizar diagnósticos opcionais.~~ **Feito (10.2):** os cinco nós servem `GET /diag` (`uptime_s`, `free_heap`, `rssi`, `hub_fail_streak`, `ota`, métricas próprias); o Hub coleta numa tarefa própria (`NodeDiag`, 30 s) e expõe por `GET /nodeDiag` e pelo comando serial `nodeDiag`. Fora do quadro de telemetria, de propósito.
4. **Validação numérica no Hub.** **Parcial:** o Hub valida **faixa** do que roteia à distância (`offset_mm` [−50, 200], períodos [100, 60000], `reset_nvs` só 1), à bomba (whitelist de chaves; `pump_command` só `reset_volume`/`start`/`stop`) e à biomassa (um `command` por revisão). **Não** rejeita `NaN`/`Inf`/estouro em `toFloat()`/`toInt()` nem booleanos fora de `0/1`; hoje quem faz isso é o fluxômetro (`parseBoundedFloat`) e, no app, os `CommandBuilders` (que lançam em valor fora de faixa). Proposta mantida: um `parseBoundedFloat` no Hub, aplicado primeiro aos ganhos de PID da bomba e aos limiares da biomassa, que passam sem faixa.
5. **Testes golden compartilhados.** **Parcial:** `ESP32S3-HUB/tests/contracts` (83 testes, `pytest`) espelha em Python a tradução app→Hub→nó de cada dispositivo e fixa por `assertIn` os trechos críticos de `Commands.h`/`HttpServer.h`/`Telemetry.h`; no app, `OpenTECHub.Simulator` + `WireCodec` servem de fixture do fio (1 607 testes). Falta o que o item pedia originalmente: fixtures **compartilhadas** entre Hub e nós (hoje o nó só é coberto pela compilação) e casos de ordem de chaves/campos opcionais nos parsers dos nós.

### Feitos que não estavam na lista (2026-09-12/13)

- **Entrega confiável para os nós de poll** — `ReliableMailbox` (`distanceBox`, `biomassBox`, `pumpBox`, `agitatorBox`): `cmd_id` por revisão, retenção até `ack_cmd_id`, `<Nó>CommandPending` no quadro, semente aleatória por boot (`seedReliableMailboxes()`), para a primeira ordem após reboot do Hub não colidir com o último ACK do nó. Regra: **uma revisão por caixa** — nova ordem substitui a ainda não entregue; o app serializa atrás de `CommandPending`.
- **Presença separada de leitura** — `<Nó>Online` é "empurrou dentro da janela"; a chave de valor exige a leitura válida declarada pelo nó (distância `-1`, biomassa `idle=1`). O filtro de estagnação do Hub foi removido.
- **Janela de presença proporcional ao período ecoado** — distância: `distancePresenceWindowMs(send_ms) = max(3 s, 2,5 × send_ms)` (D03). É o modelo proposto para a biomassa (B01: `probe_ms` até 60 s contra janela fixa de 10 s) — ainda aberto.
- **Ecos de configuração no push** — distância (`offset`, `sample_ms`, `send_ms`), bomba (`slope`, `intercept`, `kp/ki/kd`, `pot`, `cyc_vol`), biomassa (`gear`, `ema`, `probe_ms`), fluxômetro (`Kp/Ki/ff/ramp`, `cal_crc`, `hw_status`, `boot_id`). O app só libera edição depois do eco.
- **Filtro de comandos destrutivos no Hub** — `clear_nvs`/`save_config`/`load_config`/`print_config` da bomba não passam; `reset_nvs` da distância só com valor 1.

## Propostas que exigem migração coordenada

Nenhuma iniciada. Continuam válidas, com uma observação nova em cada:

1. **Telemetria por `POST application/json`** em vez de query string. O `/pumpData` já está em ~300 caracteres de URL (buffer de 420) e o `/biomassData` cresce a cada eco; a query string é o que hoje limita acrescentar `boot_id`/`hd_mode` à biomassa. Exige período dual `GET`/`POST` e remoção só depois de regravar a frota.
2. **Camada JSON única** no lugar dos parsers manuais. O fluxômetro v11.0 já mostrou o formato-alvo (transacional, com faixas); replicar por dispositivo, com fixture de regressão antes da troca, sem misturar com controle físico. Custo de flash é a restrição real na biomassa.
3. **Envelope versionado** `{device, version, seq, boot_id, payload}`. Parte do valor já foi obtido por `/nodeHello` + `NodeFirmwareCatalog`; o que falta é `seq`/`boot_id` uniformes (item 2 acima).
4. **Autenticação e limite de replay.** Decisão registrada (fluxômetro F13, distância, biomassa): SoftAP e endpoints ficam abertos enquanto o Hub for um SoftAP isolado; reabrir junto com qualquer plano de LAN compartilhada. O `cmd_id` monotônico já impede reaplicação acidental, não replay malicioso.

## Pontos frágeis observados (revistos)

- Parsers `indexOf()`/`strstr()` não interpretam escapes e dependem de delimitadores simples. O Hub e a biomassa já exigem `"chave":` (não casam texto dentro de valor); bomba, agitador e distância ainda casam a substring.
- A tradução de nomes PC→Hub→nó continua (ex.: `biomassEma` → `ema`, `pumpPidKp` → `pid_kp`, `flowSetpoint` → `flow_setpoint`) e está documentada e testada em `test_node_commands.py`; o `PROTOCOL.md` do app foi corrigido em 2026-09-13 onde havia divergido (`set_ema`/`set_period`).
- `ack_cmd_id` confirma aplicação no software, não conclusão física (válvula, motor, branco óptico). Os documentos por dispositivo dizem o que cada ACK prova.
- **Política de ACK diverge entre nós:** biomassa confirma qualquer payload (para nunca prender a caixa do Hub); distância só confirma com chave válida (o Hub compensa não enfileirando o que o nó não reconhece). Ambas funcionam; a divergência precisa continuar documentada para quem escrever o próximo nó.
- HTTP 200 no push confirma recebimento, não plausibilidade; com roteamento desligado o Hub responde 403 e o nó conta como falha (o poll zera o contador).
- **Rotinas bloqueantes nos nós** (branco e busca de marcha da biomassa, 20–40 s) não servem o Hub: sem push nem poll, o nó parece ausente e um `stop` só chega ao final (B02).
- **Janelas fixas de presença** contra períodos configuráveis: resolvido para a distância (D03), aberto para a biomassa (B01, 25 s de período contra 10 s de janela).

## Sequência recomendada (atualizada)

1. ~~Fixtures e telemetria de versão~~ — versão feita (`/nodeHello` + catálogo); fixtures parciais (contratos do Hub + simulador do app).
2. **Agora:** fechar B01/B02 da biomassa no Hub (janela por `probe_ms`) e decidir o `boot_id` da frota junto com o `POST` JSON, já que a query string é o gargalo.
3. Validação numérica no Hub (`parseBoundedFloat`) e fixtures compartilhadas Hub↔nó.
4. Camada JSON única, um nó por vez, começando pelo que tem folga de flash (bomba, distância) e deixando a biomassa para depois de revisar partições.
5. Por último `POST` JSON com período dual e, se houver LAN, autenticação.

Cada etapa continua exigindo build, contract test, bancada e soak separados.
