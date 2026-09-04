# Plano de implementação — Ensaios de potência de impelidor (aba "Potência")

**Data:** 2026-09-03
**Escopo:** uma nova página de Automação, "Potência", que automatiza os ensaios de
consumo de potência de impelidores — potência não-gaseificada `P₀`, potência
gaseificada `P_G`, e as grandezas adimensionais derivadas (número de potência `Np`,
número de aeração `Fl_G`, número de Froude `Fr`, razão `P_G/P₀` e a fronteira de
_flooding_). O ensaio é dirigido por uma tabela de condições, à imagem de
"Determinar kLa".
**Estado:** especificação para revisão. Nada implementado. Este documento é a peça a
discutir **antes** de escrever código.

**Leia antes:**

- `docs/PLANO_SERVO_POTENCIA_APP.md` — a integração da telemetria do servo, **concluída**
  (Gates D e E). É a fonte dos campos `ServoTorqueNm`, `ServoRpm`, `ServoPowerW`. A §1.4
  daquele plano registra `Np` e `P/V` como "o passo seguinte natural depois do Gate F" e
  diz, literalmente, que a curva de vazio `P_vazio(N)` "é um ensaio, não uma linha de
  código". **Este plano é esse ensaio.**
- `docs/PLANO_IMPLEMENTACAO_TESTES_KLA.md` — o molde arquitetural. O runner de kLa
  (`Services/KlaTesting/`) já é uma máquina de estados de varredura que comanda rotação e
  gás, detecta regime estável, revisa e persiste. As §§9.3, 10, 11 e 11.2 daquele plano
  (tabela de condições, protocolo de gases, máquina de estados, estabilização no alívio)
  são reaproveitadas aqui quase inteiras.
- `Potencia TECNAL/PROTOCOLO_HUB_v9_PARA_APLICATIVO.md` — a semântica normativa dos campos
  do fio e as armadilhas de bancada de 2026-09-02.
- `_ESP32S3_firmware/docs/WIRE_CONTRACT_V9.md` — o contrato formal dos endpoints;
  `motorSetpoint` é **referência de rotação** (firmware ≥ 9.1) e o fluxômetro é um
  **controlador** de vazão (`FlowSetpoint` com ACK).

---

## 1. Objetivo

Transformar o par torque-medido + rotação-medida, que já chega ao app, num instrumento de
caracterização de impelidores. Uma página, duas famílias de ensaio que compartilham o
mesmo motor de varredura:

| Família | Varre | Mede | Entrega |
|---|---|---|---|
| **Não-gaseificada** | rotação `N`, sem ar | torque `τ` em regime | curva `P₀(N)` e `Np(Re)` |
| **Gaseificada** | vazão `Q_g` e/ou `N`, sob ar | torque `τ` em regime | `P_G`, `P_G/P₀(Fl_G)` e o _flooding_ |

As duas não são páginas diferentes nem sequer ensaios obrigatoriamente separados: a
**tabela de condições** dirige tudo, e cada linha declara se quer só `P₀`, só `P_G`, ou
ambas — porque o planejamento experimental é do usuário, e alguém pode querer só a curva
não-gaseificada de um impelidor novo, ou só o mapa de _flooding_ de uma geometria já
caracterizada.

O `Np` **absoluto** — comparável à literatura (Rushton ≈ 5, pá inclinada 45° ≈ 1,3…) — é
um objetivo declarado, e ele **exige** duas coisas que este plano trata como de primeira
classe, não como acessório: uma **calibração de torque** e uma **curva de tara**
`P_vazio(N)`. Sem elas, o app entrega comparações **relativas** entre impelidores, que já
são úteis; com elas, entrega número físico.

---

## 2. Decisões congeladas

1. **Dirigido por tabela, gaseificação opcional por condição.** Cada linha da tabela tem um
   modo: `NãoGaseificada`, `Gaseificada` ou `Ambas`. A varredura executa o que a tabela
   pede, na ordem que a tabela define. `P_G` nunca é obrigatória.
2. **`Np` absoluto é meta, e portanto calibração e tara são etapas do ensaio**, não pós-
   processamento opcional (§9). O ensaio pode rodar sem elas, mas marca o resultado como
   _relativo_ e o registra assim no manifesto.
3. **O app comanda o fluxômetro de massa.** A varredura de `Q_g` é automática, reusando o
   protocolo de gases, o intertravamento e a confirmação por ACK do kLa (§11). A
   estabilização no alívio (§13) é oferecida como opção, porque o medidor entrega um pulso
   ao abrir e leva segundos para assentar — exatamente o problema que a §11.2 do plano de
   kLa já resolveu.
4. **A potência do ensaio é recalculada no app**, a partir de `ServoTorquePct`/`ServoTorqueNm`
   e da **rotação medida** `ServoRpm`, aplicando tara e calibração de forma transparente. O
   `ServoPowerW` do nó é exibido como referência, mas **não** é a grandeza científica: ela
   depende de `MOTOR_RATED_TORQUE_NM` (valor de placa, possivelmente errado) e não conhece a
   tara (`PLANO_SERVO_POTENCIA_APP.md` §1.2/1.3).
5. **`N` é sempre a rotação medida**, nunca o setpoint. A bancada mediu 96,6 rpm para 100
   comandados antes da correção do CN1; com o feedforward do firmware 9.1 o desvio caiu para
   ±0,5 rpm, mas o princípio permanece: fórmula nenhuma usa o comando.
6. **`motorSetpoint = 0` é proibido durante a varredura.** Zero **desabilita** o motor e
   trava o teclado do módulo (`PROTOCOLO_HUB_v9` §5). A varredura começa e termina no mínimo
   de hardware (15 rpm), e abortar rampa até lá, nunca até zero.
7. **Espelhar `KlaTesting`.** Novo subsistema `Services/PowerTesting/` com runner, store,
   contratos de arquivo e engine de análise na mesma forma; novo `CommandOwner.PowerAssay`
   no árbitro de comando.
8. **Rotular potência e energia como "mecânica estimada"** em toda a interface, no rótulo e
   não só no _tooltip_.
9. **Captura manual de energia elétrica é um modo opcional**, não um dispositivo. O app segura
   o sistema em regime na condição do teste e o operador lê um wattímetro externo (na rede,
   medindo Módulo + Motor) e digita o valor. Correlaciona a potência mecânica estimada com a
   elétrica da tomada, sem nenhuma fiação nova ao Hub (§4.8, §12.3).
10. **Cada ponto é uma condição estável independente do caminho.** Sempre se espera o regime;
    subir ou descer em `N`/`Q_g` dá o mesmo ponto. Não há varredura de histerese — o _flooding_
    é lido da forma da curva de pontos estáveis (§4.5).
11. **O regime é medido sobre o torque, com piso de ruído da tara.** O limiar de estabilidade é
    `max(absoluto, relativo)`, com o piso absoluto vindo do `σ_τ(N)` medido na tara; a janela de
    média é dimensionada por esse `σ_τ` para um erro-padrão alvo (§12.1). Um ponto que não
    converge é **recapturado no lugar** até `MaxTries` (Q7) e, se persistir, marcado "não
    convergiu" sem derrubar o ensaio.
12. **`Np` é por impelidor, com o `D` de cada estágio.** A geometria é uma **lista** de
    impelidores (conjuntos mistos são o caso normal); o rateio de potência entre estágios é
    hipótese explícita, com o `Np` do conjunto exibido em paralelo (§4.3).
13. **O ensaio é autocontido e recusa rodar durante cultivo/cascata.** Vive na sua pasta em
    `Testes-Potencia/`, independente de sessão de cultivo; iniciar com a cascata engatada é
    bloqueado (§14). Import bidirecional de condições com o Mapeamento kLa é fase 3 (§18).
14. **Parada adaptativa por confiança, em duas portas (§12.1).** O tempo por condição é
    governado pela precisão atingida, não por relógio fixo: Porta 1 espera a média do torque
    parar de derivar; Porta 2 acumula até `IC₉₅ ≤ max(relativo, piso do σ_τ)` — o que for
    atingido primeiro. `n_min = 60`, `t_max = 5 min`, `MaxTries = 3`. Confiança julgada no
    torque, propagada para `Np` na exibição. Replicatas continuam, cada uma até o alvo. `SE`
    ignora autocorrelação por ora (`IC` é limite inferior, documentado).

---

## 3. Evidência da auditoria — o que já existe

### 3.1 A telemetria de potência está pronta

`PLANO_SERVO_POTENCIA_APP.md` fechou os Gates D e E em 2026-09-02. O app já tem, validado e
testado:

- `SensorSnapshot` com `ServoRpm`, `ServoTorquePct`, `ServoTorqueNm`, `ServoLoadPct`,
  `ServoPowerW`, `ServoEnergyWh`, presença e roteamento;
- `ServoDriveViewModel` com leituras formatadas, taxa de erro, e os três comandos
  administrativos;
