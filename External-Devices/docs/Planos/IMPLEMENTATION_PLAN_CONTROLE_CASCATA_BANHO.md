# PLANO DE IMPLEMENTAÇÃO: CONTROLE EM CASCATA DO BANHO EXTERNO PELO SENSOR DO REATOR

**Data:** 2026-09-21
**Status:** **incorporado; não executar separadamente**
**Documento executor:** `IMPLEMENTATION_PLAN_BANHO_HUB.md`
**Documento geral:** `IMPLEMENTATION_PLAN_BANHO.md`

> Este arquivo preserva a análise técnica original da cascata. Suas etapas foram consolidadas
> e atualizadas no plano individual do Hub, junto com registro do nó, `/bathData`, caixa
> confiável, roteamento, telemetria e validação. O nó r3 e o app Android próprio já estão
> implementados; Hub e Windows App continuam sem implementação. Em caso de divergência, usar
> `IMPLEMENTATION_PLAN_BANHO_HUB.md` para o Hub e
> `IMPLEMENTATION_PLAN_BANHO_WINDOWS_APP.md` para o Windows App.

---

## 1. Problema

O valor informado pelo usuário é a temperatura desejada **no reator**. O sensor que produz
`Tempval` está no reator, mas o C404 regula a temperatura da água do banho externo. Portanto,
enviar diretamente `tempSetpoint = 30,0 °C` como `setpoint = 30,0 °C` ao C404 não fecha a
malha sobre a variável de processo correta.

Em regime, perdas para o ambiente podem fazer, por exemplo, o banho estabilizar em 30,0 °C e
o reator em 28,7 °C. Durante o cultivo, o calor metabólico pode reduzir, anular ou inverter
essa diferença. Um deslocamento fixo de +1,3 °C não resolve o problema para todas as fases do
processo.

O plano anterior integra o banho como segunda via de `tempSetpoint`, mas ainda trata o
setpoint do usuário como o setpoint direto do C404. Este documento altera essa semântica:

- `tempSetpoint`: referência do reator definida pelo usuário, preservada como única referência
  de temperatura do processo;
- `Tempval`: temperatura medida no reator e realimentação da nova malha externa;
- `bathCommandSetpoint`: saída calculada pela nova malha e referência enviada ao C404;
- `BathPv`: temperatura medida pelo C404 na água do banho, usada para supervisão, não como
  realimentação principal da malha externa.

## 2. Arquitetura proposta

Implementar controle em cascata com duas malhas:

```text
 tempSetpoint do usuário                   bathCommandSetpoint
 (temperatura desejada                     (temperatura ordenada
  no reator)                                ao C404)
          │                                         │
          ▼                                         ▼
 ┌───────────────────┐   erro do reator    ┌──────────────────┐
 │ Controlador PI    │────────────────────▶│ C404 / banho     │
 │ externo, no Hub   │                     │ malha interna    │
 └─────────▲─────────┘                     └────────┬─────────┘
           │                                        │ circulação
           │ Tempval                                ▼
           └─────────────────────────────── reator / processo
```

O controlador externo deve residir no **Hub**, não no aplicativo e não no ESP32-S3 do banho:

1. o Hub já adquire `Tempval` diretamente da placa do módulo;
2. o Hub permanece operando se o aplicativo for desconectado;
3. o Hub escolhe a via térmica e já será responsável pela caixa confiável de comandos do
   banho;
4. o nó do banho continua com uma responsabilidade simples: aplicar e confirmar no C404 o
   setpoint que recebeu.

O modo `auto` já existente no nó não é essa nova malha. Ele apenas impede que uma alteração
manual no painel do C404 permaneça diferente do último alvo recebido. Para evitar ambiguidade:

- **automático do nó / guarda do painel:** mantém o C404 no último `bathCommandSetpoint`;
- **controle em cascata do Hub:** altera `bathCommandSetpoint` para que `Tempval` alcance
  `tempSetpoint`.

Quando a via externa estiver ativa, o nó deve ficar em modo `auto`, pois o guarda do painel e
a cascata são complementares e não concorrentes: cada novo comando da cascata passa a ser o
novo alvo defendido pelo guarda.

## 3. Decisões de projeto

### 3.1 Semântica operacional

1. O operador continua informando somente o setpoint do reator.
2. Na via original, `tempSetpoint` continua sendo enviado à placa TECNAL como hoje.
3. Na via do banho externo, `tempSetpoint` não pode mais ser encaminhado diretamente ao C404.
   Ele ativa/alimenta o controlador externo, que produz `bathCommandSetpoint`.
4. Controle direto do setpoint do C404 permanece apenas nas ferramentas locais de bancada do
   nó (`bath_app.py`, aplicativo próprio e `/ui`). Não deve aparecer como operação normal no
   aplicativo do Hub.
5. `tempSetpoint = 0` desativa a cascata, zera seu integrador e deixa de emitir novos comandos.
   Isso **não desliga fisicamente o C404**, que permanece no último setpoint. A interface e o
   manual devem deixar essa limitação explícita.
6. O integrador aprendido durante um cultivo não deve ser persistido na NVS. Carga térmica,
   volume, vazão e temperatura ambiente podem mudar entre lotes.

### 3.2 Controlador recomendado

Usar inicialmente um **PI**, sem termo derivativo. O processo térmico tem atraso elevado e a
derivada amplificaria ruído e quantização do sensor sem benefício claro.

Definições:

```text
r        = tempSetpoint, temperatura desejada no reator (°C)
y        = temperatura filtrada do reator, derivada de Tempval (°C)
e        = r - y (°C)
bias     = compensação térmica inicial configurável (°C)
I        = termo integral (°C de comando do banho)
u_raw    = r + bias + Kp*e + I
u_limit  = limite de segurança/faixa aplicado a u_raw
u_cmd    = u_limit após limitador de velocidade e quantização no passo do C404
```

Atualização integral:

```text
I = I + Ki * e * dt
```

`Kp` tem unidade °C_banho/°C_reator e `Ki` tem unidade
°C_banho/(°C_reator·s). A implementação também pode expor `Ti`, usando `Ki = Kp/Ti`, porque
`Ti` em segundos é mais simples de ajustar em bancada.

O termo `r + bias` fornece a ação antecipatória básica. O PI corrige perdas variáveis e calor
metabólico. No exemplo apresentado, o integral elevaria gradualmente o comando do C404 até
aproximadamente 31,3 °C, se esse for o valor necessário para manter o reator em 30,0 °C. Se o
cultivo passar a gerar calor, o mesmo integral reduzirá o comando do banho.

