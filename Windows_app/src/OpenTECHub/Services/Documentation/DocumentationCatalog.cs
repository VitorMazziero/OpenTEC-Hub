using System;
using System.Collections.Generic;
using System.Linq;

namespace OpenTECHub.Services.Documentation;

/// <summary>How one block of documentation is rendered.</summary>
public enum DocumentationBlockKind
{
    Paragraph,
    Bullet,
    Field,
    Formula,
    Note,
}

/// <summary>A unit of text shown in the in-app manual.</summary>
public sealed record DocumentationBlock(DocumentationBlockKind Kind, string Text, string Label = "")
{
    public string Markup => Kind == DocumentationBlockKind.Field && Label.Length > 0
        ? $"**{Label}** — {Text}"
        : Text;

    public string Caption => Kind == DocumentationBlockKind.Formula ? Label : "";

    public bool HasCaption => Caption.Length > 0;
}

/// <summary>A titled group of explanations inside a documentation topic.</summary>
public sealed record DocumentationSection(string Title, IReadOnlyList<DocumentationBlock> Blocks);

/// <summary>A page of the in-app operator manual.</summary>
public sealed record DocumentationTopic(
    string Id,
    string Title,
    string Summary,
    IReadOnlyList<DocumentationSection> Sections);

/// <summary>
/// Text of the operator manual shown in Configurações › Documentação.
///
/// The catalog intentionally describes what a person sees, why a control exists, how to use it
/// and how to interpret the result. Implementation names, communication fields and storage
/// details belong to engineering records, not to the operator manual.
/// </summary>
public static class DocumentationCatalog
{
    public const string DashboardTopicId = "painel";
    public const string ControlTopicId = "controle";
    public const string RecipesTopicId = "receitas";
    public const string RecipeBlocksTopicId = "receitas-blocos";
    public const string RecipeOxygenTopicId = "receitas-controle-o2";
    public const string KlaDeterminationTopicId = "kla-determinacao";
    public const string KlaMappingTopicId = "kla-mapeamento";
    public const string PowerTopicId = "potencia";
    public const string PowerMapTopicId = "potencia-mapa";
    public const string CalibrationTopicId = "calibracoes";
    public const string HistoryTopicId = "historicos";
    public const string EventsTopicId = "eventos";
    public const string IntegrationTopicId = "integracao-kla-potencia";
    public const string PowerTareTopicId = "potencia-tara";
    public const string PowerSinglePointTopicId = "potencia-ponto-unico";
    public const string PowerElectricalTopicId = "potencia-correlacao-eletrica";
    public const string PowerKlaMapTopicId = "potencia-mapa-kla";
    public const string SettingsConnectionTopicId = "configuracoes-conexao";
    public const string SettingsGasRigTopicId = "configuracoes-gas-valvulas";

    private static readonly Lazy<IReadOnlyList<DocumentationTopic>> LazyTopics = new(Build);

    public static IReadOnlyList<DocumentationTopic> Topics => LazyTopics.Value;

    public static DocumentationTopic? Find(string? topicId) =>
        string.IsNullOrWhiteSpace(topicId)
            ? null
            : Topics.FirstOrDefault(t => string.Equals(t.Id, topicId, StringComparison.OrdinalIgnoreCase));

    private static DocumentationBlock P(string text) => new(DocumentationBlockKind.Paragraph, text);
    private static DocumentationBlock B(string text) => new(DocumentationBlockKind.Bullet, text);
    private static DocumentationBlock F(string label, string text) => new(DocumentationBlockKind.Field, text, label);
    private static DocumentationBlock N(string text) => new(DocumentationBlockKind.Note, text);
    private static DocumentationBlock Eq(string formula, string caption) => new(DocumentationBlockKind.Formula, formula, caption);

