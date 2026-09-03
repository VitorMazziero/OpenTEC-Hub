# Plano de integração do servo drive no aplicativo — Gates D e E

**Data:** 2026-09-02
**Escopo:** o que muda no OpenTEC-Hub (PC) para consumir a telemetria de potência
do Delta ASDA-B2 publicada pelo Hub v9 — e, com a rotação finalmente medida, a
correção do CN1 que faz o eixo entregar o que foi pedido (§2.6, já no firmware).
**Pré-requisito cumprido:** Gates A, B e C aprovados na bancada em 2026-09-02, e
o nó instalado no ModuloTECNAL_2. Os dois firmwares estão gravados e provados.

**Leia antes:**

- `Potencia TECNAL/PROTOCOLO_HUB_v9_PARA_APLICATIVO.md` — a semântica dos campos,
  escrita para este trabalho. É a fonte normativa deste plano.
- `_ESP32S3_firmware/docs/WIRE_CONTRACT_V9.md` — o contrato formal dos endpoints.
- `Potencia TECNAL/PLANO_INTEGRACAO_POTENCIA_OPENTECHUB.md` §5 — o esboço
  anterior. **Este documento o substitui**: aquele foi escrito antes da bancada e
  não conhecia metade do que está aqui.

---

## 1. As variáveis que passam a existir

### 1.1. O que vem do fio

Quatorze chaves, em dois grupos que se comportam de maneira diferente. **A
distinção governa o parser inteiro.**

**Sempre publicadas** — existem no quadro com o nó presente ou ausente:

| Chave | Tipo | Significado |
|---|---|---|
| `ServoOnline` | bool | houve push válido nos últimos 6000 ms |
| `ServoCommEnabled` | bool | espelho do flag `servoComm` persistido em NVS |
| `ServoCommandPending` | bool | há comando enfileirado e não consumido |
| `ServoCommandQueueDepth` | int 0..8 | profundidade da FIFO do hub |

**Publicadas só quando há amostra publicável** — presença fresca **e** roteamento
ligado. Nos demais casos **a chave não existe no JSON**:

| Chave | Unidade | Origem | Observação |
|---|---|---|---|
| `ServoRpm` | rpm | P0-09 (código 7) | medida real do eixo; 0 é medida legítima |
| `ServoTorquePct` | % | P0-44 (código 54) | torque instantâneo, com sinal, resolução 0,1 % |
| `ServoTorqueNm` | N·m | derivado | depende de `MOTOR_RATED_TORQUE_NM` |
| `ServoLoadPct` | % | P0-10 (código 12) | carga **média**, inteiro — não é o mesmo que torque |
| `ServoPowerW` | W | derivado | potência **mecânica estimada** no eixo |
| `ServoEnergyWh` | Wh | integrado no nó | **pode andar para trás** |
| `ServoState` | 0..3 | P0-46 | 0 OFF · 1 READY · 2 SON · 3 ALARM |
| `ServoAlarm` | int | P0-01 | hex espelha o painel: `0x0011` = `AL011` |
| `ServoCommOk` | uint32 | contador | +3 por amostra bem-sucedida |
| `ServoCommErr` | uint32 | contador | +1 por amostra falhada |

> **Chave ausente não é chave zero.** `ServoRpm: 0.0` é um motor parado; a
> **ausência** de `ServoRpm` é "sem dado". O app renderiza traço, nunca zero. É a
> regra que o `SensorReadings.NotReceived` já existe para expressar.

### 1.2. Como cada número é calculado

O nó lê três blocos Modbus por amostra, com função `03H`:

| Endereço | Registradores | Conteúdo |
|---|---|---|
| `0x0002` | 1 word | `P0-01` — código de alarme |
| `0x0012` | 4 words | `P0-09` (2) velocidade + `P0-10` (2) carga média |
| `0x0058` | 6 words | `P0-44` (2) torque + `P0-45` (1) seletor + `P0-46` (1) status |

`P0-09`, `P0-10` e `P0-44` são **registradores de monitor**: mostram a grandeza
que `P0-17`, `P0-18` e `P0-45` mandarem mostrar. Com os valores de fábrica eles
respondem Modbus normalmente e devolvem **outra coisa**. O mapeamento vigente:

```text
P0-17 = 7    ->  P0-09 = velocidade de retorno   (0,1 rpm)
P0-18 = 12   ->  P0-10 = carga media             (% inteiro)
P0-45 = 54   ->  P0-44 = torque de retorno       (0,1 %)
```

`P0-45` é **volátil** — zera a cada religamento do drive — então quem o mantém em
54 é o firmware, por escrita `06H` confirmada por leitura, a cada amostra. Uma
amostra sem mapeamento válido é **descartada inteira**, não publicada.

**Composição das words.** Cada grandeza de 32 bits vem em duas words, **low word
primeiro**, interpretadas com sinal:

```text
speedRaw  = int32(low = w[0], high = w[1])      de 0x0012
loadRaw   = int32(low = w[2], high = w[3])      de 0x0016
torqueRaw = int32(low = w[0], high = w[1])      de 0x0058
status    = w[4]                                 P0-46
```

A ordem foi decidida na bancada em 2026-09-02, nas duas grandezas: a 100 rpm,
low-first deu 95,8..98,6 rpm contra ~96 no painel; high-first deu 6.330.777 rpm.
No torque, 1,4 % contra 91.750 %.

**As linhas de aritmética.** É tudo o que existe:

```text
rpm       = speedRaw  x 0,1                    [rpm]
torquePct = torqueRaw x 0,1                    [%]
torqueNm  = torqueRaw x (T_nom / 1000)         [N.m]
loadPct   = loadRaw                            [%]     <- sem escala, escala 1
powerW    = torqueNm x rpm x 2*pi/60           [W]
energyJ  += ((P_anterior + P) / 2) x dt        [J]     <- trapezio
energyWh  = energyJ / 3600                     [Wh]
```

Com `T_nom = MOTOR_RATED_TORQUE_NM = 1,27 N·m` (motor ECMA-C20604ES).

- **`torqueNm`** é o torque percentual aplicado ao torque nominal de placa:
  `torqueRaw × T_nom/1000` é idêntico a `(torquePct/100) × T_nom`. O drive não
  mede N·m; ele reporta uma fração do nominal, e o N·m é essa fração vezes um
  número que veio da **placa de identificação do motor**, não de uma medida.
- **`powerW`** é `P = T·ω`, com `ω = rpm × 2π/60` em rad/s. A constante
  `TWO_PI_OVER_60 = 0,10471975511965977` é esse fator.
- **`energyWh`** é a integral trapezoidal da potência, **com rejeição de lacuna**:
  se `Δt > 2,5 × poll_ms`, o intervalo é descartado em vez de integrado. Perder
  amostras não inventa energia.

**Conferência numérica da bancada (2026-09-02, Módulo 2):**

```text
torque 1,4 %  ->  0,014 x 1,27       = 0,01778 N.m    publicado 0,0178
rpm 92,7      ->  92,7 x 0,10472     = 9,707 rad/s
potencia      ->  0,01778 x 9,707    = 0,1726 W       publicado 0,17
```

### 1.3. O que esses números **não** são

Quatro ressalvas que precisam aparecer na interface, não só neste documento:

1. **Potência mecânica estimada, não elétrica.** Deriva de torque × velocidade no
   eixo. Não inclui perdas do drive, do motor, nem o consumo da eletrônica. Um
   operador que ler "0,17 W" como consumo da tomada erra por ordens de grandeza.