### 3.3 Proteções obrigatórias do algoritmo

- **Filtro da PV:** filtro passa-baixas de primeira ordem em `Tempval`, com constante de tempo
  configurável. Alarmes de sobretemperatura continuam usando o valor bruto, sem filtro.
- **Anti-windup:** integrar somente quando a saída não estiver saturada ou quando o erro estiver
  trazendo a saída de volta para dentro do limite. Nunca acumular integral com sensor inválido,
  nó offline, comando travado ou cascata pausada.
- **Limite absoluto:** `u_cmd` deve respeitar `sp_min`/`sp_max` do C404 e limites térmicos de
  processo mais restritivos configurados no Hub.
- **Limite relativo:** limitar quanto o banho pode ficar acima ou abaixo da referência do
  reator (`bath_max_offset_high_c` e `bath_max_offset_low_c`). Isso impede que um sensor
  incorreto peça uma água perigosamente quente ou fria.
- **Limite de velocidade:** restringir a variação do setpoint do C404 em °C/min para reduzir
  choque térmico e sobressinal.
- **Banda mínima de comando:** não emitir novo comando se a diferença para o alvo já aplicado
  for menor que uma banda configurável e menor que a resolução útil do C404.
- **Intervalo mínimo entre comandos:** não reajustar o C404 em toda leitura de `Tempval`. O
  processo é lento e os relés têm vida mecânica limitada.
- **Comando único em curso:** enquanto a caixa confiável estiver pendente ou o nó estiver em
  `running/settling`, guardar apenas o alvo calculado mais recente e enviá-lo quando o nó
  estiver pronto. Não criar uma fila histórica de setpoints obsoletos.
- **Inicialização sem degrau:** ao ativar a cascata, inicializar `I` para que a primeira saída
  coincida com o setpoint atualmente confirmado no C404. Se ele for desconhecido, iniciar em
  `r + bias`, sujeito aos limitadores.
- **Mudança de referência:** uma mudança feita pelo usuário deve atualizar imediatamente `r`,
  mas a saída continua sujeita aos limites de velocidade e segurança.

Parâmetros provisórios apenas para o primeiro ensaio, a confirmar por identificação do
processo:

| Parâmetro | Valor inicial conservador | Observação |
|---|---:|---|
| período de cálculo | 10 s | independente de `dataDelay` |
| constante do filtro de `Tempval` | 20 s | não usar para alarmes rápidos |
| intervalo mínimo entre comandos ao C404 | 30 s | aumentar se houver desgaste/oscilações |
| banda de novo comando | 0,1 °C | um passo do visor com `step_c = 0,1` |
| velocidade máxima do alvo do banho | 0,5 °C/min | parâmetro de partida, não valor validado |
| `Kp` | 0,5 | iniciar baixo |
| `Ti` | 600 s | integral lenta para a primeira bancada |
| `bias` | +0,6 °C | compensação inicial definida para o conjunto atual; o PI adapta o restante |
| offset máximo acima/abaixo de `r` | ±5,0 °C | reduzir conforme avaliação térmica e biológica |

Esses valores não devem ser tratados como sintonia de produção. Servem para iniciar testes sem
uma ação agressiva.

Com banda de 0,1 °C e quantização em `step_c = 0,1`, um novo comando pode ser enviado quando
o alvo calculado mudar pelo menos um passo do visor. Isso não limita cada comando a uma
variação de apenas 0,1 °C: se o PI calcular, por exemplo, uma mudança de 30,6 para 31,0 °C, o
novo alvo 31,0 °C pode ser enviado diretamente. O valor enviado será sempre quantizado em
múltiplos de 0,1 °C, enquanto a rapidez da mudança continuará limitada por
`bathCascadeSlewCMin` e pelo intervalo mínimo entre comandos.

## 4. Máquina de estados da cascata

Criar estados explícitos no Hub, evitando que um conjunto de `bool` permita combinações
contraditórias:

| Estado | Condição | Ação |
|---|---|---|
| `off` | via original ou `tempSetpoint = 0` | não calcula nem envia; integrador zerado |
| `waiting_inputs` | aguarda primeira PV válida, nó online e SP conhecido | não envia |
| `initializing` | condições válidas; prepara filtro e transferência sem degrau | calcula `I` a partir do alvo atual do C404 |
| `controlling` | todas as condições válidas | atualiza PI e agenda comandos conforme bandas/tempos |
| `actuator_busy` | caixa pendente ou sequência do nó em andamento | calcula para diagnóstico, mas não envia nova revisão |
| `paused` | falha temporária de PV/comunicação | congela `I`; C404 mantém o último alvo |
| `fault` | falha persistente, saturação longa ou erros repetidos do nó | bloqueia novos ajustes até reconhecimento ou novo comando válido |

Transições importantes:

- troca para a via externa: desligar a atuação térmica original, esperar `Tempval` válido e nó
  pronto, entrar por `initializing`; exigir um novo `tempSetpoint`, como já proposto para a
  troca de via;
- nó offline ou `Tempval` inválido: ir para `paused`, congelar integral e alarmar;
- retorno da comunicação: exigir amostras válidas consecutivas antes de retomar e executar
  novamente a inicialização sem degrau;
- `BathState = error/aborted` ou guarda `suspended`: ir para `fault`; não insistir
  indefinidamente apertando teclas;
- volta à via original: parar a cascata e limpar seu estado antes de reabilitar o comando UART.

## 5. Alterações necessárias no Hub

As alterações desta seção são adicionais aos itens H1–H11 de
`IMPLEMENTATION_PLAN_BANHO.md`. O item H6 daquele plano, que envia `tempReference`
diretamente ao banho, deve ser substituído pelo controlador descrito aqui.

### 5.1 Estado e nomes

Em `src/core/AppContext.h`:

- manter `tempReference` como referência do **reator**;
- criar snapshot persistente da última temperatura válida do reator e seu instante de leitura
  (`reactorTempPv`, `reactorTempPvValid`, `reactorTempPvAtMs`);
- separar `bathCommandSetpoint`, `bathCommandConfirmed`, `bathCascadeError`, termos P/I,
  saturação, último cálculo, último envio e estado da cascata;
- nunca reutilizar `BathTarget` como referência do reator: no protocolo do nó ele significa
  o último setpoint comandado ao C404.

### 5.2 Novo componente de controle

Criar um componente dedicado, por exemplo:

```text
src/control/ExternalBathCascade.h
src/control/ExternalBathCascade.cpp
```

Responsabilidades:

