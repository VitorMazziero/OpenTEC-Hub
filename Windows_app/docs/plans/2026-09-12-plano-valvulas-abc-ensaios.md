# Plano — Sistema fixo de válvulas A/B/C nos ensaios de potência e de kLa: o que muda na lógica e na interface do app

**Data:** 2026-09-12
**Origem:** `docs/plans/sistema_valvulas_ensaios_potencia_kLa_v2 (rascunho).md` e `valvulas.png`
(descrição física do arranjo, §3 "Arquitetura de acionamento elétrico") e as correções do usuário
no mesmo dia: o fluxômetro tem **duas entradas de MOSFET, 1 e 2**; **MOSFET 1 aciona B e C juntas**
(mesmo canal — abrem e fecham juntas) e **MOSFET 2 aciona A**; a nomenclatura do app deve seguir a
do hardware (A, B, C; entradas 1 e 2); essa é a ligação **padrão**, configurável (se A for para a
entrada 1, B/C vão para a 2).
**Estado:** em execução — **Etapas 1 a 5 concluídas em 12/09/2026.** Etapa 5 (runner de
potência): toda condição gaseificada passa por `PrestagingFlow` (ex-`VentStabilizing`) — Q na
saída B/C com `PrestageAgitationRpm`, saída pela banda **ou** por `FlowSettling`, uma frame
`Reactor` com o setpoint preservado → `OpeningGas` confirma → captura; P0/ungassed continua em
Fechado. Saem `VentStabilizationEnabled`, `SelectedVentValve`, `PowerVentValve`; `Vent*` →
`Prestage*` (`PrestageFlowToleranceLpm`, `PrestageFlowStableSamples`, `MaxPrestageSeconds`,
`PrestageFlowStabilityStdDevLpm/MaxErrorLpm`); `UsedVentStabilization` fica (coluna do
`resultados.csv`, agora sempre 1 em corrida gaseificada). `PowerTestDocument.GasRig` gravado ao
iniciar; `IsLegacyRig` → mensagem "montagem anterior ao arranjo A/B/C — só leitura" em
`ValidationMessage`, `CanStart` recusa (e recusa arranjo diferente do gravado).
`GasLoopStatusFor(runner, snapshot, rig)` reescrito sobre `GasRouting.Interpret`: *Fechado* /
*Reator (A)* / *Descarga + N₂ (B/C)* / *Gás sem destino* / *A e B/C abertas* + *Ar por C ·
estabilizando* na fase; `PowerView` com os seis `DataTrigger`. `CaptureSettingsDialog` sem o
checkbox e o seletor de válvula, com o bloco "Pré-estabilização por C" e a linha do arranjo.
**Decisão do usuário (12/09): sem guarda de N₂ e sem confirmação "N₂ fechado na fonte"** — no
ensaio de potência a linha B fica pinçada ou desconectada e o cilindro nunca é aberto (garantia
física; a sonda de DO nem é conectada); os passos 4 e 5 da etapa não foram implementados. Testes:
`PowerTestRunnerTests` +2 (rig gravado/legado/arranjo mudou; sequência no outro arranjo),
`PowerGassedUiTests` chip com os cinco textos e o outro arranjo; E2E no simulador (P0 +
gaseificado) passa por `PrestagingFlow`. Suíte 1577.
Etapa 4 (runner de
kLa): fases `OpeningNitrogen → Deoxygenating → PrestagingAir → SwitchingToReactor → Reoxygenating`
(saíram `ClosingNitrogen`, `WaitingForDOStability`, `OpeningVent`, `StabilizingVentFlow`,
`OpeningAir`); `BeginAirPrestage` pede Q na **mesma** rota B/C ao atingir `DOMin + AirPrestageLeadPercent`;
`EvaluateAirPrestage` exige ao mesmo tempo (a) vazão na banda por N quadros **ou** assentada
(`FlowSettling`, helper comum com o runner de potência — `PrestageFlowStabilityStdDevLpm`/`MaxErrorLpm`)
e (b) `DO ≤ DOMin` com dDO/dt plano por `StabilityRequiredSamples`; `SwitchToReactor` manda A on /
B/C off numa frame com a rotação da condição; a confirmação pelo eco é `t = 0`. **Decisões do
usuário (12/09):** DO já baixo no início → sem fase de N₂ e sem exigir a confirmação da fonte, mas
a pré-estabilização por C continua obrigatória ("baixo **e** estável": vazão e derivada do DO
assentadas antes de entrar no reator). Preflight "Confirmo que o N₂ está aberto na fonte" no
diálogo da sequência (checkbox) e nos inícios diretos (diálogo de confirmação);
`KlaTestDocument.NitrogenSourceConfirmedUtc` no manifesto e no `RunStarted` do jornal.
`KlaTestDocument.GasRig` gravado ao iniciar; `IsLegacyRig = GasRig null && Status != Draft` →
banner "montagem anterior ao arranjo A/B/C — só leitura" e `StartRunAsync` recusa (também recusa
se o arranjo em Configurações difere do gravado). **Desvios:** (1) `RelativeSeconds` continua
contando do início da corrida (arquivo e gráfico ao vivo monótonos; o ajuste log-linear é
invariante ao offset) e o `t = 0` vai explícito em `KlaTestRun/KlaTestRunSummary.SwitchRelativeSeconds`,
`SwitchFlowRateLpm`, `SwitchDoPercent`; (2) `KlaTestStore.CreateTest` perdeu os parâmetros de
válvula e `AppSettings.KlaNitrogenValve/KlaVentValve` saíram (o arranjo é `AppSettings.GasRig`);
(3) `KlaPlaybackDeviceService` lê a rota por `GasRouting.Interpret` (`Func<GasRigConfiguration>`
no `App`). Testes: `KlaTestRunnerTests` reescritos (11), `FlowSettlingTests`, VM +3, e
**`KlaRunnerSimulatorTests`** (runner fechado no simulador da Etapa 3 via `WireCodec` +
`TelemetryParser`: N₂ desce o DO, ar assenta em C, uma frame abre A, reoxigena até `DOMax`; fonte
fechada → teto da desoxigenação aborta) — substitui a checagem manual do §7.2. Suíte 1573.
Etapa 3 (simulador):
`DeviceModel.GasRig` (`--rig a-on-1|a-on-2`), `NitrogenSourceOpen` (`--nitrogen-source
open|closed`, padrão aberta), `ObservedRoute` pelo `GasRouting.Interpret`; ar oxigena só por A, N₂
desoxigena só por B/C **e** com a fonte aberta (cenário `nitrogen-left-open` força a fonte aberta —
e, como B e C ficam fechadas quando A está aberta, ele só age com B/C aberta: pré-estabilização por
C, nunca a fase de reator); linha morta = vazão ~0 e pressão subindo a `DeadEndPressureKpa`;
degrau de carga do aspersor na comutação C→A (`ReactorHeadStepFraction` 0,15 por 4 s);
`IsReliefPurging`/`IsReactorValveClosed`/`SelectedVentValve` removidos; `VentValveOpen` virou
`MainLineClosed` (v_Flow é o fechamento de linha). 8 testes em `SimulatorGasRigTests`; suíte 1558.
Etapas 1 e 2: Etapa 1 (`cee570a`):
`GasRouting.cs`, `CommandBuilders.FlowRoute`, `AppSettings.GasRig`; 33 testes. Etapa 2: os doze
produtores passam pelo roteador (runners de kLa e Potência com `RouteFrame`, receitas, cascata via
`CascadeController.BuildCommand(result, rig)`, gás proporcional da bomba, ponto único, calibração
por C com opção Reator, `BuildSetpointPreservingRoute` no painel de detalhe); `grep FlowSetpoint(`
só encontra `CommandBuilders` e os toggles crus de `FlowControlViewModel` (Etapa 6); suíte 1551.
**Desvio na Etapa 2:** `PowerTestRunner` e `PowerTestViewModel` não têm `ISettingsService` — recebem
`Func<GasRigConfiguration>` registrado no `App` (lê `Settings.Current.GasRig` a cada despacho).
`ShouldVentBeforeAir` do kLa deixou de comparar válvulas (a colisão "alívio = N₂" é o arranjo
normal agora); a validação de `StartRunAsync` que ainda recusa `ventValve == nitrogenValve` cai
na Etapa 4 junto com os campos. Independente dos dois planos anteriores de 12/09
(identidade dos nós e configuração dos nós); compartilha com o segundo a sintonia do fluxômetro (§8).
**Pré-leitura:** `docs/PROTOCOL.md` §3.1 (`valve_1`, `valve_2`, `v_Flow`), §4 (golden strings);
`docs/plans/PLANO_IMPLEMENTACAO_TESTES_KLA.md`; `docs/plans/PLANO_ENSAIOS_POTENCIA_IMPELIDOR.md`
§11–§13; `docs/DECISIONS.md` D-015 (árbitro), D-050; `Services/KlaTesting/KlaTestRunner.cs`,
`Services/PowerTesting/PowerTestRunner.cs`, `ViewModels/FlowControlViewModel.cs`,
`OpenTECHub.Simulator/DeviceModel.cs` (§`IsReliefPurging`).