2. **Tudo em N·m e W escala linearmente com `T_nom`.** Trocar o motor sem trocar
   `MOTOR_RATED_TORQUE_NM` produz torque e potência errados **sem nenhum sinal de
   erro** — o rpm continua certo, os contadores continuam limpos. É a mesma classe
   de falha silenciosa do `P0-45`.
3. **`ServoTorquePct` ≠ `ServoLoadPct`.** O primeiro é torque instantâneo, com
   sinal, resolução 0,1 %. O segundo é taxa de carga **média**, inteiro. São duas
   grandezas do drive, não duas versões da mesma. Na bancada bateram entre si
   (`P0-44` = 1,4 % com `P0-10` = 1 %), o que é conferência, não redundância.
4. **A energia pode voltar para trás.** É integrada no nó: zera no reboot dele e
   no `resetServoEnergy`, e estagna nas lacunas. O gráfico precisa aguentar
   descontinuidade para baixo sem tratá-la como erro.

### 1.4. Variáveis derivadas que o app pode calcular

Além das quatorze do fio, quatro grandezas que só o app tem como produzir:

| Derivada | Fórmula | Onde | Prioridade |
|---|---|---|---|
| **Taxa de erro Modbus** | `Δerr / (Δok + Δerr)` numa janela | `ServoDriveViewModel` | **no escopo** |
| **Desvio comando−medida** | `ServoRpm − motorSetpoint` | sai de graça (§2.4) | **no escopo** |
| **Potência específica P/V** | `P_liq / V_util` [W/m³] | novo | fase 2, ver abaixo |
| **Número de potência Np** | `P / (ρ N³ D⁵)` | novo | não recomendado agora |

**A taxa de erro tem de ser taxa.** Na bancada apareceu `ServoCommErr = 1` em 256
leituras, na primeira transação após o boot. Um total absoluto de 1 fica no
histórico para sempre; a **taxa** volta a zero e é o que distingue ruído real de
um soluço de inicialização. Alarme olha a taxa, nunca o acumulado.

**Sobre P/V.** É o parâmetro clássico de escalonamento de biorreator, e o app já
tem `KlaMappingViewModel` e `KlaDeterminationViewModel` — pela primeira vez existe
potência de eixo medida para correlacionar com kLa, o que hoje é feito por
correlação com rpm. Vale muito, mas **não entra neste plano**, por duas razões
metodológicas honestas:

- a potência medida inclui atrito de mancal, selo mecânico e acoplamento; o que
  entra na correlação de kLa é a potência entregue ao líquido. O caminho correto é
  levantar uma curva de vazio `P_vazio(N)` com o vaso descarregado e usar
  `P_liq(N) ≈ P_medida(N) − P_vazio(N)`. Isso é um ensaio, não uma linha de
  código;
- exige o volume útil como entrada declarada por ensaio, que hoje não existe no
  modelo de dados.

Fica registrado como o passo seguinte natural depois do Gate F, com o sidecar
desta fase já contendo os dados brutos necessários para levantá-lo
retroativamente.

---

## 2. A série de RPM — de comando para medida, e o laço que ela permite

**Este é o item de maior valor do plano e o de maior risco.** Até hoje o
aplicativo nunca teve realimentação de rotação. O código diz isso em três lugares,
e os três estão prestes a ficar falsos.

As §§2.1 a 2.5 tratam da medida chegar sem quebrar o que já existe. As §§2.6 a 2.9
tratam do que fazer com ela: **um PI que fecha o laço de rotação**, corrigindo o
erro de ganho e offset do CN1 que a bancada mediu.

### 2.1. Como está hoje

`ShellViewModel.cs:172` — a variável de processo de agitação nasce como "somente
comandada":

```csharp
// No RPM feedback exists on the wire, so this variable can only ever show
// what was commanded.
Motor = new ProcessVariableViewModel(
    "motor", "Agitação", "rpm", decimals: 0, isCommandedOnly: true,
    channel: TelemetryChannel.MotorRpm);
```

`SubsystemViewModel.cs:103` e `:331` — por ser `IsCommandedOnly`, o setpoint
aplicado é copiado para dentro do valor:

```csharp
Variable.Setpoint = AppliedSetpoint;
if (Variable.IsCommandedOnly) { Variable.PushCommanded(AppliedSetpoint); }
```

`ShellViewModel.cs:1210` — e é esse valor que alimenta o histórico e o log:

```csharp
// Motor is deliberately not pushed here: the device reports no RPM feedback...
var commandedRpm = Motor.Value ?? 0;
_history.Add(snapshot, commandedRpm);
_sessionLogger.Write(snapshot, commandedRpm, DescribeConnection());
```

Ou seja: **`Motor.Value`, `TelemetryChannel.MotorRpm` e a coluna 2 do TSV são
todos o comando**, e são o mesmo número.

### 2.2. A armadilha

O caminho ingênuo — empurrar `snapshot.ServoRpm` para `Motor.Value` — funciona na
tela e **corrompe silenciosamente o log**. Como `commandedRpm` é lido de
`Motor.Value`, a coluna 2 do TSV e o canal `MotorRpm` passariam a conter a medida,
com o mesmo cabeçalho, no mesmo formato, sem nenhum aviso. Todo script de análise
existente continuaria lendo e passaria a responder outra pergunta.

`SessionLogger.cs` declara o formato como contrato congelado, e
`SessionFiles.cs:108` mapeia `[TelemetryChannel.MotorRpm] = 2` para reabrir
sessões antigas. Os dois quebram junto.

### 2.3. O que fazer

**Regra:** `MotorRpm` continua sendo o comando, para sempre. A medida é um canal
novo. As duas convivem, e a interface mostra as duas.

| # | Arquivo | Mudança |
|---|---|---|
| 1 | `ShellViewModel.cs:1224` | `var commandedRpm = Motor.Setpoint ?? 0;` — **primeiro**, antes de qualquer outra coisa. Desacopla o log da variável de tela |
| 2 | `ShellViewModel.cs:172` | `isCommandedOnly: false`; apagar o comentário obsoleto |
| 3 | `ShellViewModel.cs:1210` | passar a empurrar a medida quando publicável, `null` quando não |
| 4 | `ProcessVariableViewModel.cs:209` | remover o caso especial que mostra traço quando `MotorRpm` é zero |
| 5 | `TelemetryHistory.cs:20` | novo `TelemetryChannel.ServoRpm`, separado de `MotorRpm` |

Feito o item 1, os demais deixam de ter efeito colateral.

**O item 3, em detalhe.** A medida só é válida quando publicável:

```csharp
Motor.Push(snapshot.HasServoTelemetry && snapshot.ServoOnline &&
           snapshot.ServoRpm > SensorReadings.NotReceived
    ? snapshot.ServoRpm
    : null);
```

Com o Hub v7/v8, ou com o nó ausente, `Motor.Value` fica nulo e o card mostra
traço — que é a verdade. O setpoint continua visível ao lado, vindo do
`SubsystemViewModel`, então a informação que o operador tinha antes **não se
perde**: ela muda de campo, do valor para o setpoint.

**O item 4 é obrigatório, não cosmético.** Hoje o zero é mascarado porque um
comando de 0 rpm significa malha inativa. Amanhã, 0 rpm com o nó online é uma
medida legítima de motor parado, e mostrar traço nesse caso esconderia exatamente
a informação que o novo enlace foi instalado para dar.

### 2.4. O desvio que passa a existir — e que a correção encolheu

`SubsystemViewModel.FormattedDeviation` calcula `Value − Setpoint`. Hoje esse
número é **sempre exatamente zero**, por construção. A partir desta mudança ele
passa a ser real.

