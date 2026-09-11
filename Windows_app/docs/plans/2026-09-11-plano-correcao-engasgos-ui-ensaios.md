# Plano de correção — engasgos da interface durante ensaios (Potência e kLa) e achados de bancada de 2026-09-11

**Data:** 2026-09-11 (última atualização no mesmo dia, ao fim dos ensaios Rushton-Smith e
IsojetB-Combijet)
**Escopo original:** eliminar as pausas periódicas da interface ("engasgos") observadas
enquanto um ensaio de potência captura dados, e verificar se a página de determinação de
kLa sofre do mesmo problema. O sintoma: a cada ~1 s a janela para por dezenas a centenas
de milissegundos; controles de entrada demoram a responder ao mouse; o engasgo é maior
quando "os documentos são salvos".
**Escopo acrescido ao longo do dia (§§G–O):** ensaio concluído sem saída; faixa de
alarmes; aceite automático × falhas de sequência; corrida sem captura aceita; critérios
não persistidos; envio da curva do fluxômetro (hub); revisão das alterações do operador;
navegar na tabela durante o ensaio; editar a tensão na calibração de vazão.
**Estado:** diagnóstico concluído por leitura do código, medição de I/O nesta máquina e
análise dos arquivos dos dois ensaios. **Nada deste plano foi implementado.** O working
tree tem alterações do operador não commitadas (§M) que devem ser commitadas antes.

## 0. Como executar este plano em outra sessão

1. **Ponto de partida.** Ler este arquivo inteiro; depois `docs/ARCHITECTURE.md` §2,
   `docs/CONVENTIONS.md` e `docs/DECISIONS.md` (D-046, D-047). O app **não** pode estar
   rodando durante `dotnet build` (o executável em `bin/` fica travado); confirmar com
   `Get-Process OpenTECHub`.
2. **Estado do repositório.** `git status` mostra (a) alterações do operador em
   `App.xaml.cs`, `CrashReporter.cs`, `IPowerTestRunner.cs`, `PowerTestRunner.cs`,
   `PowerTestViewModel.cs`, três views e três arquivos de teste — revisadas na §M,
   **commitar primeiro** em três commits; (b) dados de ensaio em `OpenTEC-Hub/` e
   screenshots em `docs/evidence/` modificados pelo uso normal — não fazem parte de
   nenhum commit de código; (c) alterações no `ESP32S3-HUB` (§L) — commit próprio no
   firmware.
3. **Ordem de execução** (uma etapa = um commit, com teste):
   §M.4 (commits do operador) → §K (persistir critérios; 500 s) → §J (recusar aceite
   sem captura) → §A (grades sem `Reset`) → §N (tabela navegável; depende de §A) →
   §B (I/O em fila) → §C (gráficos) → §D (prioridade do despacho) → §E (kLa) → §I
   (política autônoma + tolerância do alívio) → §G (Duplicar/Reabrir) → §H (faixa de
   alarmes) → §O (tensão editável) → §F (menores). §L é do firmware e independe.
   K e J vão primeiro porque são pequenos e são os que mais custaram tempo de bancada
   hoje; A antes de N porque N não funciona com o grid resetando a cada quadro.
4. **Comandos.** Build: `dotnet build OpenTECHub.slnx -c Debug`. Testes:
   `dotnet test tests/OpenTECHub.Tests --no-build` (≈55 s, 1139 testes; a falha
   pré-existente `ShellChromeTests.NavDrawer_in_compact_mode…` não é regressão). Testes
   de contrato do hub: `python -m unittest discover ESP32S3-HUB/tests/contracts`.
   Simulador para validar sem bancada: `dotnet run --project src/OpenTECHub.Simulator`
   e conectar por Wi-Fi/serial virtual conforme `docs/ARCHITECTURE.md`.
5. **Critério de pronto por etapa.** Cada seção lista seus testes; além deles, para
   A–D rodar o `UiHitchMonitor` (§5.1) num ensaio simulado de 10 min e anexar o
   contador de atrasos > 30 ms em `docs/evidence/`. Atualizar `CHANGELOG.md` e, onde a
   seção pede, `DECISIONS.md` (D-048 I/O em fila; D-049 supressão do crash de
   teardown; D-050 corrida sem captura não é aceitável).
6. **Dados reais para reproduzir.** `OpenTEC-Hub/Testes-Potencia/Rushton-Smith/`
   (63 pontos, 9 h, `serie-global.csv` de 3 MB, `ensaio.json` de 832 KB) e
   `…/IsojetB-Combijet/` (corridas `N0200_Q02p00_Rep01` e `N0200_Q04p00_Rep01` aceitas
   com n = 0 — o caso da §J; verificar se o operador já as alternou para rejeitadas).
   `OpenTEC-Hub/Logs/opentechub-20260911.log` e `Logs/Crash/` têm as evidências das
   §§I–M.

**Leia antes:**

- `docs/ARCHITECTURE.md` §2 — a regra de threading ("nada bloqueia a thread da UI").
  Este plano é a lista dos lugares em que a regra é violada hoje.
- `docs/plans/PLANO_ENSAIOS_POTENCIA_IMPELIDOR.md` e
  `docs/plans/PLANO_IMPLEMENTACAO_TESTES_KLA.md` — os módulos afetados.
- `docs/DECISIONS.md` D-046 — "todo dado bruto medido é gravado". O plano preserva
  isso: nenhuma amostra deixa de ser gravada; muda **quem** grava e **quando**.

---

## 1. Resumo executivo

O engasgo não é de microssegundos: são rajadas de **50–400 ms** na thread da UI, uma por
quadro de telemetria (~1 Hz durante o ensaio de potência; ver carimbos de
`serie-global.csv`). Tudo o que acontece quando um quadro chega roda **na thread da UI**,
num único item do `Dispatcher` enfileirado em `DispatcherPriority.DataBind` — prioridade
**acima** de `Input` e `Render`. O WPF precisa da mesma thread para processar hover, rolagem
e digitação (evento de entrada → trigger de estilo → layout → render); enquanto a rajada
não termina, nada disso anda.

Quatro causas, em ordem de peso:

| # | Causa | Onde | Custo por quadro | Potência | kLa |
|---|---|---|---|---|---|
| 1 | Grades da UI destruídas e reconstruídas duas vezes por quadro (`Conditions.Clear()` + 42 clones; `Results.Clear()` + 41 linhas) | `PowerTestViewModel.UpdateRunnerState` | dezenas de ms + invalidação de layout do DataGrid; seleção e estado de edição perdidos | **sim** | não |
| 2 | Escrita síncrona de arquivos na thread da UI: 2 `File.AppendAllText` (abre/escreve/fecha) por quadro; em cada mudança de fase, reescrita do manifesto de **832 KB** (`ensaio.json`) com `File.Move` e `Thread.Sleep(20)` em retry | `PowerTestStore`, `KlaTestStore` | 1–2 ms médios, picos de 12–22 ms por *append*; **55 ms médios, picos de 330 ms** para o manifesto de 832 KB (medido nesta máquina) | **sim** | parcial (appends; manifesto de 2 KB) |
| 3 | Redesenho completo dos gráficos ScottPlot a cada 1 s na thread da UI (`Clear()` + reconstrução + `AutoScale` + `Refresh()`), inclusive em páginas **ocultas**, porque `DeferredPageHost` só alterna `Visibility` e `Unloaded` nunca dispara | `PowerView`, `KlaDeterminationView`, `SynopticView` | 5–30 ms por gráfico visível; custo de preparação nos ocultos | **sim** (2) | **sim** (3) |
| 4 | Telemetria despachada em `DispatcherPriority.DataBind` (8) — ordena o processamento do quadro **antes** de entrada e render pendentes | `DeviceService.ToUi` | ordenação, não custo | sim | sim |

Descartados após verificação: OneDrive (medição igual dentro e fora da pasta sincronizada),
o transporte (todo I/O assíncrono no pool), Serilog (~1 linha / 30 s), `SettingsService`
(debounce + assíncrono), `SessionLogger` (`StreamWriter` mantido aberto — custo pequeno).

