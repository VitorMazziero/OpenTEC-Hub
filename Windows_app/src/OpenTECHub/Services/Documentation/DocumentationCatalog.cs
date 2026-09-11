using System;
using System.Collections.Generic;
using System.Linq;

namespace OpenTECHub.Services.Documentation;

/// <summary>How one block of documentation is rendered.</summary>
public enum DocumentationBlockKind
{
    /// <summary>Running prose.</summary>
    Paragraph,

    /// <summary>One item of a list, rendered with a bullet.</summary>
    Bullet,

    /// <summary>
    /// A labelled item: the control's own name, then what it does.
    /// </summary>
    /// <remarks>
    /// This is the shape most of the manual takes. An operator reading with the screen in
    /// front of them looks for the label they can see, not for a sentence that happens to
    /// mention it, so the label is rendered as the entry rather than buried in prose.
    /// </remarks>
    Field,

    /// <summary>A short emphasised line — a caution, a consequence, a rule.</summary>
    Note,
}

/// <summary>
/// One rendered unit of documentation text.
/// </summary>
/// <param name="Kind">How it is drawn.</param>
/// <param name="Text">The body. For <see cref="DocumentationBlockKind.Field"/>, what the control does.</param>
/// <param name="Label">The control's on-screen name. Only meaningful for a field.</param>
public sealed record DocumentationBlock(DocumentationBlockKind Kind, string Text, string Label = "")
{
    /// <summary>
    /// The line as the manual renders it, with <c>**</c> marking the words that carry weight.
    /// </summary>
    /// <remarks>
    /// A field is its own label plus what it does, so the label is emphasised here rather than in
    /// the view — which keeps every block kind a single string the renderer treats identically.
    /// </remarks>
    public string Markup => Kind == DocumentationBlockKind.Field && Label.Length > 0
        ? $"**{Label}** — {Text}"
        : Text;
}

/// <summary>A titled group of blocks inside a topic.</summary>
public sealed record DocumentationSection(string Title, IReadOnlyList<DocumentationBlock> Blocks);

/// <summary>
/// One page of the in-app manual, addressable by <see cref="Id"/>.
/// </summary>
/// <remarks>
/// The id is what a screen deep-links to. A card that used to carry three paragraphs of
/// explanation now carries a question-mark button and this id, so the explanation lives in
/// exactly one place and the card goes back to being controls.
/// </remarks>
public sealed record DocumentationTopic(
    string Id,
    string Title,
    string Summary,
    IReadOnlyList<DocumentationSection> Sections);

/// <summary>
/// The application's own documentation, as data.
/// </summary>
/// <remarks>
/// <para>
/// Data, not XAML, for two reasons. The operator reads this text next to the screen it
/// describes, so it has to be reachable from a deep link and readable as a whole — neither is
/// possible when the text is scattered through page markup. And the pages that still need
/// documenting are added here by writing a record, with no view work at all.
/// </para>
/// <para>
/// Every page is written the same way: first how it is laid out, then what each control on it
/// does, named exactly as the screen names it. Where the screen already explains itself, the
/// manual stays quiet — the point is to answer what the interface cannot say in the space it
/// has, not to restate its labels.
/// </para>
/// <para>
/// Order matters: it is the order of the topic list, and it follows the order an operator meets
/// the application — the dashboard they land on, the page they act from, then the assays.
/// </para>
/// </remarks>
public static class DocumentationCatalog
{
    // ---- Topic ids, referenced by the deep links on the pages -------------------
    public const string DashboardTopicId = "painel";
    public const string ControlTopicId = "controle";
    public const string RecipesTopicId = "receitas";
    public const string RecipeBlocksTopicId = "receitas-blocos";
    public const string RecipeCascadeTopicId = "receitas-cascata";
    public const string KlaDeterminationTopicId = "kla-determinacao";
    public const string KlaMappingTopicId = "kla-mapeamento";
    public const string PowerTareTopicId = "potencia-tara";
    public const string PowerSinglePointTopicId = "potencia-ponto-unico";
    public const string PowerElectricalTopicId = "potencia-correlacao-eletrica";
    public const string PowerKlaMapTopicId = "potencia-mapa-kla";

    private static readonly Lazy<IReadOnlyList<DocumentationTopic>> LazyTopics = new(Build);

    /// <summary>Every documented page, in reading order.</summary>
    public static IReadOnlyList<DocumentationTopic> Topics => LazyTopics.Value;

    /// <summary>The topic with this id, or null when nothing documents it yet.</summary>
    public static DocumentationTopic? Find(string? topicId) =>
        string.IsNullOrWhiteSpace(topicId)
            ? null
            : Topics.FirstOrDefault(t => string.Equals(t.Id, topicId, StringComparison.OrdinalIgnoreCase));

    private static DocumentationBlock P(string text) => new(DocumentationBlockKind.Paragraph, text);

    private static DocumentationBlock B(string text) => new(DocumentationBlockKind.Bullet, text);

    private static DocumentationBlock F(string label, string text) => new(DocumentationBlockKind.Field, text, label);

    private static DocumentationBlock N(string text) => new(DocumentationBlockKind.Note, text);