- seis canais de gráfico e o sidecar `servo-power.tsv` alinhado ao quadro principal;
- o simulador produzindo sete cenários de servo, incluindo "girando" com torque e potência
  coerentes entre si.

Consequência: **este plano não toca o parser nem o protocolo do servo.** Ele consome o
`SensorSnapshot` que já chega.

### 3.2 O gás é comandável, com entrega confiável

O fluxômetro é um controlador, não só um medidor. `FlowControlViewModel` já constrói
`FlowSetpoint` preservando o estado das válvulas, e o enlace tem `FlowCommandId`/
`FlowCommandAck`/`FlowCommandPending` — entrega confiável, ao contrário do servo. A vazão
medida chega em `SensorSnapshot.FlowRate` (L/min). O protocolo de gases intertravado da §10
do plano de kLa é reaproveitável sem modificação.

### 3.3 O molde de varredura existe

`Services/KlaTesting/KlaTestRunner.cs` é uma máquina de estados que já faz, para o kLa,
exatamente o que a potência precisa: comanda rotação e gás, aguarda estabilidade por janela
e contagem de confirmações (`_stabilityWindow`, `StabilityConfirmationCount`), captura,
revisa, aceita/rejeita/repete, e persiste por corrida. O ensaio de potência é **mais
simples** — não há desgaseificação, N₂, nem sonda de OD; há torque assentando.

### 3.4 O que falta criar

Ícone: `IconImpellerGeometry` já existe em `Resources/Icons/Icons.xaml`. Navegação: o grupo
"Automação" hoje é `Receitas → Determinar kLa → Mapeamento kLa` (`ShellViewModel.cs:332-334`);
a "Potência" entra **logo abaixo de Mapeamento kLa**.

---

## 4. A ciência do ensaio

Esta seção existe porque a página é um instrumento científico: quem revisar os números
precisa ver de onde eles saem. Todas as fórmulas assumem `N` em **rotações por segundo**
(`N_rps = rpm/60`) e SI, com as conversões explícitas onde entram.

### 4.1 Potência de eixo

```text
ω     = 2π · N_rps                 [rad/s]           (= rpm · 0,104719755)
P_eixo = τ · ω                     [W]
```

É o mesmo `P = T·ω` que o nó já calcula. `τ` vem do drive como fração do torque nominal:
`τ[N·m] = (torque_pct/100) · T_nom`, com `T_nom` a calibrar (§9.1).

### 4.2 Potência líquida do impelidor — a tara

O torque medido inclui o atrito de mancal, selo mecânico e acoplamento — na bancada,
1,4–2,3 % do nominal a vazio. Esse parasita **não** é potência entregue ao líquido. A
correção é uma curva de vazio, levantada uma vez por montagem:

```text
P_líq(N) = P_medida(N) − P_vazio(N)
```

`P_vazio(N)` é medida com **os impelidores montados, girando no ar** (vaso sem líquido). A
lógica é a da subtração: no ar mede-se o eixo com os impelidores rodando sem carga de fluido;
no líquido mede-se o que o conjunto gasta de fato; a diferença é o que os impelidores
entregam ao líquido. Medir a tara *no próprio líquido* não faz sentido — ali já se estaria
medindo a potência que se quer isolar. É uma curva, não um número: o atrito de selo cresce
com `N`. Fica armazenada no ensaio e reutilizável entre condições da **mesma montagem**
(mesmo eixo, selo, acoplamento **e** conjunto de impelidores) — trocar um impelidor pede nova
tara.

> Sem a tara, `Np` de um impelidor de baixa demanda (pá inclinada em água) pode estar
> dominado pelo atrito. A tara é o que separa "consumo do impelidor" de "consumo do
> conjunto".

**A tara também mede o piso de ruído.** O torque do drive tem ruído basal, e em baixa
rotação esse ruído é grande em relação ao torque líquido pequeno — o problema que a §12.1
precisa resolver. Como a curva de tara já varre `N` com o conjunto girando sem carga, ela
grava, além de `P_vazio(N)`, o **desvio-padrão do torque `σ_τ(N)`** em cada patamar. Esse
`σ_τ(N)` é o piso de ruído real daquela montagem, e alimenta duas coisas: o limiar **absoluto**
de estabilidade (§12.1) e um **portão de SNR** que marca como "abaixo do ruído" todo ponto
cujo torque líquido não supere um múltiplo de `σ_τ(N)` — em vez de publicar um `Np` que é
majoritariamente ruído. É medição, não chute: sai da própria tara.

### 4.3 Número de potência e Reynolds

```text
Np = P_líq / (ρ · N_rps³ · D⁵)             [–]
Re = ρ · N_rps · D² / μ                    [–]
```

`ρ` densidade do líquido [kg/m³], `μ` viscosidade [Pa·s], `D` diâmetro do impelidor [m]. No
regime turbulento (`Re > ~10⁴`) `Np` satura num platô que é a assinatura da geometria — é
esse platô que valida a bancada contra a literatura. Abaixo disso, `Np` cai com `Re`, e a
varredura desenha a transição.

**`Np` é por impelidor, com o diâmetro de cada um.** A bancada testa **configurações** de
impelidores, e alguns têm diâmetros diferentes entre si no mesmo eixo. Então `Np` da
literatura é definido **por impelidor**: a potência total do eixo é rateada entre os `n`
impelidores (`P_líq/n` como primeira aproximação, quando são iguais e bem espaçados), e cada
`Np` usa o **seu** `D` e o **seu** `Re`. Um conjunto misto (p.ex. Rushton embaixo + pá
inclinada em cima) produz um `Np` por estágio, não um único número — e a interface mostra
cada um rotulado. O rateio igual é uma hipótese explícita, registrada no resultado; quando os
impelidores diferem muito, o número honesto é o **`Np` do conjunto** (potência total sobre um
`D` de referência declarado), exibido em paralelo.

### 4.4 Calibração de torque — o que torna `Np` absoluto

O drive não mede N·m; reporta uma fração do nominal, e o N·m é essa fração vezes `T_nom`, um
número de placa. Para `Np` absoluto, `T_nom` (ou, equivalentemente, um par escala/offset
sobre o torque reportado) precisa ser aferido contra um torque **conhecido** (§9.1). Sem
isso, `Np` carrega o erro de placa de `T_nom` como fator multiplicativo — comparações
relativas entre impelidores continuam válidas, valores absolutos não.

### 4.5 Gaseificado — razão de potência, aeração e _flooding_

Sob aeração o impelidor consome menos potência: cavidades de gás atrás das pás reduzem o
arrasto. A queda é caracterizada por:

```text
Fl_G = Q_g / (N_rps · D³)          [–]   número de aeração (Q_g em m³/s = L/min ÷ 60000)
Fr   = N_rps² · D / g              [–]   número de Froude
razão = P_G / P₀                   [–]   a 'mesma' N, com e sem gás
```

`P_G/P₀` cai com `Fl_G` crescente. O **_flooding_** — o impelidor deixando de dispersar o
gás — aparece como o ponto em que a curva `P_G/P₀` × `Fl_G` atinge o mínimo ou muda de
inclinação, com `Fr` como co-variável. A transição afogado→carregado→disperso pode ser
varrida de dois jeitos: `Q_g` crescente a `N` fixo, ou `N` crescente a `Q_g` fixo.

**O `P₀` da razão vem da curva não-gaseificada ajustada.** Cada ponto gaseificado precisa de
um `P₀` à sua própria `N` para formar `P_G/P₀`. Em vez de exigir uma medida não-gaseificada
casada a cada `N`, o app **interpola** `P₀(N)` da curva `Np` não-gaseificada já levantada
(`P₀ = Np · ρ · N_rps³ · D⁵`) — assim qualquer ponto gaseificado tem denominador sem
re-medir. Quando não há curva ajustada disponível (nenhum ensaio não-gaseificado daquela
montagem), a razão só é calculada se existir um ponto não-gaseificado **medido** na mesma `N`
(a reserva); sem nenhum dos dois, a razão fica em branco em vez de inventada.

**Cada ponto é uma condição estável independente do caminho.** O ensaio sempre espera o
regime; um ponto medido subindo ou descendo em `N`/`Q_g` é o mesmo ponto, porque é a mesma
condição estável. Por isso **não** há varredura de histerese nem ordenação obrigatória por
sentido: se estabilizou, não há dependência do estado anterior a modelar. O _flooding_ é lido
da **forma** da curva de pontos estáveis, não de comparar ida com volta.

### 4.6 Esclarecimento: dois "flow numbers", só um mensurável

O termo _Flow Number_ é ambíguo e vale fixar o vocabulário:

- **Número de aeração / gas flow number** `Q_g/(N D³)` — governa o _flooding_. É
  **mensurável** aqui: `Q_g` do fluxômetro, `N` do servo, `D` da geometria. É este que a
  página calcula.
