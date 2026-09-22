# H08 — Integração de bancada do banho externo

**Status:** `SOFTWARE_CHECKED / PHYSICAL_PENDING_G1_G9`

**Hub:** `10.6.0-dev`

**Nó:** banho r3.2
**Regra de segurança:** troca break-before-make usando `100B`; sem fallback automático para a via original.

## Evidência de software já obtida

- O simulador de host em `External-Devices/banho-termostatico/tests/host-sim` executou `build.cmd` com `ALL PASSED (0 failure(s))`.
- A suíte de contratos do Hub passou com 111 testes.
- A compilação do firmware passou dentro do limite registrado no H07: 1.146.304 bytes (87%) de flash e 51.712 bytes (15%) de RAM global.

Essa evidência não substitui os gates elétricos, de rede e de processo G1–G9.

## Matriz de ensaio físico

| ID | Ensaio | Evidência bruta obrigatória | Critério de aprovação |
|---|---|---|---|
| P01 | Presença por 60 s | captura de `/nodeHello`, `/bathData`, `/readData` e RSSI | `BathOnline`, IP, versão e MAC permanecem estáveis |
| P02 | Comando, ACK e conclusão | `cmd_id`, ACK, estados `running/settling/done` e timestamps | novo alvo só após ACK da caixa e `done` |
| P03 | Perda de Wi‑Fi | log de reentrega e contagem de comandos | mesmo `cmd_id` é repetido sem movimento duplicado |
| P04 | Erro, abort e guarda suspensa | payload do nó e snapshot do Hub | estado `fault/paused`, integral congelada e nenhum novo comando |
| P05 | Troca nos dois sentidos | UART, `100B`, `tempControlMode`, setpoints antes/depois | break-before-make; nenhum comando no mesmo quadro |
| P06 | Reboot do Hub e do nó | logs antes/depois e `/readData` | não retoma atuação nem reaproveita setpoint antigo |
| P07 | Perda e retorno de PV | `Tempval`, validade, stale e estado da cascata | pausa sem degrau; retomada somente com PV válida |
| P08 | Exclusividade do atuador | traço UART `B`/`100B`, `TempModuleActuatorOn` e fila do banho | nunca há atuação original e externa simultânea |
| P09 | Modo/parada durante hold | `bathMode`, `bathAbort` durante `running`, IDs e `BathCommandCompletion` | modo não gera `completion_timeout`; parada entrega `stop`, nó em `manual`, via inalterada |
| P10 | Reset de falha | falha por `sp_mismatch`/timeout, `bathCascadeReset`, `/readData` antes/depois | cascata volta a `controlling` sem degrau; guarda rearmada |
| P11 | Posse do Hub | comando pelo `bath_app`/`/ui` com a cascata ativa | `409 hub_owned`, nenhum relé; `abort` local funciona |
| P12 | Reboot do Hub com nó ligado | tempo até `BathOnline` | nó refaz hello ao receber 403, sem esperar 30 s |
| P13 | Receita pela temperatura do reator | log da receita, `Tempval`, banda e permanência | bloco só avança após permanência na banda |

## Registro do ensaio

Preencher para cada execução: data/hora, operador, identificação do Hub e do nó, commit do
firmware, versão r3.2, configuração, captura serial completa, exportação de `/readData`, logs do
nó, resultado por P01–P13 e observações de falha. Uma falha deve permanecer registrada; não usar
fallback para mascará-la.

H08 só pode ser fechado quando G1–G9 estiverem aprovados com evidência reproduzível. Até lá, o
firmware permanece adequado para validação de software, mas não liberado para cultivo.
