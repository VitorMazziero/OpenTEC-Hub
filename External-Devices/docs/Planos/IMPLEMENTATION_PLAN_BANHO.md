# PLANO GERAL: BANHO EXTERNO (C404) NO ECOSSISTEMA OPENTEC-HUB

**Data de consolidação:** 2026-09-21
**Função deste arquivo:** visão geral, decisões de arquitetura, fronteiras e critérios globais
**Não usar como roteiro de edição:** as etapas executáveis estão nos planos individuais abaixo

## 1. Mapa documental e estado real

| Documento | Responsabilidade | Estado em 2026-09-21 |
|---|---|---|
| `IMPLEMENTATION_PLAN_BANHO.md` | ideia geral e contrato entre componentes | este documento |
| `IMPLEMENTATION_PLAN_BANHO_HUB.md` | todas as mudanças do firmware do Hub, incluindo integração do nó e cascata térmica | **implementado** (10.5.1; corrigido em 10.6.0-dev) — físico pendente |
| `IMPLEMENTATION_PLAN_BANHO_WINDOWS_APP.md` | protocolo, simulador, interface, alarmes, receitas e registros do Windows App | **implementado** (W01–W08; corrigido K06–K08) |
| `IMPLEMENTATION_PLAN_BANHO_APP_ANDROID.md` | aplicativo Android próprio do banho, direto ao nó | **implementado** em `banho-termostatico/apps/flutter` (1.2.0: posse do Hub) |
| `IMPLEMENTATION_PLAN_BANHO_CORRECOES.md` | auditoria de 2026-09-22 e correções K00–K11 em todos os contextos | **implementado em software** — bancada pendente |
| `IMPLEMENTATION_PLAN_CONTROLE_CASCATA_BANHO.md` | fonte técnica original da cascata | incorporado ao plano do Hub; não executar separadamente |

Há dois aplicativos Android diferentes no repositório:

- `External-Devices/banho-termostatico/apps/flutter`: aplicativo **próprio do banho**, direto
  ao ESP32-S3. Está implementado e é a ferramenta móvel de bancada.
- `Android_app/`: aplicativo geral do Hub. A integração do banho nesse aplicativo não foi
  implementada por esta entrega e não é confundida com o aplicativo próprio acima.

## 2. Objetivo do projeto

Integrar um banho externo com controlador Contemp C404 como segunda via térmica do módulo,
sem criar uma segunda referência de processo e sem deslocar a malha crítica para um aplicativo.

O operador informa a temperatura desejada **no reator**. Quando a via original está ativa, o
Hub envia essa referência à placa TECNAL pela UART. Quando a via externa está ativa, o Hub usa
`Tempval`, medido no reator, para calcular lentamente o setpoint necessário no C404.

```text
                  referência do processo
                       tempSetpoint
                            │
              ┌─────────────┴─────────────┐
              │ tempControlMode = 0       │ tempControlMode = 1
              ▼                           ▼
      placa TECNAL / UART          PI externo no Hub
       controle térmico B          realimentado por Tempval
                                          │
                                          ▼ bathCommandSetpoint
                                  nó bath r3 → relés → C404
                                          │
                                          ▼ circulação térmica
                                        reator
```

O C404 conserva sua própria malha interna sobre a água do banho. O PI do Hub é a malha
externa: ele não substitui o C404; calcula qual referência o C404 precisa receber para que a
temperatura do reator alcance o valor solicitado.

## 3. Estado dos componentes

### 3.1 Nó `banho-termostatico`

Firmware ativo: **r3**.

Implementado:

- controle do C404 por relés, setpoint absoluto/relativo, `home`, abort e teclas cruas;
- leitura do display, modo sombra/display, hold em malha fechada e guarda manual/automático;
- API local `/status`, `/config`, `/command`, `/display`, `/diag`, `/ui` e `/update`;
- `cmd_id` idempotente: uma revisão aceita não reaplica toques quando é reentregue;
- enlace do Hub em tarefa própria, sem bloqueio HTTP no laço que temporiza os relés;
- anúncio `dev=bath&ver=r3`, push em `/bathData` e contrato observável completo;
- `hub_enabled = 0` por padrão até a bancada integrada.

Evidência disponível: compilação embarcada e simulação host. Isso não fecha os gates físicos
G1–G9, que continuam pendentes em `banho-termostatico/docs/VALIDATION.md`.

### 3.2 Aplicativo Android próprio do banho