- **Pumping flow number** do impelidor `Q_bombeado/(N D³)` — caracteriza a vazão de
  descarga do impelidor. Exige velocimetria do escoamento (LDA/PIV). **Não é mensurável**
  nesta bancada e está fora de escopo.

Quando a interface disser "número de fluxo / aeração", é sempre o primeiro.

### 4.7 O gancho com o kLa — `P/V`

`P/V` [W/m³] é o parâmetro clássico de escalonamento, e pela primeira vez há potência de
eixo medida para correlacionar com kLa em vez de com rpm (`PLANO_SERVO_POTENCIA_APP.md`
§1.4). A página produz `P_líq`; com o volume útil declarado (§8), `P/V` sai de graça e pode
ser importado pelo Mapeamento kLa como âncora. O acoplamento é registrado como fase 3 (§18),
não como pré-requisito.

### 4.8 Correlação com a potência elétrica da rede (opcional)

A potência do ensaio é **mecânica no eixo** — não inclui as perdas do motor, do drive nem a
eletrônica do Módulo. Quem quiser fechar essa distância pode, opcionalmente, medir a potência
**elétrica** consumida da rede com um wattímetro ligado à tomada do conjunto (Módulo + Motor)
e registrar os pares no app (§12.3). Não há fiação nova ao Hub: o wattímetro é um instrumento
externo lido pelo operador, e o app apenas segura o sistema em regime para que a leitura seja
estável e digita-se o valor.

Sobre uma faixa de operação, os dois se relacionam de forma aproximadamente afim:

```text
P_elétrica ≈ a · P_mecânica + b
```

- `b` [W] é o **consumo de vazio** — eletrônica do Módulo, magnetização do motor e perdas do
  drive que existem mesmo com o eixo sem carga. É por isso que a diferença **não** é
  eficiência pura: a 0,17 W de eixo, a tomada já puxa dezenas de watts de _overhead_.
- `a` [–] é `1/(η_motor·η_drive) > 1`, a inclinação que converte eixo em rede na faixa
  carregada.

Alguns pares `(P_mecânica, P_elétrica)` bem escolhidos ao longo da varredura ajustam `a` e
`b` e permitem, depois, **estimar o consumo elétrico de um processo** a partir da potência de
eixo medida — que é o número que o operador realmente paga na conta. É caracterização de
bancada, honesta sobre o que mede: `b` é do conjunto, não do impelidor, e não entra em `Np`.

---

## 5. Identidade e estrutura do workspace

À imagem de `Testes-kLa` (plano de kLa §§4–5):

- pasta raiz `Testes-Potencia/`, irmã de `Testes-kLa/`, sob o mesmo `AppPaths`;
- um ensaio é uma subpasta nomeada pelo usuário (sem seletor de pasta do SO), com regra de
  nome única e sanitizada como a do kLa;
- o ensaio é **independente**, com associação **opcional** a: um registro de impelidor
  (§8), um mapa de kLa (para `P/V`), e uma calibração/tara (§9) reutilizável entre ensaios
  da mesma montagem.

---

## 6. Estrutura interna de um ensaio

| Arquivo | Conteúdo |
|---|---|
| `ensaio.json` | fluido (`ρ`, `μ`, `T`), geometria (impelidor, vaso, volume, chicanas), referências à calibração e à tara, metadados |
| `tabela-condicoes.json` | as condições planejadas, com modo de gás por linha (§7.3) |
| `serie-global.csv` | amostra a amostra ao vivo: `t`, `rpm`, `torque_pct`, `torque_nm`, `p_eixo_w`, `flow_lpm`, fase |
| `tara.json` | curva `P_vazio(N)` da montagem, com data e condições |
| `calibracao-torque.json` | escala/offset aferidos, com o torque de referência e a data |
| corrida `NNN/` | por condição×replicata: pontos brutos, janela de média, veredito, leituras manuais de energia (§12.3) |

**Autocontido, não sidecar.** Diferente do `servo-power.tsv` — que é um *sidecar*, gravado
**ao lado** do log de uma sessão de cultivo em `SessionLogger` — o ensaio de potência é
**independente**: ele vive inteiro na sua pasta em `Testes-Potencia/`, com o seu próprio
`serie-global.csv`, e **não** depende de haver uma sessão de cultivo gravando (aliás, o ensaio
recusa iniciar durante um cultivo, §14). Essa é a diferença que a Q22 levantou: "independente"
e "grava sidecar próprio" colapsam no mesmo — o ensaio é dono dos seus arquivos, ponto. As
convenções de formato são herdadas do servo por consistência: `InvariantCulture`, ponto
decimal, `t` em `TimeMinutes`, vazio (nunca zero) para leitura ausente, e um cabeçalho de
metadados com `HubFirmwareVersion`, `HubProtocolVersion`, o `T_nom` assumido, a tara/calibração
usadas e a versão do contrato — sem eles os N·m e W não são reinterpretáveis depois.

---

## 7. Modelo de dados

### 7.1 Domínio

```text
PowerTest        { Id, Name, Fluid, Geometry, TareRef, CalibrationRef, Conditions[],
                   ImpellerSetRef?, KlaMapRef?, … }
Fluid            { RhoKgM3, MuPaS, TempC, PresetName? }
Geometry         { Impellers[], VesselDiameterM, LiquidVolumeM3, Baffled }
Impeller         { Type, DiameterM, BladeCount, ClearanceM, StageIndex }   // lista: eixo misto
PowerCondition   { N_rpm, Qg_lpm?, Qg_vvm?, GasMode, Replicates, Done, Origin, Status }
PrecisionTarget  { RelPct, AbsFloorSource=Tare, Nmin=60, TmaxSec=300, MaxTries=3 }  // §12.1
PowerRun         { ConditionRef, Replicate, Phase, Points[], NSamples, AchievedCI95,
                   StopReason, Verdict, Tries, ManualElec? }   // StopReason: Target|Tmax|Aborted
PowerDataPoint   { t, RpmMeasured, TorquePct, TorqueNm, P_shaft_W, Flow_lpm, Counted }
ManualElecReading{ P_elec_W, P_mech_W_atReading, N_rpm, GasState, Instrument?, Note?, Time }
TareCurve        { (N_rpm, P_void_W, SigmaTau)[], Date, ImpellerSetHash }   // SigmaTau = piso de ruído
TorqueCalibration{ Scale, Offset, ReferenceNm, Date }
```

`GasMode = Ungassed | Gassed | Both | SinglePoint`. Uma condição `Both` gera dois pontos
científicos à mesma `N`: `P₀` (gás fechado) e `P_G` (gás na condição). `SinglePoint` é o modo
de conferência rápida (§12.4): segura uma `N`/`Q_g` e reporta `P`/`Np` ao vivo, sem tabela.
`Qg` entra em `L/min` **ou** `vvm`, e o app calcula o outro a partir do volume útil declarado
(§8); o valor guardado é sempre o `L/min` medido.

### 7.2 Grandezas derivadas (engine de análise)

`P_líq = P_shaft − P_void(N)`; por estágio, `Np_i`, `Re_i` com o `D` de cada impelidor
(§4.3); `Fl_G`, `Fr`, `P_G/P₀` (denominador da curva ajustada, §4.5); e a detecção de
_flooding_ (mínimo/joelho de `P_G/P₀ × Fl_G`, mais _overlay_ de correlação, §16). O **portão
de SNR** marca um ponto como "abaixo do ruído" quando `P_líq(N)` não supera `k·σ_τ(N)·ω`
(`σ_τ` da tara, `k` configurável). Todas recalculáveis na revisão, porque dependem de
entradas editáveis (`ρ`, `μ`, `D`, tara, calibração).

### 7.3 A tabela de condições

Mesma gramática da tabela de kLa (§9.3 daquele plano), com uma coluna a mais:

| Estado | N (rpm) | Qg (L/min) | Gás | Repl. | Concl. | Np | P_G/P₀ | Origem |
|---|---:|---:|---|---:|---:|---:|---:|---|
| Pendente | 300 | — | Não-gaseif. | 3 | 0 | — | — | Manual |
| Pendente | 300 | 5,0 | Ambas | 2 | 0 | — | — | Manual |
| Pendente | 600 | 5,0 | Gaseif. | 2 | 0 | — | — | Mapa kLa |

Regras herdadas: replicatas por linha; ordenação `N` crescente depois `Q` crescente; a
condição ativa congela `N`/`Q` quando confirmada; futuras editáveis; repetir/rejeitar/
pular/encerrar. A coluna de vazão aceita `L/min` ou `vvm` (alterna a exibição; guarda o
`L/min` medido). O modo `SinglePoint` não usa a tabela — é o painel de conferência ao vivo.

---

## 8. Geometria e propriedades do fluido

