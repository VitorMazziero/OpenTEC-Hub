# PLANO DE IMPLEMENTAÇÃO DO HUB: BANHO EXTERNO E CASCATA TÉRMICA

**Data:** 2026-09-21
**Status:** H00–H07 implementados (10.5.1-dev); correções K00–K05 em 10.6.0-dev (ver `IMPLEMENTATION_PLAN_BANHO_CORRECOES.md`); H08–H10 físicos pendentes
**Base consolidada:** `IMPLEMENTATION_PLAN_BANHO.md` + `IMPLEMENTATION_PLAN_CONTROLE_CASCATA_BANHO.md`
**Alvo previsto:** `ESP32S3-HUB` 10.4.0-dev → 10.5.0

## 1. Escopo e fronteiras

Este é o único roteiro executável para mudanças do Hub relacionadas ao banho. Ele substitui a
execução separada dos itens H1–H11 do plano geral antigo e das etapas E03–E08 do plano de
cascata.

Inclui:

- registro do nó `bath`, `/bathData`, diagnóstico e caixa confiável;
- segunda via térmica com troca break-before-make;
- transformação de `Tempval` em entrada interna válida e temporal;
- controlador PI externo, máquina de estados, limites e persistência;
- telemetria completa do nó e da cascata;
- testes de contrato, testes host, orçamento de JSON/flash/RAM e validação integrada.

Não inclui edição do Windows App, de `Android_app/` ou do app Android próprio do banho. O nó r3
já entrega o contrato necessário; qualquer ajuste posterior nele deve ser um commit separado.

## 2. Pré-condições e estado de partida

- Hub atual: `HUB_FIRMWARE_VERSION "10.4.0-dev"`.
- Hub ainda não possui `DEV_BATH`, `bathBox`, `/bathData`, `tempControlMode` ou `BathCascade*`.
- Nó do banho: r3 implementado, compilado e simulado; `hub_enabled=0` por padrão.
- Gates físicos G1–G9 do nó continuam pendentes. Integração pode avançar contra fixtures, mas
  a via externa não pode ser liberada para cultivo antes desses gates.
- `Tempval` precisa ser confirmado em bancada como temperatura real do reator. Se representar
  outro ponto, a realimentação deste plano deve ser revista antes de H04.
- Confirmar que `100B` realmente desabilita a atuação térmica original antes de aceitar a troca
  de via como segura.

## 3. Semântica final — elimina a contradição dos planos anteriores

O plano-base antigo roteava `tempReference` diretamente para `{"setpoint":...}`. Isso **não
deve ser implementado**. Na via externa:

```text
tempSetpoint/tempReference = referência do reator
Tempval                    = PV do reator
BathCommandSetpoint        = saída calculada pelo PI
payload do bathBox         = {"setpoint":BathCommandSetpoint}
```

Na via original, `tempSetpoint` continua gerando o comando UART `B` existente.

Regras invariantes:

1. nunca atuar simultaneamente pela placa original e pelo banho;
2. ao trocar a via, ignorar `tempSetpoint` no mesmo quadro;
3. exigir novo setpoint do reator após troca para a via externa ou reboot;
4. `tempSetpoint=0` desativa/zera a cascata, mas não desliga o C404;
5. não fazer fallback automático para a via original;
6. não persistir integral, PV filtrada, saída transitória ou estado operacional;
7. enviar um novo alvo somente depois do ACK da caixa e de `BathState=done`;
8. enquanto o atuador estiver ocupado, conservar apenas o alvo calculado mais recente.

## 4. Contrato do nó r3 consumido pelo Hub

### 4.1 Registro

```text
GET /nodeHello?dev=bath&ver=r3&mac=<MAC real>
```

### 4.2 Push `/bathData`

Campos obrigatórios:

| Campo | Validação/uso |
|---|---|
| `sp`, `known`, `target` | finitos; sombra, validade e alvo defendido |
| `state`, `phase`, `err` | strings curtas; estado observável da sequência |
| `pv`, `pv_ok` | `pv` só vira número válido quando `pv_ok=1` |
| `display_sp`, `display_sp_ok` | confirmação independente do alvo no C404 |
| `sp_source` | deve ser 1 para cascata integrada |
| `mode`, `guard` | cascata exige `mode=auto`; `suspended` gera fault |
| `dev`, `dev_ok` | desvio display−alvo; `null` quando inválido |
| `time` | uptime finito e não negativo |
| `ack_cmd_id` | confirma somente revisão aceita pelo nó |

Ausência de obrigatório, número não finito ou enum inválido retorna `400` sem mutar o estado.
O handler confirma `bathBox` pelo `ack_cmd_id` e responde com o payload ainda pendente; sem
payload, responde texto simples `Bath data received`.

### 4.3 Caixa confiável

- `ReliableMailbox bathBox`, com semente aleatória a cada boot;
- comando retido até ACK igual;
- ACK antigo não limpa revisão nova;
- payload recusado pelo nó permanece pendente porque o ACK não avança;
- o Hub deve distinguir ACK de conclusão: ACK não equivale a `done`.

## 5. Estado a adicionar no Hub

Em `src/core/AppContext.h`:

- `DEV_BATH` antes de `DEV_COUNT` e entrada `{ "bath", ... }` no registro;
- `ReliableMailbox bathBox`;
- estado do nó: SP, alvo, PV, display SP, flags de validade, modo, guarda, estado, fase, erro,
  último update e origem do SP;
- `bool bathCommOn`;
- `enum class TempControlRoute { UartModule=0, ExternalBath=1 }`;
- flag de troca de via e trava de transição;
- amostra `reactorTempPv`, validade e timestamp;
- configuração, estado e snapshot do controlador `ExternalBathCascade`;
- `bathCommandSetpoint`, valor confirmado, alvo `latest-wins`, tempos de cálculo/envio/conclusão.

Todos os snapshots compartilhados devem ser copiados sob mutex curto. Não montar `String` nem
executar o PI segurando `stateMutex`.

## 6. Controlador `ExternalBathCascade`

Criar componente puro em `src/control/ExternalBathCascade.{h,cpp}`, sem HTTP, JSON, NVS ou
GPIO.

### 6.1 Equações

```text
r       = tempReference
y       = reactorTempPv filtrada
e       = r - y
u_raw   = r + bias + Kp*e + I
I(n+1)  = I(n) + Ki*e*dt
u_cmd   = u_raw após limites absoluto/relativo, slew e quantização
```

PI inicialmente sem derivada. `Ti` pode ser exposto e convertido por `Ki=Kp/Ti`.

### 6.2 Estados

| Estado | Entrada | Ação |
|---|---|---|
| `off` | via original ou SP=0 | não calcula; integral zerada |
| `waiting_inputs` | falta PV, nó, SP conhecido ou config compatível | não envia |
| `initializing` | entradas tornam-se válidas | inicialização sem degrau |
| `controlling` | entradas válidas | calcula e agenda |
| `actuator_busy` | mailbox pendente ou nó running/settling | calcula diagnóstico; não envia |
| `paused` | falha temporária de PV/rede | congela integral |
| `fault` | erro persistente, abort, guarda suspenso ou saturação longa | bloqueia até reset/novo comando válido |

### 6.3 Proteções

- filtro passa-baixas com `dt` real;
- anti-windup condicional;
- limites absolutos do C404 e do processo;
- offsets relativos acima/abaixo de `r`;
- slew rate em °C/min;
- quantização e banda de 0,1 °C;
- período de cálculo separado do intervalo mínimo entre comandos;
- retomada sem degrau após pausa, falha, sintonia ou reconexão;
- temporizadores corretos após rollover de `millis()`.

### 6.4 Padrões provisórios

