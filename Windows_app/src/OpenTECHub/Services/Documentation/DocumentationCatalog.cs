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
public sealed record DocumentationBlock(DocumentationBlockKind Kind, string Text, string Label = "");

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