- receber configuração, referência do reator, PV e estado do atuador;
- filtrar a PV e executar o PI com tempo real decorrido, sem pressupor período perfeito;
- aplicar anti-windup, limites absoluto/relativo, velocidade e quantização;
- decidir se existe um novo comando relevante;
- expor um snapshot somente de diagnóstico para a telemetria;
- possuir métodos explícitos de `enable`, `disable`, `pause`, `resume` e `resetFault`;
- não conhecer HTTP, JSON, aplicativo ou GPIO.

Essa separação permite testar o algoritmo no PC com relógio simulado, sem compilar toda a
pilha Arduino.

### 5.3 Aquisição de temperatura

Em `src/sensor/Telemetry.h`, `temperatureVal` hoje é variável local de
`readAndBroadcastSensorData()`. Após validar a resposta de `sendSensorCommand("b", true)`, o
valor e seu timestamp devem ser publicados para o controlador sob a sincronização já usada
para o estado compartilhado. Valor fora da faixa ou resposta vazia deve marcar a PV como
inválida; `-1` não pode entrar no PI.

O timeout deve acompanhar a cadência de leitura, por exemplo
`max(3 × dataDelay, 5000 ms)`, com limite superior definido. Alterar `dataDelay` não deve
alterar o período nominal do PI.

### 5.4 Execução e roteamento

Em `src/core/Runtime.h`:

- executar o serviço da cascata na tarefa principal, após disponibilizar a nova amostra de
  temperatura, sem criar concorrência com `processOutgoingCommands()`;
- quando `tempControlRoute == ExternalBath`, o bloco de temperatura não envia
  `tempReference` diretamente ao nó;
- quando o controlador autorizar uma nova saída, montar
  `"setpoint":<bathCommandSetpoint>` e usar `queueReliable(bathBox, ...)`;
- não enviar enquanto `bathBox` estiver pendente, `BathState` estiver `running/settling` ou o
  nó estiver offline;
- manter estratégia **latest-wins**: apenas o cálculo mais recente deve sobreviver ao período
  em que o atuador estiver ocupado;
- o ACK da caixa informa que o nó aceitou/iniciou a ação; `BathState = done` e a confirmação
  pelo display informam que o C404 terminou. O intervalo entre comandos deve partir da
  conclusão, não apenas do ACK;
- a placa térmica original deve permanecer desativada durante toda a via externa, evitando
  duas malhas atuando simultaneamente.

### 5.5 Comandos e configuração

Em `src/protocol/Commands.h`, manter:

- `tempSetpoint`: referência do reator;
- `tempControlMode`: seleção da via original/externa;
- `bathMode`, `bathSync`, `bathAbort` para operação do nó.

Adicionar comandos de configuração claramente separados da referência de processo, por
exemplo:

| Chave | Função |
|---|---|
| `bathCascadeKp` | ganho proporcional |
| `bathCascadeTiS` | tempo integral; zero pode desativar a integral somente em bancada |
| `bathCascadeBiasC` | compensação antecipatória inicial |
| `bathCascadePeriodMs` | período do cálculo |
| `bathCascadeFilterS` | constante do filtro da PV |
| `bathCascadeCommandMinMs` | intervalo mínimo entre comandos concluídos |
| `bathCascadeCommandBandC` | alteração mínima para novo comando |
| `bathCascadeSlewCMin` | velocidade máxima do alvo do banho |
| `bathCascadeOffsetHighC` / `bathCascadeOffsetLowC` | limites relativos ao SP do reator |
| `bathCascadeOutputMinC` / `bathCascadeOutputMaxC` | limites absolutos |
| `bathCascadeReset` | limpa falha e reinicializa sem degrau |

Validar todos os números como finitos e aplicar faixas rígidas. Não aceitar alteração dos
ganhos no mesmo quadro de uma troca de via ou setpoint. Mudanças de sintonia devem reiniciar
o controlador sem degrau, não zerar a saída abruptamente.

### 5.6 Persistência

Em `src/storage/Settings.h`:

- persistir configuração e limites após validação;
- não persistir integral, PV filtrada, erro acumulado, temporizadores nem estado `controlling`;
- no boot, permanecer em `waiting_inputs` até confirmar PV e nó;
- recomendar que um novo comando de `tempSetpoint` seja exigido após reboot quando a via
  externa estiver selecionada. Se for mantido o comportamento atual de restaurar setpoints,
  isso deve ser uma decisão explícita e coberta por teste de segurança.

### 5.7 Telemetria

Além dos campos do plano anterior, publicar sempre:

```json
"TempSetpoint":30.0,
"BathCascadeState":"controlling",
"BathCascadeEnabled":true,
"BathCascadeError":0.18,
"BathCascadePvFiltered":29.82,
"BathCommandSetpoint":31.2,
"BathCommandConfirmed":31.1,
"BathCascadeP":0.09,
"BathCascadeI":1.11,
"BathCascadeSaturated":false,
"BathCascadePausedReason":"",
"BathCascadeLastUpdateMs":123456
```

Nomes podem ser ajustados ao padrão final, mas os conceitos não devem ser fundidos. Em
especial:

- `TempSetpoint`: alvo do reator;
- `Tempval`: PV do reator;
- `BathCommandSetpoint`: saída da cascata;
- `BathSp`/`BathTarget`: valores observados/defendidos pelo nó no C404;
- `BathPv`: temperatura da água do banho;
- `BathDeviation`: diferença interna do nó entre display de SP e alvo do C404;
- `BathCascadeError`: diferença do processo `TempSetpoint - Tempval`.

## 6. Alterações necessárias no nó do banho

O cálculo PI não deve ser duplicado no nó. Além dos pré-requisitos r3 já descritos no plano de
integração, são necessários apenas contratos que tornem o atuador observável:

1. continuar aceitando `setpoint` como setpoint **do C404**, não do reator;
2. enviar push ao Hub durante sequências longas, por tarefa própria;
3. reportar `seq_state`, `seq_error`, `sp_target`, `display_sp`, `display_pv`, `guard` e
   `ack_cmd_id` suficientes para o Hub distinguir comando aceito, em execução e concluído;
4. não confirmar `cmd_id` quando recusar por `busy`, `range`, display inválido ou outra falha;
5. manter idempotência: reentrega do mesmo `cmd_id` nunca repete toques;
6. considerar um campo futuro `source:"hub_cascade"` apenas para diagnóstico; ele não é
   requisito do controle;
7. quando comandado pelo Hub em via externa, operar com `sp_source = display` e guarda em
   `auto`. Modo sombra não oferece confirmação suficiente para uma cascata de processo.