| Chave | Padrão inicial |
|---|---:|
| `bathCascadeKp` | 0,5 |
| `bathCascadeTiS` | 600 s |
| `bathCascadeBiasC` | +0,6 °C |
| `bathCascadePeriodMs` | 10000 ms |
| `bathCascadeFilterS` | 20 s |
| `bathCascadeCommandMinMs` | 30000 ms após `done` |
| `bathCascadeCommandBandC` | 0,1 °C |
| `bathCascadeSlewCMin` | 0,5 °C/min |
| `bathCascadeOffsetHighC/LowC` | 5,0 °C |
| `bathCascadeOutputMinC/MaxC` | limites aprovados do C404/processo |

São valores de partida para ensaio com água, não sintonia de produção.

## 7. Comandos aceitos pelo Hub

| Chave | Regra |
|---|---|
| `bathComm` | bool; persistido; padrão falso |
| `tempControlMode` | 0/1; persistido; aciona transição segura |
| `tempSetpoint` | referência do reator; nunca setpoint direto do C404 na via 1 |
| `bathMode` | manual/auto; operação do nó |
| `bathSync` | float finito; somente operação assistida |
| `bathAbort` | somente valor 1 |
| `bathCascadeKp`, `bathCascadeTiS`, `bathCascadeBiasC` | sintonia validada |
| `bathCascadePeriodMs`, `bathCascadeFilterS` | temporização validada |
| `bathCascadeCommandMinMs`, `bathCascadeCommandBandC` | proteção dos relés |
| `bathCascadeSlewCMin` | limite de velocidade |
| `bathCascadeOffsetHighC/LowC` | limites relativos não negativos |
| `bathCascadeOutputMinC/MaxC` | limites absolutos coerentes (`min<max`) |
| `bathCascadeReset` | limpa fault e reinicializa sem degrau |

Não aceitar alteração de sintonia no mesmo quadro de troca de via ou setpoint. Validar finitude
e o conjunto inteiro antes de mutar qualquer campo; erro não produz atualização parcial.

## 8. Aquisição e roteamento

### 8.1 `Tempval`

Após resposta válida de `sendSensorCommand("b", true)`:

- publicar valor, validade e timestamp;
- rejeitar vazio, não finito ou fora da faixa;
- nunca entregar `-1` ao PI;
- considerar stale após `max(3×dataDelay, 5000 ms)`, com teto explícito;
- manter aquisição mesmo sem controle ativo;
- não acoplar período do PI a `dataDelay`.

### 8.2 Troca para o banho externo

1. receber `tempControlMode=1`;
2. descartar `tempSetpoint` do mesmo quadro;
3. enviar `100B` à placa original e confirmar `tempOn=false`;
4. limpar atuação pendente anterior e entrar em `waiting_inputs`;
5. exigir nó online, `bathComm`, `sp_source=display`, modo auto, display conhecido e PV válida;
6. aguardar novo `tempSetpoint` antes de inicializar o PI.

### 8.3 Retorno à via original

1. desabilitar e limpar a cascata;
2. não enviar comando novo ao C404;
3. descartar setpoint do mesmo quadro;
4. aguardar novo `tempSetpoint` antes de reativar a UART.

## 9. Telemetria `/readData`

Sempre publicar presença e roteamento, mesmo sem nó:

```json
"BathOnline":false,"BathCommEnabled":false,
"BathCommandPending":false,"BathCommandId":0,"BathCommandAck":0,
"TempControlViaBath":false,"BathCascadeEnabled":false,
"BathCascadeState":"off"
```

Quando válidos, publicar separadamente:

```json
"TempSetpoint":30.0,"BathSp":31.1,"BathTarget":31.2,"BathPv":30.8,
"BathState":"done","BathPhase":"","BathError":"","BathMode":1,
"BathGuard":"watch","BathDeviation":-0.1,"BathSpSource":1,
"BathCascadeError":0.18,"BathCascadePvFiltered":29.82,
"BathCommandSetpoint":31.2,"BathCommandConfirmed":31.1,
"BathCascadeP":0.09,"BathCascadeI":1.11,
"BathCascadeSaturated":false,"BathCascadePausedReason":"",
"BathCascadeLastUpdateMs":123456
```