---

## 0. Como executar este plano em outra sessão

1. **Ponto de partida.** Ler este arquivo, o documento físico e a pré-leitura. Baseline:
   `dotnet test Windows_app/OpenTECHub.slnx` (1448+ aprovados em 12/09/2026). O app não pode
   estar rodando durante `dotnet build`.
2. **Ordem** (uma etapa = um commit com testes, na `main`): **1** domínio de roteamento →
   **2** builders e todos os produtores de comando de gás → **3** simulador → **4** runner de kLa
   → **5** runner de potência → **6** página Controle, painel de detalhe, sinótico → **7**
   segurança e alarmes → **8** Configurações › Gás e válvulas + proveniência → **9** docs.
   1–3 antes de 4–6 porque os runners e a UI passam a depender do roteador e o simulador é o
   único jeito de exercitar as máquinas de estado sem bancada.
3. **Regras.** Fio congelado: continuam existindo só `flowSetpoint`, `maxFlow`, `valve_1`,
   `valve_2`, `v_Flow` — a mudança é **inteira no app** (quem decide o que cada entrada recebe).
   Nenhum firmware muda. Todo comando de gás passa pelo árbitro (D-015) e por **um único
   roteador** (§3.1). Nomes na interface = nomes no hardware (§3.2). Nunca comandar setpoint > 0
   sem destino (§3.3). Código em inglês, UI em pt-BR.
4. **Critério de pronto por etapa** listado em cada uma; ao fim, Etapa 9 e recibo de bancada
   (§7.3).

---

## 1. O arranjo físico, como o app precisa entendê-lo

### 1.1 Componentes e ligação elétrica

| Hardware | Função | Ligação |
|---|---|---|
| **A** — válvula de entrada | conecta a linha de ar do fluxômetro ao T de junção e ao aspersor | uma entrada de MOSFET do fluxômetro (**padrão: entrada 2**) |
| **B** — válvula de N₂ | liga a linha de nitrogênio ao T de junção antes do aspersor | **a outra entrada, compartilhada com C** (**padrão: entrada 1**) |
| **C** — válvula de descarga (bypass de estabilização) | descarrega o ar do fluxômetro para a atmosfera | **mesmo MOSFET de B** |
| Entradas 1 e 2 | os dois MOSFETs do fluxômetro | `valve_1` / `valve_2` no fio |
| `v_Flow` (GPIO 5 do fluxômetro) | no protocolo, "fechamento da linha, ativo-alto" | **assunção deste plano: não aciona nenhuma das três válvulas**; continua a ser enviado como hoje (`1` com setpoint 0) e não participa do roteamento — confirmar na bancada (§8) |

A ligação padrão é a do documento físico: **MOSFET 1 → B e C, MOSFET 2 → A** (`valve_1` = B/C,
`valve_2` = A). Atenção: é o **inverso** do que o app assume hoje (`SelectedNitrogenValve = Valve2`
põe o N₂ na entrada 2) — mais um motivo para a configuração ser explícita e visível. O operador
pode inverter em Configurações; B/C vão sempre para a entrada que A não usa.

### 1.2 Os quatro estados elétricos e o que significam

| Entrada de A | Entrada de B/C | Ar do fluxômetro | N₂ | Válido? | Nome no app |
|---|---|---|---|---|---|
| 0 | 0 | **sem destino** (linha morta) | fechado | só com setpoint 0 | **Fechado** |
| 1 | 0 | → A → aspersor | fechado | sim | **Reator (A)** |
| 0 | 1 | → C → descarga | → B → aspersor (se a fonte estiver aberta) | sim | **Descarga + N₂ (B/C)** |
| 1 | 1 | → A e → C ao mesmo tempo | → B → aspersor | **nunca comandado** | — |

Só há **dois destinos possíveis para o ar** e eles são mutuamente exclusivos com o N₂: ou o ar vai
ao reator (A), ou é descarregado por C enquanto B deixa o N₂ entrar. A comutação entre os dois
estados válidos é o evento experimental dos dois ensaios (§4.2 e §7 do documento físico).

### 1.3 Consequências que o app precisa absorver

1. **O reator deixou de ser o caminho padrão.** Hoje "ar ao reator" é `valve_1 = 0, valve_2 = 0`
   (o gás segue pela linha sem válvula). No arranjo novo, as duas entradas em 0 com setpoint > 0
   são uma **linha morta**: o fluxômetro empurra contra válvulas fechadas, a pressão sobe, o
   controlador satura. Isso é uma **mudança física incompatível**, não uma opção: todo produtor de
   setpoint de vazão do app precisa passar a abrir A.
2. **B e C são uma só.** Não existe "descarregar o ar sem abrir o N₂" nem "N₂ sem descarga". Logo:
   - nos ensaios de **potência**, durante a estabilização por C o N₂ entra no reator **se a fonte
     estiver pressurizada** (o §5 do documento físico exige isolamento a montante de B: fonte
     despressurizada, regulador fechado ou válvula manual fechada) — o app não consegue medir
     isso; precisa exigir do operador a confirmação "N₂ isolado a montante de B" antes de
     qualquer condição gaseificada (e pode vigiar a sonda de O₂: DO caindo durante a
     estabilização é sinal de N₂ aberto);
   - no **kLa**, não existe mais a janela "sem gás nenhum" entre fechar o N₂ e admitir o ar: fechar
     B fecha C, e abrir C reabre B. A pré-estabilização do ar acontece **com o N₂ ainda fluindo**
     (§6 do documento), e a comutação A↔B/C é **um único comando** que fecha o N₂, fecha a descarga
     e abre o reator ao mesmo tempo. **`t = 0` é esse instante.**