    private static IReadOnlyList<DocumentationTopic> Build() =>
    [
        new DocumentationTopic(
            DashboardTopicId,
            "Painel",
            "A tela para acompanhar o processo, visualizar tendências e registrar acontecimentos.",
            [
                new DocumentationSection("Como a página é organizada", [
                    P("O Painel reúne as leituras atuais do biorreator e dos dispositivos externos. Ele é uma tela de acompanhamento: para alterar uma condição, use Controle, uma receita ou a página do ensaio correspondente."),
                    F("Cartões de leitura", "Mostram o valor mais recente de cada variável, a unidade e um indicador de estado."),
                    F("Gráficos", "Exibem uma ou mais variáveis ao longo do tempo. Cada gráfico pode mostrar uma variável diferente."),
                    F("Ferramentas", "Permitem escolher a quantidade de gráficos, ajustar a janela de tempo, pausar a visualização, limpar a tela, marcar um evento e exportar dados."),
                    F("Barra de estado", "Resume a receita em execução, o registro da sessão, a conexão e o momento da última atualização."),
                ]),
                new DocumentationSection("Acompanhar as leituras", [
                    P("Use os cartões como uma visão rápida do processo. Para investigar uma variável, abra o detalhe do cartão e observe a tendência e a qualidade do sinal."),
                    B("Arraste um cartão para um gráfico para acompanhar aquela variável."),
                    B("Clique em um cartão para abrir seus detalhes."),
                    N("Um traço (—) significa que não há uma leitura válida disponível. Ele não representa zero."),
                ]),
                new DocumentationSection("Ferramentas dos gráficos", [
                    F("2 Gráficos", "Alterna entre 1, 2 e 4 áreas de visualização."),
                    F("Janela", "Define quanto tempo fica visível na tela. Aumente a janela para enxergar uma tendência mais longa."),
                    F("Pausar", "Congela apenas o desenho para você examinar a curva. A aquisição e o registro continuam."),
                    F("Cursor", "Mostra os valores da curva no ponto indicado pelo mouse."),
                    F("Limpar", "Remove os traços da tela sem apagar o registro da sessão."),
                    F("Mostrar novamente", "Reconstrói os gráficos usando novamente os dados da sessão."),
                    F("Nova Etapa", "Começa uma nova etapa de registro e reinicia o relógio relativo. Use ao trocar de condição experimental."),
                    F("Marcar evento", "Adiciona uma anotação com texto e horário ao registro do processo."),
                    F("Exportar", "Gera um arquivo CSV com os dados da janela atualmente visualizada."),
                ]),
                new DocumentationSection("Tempo e registro", [
                    F("Tempo de processo · Zerar", "Reinicia o relógio exibido sem interromper a aquisição."),
                    F("Nova Etapa / Corrida", "Abre uma nova etapa de acompanhamento e zera o tempo relativo."),
                    F("Receita · Fase · Decorrido · Próxima ação", "Mostram o andamento da receita, quando houver uma em execução."),
                    F("Registro", "Indica se os dados estão sendo gravados e quantas amostras já foram registradas."),
                    F("Estado do sistema · Última atualização", "Ajudam a perceber uma perda de comunicação. Se o horário parar de avançar, verifique a conexão."),
                ]),
            ]),

        new DocumentationTopic(
            ControlTopicId,
            "Controle",
            "A página para definir condições, acompanhar respostas e operar os equipamentos com segurança.",
            [
                new DocumentationSection("Como a página é organizada", [
                    P("Cada linha representa uma variável ou um dispositivo. O valor lido é o que está acontecendo agora; o novo setpoint é o alvo que você pretende aplicar."),
                    F("Tabela superior", "Reúne agitação, temperatura, pH, alívio de pressão, oxigênio, nutrientes e antiespumante."),
                    F("Dispositivos Externos", "Reúne vazão de ar, distância, bomba externa, absorbância e frasco agitador."),
                    F("Detalhe da linha", "Abra pela seta para ver calibração, sintonia, saúde do sinal e controles específicos."),
                    F("Parada segura", "Interrompe os atuadores de forma coordenada quando é necessário colocar o sistema em condição segura."),
                ]),
                new DocumentationSection("Como ler uma linha", [
                    F("Variável / Dispositivo", "Identifica o equipamento e mostra um indicador de estado."),
                    F("Valor Lido", "Mostra a medida atual. Na agitação, aparecem rotação e torque."),
                    F("Setpoint", "É o alvo atualmente confirmado pelo equipamento."),
                    F("Novo Setpoint", "É o valor que será solicitado quando você terminar a edição."),
                    F("Unidade · Faixa", "Indica como interpretar o número e quais limites são aceitos."),
                    F("Ativo", "Liga ou desliga a atuação daquela variável. Desligar a atuação não apaga o alvo informado."),
                    N("Zero rpm desliga o acionamento da agitação. Para operar, use a faixa recomendada exibida na tela."),
                ]),
                new DocumentationSection("Enviar uma condição", [
                    P("Digite o valor em Novo Setpoint e pressione Enter ou saia do campo. O aplicativo confere os limites antes de enviar a solicitação."),
                    B("Um valor fora da faixa é recusado e não chega ao equipamento."),
                    B("Algumas variáveis usam uma lista de modos em vez de um número. Escolha o modo e abra a engrenagem para os ajustes específicos."),
                    B("Enquanto uma receita, uma automação ou um ensaio estiver usando um atuador, o controle manual correspondente pode ficar bloqueado. Isso evita dois comandos conflitantes."),
                ]),
                new DocumentationSection("Variáveis internas", [
                    F("Agitação", "Permite definir a rotação e acompanhar torque, carga, potência estimada e energia acumulada. Use Zerar energia acumulada somente para reiniciar o contador de acompanhamento."),
                    F("Temperatura", "Mostra a temperatura atual e a qualidade das últimas leituras, ajudando a distinguir uma medida estável de um sinal ruidoso."),
                    F("pH", "A Histerese cria uma pequena faixa em torno do alvo para evitar acionamentos repetidos. Operação, Mistura e Intensidade definem como a dosagem é feita."),
                    F("Oxigênio", "Controla o oxigênio dissolvido usando um dos modos disponíveis. O detalhe reúne a calibração da sonda e os ajustes da malha."),
                    F("Nutrientes", "Define os tempos de dosagem e mistura, além do intervalo de repetição do ciclo."),
                    F("Antiespumante", "Define a duração da dosagem e o tempo de mistura antes da próxima verificação."),
                    F("Alívio de Pressão", "Permite acompanhar e ajustar a pressão diretamente na tabela."),
                ]),
                new DocumentationSection("Dispositivos externos", [
                    F("Vazão de Ar", "Escolha a entrada que será usada e defina a vazão. O detalhe mostra a rota do gás, a calibração e os ajustes do controlador."),
                    F("Distância", "Configura a medição por ultrassom. Em automação de espuma, a bomba de nutrientes pode ser usada para aplicar antiespumante."),
                    F("Bomba Externa", "Escolha um perfil de dosagem, confira a prévia de vazão e volume e envie o perfil quando estiver satisfeito. Zerar volume reinicia o volume acumulado da bomba."),
                    F("Absorbância", "Mostra absorbância e leitura bruta, permite capturar o branco, iniciar ou parar a leitura e ajustar os limiares usados pela automação."),
                    F("Frasco Agitador", "Define intensidade, sentido e automação por espuma. Reativar potenciômetro devolve o comando ao controle físico da bancada."),
                    N("A posição das válvulas de gás é explicada em Documentação › Gás e válvulas."),
                ]),
                new DocumentationSection("Predefinições e parada", [
                    F("Predefinição", "É um conjunto de alvos salvo para repetir uma condição de processo."),
                    F("Carregar", "Preenche a coluna Novo Setpoint. Revise os valores e confirme cada alteração; carregar não aciona o equipamento sozinho."),
                    F("Salvar como…", "Salva os valores atuais como uma nova predefinição."),
                    F("Parada segura", "Interrompe a atuação e leva rotação, aquecimento, vazão e bombas para uma condição segura. Use em uma emergência ou antes de abandonar o processo."),
                ]),
            ]),

        new DocumentationTopic(
            RecipesTopicId,
            "Receitas",
            "Como montar uma sequência automática de etapas e acompanhar sua execução.",
            [
                new DocumentationSection("O que é uma receita", [
                    P("Uma receita é uma sequência visual de ações, esperas e decisões. Ela permite repetir um procedimento com a mesma ordem e os mesmos critérios."),
                    B("Exemplo: aquecer, estabilizar, ligar agitação e aeração e iniciar o controle de oxigênio."),
                    B("Exemplo: dosar nutrientes em ciclos e registrar um evento quando uma condição for atingida."),
                    B("Exemplo: parar em uma Intervenção Manual para que o operador confira a bancada antes de continuar."),
                    N("A receita respeita os mesmos limites e a mesma parada segura da operação manual."),
                ]),
                new DocumentationSection("Como o fluxo funciona", [
                    P("Cada bloco representa uma etapa. As conexões indicam qual bloco vem depois. A receita começa em Início e termina em Fim."),
                    B("Todo caminho precisa sair de Início e chegar a pelo menos um Fim."),
                    B("Blocos de espera só liberam a sequência quando o tempo ou a condição definida for atendida."),
                    B("Sincronizar (E) espera todos os caminhos. Qualquer (OU) continua pelo primeiro caminho que chegar."),
                    B("O laço de Controle de O₂ é o local próprio para repetir o controle."),
                ]),
                new DocumentationSection("A página, parte por parte", [
                    F("Barra superior", "Contém salvar, carregar, excluir, desfazer, refazer, validação e os controles de execução."),
                    F("Minhas Receitas", "Lista os procedimentos salvos para abrir, duplicar ou excluir."),
                    F("Biblioteca de blocos", "Oferece os tipos de etapa que podem ser colocados no desenho."),
                    F("Canvas", "É a área onde os blocos são posicionados e conectados."),
                    F("Propriedades do Bloco", "Mostra os ajustes do bloco selecionado."),
                    F("JSON & Validação", "O JSON é a representação técnica da receita. A validação informa o que impede a execução e o que é apenas um aviso."),
                ]),
                new DocumentationSection("Montar e editar", [
                    B("Clique em um bloco da biblioteca para adicioná-lo."),
                    B("Conecte a saída de um bloco à entrada do próximo."),
                    B("Selecione um bloco ou conexão e use Excluir bloco ou Delete."),
                    B("Use Ctrl+Z e Ctrl+Y para desfazer e refazer alterações."),
                    B("Em listas de pontos ou controles, use + Adicionar para incluir outra linha."),
                ]),
                new DocumentationSection("Salvar e validar", [
                    F("Salvar", "Guarda a receita atual com o nome da aba."),
                    F("Carregar", "Abre uma receita que já foi salva."),
                    F("Validação", "Erros precisam ser corrigidos antes de iniciar. Avisos chamam atenção para algo que pode ser intencional."),
                    N("Leia a receita do início ao fim antes de iniciar e confirme que cada caminho chega ao resultado desejado."),
                ]),
                new DocumentationSection("Executar uma receita", [
                    P("Ao iniciar, a receita assume temporariamente os atuadores envolvidos. Por isso o controle manual correspondente fica bloqueado enquanto ela roda."),
                    F("▶ Iniciar", "Começa a receita. Você pode escolher iniciar a partir de uma condição segura ou preservar o estado atual."),
                    F("⏸ Pausar / Retomar", "Suspende ou retoma o avanço entre etapas. Um equipamento já acionado continua na condição atual."),
                    F("⏹ Parar", "Encerra a receita e devolve o controle aos comandos manuais."),
                    F("⏭ Pular bloco", "Encerra uma espera do bloco atual e segue para o próximo. Use somente quando tiver certeza de que a condição foi atendida."),
                    F("Faixa de execução", "Mostra a etapa atual, o motivo de uma espera e o que já foi concluído."),
                    N("Uma perda de comunicação ou uma condição insegura interrompe a receita para evitar comandos sem confirmação."),
                ]),
                new DocumentationSection("As famílias de blocos", [
                    F("Fluxo", "Início e Fim organizam o começo e o término."),
                    F("Gatilhos", "Temporizador, Monitorar Variável e Intervenção Manual aguardam tempo, condição ou autorização."),
                    F("Lógica", "Sincronizar (E), Qualquer (OU) e Controle de O₂ organizam decisões e repetições."),
                    F("Ações", "Definir Ponto de Ajuste, Múltiplos Pontos de Ajuste, Controle de Malha e Múltiplos Controles enviam alterações ao processo."),
                    F("Bombas", "Bomba pH, Bomba Antiespuma e Bomba Nutrientes realizam dosagens internas."),
                    F("Dispositivos Externos", "Bomba Externa, Absorbância e Agitador de Frasco atuam em equipamentos externos."),
                    F("Utilitários", "Aquisição de Dados, Registrar Evento e Zerar Variáveis ajudam a acompanhar e organizar o procedimento."),
                ]),
            ]),

        new DocumentationTopic(
            RecipeBlocksTopicId,
            "Receitas · Blocos",
            "O significado de cada bloco e dos campos que aparecem ao configurá-lo.",
            [
                new DocumentationSection("Como escolher um bloco", [
                    P("Escolha o bloco pela intenção da etapa: iniciar, esperar, comandar, tomar uma decisão, registrar ou encerrar. Depois de selecioná-lo, leia os campos no painel de propriedades."),
                    N("Alguns campos aparecem ou desaparecem conforme a escolha feita no próprio bloco. Isso evita que você preencha opções que não se aplicam."),
                ]),
                new DocumentationSection("Fluxo", [
                    F("Início", "Marca o ponto onde a receita começa."),
                    F("Fim", "Marca uma saída concluída com sucesso."),
                ]),
                new DocumentationSection("Gatilhos — etapas que esperam", [
                    F("Temporizador", "Espera uma duração em segundos, minutos ou horas."),
                    F("Monitorar Variável", "Espera uma leitura satisfazer uma condição. Defina variável, comparação, valor alvo, frequência de verificação, confirmações consecutivas e tempo limite."),
                    F("Monitorar Variável · no laço do Controle de O₂", "Nesse uso, a leitura participa da decisão de cada repetição do controle, em vez de ser uma etapa separada."),
                    F("Intervenção Manual", "Mantém a receita parada até alguém autorizar a continuação. O botão alterna entre BLOQUEAR e PASSAR."),
                    F("Intervenção Manual · no laço do Controle de O₂", "Nesse uso, alterna entre Manter Rodando e Sair do Loop."),
                ]),
                new DocumentationSection("Lógica", [
                    F("Sincronizar (E)", "Só continua quando todos os caminhos ligados tiverem terminado."),
                    F("Qualquer (OU)", "Continua assim que o primeiro caminho ligado terminar."),
                    F("Controle de O₂", "Mantém o oxigênio dissolvido próximo do alvo enquanto a receita continua dentro desse bloco."),
                ]),
                new DocumentationSection("Ações", [
                    F("Definir Ponto de Ajuste", "Altera uma variável para o valor informado. Para pH, também pode definir Histerese."),
                    F("Múltiplos Pontos de Ajuste", "Altera vários alvos juntos, útil quando uma nova condição precisa entrar de uma vez."),
                    F("Controle de Malha", "Liga ou desliga uma malha de controle."),
                    F("Múltiplos Controles", "Liga ou desliga várias malhas em uma única etapa."),
                ]),
                new DocumentationSection("Bombas", [
                    F("Bomba pH", "Escolhe ácido ou base e define operação, intensidade, duração do pulso, tempo de espera e ação manual."),
                    F("Bomba Antiespuma", "Define operação, intensidade, pulso, mistura e ação manual para a dosagem de antiespuma."),
                    F("Bomba Nutrientes", "Define operação, duração da dosagem, mistura, volume e ação manual."),
                    N("Operação manual aciona uma ação imediata; operação por ciclo usa os tempos definidos para dosar e misturar."),
                ]),
                new DocumentationSection("Dispositivos externos", [
                    F("Bomba Externa", "Escolhe a ação e, ao enviar um perfil, seus tempos, parâmetros e segmentos de vazão."),
                    F("Absorbância", "Escolhe entre habilitar, capturar branco, iniciar, parar ou registrar os limiares."),
                    F("Agitador de Frasco", "Aciona ou para o agitador e define intensidade, sentido e modo automático."),
                ]),
                new DocumentationSection("Utilitários", [
                    F("Aquisição de Dados", "Marca no registro uma janela destinada à análise, por tempo ou por uma etapa do procedimento."),
                    F("Registrar Evento", "Escreve uma mensagem com horário para documentar uma ocorrência."),
                    F("Zerar Variáveis", "Reinicia variáveis acumuladas. Use com atenção, pois o estado anterior deixa de estar disponível para o processo."),
                ]),
            ]),

        new DocumentationTopic(
            RecipeOxygenTopicId,
            "Receitas · Controle de O₂",
            "Como o controle de oxigênio mantém o alvo e como escolher o modo de atuação.",
            [
                new DocumentationSection("O que este bloco faz", [
                    P("O Controle de O₂ compara continuamente o oxigênio medido com o alvo e ajusta agitação e/ou aeração. Ao inserir o bloco, ele começa em modo ∞ INFINITO e permanece ativo mesmo depois de estabilizar."),
                    P("Cascata é um dos quatro métodos de atuação do bloco. Os outros são Agitação, Aeração e Mapa (trajetória kLa)."),
                    N("Para entender o bloco em uma receita, pense em três perguntas: qual é o alvo, como o equipamento deve reagir e quando o controle deve terminar."),
                ]),
                new DocumentationSection("As quatro portas", [
                    F("Entrada", "Recebe o fluxo da etapa anterior."),
                    F("Condição de Saída", "Recebe um Temporizador, Monitorar Variável ou Intervenção Manual quando uma condição externa deve decidir o término. Ao ligar uma dessas opções, o laço interno infinito é substituído."),
                    F("Retorno da Condição", "Recebe o retorno da condição externa. Quando permanece ligado diretamente à Condição de Saída, forma o laço interno padrão ∞ INFINITO."),
                    F("Saída", "Continua a receita depois que o controle termina."),
                    N("Remova o conector entre Condição de Saída e Retorno da Condição para usar SAI AO ESTABILIZAR: o controle termina quando o oxigênio fica dentro de ±2% do alvo por três leituras consecutivas."),
                    B("Monitorar Variável e Intervenção Manual têm comportamento próprio quando estão ligados ao laço: eles ajudam a decidir se o controle continua ou termina."),
                ]),
                new DocumentationSection("Como a correção é calculada", [
                    P("O controle observa o valor atual, a tendência recente e a diferença para o alvo. Com isso, antecipa parte da necessidade e reduz oscilações."),
                    Eq("DOT_previsto = DOT_atual + tendência · horizonte", "A leitura atual é projetada alguns instantes à frente para antecipar a resposta do processo."),
                    Eq("erro = alvo − oxigênio previsto", "Quanto maior a diferença para o alvo, maior a correção solicitada."),
                    Eq("correção = Kp·erro + Ki·acúmulo + Kd·variação do erro", "Os três termos representam resposta imediata, efeito acumulado e reação à mudança do erro."),
                    F("SP de O₂ (%)", "É o alvo de oxigênio dissolvido."),
                    F("Intervalo de cálculo do PID (s)", "É o tempo entre duas correções. Intervalos menores reagem mais rápido, mas podem acompanhar mais ruído."),
                    F("K_DOT (laço externo)", "Define quanto a tendência de oxigênio influencia a correção prevista."),
                    F("Kp · Ki · Kd", "Ajustam, respectivamente, a resposta ao erro atual, ao erro acumulado e à mudança do erro."),
                    F("Horizonte t_pred (s)", "Define quanto à frente o controle tenta antecipar o comportamento."),
                    F("Janela do preditor (amostras)", "Define quantas leituras recentes participam da estimativa da tendência."),
                    F("τ_D do filtro (s)", "Suaviza a parte derivativa para evitar que ruído cause correções excessivas."),
                    F("Método (estimativa de taxa)", "Escolhe como a tendência é estimada: por uma reta ajustada às leituras ou por uma média móvel."),
                    F("Janela da média (amostras)", "Define quantas leituras entram na média usada pelo método escolhido."),
                ]),
                new DocumentationSection("Anti-windup", [
                    P("Quando um atuador chega ao limite, a correção desejada pode continuar aumentando mesmo sem poder ser aplicada. O anti-windup evita que esse excesso acumulado atrase a recuperação."),
                    Eq("I = limitar soma do erro recente entre I_min e I_max", "O acúmulo do erro é calculado em uma janela e mantido dentro de limites."),
                    F("I_min · I_max", "Limites inferior e superior do acúmulo do erro."),
                    F("Janela do integrador (s)", "Tempo de histórico considerado para o acúmulo."),
                ]),
                new DocumentationSection("Os quatro métodos de atuação", [
                    F("Agitação", "Somente a rotação varia para corrigir o oxigênio."),
                    F("Aeração", "Somente a vazão de gás varia para corrigir o oxigênio."),
                    F("Cascata (percentuais)", "A rotação atua primeiro ou em uma faixa inicial e a aeração entra na faixa seguinte. A sobreposição torna a transição gradual."),
                    F("Mapa (trajetória kLa)", "A saída do controle escolhe uma posição em uma trajetória previamente calculada no mapa de kLa. Cada posição fornece um par de rotação e vazão."),
                ]),
                new DocumentationSection("Faixas e ganhos", [
                    P("As faixas definem onde cada atuador pode trabalhar e os ganhos relativos informam qual deles tem mais efeito sobre a oxigenação nesta montagem."),
                    Eq("saída do atuador = faixa mínima + posição na faixa · (faixa máxima − faixa mínima)", "A saída do controle é convertida em um valor físico dentro dos limites definidos."),
                    F("N_min · N_max (rpm)", "Limites de rotação usados pelo controle."),
                    F("Q_min · Q_max (vvm)", "Limites de aeração usados pelo controle."),
                    F("Faixa da Agitação (% do output)", "Trecho da saída em que a rotação varia."),
                    F("Faixa da Aeração (% do output)", "Trecho da saída em que a vazão varia."),
                    F("Agitação e Aeração (ganho relativo)", "Indica quanto cada atuador contribui para alterar o oxigênio."),
                    N("Sobreposição entre faixas produz uma transição suave. Um intervalo sem nenhum atuador ativo pode deixar uma parte da demanda sem resposta."),
                ]),
                new DocumentationSection("Predefinições de oxigênio", [
                    F("Predefinição Salva · Carregar", "Carrega uma sintonia salva de ganhos, limites e faixas."),
                    F("Salvar como…", "Salva a sintonia atual para reutilização."),
                    N("A predefinição guarda a forma de atuação, não o alvo de oxigênio. Confira SP de O₂ depois de carregar."),
                ]),
            ]),

        new DocumentationTopic(
            KlaDeterminationTopicId,
            "Determinar kLa",
            "Como medir a velocidade de transferência de oxigênio e decidir se uma corrida é aproveitável.",
            [
                new DocumentationSection("O que a página mede", [
                    P("O ensaio de gassing-out mede a recuperação do oxigênio após uma retirada controlada. Abiótico usa N₂ em meio sem células; biótico mede primeiro o consumo respiratório e depois a reoxigenação."),
                    Eq("dC/dt = kLa · (C* − C) − OUR", "OUR é o consumo de oxigênio. No abiótico, considera-se OUR = 0."),
                    Eq("Ceq = C* − OUR/kLa", "Com consumo constante, o equilíbrio respiratório Ceq pode ser menor que a saturação física C*. Esses valores não são intercambiáveis."),
                    Eq("ln(Ceq − C) = constante − kLa · t", "A inclinação permite estimar a taxa quando as condições e o consumo são constantes. A resposta da sonda pode limitar essa identificação."),
                    N("Qualquer tecnologia de sonda pode ser usada. Resposta desconhecida condiciona o resultado; taxas inseparáveis da resposta da sonda são recusadas. A execução biótica no equipamento aguarda validação em bancada; análise de curvas salvas permanece disponível."),
                ]),
                new DocumentationSection("Como a página é organizada", [
                    F("Cabeçalho", "Seleciona Abiótico/Biótico e Único/Múltiplos e reúne as leituras atuais."),
                    F("Coluna lateral", "Agrupa preparação, etapa atual, próxima ação e revisão. O mesmo layout atende aos dois protocolos."),
                    F("Matriz de condições", "Lista as combinações de rotação, vazão e número de réplicas."),
                    F("Revisão e aceite", "Permite conferir a curva, ajustar a região analisada e aceitar, rejeitar ou repetir a corrida."),
                    F("Gráficos", "Começam no topo da área principal. As abas Oxigênio, Regressão e Diagnóstico usam o espaço inteiro; Ver todos permite compará-las."),
                ]),
                new DocumentationSection("Matriz de condições", [
                    P("Cada linha é uma réplica. Se uma condição tiver três réplicas, ela aparecerá em três linhas e cada linha terá sua própria decisão."),
                    F("N (rpm) · Q (L/min) · Reps · +", "Adiciona uma condição e informa quantas réplicas devem ser executadas."),
                    F("Status", "Indica se a réplica está pendente, em andamento, aceita ou rejeitada."),
                    F("kLa", "Mostra o resultado aceito quando a análise foi concluída."),
                    F("Ações", "Permite abrir uma corrida para revisão ou remover uma condição."),
                    F("Próxima corrida", "Executa o próximo item pendente após revisão, retorno e intervalo. Rejeitar uma tentativa mantém a mesma réplica pendente."),
                    F("Avançar após aceitar", "Continuação opcional após aceite. Não repete automaticamente resultados rejeitados."),
                    F("Encerrar fila", "Cancela o avanço e a espera, conservando histórico e corrida atual. Para parar a corrida, use a ação de parada com retorno."),
                ]),
                new DocumentationSection("Como uma corrida acontece", [
                    P("O ensaio passa por etapas para separar a preparação da medição. A barra de estado mostra a etapa atual."),
                    B("Preparar: estabilize no equilíbrio inicial, confira OD novo, vazão, agitação e confirmação dos gases."),
                    B("Abiótico: retirar O₂ com N₂ e agitação de remoção; aguardar OD estável; estabilizar ar no escape pelo procedimento de válvulas; comutar ar para o reator."),
                    B("Biótico: manter fluxômetro ligado (v_flow aberto), N₂ desligado e isolado; desviar ar para o escape e usar a queda de OD para estimar consumo; recolocar ar no reator pela válvula."),
                    B("Reoxigenar: registrar a curva até o critério de equilíbrio/prazo. O biótico usa equilíbrio respiratório e considera consumo no balanço."),
                    B("Restaurar cultivo: confirmar ar, agitação medida, OD estável e retorno do controlador antes de revisar ou iniciar outra corrida biótica."),
                    B("Revisar: conferir qualidade científica separadamente do retorno físico, e aceitar ou rejeitar."),
                    N("Se a leitura de oxigênio, a vazão ou a comunicação não forem confiáveis, a corrida é interrompida para evitar um resultado enganoso."),
                ]),
                new DocumentationSection("Revisão e aceite", [
                    P("A análise transforma a curva medida em um número. Confira se a região escolhida representa a parte regular da recuperação."),
                    F("Região Linear (s)", "Trecho usado para calcular a inclinação da curva transformada. Evite o início muito transitório e o final já próximo da saturação."),
                    F("Equilíbrio", "Ceq é estimado pela recuperação ou informado com origem. Não é fixado automaticamente em 100%."),
                    F("Ceq (%)", "Permite informar manualmente o equilíbrio quando ele for conhecido."),
                    F("Recalcular", "Refaz a análise usando as regiões e o equilíbrio exibidos."),
                    F("kLa Estimado · IC 95%", "Mostra o resultado e uma faixa provável para o valor."),
                    F("R² · RMSE · Sensib.", "Mostra o quanto a curva segue o ajuste, o tamanho do erro e a dependência do resultado em relação ao equilíbrio escolhido."),
                    F("Aceitar Corrida", "Confirma a análise e inclui a réplica entre os resultados utilizáveis."),
                    F("Rejeitar", "Marca a corrida como inconclusiva e pede um motivo. Os dados permanecem disponíveis para consulta."),
                    F("Repetir", "Cria uma nova tentativa da mesma condição, mantendo a anterior para comparação."),
                    F("OUR", "Mostra consumo em pontos percentuais por hora. Concentração exige Cref e origem; transferência residual ou consumo variável condicionam/invalidam a interpretação."),
                ]),
                new DocumentationSection("Limiares de operação", [
                    F("OD final da remoção", "Alvo editável, com faixa inicial de 5–20%. Faixa usual de cultivo e alvo do ensaio são parâmetros distintos."),
                    F("Rotação da remoção", "Padrão 100 rpm, editável; independente da rotação N da reoxigenação."),
                    F("Limites da fila", "Limita tentativas, corridas, exposição acumulada e intervalo. Não representa limites biológicos universais."),
                    F("Contexto das medições", "Registra meio, cultivo, janela e origem física/simulada. O contexto é congelado em cada corrida."),
                    F("Engrenagem", "Abre tempos máximos, critérios de estabilidade e opções de análise avançada."),
                ]),
                new DocumentationSection("Os três gráficos", [
                    F("Oxigênio Dissolvido", "Mostra a leitura ao longo do tempo, os limites e a curva ajustada."),
                    F("Regressão Log-Linear", "Mostra a transformação usada para verificar se o trecho escolhido se comporta como uma reta."),
                    F("kLa Instantâneo Diagnóstico", "Mostra o resultado calculado ao longo da curva. Variações fortes sugerem que a região precisa ser revista."),
                ]),
                new DocumentationSection("Concluir, importar e interromper", [
                    F("Importar Teste", "Abre um ensaio já realizado para revisar as curvas sem iniciar o equipamento."),
                    F("Concluir Teste", "Encerra o ensaio depois que todas as decisões necessárias foram tomadas."),
                    F("Abortar", "Interrompe a aquisição e solicita a finalização/restauração do protocolo. No biótico o retorno mantém o fluxômetro e repõe ar no reator; falha de retorno bloqueia a fila."),
                    F("Enviar ao mapa", "Ação opcional em Mapeamento kLa. Usa aceitos compatíveis; um ponto não identifica uma superfície. Salvar alterações de mapa publicado cria um novo rascunho."),
                    N("Concluir significa terminar normalmente. Abortar significa parar antes do término porque continuar não é desejável ou seguro."),
                ]),
            ]),

        new DocumentationTopic(
            KlaMappingTopicId,
            "Mapeamento kLa",
            "Como transformar resultados de kLa em uma superfície e em uma trajetória de operação.",
            [
                new DocumentationSection("O que a página faz", [
                    P("O mapeamento reúne vários resultados de kLa em uma superfície que representa o comportamento do processo entre os pontos medidos. Sobre essa superfície, pode calcular uma trajetória de aumento de kLa que evita chegar perto dos limites de rotação e vazão."),
                    P("A página não controla o equipamento. Ela prepara um mapa para ser consultado posteriormente por um controle configurado para usar uma trajetória."),
                ]),
                new DocumentationSection("Como a página é organizada", [
                    F("Dados", "Identificação do experimento, limites e tabela de pontos."),
                    F("Superfície", "Mapa colorido, direção de crescimento e trajetória calculada."),
                    F("Diagnóstico", "Qualidade do ajuste, cobertura e avisos que ajudam a julgar o resultado."),
                    F("Rodapé", "Salva, estima a superfície, calcula a trajetória, cancela ou publica o resultado."),
                    B("Em uma janela estreita, essas áreas aparecem como abas para manter os controles legíveis."),
                ]),
                new DocumentationSection("Criar o experimento", [
                    F("Novo experimento · Criar", "Começa um mapa separado para uma campanha ou condição de processo."),
                    F("Duplicar · Excluir · Importar…", "Reutiliza uma base, remove um mapa ou abre um mapa de outra localização."),
                    F("Nome · Caldo / meio · Ensaio / lote · Notas", "Registra o contexto necessário para entender e citar o mapa depois."),
                    F("Qg mín. / máx. · N mín. / máx.", "Define a região de vazão e rotação onde o mapa pode ser usado."),
                ]),
                new DocumentationSection("Inserir e conferir pontos", [
                    F("Tabela N · Qg · kLa", "Cada linha contém uma condição medida e seu kLa."),
                    F("+ Ponto", "Adiciona uma linha para digitação manual."),
                    F("Desenho 3²", "Cria nove combinações de rotação e vazão para organizar um planejamento 3×3. Os resultados ainda precisam ser medidos."),
                    F("Importar de Teste…", "Traz os resultados aceitos da página Determinar kLa."),
                    F("Ordenar", "Organiza os pontos para facilitar a conferência."),
                    N("O mapa é confiável dentro da região coberta pelos pontos. Fora dela, o aplicativo sinaliza que não há dados suficientes para afirmar o comportamento."),
                ]),
                new DocumentationSection("Superfície, gradiente e trajetória", [
                    F("Estimar superfície", "Constrói a superfície a partir dos pontos preenchidos."),
                    F("Calcular trajetória", "Calcula um caminho que aumenta o kLa e mantém uma margem dos limites."),
                    F("Mapa de superfície", "As cores representam kLa, as setas indicam a direção de maior crescimento e a linha mostra a trajetória."),
                    F("Classificação por folga média", "Compara possíveis pontos de partida e mostra quais permanecem mais afastados dos limites."),
                    F("Referência · Prévia rápida", "Restaura uma configuração de referência ou calcula uma prévia para avaliar uma mudança."),
                ]),
                new DocumentationSection("Diagnóstico científico", [
                    P("Use o diagnóstico para verificar se o mapa representa os pontos medidos antes de publicá-lo."),
                    F("Identidade do algoritmo", "Mostra o método de construção do mapa e os parâmetros usados. É útil para comparar duas análises, mas não é necessário para operar a página."),
                    F("Faixa da superfície", "Mostra os menores e maiores kLa calculados no mapa."),
                    F("Resíduos nos pontos medidos", "Indica a diferença entre cada ponto medido e o valor representado pela superfície. Resíduos grandes pedem revisão."),
                    F("Cobertura do domínio", "Indica quanto da região definida está apoiado por pontos medidos."),
                    F("Condição inicial selecionada", "Mostra de onde a trajetória começa."),
                    F("Folga média normalizada", "Compara a margem da trajetória escolhida com as melhores alternativas."),
                    F("Relação de alocação", "Resume a faixa de kLa percorrida e o tamanho da trajetória."),
                    F("Superfície · Trajetória", "Identifica a versão do mapa e da trajetória atualmente exibidas."),
                    F("Recusas e avisos", "Explica por que um cálculo não pôde ser feito ou por que uma região não deve ser usada."),
                ]),
                new DocumentationSection("Publicar e usar", [
                    F("Publicar para controle", "Disponibiliza a trajetória para um controle de oxigênio configurado para usar o mapa."),
                    F("Última publicação", "Mostra quando o mapa em uso foi publicado."),
                    F("Arquivo no disco · Copiar caminho · Abrir pasta · Exportar…", "Permite guardar uma cópia e compartilhar o experimento com outra pessoa."),
                    B("Publicar não inicia o equipamento nem altera uma receita existente automaticamente."),
                ]),
            ]),

        new DocumentationTopic(
            PowerTopicId,
            "Potência",
            "Como medir a potência do impelidor, acompanhar cada corrida e revisar os pontos capturados.",
            [
                new DocumentationSection("O que a página mede", [
                    P("O ensaio de potência mede o esforço necessário para girar o impelidor em cada combinação de rotação e vazão. A partir do torque e da rotação, o aplicativo calcula a potência entregue ao fluido e indicadores que permitem comparar condições."),
                    Eq("P = τ · ω = τ · 2πN/60", "A potência de eixo é calculada pelo torque medido e pela velocidade de rotação."),
                    Eq("P_líq = P − P₀(N)", "A potência líquida é a potência de eixo menos a potência consumida pela montagem sem fluido, obtida na tara."),
                    Eq("Np = P_líq / (ρ · n³ · D⁵)", "Np normaliza a potência pela densidade, rotação e diâmetro do impelidor."),
                    Eq("Re = ρ · n · D² / μ", "Re indica o regime de escoamento do impelidor."),
                    N("Sem tara, o ensaio fica em modo relativo: ainda permite comparar corridas, mas não representa uma potência líquida absoluta."),
                ]),
                new DocumentationSection("Com gás", [
                    P("Em uma condição gaseificada, a vazão é estabilizada antes de ser encaminhada ao reator. A configuração das válvulas e o caminho do gás podem ser conferidos em Documentação › Gás e válvulas."),
                    F("Pré-estabilização por C", "Define quanto a vazão deve se estabilizar antes da medição."),
                    F("Malha de gás", "Informa se o gás está fechado, em preparação, no reator ou em uma rota sem destino."),
                    Eq("Fr = n² · D / g", "Fr compara os efeitos da rotação com a gravidade."),
                    Eq("P_G/P₀ = potência com gás ÷ potência sem gás", "Indica a queda de potência causada pela aeração na mesma rotação."),
                ]),
                new DocumentationSection("Como a página é organizada", [
                    F("Cabeçalho", "Mostra o estado, se a tara é relativa ou absoluta e os comandos da sequência."),
                    F("Aba Montagem", "Reúne fluido, vaso, impelidores, geometria e tara."),
                    F("Aba Aquisição", "Define faixas, condições e critérios de captura."),
                    F("Aba Validação", "Reúne tara, ponto único, correlação elétrica e vínculo com mapa de kLa."),
                    F("Área de resultados", "Mostra leituras, progresso, gráficos e pontos capturados."),
                ]),
                new DocumentationSection("Por que a captura é adaptativa", [
                    P("O aplicativo não usa apenas um tempo fixo. Primeiro espera a rotação e o torque se estabilizarem; depois acumula amostras até a incerteza ficar pequena o suficiente."),
                    B("Estacionariedade: a leitura precisa parar de variar sistematicamente."),
                    B("Precisão: o intervalo de confiança de 95 % precisa atingir o critério definido."),
                    N("Por isso duas condições podem levar tempos diferentes. O objetivo é obter uma medida confiável, não fazer todas as corridas durarem exatamente o mesmo tempo."),
                    F("Precisão adaptativa", "Compara a incerteza atual com o alvo de captura."),
                    F("Pontos capturados", "Lista uma linha para cada corrida, com status, rotação, torque, potência, Np, Re, IC95 (intervalo de confiança de 95 %) e, quando aplicável, os valores relacionados ao gás."),
                ]),
                new DocumentationSection("O que fazer com uma corrida", [
                    F("Iniciar / continuar", "Começa a sequência ou retoma a partir do ponto em que ela foi pausada."),
                    F("Pausar", "Interrompe temporariamente o avanço da sequência, mantendo o processo na condição atual."),
                    F("Pular", "Abandona a espera do ponto atual e passa ao próximo. Use quando a condição já foi verificada por outro meio."),
                    F("Parar e revisar", "Interrompe a sequência de aquisição e leva os resultados capturados para revisão."),
                    F("Concluir ensaio", "Marca o ensaio como terminado normalmente depois que os pontos foram revisados."),
                    F("Interromper", "Finaliza o ensaio antes da conclusão normal. Use quando continuar não for desejável ou seguro."),
                    N("Parar e revisar encerra a sequência de captura, mas deixa o ensaio aberto para análise. Concluir ensaio encerra o trabalho normalmente; Interromper registra que ele terminou antes do esperado."),
                ]),
            ]),

        new DocumentationTopic(
            PowerMapTopicId,
            "Mapa de Potência",
            "Como transformar corridas de potência em uma superfície, comparar impelidores e estudar eficiência.",
            [
                new DocumentationSection("O que a página faz", [
                    P("O mapa de potência organiza os pontos medidos em uma superfície sobre rotação e vazão. Ele permite visualizar regiões de operação e comparar o custo energético de diferentes condições."),
                    Eq("kLa = K · (P/V)^α · (v_s)^β", "A correlação de van't Riet relaciona transferência de oxigênio, potência volumétrica e velocidade superficial do gás."),
                    Eq("η = kLa / (P/V)", "A eficiência indica quanto de transferência de oxigênio é obtido por unidade de potência volumétrica."),
                ]),
                new DocumentationSection("As três abas", [
                    F("Mapeamento de Potência", "Cria o mapa, reconstrói a superfície e exibe pontos e regiões de operação."),
                    F("Modelos e Ajustes", "Vincula o mapa de kLa e ajusta a correlação de van't Riet aos dados disponíveis."),
                    F("Comparação e escalonamento", "Compara impelidores e estima a potência volumétrica necessária em outra escala."),
                ]),
                new DocumentationSection("Escalonamento", [
                    P("A calculadora responde quanta potência por volume é necessária para alcançar um kLa alvo."),
                    Eq("P/V = [ kLa_alvo / (K · v_s^β) ]^(1/α)", "É a forma inversa da correlação de van't Riet."),
                    N("P/V não define sozinho uma rotação e uma vazão únicas. Informe uma regra de operação ou use uma trajetória para transformar o resultado em uma condição possível."),
                ]),
                new DocumentationSection("Como interpretar o mapa", [
                    B("Use a superfície dentro da região realmente coberta pelos pontos medidos."),
                    B("Áreas sem dados suficientes aparecem como indefinidas, em vez de receber uma extrapolação silenciosa."),
                    B("A fronteira de flooding representa a região em que o comportamento do gás muda. Compare-a com os pontos observados e não a trate como uma medida absoluta."),
                    F("Exportar P/V para o mapa kLa", "Leva a potência volumétrica calculada para a análise de kLa e eficiência."),
                ]),
            ]),

        new DocumentationTopic(
            CalibrationTopicId,
            "Calibrações",
            "Como conferir sensores, coletar referências e aplicar uma nova relação entre leitura e valor real.",
            [
                new DocumentationSection("Como a página é organizada", [
                    P("Cada aba trata uma grandeza: pH, oxigênio, vazão de ar, sensor de biomassa e bomba externa. A lógica geral é sempre a mesma: observar a leitura, coletar referências conhecidas, revisar a curva e aplicar somente quando o resultado fizer sentido."),
                    F("Leitura ao vivo", "Mostra a leitura atual, a leitura bruta quando ela é útil para diagnóstico e a estabilidade recente."),
                    F("Curva vigente", "Mostra a calibração que está sendo usada antes de aplicar uma nova."),
                ]),
                new DocumentationSection("Calibrar um sensor", [
                    F("Dois pontos", "Usa duas referências conhecidas para ajustar a inclinação e o deslocamento da leitura."),
                    F("Um ponto (só intercepto)", "Corrige o deslocamento mantendo a inclinação já conhecida. É adequado para uma conferência rápida, não para substituir uma calibração completa."),
                    F("Referência", "É o valor conhecido do padrão ou solução usada na calibração."),
                    F("Critério de aquisição", "Define quantas leituras serão observadas e quanta estabilidade é necessária para aceitar o ponto."),
                    F("Etapas de execução", "Preparar → Ponto 1 → Ponto 2 → Salvar. A etapa atual fica destacada e o botão principal mostra a próxima ação. Após o primeiro ponto, troque o tampão ou padrão e confirme que o segundo está pronto; nenhuma leitura é coletada para o ponto 2 antes dessa confirmação."),
                    F("Aplicar no app · Salvar e usar curva", "Após revisar, salva os coeficientes em Configuracoes/settings.json da pasta de trabalho selecionada e passa a usar a nova curva. Ao reabrir essa pasta, o aplicativo carrega esses coeficientes automaticamente."),
                    N("Depois de aplicar, confira a leitura com uma referência independente. Se o resultado não fizer sentido, reverta ou repita a calibração antes de usar o sensor em um ensaio."),
                ]),
                new DocumentationSection("Calibração da vazão de ar", [
                    F("Vazão de ar", "Relaciona a leitura elétrica do fluxômetro com a vazão real medida na bancada."),
                    F("Transição", "Separa as duas regiões da curva quando uma única relação não descreve bem toda a faixa de vazão."),
                    F("Pontos da tabela", "Cada ponto combina a leitura do instrumento com a vazão de referência medida pelo operador."),
                    F("Salvar pontos", "Guarda os pontos coletados para continuar a revisão depois."),
                    F("Salvar e enviar curva", "Aplica a curva revisada à medição de vazão depois da confirmação do operador."),
                ]),
                new DocumentationSection("Calibração da bomba externa", [
                    P("A bomba é calibrada medindo quanto volume ela entrega durante um intervalo conhecido. O aplicativo transforma volume e tempo em vazão e usa os pontos da tabela para reconstruir a curva."),
                    F("Novo perfil", "Cria um perfil vazio para uma mangueira ou condição de uso específica."),
                    F("Adicionar ponto", "Inclui uma linha para informar rotação, tempo e volume medido."),
                    F("Tabela de pontos", "Edite os valores diretamente. A vazão calculada é volume dividido pelo tempo."),
                    F("Gráfico", "É reconstruído automaticamente com os valores atuais da tabela, permitindo verificar a curva antes de salvar."),
                    F("Salvar Preferências", "Guarda as preferências de calibração no computador para reutilização."),
                    F("Salvar perfil", "Arquiva a curva identificada para que ela possa ser selecionada posteriormente."),
                    F("Controle manual · preencher mangueira", "Aciona a bomba para preencher a linha e retirar bolhas. Essa ação não cria um ponto de calibração."),
                    N("Use volume medido por uma proveta, béquer graduado ou balança com conversão conhecida. Anote o tempo real do intervalo e repita pontos quando houver dúvida."),
                ]),
                new DocumentationSection("Onde a calibração aparece", [
                    B("A página Controle oferece o atalho para a calibração de cada variável."),
                    B("Os ensaios usam a calibração vigente no momento da aquisição e registram os valores observados junto dos resultados."),
                    B("A tara de potência é uma referência separada: ela mede o consumo da montagem sem fluido e não substitui a calibração de um sensor."),
                ]),
            ]),

        new DocumentationTopic(
            HistoryTopicId,
            "Históricos",
            "Como localizar sessões anteriores, conferir seu conteúdo e exportar os dados.",
            [
                new DocumentationSection("O que a página mostra", [
                    P("Históricos apresenta as sessões registradas pelo aplicativo com nome, data, duração, quantidade de linhas, tamanho e conexão utilizada."),
                    F("Pesquisar sessões", "Filtra a lista pelo nome."),
                    F("Atualizar", "Procura novamente por sessões disponíveis."),
                    F("Abrir pasta", "Abre o local onde as sessões estão guardadas."),
                ]),
                new DocumentationSection("Conferir uma sessão", [
                    P("Ao selecionar uma sessão, confira o começo e o fim do registro antes de interpretar uma curva. Isso ajuda a encontrar sessões incompletas ou encerradas por falta de energia."),
                    F("Primeira e última leitura", "Mostram como o registro começa e termina."),
                    F("Verificação do formato", "Indica se a sessão pode ser lida normalmente pelo aplicativo."),
                ]),
                new DocumentationSection("Usar os dados", [
                    F("Carregar no Gráficos", "Abre a sessão para navegar pelas tendências."),
                    F("Exportar CSV", "Cria uma cópia em formato de tabela para análise externa."),
                    N("Ensaios de kLa e potência possuem suas próprias telas de revisão, porque além da série de leituras também precisam guardar decisões e resultados."),
                ]),
            ]),

        new DocumentationTopic(
            EventsTopicId,
            "Eventos",
            "O registro cronológico de ações, avisos, alarmes e mudanças importantes no processo.",
            [
                new DocumentationSection("A lista de eventos", [
                    P("Cada linha registra o que aconteceu, quando aconteceu, sua origem, severidade e mensagem. Consulte esta página quando precisar entender por que uma etapa mudou ou foi interrompida."),
                    F("Pesquisar eventos", "Filtra a lista pelo texto digitado."),
                    F("Seguir novas entradas", "Mantém a lista acompanhando os eventos mais recentes."),
                    F("Pausar", "Congela a visualização sem interromper o registro."),
                    F("Copiar · Exportar", "Copia a seleção ou cria uma cópia dos eventos visíveis."),
                    F("Limpar visualização", "Limpa a tela, mas não apaga o histórico registrado."),
                ]),
                new DocumentationSection("Dados brutos", [
                    P("O painel inferior mostra a informação recebida antes de ser transformada em cartões e gráficos. Ele é útil quando uma leitura parece incompleta ou diferente do esperado."),
                ]),
                new DocumentationSection("Gravação da sessão", [
                    F("Iniciar / parar", "Liga ou desliga o registro contínuo. O registro normalmente começa com o aplicativo."),
                    F("Novo arquivo", "Começa uma nova sessão de registro."),
                    F("Abrir pasta", "Abre o local dos registros."),
                    B("Use eventos para anotar trocas de condição, intervenções, calibrações e qualquer ocorrência relevante."),
                ]),
            ]),

        new DocumentationTopic(
            IntegrationTopicId,
            "Integração: kLa, potência e controle de O₂",
            "Como usar os resultados de oxigenação e potência juntos para estudar eficiência.",
            [
                new DocumentationSection("A cadeia de trabalho", [
                    P("A sequência recomendada é: Determinar kLa mede a transferência de oxigênio; Mapeamento kLa organiza esses resultados; Potência mede o custo mecânico; Mapa de Potência cruza os dois; Controle de O₂ pode usar a trajetória publicada em Publicar para controle."),
                    P("Cada etapa é revisada antes de ser usada na seguinte. Isso mantém claro quais pontos foram medidos, aceitos, interpolados ou apenas estimados."),
                ]),
                new DocumentationSection("1 · Medir kLa", [
                    P("Crie as condições e réplicas na página Determinar kLa. Aceite somente curvas estáveis e coerentes com o procedimento."),
                    B("Resultado: kLa e sua incerteza para cada condição aceita."),
                ]),
                new DocumentationSection("2 · Construir a superfície", [
                    P("Use Importar de Teste para trazer os resultados aceitos ao Mapeamento kLa, estime a superfície e calcule uma trajetória quando precisar de uma sequência de operação."),
                    B("Resultado: superfície de kLa, diagnóstico e, se desejado, uma trajetória publicada."),
                ]),
                new DocumentationSection("3 · Medir potência", [
                    P("Repita ou selecione as mesmas condições na página Potência e faça a tara antes de comparar valores absolutos."),
                    B("Resultado: potência líquida, P/V e indicadores de regime para cada condição."),
                ]),
                new DocumentationSection("4 · Cruzar os mapas", [
                    P("No Mapa de Potência, vincule o mapa de kLa e analise somente a região em que as duas superfícies possuem cobertura. A correlação ajustada descreve a relação entre transferência de oxigênio e potência."),
                    Eq("η = kLa / (P/V)", "A eficiência compara a transferência de oxigênio com a potência volumétrica necessária."),
                    N("A intersecção evita comparar um valor medido de um lado com uma extrapolação sem suporte do outro."),
                ]),
                new DocumentationSection("5 · Usar no controle", [
                    P("Quando a trajetória estiver revisada e publicada, o método Mapa (trajetória kLa) do Controle de O₂ pode caminhar por ela enquanto ajusta o oxigênio."),
                    B("A trajetória fornece combinações de rotação e vazão que já foram avaliadas no mapa."),
                    B("Publicar um mapa não inicia o processo nem altera uma receita existente automaticamente."),
                ]),
            ]),

        new DocumentationTopic(
            PowerTareTopicId,
            "Potência · Tara do eixo",
            "Como medir o consumo da montagem sem fluido para obter potência líquida.",
            [
                new DocumentationSection("O que a tara mede", [
                    P("A tara é realizada sem líquido para medir o esforço consumido pelo eixo, selos, mancais, acoplamento e demais partes da montagem. Esse consumo é descontado do ensaio com fluido."),
                    P("A diferença entre a potência com fluido e a potência da tara é a potência líquida usada no cálculo de Np."),
                    N("Sem uma tara adequada, use os resultados apenas para comparação relativa entre condições equivalentes."),
                ]),
                new DocumentationSection("Como executar", [
                    F("Medir nova tara", "Abre o assistente para escolher rotação inicial, final e passo."),
                    F("Iniciar ensaio no ar", "Percorre as rotações, espera a estabilização e coleta leituras suficientes em cada patamar."),
                    F("Tabela de patamares", "Mostra rotação média, consumo de vazio, incerteza, ruído e número de leituras."),
                    F("Perfil existente · Usar no ensaio", "Aplica uma tara já conferida ao ensaio atual."),
                    F("Criar nova tara · Salvar nova tara", "Arquiva a curva medida para reutilização."),
                ]),
                new DocumentationSection("Cuidados", [
                    B("A tara pertence à montagem. Trocar impelidor, selo, acoplamento ou eixo pode exigir uma nova medição."),
                    B("Use a tara do mesmo eixo e conjunto que será usado no ensaio."),
                    B("Se um patamar não estabilizar, corrija a causa antes de aceitar a curva."),
                    B("Uma tara interrompida pode ser consultada, mas não deve ser tratada como completa sem revisão."),
                ]),
            ]),

        new DocumentationTopic(
            PowerSinglePointTopicId,
            "Potência · Ponto único",
            "Como conferir uma condição isolada antes ou durante um ensaio.",
            [
                new DocumentationSection("Para que serve", [
                    B("Verificar se o eixo responde ao comando."),
                    B("Observar vibração, vazamento e formação de vórtice antes da sequência."),
                    B("Conferir torque e potência em uma condição avulsa."),
                    B("Adicionar uma condição conferida à matriz do ensaio."),
                ]),
                new DocumentationSection("Os controles", [
                    F("N (rpm) · Qg (L/min)", "Define rotação e vazão da condição a conferir."),
                    F("Comandar", "Leva o equipamento à condição e começa a mostrar as leituras."),
                    F("Parar eixo", "Encerra a conferência e libera o acionamento."),
                    F("Adicionar à tabela de condições", "Inclui a condição na matriz para que ela possa ser ensaiada formalmente."),
                ]),
                new DocumentationSection("Interpretar a conferência", [
                    P("O ponto único é uma verificação, não substitui a captura estatística do ensaio. Quando quiser comparar potência absoluta, confira se a tara e a calibração corretas estão selecionadas."),
                ]),
            ]),

        new DocumentationTopic(
            PowerElectricalTopicId,
            "Potência · Correlação elétrica",
            "Como comparar a potência mecânica do eixo com a potência elétrica consumida.",
            [
                new DocumentationSection("O que é comparado", [
                    P("A potência mecânica é calculada a partir do torque e da rotação. A potência elétrica é medida por um wattímetro externo e informada pelo operador."),
                    P("A comparação ajuda a estimar as perdas do motor, acionamento e transmissão. Ela não substitui a potência líquida usada para descrever o fluido."),
                ]),
                new DocumentationSection("A tabela", [
                    F("N", "Rotação da leitura."),
                    F("P mec (W)", "Potência mecânica calculada pelo eixo."),
                    F("P elét (W)", "Potência elétrica lida no wattímetro."),
                    F("Instrumento", "Identificação do wattímetro usado na leitura."),
                    N("Registre as leituras elétricas na mesma condição e aguarde a estabilização antes de comparar."),
                ]),
            ]),

        new DocumentationTopic(
            PowerKlaMapTopicId,
            "Potência · Mapa de kLa e eficiência",
            "Como cruzar os mapas de potência e kLa para estudar eficiência.",
            [
                new DocumentationSection("O vínculo", [
                    P("Vincule um mapa de kLa ao ensaio de potência para consultar, na mesma condição de rotação e vazão, a transferência de oxigênio e a potência necessária."),
                    F("Lista de mapas · ↻", "Mostra os mapas disponíveis e atualiza a lista."),
                    F("Vincular mapa de kLa", "Seleciona o mapa que será usado na comparação."),
                ]),
                new DocumentationSection("A leitura de eficiência", [
                    B("A eficiência kLa/(P/V) é calculada somente na intersecção das regiões cobertas pelos dois mapas."),
                    B("A tabela mostra kLa, potência líquida, potência volumétrica e eficiência para cada condição comum."),
                    N("Se uma condição estiver fora da cobertura de um dos mapas, ela é marcada como indisponível. O aplicativo não inventa um valor para completar a comparação."),
                ]),
            ]),

        new DocumentationTopic(
            SettingsConnectionTopicId,
            "Configurações · Conexão",
            "Como configurar a ligação com o sistema e verificar a presença dos dispositivos externos.",
            [
                new DocumentationSection("Como a seção é organizada", [
                    P("A parte superior guarda preferências de conexão. A parte inferior mostra quais dispositivos externos foram encontrados e quando foram vistos pela última vez."),
                    P("O botão de conexão rápida da barra superior é usado para conectar ou desconectar imediatamente. Configurações guarda as preferências para as próximas aberturas."),
                ]),
                new DocumentationSection("Preferências de conexão", [
                    F("Conectar automaticamente ao iniciar", "Tenta usar a última forma de conexão ao abrir o aplicativo."),
                    F("Alternar de meio automaticamente ao perder o link", "Tenta a outra forma de conexão quando a primeira falha."),
                    F("Endereço Wi-Fi", "É o endereço do sistema na rede local. Confirme que o computador está na mesma rede."),
                    F("Período de telemetria (ms)", "Define a frequência com que novas leituras são recebidas."),
                ]),
                new DocumentationSection("Nós na rede do Hub", [
                    P("A tabela lista os dispositivos externos esperados. Um traço significa que ainda não há informação suficiente sobre aquele dispositivo."),
                    F("Dispositivo", "Nome usado no restante do aplicativo."),
                    F("IP", "Endereço atual do dispositivo na rede."),
                    F("MAC · Firmware", "Identificadores úteis para conferir se o dispositivo correto está conectado."),
                    F("Estado", "Indica se o dispositivo está online, offline ou aguardando a primeira comunicação."),
                    F("Visto há", "Tempo desde a última leitura recebida."),
                    F("RSSI · Memória · Tempo ligado", "Indicadores de saúde da comunicação e do tempo de funcionamento."),
                    F("Falhas de comunicação", "Quantidade de tentativas recentes que não tiveram resposta."),
                    F("Atualizar saúde", "Atualiza os indicadores dos dispositivos."),
                    F("Nós com endereço", "Mostra quantos dispositivos possuem endereço conhecido."),
                    N("Se o dispositivo não aparecer ou ficar offline, verifique alimentação, rede e a identificação da bancada antes de iniciar um ensaio."),
                ]),
                new DocumentationSection("Abrir o diagnóstico", [
                    F("Abrir diagnóstico", "Abre a página de diagnóstico do dispositivo para uma inspeção mais detalhada."),
                    F("Copiar IP", "Copia o endereço para uso em uma ferramenta de rede."),
                ]),
            ]),

        new DocumentationTopic(
            SettingsGasRigTopicId,
            "Gás e válvulas",
            "Como o ar e o nitrogênio são encaminhados nos ensaios e como conferir a posição das válvulas.",
            [
                new DocumentationSection("Entender o arranjo", [
                    P("Leia o fluxograma da esquerda para a direita: o fluxômetro mede e regula a vazão; depois um T divide o gás entre A, que leva ar ao reator, e C, que libera ar para descarga ou purga. B leva nitrogênio e abre junto com C. Assim, B e C compartilham a mesma entrada elétrica, mas têm funções diferentes na tubulação."),
                    P("No ensaio de potência, use somente ar e mantenha o nitrogênio fechado. No ensaio de kLa, o nitrogênio reduz o oxigênio no início; depois o ar é encaminhado ao reator para acompanhar a recuperação."),
                    F("Fluxograma", "A = ar para o reator; B = nitrogênio; C = descarga ou purga de ar; B + C = linhas que abrem juntas. Clique na imagem para ampliar e compare a legenda com as mangueiras instaladas."),
                ]),
                new DocumentationSection("Configurar a entrada", [
                    F("Válvula A ligada na entrada", "Escolhe qual entrada do fluxômetro aciona a passagem de ar para o reator. A outra entrada fica associada às válvulas B e C."),
                    F("Restaurar padrão", "Volta à associação recomendada para a montagem: A na entrada 2 e B/C na entrada 1."),
                    F("Resumo do arranjo", "Mostra de forma legível qual entrada aciona A e qual aciona B/C, para você comparar a configuração com a bancada."),
                    N("Faça alterações somente com receitas e ensaios parados. Depois de alterar, confira a rota exibida e faça uma verificação curta antes de uma campanha."),
                ]),
                new DocumentationSection("Como cada página usa o gás", [
                    B("Controle › Vazão de Ar: escolhe fechado ou uma entrada de gás e mostra a rota observada."),
                    B("Ensaio de kLa: usa nitrogênio para retirar oxigênio e depois ar para acompanhar a recuperação."),
                    B("Ensaio de potência: estabiliza o ar antes de encaminhá-lo ao reator."),
                    B("Receitas e controle de oxigênio: usam a entrada configurada para o ar do reator."),
                    B("Calibração de vazão: pode usar a descarga ou o reator conforme a montagem e o procedimento."),
                ]),
                new DocumentationSection("Conferência antes do ensaio", [
                    B("Compare a imagem com as mangueiras instaladas na bancada."),
                    B("Confirme que a fonte de nitrogênio está fechada quando o procedimento não a utilizar."),
                    B("Verifique se o resumo mostra a rota esperada antes de iniciar uma corrida."),
                    B("Se a conexão estiver indisponível, resolva a comunicação antes de operar as válvulas."),
                ]),
            ]),
    ];
}