    private static IReadOnlyList<DocumentationTopic> Build() =>
    [
        // ════════════════════════════════════════════════════════════ Painel
        new DocumentationTopic(
            DashboardTopicId,
            "Painel",
            "A tela de acompanhamento: cartões de leitura, gráficos, ferramentas de visualização e o registro da sessão.",
            [
                new DocumentationSection("Como a página é organizada", [
                    P("O Painel é a tela de acompanhamento. Ele não comanda nada: todo comando sai da página Controle, de uma receita ou de um ensaio. São quatro faixas, de cima para baixo."),
                    F("Faixa de cartões", "As leituras atuais, em dois grupos: Parâmetros internos (agitação, temperatura, pH, oxigênio, alívio de pressão, nutrientes, antiespumante) e Dispositivos externos (vazão de ar, distância, absorbância, bomba externa)."),
                    F("Área de gráficos", "Um, dois ou quatro gráficos lado a lado, cada um com o seu eixo e a sua variável."),
                    F("Barra de ferramentas", "Logo abaixo dos gráficos: quantidade de gráficos, janela de tempo, pausa, cursor, limpeza, Nova Etapa, marcação de evento e exportação."),
                    F("Barra de estado", "No rodapé: receita em curso, registro da sessão, estado do sistema e hora da última telemetria."),
                ]),
                new DocumentationSection("Cartões de leitura", [
                    P("Cada cartão mostra o nome da variável, o valor atual e a unidade, com um ponto colorido de estado à esquerda."),
                    B("Arraste um cartão para dentro de um gráfico para plotar aquela variável. É assim que se escolhe o que cada gráfico mostra."),
                    B("Clique no cartão para abrir o detalhe da variável, com tendência e saúde do sinal."),
                    N("Um traço (—) no lugar do número quer dizer leitura não recebida, que não é o mesmo que zero. Cartão em traço com o sistema conectado é nó que não respondeu — não medida nula."),
                ]),
                new DocumentationSection("Barra de ferramentas dos gráficos", [
                    F("2 Gráficos", "Alterna entre 1, 2 e 4 gráficos simultâneos. Com quatro, cada um fica com metade da altura."),
                    F("Janela", "Quanto tempo permanece visível (por exemplo, 30 min). O eixo acompanha a janela; o que sai dela continua no arquivo da sessão."),
                    F("Pausar", "Congela o desenho sem interromper a aquisição: a sessão continua gravando enquanto você examina a tela."),
                    F("Cursor", "Liga a leitura de valores sobre a curva, no ponto onde está o mouse."),
                    F("Vassoura", "Limpa apenas o que está desenhado. O arquivo da sessão não é apagado."),
                    F("Olho", "Traz de volta aos gráficos todos os dados da sessão, desfazendo a limpeza visual."),
                    F("Nova Etapa", "Abre um novo arquivo de sessão e zera a contagem relativa. Use ao trocar de condição de processo, para que cada etapa tenha o seu próprio arquivo."),
                    F("Bandeira", "Marca um evento: uma anotação de texto no registro, com o instante em que foi criada."),
                    F("Exportar", "Grava em CSV o que está visível nos gráficos, respeitando a janela em uso."),
                ]),
                new DocumentationSection("Sessão e tempo de processo", [
                    P("Tudo o que chega do equipamento é gravado continuamente em Sessoes/, desde a abertura do aplicativo — não é preciso iniciar nada."),
                    F("Nome da Sessão", "No canto inferior esquerdo da navegação. Editar e clicar em Aplicar renomeia o registro em uso a partir dali; o que já foi gravado não é reescrito."),
                    F("Tempo de processo · Zerar", "No topo da janela. Reinicia a contagem do relógio sem trocar de arquivo."),
                    F("Nova Etapa / Corrida", "No topo da janela, mesma ação do botão da barra de ferramentas: novo arquivo de sessão e contagem zerada."),
                    F("Tempo real · amostras", "À direita da barra de ferramentas: quantas amostras a sessão já tem e há quanto tempo ela corre."),
                ]),
                new DocumentationSection("Barra de estado", [
                    F("Receita · Fase · Decorrido · Próxima ação", "Descrevem a receita em execução, quando há uma."),
                    F("Registro", "Se a sessão está gravando e quantas linhas já foram escritas."),
                    F("Estado do sistema · Última atualização", "À direita. Última atualização parada é o primeiro sinal de enlace perdido, mesmo antes de os cartões voltarem a traço."),
                ]),
            ]),

        // ═══════════════════════════════════════════════════════════ Controle
        new DocumentationTopic(
            ControlTopicId,
            "Controle",
            "A página de atuação: as colunas da tabela, o detalhe de cada variável e de cada dispositivo, e a parada segura.",
            [
                new DocumentationSection("Como a página é organizada", [
                    P("Uma tabela por família de equipamento, e uma barra de ações no rodapé."),
                    F("Tabela superior", "As variáveis do módulo do biorreator: Agitação, Temperatura, pH, Alívio de Pressão, Oxigênio, Nutrientes e Antiespumante."),
                    F("Dispositivos Externos", "Os nós que conversam com o Hub: Vazão de Ar, Distância, Bomba Externa, Absorbância e Frasco Agitador."),
                    F("Barra do rodapé", "Predefinições de setpoints à esquerda e a Parada segura à direita."),
                    B("A seta no início de cada linha abre o painel de detalhe daquela variável — é onde ficam os parâmetros finos, a calibração e a saúde do sinal."),
                ]),
                new DocumentationSection("As colunas da tabela", [
                    F("Variável / Dispositivo", "Nome e ícone. O ponto colorido ao lado indica o estado da linha."),
                    F("Valor Lido", "O que o sensor está medindo agora. Agitação mostra duas linhas: rotação e torque em % do nominal."),
                    F("Setpoint", "O alvo que o equipamento tem hoje, confirmado por ele."),
                    F("Novo Setpoint", "O valor que você quer passar a pedir. É o único campo editável da linha."),
                    F("Unidade · Faixa", "A unidade do valor e os limites aceitos. Um valor fora da faixa é recusado antes de virar comando."),
                    F("Ativo", "Liga e desliga a malha daquela variável. Um setpoint enviado com a malha desligada fica registrado como alvo, mas nada atua até ligá-la."),
                    N("Agitação não aceita 0 rpm como comando de operação: 0 desabilita o acionamento. A faixa útil começa em 15 rpm."),
                ]),
                new DocumentationSection("Enviar um setpoint", [
                    P("Digite o valor em Novo Setpoint e pressione Enter, ou apenas saia do campo. Não há botão de confirmar por linha: a perda de foco já vale como confirmação, para que o operador de bancada não precise mirar um botão."),
                    B("O comando é validado contra os limites de engenharia e só então enviado ao equipamento."),
                    B("Oxigênio não recebe um número direto: escolha o modo na lista (Cascata, por exemplo) e ajuste a malha pela engrenagem ao lado, que abre a sintonia."),
                    B("Bomba Externa também é uma lista de modos, não um número: o perfil é montado no painel de detalhe e enviado de lá."),
                ]),
                new DocumentationSection("Detalhe — variáveis internas", [
                    F("Agitação", "Servo Delta ASDA-B2. Traz o setpoint de rotação, a via do comando (UART/CN1 pelo módulo e visor, ou Modbus direto), a rotação e o torque medidos, a carga média, a potência mecânica estimada e a energia acumulada. Amostragem define o período de leitura do servo em ms; Zerar energia acumulada reinicia só o acumulador de dados, sem afetar o processo."),
                    F("Temperatura", "Controle térmico integrado ao módulo. O painel traz a Saúde do Sensor: última leitura aceita, se há sinal presente, quantas das 30 amostras recentes foram aceitas, o ruído sem tendência (σ) e o veredito de estabilidade."),
                    F("pH", "Histerese (±) é a banda morta em torno do setpoint; Operação da bomba (s) e Mistura (s) definem o pulso de dosagem e a espera antes de medir de novo; Intensidade (%) é a potência da bomba. Mostra a leitura bruta ao lado e dá acesso direto a Calibrar pH."),
                    F("Oxigênio", "Controle em malha fechada por cascata de agitação e aeração. O detalhe traz Calibrar Oxigênio e a saúde da sonda; os ganhos e o caminho da cascata ficam na engrenagem da própria linha."),
                    F("Nutrientes", "Operação (s) e Mistura (s) são o pulso e a espera de cada dosagem; Operação ciclo (min) e Mistura ciclo (min) são o ciclo longo que repete essa dosagem ao longo do cultivo."),
                    F("Antiespumante", "Operação (s) e Mistura (s), com o mesmo significado da dosagem de nutrientes."),
                    F("Alívio de Pressão", "Linha sem painel de detalhe: setpoint e leitura de pressão na própria tabela."),
                ]),
                new DocumentationSection("Detalhe — dispositivos externos", [
                    F("Vazão de Ar", "Além do setpoint, comanda as três saídas do fluxômetro: Válvula auxiliar, Válvula de N₂ e Válvula de fechamento, cada uma com a sua telemetria. maxFlow é o fundo de escala do medidor, e Calibrar Vazão abre a calibração da curva."),
                    F("Distância", "Atraso inicial (s), Pulso (s) e Intervalo (s) governam a medição por ultrassom. Este sensor é usado como sensor de espuma, e o atuador de antiespumante nesse caso é a bomba de nutrientes."),
                    F("Bomba Externa", "Modo escolhe entre os cinco perfis; Tempo inicial e Tempo final delimitam a janela de dosagem e λ parametriza o perfil. O gráfico mostra vazão e volume acumulado antes do envio, com pico e volume total abaixo. Gás proporcional liga a aeração ao volume dosado por Q_g = (V₀ + Vol/1000)·vvm — quando ativo, comanda a aeração pelo mesmo árbitro do controle manual e é recusado se a cascata detiver a aeração. Reverter descarta as edições; Enviar perfil manda o conjunto ao nó."),
                    F("Absorbância", "Mostra Abs, Raw, IT e PWM lado a lado. Capturar branco registra a referência óptica; Iniciar e Parar controlam a leitura contínua. Limiar baixo, alto e ótimo definem a janela de trabalho do sensor e vão ao nó por Enviar limiares. Calibrar Biomassa abre a curva absorbância → concentração."),
                    F("Frasco Agitador", "Intensidade (%) por deslizador, Sentido horário ou anti-horário e Automação por espuma. Mostra quem comandou por último; Reativar potenciômetro devolve o comando ao botão físico da bancada."),
                    N("O deslizador do agitador nunca envia sinal negativo: o sentido é uma chave separada da intensidade."),
                ]),
                new DocumentationSection("Quem manda no atuador", [
                    P("Um único árbitro controla o acesso ao equipamento, para que dois donos nunca comandem o mesmo atuador. A posse pode estar com o operador (Manual), com uma automação interna como a cascata de oxigênio (Automático) ou com uma receita em execução (Receita)."),
                    B("Enquanto uma receita ou uma automação detém a posse, os campos manuais correspondentes ficam travados e a página mostra quem está com ela. Isso não é falha: é a recusa deliberada de um comando que contrariaria o que está rodando."),
                    B("Um ensaio de kLa ou de potência também reivindica os atuadores de que precisa enquanto corre, pelo mesmo mecanismo."),
                ]),
                new DocumentationSection("Rodapé: predefinições e parada segura", [
                    F("Predefinição", "Um conjunto de setpoints salvo com nome, para uma condição de processo recorrente."),
                    F("Carregar", "Traz os valores da predefinição para a coluna Novo Setpoint. Eles ainda precisam ser enviados, linha a linha — carregar não comanda nada."),
                    F("Salvar como…", "Grava o conjunto atual de setpoints como uma nova predefinição."),
                    N("Parada segura revoga a posse de qualquer receita, cascata ou ensaio em andamento e corta os atuadores: rotação a zero, aquecimento desligado, vazão a zero e bombas paradas. A ação fica registrada no log de eventos."),
                ]),
            ]),

        // ═══════════════════════════════════════════════════════════ Receitas
        new DocumentationTopic(
            RecipesTopicId,
            "Receitas",
            "O que dá para automatizar, como montar e salvar uma receita, e o que acontece com o controle manual enquanto ela roda.",
            [
                new DocumentationSection("O que dá para fazer com isto", [
                    P("Uma receita é um procedimento que o aplicativo executa sozinho: você desenha o que deve acontecer, em que ordem e sob que condições, e o programa comanda o reator no seu lugar. O limite é o que se consegue descrever com blocos — alguns exemplos do que a bancada já permite montar:"),
                    B("Uma partida completa: aquecer até 30 °C, esperar estabilizar, ligar a agitação, abrir a aeração e só então iniciar o controle de oxigênio."),
                    B("Uma batelada alimentada: manter o oxigênio em cascata enquanto a bomba externa dosa um perfil exponencial por seis horas, com o gás proporcional acompanhando o volume dosado."),
                    B("Um degrau de condição no meio do cultivo: registrar um evento, trocar rotação e vazão de uma vez, abrir uma janela de aquisição de dados e devolver a condição anterior ao fim dela."),
                    B("Um ensaio noturno com guarda humana: rodar a sequência até um ponto, parar numa Intervenção Manual e esperar alguém liberar na manhã seguinte."),
                    B("Uma resposta a evento: esperar a absorbância cruzar um limiar e, a partir daí, iniciar a indução — dosar, mudar temperatura e anotar o instante no registro."),
                    N("O que a receita **não** faz é inventar segurança: ela usa os mesmos limites de faixa, o mesmo árbitro e a mesma parada segura da operação manual. Um comando que a página Controle recusaria, a receita também recusa."),
                ]),
                new DocumentationSection("O conceito: blocos, conexões e fluxo", [
                    P("Cada bloco é uma etapa. A ligação entre a **Saída** de um bloco e a **Entrada** do próximo diz o que vem depois do quê — é o fluxo que a receita percorre, um bloco de cada vez, a partir do **Início** até um **Fim**."),
                    B("Todo caminho começa num único bloco Início e precisa chegar a pelo menos um Fim. A validação recusa a receita se isso não for verdade."),
                    B("Um bloco só é alcançado quando o anterior termina. Um Temporizador termina quando o tempo passa; um Monitorar Variável, quando a condição se confirma; um bloco de comando, assim que o equipamento confirma."),
                    B("Para abrir caminhos paralelos, ligue a mesma saída a mais de um bloco. Para juntá-los de volta, use **Sincronizar (E)**, que espera todos, ou **Qualquer (OU)**, que segue com o primeiro que chegar."),
                    B("Ciclos só são permitidos dentro do laço de uma cascata. Um ciclo em qualquer outro lugar é erro de validação, não uma repetição."),
                ]),
                new DocumentationSection("A página, parte por parte", [
                    F("Barra superior", "Salvar, Carregar, Excluir bloco, desfazer/refazer e o painel de JSON & Validação. À direita, os comandos de execução."),
                    F("Minhas Receitas", "A biblioteca de receitas salvas no workspace: abrir, duplicar e excluir."),
                    F("Abas", "Uma receita por aba. Dá para manter várias abertas e alternar sem perder o desenho de nenhuma."),
                    F("Biblioteca de blocos", "À esquerda, os blocos disponíveis agrupados por família. Clique para adicionar ao canvas."),
                    F("Canvas", "Onde a receita é montada. Arraste para mover blocos, use a roda para aproximar e afastar, arraste o fundo para deslocar a vista."),
                    F("Propriedades do Bloco", "À direita: os campos do bloco selecionado. O que aparece aqui muda conforme o bloco e conforme as escolhas feitas nele."),
                    F("JSON & Validação", "O painel inferior: o JSON da receita como será salvo e a lista de erros e avisos. Clique num apontamento para centralizar o bloco correspondente."),
                ]),
                new DocumentationSection("Montar, ligar e editar", [
                    B("Adicionar: clique no bloco na biblioteca. Ele entra no canvas já com os valores padrão."),
                    B("Ligar: clique no ponto de saída de um bloco e depois no ponto de entrada do outro. Os pontos compatíveis acendem enquanto a ligação está em curso."),
                    B("Selecionar e excluir: clique no bloco (ou na conexão) e use Excluir bloco, ou a tecla Delete."),
                    B("Desfazer e refazer: Ctrl+Z e Ctrl+Y, ou os botões da barra. Valem para posição, parâmetros, ligações e exclusões."),
                    B("Blocos com listas — Múltiplos Pontos de Ajuste e Múltiplos Controles — têm **+ Adicionar** no painel de propriedades: cada linha é um par variável/valor enviado no mesmo comando."),
                ]),
                new DocumentationSection("Salvar, carregar e o JSON", [
                    P("Uma receita é um arquivo JSON versionado em `Receitas/`, no workspace. É ele que a biblioteca lista e o que o aplicativo lê de volta."),
                    F("Salvar", "Grava a receita da aba atual na biblioteca. O nome da aba é o nome do arquivo."),
                    F("Carregar", "Abre a biblioteca Minhas Receitas para escolher uma receita salva."),
                    F("JSON da Receita", "Mostra exatamente o que será gravado: blocos, parâmetros, conexões e posições. Serve para conferir, comparar duas receitas ou anexar a um registro de experimento."),
                    F("Validação", "Erros impedem a execução; avisos não. “Bloco inacessível a partir do Início” é aviso — o bloco existe mas nunca será alcançado; “Não há caminho do Início até um Fim” é erro."),
                    N("O formato é versionado e tolerante na leitura: uma receita gravada por uma versão anterior continua abrindo."),
                ]),
                new DocumentationSection("Executar: o que muda no equipamento", [
                    P("**Iniciar uma receita desativa o controle manual.** Ao começar, a receita reivindica todos os atuadores no árbitro de comandos, e enquanto ela estiver rodando os campos da página Controle que dependem deles ficam travados. Não é um bloqueio de tela: um comando manual enviado nesse intervalo é recusado pelo árbitro, e não chega ao equipamento."),
                    F("▶ Iniciar", "Pergunta antes como começar: **zerando as malhas** (envia uma parada segura e parte do repouso) ou **preservando** o estado atual do processo. Zerar é o começo limpo de uma partida; preservar é o certo quando a receita entra no meio de um cultivo em andamento."),
                    F("⏸ Pausar / Retomar", "Congela o avanço entre blocos. O que já foi comandado permanece: pausar não desliga aquecimento, agitação nem dosagem."),
                    F("⏹ Parar", "Encerra a receita e devolve a posse dos atuadores ao controle manual."),
                    F("⏭ Pular bloco", "Só faz sentido quando a receita está esperando — num Temporizador, num Monitorar Variável ou numa Intervenção Manual. Encerra a espera e segue para o próximo bloco."),
                    F("Faixa de execução", "Enquanto roda, a barra mostra o bloco atual e o motivo da espera; no canvas, o bloco em execução fica realçado e os concluídos ficam marcados."),
                    N("Perda de enlace ou de realimentação faz **aborto seguro**: o árbitro devolve a posse ao modo manual e a receita para, em vez de seguir comandando às cegas. O motivo fica no registro de eventos."),
                    N("A Parada segura global, no rodapé da página Controle, revoga a posse da receita a qualquer momento — é o caminho para interromper tudo sem procurar o botão certo."),
                ]),
                new DocumentationSection("As famílias de blocos", [
                    P("A biblioteca é agrupada por família, e a cor do cabeçalho de cada bloco no canvas repete o grupo — dá para ler a receita de longe pela cor."),
                    F("Fluxo", "Início e Fim: onde a receita começa e onde termina."),
                    F("Gatilhos", "Temporizador, Monitorar Variável e Intervenção Manual: os blocos que **esperam** alguma coisa."),
                    F("Lógica", "Sincronizar (E), Qualquer (OU) e Controle de O₂: junções de caminhos e o laço de controle."),
                    F("Ações", "Definir Ponto de Ajuste, Múltiplos Pontos de Ajuste, Controle de Malha e Múltiplos Controles: os blocos que **comandam** o módulo."),
                    F("Bombas", "Bomba pH, Bomba Antiespuma e Bomba Nutrientes: as bombas de dosagem internas ao módulo."),
                    F("Dispositivos Externos", "Bomba Externa, Absorbância e Agitador de Frasco: os nós que conversam com o Hub por Wi-Fi e que, por isso, podem estar ausentes."),
                    F("Utilitários", "Aquisição de Dados, Registrar Evento e Zerar Variáveis: o que anota e o que limpa, sem tocar no processo."),
                    B("Cada bloco tem a sua definição e o seu uso no assunto **Receitas · Blocos**; a cascata, por ser o único que contém um controlador, tem assunto próprio."),
                ]),
            ]),

        // ══════════════════════════════════════════════════ Receitas · blocos
        new DocumentationTopic(
            RecipeBlocksTopicId,
            "Receitas · Blocos",
            "Os dezenove blocos, um a um: o que cada um faz, o que se ajusta nele e o que muda conforme o contexto.",
            [
                new DocumentationSection("Como ler esta página", [
                    P("Cada entrada abaixo traz o nome do bloco como ele aparece na biblioteca e o que ele faz. Onde um campo só existe em certas condições, isso está dito — o painel de propriedades esconde o que não se aplica, em vez de mostrar campo inerte."),
                    N("Dois blocos mudam de significado quando ligados à **Condição de Saída** de uma cascata. Isso está descrito no assunto Receitas · Cascata e repetido aqui, em cada um deles."),
                ]),
                new DocumentationSection("Fluxo", [
                    F("Início", "O ponto de entrada. Exatamente um por receita, e só tem porta de saída."),
                    F("Fim", "Encerramento bem-sucedido. Pelo menos um por receita; pode haver vários, um por caminho."),
                ]),
                new DocumentationSection("Gatilhos — os blocos que esperam", [
                    F("Temporizador", "Espera uma duração fixa e segue. Campos: **Duração** e **Unidade** (segundos, minutos ou horas)."),
                    F("Monitorar Variável", "Segura a receita até que uma variável medida satisfaça uma comparação. Campos: **Variável**, **Condição** (≥, ≤, =, …), **Valor alvo**, **Intervalo de polling**, **Confirmações consecutivas** e **Tempo limite** (0 = sem limite). As confirmações consecutivas são o que evita que um único pico de ruído libere a etapa."),
                    F("Monitorar Variável · no laço da cascata", "Ligado à Condição de Saída de uma cascata, ele deixa de ser etapa e passa a ser lido a cada iteração do PID: o campo **Intervalo de polling** desaparece, porque quem define a cadência é a cascata."),
                    F("Intervenção Manual", "Para a receita em standby até alguém liberar. O botão no próprio bloco alterna entre **BLOQUEAR** e **PASSAR**, e pode ser trocado ao vivo durante a execução — é o bloco do “só continue quando eu autorizar”."),
                    F("Intervenção Manual · no laço da cascata", "Ligada à Condição de Saída, vira a chave **Manter Rodando / Sair do Loop**: o operador decide, durante a corrida, quando o controle de O₂ termina."),
                ]),
                new DocumentationSection("Lógica — juntar caminhos", [
                    F("Sincronizar (E)", "Junção que espera **todos** os caminhos que chegam nele. Use quando duas preparações paralelas precisam estar ambas prontas."),
                    F("Qualquer (OU)", "Junção que segue com o **primeiro** caminho a chegar. Use para “o que acontecer antes”: a temperatura estabilizar ou o tempo limite vencer."),
                    F("Controle de O₂", "O laço de controle de oxigênio dissolvido. Tem assunto próprio — veja Receitas · Cascata."),
                ]),
                new DocumentationSection("Ações — comandar o módulo", [
                    F("Definir Ponto de Ajuste", "Escreve um setpoint em unidades de engenharia. Campos: **Variável** e **Valor**. Com a variável pH aparece também **Histerese**, que não faz sentido para as demais."),
                    F("Múltiplos Pontos de Ajuste", "O mesmo, para vários setpoints enviados como um único comando. Cada linha da lista tem Variável, Valor e, para pH, Histerese. Enviar junto importa: as mudanças chegam ao equipamento no mesmo quadro, não em sequência."),
                    F("Controle de Malha", "Liga ou desliga uma malha. Campos: **Malha** e **Operação** (habilitar ou desabilitar)."),
                    F("Múltiplos Controles", "Liga e desliga várias malhas num comando só, uma linha por malha."),
                ]),
                new DocumentationSection("Bombas — dosagem interna ao módulo", [
                    F("Bomba pH", "Campos: **Bomba alvo** (ácido ou base), **Operação**, **Intensidade (%)**, **Tempo ligada (s)**, **Tempo desligada (s)** e **Ação manual** (ligar ou desligar). Tempo ligada e tempo desligada são o pulso de dosagem e a espera de mistura antes da próxima leitura valer."),
                    F("Bomba Antiespuma", "Os mesmos campos, sem a escolha de bomba alvo."),
                    F("Bomba Nutrientes", "Campos: **Operação**, **Tempo dosagem ligada**, **Tempo dosagem desligada**, **Volume a dosar (mL)** e **Ação manual**. É a bomba que o sensor de distância usa como atuador de antiespuma quando a automação por espuma está ativa."),
                    N("Em todas as três, **Operação** decide o que os demais campos significam: em acionamento manual vale a Ação manual; em dosagem por ciclo valem os tempos."),
                ]),
                new DocumentationSection("Dispositivos externos — os nós atrás do Hub", [
                    F("Bomba Externa", "Campos: **Ação** — ativar roteamento, enviar perfil ou parar. Só com *enviar perfil* aparecem **Perfil** (um dos cinco modos do firmware), **Início** e **Fim** (min), **λ** e **φ**, cujo significado muda conforme o perfil escolhido. **Coeficientes p0..pN**, **Tempos dos segmentos** e **Vazões dos segmentos** descrevem os perfis por partes."),
                    F("Absorbância", "Campos: **Ação** — ativar roteamento, capturar branco, iniciar, parar ou gravar limiares. Só com *limiares* aparecem **Limiar baixo**, **alto** e **ótimo**, em contagens."),
                    F("Agitador de Frasco", "Campos: **Ação** (acionar ou parar) e, ao acionar, **Intensidade (%)** e **Sentido**. **Modo automático** entrega o agitador à automação por espuma."),
                    N("Estes três podem **esperar** pelo dispositivo: se o nó não confirmar, a receita segura o bloco e avisa, em vez de dar o comando por entregue. É a diferença deles para as bombas internas, que não podem estar ausentes sozinhas."),
                ]),
                new DocumentationSection("Utilitários — anotar e limpar", [
                    F("Aquisição de Dados", "Marca uma janela rotulada no registro da sessão. Campos: **Modo** e, no modo de tempo fixo, **Duração** e **Unidade**. Serve para separar no arquivo o trecho que corresponde a uma fase do experimento."),
                    F("Registrar Evento", "Escreve uma **Mensagem** em Eventos, com o instante. É o que transforma “às 14h20 eu induzi” numa linha rastreável."),
                    F("Zerar Variáveis", "Zera as variáveis de processo do módulo. Pede confirmação, porque destrói estado acumulado."),
                ]),
            ]),

        // ════════════════════════════════════════════════ Receitas · cascata
        new DocumentationTopic(
            RecipeCascadeTopicId,
            "Receitas · Cascata de O₂",
            "O único bloco que contém um controlador: como o PID funciona, os quatro modos de atuação e todos os ajustes.",
            [
                new DocumentationSection("O que este bloco é", [
                    P("O **Controle de O₂** é o núcleo científico do aplicativo: um laço fechado que mantém o oxigênio dissolvido num setpoint atuando sobre agitação e aeração. Ele é a mesma malha que a página Controle expõe na linha Oxigênio — a diferença é que aqui ela entra como etapa de um procedimento, com os seus ganhos e faixas gravados na receita."),
                    P("Enquanto o bloco roda, a receita fica dentro dele: o controlador recalcula a cada intervalo definido e comanda os atuadores até que a condição de saída se cumpra. Só então a receita segue pela porta **Saída**."),
                    B("Ver também o assunto **Controle**, seção de detalhe do Oxigênio, para a mesma malha vista do lado da operação manual."),
                ]),
                new DocumentationSection("As quatro portas", [
                    F("Entrada", "Por onde a receita chega ao bloco."),
                    F("Condição de Saída", "Liga ao bloco que **decide quando o laço termina**: um Monitorar Variável, um Temporizador ou uma Intervenção Manual."),
                    F("Retorno da Condição", "Fecha o laço: é por onde a resposta daquele bloco volta ao controlador, a cada iteração."),
                    F("Saída", "Por onde a receita continua depois que o laço encerra."),
                    N("Sem nada ligado à Condição de Saída o bloco exibe **SAI AO ESTABILIZAR**: o laço encerra sozinho quando o oxigênio fica dentro de ±2 % do setpoint por três leituras. É um padrão conveniente, não uma regra do processo — quando o critério de parada importa, declare-o."),
                    N("O bloco ligado à Condição de Saída **não é executado como etapa**: a cascata o lê a cada iteração. Enquanto for falso, o controle continua; quando ficar verdadeiro, o laço encerra. A mesma peça no fluxo normal significaria o oposto — esperar até ser verdade para então seguir."),
                ]),
                new DocumentationSection("Como o controlador funciona", [
                    P("São dois laços encadeados. O **laço externo** observa a trajetória do oxigênio e prevê onde ele estará daqui a um horizonte; o **laço interno** é um PID que corrige a diferença entre essa previsão e o setpoint. Prever, em vez de reagir ao erro atual, é o que permite acompanhar a demanda crescente de um cultivo sem oscilar."),
                    F("SP de O₂ (%)", "O alvo de oxigênio dissolvido, em saturação relativa."),
                    F("Intervalo de cálculo do PID (s)", "De quanto em quanto tempo o controlador recalcula. Mais curto responde mais rápido e amplifica ruído; mais longo suaviza e atrasa."),
                    F("K_DOT (laço externo)", "O ganho sobre a taxa de variação do oxigênio — quanto a tendência observada pesa na correção."),
                    F("Kp · Ki · Kd", "Os ganhos do PID interno: proporcional ao erro, ao acúmulo do erro e à sua taxa."),
                    F("Horizonte t_pred (s)", "Quão longe à frente a previsão olha. É o parâmetro que dá ao laço a antecipação; um horizonte muito longo antecipa demais e responde a algo que ainda não aconteceu."),
                    F("Janela do preditor (amostras)", "Quantas leituras entram na estimativa da trajetória."),
                    F("τ_D do filtro (s)", "A constante do filtro da derivada. Derivada sem filtro é ruído amplificado."),
                    F("Método (estimativa de taxa)", "Como a taxa de variação é estimada: mínimos quadrados sobre a janela, ou média móvel. Mínimos quadrados é o padrão e é o método do manuscrito."),
                    F("Janela da média (amostras)", "O tamanho da janela usada por esse método."),
                ]),
                new DocumentationSection("Anti-windup", [
                    P("Enquanto o atuador está saturado — a agitação já no máximo, por exemplo — o termo integral continuaria somando um erro que ele não consegue corrigir, e o controle ficaria lento para voltar quando a saturação passasse. Os três campos abaixo são o que impede isso."),
                    F("I_min · I_max", "Os limites do termo integral. Ele não cresce além deles."),
                    F("Janela do integrador (s)", "Por quanto tempo o acúmulo de erro é considerado. O que é mais antigo que a janela deixa de pesar."),
                ]),
                new DocumentationSection("Os quatro modos de atuação", [
                    F("Agitação", "Só a rotação atua. Use quando a aeração está fixa por outro motivo — um ensaio a vazão constante, por exemplo."),
                    F("Aeração", "Só a vazão de gás atua. Use quando a rotação não pode variar, como num estudo de cisalhamento."),
                    F("Cascata (percentuais)", "Os dois atuam, cada um numa janela da saída do controlador. É o modo padrão: a agitação cobre a parte baixa da demanda, a aeração entra depois, e a faixa em que as duas se sobrepõem é a transição suave entre elas."),
                    F("Mapa (trajetória kLa)", "A saída do controlador é convertida em um par (rotação, vazão) lido de uma trajetória publicada por um mapa de kLa. Aqui o controlador não escolhe percentuais: ele anda sobre um caminho medido, com **ID do Mapa kLa** apontando qual."),
                ]),
                new DocumentationSection("Faixas, janelas e ganhos relativos", [
                    P("Três grupos de campos descrevem, nessa ordem, onde o controlador pode atuar, como ele divide a sua saída entre os atuadores e quanto cada atuador rende."),
                    F("N_min · N_max (rpm)", "Os limites físicos da agitação neste ensaio. O controlador nunca comanda fora deles."),
                    F("Q_min · Q_max (vvm)", "Os limites físicos da aeração, na mesma lógica."),
                    F("Faixa da Agitação (% do output)", "**Início** e **Fim**: o trecho da saída do controlador em que a agitação varia. Com 0–40 %, a agitação percorre N_min→N_max nos primeiros 40 % da demanda."),
                    F("Faixa da Aeração (% do output)", "O mesmo para a vazão. Com 30–70 %, a aeração começa a subir antes de a agitação terminar — e essa sobreposição de 30 a 40 % é onde os dois atuam juntos, mostrada no diagrama do painel."),
                    F("Agitação e Aeração (ganho relativo)", "Quanto de oxigenação cada atuador entrega por unidade da sua faixa. São eles que dizem ao controlador que, nesta montagem, aeração rende mais que agitação — ou o contrário."),
                    N("As janelas não precisam somar 100 % nem ser disjuntas. Sobreposição é transição suave; um vão entre elas é uma faixa de demanda em que nenhum atuador se move, e isso o controlador não corrige por conta própria."),
                ]),
                new DocumentationSection("Predefinições de oxigênio", [
                    F("Predefinição Salva · Carregar", "Traz para o bloco um conjunto inteiro de ganhos, faixas e limites já validado. É como se reaproveita a sintonia de um cultivo no seguinte."),
                    F("Salvar como…", "Grava a sintonia atual do bloco sob um nome, para os próximos ensaios."),
                    N("A predefinição guarda a sintonia, não o setpoint do experimento: confira o SP de O₂ depois de carregar uma."),
                ]),
            ]),

        // ═══════════════════════════════════════════════════ Determinar kLa
        new DocumentationTopic(
            KlaDeterminationTopicId,
            "Determinar kLa",
            "O ensaio de gassing-out: como a corrida acontece, o que cada card controla e como a curva vira um kLa aceito.",
            [
                new DocumentationSection("O que a página faz", [
                    P("Esta página mede o **coeficiente volumétrico de transferência de oxigênio (kLa)** pelo método dinâmico de gassing-out, em meio abiótico. O ensaio tira o oxigênio do líquido com nitrogênio, devolve ar e observa a velocidade com que o oxigênio volta: quanto mais rápido, maior o kLa daquela condição de rotação e vazão."),
                    P("A cada corrida o aplicativo ajusta `ln(C* − C)` contra o tempo. Num sistema de primeira ordem essa reta tem inclinação `−kLa`, e é dela que o número sai — com R², RMSE e intervalo de confiança, porque uma inclinação sem incerteza não é uma medida."),
                    N("O ensaio é **abiótico**: não há consumo de oxigênio por células. Rodar com cultivo dentro invalida a hipótese do ajuste, não apenas o valor."),
                ]),
                new DocumentationSection("Como a página é organizada", [
                    F("Cabeçalho", "Estado do ensaio, as leituras ao vivo (OD dissolvido, vazão atual, tempo da fase e tempo total) e as ações de ensaio: Novo Teste, Importar Teste, Parar e Revisar, Concluir Teste e Abortar."),
                    F("Coluna esquerda", "A preparação e a decisão: a matriz de condições, o card de revisão e aceite, e os limiares de operação."),
                    F("Coluna direita", "Os três gráficos da corrida. Em janela estreita eles viram um seletor — Oxigênio, Regressão e Diagnóstico — em vez de encolherem até não mostrarem eixo."),
                ]),
                new DocumentationSection("Matriz de condições", [
                    P("Uma linha por **replicata**, não por condição: três réplicas de 300 rpm e 2 L/min são três linhas, e cada uma vira uma corrida com a sua própria curva e o seu próprio arquivo."),
                    F("N (rpm) · Q (L/min) · Reps · +", "Inclui uma condição na matriz, com a quantidade de réplicas pedidas."),
                    F("Status", "Onde aquela réplica está: pendente, em andamento, aceita ou rejeitada."),
                    F("kLa", "O valor aceito daquela réplica, quando já houver um."),
                    F("Ações", "O ícone de tendência recarrega a curva e os resultados daquela corrida para revisão; a lixeira remove a condição da matriz, com confirmação."),
                    F("Executar Sequência", "Roda a matriz sozinha. A caixa de diálogo deixa escolher entre executar **apenas as pendentes** — as que ainda não têm réplica aceita — ou a matriz inteira."),
                ]),
                new DocumentationSection("Como uma corrida acontece", [
                    P("Cada corrida segue a mesma sequência de fases, e o cabeçalho mostra em qual delas o ensaio está:"),
                    B("**Fechar gases** — o ensaio confirma pelo fluxômetro que tudo está fechado antes de começar. Sem essa confirmação nada avança."),
                    B("**Abrir N₂ e desoxigenar** — o nitrogênio entra com a rotação de desgaseificação até o oxigênio cair abaixo do limiar de desligamento."),
                    B("**Fechar N₂ e esperar estabilizar** — o gás é cortado e o ensaio espera a leitura parar de cair, medindo a derivada do sinal. É o que garante que o `t₀` não pegue a sonda ainda em queda."),
                    B("**Alívio (opcional)** — quando a montagem tem válvula de alívio, a vazão é estabilizada fora do vaso antes de entrar, para que o pulso inicial do fluxômetro não contamine o começo da curva."),
                    B("**Abrir ar e reoxigenar** — a curva que interessa. Termina quando o oxigênio atinge o limiar superior."),
                    B("**Parar e revisar** — as válvulas fecham e a corrida espera a sua decisão."),
                    N("Fluxômetro offline, leitura de oxigênio inválida ou telemetria parada abortam a corrida em vez de continuar medindo às cegas."),
                ]),
                new DocumentationSection("Revisão e aceite", [
                    P("É aqui que a curva vira número. O ajuste automático propõe uma região; você confere e, se quiser, corrige."),
                    F("Região Linear (s)", "O trecho da reoxigenação usado no ajuste log-linear. Encurtar pela esquerda descarta o transiente inicial; encurtar pela direita descarta a saturação."),
                    F("Região C* (s)", "O trecho da cauda usado para estimar a concentração de equilíbrio."),
                    F("Ceq (%)", "Fixa o C* manualmente, em vez de estimá-lo. A caixa ao lado liga o modo manual."),
                    F("Recalcular", "Refaz o ajuste com as regiões e o C* que estão na tela."),
                    F("kLa Estimado · IC 95%", "O resultado e o seu intervalo de confiança."),
                    F("R² · RMSE · Sensib.", "A qualidade do ajuste e quanto o kLa mudaria se o C* fosse outro. Sensibilidade alta quer dizer que o número depende mais da escolha de C* do que da curva."),
                    F("Aceitar Corrida", "Grava a análise como revisão daquela réplica e marca a matriz."),
                    F("Rejeitar", "Registra a corrida como inconclusiva, com motivo. Os dados brutos continuam em disco."),
                    F("Repetir", "Roda de novo a mesma condição; a tentativa anterior é preservada numa pasta própria."),
                    F("Aceite Automático", "Aceita sozinho as corridas que passam nos critérios. Útil para uma matriz longa sem operador presente — e a razão para conferir os limiares antes."),
                    N("Reanalisar uma corrida cria uma **nova revisão**, não sobrescreve a anterior: o histórico de análises fica no arquivo do ensaio."),
                ]),
                new DocumentationSection("Limiares de operação", [
                    F("Desligar N₂ (%)", "O oxigênio abaixo do qual o nitrogênio é cortado — o piso da curva."),
                    F("DO Máx (%)", "O oxigênio em que a reoxigenação termina — o teto da curva."),
                    F("Rot. N₂ (rpm)", "A rotação usada durante a desgaseificação, que não precisa ser a da condição."),
                    F("Engrenagem", "Abre os **parâmetros avançados**: tempos máximos de desoxigenação e reoxigenação, a espera após desligar o N₂ (atraso mínimo, janela da derivada, confirmações consecutivas, espera máxima), a estabilização no alívio e os padrões da análise automática (suavização e Ceq inicial). Alterações válidas valem ao vivo."),
                ]),
                new DocumentationSection("Os três gráficos", [
                    F("Oxigênio Dissolvido", "A corrida como ela acontece: OD bruto e filtrado, as linhas de DO mín e DO máx e o ajuste exponencial correspondente ao kLa estimado."),
                    F("Regressão Log-Linear", "`ln(C* − C)` contra o tempo, com a reta de mínimos quadrados. É o gráfico em que um ajuste ruim aparece como curvatura — algo que o R² sozinho não conta."),
                    F("kLa Instantâneo Diagnóstico", "O kLa calculado ponto a ponto e o valor final. Um kLa instantâneo que deriva ao longo da janela indica região linear mal escolhida."),
                ]),
                new DocumentationSection("O que fica gravado", [
                    P("Cada ensaio é uma pasta autocontida em `Testes-kLa/`, e cada réplica tem a sua."),
                    B("`dados-brutos.csv` da corrida: todo quadro recebido, em todas as fases — inclusive N₂ e intertravamentos — com temperatura e rotação medida."),
                    B("`serie-global.csv`: o ensaio inteiro, do início ao fim, mesmo entre corridas."),
                    B("`analise.json` com as revisões e o hash dos dados brutos, `resultado.csv` por corrida e `resumo-resultados.csv` por condição."),
                    F("Importar Teste", "Abre um ensaio salvo para visualizar e reanalisar as curvas, sem tocar no equipamento."),
                    F("Concluir Teste · Abortar", "Encerram o ensaio: o primeiro o marca como concluído; o segundo interrompe e fecha os gases com segurança."),
                ]),
            ]),

        // ══════════════════════════════════════════════════ Mapeamento kLa
        new DocumentationTopic(
            KlaMappingTopicId,
            "Mapeamento kLa",
            "Dos pontos medidos à trajetória publicada: a superfície contínua, o gradiente, a folga aos limites e o que a cascata consome.",
            [
                new DocumentationSection("O que a página faz", [
                    P("O mapeamento transforma os kLa medidos em pontos isolados numa **superfície contínua** sobre o plano (vazão de ar, agitação), e sobre ela calcula o caminho que a cascata deve percorrer para aumentar o kLa mantendo-se o mais longe possível dos limites dos atuadores."),
                    P("A cadeia é a do artigo, e a página a exibe no subtítulo: dados experimentais → superfície contínua → gradiente normalizado → máxima folga → perfil publicado."),
                    N("**Esta página nunca envia comandos.** Ela é ajuste científico; o que ela produz só chega ao reator quando uma cascata configurada no modo *Mapa* consome o perfil publicado."),
                ]),
                new DocumentationSection("Como a página é organizada", [
                    F("Coluna esquerda — Dados", "Experimentos, identificação, domínio dos atuadores e a tabela de pontos medidos."),
                    F("Centro — Superfície", "O mapa de kLa com o gradiente e a trajetória, e abaixo dele a classificação por folga média aos limites."),
                    F("Coluna direita — Diagnóstico", "Identidade do algoritmo, parâmetros numéricos, diagnóstico científico, recusas e o estado de publicação."),
                    F("Rodapé", "Salvar experimento, Estimar superfície, Calcular trajetória, Cancelar e Publicar para controle."),
                    B("Abaixo de 1200 DIP as três colunas viram um seletor Dados / Superfície / Diagnóstico. Nada é removido: a seção fora da tela mantém o estado."),
                ]),
                new DocumentationSection("Experimento e domínio", [
                    F("Novo experimento · Criar", "Cada experimento é um arquivo próprio em `Mapas/`, com os seus pontos e a sua superfície."),
                    F("Duplicar · Excluir · Importar…", "Reaproveitar um experimento como base, removê-lo ou trazer um arquivo de fora."),
                    F("Nome · Caldo / meio · Ensaio / lote · Notas", "A identificação que torna o mapa citável depois. O meio importa: kLa medido em água não descreve um caldo viscoso."),
                    F("Qg mín. / máx. · N mín. / máx.", "O **domínio dos atuadores**: os limites dentro dos quais a superfície é construída e a trajetória pode andar. São os limites do equipamento e do ensaio, não da matemática."),
                ]),
                new DocumentationSection("Pontos medidos", [
                    F("Tabela N · Qg · kLa", "Os pontos experimentais. Cada linha é uma condição medida e o seu kLa; o × remove a linha."),
                    F("+ Ponto", "Acrescenta uma linha em branco para digitar."),
                    F("Desenho 3²", "Gera as nove coordenadas de um fatorial 3×3 dentro do domínio. Cria **apenas as coordenadas**: o kLa de cada uma continua em branco até ser medido."),
                    F("Importar de Teste…", "Traz as médias das réplicas aceitas de um ensaio da página Determinar kLa. É o caminho normal — medir lá, mapear aqui, sem digitar número."),
                    F("Ordenar", "Ordena por N e depois por Qg, o que torna a tabela conferível a olho."),
                    N("A superfície só é definida dentro do fecho convexo dos pontos. Fora dele nada é extrapolado em silêncio — a região aparece indefinida."),
                ]),
                new DocumentationSection("Superfície, gradiente e trajetória", [
                    F("Estimar superfície", "Ajusta a superfície contínua aos pontos medidos. O painel de diagnóstico passa a mostrar faixa, resíduos e cobertura."),
                    F("Calcular trajetória", "Integra o caminho de subida sobre a superfície, escolhendo a condição inicial de maior folga aos limites."),
                    F("Mapa de superfície", "As cores são o kLa; os traços cinza, o gradiente normalizado — a direção de maior aumento em cada ponto; a linha azul, a trajetória; o ponto laranja, a condição inicial escolhida."),
                    F("Classificação por folga média", "O segundo mapa varre condições iniciais e mostra a folga média aos limites de cada uma. A crista clara é o conjunto de partidas que ficam longe das bordas por mais tempo — e o ponto azul é a escolhida."),
                    F("Referência · Prévia rápida", "Referência restaura os parâmetros numéricos do artigo; a prévia calcula numa malha grosseira, para ver o efeito de uma mudança sem esperar o cálculo completo."),
                ]),
                new DocumentationSection("Diagnóstico científico", [
                    P("A coluna direita existe para que o mapa possa ser defendido, não apenas exibido."),
                    F("Identidade do algoritmo", "O método exato: interpolação Clough–Tocher em malha 300×300, preenchimento por vizinho mais próximo, suavização gaussiana, spline bicúbica, integração RK45 e a malha de folga 150×150. **Parâmetros de referência: True** quer dizer que nada foi alterado em relação ao artigo."),
                    F("Faixa da superfície", "Os kLa mínimo e máximo que a superfície assume no domínio."),
                    F("Resíduos nos pontos medidos", "RMSE e maior desvio entre a superfície e os pontos que a geraram. É a primeira coisa a olhar: resíduo grande é superfície que não descreve os próprios dados."),
                    F("Cobertura do domínio", "Quanto do domínio a superfície define, e quantos nós dependeram do vizinho mais próximo em vez da interpolação."),
                    F("Condição inicial selecionada", "O ponto de partida da trajetória, em unidades normalizadas e físicas."),
                    F("Folga média normalizada", "A folga da trajetória escolhida e a melhor avaliada. Iguais significam que a escolha foi a melhor disponível."),
                    F("Relação de alocação", "A faixa de kLa que a trajetória percorre, o seu comprimento e quantos pontos ela tem."),
                    F("Superfície · Trajetória (hashes)", "Identificam esta revisão exata. É o que um resultado cita para dizer qual mapa o gerou."),
                    F("Recusas e avisos", "O que o cálculo se recusou a fazer — e por quê. Silêncio aqui é diferente de ausência de problema: significa que nada foi recusado."),
                ]),
                new DocumentationSection("Publicar e arquivar", [
                    F("Publicar para controle", "Torna o perfil disponível para a cascata de oxigênio. Antes disso, **Disponibilidade para controle** diz *não publicado*, e nenhuma cascata pode consumi-lo."),
                    F("Última publicação", "Quando o perfil em uso foi publicado — o que distingue o mapa que está na tela do que está sendo usado."),
                    F("Arquivo no disco · Copiar caminho · Abrir pasta · Exportar…", "Onde o experimento está gravado e como levá-lo para outro lugar."),
                    B("Na receita, o bloco Controle de O₂ no modo *Mapa (trajetória kLa)* é quem consome o perfil publicado — veja o assunto Receitas · Cascata de O₂."),
                ]),
            ]),

        // ═══════════════════════════════════════════════════ Potência · tara
        new DocumentationTopic(
            PowerTareTopicId,
            "Potência · Tara do eixo",
            "A calibração de referência do ensaio: a potência de vazio e o ruído de torque da montagem.",
            [
                new DocumentationSection("O que a tara mede", [
                    P("A tara no ar é a calibração de referência do ensaio de potência. Com o vaso seco, ela quantifica quanta potência a própria montagem consome — atrito de selos, mancais e acoplamento — em cada rotação, e qual é o ruído basal de torque (στ) nessa rotação."),
                    P("O aplicativo desconta essa potência de vazio (P₀) da potência de eixo medida no ensaio. O que sobra é a potência líquida dissipada no fluido, que é a grandeza que alimenta o Np."),
                    N("Sem tara aplicada o ensaio roda em modo relativo: os números continuam válidos para comparar condições entre si, mas não são Np absolutos."),
                ]),
                new DocumentationSection("Os controles do card", [
                    F("Medir nova tara", "Abre o assistente de varredura no ar. N ini, N fim e Passo definem os patamares de rotação."),
                    F("Iniciar ensaio no ar", "Começa a varredura. Cada patamar passa por estabilização de velocidade, depois por estacionariedade de torque, e só então acumula amostras até fechar o IC95."),
                    F("Tabela de patamares", "Uma linha por rotação: N alvo e N média medida, P₀ e o seu ±IC95, o στ %, o número de amostras (n) e quantas tentativas foram necessárias."),
                    F("Perfil existente · Usar no ensaio", "Aplica ao ensaio aberto uma curva já arquivada, sem repetir a varredura no ar."),
                    F("Criar nova tara · Salvar nova tara", "Arquiva sob um nome a curva medida neste ensaio, para reuso nos próximos."),
                ]),
                new DocumentationSection("Regras que valem lembrar", [
                    B("A tara pertence à montagem — eixo, selo, acoplamento e o conjunto de impelidores — e não ao fluido. Trocar qualquer impelidor pede nova tara."),
                    B("A bancada opera mais de um eixo, então a biblioteca guarda uma curva por eixo. O aplicativo compara o conjunto de impelidores do perfil com o do ensaio e avisa quando divergem, em vez de aplicar em silêncio a tara do eixo errado."),
                    B("Um patamar que não converge dentro das tentativas configuradas interrompe a varredura, e a curva válida anterior é mantida."),
                    B("As leituras são gravadas em Taras-Brutas/ enquanto a varredura corre, uma por varredura. Uma varredura cancelada ou interrompida deixa em disco tudo o que mediu."),
                ]),
            ]),

        // ════════════════════════════════════════════ Potência · ponto único
        new DocumentationTopic(
            PowerSinglePointTopicId,
            "Potência · Ponto único",
            "A conferência manual de uma condição, fora da tabela do ensaio.",
            [
                new DocumentationSection("Para que serve", [
                    B("Conferir o acionamento antes de iniciar um ensaio."),
                    B("Inspeção pré-ensaio: ausência de vibração anormal, vazamento e vórtice."),
                    B("Leitura instantânea de torque e potência numa condição avulsa."),
                    B("Incluir na tabela do ensaio uma condição que você acabou de testar."),
                ]),
                new DocumentationSection("Os controles do card", [
                    F("N (rpm) · Qg (L/min)", "A condição a comandar. Qg em zero mantém o ensaio seco."),
                    F("Comandar", "Leva o eixo à condição e começa a mostrar a leitura ao vivo."),
                    F("Parar eixo", "Encerra a conferência e libera o atuador."),
                    F("Adicionar à tabela de condições", "Insere no ensaio a condição testada. O ponto único não faz isso sozinho."),
                ]),
                new DocumentationSection("O que ele usa e o que ele grava", [
                    P("O ponto único usa a mesma tara e a mesma calibração do ensaio aberto — sem elas, os números saem rotulados como relativos. As guardas de segurança do ensaio valem aqui, inclusive o mínimo de 15 rpm."),
                    B("Cada conferência é gravada em Pontos-Unicos/, com um manifesto que registra a rotação e a vazão comandadas e o torque nominal do motor. Isso vale também quando nenhum ensaio está aberto."),
                ]),
            ]),

        // ═════════════════════════════════════ Potência · correlação elétrica
        new DocumentationTopic(
            PowerElectricalTopicId,
            "Potência · Correlação elétrica",
            "Como a potência de eixo se relaciona com a potência elétrica medida na tomada.",
            [
                new DocumentationSection("O que é correlacionado", [
                    P("A potência de eixo é calculada a partir do torque do servo. A potência elétrica ativa é medida por um wattímetro externo na tomada do motor e digitada pelo operador durante uma corrida, quando o ensaio pausa para a leitura."),
                    P("O ajuste entre as duas descreve as perdas do conjunto — acionamento, motor e transmissão — nos pontos medidos."),
                ]),
                new DocumentationSection("A tabela", [
                    F("N", "Rotação em que a leitura foi tomada."),
                    F("P mec (W)", "Potência de eixo calculada pelo servo naquele instante."),
                    F("P elét (W)", "Potência elétrica ativa digitada pelo operador."),
                    F("Instrumento", "Qual wattímetro produziu a leitura, para rastreabilidade."),
                    N("A leitura elétrica é registrada junto da corrida, mas não entra na potência líquida nem no Np: é grandeza do conjunto, não do fluido."),
                ]),
            ]),

        // ══════════════════════════════════════ Potência · mapa de kLa
        new DocumentationTopic(
            PowerKlaMapTopicId,
            "Potência · Mapa de kLa e eficiência",
            "Vincular um mapa de kLa ao ensaio de potência e ler a eficiência de oxigenação.",
            [
                new DocumentationSection("O vínculo", [
                    P("Vincular um mapa de kLa permite cruzar, na mesma condição de rotação e vazão, o kLa interpolado na superfície contínua do mapa com a potência líquida medida aqui. A tabela de condições do ensaio é preservada pelo vínculo."),
                    F("Lista de mapas · ↻", "Os mapas de kLa salvos no workspace; o botão recarrega a lista."),
                    F("Vincular mapa de kLa", "Associa o mapa escolhido ao ensaio aberto."),
                ]),
                new DocumentationSection("A leitura de eficiência", [
                    B("A eficiência kLa/(P/V) é calculada na região de controle, que é a intersecção entre as condições do mapa e as do ensaio."),
                    B("A tabela compara, condição a condição, o kLa interpolado, a potência líquida, a potência volumétrica P/V e a eficiência específica η."),
                    N("Fora do fecho convexo do mapa a superfície é indefinida e nada é extrapolado em silêncio: a condição aparece marcada como fora da região."),
                ]),
            ]),
    ];
}