3. **A estabilização por descarga deixa de ser opcional** ("montagem opcional", `VentStabilizationEnabled`
   nos dois runners). É o único jeito de levar o fluxômetro ao setpoint sem que o pulso de 2–5× o
   valor nominal chegue ao reator; os *checkboxes* e os seletores de "válvula do alívio" e
   "válvula do N₂" somem, substituídos por uma configuração única do arranjo (§3.2).
4. **A espera de dissipação do N₂** (`PostNitrogenMinimumDelaySeconds`, `WaitingForDOStability`
   com tudo fechado) perde o sentido físico. O critério de estabilidade da sonda (derivada de DO)
   continua útil, mas aplicado **no piso de DO, com N₂ ainda fluindo**, em paralelo com a
   estabilidade da vazão de ar por C. O ajuste linear do kLa já começa na janela 45–70 % de DO
   (`AutoLinearStartPercent`/`AutoLinearEndPercent`), o que exclui o transiente inicial do
   `t = 0` de qualquer forma.
5. **A comutação muda a carga do controlador de vazão.** Descarregar por C (pressão ≈ atmosférica)
   e borbulhar pelo aspersor (coluna de líquido) não têm a mesma perda de carga; ao comutar, o
   fluxômetro vê um degrau de carga e pode transitar de novo, menor. O runner deve continuar a
   registrar `FlowRate` após `t = 0` (já registra) e a bancada deve medir o tamanho desse segundo
   transiente (§7.3) — é também o caso de uso da sintonia do PI exposta no plano de configuração
   dos nós.

---

## 2. O que o app faz hoje e por que quebra no arranjo novo

| Produtor de comando de gás | Hoje | No arranjo novo |
|---|---|---|
| `KlaTestRunner.OpenAir` (`FlowSetpoint(Q, false, false)`) | ar ao reator pelo caminho sem válvula | **linha morta** com setpoint Q |
| `KlaTestRunner.OpenNitrogen` (`FlowSetpoint(0, N₂=1)`) | N₂ pela válvula escolhida, ar em 0 | correto por acaso se o N₂ estiver na entrada de B/C; abre C junto (inócuo com setpoint 0) |
| `KlaTestRunner.OpenVent` (opcional, "válvula do alívio ≠ válvula do N₂") | alívio numa entrada, N₂ na outra | a premissa "alívio ≠ N₂" é **falsa** — são a mesma entrada; o `VentSkipped` dispararia sempre |
| `KlaTestRunner.TransitionToReoxygenation` (safe-stop → espera DO estável → ar) | fecha tudo, espera, abre ar direto | a espera "sem gás" só é possível com setpoint 0; reabrir ar por C reabre o N₂ |
| `PowerTestRunner.StartGasAdmission` sem alívio (`FlowSetpoint(Q, false, false, false)`) | ar ao reator | **linha morta** |
| `PowerTestRunner` com alívio (`FlowSetpoint(Q, vent=1)` → `FlowSetpoint(Q, false, false)`) | alívio → reator pelo caminho sem válvula | segunda frame vira linha morta; a primeira abre B (N₂) junto com C |
| `RecipeEngine.Actuation` (`FlowSetpoint(value, MaxFlow)`) | ar ao reator | **linha morta** — receitas com aeração deixam de aerar |
| `PumpControlViewModel` gás proporcional (`FlowSetpoint(qg, false, false)`) | ar ao reator | **linha morta** |
| `ShellViewModel`/painel de detalhe (`BuildSetpointUsingObservedValves`) | preserva as válvulas observadas | preserva um estado que pode ser Fechado com setpoint > 0 |
| `FlowControlViewModel` (Controle: *Válvula auxiliar*, *Válvula de N₂*, *Válvula de fechamento*) | três toggles crus | rótulos errados (a "auxiliar" é A ou B/C conforme a ligação; a "de N₂" também é a descarga); permite comandar linha morta e "as duas abertas" |
| `PowerTestViewModel.GasLoopStatusFor` | "Reator Aberto" quando qualquer entrada = 1; "Alívio aberto" quando `v_Flow = 1` com setpoint > 0 | lê o estado ao contrário do arranjo (B/C = 1 seria "Reator Aberto"); trata `v_Flow` como alívio, contra o `PROTOCOL.md` |
| `CommandBuilders.CoreSafeStop` (comentário "inverted vent flag") | `v_Flow` chamado de alívio | mesma ambiguidade documental |
| Simulador `DeviceModel.IsReliefPurging`/`IsReactorValveClosed` | modela "alívio numa entrada, reator no caminho padrão" | modelo físico errado para o arranjo novo |
| `FlowCalibrationViewModel` (`{"flowSetpoint":1.5,"valve_1":0,"valve_2":0,"v_Flow":0}`) | calibra soprando pelo reator | **linha morta** durante a calibração |

Em resumo: **todo caminho que hoje "abre ar" comanda uma linha morta no arranjo novo**, e as
opções de alívio dos dois ensaios partem de uma topologia que deixou de existir. O `Hub` e o
fluxômetro não mudam nada: o fio continua `valve_1`/`valve_2`/`v_Flow`.

---

## 3. Decisões de design

### 3.1 Um roteador único: intenção → entradas

Ninguém no app escreve `valve_1`/`valve_2` diretamente. Um tipo puro em `OpenTECHub.Protocol`,
`GasRouting`, recebe uma **intenção** (`GasRoute`) e a **ligação** (`GasRigConfiguration`) e
devolve o par de entradas:

```text
GasRoute.Closed          → A=0, B/C=0   (só aceito com setpoint 0; senão exceção no builder)
GasRoute.Reactor         → A=1, B/C=0
GasRoute.VentAndNitrogen → A=0, B/C=1
```

`GasRigConfiguration { AirInletInput = 1 | 2 }` diz em que entrada está A (**padrão 2**); B/C
ficam na outra (padrão 1).
O caminho inverso, `GasRouting.Interpret(valve1, valve2, setpoint, config)`, traduz o eco do
fluxômetro em `GasRoute` (+ `BothOpen` e `DeadEnd` como estados anômalos observáveis) e alimenta
todo texto de estado da UI. `FlowSafeStop` continua `setpoint 0, 0, 0, v_Flow 1` = `Closed`.

### 3.2 Nomenclatura = hardware

Na interface, nas mensagens de fase, nos eventos e nos manifestos: **A**, **B**, **C**,
**entrada 1**, **entrada 2**. Nunca mais "válvula auxiliar", "válvula de N₂" (é B), "válvula do
alívio" (é C), "valve_1/valve_2" soltos. A configuração se lê como no painel do equipamento:
*"A na entrada 2 · B/C na entrada 1"*. Onde o fio aparece (Eventos › comando enviado, jornal), o
app anota a tradução: `valve_1=0 (B/C) valve_2=1 (A)`.

### 3.3 Intertravamentos, no builder e na telemetria

- **Setpoint > 0 exige destino.** `CommandBuilders.FlowRoute(...)` recusa `Closed` com setpoint > 0
  (`ArgumentException`); nenhum produtor consegue montar uma linha morta por engano.
