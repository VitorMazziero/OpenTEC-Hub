# Plano de adequação da interface para notebooks

Status: **implementado em 10/09/2026** nas etapas 1 a 4; a etapa 5 (matriz visual por escala e
tema) segue pendente. A proposta original de 10/09 está preservada abaixo, a partir da seção 1;
esta seção registra o que foi de fato entregue, o que mudou em relação à proposta e o que ficou
de fora. Ver [DECISIONS D-045](../DECISIONS.md) e o [CHANGELOG](../CHANGELOG.md).

## 0. Estado da implementação

### 0.1 Entregue

| Etapa | Entrega | Evidência |
|---|---|---|
| 1 | `PowerLabel` e `CompactField` movidos para `Themes/Controls.xaml`; o diálogo de critérios abre | Aberto no app publicado com workspace temporário, incluindo as opções de estabilização expandidas; log da execução sem erros e sem `Logs/Crash/` |
| 1 | `MinTrackSize` preenchido em `WM_GETMINMAXINFO`, com DPI da janela | `WindowChromeMaximizeFix`; mínimo passa a 1024 × 640 DIP |
| 1 | Glifo do X redesenhado em meio pixel e chrome unificado em um único template | `ShellChromeTests.Close_glyph_is_drawn_inside_its_box` e `Caption_button_templates_are_defined_once_for_every_window` |
| 2 | Navegação compacta de 56 DIP abaixo de 1440 DIP, com drawer sobreposto, `Esc`, clique fora e nome de sessão acessível | Executado no app a 1280 DIP |
| 2 | Versão curta no cabeçalho, string completa no tooltip | `ShellVersionLabelTests` |
| 3 | Controle: colunas proporcionais com piso/teto; "Parada segura" em coluna medida antes das predefinições | `CompactLayoutTests` |
| 3 | Receitas: painéis laterais estreitam sob 1200 DIP; JSON/Validação com altura limitada | `CompactLayoutTests` |
| 3 | Determinar kLa: painel de preparação a 360 DIP e seletor de gráfico sob 720 DIP de altura | `Kla_determination_shows_one_plot_at_a_time_when_the_page_is_short` |
| 3 | Mapeamento kLa: abas Dados / Superfície / Diagnóstico sob 1200 DIP | `Kla_mapping_compact_section_takes_the_whole_row` |
| 3 | Potência: métricas em `WrapPanel`, altura de gráfico flexível e um gráfico por vez sob 900 DIP | `CompactLayoutTests` e execução no app |
| 4 | Configurações: ilustração cede espaço e o menu vira seletor sob 1000 DIP | `CompactLayoutTests` |
| 4 | Calibrações: coluna de pontos certificados devolve ~110 DIP à curva sob 1100 DIP | `CompactLayoutTests` |
| 4 | Diálogos limitados à área do proprietário antes de `ShowDialog` | `DialogBoundsTests`; `InputDialog` e o diálogo de critérios abertos no app |

O aceite automatizado é `CompactLayoutTests`: cada um dos doze destinos é arranjado em
936 × 534 DIP — o que a menor janela deixa depois da barra de navegação, do título e da barra de
estado — e o teste falha se algo visível ficar além da borda direita ou se um texto sem elipse for
truncado. Rolagem horizontal **local** de tabela continua permitida; de página, não. O mesmo teste
roda a 1680 × 980 DIP, para que a adaptação compacta não estrague o layout amplo. Suíte completa:
1230 testes, todos aprovados.

### 0.2 Diferenças em relação à proposta

- **Controle** não ganhou o modo de duas linhas por variável. A distribuição proporcional com
  `MinWidth`/`MaxWidth` já entrega o critério de aceite — nada cortado em 936 DIP — sem duplicar o
  template de cada uma das quatorze linhas da tabela. As duas linhas por variável continuam sendo a
  saída correta se o mínimo cair abaixo de 1024 DIP.
- **Receitas** teve os painéis estreitados por breakpoint em vez de convertidos em drawers com
  botão. O canvas ganha ~104 DIP na janela mínima; drawers continuam sendo a resposta melhor e
  ficam pendentes.