Quanto ele vale depende de a correção do CN1 (§2.6) estar valendo naquele módulo:

| Situação | Desvio típico |
|---|---|
| Sem correção, como a bancada mediu | até **−3,4 %** a 100 rpm |
| Com a correção do firmware `9.1.0-dev` | **±0,5 rpm** em toda a faixa |
| Módulo 2 antes da varredura de verificação | desconhecido, entre os dois |

Nos três casos o número é honesto: é o eixo contra o pedido. Mas a interface
precisa dizer isso, porque um desvio onde antes havia zero exato parece defeito
novo mesmo quando é pequeno.

Ação: uma nota no painel de detalhe da variável de agitação dizendo que o desvio é
medido no eixo e inclui a cadeia inteira do comando analógico do CN1. **Um desvio
grande e sistemático é sinal de que aquele módulo precisa da própria varredura**,
não de que a telemetria está errada.

### 2.5. O que **não** mudar agora

- **`OurSoftSensorService.cs:65`** usa `settings.Current.Setpoints.MotorRpm` como
  agitação comandada no cálculo de OUR. Trocar por medida altera resultados
  científicos já produzidos por este app. Fica como decisão separada, depois do
  Gate F, com comparação lado a lado.
- **`KlaPlaybackSample.SourceMotorRpm`** e o mapeamento de kLa: mesma razão.
- **A coluna 2 do TSV legado.** Nunca.

### 2.6. A correção do CN1 — decidida e implementada no hub

> **Resolvido em 2026-09-02.** Não há PI. A correção é um **feedforward no hub**:
> a inversa da calibração afim do CN1, aplicada ao `motorSetpoint` no ponto de
> emissão da UART2. Firmware `9.1.0-dev`, em `Config.h`
> (`motorCommandForReference`) e `src/core/Runtime.h`. O restante desta seção é o
> raciocínio que levou até lá, preservado porque explica por que **não** há laço
> fechado — e o que fazer se um dia o feedforward não bastar.

Com a medida disponível, o desvio de ganho e offset do CN1 deixa de ser algo que só
se documenta e passa a ser algo que se corrige. A primeira pergunta não era como
sintonizar: era **em qual das duas placas isso roda**.

**O hub já tem os dois sinais.** Ele recebe `ServoRpm` pelo `/servoData`, a 1 Hz,
empurrado pelo nó; e é ele quem comanda a rotação pela UART2. O PC não está nesse
caminho — ele só lê `/readData` e posta `/command`.

```text
laco no hub    no --1 Hz--> HUB --UART2--> modulo --CN1--> drive
laco no app    no --1 Hz--> hub --HTTP dataDelay--> PC --HTTP--> hub --UART2--> ...
```

**A recomendação é o hub**, e a razão decisiva é uma só:

> **Um laço no app abre quando o PC sai.** Hoje, fechar o notebook não para nada: o
> hub segura os setpoints na NVS e continua comandando. Com o PI no PC, uma
> desconexão congela o `motorSetpoint` no último valor que o PI calculou — que **não
> é o número que o operador pediu**, e sim a referência mais a correção. O reator
> fica comandado num valor que ninguém escolheu, por dias, e o número no painel do
> módulo não corresponde a nada. É a regra clássica: o laço regulatório mora junto do
> atuador; o supervisório manda referência, não posição de atuador.

Três consequências que reforçam:

- **O dobro da taxa, sem jitter.** O hub vê a medida a 1 Hz; o PC vê a `dataDelay`,
  2000 ms por padrão, mais o round-trip HTTP e o despacho do WPF.
- **O problema da NVS desaparece.** A banda morta da §2.8 só existe porque o PI
  escreveria pelo `/command`. No hub a saída vai direto para a UART2, e a NVS só
  precisa persistir a **referência**, que muda raramente.
- **Não é categoria nova para o hub.** Ele já roda controle em malha fechada: o
  intertravamento de espuma aciona bomba e agitador a partir do sensor de distância.

E há um ganho de contrato: com o laço no hub, **`motorSetpoint` passa a significar o
que o nome diz** — uma referência de rotação, não uma posição de atuador. O app
continua mandando exatamente a mesma chave, na mesma faixa; um app antigo contra um
hub novo simplesmente passa a acertar a rotação. Compatível na direção boa. E a
coluna 2 do TSV continua sendo a referência sem nenhum esforço extra.

**A ressalva, que é de sequência e não de arquitetura.** O hub v9 acabou de ser
validado na bancada, e a superfície de sintonia — gráficos, termos ao vivo, journal,
testes contra planta simulada — está toda no app. Identificar `Kp` e `Ki` no ESP32 é
um ciclo de gravação por tentativa; no PC é um rebuild.

Então:

1. **prototipar no app** para identificar os ganhos e decidir se feedforward basta;
2. **mudar o laço para o hub** com os números já conhecidos.

A planta não se importa onde o controlador roda — a identificação transfere
inteira. O que o app perde nessa mudança é só o laço; a referência, os gráficos e o
registro continuam sendo dele.

### 2.6.0. O que foi feito: feedforward, não laço

**Não era preciso um laço.** O Gate A levantou nove pontos de 100 a 1000 rpm com
resíduo máximo de 1,49 rpm, e o ajuste é afim:

```text
lido = 1,00617 x comandado - 4,24
```

Afim é invertível exatamente. O hub aplica a inversa antes de emitir o `"<N>A"`:

```text
comandado = (referencia - INTERCEPT) / SLOPE
```

Resultado calculado sobre os próprios pontos da bancada — erro residual de
**±0,5 rpm** em toda a faixa, contra até −2,15 rpm antes:

| Referência | Comando emitido | rpm esperado | Erro |
|---:|---:|---:|---:|
| 50 | 54 | 50,1 | +0,09 |
| 100 | 104 | 100,4 | +0,40 |
| 300 | 302 | 299,6 | −0,38 |
| 600 | 601 | 600,5 | +0,47 |
| 1000 | 998 | 999,9 | −0,08 |

O que sobra é o resíduo do próprio ajuste (1,49 rpm), que é o piso: nenhum
controlador melhora isso, porque é dispersão da medida, não erro sistemático.

**Por que feedforward ganha do PI aqui**, e não é só simplicidade:

- **não tem integrador**, logo não tem o que oscilar, não tem windup, não tem
  sintonia e não tem transferência sem solavanco a projetar;
- **não depende de medida chegando.** Continua correto com o nó do servo
  desligado, com `servoComm` em 0, e no Módulo 1, que não tem servo nenhum. Um
  laço precisaria de `ServoRpm` a cada passo;
- **não tem a armadilha do zero em regime.** A única guarda é a de referência
  zero, que é explícita e local.

A saturação do comando em 1000 dá rotação máxima de 1001,9 rpm, então toda a
faixa de 0 a 1000 é atingível sem ceifar.

**A ressalva que fica.** Os coeficientes foram medidos no **Módulo 1**, e a placa
do CN1 é de código fechado e específica de cada módulo. O Módulo 2 herda os mesmos
valores como ponto de partida e precisa da própria varredura — que agora é barata,
porque é justamente nele que está o nó publicando `ServoRpm` a 1 Hz. Desativar a
correção num módulo é pôr `SLOPE 1.0` e `INTERCEPT 0.0`: a inversa vira identidade
e nada mais muda.

### 2.6.1. Se um dia o feedforward não bastar — o PI que não foi feito

O que segue **não está implementado**. Fica registrado porque é a próxima peça se a
correção estática se mostrar insuficiente: deriva com temperatura, envelhecimento
da placa do CN1, ou um módulo cuja não linearidade não seja afim.