## 7. Aplicativos

### 7.1 Interface operacional

Nos aplicativos Windows e Android do Hub:

- rotular o campo principal como **“Setpoint do reator”**;
- mostrar separadamente **“Temperatura do reator”**, **“Setpoint calculado do banho”** e
  **“Temperatura do banho”**;
- mostrar estado da cascata, erro do reator, saturação e motivo de pausa/falha;
- deixar sintonia em uma área avançada, com unidades, limites e restauração dos padrões;
- não permitir que o campo principal seja interpretado como setpoint direto do C404;
- avisar que “desativar/liberar” não desliga o banho externo;
- registrar em sessão tanto a referência do reator quanto a saída calculada do banho.

O aplicativo próprio do nó pode continuar comandando o C404 diretamente, mas deve avisar que
uma alteração local será substituída pelo Hub enquanto a cascata estiver ativa.

### 7.2 Alarmes mínimos

- sensor do reator inválido ou atrasado;
- nó do banho offline;
- comando pendente além do timeout;
- sequência `error`/`aborted` ou guarda suspenso;
- saída saturada por tempo configurável sem o reator entrar na tolerância;
- desvio persistente do reator;
- `BathPv` fora dos limites absolutos;
- diferença implausível entre banho e reator;
- atuação externa selecionada, mas placa original ainda reportada como ativa, se houver
  telemetria para comprová-lo.

## 8. Identificação e sintonia em bancada

Antes de habilitar a integral em cultivo, caracterizar o conjunto banho–mangueiras–camisa–vaso.

### 8.1 Ensaio de degrau

1. Usar água e volume representativos, agitação e vazão de circulação constantes.
2. Estabilizar o sistema com setpoint fixo do C404.
3. Aplicar um degrau pequeno e seguro, por exemplo +1,0 °C no C404.
4. Registrar a cada segundo `BathTarget`, `BathPv`, `Tempval` e temperatura ambiente até novo
   regime.
5. Repetir com degrau negativo se o equipamento aquecer e resfriar ativamente.
6. Estimar ganho estacionário `K`, atraso `theta` e constante de tempo `tau` entre comando do
   banho e temperatura do reator.

### 8.2 Sintonia inicial

Começar com `Ki = 0` e aumentar `Kp` lentamente até obter correção útil sem oscilação. Depois
introduzir integral lenta. Se for usado modelo de primeira ordem com atraso, uma sintonia IMC
conservadora pode partir de:

```text
Kp = tau / (K * (lambda + theta))
Ti = tau
```

Escolher `lambda` grande, no mínimo comparável à maior dinâmica observada, e tornar a malha
mais lenta se houver sobressinal ou comandos frequentes ao C404. O objetivo não é corrigir em
segundos; é remover erro estacionário sem excitar um processo que responde em minutos.

O `bias` pode ser preenchido com a diferença estacionária observada sem geração biológica,
mas o integral continua necessário para compensar mudanças de ambiente e metabolismo.

## 9. Simulador e testes automatizados

### 9.1 Controlador puro

Adicionar testes no Hub para:

- erro positivo aumenta gradualmente o setpoint do banho;
- erro negativo o reduz;
- erro zero mantém a saída;
- integral remove offset estacionário;
- saturação não causa windup;
- retorno da saturação ocorre sem salto;
- PV inválida congela o integrador e não envia comando;
- retomada é sem degrau;
- banda, período mínimo, quantização e slew rate são respeitados;
- mudança de setpoint e troca de via limpam estados corretos;
- valores `NaN`/infinitos são recusados;
- rollover de `millis()` não quebra os temporizadores.

### 9.2 Contrato Hub–nó

- setpoint do usuário nunca aparece diretamente no payload do nó quando a saída PI é
  diferente;
- `BathCommandSetpoint` é o valor colocado na caixa confiável;
- enquanto o nó está ocupado, no máximo um alvo mais recente fica pendente;
- ACK sem `done` não libera imediatamente outro comando;
- timeout, erro e abort pausam a cascata;
- reentrega de `cmd_id` não duplica acionamentos dos relés.

### 9.3 Modelo térmico no simulador

O simulador deve deixar de aproximar a temperatura diretamente por `TemperatureSetpoint` na
via externa. Modelar pelo menos dois estados:

```text
dT_banho/dt  = resposta da malha interna do C404 ao bathCommandSetpoint
dT_reator/dt = troca térmica banho↔reator + perda para ambiente + calor metabólico
```

Permitir variar perda ambiente e geração metabólica durante a execução. Cenários obrigatórios:

1. C404 em 30,0 °C produz reator em 28,7 °C; cascata elimina o offset;
2. entrada posterior de calor metabólico; cascata reduz o setpoint do banho e recupera o
   reator;
3. nó offline por alguns minutos; controlador pausa sem windup;
4. sensor do reator inválido; nenhum novo comando;
5. saída chega ao limite e gera alarme sem continuar acumulando integral;
6. degrau de referência sem exceder os limites de velocidade e sobressinal aceitos.

## 10. Validação integrada e critérios de aceite

| Ensaio | Critério de aceite |
|---|---|
| Offset térmico constante | reator entra e permanece na tolerância configurada em torno do SP; erro estacionário menor que 0,2 °C |
| Perturbação de calor | após perturbação representativa, recupera a tolerância sem oscilação sustentada |
| Desconexão do aplicativo | controle continua no Hub |
| Perda do nó | PI pausa, integral não cresce, alarme aparece e C404 não recebe comandos espúrios ao retornar |
| Falha de `Tempval` | nenhum novo setpoint é calculado/aplicado; retomada sem degrau após amostras válidas |
| Limites | setpoint do C404 nunca ultrapassa limites absolutos, relativos ou slew rate |
| Relés | frequência de comandos não excede o intervalo configurado; medir acionamentos por hora |
| Troca de via | nunca há atuação simultânea da placa original e do banho externo |
| Reboot do Hub | não há retomada inesperada nem salto da saída; comportamento segue a política documentada |
| Alteração manual no C404 | guarda restaura o alvo vigente; cascata continua referenciada ao reator |
| Calor metabólico crescente | integral adapta o comando sem exigir offset manual fixo |

A tolerância final, tempo de acomodação e sobressinal aceitável devem ser definidos com o
responsável pelo processo depois do ensaio de identificação; não devem ser inventados no
firmware.

## 11. Etapas detalhadas de implementação e commits