`BathPv`, `BathSp`, `BathDeviation`, termos PI e PV filtrada devem ser `null` quando inválidos.
Adicionar também `BathIP`, `BathNodeVer`, `BathNodeMac` e suporte a `/nodeDiag?dev=bath`.

## 10. Arquivos previstos

| Área | Arquivos principais |
|---|---|
| estado/versão | `Config.h`, `src/core/AppContext.h` |
| mailbox/comandos | `src/protocol/Mailboxes.h`, `src/protocol/Commands.h` |
| HTTP/nós | `src/network/HttpServer.h`, `src/network/NodeDiagTask.h` |
| aquisição/telemetria | `src/sensor/Telemetry.h`, `src/sensor/SensorUart.h` |
| runtime/rota | `src/core/Runtime.h` |
| PI | novos `src/control/ExternalBathCascade.h/.cpp` |
| persistência | `src/storage/Settings.h` |
| contratos | `tests/contracts/test_bath_*.py`, `test_node_registry.py`, `test_http_frames.py`, `test_json_keys.py`, `test_temp_route.py` |
| host | novo teste puro/orquestração do controlador |
| documentação | `docs/WIRE_CONTRACT_V9.md`, `ARCHITECTURE.md`, `VALIDATION.md`, changelog |

## 11. Etapas detalhadas de execução

Cada etapa termina em commit próprio, compilável e testado. Não editar aplicativos nessas
etapas.

### H00 — Congelar baseline e pré-condições

**Ações:** executar contratos atuais, compilar 10.4, registrar flash/RAM e maior `/readData`;
confirmar ausência de mudanças locais; registrar pendências físicas `Tempval` e `100B`.

**Conclui quando:** baseline reproduzível e números registrados.
**Commit:** somente documentação/evidência, se necessário.

### H01 — Integrar identidade, `/bathData` e `bathBox`

**Ações:** adicionar `DEV_BATH`; criar/semear mailbox; implementar validação atômica do push,
ACK e resposta por carona; registrar em hello/nodes/diag; ampliar buffers com medição; não
rotear temperatura ainda.

**Testes:** ACK correto/antigo, reboot, payload incompleto/não finito, presença/timeout,
diagnóstico e tamanho máximo.
**Commit:** `feat(hub): integrar no do banho com caixa confiavel`

### H02 — Tornar `Tempval` uma amostra interna válida

**Ações:** valor/validade/timestamp, faixa, stale timeout, snapshot curto e rollover; preservar
telemetria já existente.

**Testes:** válida, inválida, stale, mudança de `dataDelay` e rollover.
**Commit:** `refactor(hub): disponibilizar temperatura do reator com validade temporal`

### H03 — Implementar PI puro e máquina de estados

**Ações:** componente isolado, filtro, PI, anti-windup, inicialização sem degrau, limites,
slew, quantização, banda, temporizadores e snapshot diagnóstico.

**Testes:** erros ±/zero, remoção de offset, saturação/retorno, pausa/retomada, NaN/inf,
rollover e defaults.
**Commit:** `feat(hub): adicionar controlador PI da cascata termica externa`

### H04 — Implementar a troca de via e orquestração

**Ações:** `TempControlRoute`; break-before-make; descarte no mesmo quadro; novo SP obrigatório;
`latest-wins`; enviar somente `BathCommandSetpoint`; esperar ACK+done; pausa/fault; SP=0;
retorno seguro à UART.

**Testes:** nunca duas vias, SP do usuário diferente da saída, ACK sem done, perda/retorno do
nó e da PV, recusa e abort.
**Commit:** `feat(hub): rotear temperatura externa pela cascata do banho`

### H05 — Validar e persistir configuração

**Ações:** implementar todas as chaves `bathCascade*`; validação transacional; defaults;
persistir somente configuração e via; política segura de reboot; hash/debounce da NVS.