Implementado em `External-Devices/banho-termostatico/apps/flutter`, versão 1.1.0+2:

- Operação, Modos, Bancada e Configuração;
- `cmd_id` persistido e reentrega da mesma revisão;
- setpoint, deltas, sync, modo, abort, home, teclas/hold, display, diagnóstico e NVS;
- traço local de 10 minutos, host persistido e aviso de firmware fora de r2/r3.

Análise, testes automatizados e geração de APK constituem validação de software. O uso contra o
dispositivo real nos gates G1–G9 permanece pendente.

### 3.3 Hub e Windows App

Implementados em 2026-09-22 (Hub 10.5.1 e Windows W01–W08) e corrigidos no mesmo dia após
auditoria (Hub 10.6.0-dev, nó r3.2, Windows K06–K08, `Android_app` K09, `bath_app` 1.2.0 K10);
ver `IMPLEMENTATION_PLAN_BANHO_CORRECOES.md`. A `bathBox` deu lugar ao `BathCommandCoordinator`.
Os gates físicos (G1–G9, H08 P01–P13, H09, H10) continuam pendentes.

## 4. Decisões de arquitetura

1. **Uma referência de processo:** `tempSetpoint` é sempre a temperatura desejada no reator.
2. **Duas vias, nunca duas atuações simultâneas:** `tempControlMode=0` seleciona a placa
   original; `tempControlMode=1` seleciona o banho externo.
3. **Cascata no Hub:** aplicativos apenas comandam e supervisionam; desconectá-los não encerra
   o controle.
4. **Sinais não podem ser fundidos:**
   - `tempSetpoint`: referência do reator;
   - `Tempval`: PV do reator e realimentação da cascata;
   - `BathCommandSetpoint`: saída calculada pelo Hub para o C404;
   - `BathSp`/`BathTarget`: SP observado e alvo defendido pelo nó;
   - `BathPv`: temperatura da água indicada pelo C404.
5. **Troca break-before-make:** ao entrar na via externa, o Hub desliga primeiro a atuação
   térmica original (`100B`) e descarta qualquer setpoint no mesmo quadro. Um novo
   `tempSetpoint` é exigido.
6. **Sem fallback automático:** dois banhos são equipamentos físicos diferentes. Nó ausente
   gera pausa/alarme; não troca silenciosamente para a via original.
7. **Parada do banho (revisada em 2026-09-22, D-2/D-4):** `bathAbort`, `tempSetpoint=0` na via
   externa e `resetVariables` desligam a cascata e entregam `stop` ao nó (abort + guarda em
   manual); o banho permanece no último SP, a via e `bathComm` não mudam. A interface deve
   dizer “cascata desligada, C404 em manual no último SP”, nunca “desligado”.
7a. **Posse do Hub (D-1):** com a cascata ativa o Hub é dono do nó (`X-Hub-Owner`); a API local
   aceita só abort/stop. A guarda em automático continua revertendo o painel.
7b. **Receitas (D-3):** na via externa um bloco de temperatura só avança quando o reator atinge
   o valor pedido (banda e permanência).
8. **Modo automático do nó não é a cascata:** o guarda do painel restaura o último alvo do
   C404; o PI do Hub muda esse alvo a partir do erro do reator.
9. **Configuração de bancada fica local:** tempos de tecla, display, sensoriamento e parâmetros
   do hold não são expostos como comandos normais do Hub.
10. **Integração exige `sp_source=display` e guarda `auto`:** sem confirmação visual do C404,
    a cascata não possui atuador observável suficiente.

## 5. Contrato final entre nó e Hub

### 5.1 Nó → Hub

`GET /bathData` a cada `send_period`, inclusive durante `running/settling`:

| Campo | Significado |
|---|---|
| `sp`, `known`, `target` | sombra, validade e último alvo do C404 |
| `state`, `phase`, `err` | estado, fase e erro literal da sequência |
| `pv`, `pv_ok` | PV mostrada pelo C404 |
| `display_sp`, `display_sp_ok` | SP lido diretamente no display |
| `sp_source` | 0 sombra; 1 display |
| `mode`, `guard`, `dev`, `dev_ok` | modo/guarda e desvio interno do nó |
| `time` | uptime do nó em segundos |
| `ack_cmd_id` | última revisão aceita e aplicada pelo parser |