- **As duas abertas nunca são comandadas por um ensaio ou automação.** O roteador não tem essa
  saída. **Operação livre não tem restrição (decisão do usuário, 12/09/2026):** os toggles crus da
  página Controle (Avançado) abrem e fecham qualquer entrada, em qualquer combinação, sem bloqueio —
  a tela só **avisa** ("as duas abertas", "sem destino") e a telemetria denuncia (Etapa 7). A troca
  ON/OFF numa frame só e a recusa de linha morta valem para os runners, receitas, cascata e demais
  produtores automáticos, que passam pelo roteador.
- **Observado ≠ comandado é alarme.** Um eco com setpoint > 0 e as duas entradas em 0 por mais de
  3 s levanta o alarme *Gás sem destino* (Etapa 7); `BothOpen` observado levanta *A e B/C abertas*
  (aviso). Ambos aparecem no chip da malha de gás.
- **N₂ na fonte é responsabilidade do operador, com confirmação registrada.** Preflight de
  potência (condição gaseificada): "Confirmo que o N₂ está fechado na fonte"; preflight de kLa:
  "Confirmo que o N₂ está aberto na fonte". A confirmação vai ao manifesto e ao jornal. Guarda
  extra no ensaio de potência: se o DO cair mais de 5 % durante a estabilização por C, aviso
  "N₂ pode estar aberto".

### 3.4 As máquinas de estado seguem o documento físico

- **Potência (condição gaseificada):** `Fechado` → `VentAndNitrogen` com setpoint Q (estabilização
  por C, N₂ fechado na fonte) → estável → **uma frame** `Reactor` com o mesmo setpoint → confirma →
  captura. Estabilização deixa de ser opcional; `VentStabilizationEnabled`/`SelectedVentValve`
  somem das configurações.
- **kLa:** `Fechado` → `VentAndNitrogen`, setpoint 0 (só N₂) → desoxigenação → ao atingir o
  gatilho de pré-estabilização, **mesma rota** com setpoint Q (ar por C, N₂ continua) → estabilidade
  da vazão **e** do piso de DO → **uma frame** `Reactor` → confirmação = **`t = 0`** → reoxigenação.
  `SelectedNitrogenValve`, `SelectedVentValve`, `VentStabilizationEnabled`,
  `PostNitrogenMinimumDelaySeconds` e `MaxPostNitrogenStabilizationSeconds` somem;
  `AirPrestageLeadPercent` (0 = pré-estabilizar ao atingir `DOMin`; > 0 = começar esse tanto de
  % antes) entra.
- **Comutação é uma frame só**, com `valve_1` e `valve_2` no mesmo JSON: o fluxômetro aplica os
  dois `digitalWrite` em sequência no mesmo ciclo (`CommandCodec.h:145-151`); não há estado
  intermediário observável no fio.

### 3.5 Compatibilidade

Não há arranjo antigo a preservar (o físico mudou). Documentos de ensaio antigos continuam a
**abrir** (os campos `SelectedNitrogenValve`/`SelectedVentValve` ficam no modelo como legado,
ignorados ao executar), e um ensaio antigo **não pode ser continuado** no arranjo novo: ao abrir um
manifesto sem `GasRig`, o app marca "montagem anterior ao arranjo A/B/C — só leitura".

---

## 4. Modelo e vocabulário

```csharp
// OpenTECHub.Protocol/GasRouting.cs
public enum GasInput { Input1 = 1, Input2 = 2 }
public sealed record GasRigConfiguration(GasInput AirInletInput)      // A; B/C = the other
{
    public static readonly GasRigConfiguration Default = new(GasInput.Input2);   // MOSFET 2 → A; MOSFET 1 → B+C
    public GasInput VentAndNitrogenInput => AirInletInput == GasInput.Input1 ? GasInput.Input2 : GasInput.Input1;
}
public enum GasRoute { Closed, Reactor, VentAndNitrogen }
public enum ObservedGasRoute { Closed, Reactor, VentAndNitrogen, BothOpen, DeadEnd /* setpoint>0, both 0 */ }
public static class GasRouting
{
    public static (bool Valve1, bool Valve2) Resolve(GasRoute route, GasRigConfiguration rig);
    public static ObservedGasRoute Interpret(bool valve1, bool valve2, double setpoint, GasRigConfiguration rig);
    public static string Describe(GasRoute route, GasRigConfiguration rig);   // "Reator (A na entrada 2)"
    public static string DescribeWire(bool v1, bool v2, GasRigConfiguration rig); // "valve_1=0 (B/C) · valve_2=1 (A)"
}
// CommandBuilders
public static OpenTECCommand FlowRoute(double setpoint, double maxFlow, GasRoute route, GasRigConfiguration rig);
```

Persistência: `AppSettings.GasRig` (versão de esquema; ausente = `Default`). Manifestos:
`KlaTestDocument.GasRig`, `PowerTestDocument.GasRig`, `FlowCalibration` recibo `GasRig`,
`ExternalNodeProvenance`-style no sidecar ("# gas_rig: A=2 B/C=1").

Textos (pt-BR, iguais em todas as páginas): **Fechado** · **Reator (A)** · **Descarga + N₂ (B/C)**
· **A e B/C abertas** (anômalo) · **Gás sem destino** (anômalo).

---

## 5. Etapas

### Etapa 1 — Domínio de roteamento (`OpenTECHub.Protocol`) e configuração persistida

**Objetivo.** Um único lugar que sabe o que cada entrada recebe, testável sem WPF.
**Arquivos.** `src/OpenTECHub.Protocol/GasRouting.cs` (novo), `CommandBuilders.cs`,
`src/OpenTECHub/Services/Persistence/AppSettings.cs` (`GasRigSettings`), testes
`GasRoutingTests.cs` (novo), `WireFormatTests.cs`.
**Passos.**
1. Tipos do §4. `Resolve`/`Interpret`/`Describe*` puros. `Interpret`: `(0,0,sp>0)` → `DeadEnd`;
   `(1,1,·)` → `BothOpen`; os demais pelo mapa da configuração.
2. `CommandBuilders.FlowRoute(setpoint, maxFlow, route, rig)`: `Closed` com setpoint > 0 lança;
   `v_Flow` continua derivado (`setpoint == 0`); reutiliza `FlowSetpoint` por baixo para manter o
   *golden string* do fio. `FlowSafeStop` inalterado.
3. `AppSettings.GasRig { AirInletInput }` com default `Input2` (MOSFET 2 → A, MOSFET 1 → B+C);
   `SettingsService` migra ausência para o default sem avisar (é o padrão de ligação).
**Testes.** `Resolve` para as seis combinações (3 rotas × 2 ligações); `Interpret` idem +
anômalos; `FlowRoute` recusa `Closed` com setpoint > 0; golden strings: `Reactor` com o padrão (A em 2) →
`{"flowSetpoint":2.5,"maxFlow":50.0,"valve_1":0,"valve_2":1,"v_Flow":0}`; `VentAndNitrogen` →
`valve_1:1, valve_2:0`; A em 1 inverte; `pt-BR`.
**Pronto.** Suíte verde; nenhum produtor usa o roteador ainda (Etapa 2).
**Commit.** `feat(protocol): roteamento de gas A/B/C (GasRouting, GasRigConfiguration, FlowRoute)`.
**Esforço.** P.

### Etapa 2 — Todos os produtores de comando de gás passam pelo roteador