**Testes:** faixas, coerência min/max, mutação parcial, restauração sem atuação e ausência de
escrita contínua.
**Commit:** `feat(hub): persistir configuracao segura da cascata do banho`

### H06 — Publicar telemetria e diagnóstico finais

**Ações:** todos os campos da seção 9; `null` correto; identidade/diag; snapshot consistente;
medir e ajustar reserva do JSON; versão 10.5 somente nesta etapa.

**Testes:** nó presente/ausente, strings máximas, nulos, estado/termos, orçamento do quadro e
compatibilidade com consumidores antigos.
**Commit:** `feat(hub): expor diagnosticos da cascata termica do banho`

### H07 — Fechar contratos, documentação e orçamento

**Ações:** atualizar wire contract, arquitetura, validação e changelog; executar toda a suíte,
compilar firmware, registrar flash/RAM e confirmar que buffers não truncam.

**Conclui quando:** contratos verdes, compilação dentro do limite e documentação coincide com
o fio.
**Commit:** `docs(hub): fechar contrato do banho externo e cascata termica`

### H08 — Integração com nó r3 em bancada

**Dependência:** gates G1–G9 do nó.

**Ensaios:** presença durante hold de 60 s; comando/ACK/done; reentrega por perda de Wi-Fi;
erro/abort/guarda suspenso; troca de via; reboot; perda de PV; nenhuma atuação simultânea.

**Conclui quando:** evidência bruta e critérios do plano geral aprovados; falha não é mascarada
por fallback.
**Commit:** `test(bath-hub): registrar integracao do no r3 e troca de via`

### H09 — Identificação e sintonia com água

**Ações:** degraus positivos/negativos; registrar volume, vazão, agitação e ambiente; estimar
ganho, atraso e constante de tempo; começar `Ki=0`, depois integral lenta; medir sobressinal,
acomodação e comandos/hora; repetir perda de rede/PV e saturação.

**Conclui quando:** parâmetros de produção aprovados pelo responsável do processo.
**Commit:** `test(bath-cascade): registrar identificacao e sintonia de bancada`

### H10 — Liberação operacional

**Ações:** manual, contingências, versões mínimas, aviso de que C404 não desliga remotamente e
correspondência binário↔commit.

**Conclui quando:** checklist formal aprovado; somente então marcar pronto para cultivo.
**Commit:** `docs(bath-cascade): consolidar operacao e prontidao para cultivo`

## 12. Comandos de verificação previstos

```powershell
python -m unittest discover ESP32S3-HUB/tests/contracts
powershell -ExecutionPolicy Bypass -File ESP32S3-HUB/tools/compile.ps1
git diff --check
```

Adicionar ao script de contrato global somente depois que os novos testes forem estáveis.

## 13. Critérios de aceite integrados

| Ensaio | Critério |
|---|---|
| offset constante | erro estacionário do reator < 0,2 °C após sintonia aprovada |
| perturbação térmica | retorno à tolerância sem oscilação sustentada |
| app desconectado | controle permanece no Hub |
| nó/PV perdidos | pausa, integral congelada e retomada sem degrau |
| limites | C404 nunca excede absolutos, relativos ou slew |
| relés | intervalo mínimo respeitado; acionamentos/hora medidos |
| troca de via | nenhuma atuação simultânea |
| reboot | nenhuma retomada ou salto inesperado |
| mudança manual no C404 | guarda restaura alvo; cascata continua referenciada ao reator |

## 14. Riscos que permanecem

- O C404 não possui desligamento remoto nesta integração por teclas.
- `BathPv` e `Tempval` medem pontos distintos e nunca podem substituir um ao outro.
- Dinâmica muda com volume, circulação, agitação, isolamento e metabolismo.
- Resolução do C404 e vida mecânica dos relés limitam a frequência de correção.
- Capacidade insuficiente de aquecer/resfriar deve aparecer como saturação, não windup.
- Testes e compilação não provam segurança térmica nem desempenho no processo real.