- **Conjunto de impelidores** como **lista**, não um diâmetro só: cada estágio tem tipo, `D`,
  nº de pás, _clearance_ e posição no eixo. O registro nasce **pré-carregado com quatro tipos**
  — **Rushton (pás planas)**, **hélice marinha**, **orelha de elefante** e **Smith (pás
  côncavas / CD-6)** — cada um com um `Np` de literatura de referência para o _overlay_ (§15).
  Configurações mistas (diâmetros diferentes entre estágios) são o caso normal desta bancada, e
  cada `Np` usa o `D` do seu estágio (§4.3). O conjunto é reutilizável e compartilhado com o
  Mapeamento kLa, não duplicado.
- **Rotação (limites de hardware):** mínimo **15 rpm**, máximo **1000 rpm**; a varredura usa
  **passo padrão de 50 rpm** (passo mínimo 5 rpm). Zero é proibido (desabilita o motor, §2.6),
  então o piso da varredura e do abort é 15 rpm, não 0.
- **Vaso e líquido:** `T` (diâmetro do tanque), **volume útil** e chicanas sim/não. O volume
  é **obrigatório** quando a vazão for dada em `vvm` (§11). `D⁵` e o regime dependem da
  geometria; sem chicana a alta `N` há vórtice e o `Np` deixa de ser limpo — a interface avisa.
- **Fluido:** `ρ`, `μ` na temperatura do ensaio, com _presets_. **Foco da fase 1 é água / platô
  turbulento**; a varredura de viscosidade (glicerol) para `Np(Re)` fora do turbulento fica para
  depois. Valor único por ensaio, mas a **T medida** é registrada por ponto para auditoria
  (§13). `μ` entra em `Re`; a temperatura importa, sobretudo para `μ`.

---

## 9. Calibração e tara — o núcleo do `Np` absoluto

### 9.1 Calibração estática de torque

Procedimento guiado, na forma das Calibrações que já existem no app: com o servo energizado
segurando posição (ou em rotação baixa constante), aplica-se um **torque conhecido**
`τ_ref = m·g·r` (massa aferida `m` a um braço `r`, por polia/alavanca no eixo) e lê-se o
`torque_pct` reportado. **Um ponto** (sua decisão) produz o fator de escala, com `Offset = 0`:

```text
τ_calibrado[N·m] = Scale · (torque_pct/100 · T_nom)      Scale = τ_ref / τ_lido,  Offset = 0
```

Fica em `calibracao-torque.json`, com a massa, o braço e a data. A estrutura mantém `Offset`
como campo (default 0) para uma futura calibração de dois pontos, sem virar dívida — mas a fase
1 usa só a escala. **Provisão honesta:** sem esta etapa, o ensaio roda e marca os resultados
como _relativos_ no manifesto e na interface.

### 9.2 Curva de tara `P_vazio(N)`

Uma varredura de `N` (o mesmo motor da §12) com **os impelidores montados girando no ar**
(vaso sem líquido), gravando `P_vazio` **e** o desvio-padrão do torque `σ_τ(N)` em cada
patamar. É um ensaio curto, guiado, gravado em `tara.json` e reutilizável. A interface deixa
claro que a tara pertence à **montagem** (eixo+selo+acoplamento+**conjunto de impelidores**),
não ao fluido: trocar qualquer impelidor pede nova tara — o `ImpellerSetHash` guardado na tara
é comparado ao do ensaio e uma divergência avisa antes de aplicar uma tara alheia. O `σ_τ(N)`
gravado aqui é o piso de ruído que a §12.1 e o portão de SNR (§7.2) consomem.

### 9.3 Proveniência

Todo resultado carrega, no ponto e no manifesto, **qual** tara e **qual** calibração o
geraram, com datas. Reprocessar com outra tara é uma revisão registrada, não uma
sobrescrita silenciosa — mesma disciplina científica do kLa.

---

## 10. Nova página e navegação

- `NavigationItem("power", "Potência", "Impeller", "Automação", "#64B5F6")`, inserido após
  `kla-mapping` em `ShellViewModel.cs`; roteamento no `MainWindow.xaml` como os demais;
  atalho de teclado na sequência dos existentes.
- **Duas páginas desde o começo**, ambas em Automação, espelhando `Determinar kLa` →
  `Mapeamento kLa`:
  - `NavigationItem("power", "Potência", "Impeller", …)` — **aquisição**: setup, tabela,
    varreduras (gaseificada e não-gaseificada), ponto único, curvas `Np(Re)` e `P_G/P₀×Fl_G`.
  - `NavigationItem("power-map", "Mapa de Potência", "Search", …)` — **síntese**: superfície
    `(N,Q_g)` em camadas, fronteira de _flooding_, comparação de impelidores, cruzamento
    `P/V`↔kLa.

  As duas nascem já na fase 1 (nav + página roteada) para fixar a estrutura; a de mapa entra
  como **esqueleto** e ganha conteúdo na fase 3. O que **não** se separa é gaseificada de
  não-gaseificada — é o mesmo runner dirigido pela tabela, na página de aquisição.
- **Layout mestre-detalhe**, confirmado com o usuário:

```text
┌ POTÊNCIA ─────────────────────────────────────────────────────────┐
│ Barra lateral (~340 px)          │  Resultados (restante)          │
│ ──────────────────────────────── │  ─────────────────────────────  │
│ • Ensaio: criar/abrir            │  ┌ Ao vivo ───────────────────┐ │
│ • Fluido: ρ, μ, T (presets)      │  │ τ e rpm × tempo             │ │
│ • Geometria: impelidor, vaso,    │  │ (assentamento + janela de   │ │
│   volume, chicanas               │  │  média em destaque)         │ │
│ • Calibração / Tara (status,     │  └─────────────────────────────┘ │
│   medir)                         │  ┌ Gráfico principal ─────────┐ │
│ • Tabela de condições (§7.3)     │  │ Np×Re │ P_G/P₀×Fl_G │ mapa  │ │
│ • Limiares: dwell, tolerância    │  │  (conforme o modo)          │ │
│   de torque, nº confirmações,    │  │  + overlay de literatura    │ │
│   estabilização no alívio ☐      │  └─────────────────────────────┘ │
│ • Limites: N máx, τ máx, N mín   │  ┌ Tabela de pontos ──────────┐ │
│ • Ao vivo: N, τ, P, Q_g, Np/     │  │ N,τ_líq,P,Np,Re,Q_g,Fl_G,   │ │
│   Re/Fl_G/Fr calculados          │  │ Fr,P_G/P₀,desvio (export.)  │ │
│ • ▶ Iniciar ⏸ ⏭ ⏹ (abortar =     │  └─────────────────────────────┘ │
│   rampa a N mín, nunca 0)        │                                 │
│ • Progresso i/n, ETA             │                                 │
└───────────────────────────────────────────────────────────────────┘
```

O card **Limiares** concentra os parâmetros e aplica ao vivo, como o de kLa; a página inteira
é de aplicação automática (`ControlWorkspaceContractTests` proíbe botão "Aplicar"). Entre eles
ficam os dois _checkboxes_ opcionais — **estabilização no alívio** (§13) e **obter energia
manual** (§12.3); o segundo, quando ligado, faz a captura pausar em cada ponto com um campo
para a leitura do wattímetro.

---

## 11. Protocolo de gases e comando de rotação

- **Gás:** reaproveita a tabela de estados e o intertravamento da §10 do plano de kLa —
  `flowSetpoint` + `V1`/`V2`/`v_Flow`, troca sempre por "fechar → confirmar → novo estado →
  confirmar", com a confirmação exigindo `FlowCommandPending==false`,
  `FlowCommandAck==FlowCommandId`, setpoint dentro da tolerância e fluxômetro online. Só as
  linhas de **alívio** dependem da montagem opcional (§13).
- **Rotação:** `motorSetpoint` como referência (o firmware 9.1 aplica a inversa do CN1);
  o app lê `ServoRpm` e só captura quando a **medida** entra na banda do alvo. Nunca comanda
  0 (§2.6).
- **Unidades de vazão:** o fluxômetro reporta `FlowRate` em **L/min reais** (nas condições de
  P,T da linha), então `Fl_G = (L/min ÷ 60000)/(N_rps·D³)` usa o valor medido direto, sem
  correção de normalização. A condição pode ser escrita em `L/min` ou em `vvm`
  (`vvm·V_útil = L/min`), exigindo o volume útil declarado; o app converte e guarda o `L/min`
  medido como grandeza primária.

---

## 12. Máquina de estados

Mais enxuta que a do kLa. Por condição da tabela:

```text
Idle → Preflight → (por condição)
  PreparingCondition
    → SettingSpeed            comanda N, espera ServoRpm na banda-alvo
    → [se Gaseificada/Ambas e alívio ligado] VentStabilizing (§13)
    → OpeningGas              abre Q_g da condição, confirma por ACK
    → SettlingTorque          PORTA 1: espera a média de τ parar de derivar (estacionário)
    → AccumulatingToTarget    PORTA 2: acumula até IC₉₅ ≤ alvo híbrido (n_min=60, t_max=5min)
       └─ t_max sem alvo / rejeitado → recaptura NO LUGAR (até MaxTries) → §12.1
    → [se 'Obter energia manual' ligado] HoldingForManualEnergy (§12.3)
    → Captured                grava o ponto (P₀ e/ou P_G)
    → [condição 'Ambas': fecha gás, repete SettlingTorque/AccumulatingToTarget para P₀]
  → PreparingNextRun → … → Reviewing → Accepted/Rejected/Repeat → Completed
                                      (Aborting / Faulted a qualquer momento)
```