- **A faixa de indicadores do Painel** foi mantida como está. Ela já rola horizontalmente dentro do
  próprio card, e é a origem do arraste para o gráfico; transformá-la em `WrapPanel` custaria
  altura de gráfico numa página de 534 DIP e mexeria no gesto de arraste. Fica registrado como
  divergência consciente, não como item esquecido.
- **A altura do gráfico de Potência** passou de 390 fixos para `MinHeight` de 330. Torná-la
  verdadeiramente elástica exige converter a coluna de resultados de `StackPanel` para `Grid` com
  linha estrela — mudança maior, deixada para quando a página for revista.

### 0.3 Pendente

- Etapa 5 do cronograma: matriz visual em 1280 × 720/100 %, 1920 × 1080/100 % e 1920 × 1080/150 %,
  temas claro e escuro, com medição de resolução física, escala e área útil por evidência. As
  capturas em `docs/evidence/screenshots/` foram regeneradas por esta entrega, mas continuam sendo
  renderização em memória a 1280 × 800 — não substituem a matriz.
- Substituição dos caracteres `⬇`, `✔` e `✓` remanescentes por vetores do sistema de ícones.
- Reconciliação completa de `CURRENT_STATUS.md` na preparação do release.
- Validação em hardware: nada nesta entrega foi exercitado com o Hub conectado.

---

## 1. Evidência e limites da análise

- Revisados os 36 prints `../09-09_app_page_images/2026-09-09 (14).png` até `(49).png`, com inspeção ampliada dos prints 19, 20, 48 e 49, e os layouts WPF correspondentes. Todos os arquivos são capturas de desktop de 1920 × 1080; a aplicação aparece em janela. Não são uma validação em 720p ou da janela maximizada.
- O print 49 comprova corte do cabeçalho, navegação interna, métricas e gráficos da página de potência em janela estreita. O print 48 registra uma exceção ao abrir critérios de captura.
- Base atual: branch `fix/cascata-condicao-de-saida`, com alterações locais preexistentes inclusive em PowerView e PowerTestViewModel. A implementação futura deve preservar e integrar essas alterações.
- `MainWindow.xaml:15–16`: padrão 1280 × 800 e mínimo declarado 960 × 640. `MainWindow.xaml.cs`, em `ApplyResponsiveLayout`, força `IsNavigationCompact = false`. Já existem estilos para navegação de 184/52 unidades, mas o modo compacto está desativado.
- `ControlView.xaml:151–159`: as colunas fixas somam 1038 unidades antes de margens e navegação. `KlaMappingView.xaml:111–115`: 300 + 10 + 360 mínimo + 10 + 320 = 1000 apenas para o corpo da página. Ambas precisam de redistribuição.
- `ReceitasView.xaml`: biblioteca de 212 e propriedades de 340 comprimem o canvas. `KlaDeterminationView.xaml`: painel de 450 e conjunto de gráficos com altura mínima de 620. `PowerView.xaml`: painel de 340, gráfico com mínimo de 430 e linha de gráficos de altura fixa 390.
- `SettingsView.xaml`: menu interno de 230 e divisão do conteúdo entre formulário e ilustração, mesmo quando falta espaço para os campos.
- O problema do X maximizado foi relatado pelo usuário; a causa exata ainda precisa de reprodução. Existe tratamento de `WM_GETMINMAXINFO`, mas ele não define `MinTrackSize` e marca a mensagem como tratada. Investigar sua interação com os limites WPF; não assumir que alterar apenas MinWidth resolve a restrição durante o arraste.
- `CURRENT_STATUS.md` contém informação de branch/versão anterior à base atual e ao executável 0.26.2-dev do crash. Reconciliar esse documento na preparação do release; não usar seus testes históricos como aceite desta proposta.

## 2. Contrato de tamanho proposto

Usar unidades independentes de DPI (DIP) para layout; pixels físicos somente para especificação do monitor. A relação aproximada é `DIP = pixels / escala`. A barra de tarefas e as bordas ainda consomem espaço.