Esta seção é o roteiro de execução. Cada etapa termina em **um commit próprio**, compilável e
com os testes indicados aprovados. Não misturar alterações de nó, Hub, aplicativo Windows e
aplicativo Android no mesmo commit. Se uma etapa não passar pelo seu critério de conclusão,
ela não deve ser commitada nem servir de base para a seguinte.

As mensagens abaixo seguem o padrão já usado no repositório e são parte do plano. Ajustes
puramente mecânicos dentro da mesma etapa permanecem no mesmo commit; mudança de escopo exige
uma nova etapa e outro commit.

### E01 — Separar o enlace do Hub da tarefa que aciona os relés

**Dependência:** validações G1–G7 do nó concluídas.
**Componente:** `External-Devices/banho-termostatico`.

**Arquivos previstos:**

- `firmware/thermostatic-bath/src/core/FirmwareApp.cpp`;
- novo `firmware/thermostatic-bath/src/network/HubLink.h`;
- novo `firmware/thermostatic-bath/src/network/HubLink.cpp`;
- `firmware/thermostatic-bath/src/core/AppContext.h/.cpp`;
- `firmware/thermostatic-bath/src/network/NetworkManager.*`;
- `tests/host-sim/`.

**Alterações:**

1. mover `sendHubHello()` e `pushToHub()` para `HubLink`;
2. criar uma tarefa FreeRTOS exclusiva para HTTP, com snapshot protegido do estado do nó;
3. transportar respostas do Hub para o `loop()` por fila, mantendo `processCommand()` e o
   `SetpointManager` em uma única tarefa;
4. impedir que timeout HTTP altere a duração de um toque ou de um hold;
5. manter push periódico mesmo durante `running/settling`;
6. garantir que OTA/abort encerrem relés e tarefa de rede de forma segura;
7. medir e documentar pilha livre da nova tarefa.

**Testes e conclusão:** host-sim verde; teste de hold de 60 s sem alongamento do relé; push
continua em aproximadamente `send_period_ms`; sem acesso concorrente direto ao
`SetpointManager`; compilação do firmware e uso de RAM registrados.

**Commit:** `feat(banho-termostatico): separar enlace do Hub da sequencia de teclas`

### E02 — Fechar o contrato r3 do nó como atuador observável

**Dependência:** E01.
**Componente:** `External-Devices/banho-termostatico`.

**Arquivos previstos:**

- `src/config/BoardConfig.h`;
- `src/core/FirmwareApp.cpp` ou `src/network/HubLink.cpp`;
- `src/protocol/ConfigCodec.cpp`;
- `src/setpoint/SetpointGuard.*`;
- `docs/PROTOCOL.md`, `docs/CURRENT_STATUS.md`, `docs/VALIDATION.md`, `CHANGELOG.md`;
- `tests/host-sim/sim.cpp`.

**Alterações:**

1. trocar a rota para `/bathData` e anunciar `dev=bath`, versão `r3` e MAC real;
2. incluir no push `sp`, `known`, `target`, `state`, `phase`, `err`, `pv`, `pv_ok`,
   `display_sp`, `display_sp_ok`, `mode`, `guard`, `dev`, `dev_ok`, `time` e `ack_cmd_id`;
3. manter `ack_cmd_id` inalterado quando um comando for recusado por `busy`, `range`, display
   inválido ou qualquer outro erro;
4. testar que três reentregas do mesmo `cmd_id` causam no máximo uma sequência de teclas;
5. exigir `sp_source=display` para operação integrada em cascata e reportar configuração
   incompatível sem inventar um setpoint;
6. atualizar a identificação do firmware para r3 sem alterar a API local de bancada.

**Testes e conclusão:** golden push completo; recusa não confirma revisão; reentrega é
idempotente; `error/aborted/done` são distinguíveis; firmware compila; documentação do nó
corresponde exatamente ao fio.

**Commit:** `feat(banho-termostatico): publicar contrato r3 para controle pelo Hub`

### E03 — Integrar o nó do banho e sua caixa confiável no Hub

**Dependência:** E02.
**Componente:** `ESP32S3-HUB`.

**Arquivos previstos:**

- `ESP32S3-HUB/src/core/AppContext.h`;
- `ESP32S3-HUB/src/protocol/Mailboxes.h`;
- `ESP32S3-HUB/src/network/HttpServer.h`;
- `ESP32S3-HUB/src/network/NodeDiagTask.h`;
- `ESP32S3-HUB/Config.h`;
- `tests/contracts/test_node_registry.py`;
- novo `tests/contracts/test_bath_mailbox.py`;
- `tests/contracts/test_http_frames.py` e `test_json_keys.py`.

**Alterações:**

1. adicionar `DEV_BATH`, registro de identidade e timeout próprio;
2. criar `bathBox`, semear seu espaço de `cmd_id` a cada boot e expor estado pendente/ACK;
3. implementar `/bathData` com validação de todos os campos e números finitos;
4. atualizar o estado do nó sob `stateMutex`, confirmar a caixa pelo `ack_cmd_id` e devolver o
   payload pendente na resposta;
5. registrar `bath` em `/nodeHello`, `/nodes` e `/nodeDiag`;
6. aumentar buffers dimensionados pelo número de nós e medir o pior quadro;
7. ainda não rotear `tempSetpoint`: ao final desta etapa o canal existe, mas não controla o
   banho automaticamente.

**Testes e conclusão:** comando permanece até ACK igual; ACK antigo não limpa a caixa;
reinício do Hub não colide com revisão persistida no nó; payload inválido retorna `400`;
todos os contratos do Hub passam e o firmware compila.

**Commit:** `feat(hub): integrar no do banho com caixa confiavel`

### E04 — Transformar `Tempval` em entrada interna válida e temporal

**Dependência:** E03.
**Componente:** `ESP32S3-HUB`.

**Arquivos previstos:**

- `ESP32S3-HUB/src/core/AppContext.h`;
- `ESP32S3-HUB/src/sensor/Telemetry.h`;
- opcional novo `ESP32S3-HUB/src/control/TemperatureSample.h`;
- testes de contrato/host do Hub.

**Alterações:**

1. publicar `reactorTempPv`, validade e timestamp depois de validar a resposta UART `b`;
2. rejeitar resposta vazia, não finita ou fora da faixa; `-1` permanece apenas sentinela de
   telemetria e nunca entra no controle;
3. calcular stale timeout por `max(3 × dataDelay, 5000 ms)`, com teto explícito;
4. copiar a amostra por snapshot curto sob mutex; não executar cálculo PI segurando mutex;
5. preservar a aquisição de temperatura mesmo quando nenhuma via de controle estiver ativa;
6. cobrir rollover de `millis()` na idade da amostra.