---

## 2. Diagnóstico detalhado — Potência

### 2.1 A cadeia por quadro (thread da UI)

`DeviceService.OnTelemetryReceived` → `Dispatcher.BeginInvoke(…, DataBind)`
([DeviceService.cs:256](../../src/OpenTECHub/Services/Communication/DeviceService.cs#L256),
[:281](../../src/OpenTECHub/Services/Communication/DeviceService.cs#L281)) → ~25 assinantes
de `TelemetryReceived`. Entre eles:

1. **`PowerTestRunner.OnTelemetryReceived`**
   ([PowerTestRunner.cs:821](../../src/OpenTECHub/Services/PowerTesting/PowerTestRunner.cs#L821))
   - `AppendSample` ([:1229](../../src/OpenTECHub/Services/PowerTesting/PowerTestRunner.cs#L1229))
     → `_store.AppendRunRawDataPoint`
     ([PowerTestStore.cs:746](../../src/OpenTECHub/Services/PowerTesting/PowerTestStore.cs#L746):
     `Directory.CreateDirectory` + `File.Exists` + `new FileInfo().Length` +
     `File.AppendAllText`) → `AppendGlobalSample` → `_store.AppendGlobalSeriesSample`
     ([:844](../../src/OpenTECHub/Services/PowerTesting/PowerTestStore.cs#L844):
     `File.AppendAllText` no `serie-global.csv` de 3 MB).
   - `DataPointAdded` → `PowerTestViewModel.OnDataPointAdded`
     ([PowerTestViewModel.cs:3632](../../src/OpenTECHub/ViewModels/PowerTestViewModel.cs#L3632))
     → `LivePoints.Add` + **`UpdateRunnerState()`**.
   - `RaiseStateChanged()` → `OnRunnerStateChanged` → **`UpdateRunnerState()` de novo**
     ([:3570](../../src/OpenTECHub/ViewModels/PowerTestViewModel.cs#L3570)).
2. **`UpdateRunnerState`** ([:3578](../../src/OpenTECHub/ViewModels/PowerTestViewModel.cs#L3578)),
   duas vezes por quadro:
   - `RebuildResults()` ([:3648](../../src/OpenTECHub/ViewModels/PowerTestViewModel.cs#L3648)):
     `Results.Clear()` + re-adição de todas as corridas → `Reset` no DataGrid de
     resultados, `SelectedResultRow` perdido; `DetectFlooding` enquanto `Flooding` for
     nulo com ≥ 3 corridas; `UpdateKlaEfficiencyComparison()`.
   - `RefreshConditionRows()` ([:3711](../../src/OpenTECHub/ViewModels/PowerTestViewModel.cs#L3711)):
     `Conditions.Clear()` + 42 × `Clone()` → `Reset` no DataGrid do plano; todos os
     `PropertyChanged` desassinados e reassinados em `OnConditionsCollectionChanged`;
     `SelectedCondition` reatribuído. **É a causa direta do hover/edição estranhos**: a
     grade do plano é destruída e reconstruída duas vezes por segundo, e nenhum contêiner
     de linha, estado de hover ou de edição sobrevive.
   - `NotifyDocumentState()` ([:3763](../../src/OpenTECHub/ViewModels/PowerTestViewModel.cs#L3763)):
     15 `OnPropertyChanged` + `RefreshPreflight()` → `_runner.CanStart` → `ValidateDocument`.
   - `UpdateGasLoopStatus()`, `UpdateSequenceProgress()`.
3. **`PowerTestViewModel.OnTelemetryReceived`**
   ([:3244](../../src/OpenTECHub/ViewModels/PowerTestViewModel.cs#L3244)) →
   `RecalculateLiveMetrics()` (inclui `ResolveReferenceP0`) + `RefreshPreflight()`
   (com throttle de 500 ms).

### 2.2 A cada mudança de fase (29 chamadas a `SetPhase`, 14 a `PersistCurrentRun`)

`PersistCurrentRun` ([PowerTestRunner.cs:1371](../../src/OpenTECHub/Services/PowerTesting/PowerTestRunner.cs#L1371)),
sempre na thread da UI:

- `SaveRunResult` — SHA-256 do CSV bruto + escrita atômica.
- `SaveConditionsTable` — 29 KB, escrita atômica.
- **`SaveTestManifest`** — serializa `ensaio.json` **indentado de 832 KB**, dos quais
  **490 KB são `tare.samples`** (as amostras brutas da tara embutidas no manifesto;
  `tare.rawSamplesFileName` existe no esquema mas está vazio) → grava em `.tmp-*` →
  `File.Move` com até 5 tentativas e **`Thread.Sleep(20)`** entre elas
  ([PowerTestStore.cs:975](../../src/OpenTECHub/Services/PowerTesting/PowerTestStore.cs#L975)).
- `UpdateResultsSummary` — escrita atômica.

Medição nesta máquina (PowerShell, `File.WriteAllText` de 832 KB + move): **~55 ms em
média, picos de 330 ms**. É o engasgo "quando os documentos são salvos".

### 2.3 Independentemente, a cada 1 s

`PowerView._redrawTimer` ([PowerView.xaml.cs:24](../../src/OpenTECHub/Views/PowerView.xaml.cs#L24),
[:150](../../src/OpenTECHub/Views/PowerView.xaml.cs#L150)): `plot.Clear()` + três
`Select().ToArray()` de até 6000 pontos + dois `Scatter` + `AutoScale` + `Refresh()`
(render Skia por software na thread da UI) para o gráfico ao vivo, mais `Np(Re)` ou
`PG/P0`.

---

## 3. Diagnóstico — kLa (a pergunta desta revisão)

A página de determinação de kLa **compartilha a arquitetura** e portanto compartilha as
causas 2, 3 e 4. **Não** compartilha a causa 1 (a mais pesada) e o manifesto é pequeno.
O resultado esperado é um engasgo **menor e menos perceptível** durante a corrida, e um
engasgo **maior** no momento de aceitar/rejeitar uma corrida.

### 3.1 O que é igual

- **Appends síncronos por quadro na thread da UI**:
  `KlaTestRunner.OnTelemetryReceived`
  ([KlaTestRunner.cs:576](../../src/OpenTECHub/Services/KlaTesting/KlaTestRunner.cs#L576))
  chama `_store.AppendRunRawDataPoint` ([:637](../../src/OpenTECHub/Services/KlaTesting/KlaTestRunner.cs#L637))
  e `_store.AppendGlobalSeriesSample` ([:670](../../src/OpenTECHub/Services/KlaTesting/KlaTestRunner.cs#L670)),
  ambos `File.AppendAllText` abre/escreve/fecha
  ([KlaTestStore.cs:556](../../src/OpenTECHub/Services/KlaTesting/KlaTestStore.cs#L556),
  [:791](../../src/OpenTECHub/Services/KlaTesting/KlaTestStore.cs#L791)). Mesmo custo por
  quadro do ensaio de potência (1–2 ms médios, picos de 12–22 ms). Tudo dentro de
  `lock (_gate)`.
- **`RaiseStateChanged()` a cada quadro** ([:724](../../src/OpenTECHub/Services/KlaTesting/KlaTestRunner.cs#L724))
  → `KlaDeterminationViewModel.OnRunnerStateChanged`
  ([KlaDeterminationViewModel.cs:1727](../../src/OpenTECHub/ViewModels/KlaDeterminationViewModel.cs#L1727)).
  Aqui é **leve**: ~10 `OnPropertyChanged` e `UpdateUiState()` (mais 9). Não há
  `Clear()` de grade por quadro — `RefreshConditionsList` só roda em transições.
- **Redesenho de 3 gráficos a cada 1 s** (`_plotDo`, `_plotLogLinear`, `_plotInstantKla`)
  com `Clear()` + `ToList()` + reconstrução + `Refresh()`
  ([KlaDeterminationView.xaml.cs:25](../../src/OpenTECHub/Views/KlaDeterminationView.xaml.cs#L25),
  [:141](../../src/OpenTECHub/Views/KlaDeterminationView.xaml.cs#L141)). O gráfico de DO
  adiciona ainda 5–7 linhas de referência e, em revisão, o ajuste exponencial. O timer
  **continua rodando com a página oculta** (mesmo motivo: `DeferredPageHost` só alterna
  `Visibility`). Uma sessão que visitou Sinóptico + Potência + kLa mantém até **9
  gráficos** sendo preparados por segundo, os visíveis também renderizados.
- **Mesma prioridade `DataBind`** no despacho (causa 4).
- **Manifesto e tabela reescritos a cada mudança de fase** (`SaveTestManifest`,
  `SaveConditionsTable` em 20 pontos do runner), mas `teste.json` tem **2 KB**: a
  escrita custa ~1–3 ms, não 55–330 ms. O `KlaTestStore.WriteAllTextAtomic`
  ([KlaTestStore.cs:573](../../src/OpenTECHub/Services/KlaTesting/KlaTestStore.cs#L573))
  não tem `Thread.Sleep`; o `Thread.Sleep(50)` do arquivo está em
  `MoveDirectoryWithRetry` (renomear/excluir teste, fora do caminho quente).

### 3.2 O que é específico do kLa

- **Recomputação O(n) por quadro durante a reoxigenação**:
  `OnDataPointAdded` ([KlaDeterminationViewModel.cs:1755](../../src/OpenTECHub/ViewModels/KlaDeterminationViewModel.cs#L1755))
  faz `LivePoints.Where(Reoxygenating).ToList()` sobre todos os pontos, recalcula
  `CalculateInstantaneousKlaSeries` (suavização O(n·janela)) e `ComputeLogLinearPoints`
  (OLS), e faz `Clear()` + re-adição de `InstantaneousKlaSeries` e `LogLinearSeries`.
  Essas duas coleções **não** estão ligadas a DataGrids (o view as lê pelo timer), então
  o custo é só de CPU — alguns ms para centenas de pontos, crescendo com a corrida.
  Não é o vilão, mas é trabalho repetido que pode ser feito uma vez por tick do gráfico.
- **Pico no aceite/rejeição da corrida** (`AcceptRunAsync`
  [KlaTestRunner.cs:326](../../src/OpenTECHub/Services/KlaTesting/KlaTestRunner.cs#L326)):
  `SaveRunRawData` reescreve o CSV bruto inteiro, `ComputeFileSha256` o relê, depois
  `SaveRunAnalysis`, `SaveRunResult`, `SaveConditionsTable`, `SaveTestManifest`,
  `UpdateResultsSummary` — tudo síncrono, na thread da UI. Em seguida o ViewModel chama
  `RefreshConditionsList` ([KlaDeterminationViewModel.cs:1890](../../src/OpenTECHub/ViewModels/KlaDeterminationViewModel.cs#L1890)),
  que faz `MatrixRows.Clear()` e, **para cada corrida do ensaio, lê `analise.json` do
  disco** (`_store.LoadRunAnalysis`). Com 20–40 corridas isso é um engasgo de centenas
  de ms — uma vez por corrida, não por quadro.
- `OpenReviewDrawer` recomputa a análise completa a partir de `LivePoints` na thread da
  UI ao entrar em revisão (uma vez por corrida; aceitável, mas entra no mesmo lote).

### 3.3 Veredito para o kLa

| Sintoma | Potência | kLa |
|---|---|---|
| Engasgo ~1 Hz durante captura | forte (grades + 2 appends + gráficos) | fraco (2 appends + 3 gráficos + recompute O(n)) |
| Engasgo em mudança de fase | forte (manifesto de 832 KB) | desprezível (manifesto de 2 KB) |
| Engasgo ao aceitar corrida | moderado (`PersistCurrentRun`) | moderado a forte (`SaveRunRawData` + SHA + N leituras de `analise.json`) |
| Grade perde seleção/hover durante a corrida | **sim** | não |
| Timers de gráfico rodando com a página oculta | sim | sim |

A correção das causas 2, 3 e 4 é **compartilhada**: o mesmo padrão de escrita em segundo
plano serve aos dois stores, o mesmo gate de visibilidade serve às três views, a mesma
mudança de prioridade serve a todos. O kLa ganha de graça ao corrigir a Potência, com dois
itens próprios (§4.E).

---

## 4. Mudanças propostas (em ordem de implementação)

### A. Parar de reconstruir as grades por quadro — `PowerTestViewModel` (maior ganho de UI)

1. Dividir `UpdateRunnerState` em **por amostra** (`IsRunning`, `IsInReview`, `IsPaused`,
   `PhaseLabel`, `StatusMessage`, rótulos de tempo, `CiProgressPercent`/`CiLabel`,
   `UpdateSequenceProgress`) e **estrutural** (`RebuildResults`, `RefreshConditionRows`,
   `NotifyDocumentState`, `UpdateGasLoopStatus`, `UpdateKlaEfficiencyComparison`).
   A parte estrutural roda apenas quando muda uma chave de revisão:
   `(runner.Phase, CurrentRun?.RunId, CurrentTest.Runs.Count, soma de AcceptedReplicates,
   SettingsRevision)`.
2. `RefreshConditionRows`: **atualizar no lugar** por `ConditionId` (copiar `Status`,
   `CompletedReplicates`, `AcceptedReplicates` para os objetos já na coleção; adicionar/
   remover só quando o plano muda de verdade). Preservar `SelectedCondition`.
3. `RebuildResults`: **upsert** por `RunId`; preservar `SelectedResultRow`.
4. Remover a duplicidade: `OnDataPointAdded` só faz `LivePoints.Add`; o gatilho único é
   `StateChanged`. No runner, `OnTelemetryReceived` levanta `StateChanged` **no máximo uma
   vez** por quadro (hoje `SetPhase` + o `RaiseStateChanged` final podem somar duas).
5. Tirar `DetectFlooding` de `RebuildResults` e chamá-lo no aceite de corrida.
6. `UpdateKlaEfficiencyComparison`: o primeiro
   `_klaStore.LoadExperimentsAsync().GetAwaiter().GetResult()`
   ([:1453](../../src/OpenTECHub/ViewModels/PowerTestViewModel.cs#L1453)) vira carga
   assíncrona/preguiçosa, fora da thread da UI.

### B. Tirar o I/O dos ensaios da thread da UI — `PowerTestStore`, `KlaTestStore`

1. Extrair um **escritor em segundo plano** reutilizável
   (`Services/Persistence/BackgroundFileWriter.cs`, ou nome equivalente):
   `System.Threading.Channels` com um único consumidor (`Task` de longa duração). Um
   consumidor único preserva exatamente a semântica de ordenação que `lock (_ioLock)` dá
   hoje. Mantém um `StreamWriter` por arquivo aberto (como `SessionLogger` já faz) para
   os appends, em vez de abrir/fechar por linha.
2. Os `Append*` dos dois stores (`RunRawDataPoint`, `GlobalSeriesSample`, `EventLog`,
   `TareRawSample`, `SinglePointSample`) **formatam a linha na thread chamadora** (barato,
   e a `SensorSnapshot` é imutável) e enfileiram.
3. Os `Save*` (manifesto, tabela de condições, resultado da corrida, resumo, análise
   kLa, tara, calibração) **serializam na thread chamadora** — mantém a semântica de
   consistência do documento, ~10–20 ms para o pior caso de hoje — e enfileiram a
   escrita `.tmp` + `Move`. Como é a mesma fila, o SHA-256 em `SaveRunResult` /
   `AcceptRunAsync` é calculado pelo consumidor **depois** dos appends, na ordem
   correta. O `RawDataSha256` passa a ser preenchido pelo consumidor (ou o hash é
   calculado em memória a partir das linhas já enfileiradas, evitando reler o arquivo).
4. Adicionar `Task FlushAsync()` a `IPowerTestStore` e `IKlaTestStore`; chamar em
   parar/concluir/abortar ensaio, no `Dispose`, e antes de qualquer `Load*` (para que
   uma leitura nunca veja um arquivo atrasado). Assinaturas `void` existentes mantidas,
   runners inalterados. Testes recebem um modo síncrono (`FlushAsync` após cada
   operação) para continuar determinísticos.
5. **Encolher `ensaio.json`**: deixar de embutir `tare.samples`; gravá-las no arquivo
   lateral que o esquema já prevê (`tare.rawSamplesFileName`, hoje vazio), mantendo só
   `tare.points` no manifesto. 832 KB → ~120 KB. Caminho de migração: ao carregar um
   ensaio cujo manifesto ainda traz `samples`, extraí-las para o arquivo lateral e
   reescrever o manifesto uma vez. O `tara.json` (650 KB) tem o mesmo problema e recebe
   o mesmo tratamento.
6. Com a escrita fora da thread da UI, o retry com `Thread.Sleep(20)` em
   `WriteAllTextAtomic` deixa de ser um problema (pode ficar).
7. Registrar a exceção de escrita no consumidor com `ILogger` e expor um evento
   `WriteFailed` para o runner marcar o ensaio como "gravação comprometida" — hoje uma
   exceção de I/O sobe pelo handler de telemetria.

### C. Gráficos — `PowerView`, `KlaDeterminationView`, `SynopticView`

1. Ligar os timers de redesenho a **`IsVisibleChanged`** (start/stop), não só a
   `Loaded`/`Unloaded`. Alternativa central: `DeferredPageHost` expor um evento ou
   propriedade anexada `IsPageActive` que as views observem.
2. Redesenhar **só quando os dados mudaram**: contador de versão em `LivePoints` /
   `Results` / `LogLinearSeries` / `InstantaneousKlaSeries`; o tick compara e sai cedo.
3. Gráfico ao vivo (Potência: torque + rpm; kLa: DO bruto + filtrado): substituir
   `Clear()` + reconstrução por `DataLogger`/`DataStreamer` do ScottPlot 5 (ou dois
   `Scatter` persistentes com buffers reutilizados), acrescentando só os pontos novos.
   Eliminar as cópias `ToArray()`/`ToList()` por tick.
4. Manter os timers em `DispatcherPriority.Background` (padrão do `DispatcherTimer`),
   que já fica abaixo de `Input`/`Render`.

### D. Despacho — `DeviceService.ToUi`

1. Publicar `TelemetryReceived`, `RawTelemetryReceived` e `DeviceLogReceived` em
   `DispatcherPriority.Background`; manter `StateChanged` e `CommandSent` em `Normal`.
   Os watchdogs dos runners são de 1 s; alguns ms de latência extra são inócuos.
2. Agrupar linhas de `RawTelemetry`/`DeviceLog` do mesmo quadro num único item de
   despacho.
3. Registrar em `docs/ARCHITECTURE.md` §2 a prioridade escolhida e o porquê.

### E. Itens próprios do kLa

1. `KlaDeterminationViewModel.OnDataPointAdded`: não recomputar `InstantaneousKlaSeries`
   e `LogLinearSeries` por quadro; marcar "sujo" e recomputar **uma vez por tick do
   gráfico** (1 s), ou a cada N pontos. Manter um índice do primeiro ponto de
   reoxigenação em vez de `LivePoints.Where(...)` a cada quadro.
2. `RefreshConditionsList`: **cachear** as análises por `RunFolderName` (invalidar no
   aceite/rejeição daquela corrida) em vez de reler todos os `analise.json` do disco; ou
   guardar `KlaPerHour`/`AnalysisR2` no próprio `KlaTestRun` do manifesto. Atualizar
   `MatrixRows` no lugar, como em A.2.
3. `AcceptRunAsync`/`RejectRunAsync`: com B.3 o `SaveRunRawData` + SHA + salvamentos
   saem da thread da UI; a recomputação em `OpenReviewDrawer` pode ir para
   `Task.Run` com o resultado aplicado via `await`.

### F. Menores

1. `SessionLogger`: `AutoFlush = false` + flush por timer de 1 s (ou usar o escritor
   de B.1).
2. Envolver o sink de arquivo do Serilog em `Serilog.Sinks.Async` (baixa prioridade —
   volume atual é ~1 linha / 30 s).
3. Fora deste problema, mas visível no log de hoje: `{"pHCal":"7.19"}` é reenviado a
   cada ~30 s. Investigar separadamente quem reenvia a calibração de pH.
4. Rótulo errado no chip "Malha de gás" após o ensaio concluir
   (`PowerTestViewModel.UpdateGasLoopStatus`,
   [PowerTestViewModel.cs:3543](../../src/OpenTECHub/ViewModels/PowerTestViewModel.cs#L3543)):
   com o runner ocioso, o ramo `FlowValveMain == 1` mostra "Alívio Estabilizando", mas
   `FlowSafeStop` deixa `v_Flow = 1` de propósito — é o shutoff principal ativo-alto
   (1 fecha o caminho de gás). Visto em 2026-09-11 ao fim do ensaio "Rushton-Smith":
   status `Completed`, 63/63 aceitas, válvulas 1 e 2 fechadas, e o chip preso em "Alívio
   Estabilizando". Não há perda de dados nem fase presa — é só o nome. Correção:
   reservar "Alívio Estabilizando" para `Phase == VentStabilizing`; fora de corrida,
   `v_Flow = 1` com `valve_1 = valve_2 = 0` e `flowSetpoint = 0` vira "Fechado (shutoff)",
   e `v_Flow = 1` com vazão > 0 vira "Alívio aberto".

### G. Ensaio concluído fica sem saída dentro da sessão (encontrado em 2026-09-11)

Ao terminar o ensaio "Rushton-Smith" (63/63 aceitas), Montagem, Aquisição, "Salvar
setup" e "Iniciar/continuar" ficaram desabilitados. Não é travamento: é
`CanEditPlan`/`CanStartOrContinue` exigindo `Status != Completed`
([PowerTestViewModel.cs:542](../../src/OpenTECHub/ViewModels/PowerTestViewModel.cs#L542)).
O problema é que **nada tira um ensaio de `Completed`** — nem o app, nem o recarregamento
(`PowerTestStore.LoadTest` só converte `Running` → `Interrupted`). Reiniciar o app não
destrava o ensaio; só "Novo"/"Abrir" saem do estado, e "Novo" cria um ensaio com geometria
padrão, obrigando a refazer montagem, tara e tabela.

1. **"Duplicar ensaio"** (`PowerTestViewModel.DuplicateTestCommand` +
   `IPowerTestStore.CreateTest` já existente): novo ensaio com nome pedido ao operador,
   copiando `Fluid`, `Geometry` (impelidores), `Calibration`, `Tare`, `Settings` e
   `Conditions` com `CompletedReplicates`/`AcceptedReplicates` zerados e `Status =
   Pending`; sem `Runs`, sem `Flooding`. Registrar `DuplicatedFrom` (TestId de origem) no
   manifesto para rastreabilidade.
2. **"Reabrir ensaio"** para `Completed`: confirmação destrutiva-leve, `Status →
   Interrupted`, `CompletedUtc = null`, `InterruptionReason = "Reaberto pelo operador"`,
   evento `TestReopened` em `eventos.jsonl`; corridas aceitas intactas. Depois disso
   "Iniciar/continuar" segue o caminho normal de `Interrupted`.
3. Texto do chip de status quando `Completed`: "Concluído — somente leitura. Use Novo
   ou Duplicar."
4. Confirmado na bancada em 2026-09-11: "Novo" funciona logo após um `Completed` na
   mesma sessão. O problema é de descoberta (o operador não sabia que "Novo" era o
   caminho), o que reforça os itens 1 e 3.

### H. Faixa de alarmes: sumir ao reconhecer, exceto na página de Eventos (pedido de 2026-09-11)

Comportamento pedido: cada linha da faixa superior de alarmes tem o seu "Reconhecer";
ao reconhecer, **a linha some**, e a faixa inteira some quando não sobra nada por
reconhecer. Na página **Eventos**, porém, a faixa fica **sempre** presente enquanto
houver alarme ativo (reconhecido ou não), mostrando o estado atual de todos — é a página
de auditoria, onde o operador quer ver o quadro completo.

Hoje ([ShellViewModel.cs:752](../../src/OpenTECHub/ViewModels/ShellViewModel.cs#L752),
[MainWindow.xaml:310](../../src/OpenTECHub/MainWindow.xaml#L310)): a faixa é visível
enquanto `IAlarmService.HasActiveAlarms` (qualquer alarme **latched**), e uma linha
reconhecida continua na faixa — com "Reconhecer" desabilitado — até a condição limpar
pelo `OffDeadband` (`AlarmCondition.Evaluate`,
[AlarmService.cs:80](../../src/OpenTECHub/Services/Alarms/AlarmService.cs#L80)). Ou seja,
reconhecer hoje não esconde nada em página nenhuma.

Mudanças (só apresentação; o `AlarmService` e o latch/deadband não mudam, para que o
journal e a lógica de segurança continuem iguais):

1. `ShellViewModel`: separar dois conjuntos derivados do `Snapshot()`:
   - `AnnunciatingAlarms` = latched **e não reconhecidos** (`IsAnnunciating`);
   - `LatchedAlarms` = todos os latched (o conjunto de hoje).
   Manter `Headline`/`OtherAlarms`/`AlarmMoreText` calculados sobre o conjunto que a
   página atual mostra (ver 2), para que o "+N" e a lista expandida batam com o que está
   na tela.
2. Nova propriedade `IsAlarmBannerVisible` =
   `SelectedNavigationId == "events" ? HasAlarms : HasUnacknowledgedAlarms`, notificada
   em `OnAlarmsChanged` **e** em `OnSelectedNavigationIdChanged`. A faixa passa a ligar
   `Visibility` a ela em vez de `HasAlarms`.
3. Fora de Eventos, as linhas exibidas são `AnnunciatingAlarms`; reconhecer a manchete
   promove o próximo não reconhecido a manchete, e a última reconhecida fecha a faixa.
   Em Eventos, as linhas são `LatchedAlarms`, com "Reconhecer" desabilitado nas já
   reconhecidas e o texto de estado ("Reconhecido"/"Normalizado, não reconhecido") como
   hoje.
4. "Silenciar" continua global e independente da página.
5. Testes: (a) reconhecer o único alarme em Sinóptico esconde a faixa; (b) o mesmo alarme
   reconhecido segue visível em Eventos com o botão desabilitado; (c) trocar de página
   com alarme reconhecido latched alterna a visibilidade; (d) alarme que limpa pelo
   deadband some das duas listas; (e) `AlarmMoreText` conta só as linhas da lista
   exibida.

Arquivos: `ShellViewModel.cs` (propriedades e notificações), `MainWindow.xaml` (binding
da faixa e das listas), `tests/…/ShellViewModelAlarmTests` (novos casos).

### I. Aceite automático × paradas para revisão por falha de sequência (observado em 2026-09-11)

**O que aconteceu.** Com `AutoAcceptRuns = true`, o ensaio "Rushton-Smith" parou em
"Revisando" às 10:31 local. Não foi o aceite: os 63 pontos capturados foram aceitos
automaticamente (`RunCaptured` → `RunAccepted`, 63/63). A parada foi
`RunStoppedForReview: "Tempo limite de estabilização da vazão no alívio excedido."` na
primeira condição com gás (`N0100_Q02p00_Rep01`): setpoint 2,00 L/min enviado e confirmado
(`{"flowSetpoint":2.0,"valve_2":1,"v_Flow":0}` às 10:22:50), vazão desceu de 5,45 para
3,36 L/min e **ficou travada em 3,36 por 7 min** — 487 amostras em `VentStabilizing`,
zero dentro de 2,00 ± 0,10 — até `MaxVentStabilizationSeconds` (500 s). As tentativas
seguintes a 2,00 L/min estabilizaram normalmente. Causa provável na bancada: a válvula
proporcional/controlador de vazão não regulou para baixo na primeira abertura após o
safe-stop (posição inicial ou integrador saturado). Não é defeito do app.

**Por que o aceite automático não evita isso.** `AutoAcceptRuns` só é consultado em
`FinishCapturedRun` ([PowerTestRunner.cs:1141](../../src/OpenTECHub/Services/PowerTesting/PowerTestRunner.cs#L1141)),
i.e. após captura bem-sucedida. Os cinco gatilhos de `StopForReview`
([:1193](../../src/OpenTECHub/Services/PowerTesting/PowerTestRunner.cs#L1193)) — timeout
de confirmação da válvula de alívio (10 s), timeout de estabilização no alívio, timeout
de confirmação da válvula do reator (10 s), timeout de rotação, limite de torque/rotação
— vão sempre para `Reviewing`. É a decisão correta: uma corrida que não capturou não
tem o que aceitar.

**Achado colateral — custo da estabilização no alívio.** As 55 estabilizações levaram
~150 s cada e **todas** convergiram em +0,07 a +0,10 L/min acima do alvo (2,07; 4,10;
6,07; 8,08; 10,10; 12,08…), na borda da tolerância de 0,10 L/min. O controlador tem
offset de regime de ~+0,08 L/min e demora até a leitura "cair" para dentro da banda.
Total: ~2,3 h de um ensaio de ~9 h esperando o alívio.

Mudanças propostas:

1. **Política de falha de sequência em modo autônomo** (`PowerTestSettings`, novo campo
   `UnattendedFailurePolicy`: `StopForReview` (padrão, comportamento atual) |
   `RetryThenSkip`): com `AutoAcceptRuns`, um timeout de alívio/válvula/rotação faz
   `SafeParkAndRelease`, registra `RunRejected` com o motivo, tenta mais **uma** vez a
   mesma condição e, se falhar de novo, marca a condição `Skipped` com motivo e segue.
   Limite de torque/rotação continua parando sempre (é segurança, não sequência).
   Expor no `CaptureSettingsDialog` ao lado de "Aceite automático", com o texto
   explicando o que ele **não** cobre.
2. **Tolerância do alívio**: subir o padrão de `VentFlowToleranceLpm` para 0,20 L/min
   ou, melhor, aceitar `max(0,15 L/min, 5 % do alvo)`; e/ou trocar o critério para
   **estabilidade** (desvio-padrão das últimas N amostras < 0,05 L/min e |erro| < 0,3)
   em vez de |erro| < tolerância — o que importa é que a vazão esteja estável antes de
   comutar para o reator, porque o valor final é lido de novo no reator. Mostrar no
   status a estimativa "estabilizando há X s · offset +0,08".
3. **Texto do diálogo** "Aceite automático dos pontos": acrescentar "Falhas de sequência
   (alívio, válvulas, rotação, limites) continuam parando para revisão".
4. Item de bancada, fora do app: investigar por que o controlador de vazão não regula
   para baixo na primeira abertura após `FlowSafeStop` (`v_Flow = 1`) — reproduzir
   manualmente: safe-stop → setpoint 2,0 no alívio → observar se trava em ~3,4 L/min.

### J. Corrida sem captura pôde ser aceita — ponto falso com P = 0 (ensaio IsojetB-Combijet, 2026-09-11)

**O que aconteceu.** Com `MaxVentStabilizationSeconds = 120`, três estabilizações no
alívio a 200 rpm expiraram (Q2: 2,18 aos 120 s; Q4: 4,33; Q6: 6,46) e foram para
revisão com **zero amostras**. Em duas delas (`N0200_Q02p00_Rep01`, `N0200_Q04p00_Rep01`)
o operador clicou "Aceitar": as corridas ficaram `Accepted` com `sampleCount = 0`,
`netPowerW = 0`, entraram em `resumo-resultados.csv`, e as condições foram marcadas
`Completed` — a sequência pularia por cima delas. Recuperação manual: "Alternar Aceite"
nas duas linhas (vira `Rejected`, `AcceptedReplicates` = 0, o runner as refaz) e
"Rejeitar" na terceira, ainda em revisão.

**Defeito.** `PowerTestRunner.AcceptRunAsync`
([PowerTestRunner.cs:493](../../src/OpenTECHub/Services/PowerTesting/PowerTestRunner.cs#L493))
só verifica a fase `Reviewing`; `StopForReview`
([:1193](../../src/OpenTECHub/Services/PowerTesting/PowerTestRunner.cs#L1193)) entra em
revisão mesmo com `_capture.SampleCount == 0`. O drawer de revisão oferece "Aceitar" sem
condição, e `ToggleAcceptSelectedRow`
([PowerTestViewModel.cs:2180](../../src/OpenTECHub/ViewModels/PowerTestViewModel.cs#L2180))
também aceita qualquer linha.

Mudanças:

1. `AcceptRunAsync` e `AcceptRunCore`: recusar (`InvalidOperationException` com
   "Corrida sem captura: repita ou rejeite") quando `SampleCount == 0` ou `NetPowerW`
   não finito. `ToggleAcceptSelectedRow`: mesma regra ao ir para `Accepted`.
2. Drawer de revisão: `CanAcceptRun => SampleCount > 0`; com n = 0 o drawer **não é uma
   revisão de resultado** — título "Corrida não realizada", texto "Sem captura —
   {motivo}", sem os campos de P/IC95/Np, e só "Repetir" e "Rejeitar". Confirmado com
   o operador em 2026-09-11: "não deve aparecer para eu aceitar nada, pois não se
   conseguiu fazer o teste". Em modo autônomo, aplicar a política da §I.1 (repetir uma
   vez, depois pular com motivo) sem parar.
3. `UpdateResultsSummary` e os gráficos já filtram P ≤ 0; manter, mas o resumo deve
   marcar explicitamente linhas sem captura se algum manifesto antigo as trouxer.
4. Validação ao carregar um ensaio: corrida `Accepted` com `sampleCount = 0` é
   rebaixada para `Rejected` com motivo "sem captura (migração)" e a condição reaberta;
   registrar no `eventos.jsonl`.

**Comportamento do controlador de vazão no alívio (dado de bancada, para a §I.2 e o
firmware).** Ao abrir o alívio, a vazão dispara para ~2,3× o alvo e decai
exponencialmente com τ ≈ 45 s: alvo 2 → 4,6 inicial; alvo 4 → 9,5; alvo 6 → 13,8.
Tempo até entrar em ±0,2 L/min: ~110 s (2), ~150 s (4), ~170 s (6); a 12 L/min no
Rushton, ~150 s com ±0,1. Consequências: (a) o padrão de `MaxVentStabilizationSeconds`
deve ser ≥ 300 s, e o diálogo deve avisar quando o operador põe menos que ~3τ;
(b) o critério de estabilidade por derivada (§I.2) reconheceria a convergência antes de
o valor cruzar a banda; (c) para o firmware: o overshoot na abertura sugere válvula
abrindo totalmente antes de o PI atuar (falta de pré-posicionamento/anti-windup) —
vale um item no `FLOWMETER_V05_HUB_V7_SYNC_PLAN.md`.

### K. "Critérios de parada e opções" não persiste ao fechar (ensaio IsojetB-Combijet, 2026-09-11)

**O que aconteceu.** O operador criou o ensaio às 15:38, ajustou os critérios na janela,
fechou o app normalmente às 15:55 (`=== OpenTEC-Hub exiting ===`, sem crash) e reabriu
às 16:06. O ensaio voltou com `MaxVentStabilizationSeconds = 120` e
`VentFlowToleranceLpm = 0,2` — os padrões de fábrica
([PowerTestModels.cs:274](../../src/OpenTECHub/Services/PowerTesting/PowerTestModels.cs#L274)–277)
— e as três estabilizações a 200 rpm expiraram (§J). No Rushton os 500 s "pegaram" por
acaso: uma edição posterior na tabela de condições chamou `PersistConditionPlan`, que
arrasta `BuildEditedSettings()` junto.

**Defeito.** `CaptureSettingsDialog` só edita propriedades do `PowerTestViewModel`; o
botão de fechar ([CaptureSettingsDialog.xaml.cs:24](../../src/OpenTECHub/Views/Dialogs/CaptureSettingsDialog.xaml.cs#L24))
faz `DialogResult = true; Close()` e nada mais. Os valores só chegam a
`CurrentTest.Settings` por `TryPersist` ("Salvar setup", "Iniciar/continuar",
"Pular/repor") ou por `PersistConditionPlan` (edição de célula). Fechar a janela e
depois o app perde tudo; fechar a janela durante um ensaio em curso também não aplica
nada, embora o runner leia `_currentTest.Settings` a cada fase e aceitaria o novo valor.

Mudanças:

1. `IDialogService.ShowCaptureSettings` devolve `bool`; ao fechar com OK o ViewModel
   chama um novo `PersistCaptureSettings()`: `BuildEditedSettings()`, se diferente
   `SettingsRevision++`, `CurrentTest.Settings = …`, `SaveTestManifest`, mensagem
   "Critérios salvos (revisão N)". Funciona com o ensaio parado **ou em curso** (só
   `Settings`, sem tocar em condições/geometria, que continuam protegidas por
   `CanEditPlan`). O `X` da janela pergunta "Descartar alterações?" se houver diff.
2. `SettingsRevision` já é gravado em cada linha de `serie-global.csv`, então a
   mudança no meio do ensaio fica rastreável; registrar também um evento
   `SettingsChanged` em `eventos.jsonl` com o diff (campo, de → para).
3. Padrões: `MaxVentStabilizationSeconds` **120 → 500 s** (pedido do operador;
   coerente com τ ≈ 45 s e overshoot de 2,3× medidos em §J). Manter
   `VentFlowToleranceLpm = 0,2` até a §I.2 trocar o critério.
4. Ao sair do app com setup não persistido (`BuildEditedSettings() !=
   CurrentTest.Settings` ou tabela alterada), perguntar "Salvar setup do ensaio antes de
   sair?" em `MainWindow.OnClosing`, via o mesmo `TryPersist`.
5. Teste: abrir diálogo → alterar `MaxVentStabilizationSeconds` → fechar → recarregar
   o ensaio do disco → valor persistido e `SettingsRevision` incrementada; o mesmo com
   o ensaio em `Running`, verificando que a próxima `VentStabilizing` usa o novo limite.

### L. Envio da curva de calibração do fluxômetro — correções no hub (2026-09-11, fora do app)

Duas correções feitas no `ESP32S3-HUB` (ainda não gravadas no S3 no momento do registro):
(1) `handleUSBCommands()` passa a acumular a linha serial até o `\n` — o leitor antigo
processava o que houvesse no buffer e o comando de calibração (~300 B, vários pacotes USB)
chegava fragmentado e era descartado, deixando o app em "aguardando ack";
(2) `a1`/`b1` (termos x⁴/x³ do segmento baixo) deixam de ser descartados em
`queueReliableFlowCommandFromJson`/`buildFlowCommandLocked`, o que fazia o fluxômetro
interpretar a curva baixa como quadrática.

Conferido contra o app e o firmware do fluxômetro V10: o app já termina frames com `\n`
(`SerialTransport.cs:307`), usa `InvariantCulture` e envia a curva em um frame (< 1024 B);
o fluxômetro parseia `a1`/`b1` e só os zera quando chega `k1/f1/c1` sem eles. **Nenhuma
mudança no app é necessária para o envio funcionar.** Itens adicionais recomendados:

1. Hub: após o descarte por > 1024 B sem `\n`, ignorar até o próximo fim de linha (flag
   `discarding`) em vez de seguir acumulando a cauda.
2. Hub: `HUB_FIRMWARE_VERSION` `10.0.0-dev` → `10.0.1-dev` (`Config.h`), porque o app
   grava `hubFirmwareVersion` nos manifestos dos ensaios; ajustar o cabeçalho de
   `WIRE_CONTRACT_V9.md`.
3. Hub: `tests/contracts/test_usb_line_framing.py` (frame de 300 B em pedaços de 64;
   duas linhas num lote; `\r\n`; > 1024 descartado) e um caso em `test_json_keys.py`
   com frame real de calibração (`-1.2E-05`).
4. Docs: `WIRE_CONTRACT_V9.md` (serial em linhas `\n`, ≤ 1024 B; `a1`/`b1` repassados na
   ordem `a1,b1,k1,f1,c1,k2,f2,c2`), `FLUXOMETRO_COMUNICACAO.md` (item 2026-09-11) e, no
   app, `docs/PROTOCOL.md` §3.2 (tabela e exemplo sem `a1`/`b1`).
5. App (opcional): `FlowCalibrationViewModel` — aviso após ~15 s sem ack ("o Hub
   continuará reenviando até o link voltar") e reabilitar "Salvar e enviar curva"; a
   retenção até o ack no hub permanece.
6. Validação após gravar: hub loga `Flow command queued … "a1":…,"b1":…`; serial do
   fluxômetro mostra `[HubCmd] Applied cmd_id=N` e `Params Saved.`; app sai de
   "aguardando" em ~1 s; vazão reportada ≤ 1 L/min confere com os pontos certificados.

### M. Alterações do operador em 2026-09-11 (working tree, não commitadas) — revisão

Build limpo (0 avisos); testes das áreas tocadas 30/30; suíte completa 1138/1139 — a
falha `ShellChromeTests.NavDrawer_in_compact_mode_overlays_from_left_edge_covering_rail`
(rail em X = 8, esperado 0) é pré-existente e sem relação com o diff.

1. **`CrashReporter.IsShutdownCrtUnloadException` + supressão em
   `App.OnDomainUnhandledException` e `GenerateAndSaveReport` — aprovado.** Casa
   exatamente com `Logs/Crash/crash_20260911_103551` (`__std_type_info_destroy_list →
   __scrt_uninitialize_type_info → _app_exit_callback → SingletonDomainUnload`). Exige
   `DllNotFoundException` **e** frames de teardown, então um `DllNotFoundException`
   real continua gerando relatório. Pendências: registrar como decisão em
   `DECISIONS.md` (bug conhecido do C++/CLI do WPF — `DirectWriteForwarder` — com o
   `vcruntime` já descarregado, provocado pela cópia nativa do SkiaSharp/ScottPlot) e
   citar o id do relatório suprimido; manter o teste com `StackTrace` sobrescrito.
2. **`IPowerTestRunner.RunStarted` + limpeza de `LivePoints` — aprovado com
   simplificação.** Manter `RunStarted` como fonte única e o `Clear` na fase
   `PreparingNextRun`; remover as comparações de `RunId` em `UpdateRunnerState` e
   `OnDataPointAdded` e o `LivePoints.Clear()` em `StartOrContinueAsync` (apaga o gráfico
   antes de `StartTestAsync`, que pode recusar no preflight). Decidir se
   `StartBothSubphase2` deve disparar `RunStarted` (apaga o gráfico entre P0 e PG da
   mesma corrida) — se a intenção é ver as duas subfases juntas, não disparar ali.
   Compatível com a §A. O BOM removido de `PowerTestViewModel.cs` está conforme o
   `.editorconfig` (`charset = utf-8`).
3. **`RestoreDirectory = true` nos `SaveFileDialog` — aprovado.** Falta em
   `FileInteractionService.ChooseSavePath`/`ChooseOpenPath`
   ([FileInteractionService.cs:18](../../src/OpenTECHub/Services/Platform/FileInteractionService.cs#L18),
   [:35](../../src/OpenTECHub/Services/Platform/FileInteractionService.cs#L35)); e as
   três views (`PowerView`, `SynopticView`, `PowerImpellerComparisonView`) deveriam usar
   esse serviço em vez de instanciar o diálogo à mão.
4. Commitar em três commits separados (crash reporter; gráfico por corrida; diálogos),
   cada um com o teste correspondente, antes de começar a §A/§B para não misturar.
5. Os outros dois relatórios em `Logs/Crash/` (`crash_20260909_233817`,
   `crash_20260910_000426`) **não** são o caso do item 1: são `XamlParseException` de
   `StaticResource` (linha 132 de `CaptureSettingsDialog.xaml` e linha 1053 de
   `MainWindow.xaml`) em builds de desenvolvimento de 09–10/09, corrigidos em `f08ba27`
   ("corrigir o dialogo de criterios"). Conferido em 2026-09-11: os 16 `StaticResource`
   dos dois arquivos resolvem. Nada a fazer além de não confundir com o item 1.

### N. Navegar na tabela de condições durante o ensaio (pedido de 2026-09-11)

**Pedido.** Com o ensaio rodando, poder rolar e selecionar linhas da tabela "Condições
do ensaio" para ver em que ponto a sequência está — sem editar valores.

**Hoje.** O card inteiro e o `DataGrid` estão em `IsEnabled="{Binding CanEditPlan}"`
([PowerView.xaml:405](../../src/OpenTECHub/Views/PowerView.xaml#L405)); com o ensaio
em curso `CanEditPlan` é falso e o grid fica cinza: sem rolagem, sem seleção, sem
tooltip. Além disso `RefreshConditionRows` faz `Conditions.Clear()` a cada quadro (§2.1),
o que zeraria a rolagem e a seleção mesmo com o grid habilitado — **§A.2 é pré-requisito**.

Mudanças:

1. `PowerView.xaml`: tirar `IsEnabled` do `DataGrid` de condições; ligar `IsReadOnly`
   a `{Binding CanEditPlan, Converter=InverseBool}`. Os botões `+ − ↑ ↓ Pular/repor`,
   "Gerar tabela" e "Limpar tabela" continuam em `IsEnabled="{Binding CanEditPlan}"`.
   Manter `SelectedItem="{Binding SelectedCondition}"` — com `CanEditPlan` falso, a
   seleção não dispara persistência (`OnConditionPropertyChanged` já sai cedo).
2. **Seguir a condição em curso**: propriedade `CurrentConditionId` no ViewModel
   (do `_runner.CurrentCondition`, atualizada na parte "por amostra" de
   `UpdateRunnerState`) e, no code-behind de `PowerView`, ao mudar
   `CurrentConditionId`, `ConditionsGrid.ScrollIntoView(row)` **só se o operador não
   rolou manualmente nos últimos ~5 s** (guardar o `ScrollChanged` do grid com carimbo
   de tempo). Não mexer em `SelectedCondition` automaticamente — seleção é do operador.
3. Destaque visual da linha em curso: `DataGrid.RowStyle` com `DataTrigger` em
   `Status == InProgress` (fundo `AccentSubtleBrush`/negrito), além do texto "Em curso"
   que já existe na coluna Status.
4. Com §A.2 (upsert por `ConditionId`), a rolagem e a seleção sobrevivem aos quadros.
5. Testes: `PowerTestViewModel` — durante `IsRunning`, `SelectedCondition` muda sem
   `PersistConditionPlan`; `CurrentConditionId` acompanha `runner.CurrentCondition`.
   Manual: rolar a tabela durante uma captura, selecionar linhas, confirmar que nada é
   salvo (`SettingsRevision` inalterada) e que a linha "Em curso" é seguida quando o
   operador não está rolando.

### O. Editar a tensão na tabela de calibração do fluxômetro (pedido de 2026-09-11)

**Pedido.** Na página Calibrações → vazão, além da vazão certificada, poder digitar a
tensão (`FlowVoltage`) de cada ponto, e não só capturá-la da telemetria.

**Hoje.** `FlowCalibrationPointViewModel` tem `FlowText` editável e `Voltage`
(`double?`) só de leitura na UI: o template mostra `VoltageText` num `TextBlock`
([CalibrationView.xaml:553](../../src/OpenTECHub/Views/CalibrationView.xaml#L553)); a
tensão só entra por `CaptureVoltage` (média de N quadros → `SelectedPoint.Voltage`,
[FlowCalibrationViewModel.cs:396](../../src/OpenTECHub/ViewModels/FlowCalibrationViewModel.cs#L396)).
`TryReadPoint` exige `Voltage` finita e ≥ 0; `PersistPoints` grava só pontos válidos em
`settings.Calibration.FlowCalibrationPoints`.

Mudanças:

1. `FlowCalibrationPointViewModel`: acrescentar `VoltageText` **editável** (string,
   `[ObservableProperty]`), espelhado com `Voltage`: setar `Voltage` (captura) formata
   `VoltageText` com `F6` invariante-tolerante; editar `VoltageText` parseia com
   `TryParseDouble` (aceita vírgula) e atualiza `Voltage` (ou `null` se inválido/vazio).
   Evitar recursão com um flag `_syncing`. Manter `HasVoltage`.
2. `CalibrationView.xaml`: trocar o `TextBlock` da tensão por `TextBox` com o mesmo
   estilo do de vazão (`Height=30`, `Padding=8,0`), `Text="{Binding VoltageText,
   UpdateSourceTrigger=PropertyChanged}"`, tooltip "Tensão medida (V); capturar da
   telemetria ou digitar". Placeholder/`—` quando vazio.
3. `OnPointChanged`: já reage a `Voltage`; com o espelhamento, editar `VoltageText`
   recalcula a curva e o estado dos comandos. Validação: tensão fora de `[0, 3.3]` V
   marca a linha (borda de erro) e exclui do ajuste; mensagem em `StatusText`.
4. Ordenação e persistência: `PersistPoints` continua gravando só pontos válidos,
   ordenados por vazão; manter. `ResetToFactory`/carregar do settings passam pelo
   construtor (`voltage:`), que já preenche o texto.
5. Registrar a origem do valor: `FlowCalibrationPoint` ganha `Source`
   (`Captured | Typed`) opcional no settings (default `Captured` para arquivos antigos)
   e a linha mostra um ícone/sufixo discreto quando digitada — para o relatório de
   calibração distinguir medição de transcrição.
6. Testes: parse de `VoltageText` (ponto/vírgula/vazio/inválido); captura sobrescreve
   texto; texto inválido → `TryReadPoint` null e curva sem o ponto; round-trip pelo
   settings preserva `Source`.

---

## 5. Verificação

1. **Instrumentação antes/depois**: um `UiHitchMonitor` só em `DEBUG` — `DispatcherTimer`
   em `DispatcherPriority.Input` com intervalo de 50 ms que registra (Serilog, nível
   Debug) toda vez que o tick atrasa > 30 ms, com a duração. Rodar um ensaio simulado
   contra `OpenTECHub.Simulator` (potência e kLa) por 10 min e contar os atrasos.
   Meta: zero atrasos > 30 ms durante a captura; nenhum atraso em mudança de fase.
2. **Equivalência de arquivos**: correr o mesmo ensaio simulado antes e depois de B e
   comparar `dados-brutos.csv`, `serie-global.csv`, `eventos.jsonl`,
   `resumo-resultados.csv` (iguais byte a byte, exceto carimbos) e `ensaio.json` (igual
   após remover `tare.samples`).
3. **Testes**:
   - `PowerTestStore`/`KlaTestStore`: ordem de escrita preservada entre appends e
     saves; `FlushAsync` drena; SHA-256 calculado após os appends; falha de escrita
     reportada sem derrubar o runner.
   - `PowerTestViewModel`: durante `DataPointAdded`/`StateChanged` repetidos, nenhuma
     ação `Reset` em `Conditions`/`Results`; `SelectedCondition`/`SelectedResultRow`
     preservados.
   - Migração: um manifesto com `tare.samples` embutido carrega, extrai para o arquivo
     lateral e continua carregando depois.
   - Views: timer de redesenho parado quando `IsVisible == false`.
4. **Manual**: passar o mouse e editar a tabela do plano durante uma captura; trocar de
   página durante a captura; aceitar uma corrida de kLa com 20+ corridas no ensaio e
   confirmar que a janela não trava.

---

## 6. Ordem, esforço e risco

Na ordem de execução da §0.3. "Depende de" é bloqueio real, não preferência.

| # | Etapa | Arquivos principais | Esforço | Risco | Depende de |
|---|---|---|---|---|---|
| 1 | M.4 — commits do operador | os 11 arquivos do diff atual | baixo | nenhum | — |
| 2 | K — persistir critérios ao fechar; padrão 500 s | `CaptureSettingsDialog.xaml.cs`, `IDialogService`/`DialogService.cs`, `PowerTestViewModel.cs`, `PowerTestModels.cs`, `MainWindow.xaml.cs` (OnClosing) | baixo | baixo | — |
| 3 | J — recusar aceite sem captura; drawer "Corrida não realizada" | `PowerTestRunner.cs`, `PowerTestViewModel.cs`, `PowerView.xaml`, `PowerTestStore.cs` (migração ao carregar) | baixo-médio | baixo | — |
| 4 | A — grades sem `Reset`; `StateChanged` 1×/quadro | `PowerTestViewModel.cs`, `PowerTestRunner.cs` | médio | baixo — comportamento visível idêntico | M.4 |
| 5 | N — tabela navegável durante o ensaio | `PowerView.xaml`, `PowerView.xaml.cs`, `PowerTestViewModel.cs` | baixo | baixo | A |
| 6 | B — I/O dos ensaios em fila de segundo plano; tara lateral | `PowerTestStore.cs`, `KlaTestStore.cs`, `IPowerTestStore.cs`, `IKlaTestStore.cs`, novo `Services/Persistence/BackgroundFileWriter.cs`, `PowerTestFileContracts.cs` | alto | médio — ordenação/flush; migração do manifesto | — |
| 7 | C — gráficos só visíveis e só quando mudou; `DataLogger` | `PowerView.xaml.cs`, `KlaDeterminationView.xaml.cs`, `SynopticView.xaml.cs`, `DeferredPageHost.cs` | baixo-médio | baixo | — |
| 8 | D — telemetria em `DispatcherPriority.Background` | `DeviceService.cs`, `ARCHITECTURE.md` | baixo | baixo — validar watchdogs de 1 s | — |
| 9 | E — kLa: recompute por tick; cache de análises | `KlaDeterminationViewModel.cs`, `KlaTestRunner.cs` | baixo-médio | baixo | B (para o aceite) |
| 10 | I — política autônoma; tolerância/critério do alívio | `PowerTestModels.cs`, `PowerTestRunner.cs`, `CaptureSettingsDialog.xaml` | médio | médio — muda a máquina de estados; testar no simulador | J |
| 11 | G — Duplicar / Reabrir ensaio | `PowerTestViewModel.cs`, `PowerTestStore.cs`, `PowerView.xaml` | baixo-médio | baixo | — |
| 12 | H — faixa de alarmes por página | `ShellViewModel.cs`, `MainWindow.xaml` | baixo | baixo | — |
| 13 | O — tensão editável na calibração de vazão | `FlowCalibrationViewModel.cs`, `CalibrationView.xaml`, `AppSettings.cs` | baixo | baixo | — |
| 14 | F — SessionLogger, Serilog async, chip "Malha de gás", `pHCal` repetido | `SessionLogger.cs`, `App.xaml.cs`, `PowerTestViewModel.cs` | baixo | baixo | — |
| — | L — hub: flag `discarding`, bump de versão, teste de contrato, docs | `ESP32S3-HUB/…/Commands.h`, `Config.h`, `tests/contracts/`, docs | baixo | baixo | firmware gravado |

Resultado esperado ao fim de A–D: custo por quadro na thread da UI de dezenas–centenas
de ms para ~1–2 ms; engasgo de mudança de fase eliminado; grades de plano e resultado
deixam de resetar sob o mouse; kLa deixa de engasgar no aceite da corrida. Ao fim de
J–K–I: um ensaio autônomo não para mais por timeout de alívio recuperável, nunca aceita
ponto sem captura, e os critérios digitados sobrevivem ao fechar o app. Decisões a
registrar em `DECISIONS.md`: D-048 (I/O de ensaio sempre fora da thread da UI, por fila
única ordenada), D-049 (supressão do `DllNotFoundException` de teardown do CRT), D-050
(corrida sem captura nunca é aceitável; falha de sequência não é revisão de resultado).