### 12.1 Critério adaptativo — duas portas, parada por confiança

O tempo em cada condição **não** é fixo: é governado pela **confiança atingida**. O ponto é
capturado quando **duas portas distintas** fecham — primeiro a média para de derivar, depois o
seu intervalo de confiança fica apertado o bastante. Em `N` baixa (ruído ~40 %, §grounded
abaixo) o sistema acumula mais tempo; em `N` alta fecha rápido. É o "já peguei ponto suficiente
para essa condição" formalizado.

**Porta 1 — estacionariedade.** Depois de mudar `N`/`Q_g` o torque sobe e assenta (inércia do
líquido, vórtice, cavidade de gás). A porta só abre quando a **média parou de derivar**: a
inclinação de um ajuste linear do torque na janela móvel fica dentro da tolerância por algumas
leituras consecutivas (o mesmo espírito do `dDO/dt` do kLa) — uma excursão zera a contagem. As
amostras do transiente **não contam** para a média; só depois de estacionário a acumulação
começa a valer.

**Porta 2 — precisão (é ela que dá a parada).** A confiança é avaliada **sobre o torque**
(medida direta; a potência herda o ruído da rotação). Acumulando, calcula-se a média e o erro-
padrão `SE = σ/√n`; o intervalo de 95 % é `IC₉₅ = ±1,96·SE`. Para a condição quando o `IC₉₅`
entra no alvo **híbrido, o que for atingido primeiro** (sua Q1):

```text
IC₉₅ ≤ max( k_rel · |P̄| ,  IC_piso(σ_τ) )
```

- `k_rel · |P̄|` — alvo **relativo** (ex.: ±2 % da potência média);
- `IC_piso(σ_τ)` — piso **absoluto** vindo do `σ_τ(N)` medido na tara: o `IC` mais apertado que
  vale a pena perseguir dado o ruído próprio do drive; abaixo dele estar-se-ia "medindo ruído".

O `max` (= o alvo mais frouxo vence) é o que resolve os dois extremos: em **baixa carga** o
piso salva de um alvo relativo impossível; em **alta carga** o relativo já basta e não se
super-mede. O `IC` é calculado no torque e **propagado para `Np`** na exibição (sua Q2):
`Np = 5,1 ± 0,2 (95 %)` encolhendo ao vivo (Q7).

**Guarda-corpos (sua Q4):** `n_min = 60` amostras (~30 s a 2 Hz) antes de qualquer parada
poder disparar — um sopro de silêncio não encerra cedo; `t_max = 5 min` por captura — ao
estourar, captura o melhor esforço, marca **"precisão não atingida"** e entrega ao _fallback_
de recaptura abaixo.

> **Grounded na bancada (sessão `2026-09-03_1340`, servo online, ~sem carga).** O ruído de
> torque por amostra é real e cresce com `N`: `σ_τ ≈ 0,06 %` do nominal em repouso, `≈ 0,62 %`
> a ~300 rpm e `≈ 0,41 %` a ~600 rpm. A ~300 rpm isso é **~40 % do torque médio** (1,57 %) —
> inutilizável ponto a ponto, que é precisamente o problema da Q5. **A média é o que salva:** a
> ~2 Hz, uma janela de ~1 min (~120 amostras) baixa o erro-padrão da média por `√120`, para
> ~0,06 % — de volta ao piso de repouso. Duas consequências de projeto: (1) o critério de
> regime olha o **erro-padrão da média da janela**, não a amostra crua; (2) a **janela de média
> é dimensionada pelo `σ_τ(N)` da tara** para um erro-padrão alvo, então em `N` baixa ela é
> automaticamente mais longa. Com resolução de `0,1 %` no `torque_pct`, a quantização já pesa
> num valor de 1–2 %, mais um motivo para calibrar e mediar.

**Fallback de recaptura no lugar (sua sugestão anterior).** Se o `t_max` estourar sem o alvo,
ou se o ponto for reprovado, o runner **não aborta e não pula** — ele aproveita que já está na
condição e **recaptura no mesmo ponto**, até `MaxTries` tentativas, cada uma reiniciando as
duas portas. É barato (o custo de mudar `N`/`Q_g` já foi pago) e garante que se saia da
condição com um valor. Esgotadas as tentativas, o ponto é marcado **"não convergiu"** (com o
`IC` que se conseguiu) e a varredura segue para o próximo — a condição fica pendente para
revisão, em vez de derrubar o ensaio inteiro por um ponto teimoso. Cada tentativa é gravada
(`Tries`).

**Replicatas rodam cada uma até o alvo (sua Q5).** A parada por confiança **não** substitui as
replicatas: cada replicata é uma **reaproximação independente** (re-rampa `N`/`Q_g` de outro
estado, como na Q6) e roda até o mesmo `IC` alvo. O valor da condição é o agregado das médias
das replicatas, e uma discordância **maior que os `IC` individuais** delata um efeito
sistemático de partida — o que uma passada só nunca revelaria. É a diferença entre "preciso" e
"preciso e reprodutível".

**Autocorrelação, honestamente (sua Q6).** A ~2 Hz, amostras vizinhas são correlacionadas, então
`SE = σ/√n` **subestima** a incerteza real e o `IC` mostrado é um **limite inferior** dela. Fica
assim por ora, documentado na interface e no manifesto; o refino (tamanho efetivo de amostra ou
subamostragem) entra depois da bancada, sem mudar o desenho — só encolhe menos o `IC`.

> **Amostragem ~2 Hz.** Cada amostra Modbus custa ~250 ms; com `servoPollMs` mínimo o ciclo
> real fica em ~500 ms (`PROTOCOLO_HUB_v9` §6). Ótimo para média em regime, inútil para
> transiente rápido. O ensaio é "assenta → mede", e o app pode baixar o `poll_ms` durante a
> captura e devolvê-lo depois.

### 12.2 Condição "Ambas"

Gera `P₀` e `P_G` à **mesma rotação medida**, na sequência gás-fechado → gás-aberto (ou
inverso), para que a razão `P_G/P₀` use dois pontos de mesma `N` e mesma janela — não `P₀`
de outra condição. A `N` efetiva das duas medidas pode diferir por alguns décimos de rpm; a
razão usa cada qual com sua própria `N`.

### 12.3 Captura manual de energia elétrica (opcional)

Ligado por _checkbox_ (por ensaio, ou por linha da tabela para poupar tempo). Quando ativo,
após `AccumulatingToTarget` o runner entra em `HoldingForManualEnergy`: **mantém** a condição — rotação
e estado de gás congelados, malha estável — e **não avança sozinho**. A interface mostra a
potência mecânica estimada corrente ao vivo e um campo para o operador digitar a leitura do
wattímetro externo (W da rede, Módulo + Motor). Ao confirmar:

1. grava um `ManualElecReading` com `P_elec_W`, a `P_mech_W` daquela janela, `N` medida e o
   estado de gás — o par fica amarrado ao ponto científico, não solto;
2. libera a máquina para `Captured` e a próxima condição.

Regras:

- é **espera indefinida com segurança ativa**: o _hold_ respeita os limites de `N`máx/`τ`máx e
  a parada segura; abortar durante o _hold_ rampa a 15 rpm e fecha o gás como qualquer fase;
- o wattímetro **não** é lido pelo Hub; é instrumento externo, e o valor é entrada humana.
  Campo numérico com unidade explícita (W), opcionalmente a identificação do instrumento;
- sem o modo ligado, o estado não existe e a varredura não pausa;
- a leitura é registrada no sidecar e no manifesto, mas **não** entra em `P_líq` nem em `Np` —
  é grandeza do conjunto, correlacionada à parte (§4.8, §15).

### 12.4 Ponto único (conferência rápida)

Um modo sem tabela: o operador comanda uma `N` (e opcionalmente `Q_g`), o runner leva à
condição, aplica o mesmo `SettlingTorque`, e mostra **ao vivo** `τ`, `P_líq`, `Np`/`Re` (e
`P_G/P₀`/`Fl_G` se houver gás) — sem gravar corrida, ou gravando um ponto avulso se o operador
pedir. Serve para conferir um impelidor novo, achar uma faixa antes de montar a varredura, ou
observar o efeito de uma mudança física na hora. Usa a mesma tara e calibração do ensaio
aberto; sem elas, os números saem rotulados como _relativos_. Respeita as mesmas guardas de
segurança (§14) — inclusive o mínimo de 15 rpm.

---

## 13. Estabilização no alívio (montagem opcional)