| Condição | Decisão proposta |
|---|---|
| Monitor mínimo de deployment | 1280 × 720, escala Windows 100%, área de trabalho disponível de pelo menos 1024 × 640 DIP |
| Monitor recomendado | 1920 × 1080, escala 100%, 125% ou 150%, respeitando a mesma área útil mínima |
| Menor janela redimensionável | 1024 × 640 DIP de limites externos da janela; validar o cliente real após chrome/bordas |
| Janela padrão em tela ampla | Manter 1280 × 800, ajustando os limites restaurados à área útil do monitor de destino |
| Primeira abertura em notebook | Maximizada quando o tamanho padrão não couber confortavelmente; preservar a preferência posterior quando válida |
| Limite superior | Área útil do monitor atual para maximização e redimensionamento; não fixar MaxWidth em 1920 nem impedir uso de monitores maiores |
| Ambiente com área útil menor que o mínimo | Fora do suporte inicial. Detectar e orientar sobre resolução/escala; não persistir uma janela impossível de recuperar |

O mínimo é um alvo de projeto, ainda não uma capacidade comprovada. Abaixo dele, a concentração de formulários, tabelas e gráficos exige outro perfil de interface.

| Tela / escala | Área lógica total aproximada, antes da barra de tarefas | Resultado previsto |
|---|---|---|
| 1280 × 720 / 100% | 1280 × 720 | Suportar; maximizada, navegação compacta |
| 1280 × 720 / 125% | 1024 × 576 | Fora do mínimo de altura proposto |
| 1366 × 768 / 100% | 1366 × 768 | Suportar |
| 1920 × 1080 / 100% | 1920 × 1080 | Layout amplo quando maximizada |
| 1920 × 1080 / 125% | 1536 × 864 | Layout amplo quando maximizada |
| 1920 × 1080 / 150% | 1280 × 720 | Mesmo perfil compacto de 720p; medir área útil real |
| 1920 × 1080 / 175% | 1097 × 617 | Fora do mínimo de altura proposto |

Monitor atual, work area e conversão DPI devem ser usados na restauração, maximização, limites de arraste e transferência entre monitores. A união de todos os monitores (`VirtualScreen`) não garante que uma janela restaurada esteja acessível em um monitor individual.

## 3. Navegação, cabeçalho e chrome

### Navegação

- Largura cliente menor que 1440 DIP: barra de ícones de 56 DIP, com botão explícito para abrir o drawer. Abertura sobreposta de aproximadamente 200 DIP, sem empurrar ou reconstruir a página.
- A partir de 1440 DIP: ícones e textos em painel fixo de aproximadamente 200 DIP. Permitir recolher manualmente; lembrar a preferência sem obrigar expansão em uma janela pequena.
- Hover opcional como complemento, com atraso curto (proposta: 300 ms), mantendo aberto enquanto ponteiro ou foco estiverem dentro. Não depender de hover para navegar.
- Botões de navegação com tooltip, nome acessível, foco visível e seleção clara. Escape fecha o drawer e devolve o foco; clique fora também fecha. Manter atalhos existentes.
- O editor de nome de sessão, atualmente no rodapé da barra e oculto no modo compacto, precisa continuar acessível por um botão de sessão no cabeçalho/drawer.
- A lista de navegação deve rolar verticalmente quando necessário. A barra de variáveis opcional só aparece se restar largura suficiente para a página ativa; substituir o critério global isolado de 1400 por orçamento de conteúdo.
- Medir breakpoints pela área cliente disponível. Mudanças de largura não podem recriar ViewModels, perder edições, seleção, zoom ou disparar comandos operacionais.

### Cabeçalho e rodapé

- Reservar primeiro a área dos três botões da janela; identidade e ações ocupam a largura restante e entram em modo compacto antes de encostar nesses botões.
- Mostrar versão curta; mover hash completo de build para tooltip/Sobre. O hash longo ocupa espaço visível no print 49.
- Manter conexão e tempo de processo legíveis. No modo compacto, encurtar os rótulos e oferecer ações de sessão em menu explícito. Tooltips complementam ícones de ações secundárias.
- Título da página, subtítulo/estado e barra de comandos devem ocupar linhas independentes quando necessário. Não colocar todas as ações em um StackPanel horizontal sem limite de largura.
- Rodapé: conexão, alarme e gravação prioritários; receita/fase/detalhes secundários em painel de detalhes quando faltar largura. Alertas críticos devem permanecer explícitos.