**Objetivo.** Nenhum `FlowSetpoint(x, v1, v2)` cru fora de `CommandBuilders`.
**Arquivos.** `KlaTestRunner.cs`, `PowerTestRunner.cs`, `RecipeEngine.Actuation.cs`,
`PumpControlViewModel.cs`, `FlowControlViewModel.cs` (`BuildSetpointUsingObservedValves` →
`BuildSetpointPreservingRoute`), `ShellViewModel.cs`, `PowerTestViewModel.cs` (ponto único),
`FlowCalibrationViewModel.cs`, `CascadeService`/aeração (via `FlowControl`), testes de cada um.
**Passos.**
1. Injetar a configuração: `ISettingsService.Current.GasRig` (VMs) e um `Func<GasRigConfiguration>`
   nos runners e no `RecipeEngine` (ler no início de cada corrida e gravar no manifesto).
2. Trocas mecânicas (ainda com as máquinas de estado atuais; a semântica nova entra nas Etapas
   4–5): "abrir ar" → `FlowRoute(Q, Reactor)`; "abrir N₂" → `FlowRoute(0, VentAndNitrogen)`; "alívio"
   → `FlowRoute(Q, VentAndNitrogen)`; receitas, gás proporcional da bomba, cascata e painel de
   detalhe → `Reactor`; ponto único de potência → `Reactor`; calibração de vazão → **`VentAndNitrogen`**
   por padrão (calibra soprando por C, sem encher o reator; a tela avisa "N₂ fechado na fonte") com
   opção `Reactor`.
3. `BuildSetpointPreservingRoute(setpoint)`: interpreta o eco atual; se `Closed`/`DeadEnd` e
   setpoint > 0 → `Reactor` (nunca reproduz uma linha morta); senão mantém a rota observada.
4. Testes existentes que fixam frames (`KlaTestRunnerTests`, `PowerTestRunnerTests`,
   `RecipeEngineTests`, `BiomassPumpTests`, `CascadeActuationTests`, `FlowmeterV05SyncTests`)
   passam a esperar `valve_2:1` (A, padrão) no ar ao reator — atualizar cada asserção citando o §1.3.1.
**Pronto.** `grep -rn "FlowSetpoint(" src/OpenTECHub` só encontra `CommandBuilders` e
`FlowControlViewModel` (Avançado, Etapa 6); suíte verde.
**Commit.** `refactor(gas): todo comando de vazao passa pelo roteador A/B/C`.
**Esforço.** M.

### Etapa 3 — Simulador: o arranjo A/B/C como modelo físico

**Objetivo.** As máquinas de estado das Etapas 4–5 exercitadas contra um modelo que só oxigena
quando A está aberta e só desoxigena quando B/C está.
**Arquivos.** `OpenTECHub.Simulator/DeviceModel.cs`, `WireCodec.cs`, `HttpEndpoint.cs`,
`Program.cs` (`--rig a-on-1`), testes `SimulatorGasRigTests.cs` (novo), `SimulatorPhase2Tests`.
**Passos.**
1. `DeviceModel.GasRig` (default A em 2) e `ObservedRoute` calculado de `Valve1/Valve2/FlowSetpoint`.
2. `StepOxygen`: transferência com **ar** só quando `Reactor`; **N₂** (kLa negativo, para o piso)
   só quando `VentAndNitrogen` **e** `NitrogenSourceOpen` (novo flag do modelo, default `true` no
   cenário kLa e `false` no de potência — o simulador é onde se ensaia o esquecimento da fonte);
   nada quando `Closed`.
3. `StepFlow`: `DeadEnd` (setpoint > 0, ambas 0) → vazão medida cai para ~0 e `Pressure` sobe
   (linha morta observável); `Reactor` → perda de carga do aspersor (transiente pequeno na
   comutação, parametrizado: `ReactorHeadStepFraction`, default 0,15); `VentAndNitrogen` → pulso
   de 2–5× por `_flowPulseTimer` (já existe) descarregado sem efeito no reator.
4. Remover `IsReliefPurging`/`IsReactorValveClosed`/`SelectedVentValve` do modelo (topologia
   antiga) e o `VentValveOpen` como "alívio" — `v_Flow` passa a ser só o fechamento de linha.
5. Cenário `nitrogen-left-open` (fonte de N₂ aberta num ensaio de potência) para a guarda de DO.
**Testes.** DO sobe só com `Reactor`; desce só com `VentAndNitrogen` + fonte aberta; `DeadEnd`
zera a vazão e sobe a pressão; a comutação `VentAndNitrogen → Reactor` produz o degrau de carga;
`--rig a-on-1` inverte as entradas e tudo continua igual.
**Pronto.** Suíte verde; `dotnet run --project src/OpenTECHub.Simulator -- http --rig a-on-1`
funciona.
**Commit.** `feat(simulator): arranjo A/B/C, linha morta, fonte de N2 e comutacao`.
**Esforço.** M.

### Etapa 4 — Runner de kLa: desoxigenação com pré-estabilização do ar por C e `t = 0` na comutação

**Objetivo.** Implementar a sequência do §6–§7 do documento físico.
**Arquivos.** `Services/KlaTesting/KlaTestModels.cs` (fases, settings, documento),
`KlaTestRunner.cs`, `KlaTestStore.cs` (manifesto/migração), `ViewModels/KlaDeterminationViewModel.cs`,
`Views/KlaDeterminationView.xaml`, `KlaTestFileContracts.cs`, testes `KlaTestRunnerTests`,
`KlaTestStoreTests`, `KlaDeterminationViewModelTests`, `KlaAnalysisEngineTests` (inalterado).
**Passos.**
1. **Fases** (`RunPhase`): `Idle, Preflight, ClosingAllGas, OpeningNitrogen, Deoxygenating,
   PrestagingAir, SwitchingToReactor, Reoxygenating, StoppingRun, Reviewing, Accepted, Rejected,
   PreparingNextRun, Completed, Aborting, Faulted`. Saem `ClosingNitrogen`, `WaitingForDOStability`,
   `OpeningVent`, `StabilizingVentFlow`, `OpeningAir`. Manter os nomes antigos no `enum` como
   `[Obsolete]` para ler manifestos velhos? Não — o CSV de corrida grava `EventCode = phase`; um
   leitor de arquivo antigo só precisa de string. Remover.
2. **Settings** (`KlaTestSettings`): remover `VentStabilizationEnabled`, `PostNitrogenMinimumDelaySeconds`,
   `MaxPostNitrogenStabilizationSeconds`; manter `DegassingAgitationRpm`, `VentAgitationRpm` →
   renomear `PrestageAgitationRpm` (rotação durante a pré-estabilização — continua baixa: o ar não
   está no reator, mas o N₂ está, e a rotação de desoxigenação já é a que vale; **decisão:** a
   pré-estabilização usa `DegassingAgitationRpm`, sem rotação própria — remover `VentAgitationRpm`),
   `VentFlowToleranceLpm`/`VentFlowStableSamples`/`MaxVentStabilizationSeconds` → `PrestageFlow*`;
   acrescentar `AirPrestageLeadPercent` (default 0). Estabilidade da sonda: `StabilityDerivative*`
   e `StabilityRequiredSamples` continuam, aplicados no piso.
3. **Documento** (`KlaTestDocument`): `GasRig` (gravado ao iniciar), `NitrogenSourceConfirmedUtc`;
   `SelectedNitrogenValve`/`SelectedVentValve` ficam como legado (leitura), `IsLegacyRig =>
   GasRig is null` bloqueia continuar.