Reaproveita a §11.2 do plano de kLa, sem a parte de OD. Ao abrir o fluxômetro no setpoint, o
medidor entrega um pulso acima da vazão pedida e leva segundos para assentar; num ensaio de
potência isso contamina a média de `P_G` no instante em que ela começa. Com a válvula de
alívio instalada logo após o fluxômetro (saída auxiliar que o N₂ não usa) e a opção ligada por
_checkbox_:

1. abrir o alívio **e** comandar o fluxômetro na vazão da condição — o pulso sai pelo alívio,
   não pelo reator;
2. aguardar `|Q_medida − Q| ≤ tolerância` por `N` leituras consecutivas;
3. fechar o alívio preservando o setpoint assentado — o gás muda de destino sem novo pulso;
4. confirmar e só então `OpeningGas`/`AccumulatingToTarget`.

Sem a montagem, o _checkbox_ fica desligado e o ensaio abre o gás direto, absorvendo o pulso
com um dwell maior antes de `SettlingTorque`.

---

## 14. Propriedade e segurança

- Novo `CommandOwner.PowerAssay`, no mesmo árbitro do kLa: enquanto o ensaio roda, ele é o
  dono da agitação e da malha de gás; o operador não digita rpm nem vazão à mão (como no kLa).
- **Recusa iniciar com cultivo/cascata ativos.** O ensaio varre a agitação de forma agressiva
  e é incompatível com um cultivo em andamento. Se a cascata de O₂ estiver engatada, ou houver
  dosagem/controle de processo ativo, o botão de iniciar fica bloqueado com o motivo explícito;
  o operador desengata primeiro, conscientemente. Não há "forçar".
- **Nunca comandar 0 rpm.** Saturação inferior 15 rpm (mínimo de hardware); abortar rampa até
  15 e fecha o gás pelo caminho de parada segura, nunca desabilita o motor.
- **Guardas de limite:** `N` máx e `τ` máx (% do nominal) configuráveis; ultrapassar
  interrompe a condição e vai para revisão, não força.
- **Medida ausente para o ensaio.** Se `ServoOnline` cair ou o roteamento for desligado no
  meio de uma captura, a média corrente é descartada (não completada com o último valor), a
  condição pausa e anuncia; retomar reinicia a captura daquele ponto.

---

## 15. Gráficos e resultados

- **Ao vivo:** `τ` e `rpm` × tempo, com a fase de acumulação destacada, **e um indicador de
  `IC₉₅` encolhendo** (barra/rótulo "`Np = … ± … (95 %)` — pronto quando ≤ alvo", sua Q7) — o
  operador vê a decisão de parar acontecer, não um relógio opaco. Reusa a infra de gráficos do
  app (ScottPlot).
- **Principal, conforme o modo:**
  - não-gaseificada → `Np × Re` (eixo `Re` log), um ponto por captura, com _overlay_ do `Np`
    de literatura para a geometria escolhida;
  - gaseificada → `P_G/P₀ × Fl_G`, com o ponto de _flooding_ marcado e `Fr` no eixo
    secundário;
  - mapa → superfície/contorno de `P_G/P₀` sobre `(N, Q_g)`, com a fronteira de _flooding_,
    reusando o motor de superfície do Mapeamento kLa.
- **Correlação elétrica** (quando há `ManualElecReading`): dispersão `P_elétrica × P_mecânica`
  com o ajuste afim `a·P_mec + b`, exibindo `a`, `b` e o consumo de vazio (§4.8).
- **Tabela de pontos** exportável (CSV), uma linha por ponto científico.

---

## 16. Revisão científica

- Aceitar/rejeitar por ponto, como o kLa faz por corrida; **auto-aceite opcional** (checkbox)
  quando a dispersão das replicatas fica abaixo do limiar, senão revisão manual. Pontos "não
  convergiu" (§12.1) chegam à revisão sinalizados, com o número de tentativas.
- Ajuste do platô de `Np` **por padrão**, no regime turbulento, com o **`Re` de corte
  editável** na revisão (padrão automático em `Re > ~10⁴`); cada ponto carrega o `IC₉₅`
  atingido na captura (§12.1), propagado para `Np`, então o platô é ajustado **ponderado pela
  precisão** e o número sai com barra de erro real, não só valor. Pontos "precisão não atingida"
  entram sinalizados e podem ser excluídos do ajuste.
- **Detecção de _flooding_ automática** (mínimo/joelho de `P_G/P₀ × Fl_G`, exibida com a
  `N`/`Q_g`), **com _overlay_ da correlação de Nienow por padrão** para comparar medido ×
  previsto — o marcador automático é editável à mão na revisão. (Fase 2.)
- **Rateio de `Np` por estágio** confirmado/ajustado aqui: a hipótese de partes iguais é
  explícita, e o revisor pode marcá-la como inválida para um conjunto muito assimétrico,
  passando a exibir só o `Np` do conjunto (§4.3).
- Recalcular `ρ`, `μ`, `D`, tara ou calibração gera uma **revisão registrada**, nunca uma
  sobrescrita — os pontos brutos (`τ`, `rpm`, `Q`) permanecem intactos e reprocessáveis.

---

## 17. Simulador e testes

- **Simulador:** estender o modelo de servo para responder a `N` e `Q_g` com um `Np` de
  brinquedo (`τ ∝ ρ Np N² D⁵`), assentamento de primeira ordem e um joelho de `P_G/P₀` numa
  `Fl_G` de corte — para exercitar regime, captura e detecção de _flooding_ sem hardware.
- **Testes:** matemática (`Np` por estágio com `D` de cada, `Re`/`Fl_G`/`Fr`, unidades L/min
  reais e vvm, cultura pt-BR); subtração de tara e o **portão de SNR** vindo do `σ_τ(N)`;
  aplicação de calibração; tabela dirigindo modos de gás por linha; condição "Ambas" e o `P₀`
  interpolado da curva ajustada gerando a razão; estabilização no alívio; **parada adaptativa**
  (Porta 1 estacionariedade, Porta 2 `IC₉₅ ≤ max(relativo, piso do σ_τ)`, `n_min`/`t_max`, `IC`
  propagado a `Np`, replicatas independentes até o alvo); máquina de estados (**recaptura no
  lugar até `MaxTries` e o ponto "não convergiu"**, abort a 15 rpm, medida ausente, _hold_ de
  energia manual pausando
  sem avançar); **recusa de iniciar com cascata/cultivo ativos**; ponto único; o ajuste afim da
  correlação elétrica (≥4 pares); e a reabertura de ensaios independentes (arquivos próprios em
  `Testes-Potencia/`), contra `ControlWorkspaceContractTests`.

---

## 18. Faseamento

| Fase | Entrega | Depende de |
|---|---|---|
| **1** | **as duas páginas** (aquisição + esqueleto do mapa) + geometria (conjunto de impelidores) + fluido + **tara (+σ)** + **calibração (1 ponto)** + **ponto único** + varredura não-gaseificada → **platô de `Np`** | telemetria do servo (pronta) |
| **2** | varredura gaseificada + `P_G/P₀ × Fl_G` + _flooding_ (auto + _overlay_ Nienow) + estabilização no alívio | protocolo de gases (reuso do kLa) |
| **3** | mapa `(N,Q_g)` (camadas) + **comparação de impelidores** + import bidirecional + acoplamento `P/V` ↔ Mapeamento kLa | fases 1–2 |

A fase 1 valida o mais arriscado (torque, tara, calibração, regime) com o mínimo de
dependências, e já inclui o **ponto único** (Q1) e a **captura manual de energia** (§12.3) —
ambos baratos, só um estado de _hold_ e campos de entrada. A fase 2 acende o gás. A fase 3
fecha o ciclo com o kLa: **import bidirecional** de condições `(N×Q_g)` (Q23) permite medir
potência exatamente nos pontos de um mapa de kLa interpolado e cruzar qualquer ponto de um com
qualquer ponto do outro, e a **comparação de impelidores** (Q2) sobrepõe curvas de ensaios
diferentes.

### 18.1 Fase 1 — ordem de implementação e progresso

Cada passo é compilável e testável sozinho; a ordem não é arbitrária (o domínio antes do
engine, o engine e o simulador antes do runner, o runner antes da UI). **Marque `[x]` ao
concluir, com a data e o commit** — esta lista é o estado vivo do desenvolvimento.

- [x] **1. Domínio + armazenamento** — `Services/PowerTesting/` (`PowerTestModels`,
      `PowerTestFileContracts`, `IPowerTestStore`/`PowerTestStore`) autocontido sob
      `Testes-Potencia/`; `AppPaths.PowerTestsDirectory`; registro na DI. _(2026-09-04, commit
      `c9c893a`: geometria de conjunto misto, tabela com modo de gás por linha, tara com `σ_τ`,
      calibração de um ponto, resumo de corrida com `IC`/`StopReason`; 15 testes de ida-e-volta;
      suíte completa 864 verdes / 1 ignorado)_

