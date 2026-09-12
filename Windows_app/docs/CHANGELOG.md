# Changelog

All notable changes to OpenTEC-Hub. Version numbers follow
[Semantic Versioning](https://semver.org/); the single source for the number is
`<Version>` in `Directory.Build.props`.

---

## [Unreleased]

### Fixed — bancada de 11/09/2026 (plano `docs/plans/2026-09-11-plano-correcao-engasgos-ui-ensaios.md`)
- **Tensão editável na tabela de calibração do fluxômetro (§O).** Cada ponto só recebia a tensão
  por captura da telemetria (média de N quadros); a coluna era um `TextBlock`. Passa a ser um campo
  editável espelhado com `Voltage` (captura formata, digitar parseia com ponto ou vírgula; vazio ou
  inválido = sem tensão). Fora de 0–3,3 V a linha ganha borda de erro, sai do ajuste e o status
  avisa. Cada ponto registra a origem (`FlowCalibrationPoint.Source`: `Captured` | `Typed`,
  arquivos antigos leem `Captured`) e a linha mostra "digitada" quando transcrita — para o
  relatório de calibração distinguir medição de transcrição.
- **Faixa de alarmes some ao reconhecer — exceto em Eventos (§H).** A faixa ficava visível
  enquanto qualquer alarme estivesse *latched*, e uma linha reconhecida continuava lá, com o botão
  cinza, até a condição limpar pelo deadband: reconhecer não escondia nada em página nenhuma.
  Agora cada *Reconhecer* tira a sua linha da faixa, a manchete seguinte sobe, e a faixa fecha
  quando não sobra nada por reconhecer. Em **Eventos** — a página de auditoria — a faixa fica
  enquanto houver alarme latched, reconhecido ou não, com o botão desabilitado nas linhas já
  reconhecidas. O `+N` e a lista expandida contam só as linhas da página atual. Só apresentação
  (`AlarmBannerPresenter`, puro): `AlarmService`, latch, deadband e journal não mudam; *Silenciar*
  continua global.
- **Ensaio concluído deixa de ser um beco sem saída (§G).** Nada tirava um ensaio de `Completed`
  — nem o app, nem o recarregamento — e Montagem, Aquisição, *Salvar setup* e *Iniciar* ficavam
  desabilitados; só *Novo* saía do estado, com geometria padrão e obrigando a refazer montagem,
  tara e tabela. Dois botões novos no card do ensaio: **Duplicar** cria um ensaio com o mesmo
  fluido, geometria, calibração, tara, critérios e tabela (ids novos, contadores zerados, sem
  pontos, sem flooding) e grava `duplicatedFrom` no manifesto e `TestDuplicated` no journal;
  **Reabrir** volta um `Completed` a `Interrompido` (confirmação, `CompletedUtc = null`,
  `InterruptionReason = "Reaberto pelo operador"`, `TestReopened`), mantendo os pontos aceitos, e
  *Iniciar/continuar* segue o caminho normal. O chip de status passa a dizer *Concluído — somente
  leitura. Use Novo, Duplicar ou Reabrir.*
- **Política de falha de sequência em modo autônomo e critério de estabilidade do alívio (§I).**
  No Rushton-Smith um único tempo limite de alívio na primeira condição com gás parou um ensaio
  autônomo de 9 h em "Revisando" — o aceite automático só é consultado após uma captura. Novo
  `PowerTestSettings.UnattendedFailurePolicy`: `StopForReview` (padrão, comportamento anterior) ou
  `RetryThenSkip` — com aceite automático, um tempo limite de alívio, válvula ou rotação rejeita a
  corrida com o motivo, repete a condição **uma** vez e, se falhar de novo, marca a condição como
  pulada e segue; limite de torque/rotação continua parando sempre. Nada aqui aceita ponto (D-050).
  No diálogo, a caixa fica abaixo de *Aceite automático* e só habilita com ele. E as 55
  estabilizações do Rushton convergiram em +0,07…+0,10 L/min, na borda da banda, gastando ~150 s
  cada (~2,3 h do ensaio) esperando a leitura "cair" para dentro: o alívio passa a ter uma segunda
  saída, por **estabilidade** — desvio-padrão das últimas N leituras ≤ `VentFlowStabilityStdDevLpm`
  (0,05) e |média − alvo| ≤ `VentFlowStabilityMaxErrorLpm` (0,3) — porque o que importa é a vazão
  ter assentado; ela é medida de novo no reator. O status mostra "estabilizando há X s · offset
  +0,08 · σ 0,008". Item de bancada, fora do app: o controlador de vazão não regula para baixo na
  primeira abertura após `FlowSafeStop` (travou em 3,36 L/min por 7 min com alvo 2,00).
- **kLa: séries de diagnóstico a cada 5 pontos, análises em cache, matriz no lugar (§E).**
  `OnDataPointAdded` fazia `LivePoints.Where(Reoxygenating).ToList()` sobre todos os pontos e
  recalculava a suavização O(n·janela) e a OLS a **cada quadro**; passa a guardar o índice do
  primeiro ponto de reoxigenação e a recomputar a cada 5 pontos (a revisão recomputa exato).
  `RefreshConditionsList` relia **todos** os `analise.json` do disco a cada aceite — centenas de
  ms com 20–40 corridas; as análises ficam em cache válido apenas para a instância do sumário
  que as originou (o runner substitui o sumário ao mudar o desfecho; um recarregamento cria
  instâncias novas), e `MatrixRows` é atualizada no lugar por (condição, réplica) em vez de
  `Clear()`. O recálculo síncrono da revisão foi mantido: é uma vez por corrida e os testes
  dependem dele ser síncrono.
- **Gráficos só redesenham visíveis e só quando mudaram (§C).** `DeferredPageHost` esconde uma
  página colapsando-a e nunca a descarrega, então o timer iniciado em `Loaded` seguia redesenhando
  gráficos invisíveis a sessão inteira — até nove por segundo numa sessão que visitou Sinóptico,
  Potência e kLa. `Controls/VisibleRedrawTimer` roda só com a página carregada **e** visível,
  em `DispatcherPriority.Background`, e só desenha quando alguém marcou os dados como alterados
  (coleções, propriedades `Review*`/`Setting*`, tema). Ao voltar a uma página o redesenho é
  imediato. O gráfico ao vivo da Potência (torque/rotação) e o de OD do kLa (bruto/filtrado) viram
  `DataLogger`s alimentados ponto a ponto, em vez de `Clear()` + cópia de até 6000 pontos por
  segundo; no kLa só as sobreposições da revisão são reconstruídas.
- **Telemetria despachada em `DispatcherPriority.Background` (§D).** Estava em `DataBind`, acima
  de `Input` e `Render`: o quadro era processado antes de qualquer clique ou hover pendente.
  `StateChanged`/`CommandSent` continuam em `Normal`; linhas brutas e de log do mesmo quadro vão
  num único item de despacho.
- **O I/O dos ensaios saiu da thread da UI (§B, D-048).** Dois `File.AppendAllText` por quadro e,
  a cada mudança de fase, a reescrita de um `ensaio.json` de 832 KB (55 ms médios, picos de
  330 ms) rodavam na thread da UI. `PowerTestStore` e `KlaTestStore` passam a formatar/serializar no
  chamador e a enfileirar a escrita num `BackgroundFileWriter` — um consumidor, ordem de
  enfileiramento, leituras drenam antes, `FlushAsync()` nas interfaces. O SHA-256 do dado bruto é
  selado de um hash incremental alimentado com os mesmos bytes, sem reler o arquivo. `ensaio.json`
  e `tara.json` deixam de embutir `tare.samples` (que já vivem em `Taras-Brutas/`): 832 KB →
  ~120 KB, com migração única ao carregar. Falha de escrita marca o ensaio como "⚠ Gravação
  comprometida" sem pará-lo. Um teste roda o mesmo ensaio pelos dois caminhos e exige arquivos
  idênticos.
- **As grades da página de Potência deixaram de ser reconstruídas a cada quadro (§A).** Cada
  quadro de telemetria (~1 Hz) fazia `Conditions.Clear()` + 42 clones e `Results.Clear()` + 41
  linhas — **duas vezes**, porque `DataPointAdded` e `StateChanged` chamavam a mesma rotina — e o
  `DataGrid` recebia um `Reset` que destruía os contêineres de linha, o hover, a rolagem e a
  seleção duas vezes por segundo. `UpdateRunnerState` foi dividido em uma parte **por amostra**
  (flags, rótulos, IC95, progresso, chip de gás) e uma parte **estrutural** (grades, estado do
  documento, pré-voo) que só roda quando muda uma chave: fase, corrida, réplicas aceitas ou
  concluídas, condições, revisão de ajustes, status. As grades passam a ser atualizadas **no
  lugar**, por `ConditionId`/`RunId` (`PowerCondition.CopyRuntimeStateFrom`; linhas de resultado
  substituídas só quando o conteúdo muda), preservando `SelectedCondition` e `SelectedResultRow`.
  `DetectFlooding` saiu do caminho de refresh e roda ao aceitar uma corrida e ao abrir o ensaio; o
  mapa de kLa vinculado passa a ser lido do disco fora da thread da UI. No runner, um quadro de
  telemetria levanta `StateChanged` **no máximo uma vez**, depois de todas as mutações do quadro.
- **A tabela de condições pode ser navegada durante o ensaio (§N).** O card inteiro ficava em
  `IsEnabled=CanEditPlan`: com o ensaio em curso o grid ficava cinza — sem rolagem, sem seleção,
  sem tooltip. O grid passa a ser **somente leitura** (não desabilitado) enquanto o plano está
  travado; os botões `+ − ↑ ↓ Pular/repor` continuam desabilitados. A linha em curso ganha
  destaque (`AccentSubtleBrush`, negrito) e o grid **acompanha a condição em curso**
  (`CurrentConditionId` → `ScrollIntoView`) sem mexer na seleção — que é do operador — e só se o
  operador não rolou a tabela nos últimos 5 s. Depende da §A: com a grade resetando a cada quadro,
  nem rolagem nem seleção sobreviveriam.
- **Os critérios de parada não sobreviviam ao fechar o aplicativo (§K).** A janela *Critérios de
  Parada e Opções de Captura* só editava campos do ViewModel; os valores só chegavam ao
  `ensaio.json` por *Salvar setup*, *Iniciar* ou por uma edição posterior na tabela. O operador
  ajustou o tempo limite do alívio, fechou o aplicativo normalmente e o ensaio voltou com os
  padrões de fábrica — três estabilizações a 200 rpm expiraram aos 120 s. Agora **Concluir
  persiste**: `SettingsRevision` sobe, o manifesto é regravado e `eventos.jsonl` recebe um
  `SettingsChanged` com o diff campo a campo (`MaxVentStabilizationSeconds: 120 → 500`). Funciona
  com o ensaio parado **ou em curso** — o runner lê `Settings` do mesmo documento a cada fase, então
  a próxima `VentStabilizing` já usa o limite novo. O `X` e `Esc` descartam, perguntando antes se
  houver diferença; ao sair do aplicativo com setup não salvo, a janela principal pergunta
  *Salvar o setup do ensaio antes de sair?*.
- **Corrida sem captura não pode mais ser aceita (§J, D-050).** Um ponto que parou para revisão por
  tempo limite (alívio, válvula, rotação) chegava à faixa de revisão com *Aceitar* disponível; no
  IsojetB-Combijet duas corridas foram aceitas com n = 0 e P = 0 W, entraram no resumo e fecharam
  as condições. O runner recusa o aceite sem captura, a faixa vira **Corrida não realizada — Sem
  captura: {motivo}** só com *Repetir* e *Rejeitar*, *Alternar Aceite* aplica a mesma regra, e ao
  carregar um ensaio antigo as corridas aceitas sem captura são rebaixadas para rejeitadas, as
  condições reabertas e a migração registrada em `eventos.jsonl`.
- **Padrão de `MaxVentStabilizationSeconds`: 120 → 500 s.** Medido em 11/09: ao abrir o alívio a
  vazão sobe a ~2,3× o alvo e decai com τ ≈ 45 s, entrando em ±0,2 L/min só após 110–170 s. O
  diálogo avisa quando o operador põe menos de ~150 s (~3τ), e o texto de *Aceite automático* passa
  a dizer o que ele **não** cobre: falhas de sequência continuam parando para revisão.
- **Tempestade de `XamlParseException` na página de Receitas** (39 relatórios em 4 s às 18:19 de
  11/09): `RecipeConnectionViewModel.ArrowPoints` era um `PointCollection` sem `Freeze()`, e um
  `Freezable` não congelado só pode ser ligado pela thread que o criou — o template do canvas
  falhava a cada `Measure`. Passa a ser sempre congelado, como `RouteGeometry` já era.
- **Relatório de pânico falso ao fechar o aplicativo (D-049).** O `DllNotFoundException` que o
  WPF levanta ao descarregar o `DirectWriteForwarder` depois do `vcruntime` é reconhecido pela
  pilha de teardown e suprimido com um aviso no log; um `DllNotFoundException` real continua gerando
  relatório.
- **Gráfico ao vivo de Potência limpo por corrida.** O runner anuncia `RunStarted` e o ViewModel
  limpa `LivePoints` ali e em `PreparingNextRun` — inclusive na subfase gaseada de uma condição
  *Both*, que é uma corrida própria.
- **Diálogos de arquivo com `RestoreDirectory`.** Escolher uma pasta na exportação deixava de mudar
  o diretório corrente do processo.

### Added
- **Documentação dentro do aplicativo, e páginas sem texto solto (D-047).** Configurações ganha a
  seção **Documentação**, abaixo de *Comandos do equipamento* e com ícone próprio: o manual do
  programa, escrito como dado (`DocumentationCatalog`) e não como marcação. Cada página é descrita
  primeiro pelo seu layout e depois controle a controle, com o nome que aparece na tela; **Painel** e
  **Controle** entram primeiro, e *Controle* cobre linha a linha o detalhe de cada variável e de cada
  dispositivo externo. Os cards da página de Potência deixaram de imprimir a própria explicação e
  ganharam um botão “?” que abre o assunto correspondente. Com o manual aberto, a ilustração do
  biorreator sai e o texto ocupa a página inteira. Ver [DECISIONS D-047](DECISIONS.md).
- **Receitas documentadas em três assuntos (D-047).** `Receitas` traz o conceito, o que dá para
  automatizar, a página parte por parte, montagem, salvamento e JSON, e — o que não pode ser
  descoberto por surpresa — que **iniciar uma receita desativa o controle manual**, porque ela
  reivindica todos os atuadores no árbitro. `Receitas · Blocos` tem um verbete por bloco, incluindo
  o que muda conforme o contexto (um Monitorar Variável no laço da cascata perde o polling; uma
  Intervenção Manual vira a chave Manter Rodando / Sair do Loop). `Receitas · Cascata de O₂` cobre o
  PID, o horizonte de predição, o anti-windup, os quatro modos de atuação e as janelas de saída. Um
  teste exige verbete para cada bloco declarado no catálogo. O manual passa a interpretar
  `**negrito**` inline.

- **As páginas restantes documentadas, com equações (D-047).** Entram **Potência** (P = τ·ω, Np, Re,
  Fl_G, Fr, a fronteira de Nienow e a parada adaptativa de duas portas), **Mapa de Potência**
  (superfície, van't Riet `kLa = K·(P/V)^α·(v_s)^β`, eficiência e escalonamento), **Calibrações**,
  **Históricos**, **Eventos** e um assunto de **Integração** que percorre a cadeia inteira:
  Determinar kLa → Mapeamento kLa → Potência (η = kLa/(P/V)) → Controle de O₂ no método Mapa. O
  manual passa a renderizar equações como equações, cada uma com a legenda que lê os símbolos.
- **Determinar kLa e Mapeamento kLa documentados (D-047).** O ensaio de gassing-out — o método, as
  fases de cada corrida, a matriz de réplicas, a revisão que transforma a curva em número (região
  linear, região C*, R², RMSE, sensibilidade a C*) e o que fica gravado — e o mapeamento, da tabela
  de pontos à trajetória publicada, com o diagnóstico científico campo a campo e o lembrete de que a
  página **nunca envia comandos**.

### Changed
- **“Cascata” passa a nomear só o método (D-047).** O bloco chama-se **Controle de O₂** e tem quatro
  métodos de atuação — Agitação, Aeração, Cascata (percentuais) e Mapa (trajetória kLa). O assunto do
  manual foi renomeado para `Receitas · Controle de O₂` (`receitas-controle-o2`), e um teste recusa
  qualquer texto que volte a chamar o bloco de "bloco Cascata".
- **Card de Revisão e Aceite reorganizado (D-047).** Ceq ganhou linha própria com a caixa **à direita
  do campo** — disputando a coluna com *Recalcular*, ele encolhia os campos de início e fim até
  cortarem "100.0". A barra superior da página perdeu alguns pontos de tipografia e de respiro entre
  botões, que era o que empurrava as leituras do meio ("D Dissolvido", "Tempo Tota"). Em Mapeamento
  kLa, "Ajuste científico" virou **"Ajuste"**, e o estado `Para revisar` virou **`Revisão`**.
- **Cards de kLa que não cabiam na coluna (D-047).** Em Determinar kLa: o botão **Executar
  Sequência** chegava cortado porque o título era declarado antes dele no `DockPanel`; as colunas da
  matriz somavam 318 DIP numa faixa de ~310 e comiam a coluna de ações; e os três pares rótulo/campo
  de **Limiares de Operação** e da inclusão manual deixavam os últimos campos sem largura. Rótulo em
  cima do campo, larguras que somam menos que a coluna, e o botão declarado primeiro. Em Mapeamento
  kLa: os quatro botões da tabela de pontos viraram uma grade 2×2 declarada em vez de quebra de linha
  por acaso, e os três botões de arquivo passaram a duas linhas — "Exportar" chegava cortado pela
  borda do card.
- **O bloco de cascata deixou de escrever por cima das próprias portas (D-047).** O parágrafo que o
  card imprimia quando nada estava ligado à Condição de Saída virou o selo **SAI AO ESTABILIZAR**,
  com a frase na dica de contexto e completa no manual. A altura do card e a posição das portas
  passaram a ter uma origem única — eram duas contas para o mesmo número, e é por isso que crescer o
  conteúdo movia o texto sobre as portas em vez de mover as portas.
- **Página de Potência reorganizada (D-047).** O card `Ensaio` virou dois subcards — o ensaio aberto
  e a tara que ele aplica; **`Abrir` passou a `Carregar Ensaio`**, em linha própria e largura cheia,
  com *Renomear* e *Excluir* na linha seguinte. Corrigidos os textos que a coluna de 340 DIP cortava:
  “Tara do Eixo (Calibração do Sistema)” virou “Tara do eixo” com estado e ação em linhas separadas,
  e as tabelas de correlação elétrica e de patamares da tara trocaram larguras fixas — que somavam
  mais que a própria coluna — por larguras proporcionais com cabeçalhos curtos.
- **Todo dado bruto medido nos ensaios passa a ser gravado (D-046).** Quatro medidas que o
  aplicativo tomava e descartava chegam ao disco: `dados-brutos.csv` e `serie-global.csv` do kLa
  ganham `TemperatureC` e `RpmMeasured` (esquema 2, colunas anexadas ao fim, leitor tolerante ao
  esquema 1) — a temperatura define `C*` e a correção para 20 °C, e a rotação medida é a única
  evidência de que a agitação sustentou a condição relatada; a varredura de tara grava cada leitura
  em `Taras-Brutas/tara-<início>.csv` enquanto corre, de modo que uma varredura cancelada ou que não
  converge deixa de perder tudo o que mediu; e a conferência de ponto único passa a gravar
  `Pontos-Unicos/ponto-<início>_N####_<gás>.csv` com manifesto (rotação e vazão comandadas, `T_nom`),
  inclusive sem nenhum ensaio aberto. Leitura ausente é célula vazia, nunca zero.
  Ver [DECISIONS D-046](DECISIONS.md).
- **Adequação responsiva para notebooks (1024 × 640 DIP).** A navegação entra em modo compacto de
  56 DIP abaixo de 1440 DIP de largura e o menu completo abre como drawer sobreposto, sem empurrar
  a página; `Esc`, clique fora e a troca de destino fecham. Introduzida a propriedade anexada
  `Controls.Responsive` (`NarrowBelow`/`ShortBelow`, publicando `IsNarrow`/`IsShort`), que permite a
  cada página adaptar-se por gatilho de XAML a partir do tamanho real do seu container. Mapeamento
  kLa ganha seletor Dados/Superfície/Diagnóstico abaixo de 1200 DIP; os gráficos de kLa passam a um
  por vez abaixo de 720 DIP de altura, e os de Potência, um por vez abaixo de 900 DIP. Novo
  `DialogBounds` limita cada modal à área do proprietário antes de `ShowDialog`.
  Ver [DECISIONS D-045](DECISIONS.md).
- **Instalador Inno Setup, serviço de Crash Reporting e Manual do Operador (Etapa 1.10 / Fase 6).**
  Criado script de instalação `installer/OpenTECHub_Setup.iss` baseado na referência de `BlocosDeControle.iss`,
  com empacotamento self-contained win-x64 (.NET 10 embutido), leitura dinâmica de versão do binário,
  script automatizado `build_installer.ps1`. Implementado serviço de diagnóstico `CrashReporter` capturando exceções
  não tratadas no `App.xaml.cs` (`Dispatcher`, `AppDomain`, `TaskScheduler`) com dump de memória, métricas de
  processo e dupla contingência de gravação (`{Workspace}/Logs/Crash/` e `%LOCALAPPDATA%\OpenTEC-Hub\CrashDumps\`).
  Redigido manual operacional de campo completo em português brasileiro (`docs/MANUAL_DO_OPERADOR.md`).
  Conclusão de 100% do Eixo 1 (Software Desktop). Ver [DECISIONS D-041](DECISIONS.md).
- **Modelo de potência no simulador do servo.** O caminho real de `motorSetpoint` até
  `ServoRpm`/`ServoTorqueNm`/`ServoPowerW` agora responde com assentamento de primeira ordem,
  soma `rho*Np*N^3*D^5` e tara por estágio e injeta a curva de ruído medida na sessão
  `2026-09-03_1340`. Geometria, `Np`, tara e constantes de tempo são substituíveis nos testes;
  `FlowSetpoint` já produz o transiente gaseificado simples da Fase 1, sem antecipar o joelho de
  flooding reservado à Fase 2.
- **Duas rotas para os ensaios de potência.** `Potência` abre o esqueleto mestre-detalhe de
  aquisição, já ligado ao armazenamento, à telemetria do servo e ao árbitro; `Mapa de Potência`
  nasce como destino estável e placeholder explícito da Fase 3. Ambas ficam após Mapeamento kLa
  em Automação, participam dos atalhos e da paleta e são restauradas por `LastPage`.
- **Saúde estatística compartilhada dos sensores.** Temperatura, pH, oxigênio, vazão,
  biomassa e distância acumulam as 30 leituras aceitas mais recentes e exibem o desvio
  padrão residual após remover a tendência linear. Assim uma rampa legítima do processo não é
  confundida com ruído do sensor; oito amostras são exigidas antes de classificar a leitura como
  estável, ruído moderado ou ruído elevado.
- **Inicialização automatizável do workspace.** `--workspace <caminho>` inicia diretamente no
  workspace informado e `--no-workspace-prompt` reutiliza o caminho configurado ou o padrão,
  sem abrir o seletor de pasta do Windows.
- **Contrato de dispositivo externo unificado (presença, roteamento e confirmação).** Os cinco
  nós Wi-Fi passam a ser modelados como o fluxômetro já era, com três estados que nunca se
  confundem: o interruptor do operador, o roteamento que o Hub persiste (`*CommEnabled`) e a
  presença do nó (`*Online`). `ExternalDeviceStatus` é esse vocabulário, compartilhado por
  biomassa, bomba externa, agitador de frasco e sensor de nível/espuma. Quando um dispositivo é
  reportado ausente, o parser **invalida** suas leituras em vez de segurá-las — o defeito que
  deixava uma absorbância de dez minutos atrás na tela como se fosse atual. Um dispositivo sobre
  o qual o Hub nunca falou lê *aguardando telemetria*, nunca *desconectado*, e não é bloqueado:
  exigir telemetria antes de aceitar comando travaria o sensor de biomassa, que não publica nada
  enquanto está parado e é justamente o `start` que o faria publicar. Contra um Hub sem as
  chaves novas, a presença é inferida por envelhecimento local. Ver
  [PLANO_DISPOSITIVOS_EXTERNOS.md](PLANO_DISPOSITIVOS_EXTERNOS.md) e
  [DECISIONS D-026](DECISIONS.md).
- **Seis canais da bomba que já estavam no fio e eram descartados.** `PumpMode`, `PumpPWM`,
  `PumpSpeed`, `PumpTargetVol`, `PumpActive` e `PumpWaiting` passam a ser lidos. O volume-alvo é
  a própria integral do perfil calculada pelo nó — a única forma de ver a bomba atrasada em
  relação ao perfil sem recalculá-la a partir de outro relógio.
- **Envio em quadro ordenado.** `ConnectionManager.SendCommandAfterCurrentFrame`,
  `IDeviceService.SendAfterCurrentFrame` e `ICommandArbiter.DispatchSeparateFrame` enviam um
  quadro sozinho, depois do que já estiver no buffer. O buffer normal funde por projeto, o que é
  errado para pares de chaves que a ordem de parsing do firmware faz interagir.
- **Telemetria do agitador de frasco na interface**, quando o Hub publicá-la: magnitude e
  sentido reais, e **quem está comandando o motor**. Com o potenciômetro de bancada ativo, o
  card avisa que desligar devolve o controle a ele.
- **Firmwares padronizados** para o contrato acima, em pastas novas ao lado das anteriores:
  `frasco_agitador_04` (passa a empurrar telemetria — antes o Hub não sabia nada sobre esse
  dispositivo), `v_4_DC_motor_peristaltic` e `biomass_sensor_analog_v05_hubsync` (heartbeat de
  IDLE, para separar "parado" de "caído"), todos com `cmd_id` idempotente e `ack_cmd_id`. O Hub
  v8 foi editado no lugar: `ReliableMailbox` generalizada, presença e roteamento publicados
  sempre, handler `/agitatorData`, janela de validade para a bomba (que não tinha nenhuma) e
  dois relógios para a biomassa. O fluxômetro e o sensor de distância não mudaram. Ver
  [FIRMWARE_DISPOSITIVOS_EXTERNOS.md](FIRMWARE_DISPOSITIVOS_EXTERNOS.md).
- **Cinco alarmes de dispositivo externo** — `Sensor de biomassa offline`, `Bomba externa
  offline`, `Sensor de distância offline`, `Agitador de frasco offline` e `Roteamento
  divergente no Hub`. Os quatro primeiros são qualificados pelo eco de roteamento do Hub, então
  um dispositivo desligado de propósito não gera alarme; um Hub que não publica as chaves novas
  não gera nenhum, em vez de reportar todos como falhos.
- **Três blocos de receita para os dispositivos externos**, numa categoria nova
  *Dispositivos Externos*. **Bomba Externa** substitui o antigo *Controle da Bomba*, que era um
  marcador: registrava a intenção e não enviava nada, porque foi escrito enquanto a atuação da
  bomba ainda não existia. Agora envia os cinco perfis pelo mesmo construtor do card manual.
  **Sensor de Biomassa** e **Agitador de Frasco** são novos. Cada bloco despacha e então segura
  até o dispositivo confirmar, com as saídas *pular bloco* e *parar receita* de sempre — e todos
  prosseguem quando o Hub não diz nada sobre aquele dispositivo, para não travar contra um Hub
  ainda não regravado. A parada do agitador numa receita bloqueia o potenciômetro de bancada,
  ao contrário do *Desligar* manual: uma parada que um botão desfaz não é uma parada.
  [DECISIONS D-030](DECISIONS.md).
- **O ponto de estado de cada dispositivo externo passa a carregar a mesma severidade do
  chip ao lado** — vermelho em *desconectado*, âmbar em *aguardando* ou *roteamento*. Antes
  seguia apenas o interruptor do operador, então um chip vermelho podia ficar ao lado de um
  ponto âmbar: duas respostas para a mesma pergunta.
- **O fluxômetro usa o mesmo controle de chips dos demais** e ganhou o chip `roteamento`, que
  não tinha. O Hub persiste `flowComm` na NVS como os outros flags, e `FlowControlEnabled` é a
  condição do alarme `Fluxômetro offline` — uma divergência silenciosa desligava o alarme junto
  com a malha.
- **Painel reagrupado por topologia.** Os sensores de biomassa e distância — nós Wi-Fi
  independentes — estavam em *Parâmetros Internos*, e as bombas de nutriente e antiespumante —
  que ficam dentro do módulo OpenTEC, na UART interna — estavam em *Dispositivos Externos*. Um
  operador diagnosticando uma queda precisa saber qual dos dois enlaces olhar. Todos os cards
  do grupo externo passaram a mostrar presença, não só o fluxômetro.

### Changed
- **Versão no cabeçalho sem o metadado de build.** O cabeçalho mostra `v0.26.2-dev`; a string
  completa do MinVer, com o hash do commit, foi para o tooltip.
- **Faixa operacional de agitação corrigida para 15–1000 rpm.** O construtor do protocolo,
  validadores de receita, runner de potência, tela kLa, especificação da interface e testes
  usam agora o mínimo real de 15 rpm; `0` permanece reservado à desabilitação do motor.
- **Barra compartilhada de Gráficos compactada com ícones vetoriais.** Pausa/continuação,
  cursor, limpar/restaurar, evento e CSV mantêm _tooltips_ e nomes de acessibilidade. A
  exportação PNG foi removida intencionalmente dessa barra; a exportação CSV permanece.
- **Retenção intencional de envio de setpoints na perda de foco e Enter (AUD-005).** Decisão explícita de
  produto mantém o disparo de setpoints e limiares de calibração ao perder o foco do teclado (`LostKeyboardFocus`)
  ou pressionar `Enter`, preservando a agilidade operacional do operador no laboratório. Proteções de validação
  e recusa sob posse externa permanecem ativas via `IManualDispatcher`. Ver [DECISIONS D-038](DECISIONS.md).

### Fixed
- **`CaptureSettingsDialog` lançava `XamlParseException` ao abrir os critérios de captura.** Os
  estilos `PowerLabel` e `CompactField` eram declarados apenas em `PowerView.UserControl.Resources`;
  a janela independente os referenciava sem ter esse escopo. Ambos passam a viver em
  `Themes/Controls.xaml`. `PowerViewResourceContractTests` agora cobre também o diálogo e instancia
  a janela de verdade, em vez de apenas conferir a existência da chave em algum arquivo.
- **A janela podia ser arrastada abaixo do próprio mínimo.** `WindowChromeMaximizeFix` marca
  `WM_GETMINMAXINFO` como tratada, o que impede o `DefWindowProc` de preencher `MinTrackSize`; o
  handler agora o preenche a partir de `MinWidth`/`MinHeight` convertidos para pixels físicos pelo
  DPI da janela.
- **O X ficava ceifado com a janela maximizada.** O glifo era desenhado de 0,0 a 10,10, com metade
  do traço de 1 DIP fora da caixa. Passa a usar coordenadas em meio pixel com `Stretch="Uniform"`,
  e os três botões de legenda existem agora uma única vez, em `Themes/Controls.xaml`, em vez de
  copiados em `MainWindow` e em cada diálogo.
- **Conteúdo cortado na janela mínima.** A tabela de Controle deixa de somar 938 DIP fixos e passa a
  distribuição proporcional com piso e teto; o corpo do Mapeamento kLa deixa de exigir três colunas;
  as métricas de Potência quebram linha em vez de comprimir; a ilustração de Configurações cede o
  espaço ao formulário abaixo de 1000 DIP. "Parada segura" fica em coluna `Auto`, medida antes da
  faixa de predefinições, e não pode ser o controle empurrado para fora.
- **`CheckTextTruncation` comparava `DesiredSize` com `ActualWidth`.** O primeiro inclui a margem e o
  segundo não, então todo rótulo com afastamento aparecia como truncado. A margem passa a ser
  descontada antes da comparação.
- **Captura automatizada de telas e eliminação do erro COM 0x80004002 (Etapa 1.9).** Substituída a automação
  externa de desktop (`UIAutomationClient.dll`) por renderização em memória via `RenderTargetBitmap` operando sobre
  dispatcher STA isolado (`WpfRenderingHost`). Eliminados ciclos de binding recursivo em `RadioButton` (`UpdateRadioButtonGroup`),
  corrigida a sincronização de `RunOnUi` em ViewModels e adicionada suíte de testes `ScreenshotCaptureTests` cobrindo 100%,
  125% e 150% DPI em temas Claro e Escuro (30 artefatos PNG gerados em `docs/evidence/screenshots/`). Reativado e aprovado o
  teste `ThemeServiceTests`. Total da suíte ampliado para 1116 testes com 100% de aprovação e zero ignorados. Ver [DECISIONS D-040](DECISIONS.md).
- **Dívida técnica de formatação e barreira de analisadores no CI (AUD-008).** Executado `dotnet format`
  em toda a solução, corrigindo quebras de linha, identação e estilos. O arquivo `.editorconfig` foi
  alinhado com `CONVENTIONS.md` para suportar regras de nomenclatura explícitas de constantes privadas
  e campos estáticos somente-leitura em `PascalCase`. Suprimido aviso `CS0067` para eventos de mock em
  testes. O gate `dotnet format OpenTECHub.slnx --verify-no-changes --no-restore` passa a retornar código 0
  com zero erros e zero avisos na solução. Ver [DECISIONS D-039](DECISIONS.md).
- **Estabilização determinística do tempo de inicialização (AUD-006).** Verificado o impacto do `DeferredPageHost`
  (ADR D-033), garantindo inicialização diferida de rotas secundárias em `ApplicationIdle` e atingindo First-Frame
  estável de 968–1280 ms em todas as rotas (abaixo da meta de 2 s).
- **Verificação de temas de gráficos em binário empacotado (AUD-007).** Validação confirmada pelo operador
  em build publicado (`win-x64`), assegurando renderização correta de eixos e séries em temas Claro e Escuro.
- **Retentativa automática reativa de gás proporcional na liberação de aeração (AUD-004).**
  O `PumpControlViewModel` agora assina os eventos de transição de posse do `ICommandArbiter` (`OwnershipChanged`
  e `OwnershipRevoked`). Quando a aeração é tomada por outro controlador (como cascata de oxigênio ou receitas),
  o último setpoint enviado é invalidado e a retentativa é sinalizada como pendente. Ao término da sobreposição,
  quando o atuador de aeração retorna para `CommandOwner.Manual`, o ViewModel aciona imediatamente um despacho
  forçado (`MaybeSendProportionalGas(force: true)`), ignorando a banda morta de reenvio (`GasFlowResendThresholdLpm`)
  e restaurando a vazão calculada $Q_g = (V_0 + V_{\text{bomba}}/1000) \cdot \text{vvm}$ no hardware sem requerer
  novas telemetrias ou variação de volume. O setpoint aceito só é registrado após confirmação de sucesso pelo árbitro.
  Ver [DECISIONS D-037](DECISIONS.md).
- **Observabilidade completa de aceitação de comandos manuais nas ViewModels (AUD-003).** A migração para
  `IManualDispatcher.Dispatch` com retorno observável `CommandDispatchResult` foi expandida para
  `SubsystemViewModel`, `ControlViewModel` (`ApplyAll`, `ApplyFlowState`), cartões de dosagem
  (`PHControlViewModel`, `NutrientControlViewModel`, `AntifoamControlViewModel`) e telas de calibração
  (`FlowCalibrationViewModel`, `BiomassCalibrationViewModel`). Quadros manuais recusados pelo árbitro
  (ex.: receita ativa, cascata ou ensaio) não chamam mais `CommitPendingCommand()`, retêm os valores
  editados no estado pendente (`HasPendingChange = true`) e reportam explicitamente no `StatusText`
  o atuador recusado e o processo conflitante com rótulos amigáveis em pt-BR (ex.: *"Comando recusado:
  temperatura sob controle de Receita"*). Ver [DECISIONS D-036](DECISIONS.md).
- **Travamento visual dinâmico de controles manuais e desacoplamento da parada segura (AUD-002).**
  As cinco linhas de processo e os seis cartões periféricos passam a refletir em tempo real o estado de
  posse por atuador (`CurrentOwner`, `IsOwnedByOther`, `HasOwnerBadge`, `OwnerBadgeText`, `OwnerLockReason`),
  desabilitando campos de entrada e exibindo crachás de proveniência (`receita`, `controle o₂`,
  `ensaio kla`, `ensaio pot`) quando sob controle externo. O bloqueio geral de página no
  `ControlView.xaml` foi removido, assegurando que a barra de status e o botão de **Parada segura
  permaneçam 100% operáveis e acessíveis em qualquer circunstância**. Ver [DECISIONS D-035](DECISIONS.md).
- **Parada segura global garantida durante receita ativa (AUD-001).** Implementado o `ISafetyCoordinator`
  e caminho privilegiado de segurança no `ICommandArbiter` (`DispatchSafety`), revogando com prioridade
  absoluta a posse de receitas em execução, cascatas ou ensaios e despachando atomicamente o quadro de parada
  de emergência para o hardware, eliminando falhas silenciosas e falsos positivos de sucesso.
  Ver [DECISIONS D-034](DECISIONS.md).
- **Texto dos gráficos ilegível no tema escuro.** Rótulos de eixo e números de escala usavam
  `TextSecondaryBrush`, um cinza médio pensado para texto acessório — mas num gráfico o eixo
  *é* o conteúdo. Passam a usar `TextPrimaryBrush` nos quatro gráficos grandes (mapeamento kLa,
  determinação de kLa, calibração e sinóptico). A barra de cores é um painel, não um eixo, então
  `Plot.Axes.Color` nunca a alcançava: seu rótulo e seus ticks ficavam no quase-preto padrão do
  ScottPlot durante todo o tema escuro, e agora seguem o tema. Os minigráficos (`TrendSpark`,
  `PumpPreviewChart`) continuam no tom `muted` de propósito.
- **Arquivos de experimento kLa eram um GUID puro.** `Experimentos\099afd80-…​.kla.json` não diz
  a ninguém de qual ensaio se trata. Passam a ser gravados como `<nome>_<id>.kla.json` — o nome
  na frente para leitura, o id no fim porque é ele que garante unicidade e permite localizar o
  arquivo. Renomear o experimento renomeia o arquivo, sem deixar cópia antiga para trás, e os
  arquivos já existentes com nome de GUID são renomeados na primeira leitura (apenas renomeados;
  o conteúdo não é tocado).
- **"Parar" durante a reconexão demorava segundos para valer.** O laço de repetição lia o
  canal de pedidos apenas *entre* tentativas, e uma tentativa real é abrir a porta mais o
  handshake, seguidos do atraso de backup — então o operador clicava em Parar e via o
  aplicativo continuar reconectando. Agora `Disconnect` e `Connect` cancelam a tentativa em
  voo: o pedido é postado no canal e o `CancellationTokenSource` da recuperação é cancelado
  logo em seguida, de modo que o laço acorda com o pedido já disponível para ler.
- **O alarme "Link perdido" ficava na tela depois de desconectar, e "Reconhecer" não o
  tirava.** Desconectar pelo operador já encerrava o alarme, mas por um caminho que o
  `Poll` seguinte não tinha o que reportar — nenhum evento `Changed` era emitido e a faixa
  continuava desenhada com um alarme que o serviço já havia descartado. Reconhecer também
  não resolvia: não havia mais nada travado para reconhecer. O `AlarmService` agora notifica
  ao encerrar o alarme nessa transição.
- **Trocar a pasta de trabalho deixava os dados partidos entre duas pastas.** "Alterar Pasta..."
  só movia o caminho estático, mas o `settings.json`, as receitas, os mapas, os testes de kLa, o
  serviço de backup e o log já tinham lido o seu diretório no construtor — então sessões e
  histórico passavam a ser gravados na pasta nova enquanto todo o resto continuava na antiga, sem
  nenhum aviso, até o próximo reinício. Agora a troca **copia** o workspace inteiro para o
  destino (sem apagar a origem e sem sobrescrever nada que já exista lá), grava o novo caminho e
  **reinicia** o aplicativo nele, com `Logging.SessionLogPath` reapontado na cópia para o ensaio
  correspondente. A operação é recusada com receita ou ensaio de kLa em andamento e avisa quando
  o equipamento está conectado. Ver [DECISIONS D-031](DECISIONS.md).
- **Orientação dos mapas de calor fixada por teste.** O eixo N do mapa de folga e da superfície de
  kLa já havia sido invertido duas vezes a olho. `HeatmapOrientationTests` renderiza um mapa e lê
  os pixels: com `FlipVertically = true` a linha 0 do arranjo — a agitação mínima — fica na base
  do eixo, que é a convenção que os dois gráficos usam.
- **Painéis laterais do sinóptico padronizados.** O pH agora usa o mesmo resumo PV/SP/Δ e
  minigráfico dos outros parâmetros, com seus campos dentro de *Controle*. Toggles ficam no
  cabeçalho e enviam imediatamente; campos enviam em `Enter` ou perda de foco. Foram removidos
  Aplicar/Reverter, a falsa indicação de confirmação, textos de implementação, o selo
  *COMANDADO* e *Configurações avançadas*. pH, oxigênio, vazão e biomassa oferecem um botão que
  abre diretamente a aba correspondente em *Calibrações*.
- **Resumo do biorreator mais legível.** Cards, fontes e área da coluna de parâmetros foram
  ampliados, e o conteúdo dos painéis ganhou afastamento consistente da barra de rolagem.
- **A faixa de alarme escondia o fim do texto atrás dos botões.** Os dois painéis eram filhos
  sobrepostos de uma célula única, e um `StackPanel` horizontal dá largura infinita ao filho —
  o `TextTrimming` nunca disparava. Agora o texto é limitado por uma coluna e quebra linha.
- **A linha do agitador mostrava o valor encenado na coluna *Valor Lido***, que é exatamente o
  defeito que este conjunto de mudanças existe para remover. Passa a mostrar o que o nó reporta.
- **A desativação da bomba externa não parava a bomba.** O quadro único da v.6
  `{"pumpComm":0,"mode":0,"speed":0}` limpa o roteamento e em seguida descarta o próprio
  `mode:0` (`if (pumpCmdFound && pumpCommOn)`): o nó continuava dosando e só a telemetria
  silenciava. Agora são dois quadros ordenados, `{"mode":0,"speed":0}` e depois
  `{"pumpComm":0}`. O mesmo vale para a biomassa, `{"stop":1}` antes de `{"biomassComm":0}`.
  [DECISIONS D-027](DECISIONS.md).
- **A parada segura do agitador podia ser desfeita pelo potenciômetro.** `agitatorOn:0` vira
  `ActivePot = agitatorReEnablePot` no Hub, e o nó relê o botão de bancada no ciclo seguinte —
  uma parada com o botão em 60 % religava o motor em 60 %. A parada segura agora envia
  `agitatorReEnablePot:0` junto; o **Desligar** comum continua respeitando a preferência do
  operador, com aviso no card. [DECISIONS D-028](DECISIONS.md).
- **Os três momentâneos da biomassa perdiam cliques.** A caixa de comando do Hub guarda um
  comando só, é limpa na leitura e é sobrescrita por `setPending`; com o nó consultando a cada
  2 s, um `blank` seguido de `start` entregava só o `start`. Agora são serializados atrás de uma
  trava de pendência visível.
- **AUD-003 e AUD-004 nos cards de dispositivo externo.** `IManualDispatcher` devolve
  `CommandDispatchResult`, e biomassa, bomba, agitador e espuma só persistem estado, limpam
  `HasPendingChange`, incrementam a versão do perfil ou avançam o último valor de gás
  proporcional **após aceitação**. Em recusa, o valor encenado permanece e o proprietário do
  atuador é nomeado. O acoplamento de gás proporcional também é suspenso — e não recalculado a
  partir de zero — enquanto a bomba estiver ausente. [DECISIONS D-029](DECISIONS.md).
- **Estabilização da vazão no alívio antes do `t₀` do ensaio de kLa.** Bancadas com uma válvula
  de alívio instalada logo após o fluxômetro podem marcar *Abrir o alívio e esperar a vazão
  assentar* nos parâmetros do teste. Depois da estabilização pós-N₂, o runner abre a válvula
  escolhida (`valve_1` ou `valve_2`, obrigatoriamente a que o N₂ não usa) já na vazão da
  condição, mantém o pulso inicial do medidor saindo para a atmosfera e só fecha o
  alívio — iniciando `Reoxygenating` — depois que a vazão medida fica dentro de `± tolerância`
  (padrão `0,2 L/min`) do setpoint por N leituras consecutivas. O fechamento preserva o setpoint
  assentado, então nenhum novo pulso entra no reator. Durante a espera a agitação fica na
  *rotação de alívio* configurável (padrão `50 rpm`); a rotação do ensaio só é comandada no
  fechamento do alívio, porque mantê-la sem gás borbulhando reoxigenaria o meio por aeração
  superficial. Duas fases novas (`OpeningVent`,
  `StabilizingVentFlow`) são gravadas nas séries mas ficam fora do ajuste log-linear; a espera
  tem teto próprio e, ao expirar, encerra a corrida para revisão em vez de admitir ar instável.
  Desligado por padrão. Ver [PLANO_IMPLEMENTACAO_TESTES_KLA.md](PLANO_IMPLEMENTACAO_TESTES_KLA.md) § 11.2.
- **Fluxômetro v05 sincronizado pelo ESP32 Hub v7.** `Controle`, `Painel` e a calibração
  agora distinguem a conexão app–Hub do enlace Hub–fluxômetro, mostram estados de pendência e
  desconexão e bloqueiam novos comandos enquanto `FlowCommandPending` estiver ativo ou
  `FlowmeterOnline` estiver falso. O estado de vazão só é confirmado na interface após a
  telemetria liberar a pendência; os firmwares congelados não foram modificados. O plano e a
  verificação restante em hardware estão em
  [FLOWMETER_V05_HUB_V7_SYNC_PLAN.md](FLOWMETER_V05_HUB_V7_SYNC_PLAN.md).
- **Recipes hold for an unresponsive external device, and say so.** A flow-setpoint or
  aeration-enable block now waits for the flowmeter to confirm (`FlowmeterOnline` plus the
  `FlowSetpoint` echo) instead of completing on dispatch. After 8 s the engine publishes
  `IRecipeEngine.Waiting`, which logs a warning and latches the new
  `Receita aguardando dispositivo` alarm; the Receitas banner offers `Pular bloco` and
  `Parar receita`. A zero setpoint never waits, and neither does `Desligar malha de aeração`.
- **`CommandBuilders.FlowmeterLoopEnabled`.** `flowmeterComm` in a frame of its own, sent where
  the flow loop is switched — the Vazão de Ar enable and the recipe's aeration-loop block. It is
  not routed to the v05 and stays out of the setpoint/safe-stop payloads, but the Hub v7 parses
  it, persists it and republishes it as `FlowControlEnabled`: leaving it unwritten desynchronised
  the Hub from the app and left the `Fluxômetro offline` alarm unable to fire.
- **Post-merge release audit and v0.25.0 stabilization plan.** Added
  [CURRENT_STATUS.md](CURRENT_STATUS.md) with verified Git/version/build/runtime evidence, the
  ownership/safe-stop and merged biomass/pump findings, UI/build gates and the extended release plan.
- **Modal Pop-up Configuration (`OxygenConfigDialog`).** Replaced the secondary "Controle de oxigênio" tab in `Controle` with a modal configuration window opened via the ⚙ button on the Oxygen row.
- **Independent 4-Mode PID Settings.** Each mode (`Agitação`, `Aeração`, `Cascata`, `Mapa`) maintains and stores independent PID parameters in `AppSettings` and runtime structures.
- **Dual-Loop PID Cascade Controller (`CascadeTwoLoopPidController`).** Ported from industrial standard (`BlocosDeControle`) with outer predicted oxygen error loop, inner velocity-form rate error PID, low-pass derivative filter, sliding-window anti-windup, and actuator gain scheduling.
- **Cascade Channels in Charts.** Registered `CascadeEffort`, `CascadePredictedO2`, `CascadeRateSetpoint`, `CascadeRateMeasured`, and `CascadeKlaDemand` in `ChartsViewModel` and telemetry history.
- **Real-Time Input Validation.** Added visual error banner and validation rules for physical limits, effort windows, and PID parameters in `OxygenConfigDialog`.

### Fixed
- **Simulator flow model matches the Hub v7 firmware.** The simulated gas follows the setpoint
  alone and the echoed `FlowSetpoint` is withheld only while the flowmeter is offline, instead of
  both being gated on `flowmeterComm` — which in the firmware writes nothing but the Hub's own
  `FlowControlEnabled` flag.

### Changed
- **Contrato de vazão estrito do Hub v7.** Frames operacionais e de calibração usam apenas as
  chaves roteadas pelo firmware v7 (`flowSetpoint`, `maxFlow`, `valve_1`, `valve_2`, `v_Flow`
  e `k1..c2`); o app não emite mais a preferência legada `flowmeterComm`. Alarmes tratam a
  queda interna do fluxômetro mesmo quando essa preferência legada está desativada.
- **Controle/Painel UI correction pass.** Fixed the always-visible airflow drawer, widened and
  aligned the process grid, removed command-only badges from the dense value column, exposed the
  external-pump mode and biomass optimal-threshold editors in-row, stopped pump-chart Y-axis
  accumulation, restored published kLa path selection, and made the approved
  `Imagem_biorreator_side.png` the sole Painel equipment figure. The follow-up pass left-aligns
  every variable icon/name pair with an 8 px gap and restores compact oxygen, pump-mode and biomass
  editors.
- **Supported WPF chart target.** The app and WPF tests now target Windows 10 2004 or newer
  (`net10.0-windows10.0.19041.0`), so NuGet selects SkiaSharp's supported modern WPF asset instead
  of the .NET Framework fallback. The `NU1701` compatibility warning and current build analyzer
  warnings are resolved.
- **Current documentation synchronized to v0.24.0.** README and roadmap now distinguish the built
  feature inventory from field-release readiness and identify v0.25.0 as the gated stabilization/UI
  polish milestone; the assembly version has not been bumped.
- **Process Parameters Table Layout.** Moved "Modo" column to the last column in `ControlView.xaml`.
- **Removed per-row "Reverter".** Cleaned up process parameter rows to keep per-row "Aplicar" and global "Reverter tudo".
- **Gain Scheduling Restriction.** Confined advanced gain scheduling factor and controls strictly to `Cascata` mode.

### Removed
- **`CascadeTuningView`, `CascadeTuningViewModel`, `CascadeChart`.** Deleted obsolete view/viewmodel/control files.
- **Embedded graphs in configuration window.** Graphs are now consolidated in the dedicated `Gráficos` workspace.

---

## [0.24.0] - 2026-08-22

**Receitas finalization.** After a first operator run of the page, the canvas becomes a real
editor: recipe tabs and a Minhas Receitas library, zoom/pan, orthogonal connectors with arrows,
block and connection deletion, undo/redo, repeating-list editing, and the cascade loop corrected to
the ReceitasOpenTEC semantics. See [D-023](DECISIONS.md).

### Added
- **Recipe tabs + Minhas Receitas library.** `Nova Receita` opens a new tab instead of replacing the
  current one; recipes are saved as versioned JSON in the per-user recipes folder (`RecipeStore`),
  listed in a library tab with `Abrir` / `Duplicar` / `Excluir`; `Salvar` / `Carregar` wired.
- **Canvas interaction.** Wheel-zoom toward the cursor and drag-to-pan; orthogonal (90°) connectors
  with arrowheads; click a connection to select and `Delete` to remove it; click a block + `Delete`
  to remove it; undo/redo (`Ctrl+Z` / `Ctrl+Y`); clicking a validation finding centres its block.
- **`Múltiplos Pontos de Ajuste` / `Múltiplos Controles`.** Repeating-row editing (add/remove),
  generated from the block's item schema.

### Changed
- **Cascade loop matches the original.** The `Saída Loop` wires to the loop's exit *condition* — a
  `Monitorar Variável` (automatic exit) or an `Intervenção Manual` (a Continuar/Pular switch) — read
  each iteration, rather than run as a subgraph. `Intervenção Manual` is dual-role by wiring.
- **pt-BR everywhere.** Option dropdowns show their labels (not `RecipeOption { … }`), and block
  summaries read in pt-BR (e.g. `pH < 0`, not `Ph LessThan 0`).

---

## [0.23.0] - 2026-08-22

**Phase 3 WP4 — Receitas.** The graphical experimental-protocol editor, integrated directly into
the controller: author a node graph, validate it, and run it through an engine that owns the wire.
Ported from ReceitasOpenTEC's node graph, validator and engine slicing, and **re-targeted to the
ESP32-S3** — no Modbus, no VNC screen driver, no ×10/×100 scale factors, no robot panel, and no
O₂-enrichment path. See [UI_DESIGN §5.3](UI_DESIGN.md#53-receitas) and [D-023](DECISIONS.md).

### Added
- **Recipe domain (part 1).** `RecipeNodeCatalog` declares the nineteen blocks **once** — category,
  ports and parameter schema — replacing ReceitasOpenTEC's hand-written model+viewmodel+view triple
  per type; one declaration drives editing, validation and the JSON panel. `RecipeDocument` holds
  the graph with parameter values in a schema-keyed `JsonObject`. `RecipeSerializer` versions the
  JSON from v1 with a migration hook and tolerates legacy type/connector spellings (canonical is
  written, historical only read). `RecipeValidator` implements every §5.3.13 rule — graph structure
  (one Início, ≥1 Fim, reachability, path-to-Fim, cycle-outside-loop) and per-block, including
  `Monitorar` refusing actuation variables and the cascade actuator-window checks.
- **Recipe engine (part 2).** The sliced `RecipeEngine` (`.Flow`/`.Nodes`/`.Actuation`/`.Pumps`/
  `.Cascade`/`.Safety`/`.State`) drives the **same** `ICommandArbiter` as manual control under
  `CommandOwner.Recipe`. Starting a recipe **claims every actuator**, so the manual surfaces go
  inert and only the recipe writes to the wire; a link/feedback loss revokes ownership and
  safe-aborts the run; stopping safe-stops the declared subsystems and returns the wire to Manual.
  The cascade block drives the ported `CascadeController` under Recipe ownership and fires its loop
  body per iteration; live gain tuning during a run.
- **Receitas page (part 3).** A new nav destination: block library grouped by category, a draggable
  node canvas with click-to-connect ports and connectors (neutral for the defined graph, green only
  for the executed path), a property pane generated from the block schema, a live validation strip,
  the JSON panel, and Iniciar/Pausar/Parar execution controls. `Modo` now offers **Receita** and
  reflects the running recipe.

### Changed
- **`Modo → Receita` is real.** The command-ownership vocabulary reserved in Phase 2 WP4 is now
  driven end to end; selecting it (or starting a recipe) hands the wire to the engine.

### Notes
- Deliberately deferred to polish: drag-from-library, pan/zoom/minimap, repeating-list editing,
  recipe tabs/library, save/load to disk, and the in-pane live cascade readout. The pump-block
  field mapping and the vvm→L/min aeration coupling await hardware confirmation.

---

## [0.22.0] - 2026-08-22

Phase 3 WP2 — the external peristaltic pump and proportional-gas coupling. This closes the
Phase 3 WP2 software scope; pump actuation and the proportional-gas flow ride the bioreactor gate.

### Added
- **Five firmware profile modes.** `CommandBuilders.PumpConstant/Linear/Exponential/Polynomial/Piecewise`
  build the exact v.6 frames — `mode`, `init_t`, `final_t` (minutes) then the mode parameters:
  `lambda_const`; `lambda_linear`/`phi_linear`; `lambda_exp`/`phi_exp`; `p0..p20`; or `num_segments`
  with interleaved `t0,q0,t1,q1,…`. Byte-parity pinned by golden strings.
- **`PumpProfileMath`** — pure flow/volume evaluation ported from v.6's simulation: flow is zero
  before `init_t` and clamped non-negative, and accumulated volume is the trapezoidal integral
  `∫ Q dt`. Drives both the preview and the send path (one mode dispatch).
- **`PumpControlViewModel`** — a `Bomba externa` card on Controle: enable (`pumpComm`), mode selector,
  per-mode parameters, a shared **flow/accumulated-volume preview** (`PumpPreviewChart`, ScottPlot,
  theme-aware), live reported flow/volume, and the proportional-gas section.
- **Proportional-gas coupling.** `Q_g = (V₀ + PumpVol/1000)·vvm`, clamped to `maxFlow` and dispatched
  as a standard aeration frame through the **same arbiter** as manual/cascade flow — refused when the
  cascade owns aeration. It resends only on a material change.
- **Safe disabled frame** `{"pumpComm":0,"mode":0,"speed":0}` (byte-identical to v.6, `speed` vestigial),
  wired into the operator safe-stop; **payload-size validation** (1–21 coefficients, 2–100 segments,
  `t0 = 0`, strictly increasing times) from the firmware's own array bounds.
- **`PumpControlSettings`** — a **versioned** profile (all modes' parameters kept, so switching mode
  loses nothing) plus the gas coupling; each applied send bumps the version. `ActuatorId.ExternalPump`
  is now an owned arbiter actuator, its dynamic `p{i}`/`t{i}`/`q{i}` keys matched by pattern.
- **Synoptic + charts.** An external-pump tag on the reactor drawing (feed path) and the existing
  `Bomba — vazão`/`Bomba — volume` chart channels.

### Verified
- 417/417 tests pass. New: the profile golden strings and key order, `PumpProfileMath` per mode and
  its constant-profile volume, the ViewModel's mode visibility/validation/version bump, the
  proportional-gas coupling driving aeration from pump volume, and the arbiter refusing a Manual pump
  frame the cascade owns. Simulator tolerates the profile keys (unknown keys ignored). Bioreactor
  actuation remains the field gate.

---

## [0.21.0] - 2026-08-22

Phase 3 WP1 — the biomass optical sensor and its guided procedure. Enable/blank/start/stop, atomic
thresholds and the live Abs/Raw/IT/PWM readouts, plus a Calibrações procedure. Actuation of the
blank/start/stop and the reading itself ride the bioreactor gate.

### Added
- **Biomass command surface.** `CommandBuilders.BiomassComm/BiomassBlank/BiomassStart/BiomassStop/BiomassThresholds`
  — the enable, the three momentary actions (`start`/`stop` were undocumented in v.6's tree but are
  what the firmware forwards) and the atomic `{"low","high","opt"}` integration thresholds. Golden-string pinned.
- **`BiomassControlViewModel`** — a `Biomassa` card on Controle: an immediate enable toggle (as in v.6),
  blank/start/stop gated on the sensor being on, staged thresholds with validation, and the live
  Abs/Raw/IT/PWM block (all `—` until a frame carries absorbance).
- **Guided procedure** on Calibrações (`BiomassCalibrationViewModel`): enable → capture the blank →
  confirm Abs ≈ 0 → set thresholds, with live absorbance feedback and a link-loss refusal. The
  firmware exposes **no HD-mode state** (confirmed against `OpenTEC_ESP32_v7.ino`), so none is shown.
- **`BiomassControlSettings`** — the persisted low/high/optimal thresholds (raw counts); the sensor
  enable is never persisted (it starts off). `ActuatorId.Biomass` is now an owned arbiter actuator so
  a recipe cannot fight the operator over the blank/thresholds — but it is **excluded from the safe-stop**,
  because it is a measurement and a stop must not blind it (the same rule the level/foam sensor gets).
- **Synoptic.** A biomass optical-sensor tag on the reactor drawing (instrument path) and the existing
  `Biomassa` (absorbance) chart channel and rail variable.

### Verified
- Biomass golden strings and key order; the ViewModel's immediate enable, gated momentary actions,
  atomic threshold apply/persist, incoherent-threshold refusal, and the no-data-until-absorbance
  readouts; and the actuator/key mapping (see [0.22.0] for the shared 417/417 run).

---

## [0.20.1] - 2026-08-22

Two of the WP4 Phase-0 link-hygiene items — the software-only ones that need no hardware to
validate. This closes the Phase 2 **software** scope; the remaining link items and every
process behaviour ride the bioreactor gate ([HARDWARE_VALIDATION.md](HARDWARE_VALIDATION.md)).

### Changed
- **Immutable telemetry snapshots.** `ConnectionManager.Readings` (the parser's mutable state) is
  now `internal`; consumers get the immutable `SensorSnapshot` from `TelemetryReceived` or the new
  `ConnectionManager.Snapshot()` accessor, so no caller can mutate live readings.
- **Configured Wi-Fi poll period.** `DeviceService.ConnectWiFi` now sets the HTTP transport's poll
  period from `Connection.DataDelayMs` (it was fixed at the 1 s default), so Wi-Fi polls at the
  device's telemetry cadence rather than faster than the shared response buffer refreshes.

### Added
- **[HARDWARE_VALIDATION.md](HARDWARE_VALIDATION.md)** — the consolidated bioreactor test plan:
  every hardware-gated behaviour across Phase 0-2, in dependency order, with prerequisites, steps
  and pass criteria, plus the v.6 baselines to capture first.

---

## [0.20.0] - 2026-08-21

Phase 2 WP8 (part 2) — controller gain scheduling. This closes WP8, and with it the Phase 2
software scope. Advisory/actuation behaviour is otherwise unchanged; a bioreactor run is the
remaining gate.

### Added
- **`GainSchedule`** — PID gains as a piecewise-linear function of the control effort. The
  manuscript's loop gain scales as 1/kLa and the effort maps onto kLa along the published path, so
  a schedule that raises the gains with effort holds the loop gain roughly constant across the
  operating range. Gains interpolate between breakpoints and hold flat outside them.
- **`GainScheduler`** — drives the schedule against the live effort with **bounded** transitions:
  the effective gains move toward the scheduled target no faster than a slew limit, so even a fast
  effort excursion cannot step-change the loop's responsiveness. Reports the schedule segment so a
  crossing can be journalled.
- **`CascadeService` integration** — when a schedule is enabled, the service retunes the
  velocity-form controller each armed frame (bumpless: only the gains change, the rate window and
  probe history are untouched) and journals every segment crossing to Eventos.
- **`GainScheduleSettings`** — a **versioned** schedule (enable, slew bound, breakpoints), **off by
  default**, since the paper shows a single robust gain set is workable without scheduling. Each
  applied edit bumps the version.
- **`Escalonamento de ganho` card** on `Controle → Cascata e sintonia`: the enable toggle, the
  transition bound, the versioned breakpoint table and the live active gains and segment.

### Verified
- 376/376 tests pass (14 new): the schedule's interpolation/clamping/segment mapping and validation,
  the scheduler's bounded slew and single segment-crossing report, and — end to end — the cascade
  driving the gains up with effort across a breakpoint and journalling it, plus a disabled schedule
  leaving the single base tuning.

---

## [0.19.0] - 2026-08-21

Phase 2 WP8 (part 1) — the conditional-OUR soft sensor. The manuscript's oxygen-uptake-rate
inference (`analysis/2_our_soft_sensor`), made causal for live data. Observation only; it never
actuates. Gain scheduling (part 2) is next.

### Added
- **`OurSoftSensor`** — the pure core. At quasi-steady state the O₂ balance
  `dC/dt = kLa·(C*−C) − OUR` collapses to `OUR = kLa·C*·(1 − DOT/100)`. A sample is accepted only
  when DOT is within a band of the setpoint **and** the causal |dDOT/dt| is small; anything else is
  refused and carries **no value** — never zero. The accepted-interval integral (∫OUR dt) never
  advances across a refused sample, keeping the conditional total separate from any total
  consumption. The offline reference's centred Savitzky-Golay derivative becomes a causal
  `LeastSquaresRateEstimator` — the same estimator the cascade already trusts.
- **`OurSoftSensorService`** — the live sensor: DOT and airflow from telemetry, the commanded
  agitation tracked from the command stream, and kLa from the active published map's reconstructed
  surface (`IKlaMappingEngine.Reconstruct`), refusing an off-map operating point rather than
  extrapolating.
- **`OurViewModel` + an OUR readout** on `Controle → Cascata e sintonia`: the value while accepted,
  the single accept/refuse reason while refused, the kLa and dDOT/dt behind it, and the
  accepted-interval mean/total/duration — tagged `estimado`, with a `Zerar total aceito`.
- **`OurSettings`** (C*, the band, the rate limit, the gate and the causal rate window), defaulting
  to the manuscript's constants.

### Verified
- 366/366 tests pass (13 new). `OurSoftSensorScientificTests` pins a 40-row oracle extracted from
  the manuscript's own conditional-OUR output (`tools/our-reference/generate_fixture.py` →
  `tests/fixtures/our-reference.json`): the C# OUR inversion reproduces every paper row and the
  acceptance predicate reproduces the paper mask. `OurSoftSensorTests` covers the gate, the two
  refusal reasons, the never-zero-fill and the accepted-interval integration.
- Live simulator run: the OUR readout rendered on the cascade workspace with the honest
  `Aguardando o DOT atingir o setpoint` refusal and the live causal dDOT/dt, with zero binding
  failures. Evidence: `docs/evidence/ui/phase2-wp8-our-panel.png`.

---

## [0.18.1] - 2026-08-21

Custom window chrome — the shell finally draws its own title bar, closing the gap with
[UI_DESIGN §4.1](UI_DESIGN.md#41-title-bar--48-px), which has always specified it.

### Added
- **Custom title bar.** `WindowChrome` removes the default Windows caption while keeping resize,
  snap, the taskbar button and the aero-snap menu. The app's 48 px header is now the whole title
  bar, with its own **minimise / maximise-restore / close** buttons on the right — vector glyphs
  (no icon font), themed to the header; only close takes the close red (`CaptionCloseHoverBrush`)
  on hover, and the maximise glyph switches to a restore glyph when maximised.
- **`WindowChromeMaximizeFix`** answers `WM_GETMINMAXINFO` with the nearest monitor's work area, so
  a maximised window stops at the taskbar instead of overhanging its edges — correct on multi-monitor.
- New theme tokens `CaptionCloseHoverColor` / `CaptionCloseGlyphColor` (both themes) and their
  shared brushes.

### Changed
- Interactive header controls (Zerar, connection chip, Buscar, Tema, the sensor-module chip) now
  carry `WindowChrome.IsHitTestVisibleInChrome` so they stay clickable inside the drag region.

### Verified
- 350/350 tests pass (token parity and every XAML resource key still resolve, including the new
  caption tokens and styles).
- Live simulator run: the custom title bar rendered themed in dark mode with zero binding failures;
  maximising respected the taskbar with no overhang and toggled to the restore glyph.
  Evidence: `docs/evidence/ui/titlebar-custom-chrome.png`, `docs/evidence/ui/titlebar-maximized.png`.

---

## [0.18.0] - 2026-08-21

Phase 2 WP7 — the remaining v.6 cultivation auxiliaries: nutrient dosing, antifoam dosing,
the level/foam sensor and the separate flask agitator. Each is a validated desired state that
reaches the wire only through the command arbiter.

### Added
- **Nutrient dosing** (`NutrientControlViewModel`) — operation/mix timing, the two cycle counts
  and pump intensity, emitted as one atomic frame. A commanded-only pump: its synoptic tile shows
  the commanded duty cycle, tagged `comandado`, never a measured value.
- **Antifoam dosing** (`AntifoamControlViewModel`) — operation/mix timing and intensity. The
  module's `Antifoam` figure has no documented unit, so it is shown raw and unitless.
- **Level/foam control** (`FoamControlViewModel`) — the distance-sensor enable, its reference and
  the three timers of the automatic antifoam response. This is sensor/automation **configuration**,
  not a held actuator: it sits outside the arbiter and the global safe-stop, so a stop never blinds
  foam monitoring.
- **Flask agitator** (`FlaskAgitatorViewModel`) — on/off, automatic mode, a 0-100 magnitude, a
  direction and the potentiometer re-enable. A separate bench device, so it never appears on the
  reactor synoptic. The operator's **signed** percent is split into the wire's separate magnitude
  (`agitatorPercent`) and direction (`agitatorDir`) keys — the sign never leaks onto the wire.
- **Three new owned actuators** — `Nutrient`, `Antifoam`, `FlaskAgitator` — join the arbiter, so
  each gains command ownership and lifecycle tracking and takes part in the global safe-stop. The
  foam/level sensor keys stay unowned, like the calibration keys.
- **Command builders** for all four subsystems (with matching safe-stops) and four typed settings
  records in `AppSettings`.
- **Controle cards** for Nutriente, Antiespumante, Controle de espuma and Agitador de frasco; the
  first three take part in the bulk `Aplicar alterações`, and nutrient/antifoam/agitator in
  `Parada segura`.
- **Synoptic elements** for nutrient (feed), antifoam and level, each with a leader line and a
  themed callout, plus a provenance-aware read-only detail pane. The legend gains the feed and
  antifoam colours and drops the "nível não monitorado" note.
- Event/setpoint audit names for the three dosing pumps, so their commands read cleanly on Eventos.

### Changed
- **Intensity encoding is pH-only `× 10`.** Nutrient and antifoam carry the **raw** operator percent
  (0-99). Only pH multiplies by ten; the builders and docs now say so explicitly.
- `ReadOnlyDetailView` gained a per-variable `DetailNote` and a `comandado` badge, so the dosing
  variables point at their Controle cards rather than repeating pressure's "measured but
  uncontrolled" wording.

### Verified
- 350/350 tests pass (22 new): the frozen dosing frames and their safe-stops, the signed→magnitude
  /direction split (the sign is asserted absent), the three actuators' ownership and the foam keys'
  freedom, the four ViewModels, and — in `ControlViewModelTests` — that the safe-stop stops the three
  pumps but omits the foam sensor and that the bulk apply merges the pumps into one frame.
- Live simulator run: the app reached the Controle page and connected over the localhost Wi-Fi
  simulator with **zero binding failures** (first frame 1452 ms); the four dosing cards rendered
  with their fields, badges and live telemetry. Evidence:
  `docs/evidence/ui/phase2-wp7-dosing-cards.png`.

---

## [0.17.0] - 2026-08-21

Phase 2 WP6 (part 2) — the cascade made observable: the oxygen detail pane's
`Cascata`/`PID`/`Saída` tabs and the live PV/SP/kLa/output tuning chart. This closes WP6.

### Added
- **Oxygen `Cascata`/`PID`/`Saída` detail tabs.** Oxygen is the one device with an app-side
  controller the app can observe, so its synoptic detail pane now carries the cascade state:
  `Cascata` (mode, O₂ setpoint, kLa demand, allocated agitation/aeration), `PID` (the seven
  live terms plus a saturation flag) and `Saída` (effort and the allocated actuators). The
  content is read-only — tuning, mode and engage stay on Controle.
- **`CascadeDetailViewModel`** formats that state and reacts to every cascade frame; the shell
  exposes it and the detail pane reaches it the same way it reaches the telemetry history.
- **Live tuning chart** (`CascadeChart`, ScottPlot) on `Controle → Cascata e sintonia`: PV,
  setpoint and effort on the left percent axis, and the kLa demand on its own right axis
  (drawn only while engaged on the path). It is fed by `CascadeTrend`, a fixed-capacity ring
  the service fills on every armed step, and redraws at 1 Hz — theme-aware, like `TrendSpark`.
- The chart sits at the top of the tuning column, where it is watched while the fields below
  are adjusted.

### Verified
- 328/328 tests pass (9 new): the trend ring (relative timing, the sparse kLa series, capacity,
  clear), the service recording a sample per armed frame and clearing on disarm, and the detail
  view's three states, mode text and change notifications.
- Live simulator run: the tuning chart plotted PV/SP/effort with its legend while the advisory
  cascade computed, and the oxygen detail pane rendered the four tabs with the Cascata overview,
  both with zero binding failures. Evidence:
  `docs/evidence/ui/phase2-wp6-tuning-chart.png`, `docs/evidence/ui/phase2-wp6-oxygen-detail.png`.

### Documentation
- [D-017](DECISIONS.md) and [PHASE_LOG P2-11](PHASE_LOG.md) updated: WP6 is complete but for the
  bioreactor field gate. Only the WP4 Phase-0 link hygiene remains open in Phase 2's P0/P1 scope.

---

## [0.16.0] - 2026-08-21

Phase 2 WP6 (part 1) — live oxygen cascade ownership and actuation. With the WP4 alarm gate
closed, the cascade can now take the wire: it replaces the linear allocator with the published
kLa path, claims the oxygen actuators through the arbiter, and actuates — safe-aborting on
stale oxygen, link loss or a loss of ownership. The velocity-form controller is unchanged.

### Added
- **kLa-path allocation** (`KlaPathAllocation`): the control effort selects a kLa demand across
  the published receipt's range, and the monotonic allocation table gives the (aeration,
  agitation) that realises it along the paper's gradient/headroom path. It replaces the linear
  window split without touching the controller.
- **Three explicit operator modes** (`CascadeMode`): `Trajetória kLa` (both actuators on the
  published path) plus the v.6 `Somente agitação` and `Somente aeração` fallbacks, which drive
  one actuator and hold the other. Nitrogen enrichment stays disabled until its own path is proven.
- **Live actuation on `CascadeService`**: `Ativar Automático` claims agitation, aeration and the
  O₂ monitor through the arbiter (`CommandOwner.Automatic`), initialises the loop bumplessly from
  the last applied actuators, and dispatches the combined frame each telemetry step. An explicit
  `Zerar integral` re-baselines the integral contribution.
- **Safe abort**: three consecutive frames without usable oxygen, an arbiter ownership revocation
  on link loss, or a manual takeover each hand the wire back to the operator and return to the
  advisory display.
- **Consumes a published receipt**: the cascade workspace lists published kLa maps from the
  store; the trajectory mode refuses to engage until one is selected.
- The `Cascata e sintonia` workspace gains the mode selector, the published-path selector, the
  engage/disengage button with its blocked-reason tooltip, the integral reset, and a live
  `kLa demandado` readout while engaged.

### Changed
- `CascadeController` takes a pluggable `CascadeAllocation`; the linear windows remain the
  default and the advisory role is unchanged. `CascadeService` now depends on the command
  arbiter and the kLa profile store.

### Verified
- 319/319 tests pass (16 new): the kLa-path allocation and its bumpless inverse, the three modes,
  ownership claim and per-frame dispatch, the bumpless transfer, and safe abort on stale oxygen,
  link loss and manual takeover — plus the advisory-never-sends regression.
- Live simulator run: connected, navigated to `Controle → Cascata e sintonia`; the new Automático
  card renders with the mode and published-path selectors, and the engage button is correctly
  gated (a trajectory run needs a published map) with zero binding failures. Evidence:
  `docs/evidence/ui/phase2-wp6-cascade-automatic.png`.

### Documentation
- Added [D-017](DECISIONS.md) and [PHASE_LOG P2-10](PHASE_LOG.md). WP6's O₂ detail-pane
  `Cascata`/`PID`/`Saída` tabs and the live PV/SP/kLa/output tuning chart are the remaining
  part 2. Field actuation on a real bioreactor stays the hardware gate.

---

## [0.15.0] - 2026-08-21

Phase 2 WP4 (part 2) — the operational alarm engine. This completes the safety-kernel
alarm gate: the six system alarms latch, are acknowledgeable, journalled, and sound an
audible indication under a timed silence. With the arbiter's safe abort (part 1), this is
the gate that had to close before any automatic actuation.

### Added
- **`AlarmService`** — a latched, acknowledgeable engine for the six §5.4.3 system alarms:
  `Link perdido`, `Módulo sem resposta`, `Fluxômetro offline`, `Dados congelados`,
  `Sensor ausente` and `Comando não confirmado`. Each has an on-delay to raise, an
  off-deadband to clear, and stays latched until it is both acknowledged and clear.
- **Returned-unacknowledged is kept.** An alarm whose condition clears while nobody has
  acknowledged it stays in the list in that third state — an alarm nobody saw is the one
  worth keeping.
- **Audible indication with a timed silence** (`IAlarmAnnunciator`): sounds while any alarm
  is annunciating; `Silenciar áudio (10 min)` mutes for a window, never permanently, and a
  fresh alarm re-sounds through an active silence.
- **A shell alarm banner** — full width, above the rails — headlining the most severe
  active alarm with its detail, a `+N` overflow count, and `Reconhecer` / `Silenciar`
  actions. The `Comando não confirmado` alarm is driven by the WP4 part 1 command lifecycle
  (an actuator whose command timed out); recovery clears it.
- Every alarm transition (raised, acknowledged, normalised, silenced) is written to Eventos
  under `Alarme`, at error severity for critical alarms and warning for the rest.

### Verified
- 303/303 tests pass (14 new): the latch/acknowledge/deadband machine, returned-unacknowledged,
  the timed-silence expiry and re-sound, all six conditions, and the exit criterion that a
  link loss produces exactly one latched alarm.
- Live simulator run: connected, then the simulator was killed — the link faulted, the
  `Link perdido` banner latched with its audible/acknowledge actions, and every reading
  degraded to an em dash with zero binding failures. Evidence:
  `docs/evidence/ui/phase2-wp4-alarms-painel.png`.

### Documentation
- Added [D-016](DECISIONS.md) for the alarm-engine model and
  [PHASE_LOG P2-09](PHASE_LOG.md). WP4 in the roadmap is now complete but for the Phase 0
  P2/P3 link cleanup, which is hygiene rather than a gate. The full **Alarmes** page
  (active/history/configuration, [UI_DESIGN §5.4](UI_DESIGN.md)) remains Phase 5 and builds
  on this engine; per-variable limits and the persistent-foam alarm arrive with it.

---

## [0.14.0] - 2026-08-20

Phase 2 WP5 — the D-008 kLa experimental mapping and allocation-profile workspace.
It reproduces the current paper algorithm from operator-entered measurements; no paper
surface or active production profile is bundled.

### Added
- **Mapeamento kLa**, the eighth currently implemented main destination (`Ctrl+8`): named experiments, metadata,
  physical airflow/agitation domain, an editable measured `(Q_g,N,kLa)` table and a blank
  3² coordinate helper. Incomplete worksheets save exactly as drafts without becoming
  numerical input.
- Pure managed reference pipeline: C1 Clough–Tocher reconstruction on 300² nodes, nearest
  fill, reflected Gaussian smoothing, not-a-knot bicubic values/normalized derivatives,
  SciPy-compatible adaptive RK45, 150² maximum-mean-headroom search, low-to-high path
  orientation and monotonic kLa-to-`(Q_g,N)` allocation.
- Professional two-plot scientific workspace: surface/contours, measured anchors, normalized
  gradient arrows and selected path above the candidate-headroom landscape. Algorithm
  identity, units, residuals, convex-hull coverage, start, headroom, range, allocation count,
  warnings and fingerprints remain visible beside it.
- Explicit `Rascunho → Superfície estimada → Trajetória válida → Revisada → Publicada`
  lifecycle. Long stages run off the dispatcher with progress/cancel and reject stale
  results by input fingerprint. Every numerical parameter is exposed; preview/custom runs
  calculate but cannot publish as the paper method.
- Create-only, versioned JSON receipts with complete inputs/settings, review note,
  diagnostics, allocation and SHA-256 fingerprints. Read/export/load/import verify integrity;
  export is byte-identical and import always becomes a new draft needing local review.
- A parallel development-time SciPy oracle generator under `tools/kla-reference/`, plus
  managed cross-language surface/derivative/path/headroom fixtures. Python is not a runtime
  dependency.

### Safety boundary
- The fitting and receipt services do not depend on `IDeviceService`. Calculate, review,
  publish, import and export send no commands and activate no profile. Live consumption is
  WP6 work behind WP4 automatic ownership.
- A clean profile store is empty. Test paper data remains in the test assembly only.

### Verified
- 289/289 headless tests pass, including SciPy numerical tolerances, full reference candidate
  selection, monotonic/clamped allocation, scientific refusals, incomplete-draft round-trip,
  version continuity, byte-for-byte reopen/export, receipt-tamper refusal, empty-install and
  custom-publication refusal.
- The full managed 150×150 search completes in about 3.2 s on the development machine.
- Light/dark runtime review of the populated workspace recorded zero new XAML binding
  failures or unhandled exceptions. Evidence: `docs/evidence/ui/phase2-wp5-kla-*.png`.

### Documentation
- Added [KLA_MAPPING.md](KLA_MAPPING.md), P2-08 in the phase log, WP5 completion evidence in
  the roadmap, implementation detail in D-008/architecture and the SciPy BSD notice.

---

## [0.13.0] - 2026-08-20

Phase 2 WP4 (part 1) — the P0 command-path safety kernel: one arbiter owns the wire, a
honest command lifecycle, safe abort on link loss, and the operator session clock. This is
the gate that must close before any automatic actuation, so it ships before live cascade
control (WP6) can add a second command source.

### Added
- **`CommandArbiter`** — the single gate onto the wire. It decorates the transport wrapper,
  so the `IDeviceService` the whole application resolves *is* the arbiter and nothing can
  send without an owner. Ownership is tracked **per actuator** (temperature, agitation,
  oxygen, aeration, pressure, pH dosing): a command is sent only if the requester owns every
  actuator it touches, and one owned-by-another actuator refuses the whole frame atomically.
- **Explicit, journalled, bumpless ownership transfer.** `Manual` owns everything until a
  `Claim`; transfers carry the last commanded state so a new owner starts without a setpoint
  jump. `Automatic` and `Recipe` remain unreachable from the UI until WP6 and Phase 3.
- **Safe abort.** A link or feedback loss revokes every non-Manual owner back to Manual and
  raises an alarm-severity event; any command still outstanding is timed out.
- **Command lifecycle** — `Issued → TransportAccepted → TelemetryConfirmed / TimedOut`,
  honest per channel: only aeration is telemetry-confirmed (`FlowSetpoint` echo, with
  `FlowCommandAck`), and every other actuator rests at transport-accepted and says "sem eco".
- **Operator session clock** — a `Zerar tempo da sessão` header button, command-palette
  entry and `IDeviceService.ZeroSessionTime()`. It rebases the local display/log offset
  exactly as v.6 did, without ever resetting the device clock or rewriting logged samples.
- Eventos now records ownership transfers, refused commands, timed-out commands, safe aborts
  (alarm severity) and session-clock zeroing.

### Changed
- `CommandOwner` moved from the shell into the communication layer, beside the arbiter.
- The `IDeviceService`/transport gain a `ZeroSessionTime()` method and a `SessionTimeZeroed`
  echo, marshalled onto the worker so the readings stay the worker's to own.

### Verified
- 280/280 tests pass (21 new): arbiter ownership/atomic refusal/bumpless transfer, the full
  lifecycle including timeout and the aeration-only confirmation, safe abort on link loss,
  journal integration, and the session-clock rebase at both the `SensorReadings` and
  `ConnectionManager` levels.
- Live localhost simulator run: connected over Wi-Fi, first frame in 1210 ms, telemetry
  flowing through the arbiter, the `Zerar` button enabled, and zero XAML binding failures or
  exceptions. Evidence: `docs/evidence/ui/phase2-wp4-arbiter-painel.png`.

### Documentation
- Added [D-015](DECISIONS.md) for the command-arbiter model. WP4 in the roadmap is now
  annotated part-done (ownership, lifecycle, safe abort, session clock) with the alarm engine
  and Phase 0 link cleanup remaining.
- Re-audited the remaining v.6 operator/device features and ordered them P0-P2 by field
  safety and dependency. Automatic ownership, system alarms and hardware protocol closure
  now precede live cascade actuation; biomass/external-pump parity precedes Receitas.
- Corrected [D-008](DECISIONS.md): `Mapeamento kLa` is a dedicated experimental workflow
  that estimates `kLa(Q_g,N)` from operator-entered anchors, calculates the paper's
  normalized gradient/headroom path and publishes a reviewed allocation profile. It is not
  a pre-loaded surface selector, and a fresh production install has no active map.
- Added Phase 2 WP4-WP8 and Phase 3 WP1-WP4 acceptance plans to the roadmap, plus the ninth
  main-window specification for the kLa mapping workflow.

---

## [0.12.0] - 2026-08-20

Phase 2 WP3 — complete pH control plus guided pH, oxygen and airflow calibration.
Calibration stays app-side where v.6 owns it; only the flow curve belongs to the
dedicated flowmeter firmware.

### Added
- **Calibrações**, a dedicated three-tab operator workspace:
  - pH one- and two-point acquisition with distinct-frame stability/averaging, explicit
    proposal/apply, equal-raw refusal and a dosing safe-stop interlock;
  - direct oxygen zero/span capture around the existing app-side linear coefficients;
  - certified-flow point capture, 10-frame `FlowVoltage` averaging, v.6's fixed 0.0545 V
    split, live curve plot and partial/complete six-coefficient send.
- **Complete pH dosing control** on Painel and Controle: setpoint, inactive band, pump-on
  time, mix/rest time and speed, sent atomically with the v.6 `% × 10` encoding. Invalid
  input is refused instead of silently becoming pH 7.
- Pure `CalibrationMath`, persisted flow points and pH-control presets, simulator support
  for all five pH fields, `pHCal` display echo and flow coefficients.
- [CALIBRATION.md](CALIBRATION.md), the operational contract for ownership, procedures,
  interlocks, equations and validation boundaries.

### Changed
- pH is no longer read-only. Probe calibration and dosing remain separate lifecycles:
  calibrated values are computed in the app and echoed as quoted `pHCal`; dosing uses the
  independent five-field state.
- The operator safe-stop now merges the frozen Phase 1 core stop with a complete pH stop.
- Settings follows calibration changes live, so applying an unrelated staged setting can
  no longer restore stale pH or oxygen coefficients.
- Numbered navigation now uses `Ctrl+1`–`Ctrl+7`; Calibrações is destination 6 and
  Configurações is destination 7.

### Fixed
- Airflow calibration keeps every capture action reachable at 1280×800 and locks point
  editing during acquisition.
- The airflow ScottPlot surface and point list repaint correctly after a live light/dark
  theme change.
- Losing the link invalidates an in-progress pH/flow acquisition and requires an explicit
  restart or flow-point preparation instead of trusting stale command state.

### Verified
- 259/259 tests pass, including golden pH/flow frames, calibration equations and refusals,
  simulator acceptance, calibration/settings synchronization and connection-loss paths.
- Localhost HTTP simulator accepted the combined pH state, quoted display echo, six flow
  coefficients and the subsequent safe-stop with `200 OK` / buffered `OK`.
- Runtime review at 1280×800 covered all three tabs and both themes with zero new XAML
  binding failures or fatal exceptions. Evidence:
  `docs/evidence/ui/phase2-calibration-{ph,oxygen,airflow,airflow-dark}.png` and
  `phase2-ph-control-painel.png`.
- This is software/simulator evidence only. Buffer solutions, an oxygen reference,
  certified airflow standard, pump direction and physical interlocks still require the
  bioreactor hardware gate. The existing SkiaSharp `NU1701` warning is unchanged.

---

## [0.11.0] - 2026-08-20

Phase 2 WP2 — the cascade tuning workspace, wired to the WP1 controller in an advisory
role. The cascade now computes against live oxygen telemetry and is tunable on screen; it
still does not send.

### Added
- **`CascadeService`** (advisory runtime) — owns a `CascadeController`, subscribes to
  telemetry, and steps the loop on each dissolved-oxygen frame using the real elapsed time
  between frames (a `TimeProvider`, so it is deterministically testable). It computes what
  the cascade *would* command and exposes the terms; it never calls `IDeviceService.Send`.
  Arming resets the loop; disarming clears the live terms.
- **`Controle → Cascata e sintonia`** — the tuning workspace as Controle's second tab
  (`CascadeTuningView` / `CascadeTuningViewModel`):
  - **Malha**: O₂ as the controlled variable, the O₂ setpoint, and the manipulated
    checklist (agitação/aeração always on this phase; enriquecimento N₂ disabled with a
    reason).
  - **PID**: editable `Kp` · `Ki` · `Kd` · `I_min` · `I_max` · prediction horizon · rate
    window · interval, with staged apply/revert and validation (interval band, output and
    integral windows, non-negative gains).
  - **Janelas de atuação**: editable agitation/aeration windows and a live stacked
    allocation bar with the current-effort marker.
  - **Termos ao vivo**: `P` · `I` · `D` · `dSaída` · `Saída` · `DOT_pred`, plus the measured
    O₂, rate, error and the allocated agitation/aeração, updating each frame.
  - Footer: apply, revert, and named **Salvar/Carregar sintonia** (persisted; loading only
    stages, never actuates).
- `CascadeSettings` and `CascadeTuningPreset` in the typed settings record; `TimeProvider`
  registered in the composition root.
- `DoubleToStarConverter`, for the allocation bar's proportional columns.
- 15 tests: advisory-computes-but-never-sends, arming/disarming, sentinel handling,
  configure, and the workspace's validation, apply/revert, save/load and live-terms binding.

### Changed
- Controle is now a two-tab page (`Parâmetros` · `Cascata e sintonia`); the Phase 1 table,
  valves and footer are unchanged, inside the first tab.

### Notable
- **Still advisory.** The cascade does not actuate: live sending waits for command
  ownership (`Automático`) and the bioreactor, a later Phase 2 WP. The `Trajetória kLa`
  panel is a placeholder pending the [D-008](DECISIONS.md) surface, and a live tuning chart
  is deferred with it.

### Verified
- 242/242 tests pass (227 + 15). Build clean; `ResourceKeyTests` confirms every resource
  key in the new views resolves.
- Live simulator run: the app opened on `Controle → Cascata e sintonia`, connected over the
  localhost Wi-Fi simulator, streamed telemetry, and rendered the workspace with **zero XAML
  binding failures** and no fatal exception (first frame 1017 ms). The existing SkiaSharp
  `NU1701` warning is unchanged.

---

## [0.10.0] - 2026-08-20

Phase 2 WP1 — the cascade controller core. The scientific payload's control law, built and
validated headlessly before it meets the wire or the UI. No app behaviour changes yet.

### Added
- **`OpenTECHub.Services.Control`** — the Phase 2 controller home, pure math with no WPF,
  no telemetry and no wire dependency:
  - `LeastSquaresRateEstimator` — windowed slope fit that rejects the polarographic probe's
    quantisation staircase, so the derivative and prediction consume a clean rate rather
    than a noisy endpoint difference.
  - `VelocityPidController` — the corrected cascade law carried from the ReceitasOpenTEC
    design: **velocity-form output** (holds the actuator at setpoint instead of collapsing
    to zero the way v.6's positional PID did), **structural anti-windup** (the clamped
    output is the integrator; a reported integral bounded by `I_min`/`I_max` is held while
    railed), derivative-on-measurement, and the **prediction horizon**
    `DOT_pred = DOT + rate·t_pred` that compensates the 20-40 s probe dead time. Exposes the
    exact live terms the tuning workspace shows — `P`, `I`, `D`, `dSaída`, `Saída`,
    `DOT_pred`.
  - `ActuatorWindowAllocator` / `ActuatorWindow` — splits one control effort across
    agitation and aeration with overlapping windows (the `Cascata e sintonia` stacked bar).
  - `CascadeController` — composes the loop and the split, mapping one step onto the frozen
    combined actuation frame through `CommandBuilders.CascadeActuation`. It does **not**
    send: a caller queues the frame on the shared command queue.
  - `CascadeTuning` / `CascadeTerms` — the tunable parameters and the decomposed readout,
    each field a control on `Controle → Cascata e sintonia` (UI_DESIGN §5.2).
- 37 controller tests over a simulated first-order DOT plant with dead time
  (`FirstOrderDeadTimePlant`): staircase rejection, the setpoint-hold and no-windup
  properties, a prediction-reduces-overshoot comparison, closed-loop tracking of both the
  bare PID and the full cascade, allocator overlap behaviour, tuning validation, and a
  culture-pinned golden cascade frame.

### Notable
- **The controller does not send, and is not wired into the running app.** WP1 is the
  validated control core; the kLa surface ([D-008](DECISIONS.md)), gain scheduling, the OUR
  soft sensor, the dosing subsystems, and the whole UI — the tuning workspace, the `oxygen`
  detail-pane tabs, mode ownership and live actuation — are later Phase 2 work packages.
- The default gains are provisional simulator values. A small `Kp` is deliberate: the
  prediction folds into the error, so `Kp·horizon` is the loop's effective derivative gain,
  and a large proportional term on a dead-time process injects a huge derivative and
  limit-cycles. Field gains need the bioreactor.

### Verified
- 227/227 tests pass (190 baseline + 37 new). The existing `SkiaSharp.Views.WPF` `NU1701`
  compatibility warning is unchanged; the new code adds no analyzer warnings.

---

## [0.9.0] - 2026-08-20

Phase 1b WP8 plus the Phase 1 visual/responsive closure.

### Added
- Persisted normal window bounds, maximized state and the last valid page, all stored by
  stable fields in the typed settings record. Invalid or off-screen bounds recover into
  the current virtual desktop instead of reopening an unreachable window.
- `Ctrl+K` command palette with searchable pages and shell actions, keyboard cycling,
  Enter execution, Escape dismissal, disabled-action reasons and focus restoration.
- Keyboard map for current destinations (`Ctrl+1`–`Ctrl+6`), variable rail (`Ctrl+R`),
  reconnect (`F5`), chart pause/resume (`Space`) and contextual dismissal (`Esc`). Slots
  `Ctrl+7`–`Ctrl+8` remain reserved for later roadmap destinations.
- A shared 2 px accent focus adorner applied to buttons, fields, selectors, switches,
  tabs, expanders and custom list-item containers.
- A professional 1024 × 1536 RGBA reactor master for Painel: double-wall jacket,
  top-drive motor and entries, four process probes, two Rushton turbine levels, and an
  independently fed annular sparger. The shaft stops below the lower turbine and never
  continues to the sparger.
- Normalized equipment anchors, six keyboard-accessible live callout cards including pH,
  path-role leader lines, system/update header, honest no-level footer, and a vector
  decode fallback. The generated equipment contains no process state.
- Asset provenance, accepted generation prompt, SHA-256 and alpha/geometry contract in
  `docs/ASSET_PROVENANCE.md`.

### Changed
- `Ctrl+S` is reserved for the Phase 3 recipe editor. Today it opens the command palette
  on a disabled `Salvar receita` action whose reason is explicit; it never invents a save.
- Space pauses charts only when focus is not owned by an interactive input or selector.
- The navigation rail collapses to the specified 52 px icon strip below 1400 px and
  restores its labels automatically when space returns.

### Fixed
- The destructive-command preview's read-only field now binds one-way, avoiding a
  binding write attempt when the dialog opens.
- Closing the command palette by its button or outside click restores the prior keyboard
  target as reliably as Escape and command execution.
- The inline ScottPlot trend now repaints after a live theme change instead of retaining
  a white plotting surface in the dark Painel.

### Verified
- 190/190 tests pass, including window recovery, reconnect ordering, accessibility,
  command-palette structure, compact navigation, RGBA/alpha validation, normalized
  anchors and raster fallback wiring.
- Live simulator review exercised numbered navigation, chart pause/resume, command
  search, the Phase 3 recipe reservation, variable-rail toggle and F5 reconnect.
- A 1450×850 window at (140, 90), Eventos and the expanded variable rail all restored on
  relaunch; the fresh run logged zero XAML binding failures or fatal exceptions.
- Evidence: `docs/evidence/ui/wp8-command-palette.png` and
  `docs/evidence/ui/wp8-keyboard-focus.png`, plus
  `phase1-final-painel-{light,dark}.png` and `phase1-final-responsive-1280.png`. The final
  simulator interval logged no binding failure, fatal exception or unhandled exception.
  Release publish succeeded and its WPF resource bundle contains both reactor files.
  The existing `SkiaSharp.Views.WPF` `NU1701` compatibility warning remains unchanged.

---

## [0.8.0] - 2026-08-20

Phase 1b WP7 — completed pages, history/audit tooling, and display units.

### Added
- **Históricos**, a persisted-session browser separate from Gráficos. It inventories
  name, date, duration, row count, size and connection medium; previews first/last rows;
  and refuses graph loading when the header differs from `SessionLogFormat.Header`.
- Session CSV export and loading into the existing dedicated two-panel graph workspace.
  Gráficos also gains PNG/visible-data CSV export and a synchronized cursor readout.
- **Eventos**, a bounded, filterable eight-source audit journal. Successful transport
  writes publish their exact merged JSON only after the write succeeds, so command
  evidence remains honest over USB and Wi-Fi.
- Session-log controls on Eventos: start/stop, new file, path, row count, size and folder.
- In-page Configurações navigation and the display-only `Unidades` section (`°C`/`°F`,
  `kPa`/`mmHg`/`bar`, nominal vessel volume). Protocol and session values stay canonical.
- Confirmation for `resetVariables` and `restart`, showing exact JSON and defaulting to
  cancellation.

### Changed
- The roadmap's proposed `Gráficos`→`Históricos` rename was intentionally not applied.
  `Gráficos` remains the focused live/session dual-chart page; `Históricos` owns file
  discovery and hands an accepted session to it.
- Session logging now emits state/row notifications consumed by Eventos and the shell.

### Fixed
- Display-unit conversion residue can no longer leak values such as
  `36.99999999999999` into command JSON.
- Eventos no longer dereferences its second filter during first-filter initialization.
- Read-only event/session previews use one-way bindings, so selecting a row displays its
  detail without a WPF binding failure.

### Verified
- 178/178 tests pass, including post-write command evidence, rejected-write exclusion,
  session header/row parsing, unit round trips, event initialization and destructive
  confirmation.
- Localhost simulator live at 1280×800; Gráficos, Históricos, Eventos and Unidades were
  exercised with zero XAML binding failures. Evidence is in `docs/evidence/ui/wp7-*.png`.
- The existing `SkiaSharp.Views.WPF` `NU1701` compatibility warning remains unchanged.

---

## [0.7.0] - 2026-08-20

Phase 1b WP6 - the all-setpoints Controle page and explicit valve control.

### Added
- **Controle page** with all five core subsystems in one table: live PV, last applied
  setpoint, staged setpoint, range, active state, mode, owner, and per-row actions.
- **One-frame bulk apply.** Dirty subsystem commands are merged into one flat JSON
  object; invalid cross-field flow/maxFlow combinations are refused rather than clamped.
- **Valve card** for `valve_1` (auxiliary) and `valve_2` (nitrogen), with the physical
  valve telemetry beside each toggle and the derived/inverted `v_Flow` state read-only.
- Typed, named setpoint presets. Loading a preset stages fields and is asserted never to
  send a command.
- **Parada segura**, the application's only red button, behind a default-cancel dialog
  that displays the exact JSON before sending the complete five-subsystem safe state.
- Eight WP6 acceptance tests and light/dark simulator evidence in `docs/evidence/ui/`.

### Changed
- The variable rail now obeys its responsive visibility state. It stays hidden on
  Controle because the page already shows every live PV and needs the full width at
  1280 px.
- `SubsystemViewModel` now separates command construction from commit, allowing bulk
  apply to reuse exactly the same validation and state transitions as per-row apply.

### Fixed
- Clean app shutdown no longer logs a fatal exception: the dependency container is now
  disposed asynchronously because settings, session logging, and the device service are
  async-only disposables.
- The shared `ListViewItem` template now honours its `Padding` property; WP6 can compact
  its five rows without clipping the pressure row.

### Verified
- 169/169 tests pass.
- Localhost simulator connected with live telemetry; all five rows and actions were
  visible at 1280×800 in both themes with zero XAML binding failures.

---

## [0.6.0] - 2026-08-19

Advanced settings - **Phase 1 complete**.

### Added
- **Settings page** - connection options, probe calibration, spike-filter tuning,
  session-log path, theme, and the device commands (reset variables, restart
  communications, restore factory values).
- Edits are **staged and applied together**, with Revert. Calibration coefficients are
  a pair; applying a new slope against an old intercept would put wrong numbers on
  screen and into the log.
- **Live calibration preview** - what the current raw count decodes to with the
  coefficients as typed.
- `FilterSettings` in the persisted settings record, and `AppSettings.ToParserConfig()`
  now carries filter tuning through to the running parser.
- `docs/evidence/ui/` - a capture of every page, for the Phase 5 interface review.

### Changed
- Advanced settings is a **page**, not the separate window UI_DESIGN specified. The nav
  rail already had the slot, the KPI strip keeps safety context visible, and it is one
  implementation rather than two. Doc updated to match.

### Fixed
- The calibration preview was computed once before any telemetry arrived and never
  recomputed, so it read "sem leitura bruta disponível" permanently.

---

## [0.5.0] - 2026-08-19

Charts and session logging - Phase 1 feature-complete.

### Added
- **Charts page** - at most two panels side by side, each selectable from 11 channels,
  with a selectable time window and a pause control. Mirrors what v.6's graphs page
  offered, minus OUR (Phase 2 soft sensor).
- **`TelemetryHistory`** - fixed-capacity ring buffer, ~48 h at the field `dataDelay`,
  stride-downsampled to 2000 points before reaching a plot. Sentinels stored as `NaN`
  so charts show a gap rather than a line diving to -1.
- **`SessionLogger`** - tab-separated log byte-compatible with v.6, so existing
  analysis scripts keep working. Invariant numbers, UTF-8 without BOM, header only
  when the file is new.
- **Log page** - device messages plus start/stop logging.
- Page switching from the nav rail; pages stay in the visual tree so a chart does not
  rebuild every time the operator glances elsewhere.
- Chart series brushes (Okabe-Ito) in the token dictionaries.

### Fixed
- `TextTrimming="MiddleEllipsis"` is a WinUI value; WPF only has `None`,
  `CharacterEllipsis` and `WordEllipsis`. It threw at XAML parse time.
- Selector combo boxes showed the record type name instead of the label.
- Collapsing the second chart panel now collapses its grid column, not just the
  border - otherwise the remaining chart stayed at half width beside an empty gap.

---

## [0.4.0] - 2026-08-19

Phase 1's two remaining screens: the synoptic and setpoint entry.

### Added
- **Synoptic** - vector schematic of the reactor with each live value pinned at the
  hardware that produces it: jacket, motor, probes, sparger, headplate. Clicking any
  element selects it. Themed and scalable, not an image.
- **Detail pane** - reading, setpoint entry, validation, apply/revert, and the
  acknowledged state. Only one subsystem's controls on screen at a time, which is what
  stops twelve of them becoming the wall the v.6 card board was.
- **Responsive layout** - side pane at 1200 px and above, drawer below. Same ViewModel,
  so behaviour is identical and only presentation changes.
- **Setpoint entry** for all five core subsystems, with ranges paired to their command
  builders so validation and the wire cannot drift apart.
- ViewModel tests: `OpenTECHub.Tests` now references the app assembly.

### Fixed
- **Validation guarded only the button, not the send.** `Apply` checked whether the
  text parsed, so an out-of-range value like `60.1` on a 15-60 range parsed fine and
  was sent. Anything reaching the method other than the button - the Enter key, a
  future recipe engine - bypassed the range and integer rules. Found by the tests.
- The pending-change marker no longer fires when the constructor seeds the field from
  persisted settings; it lit on every subsystem at launch.

---

## [0.3.1] — 2026-08-19

### Added
- **`OpenTECHub.Simulator`** — stands in for the ESP32-S3 and the bioreactor behind it.
  HTTP on localhost (no driver, no admin, no reboot) or serial over a virtual COM pair.
  Full design in [SIMULATOR.md](SIMULATOR.md).
  - Reproduces the measured firmware quirks: the buffered `OK` served by `/readData`
    after a POST, `[ESP32_` log lines interleaved on serial, ETag/304 per published
    frame, `/ping` → `pong`, 404 elsewhere.
  - Emits **raw ADC counts**, inverting the field calibration, so the app's calibration
    and spike-filter path is genuinely exercised rather than bypassed.
  - First-order process model with a 25 s oxygen probe dead time — the piece the
    cascade's prediction horizon exists to compensate.
  - Fault injection: `no-module`, `stall`, `dropout`, `spikes`, `noise`, `drift`,
    `garbage`.

### Fixed
- `TransportFaultException` now folds the inner exception's message into its own.
  "read failed" alone cannot distinguish a timeout from a refused connection, and that
  message is what reaches the connection popover.

### Verified
- App connected to the simulator over localhost showing live values; calibration round
  trip confirmed (`Oxyval:3926.6` → 94.9 %, `pHval:15178.7` → 7.01).
- The `stall` scenario correctly triggers the Wi-Fi silence timeout added in 0.2.1.

---

## [0.3.0] — 2026-08-19

Phase 1 begins: the application shell, running and connected to real hardware.

### Added
- Composition root with DI, Serilog rolling file, and crash handlers wired from the
  first line of startup.
- `AppSettings` - one typed record, JSON, debounced atomic writes, corrupt files
  quarantined rather than deleted. Replaces v.6's 370 lines of hand-marshalling.
- `DeviceService` - wraps `ConnectionManager` and marshals telemetry to the UI thread,
  so the protocol layer stays free of any UI dependency.
- `ThemeService` - light/dark, following the Windows app theme live.
- Shell: title bar with the connection chip and popover, navigation rail, and the
  always-visible KPI strip.
- Fluent control templates (Button, TextBox, ComboBox, RadioButton, CheckBox,
  Separator, ToolTip) - WPF's built-in chrome ignores the design tokens entirely.

### Verified on hardware
- **First frame at 662 ms** (budget 2000 ms); connected to COM3 **599 ms after process
  start**, with auto-connect firing only after the window was rendered.

### Notable
- Agitation is displayed as **commanded, not measured**: the firmware sends no RPM
  feedback, and v.6 logs the commanded value in the same column as measured ones.
- Sentinels render as an em dash, never `0` - zero is a legitimate reading for
  pressure and flow.
- Two WPF traps recorded in [PHASE_LOG.md](PHASE_LOG.md): a font stack containing CSS
  keywords plus a Windows-11-only family overflowed the stack in font fallback before
  the first frame, and `ConverterParameter` cannot be bound.

---

## [0.2.2] — 2026-08-19

### Changed
- **The DTR/RTS reset pulse is off by default.** Measured as the cause of the reboot
  on every USB connect: with the pulse the device clock fell 102.1 s to 2.8 s across a
  reconnect while 5.1 s of wall time passed; without it the clock advanced 5.5 s
  against 5.5 s. Connect 1903 ms -> 13 ms, discovery 1.9 s -> 0.1 s, and device state
  now survives a reconnect. The 1.8 s boot settle now applies only when pulsing, since
  it existed solely to wait out the self-inflicted reboot.
- `ConnectionManager` escalates to a pulsed (hardware-reset) connect after
  `FailuresBeforeHardwareReset` handshake failures, for the hung-firmware case that
  nothing else recovers.

### Added
- `opentec-harness reset-test` — determines what reboots the board on connect and finds
  the minimum viable boot settle.
- [PHASE_LOG.md](PHASE_LOG.md) — record of decisions taken while executing each phase.

### Documented
- PROTOCOL.md Q6 answered and the USB parameter table corrected.
- ROADMAP.md now tracks Phase 0 deliverables and the P1/P2/P3 follow-ups as checkboxes.

---

## [0.2.1] — 2026-08-19

Two link-supervision defects found by review after the hardware runs, plus the first
tests for the state machine itself.

### Fixed
- **Wi-Fi had no silence detection.** The timeout was gated on `Medium == Usb`, so a
  Wi-Fi link whose telemetry stalled while the web server stayed up (answering 304
  forever) would never time out - the app would show frozen readings behind a healthy
  "Connected" indicator. `TelemetrySilenceTimeout` now covers both transports and is
  measured against parsed telemetry, not against any traffic.
- **The heartbeat never ran.** `HeartbeatInterval` was declared and
  `TestConnectionAsync` implemented, but nothing called either. Replaced with
  two-stage liveness: quiet for `LivenessProbeAfterSilence` (4 s) starts probing;
  quiet for `TelemetrySilenceTimeout` (8 s) drops the link even if the probe succeeds.
  A healthy link sends no probes at all - verified on hardware, 45 s USB run with
  `liveness probes: 0`.

### Added
- `ConnectionManagerTests` - 14 tests against a scriptable `FakeTransport`, covering
  silence on both media, probe failure, command coalescing, requeue after a rejected
  write, reconnect cycling, read faults and device-log/ack classification. The Wi-Fi
  silence test was verified to fail against the previous implementation.
- `ConnectionManager` accepts a transport factory, so the state machine can be driven
  without hardware.
- `LinkDiagnostics.LivenessProbes`, surfaced in the harness summary.

### Changed
- `ConnectionOptions.UsbSilenceTimeout` renamed to `TelemetrySilenceTimeout` (it is no
  longer USB-specific); `HeartbeatInterval` replaced by `LivenessProbeAfterSilence`.

---

## [0.2.0] — 2026-08-19

Phase 0: the protocol stack, built and validated over USB against a real ESP32-S3.
No UI yet. See [PHASE0_RESULTS.md](PHASE0_RESULTS.md) for the measurements.

### Added
- `OpenTECHub.Protocol`: `ITransport` + `SerialTransport` + `HttpTransport`,
  `OpenTECCommand` (culture-invariant by construction), `CommandKeys`/`TelemetryKeys`,
  `CommandBuilders`, `TelemetryParser`, `SpikeFilter`, `ConnectionManager`.
- `OpenTECHub.Harness`: console harness for hardware validation, with a wire-trace
  log for byte comparison against v.6 `command_logs/`.
- 49 tests: golden wire strings, telemetry semantics, spike-filter behaviour, and a
  culture fixture that forces pt-BR.

### Verified on hardware
- USB handshake, 60 s continuous telemetry, 0 parse failures, 0 spurious reconnects.
- Parallel port discovery finds the board in **1.9 s** with no port configured
  (v.6 would take ~12 s serially on the same machine).
- Wi-Fi on the `Modulo_OpenTEC_1` SoftAP: 20/21 checks passed, 0 warnings. 90 s soak
  gave 45 frames at ~2.1 s with 0 parse failures on a single connect; ETag 304
  conditional polling confirmed; latency p95 33 ms, ~15x headroom over the v.6
  timeouts; reconnect resumes telemetry with the ETag correctly cleared.
- Wi-Fi reconnects do **not** reboot the device (no reset line), making Wi-Fi the
  safer transport for mid-run recovery.

### Fixed relative to v.6
- `OK` acknowledgements and `[ESP32_` log lines are recognised instead of being
  counted as parse failures. v.6 counts the ack, so several commands in quick
  succession can trip a false link-loss there.
- Link-silence detection is a duration, not a count of empty reads — the count
  couples failure detection to the poll rate.
- Wi-Fi read faults raise `TransportFaultException` instead of returning null, which
  in v.6 made link loss indistinguishable from "no new data".
- Parser defaults now match the calibration actually in the field; v.6's hard-coded
  defaults disagreed with `preferences.json`.

### Documented
- Wire-level finding: `OK` and device log lines share the USB telemetry stream
  ([PROTOCOL.md](PROTOCOL.md) section 2.0), answering open question Q4.
- Hardware finding: the DTR/RTS pulse means every USB reconnect reboots the board and
  discards its process state. Raised as Q6; `PulseResetOnConnect` defaults to v.6
  behaviour pending validation.

---

## [0.1.0] — 2026-08-19

Project scaffold and plan. No application features yet.

### Added
- Solution scaffold: `OpenTECHub.Protocol` (net10.0, no WPF) + `OpenTECHub`
  (net10.0-windows, WPF) + `OpenTECHub.Tests`. Builds clean with 0 warnings.
- Build configuration: `Directory.Build.props` (single-source version, analyzers,
  language standards), `global.json` (SDK pin), `.editorconfig` (naming rules
  enforced at build).
- Fluent design tokens for light and dark themes, plus the shared brush, typography
  and geometry scales.
- Documentation set: [ROADMAP](ROADMAP.md), [PROTOCOL](PROTOCOL.md),
  [MIGRATION](MIGRATION.md), [ARCHITECTURE](ARCHITECTURE.md), [UI_DESIGN](UI_DESIGN.md),
  [DECISIONS](DECISIONS.md), [CONVENTIONS](CONVENTIONS.md).

### Decided
- .NET 10 LTS, self-contained publish ([D-003](DECISIONS.md#d-003--net-10-lts-self-contained)).
- Fluent light + dark following the system theme ([D-004](DECISIONS.md)).
- Dashboard as synoptic + detail pane + KPI strip ([D-005](DECISIONS.md)).
- Auto-connect with a status chip; Configurations stops being a destination ([D-006](DECISIONS.md)).
- pt-BR UI, English code ([D-007](DECISIONS.md)).
- Recipes as a node canvas, new implementation and new identity ([D-009](DECISIONS.md)).
- kLa gassing-out and torch leave the controller app ([D-010](DECISIONS.md)).

### Documented from the v.6 source
- The complete ESP32-S3 wire contract: both transports, all telemetry keys, ~60
  command keys, signal conditioning, and the timing constants that are load-bearing.
- Startup cost analysis: `import torch`, and serial probing that can exceed 10 s per
  unresponsive port with no way to cancel it.
- 11 defects found while reading v.6, each with a disposition.