Se acontecer, a estrutura é **feedforward mais trim integral lento**, não um PI
substituindo o feedforward: o feedforward continua carregando o grosso e
sobrevivendo à queda do PC, e o integral só limpa o resíduo.

### 2.6.1. A matemática, onde quer que ela rode

**A estrutura, e por que não é o PID do oxigênio.** O app já tem
`VelocityPidController`, com predição de tempo morto e estimador de taxa por
mínimos quadrados. Essa complexidade existe para a sonda de OD, que tem 20 a 40 s
de atraso e ruído de quantização. **O RPM não tem nada disso**: a medida chega a
1 Hz, limpa, e o próprio drive já fecha a malha de velocidade internamente — na
bancada, 200 rpm comandados deram 197,5 no quadro seguinte.

Então um controlador novo e pequeno, `AgitationPiController`, seguindo as mesmas
convenções estruturais do que já existe — forma de velocidade, saturação da saída
como integrador, sem estado integral separado que possa estourar — mas **sem**
horizonte de predição e **sem** estimador de taxa. Importar os dois num laço
rápido e limpo acrescentaria superfície de sintonia sem benefício.

A lei é a mesma nas duas placas. No app é esta classe em C#, testada contra planta
simulada como `CascadeControlTests` já faz; no hub são as mesmas quatro linhas em
C++ dentro do laço que já roda a 1 Hz. **É de propósito que a matemática caiba nas
duas** — é o que torna a sequência "prototipar no app, mudar para o hub" barata.

```text
e[k]  = referencia - ServoRpm[k]
du[k] = Kp*(e[k] - e[k-1]) + Ki*e[k]*dt
u[k]  = clamp(u[k-1] + du[k], 50, 1000)
```

**A sintonia começa quase toda no integral.** Os dados do Gate A dizem que o erro
é dominado por offset, não por ganho:

| Comandado | Medido | Erro | Erro relativo |
|---:|---:|---:|---:|
| 100 | 96,6 | −3,4 | −3,4 % |
| 200 | 198,5 | −1,5 | −0,8 % |
| 600 | 598,7 | −1,3 | −0,2 % |

Um erro que encolhe em termos absolutos e desaba em termos relativos é offset com
um pouco de ganho — exatamente o que um integrador zera. `Kp` pode começar em zero
ou perto disso; quem faz o trabalho é `Ki`.

> **Alternativa que os dados já pagam.** O Gate A levantou nove pontos de 100 a
> 1000 rpm, com resíduo abaixo de 1,5 rpm. Isso é uma curva estática invertível: um
> **feedforward** que aplique a inversa do mapa comando→rpm remove o grosso do erro
> já no primeiro quadro, e o integral fica só com o resíduo e a deriva. Não é
> obrigatório e não muda o desenho do laço — o feedforward entra como termo somado
> à saída do PI. Fica registrado porque o custo é reaproveitar uma medição que já
> existe.

### 2.7. O laço interno de uma cascata que já existe

Este é o ponto de arquitetura, e ignorá-lo cria dois controladores brigando pelo
mesmo atuador.

O app **já** manipula a rotação: `CascadeService` comanda agitação como variável
manipulada para controlar oxigênio dissolvido. Com o PI de RPM a estrutura passa a
ser uma cascata de dois níveis — que é a forma correta, não um acidente:

```text
O2 medido ──▶ [ cascata de O2 ] ──▶ referencia de rpm
                                          │
                            ServoRpm ──▶ [ PI de RPM ] ──▶ motorSetpoint ──▶ CN1
```

O laço interno fecha em um a dois quadros e o externo trabalha em dezenas de
segundos, que é a separação de escalas que uma cascata exige. E o ganho é real:
hoje a cascata de O₂ atua através de um atuador com ~3 % de erro estático e
compensa integrando; com o laço interno fechado, o atuador dela vira linear.

Três regras de acoplamento:

1. **A referência do PI é quem estiver comandando a agitação** — o setpoint
   aplicado pelo operador, ou `LastActuation.AgitationRpm` quando a cascata está
   engatada. Nunca os dois ao mesmo tempo.
2. **A cascata recebe a referência, nunca a saída do PI.**
   `CascadeService.Engage(currentAgitationRpm, …)` usa a agitação atual para
   inicializar a alocação. Com o PI no meio, `motorSetpoint` deixa de ser "rpm" e
   vira comando de atuador; entregá-lo à cascata faria a alocação dela partir de um
   número em outra unidade.
3. **Um laço de cada vez sobre o mesmo atuador.** Com a cascata engatada o operador
   não digita rpm — como já não digita hoje.

**Com o laço no hub, a regra 2 deixa de ser uma armadilha e vira uma propriedade.**
O que o app manda passa a ser referência de rotação em rpm, que é exatamente a
unidade em que a cascata já pensa: `LastActuation.AgitationRpm` vai para o fio sem
tradução, e `Engage(currentAgitationRpm, …)` recebe um número na unidade certa por
construção. A cascata nem precisa saber que existe um laço interno — que é o
argumento de sempre a favor de cascata distribuída: o supervisório manda referência
e não precisa modelar o atuador.

### 2.8. As três coisas que podem dar errado

**`motorSetpoint = 0` desabilita o motor.** É o risco mais concreto: uma saída de
PI que passeie até zero manda `0V`, desabilita o motor no Módulo TECNAL e **deixa o
teclado latchado**. A saturação inferior do PI é **50 rpm**, a mesma do
`SubsystemSpec` da agitação, e o zero fica reservado para a ação explícita de
desligar. **O PI nunca emite zero.**

**Medida ausente não é medida zero.** Se `ServoOnline` cair, ou o roteamento for
desligado, o erro deixa de existir — não vira erro grande. O PI **congela a saída e
para de integrar**, anuncia, e após algumas amostras sem medida se desengata
sozinho, deixando o último comando no lugar. Integrar sobre medida ausente é como o
laço iria a fundo de escala.

**`dt` é medido, não assumido.** O período do quadro é o `dataDelay` do hub, 2000 ms
por padrão — e `{"resetVariables":1}` o muda para 1000 ms sem avisar ninguém. O PI
usa o intervalo real entre quadros, saturado como o `MaxStepSeconds` do controlador
de oxigênio já faz.

E uma quarta, **que só existe se o laço ficar no app**: o hub só grava a NVS depois
de um período sem comandos, e um PI escrevendo a cada quadro adia a persistência
para sempre. A correção é uma **banda morta no envio** — só mandar `motorSetpoint`
novo quando o valor inteiro mudar. Em regime o PI para de escrever sozinho e o hub
ganha sua janela de silêncio; o integrador continua trabalhando em unidades
contínuas, e a banda morta é só sobre o que vai ao fio.

Com o laço no hub esta preocupação desaparece inteira, porque a saída não passa pelo
`/command`: ela vai direto para a UART2, e a NVS só persiste a referência. É mais um
ponto na coluna do hub.

> **Divergência a conferir no firmware antes de dimensionar a banda morta:** o
> `PROTOCOLO_HUB_v9_PARA_APLICATIVO.md` §5 diz que a gravação ocorre após **5 s** sem
> comandos; o `PLANO_TESTES_DOIS_ESP32S3.md` §2.2 diz **15 s** (`SAVE_DEBOUNCE_MS`).
> Um dos dois está errado.

### 2.9. O que a correção faz com o log — nada, e é essa a graça

Com a correção no hub, **o app não vê nada mudar**. Ele manda `motorSetpoint` como
sempre mandou, na mesma faixa, com a mesma chave; a inversa é aplicada do outro
lado, no ponto de emissão da UART2, e o número corrigido nunca sai do hub.