**Testes e conclusão:** amostra válida atualiza valor/timestamp; amostra inválida derruba apenas
a validade; leitura antiga torna-se stale; telemetria existente de `Tempval` não muda.

**Commit:** `refactor(hub): disponibilizar temperatura do reator com validade temporal`

### E05 — Implementar o controlador PI puro e sua máquina de estados

**Dependência:** E04.
**Componente:** `ESP32S3-HUB`.

**Arquivos previstos:**

- novos `ESP32S3-HUB/src/control/ExternalBathCascade.h/.cpp`;
- novo teste host, por exemplo `tests/host/test_external_bath_cascade.cpp`;
- ajuste no script de testes host, sem integração ainda com `Runtime.h`.

**Alterações:**

1. implementar estados `off`, `waiting_inputs`, `initializing`, `controlling`,
   `actuator_busy`, `paused` e `fault`;
2. implementar filtro da PV com tempo real decorrido;
3. implementar `u = r + bias + Kp·e + I`, com `bias` padrão de **+0,6 °C**;
4. implementar integral por `dt`, anti-windup condicional e reinicialização sem degrau;
5. aplicar limites absolutos, offsets relativos, slew rate, quantização de 0,1 °C e banda de
   novo comando de **0,1 °C**;
6. aplicar período de cálculo e intervalo mínimo entre comandos como restrições distintas;
7. congelar integral com entradas inválidas, atuador travado ou estado pausado;
8. expor snapshot imutável com erro, PV filtrada, P, I, saída bruta, saída limitada, saturação,
   estado e motivo de pausa;
9. não incluir dependências de HTTP, JSON, NVS ou Arduino além do mínimo de tipos/tempo.

**Testes e conclusão:** cobrir todos os itens de §9.1, inclusive anti-windup, retomada sem
degrau, quantização, banda de 0,1 °C, bias de +0,6 °C, slew rate, NaN e rollover. Teste host e
compilação embarcada verdes.

**Commit:** `feat(hub): adicionar controlador PI da cascata termica externa`

### E06 — Orquestrar a cascata e tornar a troca de via segura

**Dependência:** E03–E05.
**Componente:** `ESP32S3-HUB`.

**Arquivos previstos:**

- `ESP32S3-HUB/src/core/Runtime.h`;
- `ESP32S3-HUB/src/core/AppContext.h`;
- `ESP32S3-HUB/src/protocol/Commands.h`;
- `ESP32S3-HUB/src/sensor/SensorUart.h`;
- `tests/contracts/test_temp_route.py`;
- testes host de orquestração.

**Alterações:**

1. criar `TempControlRoute { UartModule, ExternalBath }` e processar `tempControlMode`;
2. fazer a troca *break-before-make*: ao entrar na via externa enviar `100B` à placa original,
   confirmar `tempOn=false` e somente depois permitir a cascata;
3. ignorar `tempSetpoint` recebido no mesmo quadro da troca de via e exigir novo comando;
4. manter `tempReference` como setpoint do reator;
5. chamar o controlador depois da aquisição de uma amostra nova e também atender seus
   temporizadores no loop principal;
6. enviar somente `bathCommandSetpoint` pela `bathBox`; proibir o repasse direto de
   `tempReference` ao nó;
7. só liberar novo comando quando caixa anterior estiver confirmada e `BathState=done`;
8. manter um único alvo latest-wins enquanto o atuador estiver ocupado;
9. entrar em pausa/falha nas condições de §4 e nunca fazer fallback automático para a placa
   original;
10. ao voltar à via original, desabilitar e limpar a cascata antes de aceitar novo setpoint
    UART;
11. `tempSetpoint=0` desativa a cascata e não promete desligar o C404.

**Testes e conclusão:** não existe caminho que atue nas duas vias; setpoint do usuário e saída
do banho podem ser diferentes; ACK sem `done` não dispara outro comando; perda/retorno do nó
é sem windup e sem degrau; contratos e compilação verdes.

**Commit:** `feat(hub): rotear temperatura externa pela cascata do banho`

### E07 — Validar e persistir a configuração da cascata

**Dependência:** E06.
**Componente:** `ESP32S3-HUB`.

**Arquivos previstos:**

- `ESP32S3-HUB/src/protocol/Commands.h`;
- `ESP32S3-HUB/src/storage/Settings.h`;
- `ESP32S3-HUB/src/core/AppContext.h`;
- `tests/contracts/test_bath_cascade_config.py`.

**Alterações:**

1. implementar todas as chaves de configuração de §5.5;
2. validar finitude, faixas, coerência `min < max`, offsets não negativos e períodos seguros;
3. definir padrões: bias +0,6 °C, banda 0,1 °C e demais valores provisórios da tabela de
   §3.3;
4. persistir apenas configuração aprovada e seleção de via;
5. nunca persistir integral, PV filtrada, saída transitória ou estado operacional;
6. reinicializar sem degrau após mudança de ganhos;
7. definir e testar a política de reboot: via pode permanecer selecionada, mas a cascata deve
   aguardar entradas válidas e novo `tempSetpoint` antes de atuar;
8. incluir configuração no hash/debounce de NVS sem gravar flash a cada ciclo.

**Testes e conclusão:** limites inválidos são recusados sem mutação parcial; reboot restaura
configuração, mas não atuação/integral; defaults coincidem com este documento; teste de desgaste
de NVS confirma que o loop não grava continuamente.

**Commit:** `feat(hub): persistir configuracao segura da cascata do banho`

### E08 — Publicar diagnóstico completo da cascata no Hub

**Dependência:** E06–E07.
**Componente:** `ESP32S3-HUB`.

**Arquivos previstos:**

- `ESP32S3-HUB/src/sensor/Telemetry.h`;
- `ESP32S3-HUB/src/network/HttpServer.h`;
- `ESP32S3-HUB/Config.h`;
- `tests/contracts/test_json_keys.py`;
- `tests/contracts/test_bath_cascade_telemetry.py`;
- documentação do Hub e changelog.

**Alterações:**

1. publicar os campos de §5.7, sempre separando referência do reator, PV do reator, saída da
   cascata e estado do C404;
