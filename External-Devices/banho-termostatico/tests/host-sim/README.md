# Simulação no PC — lógica de setpoint do `thermostatic-bath`

Compila os fontes **reais** do firmware (`setpoint/SetpointManager.cpp`,
`setpoint/SetpointGuard.cpp`, `keypad/KeyPresser.cpp`, `keypad/KeySense.cpp`,
`protocol/ConfigCodec.cpp`, `core/AppContext.cpp`) contra um Contemp C404 simulado e percorre os cenários que a bancada
ainda não pôde rodar. Não substitui `docs/VALIDATION.md`: prova a lógica de sequência, não
relés, temporização real, display ou NVS.

```text
build.cmd        compila com o MSVC (localizado por vswhere) e roda os cenários
build.cmd -v     idem, com o log serial do firmware ([SP] …)
```

`stubs/` contém o mínimo de `Arduino.h`/`WebServer.h` para o firmware compilar no PC. O
próprio `sim.cpp` substitui `DisplayReader` e `NvsConfig` por modelos e alimenta as linhas de sensoriamento:

| Modelo | O que reproduz |
|---|---|
| C404 | Incremento no toque após 20 ms; auto-repetição após um atraso (0,5 s, medido em 2026-09-26) a uma taxa (10 toques/s, medida); aceleração opcional (período menor ou passo de 1,0 °C); cauda opcional após soltar; toques discretos perdidos; janela com o display ilegível; limites `in.L`/`in.H`; dedos do operador nas teclas (com as duas setas, `▲` vence — hipótese até G7b) |
| Sensoriamento | Linhas das teclas no nível ativo de `BoardConfig::SenseActiveHigh` (HIGH no C404 real) quando o relé ou o operador fecha a tecla; `KeySense.cpp` real faz o resto |
| Display | Leitura ao vivo com 20 ms de atraso; leitura estável que só acompanha o valor 350 ms depois de ele parar e, até lá, mantém o valor estável anterior (como o `DisplayReader` real) |

Cada cenário verifica o estado final, o SP do C404, a excursão (nunca além do alvo nos
casos com hold), que nunca há dois relés fechados, e o tempo total. Os cenários P2 e I2 são
propositalmente mal configurados (`press_ms` acima do atraso de repetição; C404 que ignora
todo toque) para provar que o firmware para com `sp_mismatch` em vez de insistir.

Os parâmetros do C404 simulado são hipóteses até o gate G3b; quando a bancada medir o atraso,
a taxa e a cauda reais, ajuste `C404` em `sim.cpp` e os padrões de `hold_*` em `BathConfig`.

Os cenários U/V exercitam o contrato confiável: três entregas do mesmo `cmd_id` produzem uma
única sequência, e uma ação recusada não avança `g_lastCmdId`. A tarefa `HubLink` é
exercitada em `hub_sim.cpp`, com HTTP, tempo e FreeRTOS simulados: registro antes do
push, recuperação de 403, retries limitados e pausa durante OTA.
`network_sim.cpp` compila o `NetworkManager.cpp` real e verifica timeout/alternância A/B,
reconexão, watchdog sem desligar o rádio, OTA, desabilitação/reabilitação e rollover.
Ambos rodam em `build.cmd`; concorrência real, rádio, DHCP e stack watermark exigem bancada.