| Grandeza | Onde vive |
|---|---|
| Referência (operador ou cascata) | `Motor.Setpoint` → **coluna 2 do TSV**, como sempre |
| Comando corrigido | interno ao hub; ecoado na serial dele para conferência |
| Medida no eixo | sidecar, coluna `rpm`, e o canal `ServoRpm` |

A coluna 2 continua significando **"o que foi pedido"** — e agora, pela primeira
vez, é também o que o eixo entrega. Nenhuma coluna nova é necessária.

Fosse o laço no app, seria diferente: a saída do PI teria de ser gravada à parte,
porque referência e comando deixariam de coincidir. É mais um custo que a decisão
pelo hub evitou.

O que muda no app é só o diagnóstico: `HubFirmwareVersion` passa a `9.1.0-dev`, e
esse é o marcador que distingue, num log antigo, uma sessão comandada com correção
de uma sem. Vale registrá-lo nos metadados do sidecar (§5.2).

---

## 3. Camada de protocolo — Gate D

### 3.1. `src/OpenTECHub.Protocol/CommandKeys.cs`

Em `TelemetryKeys`, as quatorze chaves da §1.1, na grafia exata do fio. Em
`CommandKeys`, três comandos:

```csharp
public const string ServoComm = "servoComm";
public const string ResetServoEnergy = "resetServoEnergy";
public const string ServoPollMs = "servoPollMs";
```

A convenção do arquivo (`docs/CONVENTIONS.md`, "Wire-format constants") é nomear a
constante pela chave JSON, verrugas incluídas, para que um `grep` pela chave vista
no fio encontre o código que a emite. Vale aqui sem exceção.

### 3.2. `src/OpenTECHub.Protocol/CommandBuilders.cs`

Três builders tipados:

| Builder | Regra |
|---|---|
| `ServoRouting(bool on)` | emite `servoComm` 0 ou 1 |
| `ResetServoEnergy()` | emite `resetServoEnergy` 1 — só o valor 1 conta |
| `ServoPollInterval(int ms)` | **valida 250..10000 antes de serializar** |

A validação do `servoPollMs` no app é redundante com a do Hub de propósito: o Hub
recusa com `[ESP32_AVISO]` na serial dele, que o operador não está lendo. Um valor
fora de faixa tem de virar erro de validação na tela, não silêncio.

`CultureInfo.InvariantCulture` em toda conversão numérica, como sempre.

### 3.3. `src/OpenTECHub.Protocol/SensorReadings.cs`

Nos **dois** tipos — a classe mutável `SensorReadings` e o record imutável
`SensorSnapshot` — e em `Snapshot()`, que copia campo a campo:

```csharp
public bool HasServoTelemetry { get; set; }     // "o Hub já falou do servo"
public bool ServoOnline { get; set; }
public bool? ServoCommEnabled { get; set; }     // null = Hub antigo, não "desligado"
public bool? ServoCommandPending { get; set; }
public int ServoCommandQueueDepth { get; set; } = -1;

public double ServoRpm { get; set; } = NotReceived;
public double ServoTorquePct { get; set; } = NotReceived;
public double ServoTorqueNm { get; set; } = NotReceived;
public double ServoLoadPct { get; set; } = NotReceived;
public double ServoPowerW { get; set; } = NotReceived;
public double ServoEnergyWh { get; set; } = NotReceived;
public int ServoState { get; set; } = -1;
public int ServoAlarm { get; set; } = -1;
public long ServoCommOk { get; set; } = -1;
public long ServoCommErr { get; set; } = -1;
public DateTimeOffset? ServoLastSeenAt { get; set; }
```

Dois pontos de cuidado:

- **`NotReceived` é `-1.0`, e `ServoTorquePct` pode ser legitimamente negativo.**
  O torque de frenagem na desaceleração foi observado negativo na bancada. Um
  torque de exatamente −1,0 % é indistinguível do sentinela. Na exibição o risco é
  desprezível, mas **o parser não pode usar `>= 0` como teste de validade** para as
  grandezas com sinal. Use `HasServoTelemetry && ServoOnline` como guarda, que é o
  teste correto de qualquer forma.
- **Contadores em `long`, não `int`.** São `uint32` no fio; `ServoCommOk` cresce 3
  por amostra e estoura `int` em cerca de um ano e meio de operação contínua a 1 Hz.

### 3.4. `src/OpenTECHub.Protocol/TelemetryParser.cs`

Em `ParserConfig`, mais um timeout, seguindo o padrão dos outros (ligeiramente
maior que a janela do Hub, para dar folga de um quadro):

```csharp
/// <summary>Hub window is 6 s; this is the fallback for Hubs that omit ServoOnline.</summary>
public TimeSpan ServoTimeout { get; init; } = TimeSpan.FromSeconds(8);
```

Um `ParseServo(root, now)` novo, chamado na sequência dos demais (junto de
`ParsePump`/`ParseAgitator`, por volta de `:205`), com **exatamente a mesma forma
do `ParsePump`** — que é o modelo mais próximo, porque a bomba também tem
presença, roteamento e comando sem ACK:

```csharp
var sawValues = TryGetPropertyCaseInsensitive(root, TelemetryKeys.ServoRpm, out _) ||
                TryGetPropertyCaseInsensitive(root, TelemetryKeys.ServoPowerW, out _);

var presence = ResolvePresence(
    root, TelemetryKeys.ServoOnline, sawValues, _config.ServoTimeout,
    new Presence(Readings.HasServoTelemetry, Readings.ServoOnline, Readings.ServoLastSeenAt),
    now);
```

Depois, na ordem:

1. `ServoCommEnabled` e `ServoCommandQueueDepth` — lidos **sempre**, porque o Hub
   os publica sempre. São o que distingue "sem servo" de "servo sumido".
2. `ServoCommandPending` — não pegajoso, `null` quando a chave falta, como
   `PumpCommandPending`. Silêncio não é confirmação.
3. Se as chaves de valor **não estão neste quadro**: invalidar as **dez** grandezas
   para `NotReceived` / `-1` e retornar. Não segurar a última amostra.

   > O critério é a ausência das chaves, **não** `HasTelemetry && !Online`. O hub
   > publica os dez exatamente quando são publicáveis, então um quadro sem eles é o
   > hub dizendo que não há o que publicar. O critério por presença perderia o caso 2
   > da tabela abaixo — roteamento desligado com o nó presente —, em que nada
   > envelhece e a última amostra ficaria congelada parecendo atual.
   >
   > Presença e valores decaem em relógios diferentes, de propósito: a presença ganha
   > a janela de tolerância, porque um quadro sem valores não prova que o nó morreu;
   > os valores não ganham.
4. Só então ler as dez, cada uma sob `TryGetDouble`/`AssignInt`, rejeitando NaN e
   infinito.

**A regra que o parser existe para expressar**, e que a interface tem de
reproduzir sem colapsar:

| `ServoOnline` | `ServoCommEnabled` | Leitura | É falha? |
|---|---|---|---|
| `true` | `true` | operação normal | não |
| `true` | `false` | nó presente, roteamento desligado | **não** |
| `false` | `true` | nó ausente com roteamento ligado | **sim** |
| `false` | `false` | este módulo não tem servo | não |
| chave ausente | — | Hub v7/v8, sem evidência | **não** |

O quarto caso é o estado permanente e correto do ModuloTECNAL_1, configurado com
`{"servoComm":0}` em 2026-09-02. Se o app alarmar nele, gera um evento que nunca
se resolve. A quinta linha é o Hub antigo: `HasServoTelemetry == false` significa
"nada foi afirmado", que renderiza *aguardando telemetria*, nunca *offline*.