2. usar `null` para valores sem validade, nunca sentinelas numéricas apresentadas como PV;
3. publicar saturação, motivo de pausa/falha, idade da PV, último comando e confirmação;
4. garantir snapshot consistente sem manter mutex durante montagem de `String`;
5. medir tamanho máximo de `/readData` e ampliar reserva/buffer somente com valor registrado;
6. atualizar versão do Hub conforme decisão do plano-base e registrar flash/RAM;
7. documentar que `BathMode=auto` é guarda do painel e não a cascata térmica.

**Testes e conclusão:** quadro completo, quadro sem nó, valores `null`, strings máximas e
orçamento JSON cobertos; todos os contratos passam; firmware compila dentro do orçamento.

**Commit:** `feat(hub): expor diagnosticos da cascata termica do banho`

### E09 — Modelar banho e reator como duas massas térmicas no simulador

**Dependência:** contrato de telemetria E08.
**Componente:** `Windows_app/OpenTECHub.Simulator`.

**Arquivos previstos:**

- `src/OpenTECHub.Simulator/DeviceModel.cs`;
- `src/OpenTECHub.Simulator/WireCodec.cs`;
- `src/OpenTECHub.Simulator/HttpEndpoint.cs`;
- novos testes de modelo térmico em `tests/OpenTECHub.Tests/`.

**Alterações:**

1. criar estados independentes de temperatura do banho e do reator;
2. modelar resposta interna do C404, transferência banho–reator, perda ambiente e calor
   metabólico ajustável;
3. simular sequência/ACK/done do nó, indisponibilidade, erro, abort e guarda suspenso;
4. fazer a via original manter o modelo anterior e a via externa usar obrigatoriamente as duas
   massas;
5. oferecer cenários determinísticos de offset de 1,3 °C, entrada de calor e perda de rede;
6. serializar exatamente os campos definidos em E08.

**Testes e conclusão:** os seis cenários de §9.3 passam com relógio determinístico; o simulador
antigo continua compatível; nenhum teste depende de tempo real ou rede física.

**Commit:** `feat(simulator): modelar cascata termica entre banho e reator`

### E10 — Adicionar o contrato da cascata ao aplicativo Windows

**Dependência:** E08–E09.
**Componente:** `Windows_app`.

**Arquivos previstos:**

- `src/OpenTECHub.Protocol/CommandKeys.cs`;
- `CommandBuilders.cs`, `CommandActuators.cs`;
- `SensorReadings.cs`, `TelemetryParser.cs`;
- novo ou ampliado `tests/OpenTECHub.Tests/ExternalBathTests.cs`.

**Alterações:**

1. adicionar comandos da via, operação do nó e sintonia avançada;
2. adicionar todos os campos de telemetria da cascata com nulabilidade correta;
3. manter compatibilidade com Hub antigo por `HasBathTelemetry=false`;
4. mapear todos os comandos ao `ActuatorId.Temperature` para respeitar o arbiter;
5. criar builders com faixa e serialização invariável;
6. preservar `Clone`, snapshots e comparação de leituras.

**Testes e conclusão:** golden strings, parser completo/parcial/nulo, Hub antigo e arbiter
passam; solução Windows compila sem mudanças visuais ainda.

**Commit:** `feat(windows): adicionar contrato da cascata do banho externo`

### E11 — Implementar operação e sintonia da cascata na interface Windows

**Dependência:** E10.
**Componente:** `Windows_app`.

**Arquivos previstos:**

- `ViewModels/ExternalBathViewModel.cs`;
- `ViewModels/ControlViewModel.cs` e `SubsystemViewModel.cs`;
- `Views/ControlView.xaml`;
- `App.xaml.cs`;
- `Services/Persistence/AppSettings.cs` e `SettingsViewModel`;
- testes de view-model e contratos de XAML.

**Alterações:**

1. renomear o campo operacional para “Setpoint do reator”;
2. exibir `Tempval`, PV filtrada, erro, setpoint calculado do banho, setpoint confirmado no
   C404 e `BathPv` sem fundi-los;
3. implementar seletor de via com reversão visual se o comando for recusado;
4. mostrar estados, saturação e motivo de pausa/falha;
5. colocar Kp, Ti, bias (+0,6 °C), banda (0,1 °C), limites e períodos em painel avançado;
6. validar números e pedir aplicação explícita da sintonia;
7. desabilitar atuação quando o Hub não oferece telemetria ou o nó não está pronto;
8. mostrar aviso permanente de que liberar a cascata não desliga o C404;
9. persistir preferências de interface sem enviar comandos automaticamente ao abrir o app.

**Testes e conclusão:** estados online/offline/pausado/saturado, validação de sintonia, recusa de
via e compatibilidade com Hub antigo cobertos; testes Windows verdes; revisão visual realizada
contra o simulador.

**Commit:** `feat(windows): adicionar operacao e sintonia da cascata termica`

### E12 — Adicionar alarmes, receitas, gráficos e registro de sessão no Windows

**Dependência:** E11.
**Componente:** `Windows_app`.

**Arquivos previstos:**

- `Services/Alarms/AlarmModels.cs` e `AlarmService.cs`;
- `Services/Recipes/RecipeEngine.Actuation.cs`, `RecipeEngine.Devices.cs` e
  `RecipeValidator.cs`;
- `Services/Telemetry/SessionLogger.cs`, `SessionFiles.cs`, `TelemetryHistory.cs`;
- `ChartsViewModel.cs`;
- documentação e testes correspondentes.

**Alterações:**

1. implementar todos os alarmes de §7.2 com on-delay, severidade e latch definidos;
2. receita de temperatura aguarda cascata ativa, ACK e `done`, e entra em hold em falha;
3. calcular timeout da receita com base na distância e nos tempos medidos, não em constante
   silenciosa;
4. registrar `TempSetpoint`, `Tempval`, PV filtrada, erro, P, I, saída do banho, `BathPv`,
   saturação e estado;
5. adicionar séries opcionais do banho e da saída da cascata ao gráfico de temperatura;
6. atualizar manual, protocolo, ADRs e changelog do aplicativo;
7. atualizar versão do aplicativo somente após todos os testes desta camada.

**Testes e conclusão:** alarmes de falha/recuperação/ack, hold de receita, CSV e gráficos
cobertos; suíte Windows integral verde; sessão abre em versões anteriores sem quebrar colunas
obrigatórias.

**Commit:** `feat(windows): supervisionar cascata do banho em alarmes e receitas`

### E13 — Adicionar modelos e comandos da cascata ao aplicativo Android do Hub

**Dependência:** E08.
**Componente:** `Android_app`.

**Arquivos previstos:**

- novo `lib/models/external_bath_state.dart`;
- `lib/providers/telemetry_provider.dart`;
- `lib/providers/device_control_provider.dart`;
- `lib/constants/api_constants.dart`;
- testes de modelo e provider.