O Hub responde com o payload pendente da caixa confiável ou texto sem comando. A resposta JSON
é transferida para o loop principal do nó; a tarefa de rede nunca aciona relé diretamente.

### 5.2 Hub → nó

Somente operação:

- `{"cmd_id":N,"setpoint":<bathCommandSetpoint>}`;
- `{"cmd_id":N,"mode":"auto|manual"}`;
- `{"cmd_id":N,"sync_sp":<valor>}`;
- `{"cmd_id":N,"abort":1}`.

Comandos recusados por `busy`, `range`, display inválido ou outra falha não avançam
`ack_cmd_id`. Reentrega do mesmo `cmd_id` responde `duplicate` sem novos toques.

## 6. Contrato final exposto pelo Hub

### 6.1 Comandos do aplicativo

- `tempControlMode`: 0 original/UART; 1 banho externo;
- `tempSetpoint`: referência do reator;
- `bathComm`: habilitação do enlace operacional;
- `bathMode`, `bathSync`, `bathAbort`;
- parâmetros `bathCascade*` definidos no plano do Hub;
- `bathCascadeReset` para limpar falha e reinicializar sem degrau.

### 6.2 Telemetria mínima

Presença/transporte: `BathOnline`, `BathCommEnabled`, `BathCommandPending`,
`BathCommandId`, `BathCommandAck`, `BathIP`, `BathNodeVer`, `BathNodeMac`.

Estado do nó: `BathSp`, `BathKnown`, `BathTarget`, `BathState`, `BathPhase`, `BathError`,
`BathPv`, `BathMode`, `BathGuard`, `BathDeviation`, `BathSpSource`.

Roteamento/cascata: `TempControlViaBath`, `TempSetpoint`, `BathCascadeState`,
`BathCascadeEnabled`, `BathCascadeError`, `BathCascadePvFiltered`, `BathCommandSetpoint`,
`BathCommandConfirmed`, `BathCascadeP`, `BathCascadeI`, `BathCascadeSaturated`,
`BathCascadePausedReason`, `BathCascadeLastUpdateMs`.

Valores sem validade são `null`; sentinelas como `-1` não podem virar medições plausíveis.

## 7. Controlador externo

Controlador inicial: PI sem derivada.

```text
r       = tempSetpoint
y       = Tempval filtrada
e       = r - y
u_raw   = r + bias + Kp*e + I
I       = I + Ki*e*dt
u_cmd   = u_raw após limites, slew rate e quantização de 0,1 °C
```

Padrões provisórios de bancada, não de produção: período 10 s, filtro 20 s, comando mínimo a
cada 30 s, banda 0,1 °C, slew 0,5 °C/min, `Kp=0,5`, `Ti=600 s`, `bias=+0,6 °C` e offsets
relativos ±5 °C. Todos dependem de identificação com água.

Estados obrigatórios: `off`, `waiting_inputs`, `initializing`, `controlling`,
`actuator_busy`, `paused` e `fault`. Sensor inválido, nó offline, sequência em erro ou comando
travado congelam a integral e impedem novos alvos.

## 8. Limites de segurança

- Anti-windup e reinicialização sem degrau.
- Limites absolutos do C404 e limites de processo mais restritivos.
- Offsets máximos acima/abaixo da referência do reator.
- Slew rate, quantização, banda mínima e intervalo entre comandos.
- Um único alvo `latest-wins`; nunca fila histórica de setpoints obsoletos.
- ACK confirma aceitação; somente `BathState=done` e display confirmado concluem a atuação.
- Integral, PV filtrada e estado operacional não são persistidos.
- Reboot exige entradas válidas e novo `tempSetpoint` antes de voltar a atuar.

## 9. Critérios globais de conclusão

O projeto completo só pode ser declarado pronto quando:

1. firmware r3 e app Android próprio passam validações de software e gates físicos G1–G9;
2. plano do Hub foi implementado, compilado e validado em contratos/host;
3. plano do Windows App foi implementado, testado e lançado com workspace explícito;
4. a troca de via nunca permite atuação simultânea;
5. perda do nó ou de `Tempval` pausa sem windup nem comando espúrio no retorno;
6. offset, perturbação térmica, limites e frequência dos relés passam em bancada com água;
7. binários publicados correspondem aos commits validados;
8. manuais deixam explícito que parada lógica não desliga fisicamente o C404.

Compilação, simulador e testes automatizados não substituem a validação com relés, display,
circulação, sensores e proteções térmicas reais.