---

## 4. Apresentação — Gate E

### 4.1. `src/OpenTECHub/ViewModels/ServoDriveViewModel.cs` (novo)

Segue a forma do `PumpControlViewModel`, que já resolve os mesmos problemas.

**Estado**

- um `ExternalDeviceStatus("Servo drive", "do servo drive")`, que é o vocabulário
  compartilhado de *solicitado · roteado · presente*. Não reinventar;
- leituras formatadas: rpm (1 casa), torque % (1), torque N·m (4), carga % (0),
  potência W (2), energia Wh (4);
- `ServoState` → texto: `Desligado` · `Pronto` · `Energizado` · `Alarme`;
- `ServoAlarm` → **em hexadecimal**, formatado como o painel: `AL011` para
  `0x0011`. A decodificação hex→painel é literal, não conversão decimal. Sem
  tabela de causas nesta fase: mostrar o código e apontar o manual;
- **taxa** de erro Modbus, dos deltas de `ServoCommOk`/`ServoCommErr` numa janela
  móvel, com o total bruto disponível só no detalhe.

**Comandos** — três, e **nenhum toca o motor**:

| Comando | Builder | Confirmação observável |
|---|---|---|
| Habilitar/desabilitar roteamento | `ServoRouting` | `ServoCommEnabled` muda no quadro seguinte |
| Zerar energia | `ResetServoEnergy` | `ServoEnergyWh` cai a ~0 |
| Período de amostragem | `ServoPollInterval` | a taxa de crescimento de `ServoCommOk` muda |

**Não existe comando de velocidade, de enable do drive, nem escrita Modbus neste
view-model.** A velocidade continua exclusivamente pelo CN1, comandada por
`motorSetpoint`. Um botão de rotação aqui seria um segundo caminho de comando para
a mesma grandeza, por um enlace sem ACK.

**Este enlace não tem ACK.** `ExternalDeviceStatus.AckFallbackWindow` são 5 s, o
que cabe para os nós que pollam a cada 2 s. Mas com a fila cheia, o servo leva até
**16 s** (8 comandos × 2 s) para drenar. Duas consequências:

- acompanhar `ServoCommandPending` e `ServoCommandQueueDepth`, que são a
  observação real do ciclo de vida do comando, em vez de confiar só na janela;
- **não concluir falha de comando antes de 16 s.**

Registrar em `App.xaml.cs`, injetar em `ControlViewModel` e descartar junto dos
demais assinantes de telemetria.

### 4.2. Card no `ControlView.xaml`

Um painel "Servo drive — Potência" na área de dispositivos externos, na mesma
forma dos quatro que já existem (fluxômetro, biomassa, bomba externa, agitador):
`StateDot` + `ExternalDeviceStateConverter` no cabeçalho, `ExternalDeviceChips`
para os estados, `ConnectedExternalDeviceEntryStyle` nos campos.

Conteúdo, em duas linhas de leitura:

```text
rpm medido        torque %      torque N.m     carga %
potencia W        energia Wh    estado         alarme
```

Regras de renderização:

- **traço, não zero**, para chave ausente;
- potência e energia rotuladas **"mecânica estimada"** — no rótulo, não só no
  tooltip;
- `ALARM` destacado, com o código em formato de painel;
- controles desabilitados com o Hub desconectado;
- o botão de zerar energia **separado visualmente** de qualquer ação de parada: é
  um acumulador de dados, não uma ação de processo.

> As strings pt-BR do card seguem o padrão vigente do código (inline, como em
> `ChartsViewModel.cs`). O `CONVENTIONS.md` pede `.resx`, mas nenhum existe ainda
> no repositório; abrir um só para este card divergiria do resto. Fica como item
> de dívida, não deste plano.

### 4.3. Sinótico

A variável `Motor` passa a ser medida — §2. Nada mais muda no sinótico nesta fase:
o servo é um dispositivo externo, e o card dele é o lugar certo. Uma nova ladrilho
de potência no sinótico pode vir depois do Gate F, se o operador pedir.

### 4.4. Gráficos

Em `ChartsViewModel.cs:80`, seis séries novas, com as unidades corretas:

```csharp
new(TelemetryChannel.ServoRpm,       "Servo — rotação medida",      "rpm",  "Series6Brush"),
new(TelemetryChannel.ServoTorquePct, "Servo — torque",              "%",    "Series1Brush"),
new(TelemetryChannel.ServoTorqueNm,  "Servo — torque",              "N·m",  "Series2Brush"),
new(TelemetryChannel.ServoLoadPct,   "Servo — carga média",         "%",    "Series3Brush"),
new(TelemetryChannel.ServoPowerW,    "Servo — potência mecânica estimada", "W",  "Series4Brush"),
new(TelemetryChannel.ServoEnergyWh,  "Servo — energia mecânica acumulada", "Wh", "Series5Brush"),
```

**Nenhuma delas entra nos quatro slots padrão.** Os padrões atuais
(Temperatura/Oxigênio/pH/Vazão) ficam como estão; o operador seleciona as novas
explicitamente. Seis séries a mais por padrão transformariam a tela de gráficos.

---

## 5. Histórico e persistência

### 5.1. `TelemetryHistory.cs`

Seis canais novos no enum `TelemetryChannel` (`:13`), **depois** dos existentes
para não deslocar valores já gravados em qualquer lugar que os tenha persistido
por índice.

Em `Add` (`:114`), gravar `double.NaN` — não zero — quando o servo está ausente ou
offline. É o que produz lacuna no gráfico em vez de uma linha caindo a zero, e é o
mesmo tratamento que `MotorRpm` já recebe (`:145`).

**Assinatura.** `Add(SensorSnapshot snapshot, double commandedRpm)` continua
igual: `commandedRpm` segue sendo o comando (§2.3, item 1), e a medida chega
dentro do `snapshot`. Nenhuma mudança de interface é necessária — o que é o sinal
de que a separação está certa.

### 5.2. O sidecar

`SessionLogger.cs` declara o TSV legado como contrato congelado. **Não inserir
colunas Servo nele.** Um arquivo paralelo por sessão, `servo-power.tsv`,
registrado no manifesto tratado por `SessionFiles.cs`:

```text
time_min  servo_online  rpm  torque_pct  torque_nm  load_pct  power_w  energy_wh
          state  alarm  comm_ok  comm_err
```

Sem colunas de comando: com a correção no hub (§2.9), a referência já está na
coluna 2 do TSV principal e o comando corrigido nunca chega ao app.

Regras:

- ponto decimal, `InvariantCulture` — é dado para ferramenta, não texto para
  pessoa;
- `time_min` alinhado ao mesmo `TimeMinutes` do quadro principal, para que os dois
  arquivos casem linha a linha;
- **vazio para leitura ausente**, nunca zero;
- cabeçalho de metadados com versão do contrato, `HubFirmwareVersion`,
  `HubProtocolVersion` e o `MOTOR_RATED_TORQUE_NM` assumido — sem ele, os N·m e W
  do arquivo não são reinterpretáveis depois;
- reinício da energia acumulada gera evento no `EventJournal`.

`SessionFileService.Discover`/`Load` precisam abrir sessões **sem** o sidecar, que
são todas as anteriores a esta versão. A ausência do arquivo é normal, não erro.

---

## 6. Alarmes e eventos

Em `AlarmModels.cs`, dois `AlarmId` novos, seguindo os quatro `*Offline` que já
existem:

| `AlarmId` | Severidade | Condição |
|---|---|---|
| `ServoDriveOffline` | Warning | `!ServoOnline && ServoCommEnabled == true`, com `OnDelay` ≥ 10 s |
| `ServoDriveAlarm` | Critical | `ServoState == 3` ou `ServoAlarm != 0` |

E eventos no `EventJournal`, sem alarme: nó entrou/saiu de linha, reset de energia
solicitado, reset de energia observado.

O `DeviceRoutingMismatch` que já existe passa a valer também para o servo, quando
o switch do operador discorda do `ServoCommEnabled` do Hub.

**Quatro condições que não podem gerar alarme:**

1. `ServoOnline:false` + `ServoCommEnabled:false` — é o ModuloTECNAL_1, que não
   tem servo por decisão. Alarmar aqui produz um evento que nunca se resolve;
2. `HasServoTelemetry == false` — Hub v7/v8 nada afirmou;
3. `ServoCommErr` absoluto acima de zero — o alarme é sobre a **taxa**, com janela
   e histerese, e o limiar só será decidido depois do soak de 2 h (Etapa 5 do
   plano de testes). Até lá, o campo aparece na interface mas não alarma;
4. `ServoOnline:true` + `ServoCommEnabled:false` — roteamento desligado com nó
   presente é uma escolha, não uma falha. Merece chip, não alarme.

---

## 7. Simulador e testes

### 7.1. Simulador

`WireCodec.cs` (`AppendExternalDevices`) e `DeviceModel.cs` precisam produzir
**sete** cenários, que são exatamente os que a bancada exercitou:

1. Hub antigo: nenhuma chave `Servo*`;
2. nó ausente: `ServoOnline:false`, `ServoCommEnabled:true`, sem as dez;
3. nó online parado: rpm 0, potência 0, `ServoState:1` — **os zeros são medidas**;
4. nó online girando: rpm, torque e potência coerentes entre si pela §1.2;
5. roteamento desligado com nó presente: `ServoOnline:true`,
   `ServoCommEnabled:false`, sem as dez;
6. alarme: `ServoState:3`, `ServoAlarm:0x0011`;
7. energia zerada por comando, e energia andando para trás após reboot do nó.

O cenário 5 é o que o v8 não conseguia expressar e o que motivou metade do
contrato v9. O 3 e o 7 são os que quebram implementações ingênuas.

`HttpEndpoint.cs` deve aceitar os três comandos novos, recusar `servoPollMs` fora
de 250..10000 e ter uma fila de 8 que rejeite o nono — para que o app possa ser
exercitado contra a contrapressão sem o hardware.

### 7.2. Testes

| Arquivo | O que cobrir |
|---|---|
| `TelemetryParserTests.cs` | as cinco linhas da tabela da §3.4; zero legítimo; chave ausente ≠ zero; invalidação ao expirar; NaN e infinito recusados; cultura pt-BR |
| `WireFormatTests.cs` | grafia exata das três chaves de comando; faixa do `servoPollMs`; `InvariantCulture` |
| `ServoDriveViewModelTests.cs` (novo) | os quatro estados; taxa de erro por delta; confirmação observada do reset; Hub desconectado; alarme |
| `TelemetryTests.cs` | as seis séries; NaN produzindo lacuna; `MotorRpm` **continua sendo o comando** |
| `SessionLoggerTests` / `SessionFiles` | sidecar escrito e relido; sessão antiga sem o arquivo abre normalmente; TSV legado byte-idêntico |
| `AlarmServiceTests.cs` | os dois alarmes novos; e as quatro condições da §6 que **não** devem alarmar |

**O teste que protege o item de maior risco:** um caso que empurra `ServoRpm`
diferente do `motorSetpoint` e afirma que a coluna 2 do TSV e o canal `MotorRpm`
continuam com o **comando**. Sem ele, a regressão da §2.2 passa despercebida.

Compatibilidade obrigatória, e testada: o app novo continua funcionando contra Hub
v7/v8, e sessões antigas continuam abrindo sem `servo-power.tsv`.

---

## 8. Ordem de implementação

Cada etapa é compilável e testável sozinha. A ordem não é arbitrária: a etapa 1
tem de vir antes da 4, ou a regressão do log entra sem ser vista.

- [x] **1. Desacoplar o log da tela** — `commandedRpm` passa a vir de
      `Motor.Setpoint`. Um teste que fixa o comportamento atual, antes de tudo
      _(feito em 2026-09-02: `ShellViewModel.cs:1224` e
      `tests/OpenTECHub.Tests/CommandedRpmProvenanceTests.cs`, 23 testes verdes)_
- [x] **2. Protocolo** — `CommandKeys`, `CommandBuilders`, `SensorReadings`,
      `SensorSnapshot`, `Snapshot()`
      _(2026-09-02: 14 chaves de telemetria + 3 de comando; `ServoRouting`,
      `ResetServoEnergy` e `ServoPollInterval` com a faixa 250..10000 **recusada**
      e não saturada; contadores em `long`; testes em `WireFormatTests.cs`.
      `motorSetpoint` reanotado como referência, com o zero documentado como
      desabilitação e não parada)_
- [x] **3. Parser** — `ServoTimeout`, `ParseServo`, as cinco linhas da tabela
      _(2026-09-02: `ServoTimeout` 8 s, `ParseServo` no molde do `ParsePump`,
      `TryGetFiniteDouble` e `TryGetCounter` novos; 27 testes em
      `ServoTelemetryParserTests.cs`)_

> **Ajuste ao desenho, decidido na implementação.** A invalidação não é
> `HasTelemetry && !Online`, como o §3.4 dizia, e sim **a ausência das chaves de
> valor no quadro**. O critério antigo perdia o caso 2 da tabela — roteamento
> desligado com o nó presente —, em que a presença continua `true` e só os valores
> somem: a última amostra ficaria congelada na tela parecendo atual, que é
> exatamente o defeito que esta seção existe para evitar. Presença e valores passam
> a decair em relógios diferentes, e de propósito: a presença ganha a janela, porque
> um quadro sem valores não é prova de que o nó morreu; os valores não ganham, porque
> o hub os publica exatamente quando são publicáveis.
- [x] **4. RPM medida** — `isCommandedOnly: false`, `Motor.Push`, o caso especial
      do zero, canal `ServoRpm`
      _(2026-09-02: mais `HasServoSample` no contrato, a sobrecarga
      `Push(double?)`, e a série "Agitação — medida" nos gráficos)_

> **Duas coisas que a implementação acrescentou.**
>
> **`HasServoSample` no snapshot.** O plano mandava consumir a medida testando
> contra o sentinela (`ServoRpm > NotReceived`). Isso é sutilmente errado aqui:
> `NotReceived` é `-1,0` e o rpm é legitimamente negativo — a bancada capturou
> −0,30 rpm com o motor em repouso. Uma leitura de exatamente −1,0 rpm seria
> descartada como "ausente". O parser passa a publicar um flag dizendo se o quadro
> trouxe os dez valores, e ninguém a jusante precisa mais adivinhar.
>
> **Sobrecarga `Push(double?)`.** Pela mesma razão: a `Push(double)` infere ausência
> do valor, o que está certo para canais que não podem ser negativos e errado para
> este. Quem tem um flag de presença de verdade passa por ela.
>
> A série de gráfico da medida entrou junto, fora do que a etapa 8 previa, porque
> sem ela o ladrilho de agitação apontaria para um canal que a lista de gráficos não
> conhece — arrastá-lo para um painel não faria nada. As outras cinco continuam na
> etapa 8.
- [x] **5. Simulador** — os sete cenários, antes da interface, para que a
      interface seja desenvolvida contra eles
      _(2026-09-02: cenários `legacy-hub` e `servo-alarm` novos; os outros cinco são
      estados de operação alcançados por comando, não injeção de falha. Fila de 8 com
      recusa do nono e drenagem de 1 a cada 2 s; 19 testes em `ServoSimulatorTests.cs`,
      todos por ida e volta pelo parser real)_