### Fechar, maximizar e minimizar

- Unificar o template de chrome utilizado pelas janelas. Manter alvo de clique de aproximadamente 46 × 48 DIP, com glifo vetorial centralizado, área de desenho suficiente para o stroke e espaçamento interno consistente.
- Reproduzir o defeito maximizado e corrigir os limites non-client/work area com DPI. Se houver sobreposição da borda de resize, aplicar a compensação calculada ao container; não mascarar uma geometria incorreta com uma margem arbitrária no X.
- Garantir que o glifo inteiro permaneça dentro da área visível, com respiro superior e lateral, preservando a região de clique próxima à borda.
- Verificar arraste, snap, duplo clique no título, maximizar/restaurar, minimizar, taskbar e dois monitores com DPI diferentes.

## 4. Padrão visual comum

Valores iniciais para a implementação e revisão visual:

| Elemento | Padrão proposto |
|---|---|
| Título de página | 20 DIP, semibold |
| Título de seção | 16 DIP, semibold |
| Texto e entradas | 13 DIP; manter tamanho ao reduzir a janela |
| Rótulos e ajuda | 12 DIP; reservar 11 somente para metadados secundários |
| Valores principais | 16–20 DIP, numerais alinhados |
| Botões e entradas | MinHeight 32 DIP, altura automática quando o conteúdo exigir |
| Ícones | Vetores de 16 DIP nos botões e 20 na navegação; espaço de 8 até o texto |
| Espaçamento | 8 entre controles; 12 dentro de cards compactos; 16 entre seções; margem externa 12 compacta / 20 ampla |