4. **Runner:**
   - `OpenNitrogen`: `FlowRoute(0, VentAndNitrogen)` + `DegassingAgitationRpm`; confirma pelo eco.
   - `Deoxygenating`: quando `DO ≤ DOMin + AirPrestageLeadPercent` → `BeginAirPrestage()`:
     `FlowRoute(Q, VentAndNitrogen)` (mesma rota, setpoint Q; v_Flow cai para 0), fase
     `PrestagingAir`, `ResetStabilityDetection()`.
   - `PrestagingAir`: a cada quadro, avaliar (a) vazão: `|FlowRate − Q| ≤ tol` por N amostras **ou**
     assentada (reaproveitar `VentFlowHasSettled` do runner de potência, movendo-a para um helper
     comum `FlowSettling`); (b) piso de DO: `DO ≤ DOMin` **e** `|dDO/dt| ≤ threshold` por
     `StabilityRequiredSamples`. Status: "Ar por C a {Q} L/min ({dev}) · DO {x}% (dDO/dt {d}) ·
     vazão {n}/{N} · sonda {m}/{M}". Teto: `MaxPrestageSeconds` → parar para revisão
     (`StopReason.PrestageTimeout`).
   - Ambos satisfeitos → `SwitchToReactor()`: `FlowRoute(Q, Reactor)` + `AgitationRpm` da condição,
     fase `SwitchingToReactor`. Confirmação pelo eco (`IsGasStateConfirmed` com a rota) → fase
     `Reoxygenating` e **`_runStartMonotonic` = instante da confirmação** (`t = 0`); evento
     `SwitchedToReactor` com o `FlowRate` no instante e o DO inicial `C_{L,0}`.
   - `Reoxygenating`: como hoje; `StopRunAndReview` → `FlowSafeStop` (Fechado).
   - `IsGasStateConfirmed(s, target)`: `target` vira `(Flow, GasRoute)`; compara por `Interpret`.
5. **Preflight:** item "N₂ aberto na fonte" (confirmação obrigatória, gravada).
6. **UI (`KlaDeterminationView`):** remover "Válvula Conectada ao N₂", "Válvula Conectada ao
   Alívio", o *checkbox* de estabilização e o bloco "Espera após desligar N₂"; acrescentar o bloco
   **Pré-estabilização do ar por C** (antecipação em % de DO, tolerância, amostras, teto) e uma
   linha fixa "Arranjo: A na entrada {2|1} · B/C na entrada {1|2} (Configurações › Gás e válvulas)".
   Rótulos de fase: *Abrindo N₂ (B/C)*, *Desoxigenando*, *Ar por C · estabilizando*, *Comutando para
   o reator (A)*, *Reoxigenando*.
7. **Análise:** nada muda no `KlaAnalysisEngine`; o `t = 0` já é o início de `Reoxygenating`. A
   corrida grava `SwitchFlowRateLpm`, `SwitchDoPercent` no `KlaTestRunSummary` (proveniência).
**Testes.** Máquina de estados contra o simulador da Etapa 3: N₂ → DO cai → pré-estabilização
começa em `DOMin + lead` → não comuta enquanto a vazão oscila **ou** o DO ainda cai → comuta com
uma frame `Reactor` → `t = 0` na confirmação → reoxigena → revisão. Teto da pré-estabilização
→ revisão sem captura. Manifesto antigo abre em só-leitura. `KlaTestRunnerTests` reescritos onde
citavam `WaitingForDOStability`/`OpeningVent`.
**Pronto.** Suíte verde; simulador `--scenario normal` completa uma corrida com kLa plausível;
`--scenario nitrogen-left-open` não se aplica (kLa exige fonte aberta).
**Commit.** `feat(kla): pre-estabilizacao do ar por C durante o N2 e t=0 na comutacao A/(B+C)`.
**Esforço.** G (um período e meio). **Executada em 12/09/2026** — ver Estado no cabeçalho.

### Etapa 5 — Runner de potência: estabilização por C obrigatória e comutação em uma frame

**Objetivo.** Implementar o §4 do documento físico e proteger contra o N₂ aberto.
**Arquivos.** `Services/PowerTesting/PowerTestModels.cs`, `PowerTestRunner.cs`, `PowerTestStore.cs`,
`ViewModels/PowerTestViewModel.cs` (`GasLoopStatusFor`, preflight), `Views/Dialogs/CaptureSettingsDialog.xaml`,
`Views/PowerView.xaml` (chip), testes `PowerTestRunnerTests`, `PowerPreflightReadoutTests`,
`PowerGassedUiTests`, `PowerUiRefreshTests`.
**Passos.**
1. **Settings:** remover `VentStabilizationEnabled`, `SelectedVentValve` (`PowerVentValve` some);
   manter `VentAgitationRpm` → `PrestageAgitationRpm` (aqui faz sentido: durante a descarga o
   reator está sem gás e a rotação da condição pode ficar para a comutação, como hoje),
   `VentFlowToleranceLpm`/`VentFlowStableSamples`/`MaxVentStabilizationSeconds` → `PrestageFlow*`.
2. **Documento:** `GasRig`; `NitrogenSourceClosedConfirmedUtc` (preflight); legado só-leitura como
   no kLa.
3. **Runner:** condição gaseificada: `FlowRoute(Q, VentAndNitrogen)` + `PrestageAgitationRpm`
   → `PrestagingFlow` (ex-`VentStabilizing`, com os dois critérios de saída que já existem) → **uma
   frame** `FlowRoute(Q, Reactor)` + rotação da condição → `OpeningGas` (confirma) → captura.
   Ungassed/P0: `FlowSafeStop` (Fechado), como hoje. `RetryThenSkip` e a saída por estabilidade
   continuam.
4. **Guarda de N₂:** durante `PrestagingFlow`, se `OxygenCalibrated` cair mais de
   `NitrogenGuardDoDropPercent` (default 5) em relação ao início da fase → `StopForReview("DO
   caindo durante a descarga: o N₂ pode estar aberto na fonte")`, `PowerStopReason.NitrogenSuspected`.
5. **Preflight:** confirmação obrigatória "N₂ fechado na fonte" para ensaios com condição
   gaseificada; gravada.
6. **`GasLoopStatusFor`:** reescrever sobre `GasRouting.Interpret`: *Fechado* / *Reator (A)* /
   *Descarga + N₂ (B/C)* / *Gás sem destino* / *A e B/C abertas*; fase `PrestagingFlow` → *Ar por C ·
   estabilizando*. `PowerView.xaml`: os `DataTrigger` do chip acompanham os textos novos.
7. **`CaptureSettingsDialog`:** some o *checkbox* e o seletor de válvula; fica o bloco
   "Pré-estabilização por C" com tolerância/amostras/teto/rotação e a linha do arranjo.
**Testes.** Condição gaseificada sempre passa por `PrestagingFlow`; comutação em uma frame com
`valve_1:0, valve_2:1` (A em 2, padrão) e o setpoint preservado; guarda de N₂ com o cenário
`nitrogen-left-open`; P0 continua em Fechado; chip com os cinco textos; manifesto antigo só-leitura.
**Pronto.** Suíte verde; simulador completa um ensaio `Both` (P0 + gaseificado).
**Commit.** `feat(potencia): estabilizacao por C obrigatoria, comutacao em uma frame e guarda de N2`.
**Esforço.** M. **Executada em 12/09/2026 sem os passos 4 e 5 (guarda e confirmação de N₂)** — ver
Estado no cabeçalho.