> **Gate D fechado.** As etapas 1 a 5 estão feitas. O que falta do Gate D no plano
> original era só isto, e o simulador já produz as sete formas — a interface pode ser
> construída contra ele sem hardware.
- [x] **6. `ServoDriveViewModel`** + registro em `App.xaml.cs`
      _(2026-09-02: leituras formatadas com traço para ausência, estado em palavras,
      alarme em forma de painel (`AL011`), taxa de erro por janela móvel de 30 quadros,
      e os três comandos. Injetado no `ControlViewModel` e descartado com ele;
      32 testes em `ServoDriveViewModelTests.cs`)_
- [x] **7. Card no `ControlView.xaml`**
      _(2026-09-02: card 13 na área de dispositivos externos, com a mesma gramática dos
      outros cinco. Potência rotulada "W mec. est." **na própria linha**, bloco de alarme
      só quando há alarme, e a zeragem de energia isolada abaixo de um filete no fim da
      gaveta. 8 testes em `ServoCardContractTests.cs`)_

> **Correção ao desenho, imposta pelo contrato do workspace.** O §4.2 previa um botão
> "Aplicar" para o intervalo de amostragem. A página inteira é de **aplicação
> automática** — `ControlWorkspaceContractTests` proíbe `Content="Aplicar"` em todo o
> `ControlView.xaml` — e o card teve de seguir a convenção: Enter ou sair do campo
> envia, como em toda entrada da página.
- [x] **8. Histórico e gráficos** — seis canais, NaN nas lacunas, seleção explícita
      _(2026-09-02: as cinco séries restantes, todas com o mesmo portão de presença
      do `ServoRpm`; nenhuma nos quatro painéis padrão. 11 testes em
      `ServoChartChannelTests.cs`)_

> **Ajuste ao §4.4.** O plano dava o mesmo rótulo, "Servo — torque", às duas séries
> de torque, distinguindo-as só pela unidade. O seletor renderiza um canal pelo
> título e mais nada, então seriam duas entradas idênticas na lista — e o sinótico
> resolve um painel salvo pelo título antes de qualquer outra coisa, escolhendo em
> silêncio a primeira. Passaram a ser "Servo — torque (%)" e "Servo — torque (N·m)",
> com um teste que proíbe títulos repetidos em toda a lista.
- [x] **9. Sidecar** + manifesto + leitura de sessões antigas
      _(2026-09-02: `servo-power.tsv` aberto e fechado com o log principal, uma linha
      por quadro para os dois ficarem alinhados, e as seis séries voltando nos gráficos
      ao reabrir a sessão. 15 testes em `ServoSidecarTests.cs`)_

> **Duas coisas que o plano não previa.**
>
> **O preâmbulo é escrito na primeira linha, não na abertura.** `HubFirmwareVersion` e
> `HubProtocolVersion` vêm da telemetria, e na abertura nenhum quadro chegou ainda —
> escrevê-lo ali só poderia dizer "desconhecido", que é exatamente o que um leitor
> futuro precisa saber. Isso exigiu acrescentar as duas chaves ao protocolo, que a
> etapa 2 não tinha incluído.
>
> **O torque nominal do motor não é afirmado no cabeçalho.** É constante de firmware
> que o app nunca recebe, e escrever um palpite seria pior do que não escrever nada. O
> arquivo carrega `torque_pct` e `torque_nm` lado a lado justamente por isso:
> `T_nominal = torque_nm ÷ (torque_pct ÷ 100)` em qualquer linha com torque não nulo.
- [ ] **10. Alarmes e eventos**
- [ ] **11. Suíte completa verde + app iniciado com logs WPF inspecionados**
Gate D fecha na etapa 5. Gate E fecha na etapa 11. **Gate F é bancada** — os
critérios estão em `PLANO_INTEGRACAO_POTENCIA_OPENTECHUB.md` §6.

### No firmware, em paralelo

A correção do CN1 (§2.6) **não passa pelo app** e não depende de nenhuma etapa
acima. Já está feita; falta a verificação física.

- [x] inversa da calibração aplicada no ponto de emissão da UART2
      _(2026-09-02, `Config.h` + `src/core/Runtime.h`, firmware `9.1.0-dev`;
      compilação limpa em 955.008 bytes, 23 fixtures de contrato verdes)_
- [ ] **varredura de verificação no Módulo 2** — comandar 100, 300, 600 e 1000 rpm
      e conferir `ServoRpm` contra o pedido. É o item que fecha a ressalva de que os
      coeficientes foram medidos no Módulo 1
- [ ] se a varredura discordar, refazer os nove pontos **naquele módulo** e trocar
      só `MOTOR_CAL_SLOPE`/`MOTOR_CAL_INTERCEPT` no bloco `#else` do `Config.h`
- [ ] conferir na serial do hub a linha
      `Rotacao: referencia N rpm -> comando M` em cada patamar

---

## 9. Armadilhas verificadas — o que não fazer

Todas foram observadas na bancada em 2026-09-02. Nenhuma é hipotética.

| Não faça | Porque |
|---|---|
| Empurrar `ServoRpm` para `Motor.Value` sem antes trocar a fonte de `commandedRpm` | corrompe a coluna 2 do TSV em silêncio |
| Renderizar zero para chave ausente | `0.0 rpm` é motor parado; ausência é "sem dado" |
| Alarmar em `Online:false` + `CommEnabled:false` | é o ModuloTECNAL_1; o evento nunca se resolve |
| Usar `HubStations` para decidir presença | é `softAPgetStationNum()`; uma estação que some fica na tabela até 5 min |
| Alarmar no total de `ServoCommErr` | 1 erro em 256 leituras, no boot, é normal; o alarme é sobre a taxa |
| Calcular período esperado como `1000/poll_ms` | `poll_ms` é **atraso**, não período; cada amostra custa ~250 ms de barramento |
| Concluir falha de comando antes de 16 s | fila de 8, drenada a 1 por pull de 2 s |
| Tratar `503` como erro de rede | é contrapressão; retentar |
| Reiniciar o Hub automaticamente | `syncAllSensorSettings()` reenvia a NVS e **pode parar o motor** |
| Oferecer `motorSetpoint = 0` como "parar" | é **desabilitar**: manda `0V`, e o teclado do Módulo TECNAL fica latchado até um setpoint > 0 |
| Interpolar o gráfico de energia sobre uma lacuna | a integração é local ao nó e rejeita lacunas; a série tem de mostrar o buraco |
| Assumir que a energia só cresce | zera no reboot do nó e no `resetServoEnergy` |
| Rotular potência sem "mecânica estimada" | vira número de consumo elétrico na cabeça do operador |
| Aplicar a correção do CN1 à referência zero | zero é **desabilitação**; a inversa devolveria 4 e o motor ficaria habilitado |
| Persistir na NVS o comando corrigido | trocar os coeficientes reinterpretaria o que já estava gravado; a NVS guarda a **referência** |
| Levar os coeficientes do Módulo 1 para outro módulo sem varrer | a placa do CN1 é de código fechado e específica de cada módulo |
| Fechar um laço de RPM no app | fechar o notebook abriria o laço e congelaria o atuador num valor que ninguém pediu |