- [ ] **2. Engine de análise** — `PowerAnalysisEngine`, **puro** (sem UI, sem hardware), o
      lugar único de toda a matemática do §4. Reprocessável na revisão.
  - [ ] 2.1 Grandezas base: `rpm→rev/s`, `ω = 2π·N_rps`, `P_eixo = τ·ω`; torque calibrado
        `τ = Scale·(torque_pct/100·T_nom)` (`Offset` reservado, fase 1 só escala).
  - [ ] 2.2 Tara: interpolação de `P_vazio(N)` e `σ_τ(N)` na curva; `P_líq = P_medida − P_vazio`.
  - [ ] 2.3 `Np`/`Re` **por estágio** com o `D` de cada; rateio de potência (partes iguais como
        hipótese explícita) e o `Np` do conjunto em paralelo (§4.3).
  - [ ] 2.4 Portão de SNR: marca "abaixo do ruído" quando `P_líq ≤ k·σ_τ(N)·ω` (§7.2).
  - [ ] 2.5 Estatística de janela **incremental**: média, desvio, `SE = σ/√n`, `IC₉₅`;
        propagação do `IC` para `Np` pelo fator `ρN³D⁵`.
  - [ ] 2.6 Ajuste de platô: seleção `Re > corte` (editável), média **ponderada pelo `IC`**,
        incerteza do platô; pontos "não convergiu" excluíveis.
  - [ ] 2.7 Correlação afim de energia: mínimos quadrados `P_elétrica ≈ a·P_mec + b` dos pares
        manuais, com `a`, `b` e o consumo de vazio (§4.8).
  - [ ] 2.8 _(fase 2, deixar o gancho)_ `Fl_G`, `Fr`, `P_G/P₀` com `P₀` interpolado; detecção
        de _flooding_.
  - [ ] 2.9 Testes: cada fórmula, unidades, cultura pt-BR, tara, SNR, IC, platô, ajuste afim.

- [x] **3. Simulador** — estende o modelo de servo para o runner ser exercitado sem bancada.
      _(Fase 1 concluída em 2026-09-03, commit `ab4ca6d`; 26 testes de servo verdes. O joelho
      de flooding permanece corretamente adiado para 3.5/Fase 2.)_
  - [x] 3.1 Torque em regime `τ = ρ·Np(tipo)·N²·D⁵/(2π)` + tara por estágio.
  - [x] 3.2 Assentamento de 1ª ordem ao mudar `N`/`Q_g` (constante de tempo configurável).
  - [x] 3.3 Ruído `σ_τ(N)` **calibrado pelos números reais** (0,06 % repouso → ~0,6 % a 300 rpm,
        sessão `2026-09-03_1340`).
  - [x] 3.4 Acopla ao caminho existente: telemetria (`ServoRpm`/`ServoTorqueNm`/`ServoPowerW`) e
        comando (`motorSetpoint`; `FlowSetpoint` na fase 2).
  - [ ] 3.5 _(fase 2)_ joelho de `P_G/P₀` numa `Fl_G` de corte.

- [ ] **4. Runner + parada adaptativa — O ALGORITMO DO TESTE AUTOMÁTICO** (`PowerTestRunner`,
      molde `KlaTestRunner`). É o coração; cada subitem é testável contra o simulador.
  - [ ] 4.1 Esqueleto da máquina de estados (§12): `PowerRunPhase`, transições, `StateChanged`,
        `PhaseElapsedSeconds`, `IsRunning`/`IsInReview`.
  - [ ] 4.2 Pré-voo e **recusa de iniciar**: servo online+roteado, hub conectado, **cascata/
        cultivo NÃO ativos** (§14), tara/calibração presentes ou modo relativo assumido.
  - [ ] 4.3 Propriedade: `CommandOwner.PowerAssay` no árbitro; _claim_ da agitação (e do gás na
        fase 2); _release_ no fim, no abort e em `OwnershipRevoked`.
  - [ ] 4.4 `SettingSpeed`: comanda `motorSetpoint` (referência), espera **`ServoRpm` medido**
        entrar na banda-alvo por N leituras; nunca comanda 0 (piso 15 rpm).
  - [ ] 4.5 **Porta 1 — estacionariedade**: buffer móvel de `τ`, ajuste linear, `|inclinação|`
        abaixo da tolerância por N confirmações; **amostras do transiente não contam**.
  - [ ] 4.6 **Porta 2 — precisão**: acumulador incremental (média/σ/SE/`IC₉₅`); **para quando
        `IC₉₅ ≤ max(k_rel·|P̄|, piso σ_τ)`**; respeita `n_min = 60` e `t_max = 5 min`.
  - [ ] 4.7 **Recaptura no lugar**: ao estourar `t_max` ou reprovar, reinicia as duas portas até
        `MaxTries = 3`; grava `Tries` e `StopReason` (`Target`/`Tmax`/`NotConverged`).
  - [ ] 4.8 Amostragem: baixa `servoPollMs` durante a captura e devolve depois; `dt` **medido**,
        não assumido.
  - [ ] 4.9 Captura do ponto: grava `PowerRun` (média, `IC`, `N` medido, `StopReason`), a raw
        data da corrida e a `serie-global` alinhada.
  - [ ] 4.10 Condição **"Ambas"**: sequência gás-fechado→gás-aberto, dois pontos à mesma `N`
        (fase 1 só faz o `P₀`; o `P_G` acende na fase 2).
  - [ ] 4.11 **Replicatas independentes** (Q5): cada uma reaproxima e roda até o alvo; agrega as
        médias; discordância maior que os `IC` sinaliza efeito sistemático de partida.
  - [ ] 4.12 Segurança: guarda `τ`máx/`N`máx (interrompe→revisão, não força); **medida ausente**
        descarta a média corrente e pausa; abort **rampa a 15 rpm** e fecha o gás.
  - [ ] 4.13 `HoldingForManualEnergy` (se ligado): segura a condição, aguarda a entrada do
        wattímetro e grava `ManualElecReading` amarrado ao ponto (§12.3).
  - [ ] 4.14 _(fase 2)_ `OpeningGas` + `VentStabilizing` (§13).
  - [ ] 4.15 Testes: cada porta, o `IC`-stop, `n_min`/`t_max`, "não convergiu", abort a 15,
        medida ausente, recusa por cascata, "Ambas", replicatas.

- [ ] **5. Navegação + as duas páginas** (esqueleto roteado).
  - [ ] 5.1 `NavigationItem("power", "Potência", "Impeller", "Automação", …)` e
        `NavigationItem("power-map", "Mapa de Potência", "Search", …)` após `kla-mapping` no
        `ShellViewModel`.
  - [ ] 5.2 Roteamento no `MainWindow.xaml` (`PowerView` e `PowerMapView` via o conversor de
        visibilidade por `Id`).
  - [ ] 5.3 `PowerTestViewModel` e `PowerMapViewModel` registrados em `App.xaml.cs`, com
        `IPowerTestStore`, telemetria e árbitro injetados; descarte junto dos assinantes.
  - [ ] 5.4 Atalho de teclado na sequência; `LastPage` persiste a página.
  - [ ] 5.5 `PowerMapView` como **placeholder** rotulado "fase 3".

- [ ] **6. UI de aquisição — barra lateral (setup + tabela)** (`PowerView.xaml`, mestre-detalhe).
  - [ ] 6.1 Layout: `Grid` barra lateral ~340 px + área de resultados; responsivo; **tema-aware**
        por tokens (light/dark), rótulos pt-BR.
  - [ ] 6.2 Ensaio: criar/abrir/listar (diálogo de nome com validação, lista de `Testes-Potencia`);
        estados _rascunho/rodando/interrompido_.
  - [ ] 6.3 Fluido: `ρ`, `μ`, `T` + _presets_ (água); validação numérica `InvariantCulture`.
  - [ ] 6.4 **Registro de impelidores**: lista editável de estágios (tipo do catálogo dos quatro,
        `D`, pás, _clearance_, posição), adicionar/remover/reordenar; `ImpellerSetHash` recalculado
        e comparado ao da tara.
  - [ ] 6.5 Vaso e líquido: `T`, volume útil (**obrigatório se vvm**), chicanas; aviso de vórtice
        sem chicana a alta `N`.
  - [ ] 6.6 Card **Limiares** (aplicação automática, sem botão "Aplicar"): faixa/passo de `N`
        (padrão 50, mín 5, 15–1000), `k_rel`, `k_abs`, `n_min`, `t_max`, `MaxTries`, janela de
        estacionariedade; _checkboxes_ **estabilização no alívio** (fase 2) e **obter energia
        manual**.
  - [ ] 6.7 **Tabela de condições**: colunas `N`, vazão (`L/min`↔`vvm`), modo de gás, replicatas,
        concluídas, `Np`, status, origem; adicionar/editar/remover; **gerar varredura** (N ini/
        fim/passo → linhas); ordenação; repetir/rejeitar/pular.
  - [ ] 6.8 Status de **tara/calibração**: chips (presente/ausente, data, hash confere) + botões
        "medir" (→ passo 8); indicação **relativo vs absoluto**.
  - [ ] 6.9 Controles de execução: ▶ Iniciar / ⏸ / ⏭ / ⏹ (abort a 15); progresso `i/n`, ETA.