### Etapa 6 — Página Controle, painel de detalhe e sinótico

**Objetivo.** O operador comanda **destinos**, não pinos — e lê o estado com os nomes do hardware.
**Arquivos.** `ViewModels/FlowControlViewModel.cs`, `Views/ControlView.xaml` (gaveta Vazão de Ar),
`Views/DetailPaneView.xaml`, `ViewModels/ControlViewModel.cs` (`Valve1Open/Valve2Open` para o
alarme de roteamento), `Views/SynopticView.xaml`/`ShellViewModel.cs` (tag da linha de gás),
`Services/Documentation/DocumentationCatalog.cs`, testes `ControlViewModelTests`,
`FlowmeterV05SyncTests`, `ControlWorkspaceContractTests`, `CompactLayoutTests`.
**Passos.**
1. `FlowControlViewModel`: `RequestedRoute : GasRoute` (Fechado / Reator (A) / Descarga + N₂ (B/C))
   substitui `RequestedValve1/2`; `RequestedMainValveClosed` continua como "Fechar linha (v_Flow)"
   em Avançado. `ObservedRoute : ObservedGasRoute` + `ObservedRouteText` substituem
   `Valve1ActualText`/`Valve2ActualText`; `WireText` = `GasRouting.DescribeWire(...)`.
   `BuildStagedCommand`: `FlowRoute(setpoint, RequestedRoute)`; validação: setpoint > 0 com
   `Fechado` → erro "Escolha um destino para o ar (A ou B/C)".
2. XAML da gaveta: um `SegmentedControl` **Destino do gás** com os três estados, `Telemetria:
   {ObservedRouteText}` abaixo, e a linha "Arranjo: A na entrada 2 · B/C na entrada 1". `Expander`
   **Avançado** com os dois toggles crus (*Entrada 1 (B/C)* / *Entrada 2 (A)* — rótulos derivados
   da configuração) e *Fechar linha (v_Flow)*, **sem bloqueio**: qualquer combinação é enviada
   (operação livre, §3.3); "as duas abertas" e "sem destino" aparecem como aviso em texto ao lado,
   nunca como recusa. Tooltip explica que é para bancada.
3. Painel de detalhe (setpoint de vazão): usa `BuildSetpointPreservingRoute` (Etapa 2); o texto de
   estado ao lado do setpoint mostra `ObservedRouteText`.
4. Sinótico: a tag da linha de gás mostra *Reator (A)* / *Descarga + N₂ (B/C)* / *Fechado*; estado
   anômalo em vermelho.
5. `ControlViewModel` → `AlarmService` roteamento: `Valve1Open/Valve2Open` viram `RequestedRoute`
   (o alarme de "Hub × app" compara rotas).
6. `DocumentationCatalog` (Controle › gaveta Vazão de Ar): reescrever o campo "Vazão de Ar" com o
   destino do gás e o Avançado.
**Testes.** Frame por destino nas duas ligações; erro de validação no seletor de destino; Avançado
**envia** as duas abertas e a linha morta e mostra o aviso; `ObservedRouteText` para os cinco estados; contratos de rótulo (`Destino do gás`,
`Reator (A)`, `Descarga + N₂ (B/C)`); layout compacto.
**Pronto.** Suíte verde; no simulador, alternar o destino muda a rota observada no quadro seguinte.
**Commit.** `feat(controle): destino do gas (A / B+C / fechado) no lugar dos toggles de valvula`.
**Esforço.** M.

### Etapa 7 — Segurança e alarmes

**Objetivo.** O que o roteador impede de comandar, a telemetria denuncia se acontecer.
**Arquivos.** `Services/Alarms/AlarmService.cs`, `AlarmModels.cs`, `Services/Telemetry/EventJournal.cs`,
`Services/Safety/SafetyCoordinator.cs` (sem mudança de contrato; conferir que o safe-stop é
`Closed` + 0), testes `AlarmServiceTests`, `SafetyCoordinatorTests`.
**Passos.**
1. Alarme **Gás sem destino** (`AlarmKind.GasDeadEnd`): `Interpret == DeadEnd` por ≥ 3 s →
   latched, audível, faixa; ação sugerida "Fechar a linha (parada segura da vazão)"; limpa quando
   a rota volta a válida.
2. Aviso **A e B/C abertas** (`BothOpen`): latched, sem áudio.
3. `EventJournal`: mudança de rota observada → "Gás: Reator (A) → Descarga + N₂ (B/C)"
   (`AuditSource.Equipment`).
4. `SafetyCoordinator.ExecuteGlobalSafeStopAsync`: a frame de vazão já é `FlowSafeStop`; garantir
   que o texto de confirmação destrutiva diga "fecha A e B/C e a linha".
**Testes.** Cenário do simulador com linha morta forçada (Avançado) dispara o alarme em 3 s e o
limpa ao rotear; jornal com uma linha por mudança.
**Pronto.** Suíte verde.
**Commit.** `feat(alarmes): gas sem destino e A/(B+C) abertas; jornal de rota de gas`.
**Esforço.** P.

### Etapa 8 — Configurações › Gás e válvulas, e proveniência

**Objetivo.** A ligação padrão (MOSFET 1 → B+C, MOSFET 2 → A) é editável, visível em todo lugar
que a usa e gravada em cada ensaio.
**Arquivos.** `ViewModels/SettingsViewModel.cs`, `Views/SettingsView.xaml` (nova seção), `AppSettings.cs`,
`Services/Persistence/SettingsService.cs`, manifestos (`KlaTestStore`, `PowerTestStore`,
`FlowCalibration` recibo), `SessionLogger` (preâmbulo), `DocumentationCatalog` (tópico
Configurações › Gás e válvulas), testes `WorkspaceDirectoryTests`/`SettingsViewModelTests`,
`NodeProvenanceTests` (estender), `DocumentationTests`.
**Passos.**
1. Seção **Gás e válvulas**: desenho em texto do arranjo (fluxômetro → T → A → T → aspersor; C na
   descarga; B no N₂), seletor **"Válvula A ligada na entrada"** `1 | 2`, linha calculada "B e C
   ligadas na entrada {outra}", padrão `2`, botão *Restaurar padrão*. Mudar exige que nenhum
   ensaio esteja em execução (bloqueio com motivo).
2. Ao mudar: `GasRig` persiste; `FlowControlViewModel` e os runners releem na próxima corrida; o
   jornal registra "Arranjo de válvulas: A → entrada 1" (quando invertido).
3. Proveniência: `GasRig` em `teste.json`, `ensaio.json`, recibos de calibração; `# gas_rig:` no
   sidecar; `ExternalNodeProvenance`-like helper `GasRigProvenance.Describe`.
4. Tópico do manual interno + linha no tópico de Controle e nos de kLa/Potência.
**Testes.** Persistência e default; bloqueio durante ensaio; manifestos com e sem `GasRig`.
**Pronto.** Suíte verde.
**Commit.** `feat(configuracoes): arranjo de valvulas A/B/C (entrada de A) e proveniencia`.
**Esforço.** P.

### Etapa 9 — Documentação