- Rótulos acima das entradas nos formulários, alinhados à esquerda e com unidade explícita. Valores numéricos alinhados consistentemente nas tabelas; preservar precisão e unidade.
- Usar Grid Auto/* e altura automática para textos com quebra de linha. Evitar alturas fixas que comportem apenas uma linha e Viewbox para encolher toda a interface.
- Botões de texto com largura pelo conteúdo e padding; ícones explícitos para salvar, abrir, importar, exportar, adicionar, remover e configurar. Substituir emoji/caracteres inconsistentes por vetores do sistema de ícones existente.
- Iniciar, pausar, parar, abortar, aceitar e publicar mantêm rótulos visíveis. Ações de segurança não vão para menus de overflow.
- Elipse somente em caminhos, nomes extensos e detalhes secundários, com tooltip e possibilidade de copiar/ver o conteúdo completo. Não truncar unidades, estados críticos, rótulos de campos ou mensagens de validação.
- Rolagem vertical no corpo de formulários. Tabelas podem ter rolagem horizontal local, com cabeçalhos e colunas essenciais preservados. Canvas de receitas mantém pan/zoom. Evitar rolagem horizontal da página inteira e múltiplos ScrollViewers concorrendo pelo mesmo gesto.
- Rodapés de ação fora da região rolável. Ao mostrar erros de validação, permitir crescimento do card e levar o campo inválido para a área visível.
- Tema light: revisar contraste de textos auxiliares e distinção entre campo editável, somente leitura e desabilitado. Preservar a diferenciação dos estados de conexão/comando existentes.

## 5. Mudanças por página

Os números de print abaixo se referem aos arquivos de 09/09. As mudanças também se aplicam aos estados preenchidos e conectados, que os prints offline não cobrem integralmente.

| Página / evidência | Distribuição proposta em notebook | Distribuição ampla e detalhes |
|---|---|---|
| Painel — 14, 42, 43 | Separar indicadores internos/externos em grupos que possam quebrar linha ou abrir lista de variáveis; toolbar pode usar duas linhas. Em pouca altura, priorizar um gráfico com seletor; dois/quatro continuam acessíveis. | Manter dois/quatro gráficos quando houver área para eixos e legendas. Não alterar seleção de séries nem aquisição ao alternar o layout. Popover de conexão e diálogo de nova sessão devem caber na área útil. |
| Controle — 15–16 | Substituir a soma fixa de 1038 DIP por distribuição proporcional. Abaixo de aproximadamente 1050 DIP de conteúdo, apresentar cada variável em duas linhas: nome/leitura/setpoint atual; entrada/unidade/ativação. Faixa e configuração ficam no detalhe expansível. | Preservar grade de colunas no modo amplo, com alinhamento comum entre cabeçalho e linhas. Padronizar seletor de cascata, bombas e campos numéricos. Manter Parada segura visível e área de predefinição adaptável. |
| Receitas — 17–18, 44 | Biblioteca de blocos e propriedades em drawers acionados por botões; propriedade selecionada editável sem sumir atrás do canvas. JSON/Validação em região recolhível com altura limitada e rolagem própria. | Biblioteca/canvas/propriedades lado a lado quando houver espaço. Toolbar separa edição e execução. Zoom/ajustar ao conteúdo acessíveis. Testar blocos longos, múltiplos pontos de ajuste, abas e receita em execução. |
| Determinar kLa — 19, 45–46 | Reduzir painel de preparação para aproximadamente 320–360 quando couber ao lado do gráfico; em conteúdo mais estreito usar abas Preparação/Revisão e Gráficos. Substituir exigência de 620 DIP de altura por um gráfico principal com seletor OD/Regressão/Diagnóstico. | Três gráficos simultâneos quando a altura comportar. Separar título, telemetria e ações em faixas. Limiares e revisão permanecem acessíveis por rolagem; abortar/parar e estado da fase sempre visíveis. |
| Mapeamento kLa — 20 | Eliminar obrigação de três colunas. Usar abas Dados, Superfície e Diagnóstico; permitir alternar os dois gráficos. Manter estado/etapa e ações principais fora da rolagem. | Três colunas somente com pelo menos ~1200 DIP de conteúdo e gráficos legíveis. Campos em duas colunas, avisos completos e diagnóstico recolhível. Nenhuma alteração no algoritmo, unidades ou publicação. |
| Potência / Montagem — 21–22 | Painel de montagem rolável, campos alinhados em duas colunas e caminhos com elipse. Tabela de impelidores com dimensões/unidades preservadas e rolagem local. | Preservar montagem à esquerda e resultados à direita quando couberem. Catálogo como diálogo limitado à área útil, com ações de rodapé visíveis. |
| Potência / Aquisição — 23, 48 | Separar navegação interna das ações do ensaio. N e Qg organizados em grupos com rótulos acima dos campos. Métricas quebram em duas linhas. Exibir um gráfico por vez quando a área de resultados tiver menos de ~900 DIP. | Dois gráficos quando suas legendas/eixos couberem. Altura flexível em vez de 390 fixos. Tabela de pontos em aba/região de resultados, evitando que reste apenas seu título no fim da página. Preservar estados e comandos do ensaio. |
| Potência / Validação e tara — 24–25, 49 | Cards separados para rastreabilidade, calibração, tara e vínculo kLa. Avisos quebram linha; botão não compete com texto na mesma linha. Perfis e tabela de tara têm espaço próprio. | Manter dados e avisos de modo relativo/absoluto explícitos. Ações de iniciar/cancelar tara acessíveis com toda a lista rolada. |
| Mapeamento de Potência — 26–28 | Configuração em painel recolhível/aba; gráfico principal ocupa o restante. Nome do mapa e ações em linhas distintas. Exibição/Modelo mantêm campos com largura útil. | Painel lateral e superfície em paralelo. Legenda/colorbar com espaço reservado. Inspeção do cursor e limites não cortados no rodapé. |
| Modelos e Ajustes / comparação de impelidores — sem print específico | Revisar o destino e todas as abas existentes, incluindo `PowerImpellerComparisonView`: controles de seleção acima do gráfico, resultados/tabulação em região própria e rolagem local. | Preservar os fluxos existentes; confirmar em runtime a associação entre rótulos e destinos. Não presumir cobertura visual pelo print da aba de mapa. |
| Históricos — 29 | Lista como área principal; verificação do arquivo em aba/drawer de detalhes. Busca e comandos quebram linha. | Lista e prévia lado a lado. Nome/caminho completos por tooltip/cópia, linhas brutas com rolagem horizontal local, largura mínima útil para data/duração. |
| Eventos — 30 | Filtros em duas linhas; mensagem ocupa o espaço flexível, com detalhe completo da linha selecionada. Dados brutos recolhíveis. | Tabela e dados brutos em paralelo. Barra de gravação e pasta com elipse, ações estáveis; não esconder severidade ou origem. |
| Calibrações / pH e O₂ — 31–32 | Leitura e procedimento empilhados quando os formulários não couberem lado a lado; pontos de referência com rótulos e unidades completos. | Manter duas áreas em telas amplas. Ações iniciar/confirmar/aplicar em rodapé visível e respeitando os estados atuais. |
| Calibrações / Vazão — 33 | Alternar Curva e Pontos se necessário; edição de ponto/setpoint em grupo consistente, sem comandos cortados abaixo da tabela. | Gráfico e tabela lado a lado quando couberem. Preservar precisão dos valores certificados, fórmulas completas e confirmação de envio. |
| Calibrações / Biomassa — 34 | Cards Branco e Limiares empilháveis; descrição quebra linha; três limiares em duas/uma coluna quando necessário. | Duas colunas em telas amplas. Manter distinção entre leitura, integração, captura de branco e aplicação dos limiares. |
| Configurações — 35–41 | Ocultar ilustração decorativa quando houver menos de ~1200 DIP de conteúdo; menu de seções vira seletor superior abaixo de ~1000. Formulário usa a largura liberada. | Menu lateral e ilustração opcionais em tela ampla; formulário com largura confortável. Aplicar/Reverter permanecem acessíveis. Conexão: checkbox multilinha; calibração: coeficientes completos; aquisição: pH/O₂ empilháveis; unidades: seletores com unidade; aparência: seleção clara; backup: caminhos copiáveis e cards sem corte; comandos: botões em linhas próprias. Créditos/logos não comprimem ações. |
| Diálogos e overlays — 42–43, 45–48 | Limitar ao cliente/área útil do monitor com margem de cerca de 16 DIP; título e rodapé fixos, corpo rolável. Converter grades de 3–4 campos para 2/1 quando necessário. | Cobrir critérios de potência, parâmetros avançados kLa, catálogo de impelidores, configuração de oxigênio, iniciar receita, entrada de texto e confirmação destrutiva. Revisar título longo, foco, Escape e botão padrão sem mudar inadvertidamente a semântica de aplicação. |

Breakpoint de altura adicional: abaixo de aproximadamente 720 DIP de cliente, priorizar um gráfico e regiões recolhíveis. As medidas por página são valores iniciais a ajustar com o conteúdo real, não números a espalhar em code-behinds independentes.

## 6. Correção funcional prioritária: critérios de captura

Evidência: `../../OpenTEC-Hub/Logs/Crash/crash_20260909_233807_48f82f.log` e print 48. O log aponta `CaptureSettingsDialog.InitializeComponent`, linha XAML 132, e exceção interna: recurso `PowerLabel` não encontrado.

`PowerLabel` e `CompactField` são declarados em `PowerView.UserControl.Resources`; a janela independente `CaptureSettingsDialog` usa esses nomes sem importar suas definições. O Owner atribuído pelo serviço não torna os recursos locais da página recursos da janela.

Plano:

1. Extrair os estilos compartilhados para um ResourceDictionary explicitamente carregado pela página e pelo diálogo, ou para o escopo comum apropriado; verificar também dependências transitivas e os dois temas.
2. Abrir o diálogo com o comando real, editar os campos, sair do campo e concluir; conferir que valores e bloqueios existentes mantêm sua semântica. Não introduzir um novo modelo Aplicar/Cancelar durante uma correção visual.
3. Testar opções de estabilização expandidas, título longo, altura mínima e rolagem até o último campo.
4. Acrescentar teste hosted-WPF que realmente instancia o diálogo. `PowerViewResourceContractTests` atualmente cobre somente três views, não esse diálogo; existência de uma chave em algum arquivo não comprova que esteja no escopo de resolução correto.
5. Executar o fluxo no app publicado com workspace temporário e inspecionar somente os logs daquela execução. Uma compilação bem-sucedida não cobre esse erro de recurso carregado tardiamente.

## 7. Sequência de implementação após revisão do plano

| Etapa | Entrega | Aceite |
|---|---|---|
| 1 | Recursos do diálogo e diagnóstico de chrome/limites | Diálogo abre; arraste respeita mínimo; X inteiramente visível maximizado/restaurado |
| 2 | Shell responsivo, drawer e tokens comuns | Cabeçalho/rodapé/navegação íntegros na menor janela; sessão e atalhos acessíveis |
| 3 | Controle, Receitas, kLa e Potência | Cada fluxo compacto completo, ações operacionais acessíveis e dados preservados durante resize |
| 4 | Históricos, Eventos, Calibrações, Configurações e demais diálogos | Todos os campos e comandos alcançáveis, textos/unidades legíveis |
| 5 | Matriz visual/runtime e documentação do release | Evidências novas por tamanho/escala/tema; nenhum corte bloqueante ou erro de binding/recurso |

Arquivos centrais: `MainWindow.xaml/.cs`, `Services/Platform/WindowChromeMaximizeFix.cs`, `WindowPlacementBounds.cs`, `ViewModels/ShellViewModel.cs`, `Themes/Tokens.Shared.xaml`, estilos comuns, Views e Dialogs listados acima. Usar comportamento de layout reutilizável onde necessário. Preservar comandos, interlocks, estado de aquisição e contratos científicos.

## 8. Verificação necessária para concluir a UI

- Cobertura visual completa: 1280 × 720/100%, 1920 × 1080/100% e 1920 × 1080/150%, nos temas light e dark. Adicionar smoke de 1366 × 768/100% e 1920 × 1080/125%.
- Validar janela externa mínima de 1024 × 640 DIP, tamanho padrão, maximizada/restaurada e arraste contínuo. Testar 1 DIP abaixo/acima dos breakpoints e duas escalas em monitores distintos.
- Medir resolução física, escala, work area, tamanho externo e cliente em cada evidência. Redimensionar screenshots antigas não conta como teste em outra resolução/DPI.
- Em todas as páginas: estado vazio, dados representativos, nomes/caminhos longos, valores com sinal/casas decimais, validação inválida, seções expandidas, conteúdo rolado até o fim, foco por teclado e diálogos abertos.
- Nos fluxos de controle/ensaio, exercitar estados offline, conectado, comando pendente, execução, pausa, erro e revisão com simulação isolada quando disponível. A mudança de layout não pode enviar comandos nem perder uma edição em foco.
- Aceite: nenhum texto operacional cortado, nenhuma sobreposição, nenhum botão necessário fora da área alcançável, nenhum eixo/unidade essencial truncado; tooltip somente complementa informações secundárias. Rolagem deliberada é permitida, perda de conteúdo não.
- Testes automatizados focados em tamanho/restauração, preservação de estado e carregamento real de views/diálogos. Validar limites visuais dos elementos e inspecionar screenshots; testes de medidas sozinhos não detectam todo corte de texto.
- Build e testes pertinentes; lançamento real com `--workspace` temporário para evitar seletor de pasta e dados reais; revisão dos logs novos. UI concluída não substitui validação de hardware nem outros gates de deployment.

## 9. Referências técnicas

- [WPF overview — unidades independentes de DPI](https://learn.microsoft.com/en-us/dotnet/desktop/wpf/overview/).
- [WPF — layout automático](https://learn.microsoft.com/en-us/dotnet/desktop/wpf/advanced/use-automatic-layout-overview).
- [Microsoft NavigationView — padrões de navegação adaptativa](https://learn.microsoft.com/en-us/windows/apps/design/controls/navigationview). Referência de interação; a aplicação continua WPF e não recebe um controle WinUI por esta proposta. Os breakpoints deste plano são específicos da densidade do OpenTEC-Hub.