- [ ] **7. UI de aquisição — área de resultados (captura ao vivo)** (`PowerView.xaml`).
  - [ ] 7.1 Faixa ao vivo `τ` e `rpm` × tempo (ScottPlot), com a **fase de acumulação destacada**.
  - [ ] 7.2 **Indicador de `IC₉₅` encolhendo**: rótulo `Np = … ± … (95 %)` + barra de confiança
        "pronto quando ≤ alvo" — a decisão de parar fica visível (Q7).
  - [ ] 7.3 Leituras ao vivo: `N` medido, `τ %`, `τ N·m`, **P mec. estimada**, `Q_g`, e
        `Np`/`Re`/`Fl_G`/`Fr` calculados; **traço** quando ausente.
  - [ ] 7.4 Gráfico principal **`Np × Re`** (`Re` log), um ponto por captura **com barra de erro**,
        _overlay_ do `Np` de literatura do impelidor.
  - [ ] 7.5 **Tabela de pontos**: `N`, `τ_líq`, `P`, `Np`, `Re`, `IC`, `StopReason`, tentativas,
        timestamp; **exportar CSV**.
  - [ ] 7.6 Rótulos "mecânica estimada" em potência/energia; badges relativo/absoluto e
        "precisão não atingida".
  - [ ] 7.7 Testes de contrato: sem "Aplicar" (`ControlWorkspaceContractTests`), _bindings_,
        traço-não-zero.

- [ ] **8. Procedimentos guiados** (assistentes que gravam `tara.json`/`calibracao-torque.json`).
  - [ ] 8.1 **Calibração de torque (1 ponto)**: servo energizado, aplicar massa×braço, ler
        `torque_pct`, calcular `Scale`, gravar; na forma das Calibrações existentes.
  - [ ] 8.2 **Tara `P_vazio(N)+σ_τ`**: assistente de varredura no ar reusando o runner em modo
        tara; grava com `ImpellerSetHash`.
  - [ ] 8.3 **Ponto único**: painel de conferência (comanda `N`/`Q_g`, mostra ao vivo, ponto
        avulso opcional).
  - [ ] 8.4 **Captura manual de energia**: campo de entrada no _hold_, grava `ManualElecReading`;
        gráfico de correlação `P_elétrica × P_mecânica`.

- [ ] **9. Fechamento** — suíte completa verde + app iniciado, navegação às duas páginas e logs
      WPF recentes inspecionados.

**Portão da Fase 1:** uma varredura não-gaseificada roda ponta a ponta contra o simulador, para
cada condição **pela confiança** (as duas portas), e entrega `Np(Re)` com `IC`, tara e calibração
aplicadas, exibido na página e exportável. Fases 2 e 3 ganham suas próprias listas quando
começarem.

---

## 19. Armadilhas — o que não fazer

| Não faça | Porque |
|---|---|
| Usar o `ServoPowerW` do nó como potência do ensaio | não conhece a tara e depende de `T_nom` de placa; a grandeza científica é recalculada no app |
| Usar `motorSetpoint` como `N` nas fórmulas | é referência; `N` é a **medida** `ServoRpm` |
| Deixar a varredura passar por 0 rpm | 0 **desabilita** o motor e trava o teclado do módulo |
| Reportar `Np` absoluto sem calibração de torque | o valor carrega o erro de `T_nom`; sem calibração é **relativo**, e tem de ser rotulado assim |
| Esquecer a tara em impelidor de baixa demanda | o atrito de selo domina e o `Np` fica errado sem sinal de erro |
| Capturar antes do torque assentar | o pulso de vazão e a inércia do líquido enviesam a média; exigir regime por janela |
| Medir `P_G` no pulso de abertura do fluxômetro | usar a estabilização no alívio, ou dwell maior; o pulso não é a vazão declarada |
| Calcular período esperado como `1000/poll_ms` | `poll_ms` é **atraso**; cada amostra custa ~250 ms de barramento |
| Rotular potência sem "mecânica estimada" | vira consumo elétrico na cabeça do operador |
| Tratar `P_G/P₀ > 1` como erro | perto do _flooding_ e em baixa `Fl_G` a razão pode oscilar; é dado, não defeito |
| Comparar `Np` entre montagens com taras diferentes | a tara pertence à montagem; a proveniência tem de bater |
| Tratar `P_elétrica − P_mecânica` como eficiência do motor | inclui o consumo de vazio `b` do Módulo+drive; a relação é afim, não proporcional |
| Deixar a captura manual de energia entrar em `Np`/`P_líq` | é grandeza do conjunto (Módulo+Motor), correlacionada à parte, nunca somada ao eixo |
| Julgar regime pela amostra crua de torque | a ~300 rpm o ruído é ~40 % do valor; o critério é sobre o **erro-padrão da média da janela** |
| Usar janela de média fixa em toda a faixa de `N` | em `N` baixa o `σ_τ` exige acumular mais para o mesmo `IC`; a parada é por confiança, não por relógio |
| Acumular a média antes de estacionário | a Porta 1 existe para isso: contar amostras do transiente enviesa a média por mais apertado que fique o `IC` |
| Ler o `IC₉₅` como incerteza exata | a autocorrelação a 2 Hz o torna otimista; é **limite inferior**, rotulado como tal |
| Parar só porque um trecho ficou quieto | `n_min = 60` existe para não encerrar num sopro de silêncio antes de haver amostra suficiente |
| Publicar `Np` de um ponto abaixo do piso de ruído | o portão de SNR marca "abaixo do ruído"; um `Np` que é majoritariamente ruído engana |
| Assumir dependência do sentido da varredura | cada ponto é uma condição estável; se estabilizou, o caminho não importa |
| Aplicar tara de um conjunto de impelidores a outro | o `ImpellerSetHash` tem de bater; trocar um impelidor pede nova tara |

---

## 20. Decisões — resolvidas e ainda em aberto

**Resolvidas na rodada de 2026-09-03** (24 perguntas + refinamentos): tabela dirige o gás por
linha com `P_G` opcional; `Np` absoluto com calibração e tara; tara **no ar com impelidores
montados** (subtração ar→líquido); `Np` **por impelidor** com `D` de cada, conjuntos mistos;
`P₀` da razão **interpolado da curva ajustada**; regime sobre **torque** com limiar
`max(absoluto, relativo)` e piso de ruído da tara; **recaptura no lugar** com `MaxTries`;
**pontos independentes do caminho** (sem histerese); vazão em L/min reais **ou** vvm; `ρ`/`μ`
único com T medida por ponto; sem correção de holdup; _flooding_ **automático + overlay**;
platô editável na revisão; mapa em **camadas**; energia manual como **toggle**, ≥4 pares,
gaseificado incluído; ensaio **autocontido**, recusa durante cultivo; import bidirecional com
kLa na fase 3; associação a impelidor **solta, com sugestão**.

**Resolvidas na rodada de 2026-09-04 (fecham a fase 1):**

1. **Duas páginas desde o começo** — `Potência` (aquisição) e `Mapa de Potência` (síntese)
   definidas e navegáveis já na fase 1, para a estrutura ficar fixada; o mapa só ganha conteúdo
   na fase 3, mas seu lugar e identidade nascem agora (§10, §18). **Não** se separa gaseificada
   de não-gaseificada — é o mesmo runner dirigido por tabela.
2. **Calibração de torque: um ponto** — escala pura, `Offset = 0` (§9.1).
3. **Só o platô** — foco em água, número de potência no platô turbulento; **sem** varredura de
   viscosidade (glicerol) na fase 1. O eixo `Re` continua exposto, mas o `Np(Re)` fora do
   turbulento fica para depois.
4. **Rotação:** mínimo de **hardware 15 rpm**, máximo **1000 rpm**; **passo padrão 50 rpm**
   (passo mínimo 5 rpm). `τ`máx configurável como guarda.
5. **Tara não obrigatória** — permite rodar em modo **relativo**, rotulado como tal; a tara
   promove o resultado a absoluto quando existir (§9.2).
6. **Nienow** como correlação padrão do _overlay_ de _flooding_ (fase 2, §16).
7. **Exportação: só CSV** por ponto — sem relatório-resumo.
8. **Registro pré-carregado com quatro tipos:** Rushton (pás planas), hélice marinha, orelha de
   elefante e Smith (pás côncavas) (§8).
9. **`MaxTries = 3`.**

Nada fica em aberto para a fase 1. O que permanece adiado por fase: gaseificado + _flooding_
(fase 2) e mapa + comparação + import kLa + `P/V` (fase 3).