**Alterações:**

1. interpretar presença, via, estado do nó e todos os diagnósticos da cascata;
2. preservar `null` para PV/termos inválidos;
3. adicionar comandos de via, modo, sync, abort, reset e configuração avançada;
4. manter todos sob o domínio de controle de temperatura;
5. garantir que `resetAll` envie `tempSetpoint=0`, mas não prometa desligar nem mude a via;
6. manter compatibilidade com Hub sem suporte ao banho.

**Testes e conclusão:** fixtures completas, parciais e antigas; serialização exata dos comandos;
`flutter analyze` e testes de unidade verdes.

**Commit:** `feat(android): adicionar contrato da cascata do banho externo`

### E14 — Implementar a interface da cascata no aplicativo Android do Hub

**Dependência:** E13.
**Componente:** `Android_app`.

**Arquivos previstos:**

- `lib/screens/controls_screen.dart`;
- `lib/screens/dashboard_screen.dart` e `graphs_screen.dart`;
- novo `lib/widgets/external_bath_card.dart`;
- `lib/screens/settings_page.dart` ou painel avançado equivalente;
- testes de widget, `README.md` e `pubspec.yaml`.

**Alterações:**

1. usar “Setpoint do reator” como rótulo principal;
2. mostrar referência/PV do reator, saída calculada, SP/PV do C404 e estado da cascata;
3. implementar seletor de via e controles operacionais do nó;
4. implementar edição avançada com bias +0,6 °C e banda 0,1 °C visíveis;
5. mostrar saturação, pausa, falha e aviso de que o C404 não é desligado remotamente;
6. adicionar cartão de dashboard e séries no gráfico;
7. incrementar versão somente depois de análise e testes aprovados.

**Testes e conclusão:** widgets nos estados sem suporte/offline/controlando/pausado/falha;
campos inválidos não enviam comando; `flutter analyze` e `flutter test` verdes; revisão em tela
pequena e grande.

**Commit:** `feat(android): adicionar interface da cascata termica do banho`

### E15 — Identificar a planta e registrar a sintonia de bancada

**Dependência:** E01–E14; executar apenas com água e proteções térmicas verificadas.
**Componente:** documentação/evidências e, se necessário, somente defaults aprovados do Hub.

**Arquivos previstos:**

- evidências em diretório de validação do projeto;
- `External-Devices/banho-termostatico/docs/VALIDATION.md`;
- documentação de validação do Hub;
- este plano e changelogs;
- arquivos de defaults do Hub somente se os valores aprovados diferirem dos provisórios.

**Alterações e ensaios:**

1. executar degraus positivos e negativos de §8 com volume, circulação e agitação registrados;
2. estimar `K`, `theta`, `tau`, erro de sensor, resolução e capacidade de aquecer/resfriar;
3. testar inicialmente `Ki=0`; ajustar Kp; só depois habilitar integral lenta;
4. medir comandos/hora, sobressinal, tempo de acomodação e diferença banho–reator;
5. repetir perda de rede, PV inválida, saturação, abort, reboot e retorno de comunicação;
6. aprovar ou revisar limites, período, slew rate, Kp, Ti, mantendo bias inicial +0,6 °C e
   banda 0,1 °C salvo evidência contrária;
7. registrar firmware, hardware, volume, ambiente, dados brutos e resultado de cada critério;
8. não incluir alteração funcional não validada neste commit; qualquer correção encontrada
   volta para uma nova etapa de código com testes próprios.

**Testes e conclusão:** todos os critérios de §10 aprovados com água; parâmetros de produção
assinados pelo responsável do processo; nenhuma validação depende apenas de observação visual.

**Commit:** `test(bath-cascade): registrar identificacao e sintonia de bancada`

### E16 — Fechar documentação operacional e prontidão para cultivo

**Dependência:** E15 aprovado.
**Componente:** documentação transversal.

**Arquivos previstos:**

- `COMANDOS_DISPOSITIVOS_EXTERNOS.md`;
- `External-Devices/README.md`;
- manuais do operador do Hub e dos aplicativos;
- `CURRENT_STATUS.md`, `CHANGELOG.md` e ADRs de cada camada;
- checklist de liberação do cultivo.

**Alterações:**

1. substituir textos que descrevem envio direto de `tempSetpoint` ao C404;
2. documentar claramente as duas malhas, nomes dos sinais e estados;
3. registrar parâmetros aprovados, procedimento de habilitação, pausa, retomada e troca de via;
4. incluir procedimento de contingência para falha do Hub, nó, sensor e C404;
5. declarar que parada lógica não desliga fisicamente o banho;
6. listar versões mínimas compatíveis de nó, Hub e aplicativos;
7. fechar checklist de prontidão somente após conferir que binários publicados correspondem
   aos commits validados.

**Testes e conclusão:** links e versões conferidos; protocolo sem contradições; operador
consegue distinguir setpoint do reator de setpoint do banho; checklist formalmente fechado.

**Commit:** `docs(bath-cascade): consolidar operacao e prontidao para cultivo`

## 12. Riscos e limitações que permanecem

- O C404 não possui comando remoto de desligamento nesta integração por teclas. Software não
  substitui proteção térmica independente, limite físico do equipamento ou procedimento de
  emergência.
- O tempo morto da circulação pode ser grande; ganhos agressivos provocam oscilação mesmo com
  um PI matematicamente correto.
- Temperatura do banho (`BathPv`) e do reator (`Tempval`) têm significados diferentes e não
  podem ser trocadas quando uma delas falhar.
- Alterações de vazão, volume, agitação, isolamento e metabolismo mudam a planta. A integral
  compensa offset, mas não corrige uma sintonia instável.
- A resolução de 0,1 °C do C404, os tempos de toque e o desgaste dos relés impõem uma banda
  prática; tentar controle muito fino aumentará acionamentos sem melhorar o processo.
- Se o banho não tiver capacidade ativa de resfriamento, a cascata não conseguirá compensar
  geração metabólica acima da perda passiva. Esse caso deve aparecer como saturação, não como
  aumento indefinido da integral.

---

## Resultado esperado

Com a implementação deste plano, o usuário continuará solicitando 30,0 °C para o **reator**.
O Hub observará a temperatura real do reator e ajustará lentamente o C404 para o valor
necessário — 31,3 °C no exemplo de perda, ou um valor menor quando houver geração de calor —
mantendo separados e observáveis a referência do processo, a PV do processo e o comando do
atuador.