- `docs/PROTOCOL.md` §3.1: a tabela de `valve_1`/`valve_2` deixa de dizer "auxiliar / nitrogênio"
  e passa a "entradas 1 e 2 dos MOSFETs; o que cada uma aciona é configuração do app
  (`GasRigConfiguration`)"; `v_Flow` fica "fechamento de linha (GPIO 5); não roteia; assunção de
  que não aciona A/B/C — recibo de bancada"; corrigir o comentário "inverted vent flag" de
  `CoreSafeStop`. §4 golden strings novos (`Reactor`/`VentAndNitrogen` nas duas ligações).
- `docs/plans/PLANO_IMPLEMENTACAO_TESTES_KLA.md`: adendo "Arranjo A/B/C" com a máquina de
  estados nova e a definição de `t = 0`; `PLANO_ENSAIOS_POTENCIA_IMPELIDOR.md` §11–§13:
  substituir "montagem opcional" pela estabilização obrigatória por C e a guarda de N₂.
- `docs/DECISIONS.md` **D-053** — "Roteamento de gás por intenção com nomes do hardware; B e C
  no mesmo MOSFET; estabilização por C obrigatória; `t = 0` na comutação; N₂ na fonte confirmado
  pelo operador; setpoint > 0 exige destino".
- `docs/history/PHASE_LOG.md` P3-11; `CHANGELOG.md` (Changed — **breaking** para quem ainda
  tivesse o arranjo antigo; não há); `ROADMAP.md` (recibo de bancada §7.3; o item §I.4 do plano de
  11/09 ganha a nota "a descarga agora é C"); `CURRENT_STATUS.md`.
- `docs/MANUAL_DO_OPERADOR.md`: §8.A (kLa) e §8.B (potência) reescritos com o arranjo, a
  confirmação do N₂ na fonte e o significado das fases; §6 "Destino do gás" na página Controle;
  §7 calibração de vazão por C.
- `docs/plans/sistema_valvulas_ensaios_potencia_kLa_v2 (rascunho).md` → promover a
  `sistema_valvulas_ensaios_potencia_kLa.md` (commitar com `valvulas.png`), acrescentando a
  referência a este plano e a nota de que a ligação (MOSFET 1 → B+C, MOSFET 2 → A) é o padrão
  configurável do app.
**Commit.** `docs: arranjo de valvulas A/B/C (PROTOCOL 3.1, D-053, P3-11, planos de kLa e potencia, manual)`.
**Esforço.** M.

---

## 6. Ordem, dependências e esforço

| # | Etapa | Esforço | Depende de |
|---|---|---|---|
| 1 | Domínio de roteamento + configuração | P | — |
| 2 | Produtores pelo roteador | M | 1 |
| 3 | Simulador A/B/C | M | 1 |
| 4 | Runner de kLa | G | 2, 3 |
| 5 | Runner de potência | M | 2, 3 |
| 6 | Controle / detalhe / sinótico | M | 2 |
| 7 | Alarmes | P | 1, 3 |
| 8 | Configurações + proveniência | P | 1 |
| 9 | Docs | M | 1–8 |

P ≈ meio período, M ≈ um, G ≈ um e meio. Total ≈ 8–9 períodos + bancada. **Sem a Etapa 2 o app
comanda linhas mortas no arranjo novo** — ela é o mínimo indispensável antes de qualquer ensaio
com a montagem nova, mesmo que 4–6 fiquem para depois.

---

## 7. Verificação

### 7.1 Automatizada

`dotnet test Windows_app/OpenTECHub.slnx` — novos: `GasRoutingTests` (~14), `SimulatorGasRigTests`
(~8), reescrita de `KlaTestRunnerTests` e `PowerTestRunnerTests` nas fases de gás (~20), `AlarmServiceTests`
(+3), `ControlViewModelTests` (+6), contratos de rótulo e layout (+3).

### 7.2 Simulador

1. Padrão (A em 2) e `--rig a-on-1`: uma corrida de kLa completa e um ensaio de potência `Both`
   em cada ligação; os frames trocam `valve_1`/`valve_2` e o resto é idêntico.
2. `--scenario nitrogen-left-open` num ensaio de potência: a guarda para a corrida com o motivo
   certo.
3. Controle › Avançado: forçar linha morta → alarme *Gás sem destino* em 3 s → parada segura
   limpa.

### 7.3 Bancada (recibo)

0. **Ligação:** conferir MOSFET 1 → B e C, MOSFET 2 → A (ou registrar o inverso em
   Configurações); conferir o que o GPIO 5 (`v_Flow`) aciona — se acionar algo, voltar ao §8.
1. **Controle:** *Reator (A)* com 2 L/min → borbulha no aspersor, C fechada; *Descarga + N₂ (B/C)*
   → sai por C, B abre (ouvir/ver), nada no aspersor pelo ar; *Fechado* → tudo fechado, setpoint 0.
2. **Potência gaseificada:** confirmação de N₂ fechado; estabilização por C; medir o pulso (2–5×?)
   no `FlowRate` durante a descarga; comutação; medir o segundo transiente após `t = 0` (§1.3.5)
   e anotar em `docs/evidence/`; se relevante, sintonizar `kp/ki/ramp` pelo plano de configuração
   dos nós.
3. **kLa:** N₂ até `DOMin`; pré-estabilização com N₂ ainda fluindo; comutação em uma frame;
   `t = 0` no CSV coincide com o evento `SwitchedToReactor`; curva de reaeração sem o degrau
   inicial; comparar o kLa com um ensaio do arranjo antigo na mesma condição.
4. **Guarda de N₂:** abrir a fonte de propósito num ensaio de potência → a corrida para com
   "N₂ pode estar aberto".

---

## 8. Riscos e decisões em aberto

| Item | Tratamento |
|---|---|
| **O que o GPIO 5 (`v_Flow`) aciona no arranjo novo** | Assunção: nada das três. Se acionar uma válvula de linha real, ela entra no roteador como quarto sinal (Fechado = linha fechada) — confirmar na bancada antes da Etapa 2. |
| Padrão de ligação (MOSFET 1 → B+C, MOSFET 2 → A) | É o do documento físico e o **inverso** do que o app assume hoje (`SelectedNitrogenValve = Valve2`); por isso a Etapa 2 troca cada asserção de teste de forma explícita e a Etapa 8 mostra a ligação em todas as páginas. Se a bancada estiver ao contrário, muda-se em Configurações, não no código. |
| Quando começar a pré-estabilização do ar no kLa | `AirPrestageLeadPercent` = 0 (ao atingir `DOMin`) por padrão; antecipar economiza tempo mas gasta ar por C e mantém o N₂ fluindo — o operador decide por condição. |
| Segundo transiente na comutação (degrau de carga do aspersor) | Medir (§7.3.2). Se for relevante, duas saídas: sintonia do PI (plano dos nós) ou um `PostSwitchSettleSeconds` que atrasa o início da captura de potência (não o `t = 0` do kLa, que é físico). |
| N₂ aberto na fonte num ensaio de potência | Confirmação obrigatória + guarda de DO (só funciona com a sonda no reator). Sem sonda, só a confirmação. |
| Toggles crus em Avançado | Mantidos para bancada, com os dois intertravamentos; se causarem confusão, removem-se numa revisão posterior sem tocar no roteador. |
| Ensaios antigos | Só leitura; nada é migrado; o manifesto diz por quê. |
| Receitas gravadas com aeração | Passam a rotear para A automaticamente (Etapa 2); nada a editar nas receitas. |
