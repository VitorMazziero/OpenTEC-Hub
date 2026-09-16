# Plano de revisão da janela de teste de potência

**Data:** 2026-09-15  
**Estado:** planejamento aprovado para implementação futura; nenhuma alteração de código faz parte deste documento.  
**Escopo:** `Windows_app/src/OpenTECHub`, página **Potência**, incluindo as áreas
**Mapeamento de Potência**, **Modelos e Ajustes** e **Comparação de impelidores**.

## 1. Objetivo

Reorganizar a página de potência para que o fluxo operacional fique inequívoco, restaurar a
visualização adimensional de ensaios gaseificados sem reintroduzir o cálculo de flooding, fazer
os gráficos refletirem somente pontos aceitos e reduzir a densidade visual das comparações e dos
modelos.

O trabalho deve preservar:

- os dados brutos e os arquivos de ensaios existentes;
- a compatibilidade de leitura de documentos antigos que ainda contenham campos de flooding;
- o cálculo atual do Np medido da associação de impelidores;
- o comportamento seguro de parada, interrupção e tara;
- o princípio de que somente resultados aceitos alimentam gráficos, modelos e comparações.

## 2. Evidência revisada e diagnóstico do estado atual

Foram cruzadas as quatro capturas fornecidas com a implementação atual. Os principais pontos
confirmados são:

1. A tabela de resultados possui hoje uma coluna isolada de emoji no início, a coluna textual de
   status perto do fim, a ação depois de `Data/hora` e o horário antes da ação. Isso fragmenta uma
   única informação em duas regiões da tabela.
2. `Capturado` é hoje tanto uma fase interna do runner quanto uma opção do ciclo manual
   `Aceito → Capturado → Em revisão → Rejeitado → Aceito`. A fase interna é útil para transferir a
   corrida da captura automática à revisão; a opção manual não é um estado final útil ao operador.
3. O segundo gráfico da área de aquisição já teve alternância entre `Np × Re` e
   `P_G/P₀ × Fl_G`, com Fr no eixo secundário. A remoção posterior eliminou o gráfico junto com os
   elementos de flooding. A restauração pode reutilizar os valores experimentais que continuam no
   resultado (`P_G/P₀`, `Fl_G` e `Fr`) sem calcular correlação, fronteira ou ponto de flooding.
4. O gráfico `Np × Re` desenha hoje todas as linhas presentes em `Results`, inclusive rejeitadas.
   A troca manual de status reconstrói a coleção, mas a seleção do gráfico não filtra explicitamente
   `Accepted`.
5. O Np medido mostrado na tabela e no gráfico já usa `AssemblyPowerNumber`. Porém, a linha
   teórica usa `ReferenceLiteratureNp`, que atualmente escolhe apenas o Np de literatura do
   impelidor de maior diâmetro.
6. O aviso `Malha desatualizada — reconstrua` está no cabeçalho global. No tema escuro, o fundo e
   o texto usam tokens com a mesma cor âmbar, o que explica a perda de contraste.
7. O card de comparação usa uma coluna lateral para ações. O controle com o texto
   `Comparação de impelidores` visto na captura é, na implementação, o campo editável
   `ComparisonName`, não uma ação.
8. A superfície ainda oferece camada, linhas e cálculo de flooding/Nienow, apesar de o fluxo do
   ensaio já ter deixado de calcular flooding. Esses resíduos também aparecem na comparação e em
   contratos de teste antigos.
9. Os gráficos da intersecção podem receber dezenas de milhares de células da malha. O gráfico
   `kLa × P/V` cria uma série e uma legenda para cada Qg arredondado, causando a sobreposição vista
   na captura.
10. O card de tara mede e já grava a curva no ensaio; `Salvar nova tara` apenas arquiva depois a
    mesma curva em uma biblioteca nomeada. A separação em duas ações e o campo de nome permanente
    tornam o fluxo ambíguo.

### 2.1 Limites desta análise

- Os arquivos de dados sob `OpenTEC-Hub/Mapas-Potencia` e
  `OpenTEC-Hub/Testes-Potencia/IsojetB-Combijet` já têm alterações locais do operador. A futura
  implementação não deve sobrescrever, restaurar nem incluir esses arquivos em commits de código.
- As capturas demonstram o estado visual em uma configuração específica. O aceite futuro também
  precisa cobrir temas claro/escuro e os tamanhos suportados pelo app.
- Este plano não autoriza migração destrutiva de dados antigos. Campos legados de flooding podem
  permanecer no esquema para leitura, mas não devem ser recalculados nem expostos na interface
  nova.

## 3. Contratos funcionais propostos

### 3.1 Tabela de pontos capturados: status, ordem e tradução (itens 1, 2, 3 e 7)

Substituir a coluna inicial de emoji e a coluna textual separada por uma única coluna de status,
com ícone vetorial e texto em português.

Ordem final das últimas colunas:

1. métricas e motivo de parada;
2. `Tent.`;
3. `Status`;
4. `Mudar status`;
5. `Data/hora` — sempre a última coluna.

Regras:

- Usar `StateDot`, `StateChip` ou geometria vetorial equivalente, centralizada e dimensionada por
  DIP. Não usar emoji dependente da fonte do sistema.
- O ícone nunca deve ser a única fonte de significado: exibir texto e fornecer tooltip/nome
  acessível.
- Criar um mapeamento único de apresentação para fases e motivos de parada. Não deixar
  `enum.ToString()` chegar à interface. Incluir pelo menos `Aceito`, `Em revisão`, `Rejeitado`,
  `Interrompido`, `Pendente`, `Em curso`, `Concluído` e os motivos de parada existentes.
- Remover `Capturado` do ciclo manual. Fora de uma revisão ativa, `Mudar status` alternará somente
  entre `Aceito` e `Rejeitado`.
- Manter `PowerRunPhase.Captured` no domínio por compatibilidade e como transição interna do runner.
  Uma linha legada nessa fase será apresentada como `Em revisão`, nunca como `Capturado`.
- Não transformar uma corrida sem captura em aceita. O contrato D-050 continua prevalecendo.

### 3.2 Ações do ensaio no cabeçalho (item 5)

Mover `Concluir ensaio` e `Interromper` do rodapé do card de resultados para a barra superior de
ações de **Mapeamento de Potência**, imediatamente após `Parar e revisar`.

Ordem proposta:

`Iniciar/continuar` → `Pausar/retomar` → `Pular` → `Parar e revisar` → `Concluir ensaio` →
`Interromper`.

Preservar os comandos e confirmações atuais. O botão de interrupção mantém estilo de perigo e a
confirmação de estacionamento seguro. Remover as instâncias antigas do rodapé para não haver duas
fontes para a mesma ação.

### 3.3 Segundo gráfico alternável e dados aceitos (itens 4 e 6)

Restaurar dois seletores dentro do cabeçalho do segundo card de gráfico:

- `Np × Re`;
- `P_G/P₀ × Fl_G / Fr`.

O segundo modo deve exibir exclusivamente dados experimentais já calculados:

- eixo X principal: `Fl_G`;
- eixo Y esquerdo: `P_G/P₀`, com IC95 quando disponível;
- eixo Y direito: `Fr`, com distinção visual simples;
- nenhuma linha de Nienow, ponto de flooding, anotação de transição ou chamada a
  `DetectFlooding`/`ComputeFloodingBoundary`.

Embora o rótulo curto possa ser adaptado ao espaço disponível, o tooltip deve explicar os três
grupos mostrados. A escolha do gráfico deve permanecer enquanto a página estiver aberta e não pode
limpar os dados.

Ambos os modos devem usar apenas corridas com `Phase == Accepted` e valores finitos/positivos
apropriados ao eixo. A mesma regra vale para comparação de impelidores, reconstrução de superfície
e ajuste de modelos.

Atualização:

- aceitar, rejeitar, repetir ou alterar manualmente o status marca o gráfico como sujo;
- o próximo ciclo do `VisibleRedrawTimer` redesenha somente o gráfico visível;
- ao rejeitar um ponto, ele desaparece sem reiniciar a página;
- ao devolver o ponto a `Aceito`, ele reaparece;
- pontos em revisão, capturados, rejeitados, interrompidos ou sem captura não são desenhados.

### 3.4 Np teórico da associação de impelidores (item 8)

Substituir `ReferenceLiteratureNp` por um valor teórico agregado coerente com a definição de
`AssemblyPowerNumber`.

Para impelidores no mesmo eixo, com a mesma rotação, usar:

```text
Dref = maior diâmetro da associação
Np_teórico,associação = Σ [Np_teórico,i × (Di / Dref)^5]
```

Consequências:

- para impelidores de mesmo diâmetro, o Np teórico da associação é a soma dos Np individuais;
- para diâmetros diferentes, a ponderação em `D^5` mantém a equivalência de potência
  `P = ρ N³ Σ(Np_i D_i^5)`;
- para um impelidor, o resultado permanece igual ao Np de literatura atual;
- se qualquer estágio não tiver Np teórico válido, não desenhar uma linha parcial como se fosse a
  associação completa. Exibir ausência da referência e indicar qual estágio precisa ser definido.

O cálculo deve residir em função de domínio testável, não no code-behind do gráfico. O rótulo ou
tooltip da linha deve dizer `Np teórico da associação`.

### 3.5 Aviso de malha (item 9)

Mover o aviso para a barra lateral de **Modelos e Ajustes**, imediatamente abaixo do seletor de
abas `Mapa | Modelo | Exibição`. Remover as cópias do cabeçalho global e do cabeçalho opcional da
view para evitar avisos duplicados.

Apresentação proposta:

- faixa de largura total da barra lateral;
- fundo neutro/sunken, borda âmbar e texto com `TextPrimaryBrush` no tema escuro;
- ícone vetorial de aviso e texto `Malha desatualizada — reconstrua`;
- contraste mínimo WCAG AA para texto normal em ambos os temas;
- visível apenas quando há superfície existente e `IsSurfaceStale == true`.

Não alterar globalmente `StateWarningTextBrush` apenas para corrigir este card: o token é usado em
outros estados. Criar composição/estilo local de aviso e validar os dois temas.

### 3.6 Card de comparação de impelidores (itens 10 e 11)

Reorganizar a área superior para que os controles fiquem em uma faixa horizontal:

`Recarregar ensaios` → `Comparar` → `Nome da comparação` → `Salvar comparação` → `Exportar CSV`.

O texto `Comparação de impelidores` é o valor inicial do campo de nome, não um botão. Tornar a
natureza do campo explícita com rótulo/tooltip e aparência de entrada. Em largura suportada ampla,
todos os controles ficam em uma linha; no perfil compacto, a faixa pode quebrar de forma
controlada sem criar uma coluna lateral permanente.

Remover da interface e do CSV novo:

- `Efic. rel. dispersão`;
- colunas e marcadores derivados de flooding/Nienow que não pertencem mais ao fluxo ativo.

Para documentos antigos, ignorar os valores legados sem falhar a abertura.

Remover legendas de dentro dos três gráficos comparativos. Criar uma única chave de séries acima
da linha de gráficos, fora da área plotável, com uma cor por ensaio e nome curto; o tooltip pode
mostrar montagem completa. Isso preserva a identificação sem cobrir os dados. Se houver mais
ensaios do que cabem, usar rolagem horizontal local ou quebra controlada.

### 3.7 Densidade dos gráficos de modelos (item 11)

Aplicar redução apenas à apresentação. Ajustes, métricas, intersecção e exportação continuam usando
todos os pontos válidos.

Definir uma função determinística e testável de seleção com limite de **500 pontos visíveis por
gráfico**:

- nunca alterar o conjunto científico armazenado;
- preservar mínimos, máximos, bordas do domínio e pontos fora da faixa de paridade de ±15%;
- preencher o restante por amostragem estratificada em bins dos eixos, não pelos primeiros 500;
- produzir a mesma seleção para a mesma entrada, independentemente da ordem da coleção.

Aplicação:

- **Paridade do ajuste:** separar dentro/fora da faixa, preservar extremos e representar a extensão
  de kLa medido e previsto. As linhas 1:1 e ±15% permanecem.
- **kLa × P/V na intersecção:** quando houver poucos pares experimentais, mostrar todos. Quando a
  fonte for a superfície, selecionar até 500 células representativas e colorir por Qg com uma
  escala compacta, em vez de criar uma série/legenda para cada valor arredondado de Qg.

Não exibir legendas volumosas dentro dos gráficos de modelos. Preferir título, eixos e uma barra de
cor compacta para Qg fora da área dos pontos.

### 3.8 Ponto único na aba Aquisição (item 12)

Mover o card completo `Ponto único` da aba **Validação** para a aba **Aquisição**, logo abaixo do
card/tabela `Condições do ensaio`.

Preservar comandos, validações, gravação bruta e botão `Adicionar à tabela de condições`. Atualizar
o texto das abas e a documentação para que **Validação** deixe de prometer conferências manuais que
não contém mais, e **Aquisição** passe a descrever o ponto avulso.

### 3.9 Integração de mapa kLa em Modelos e Ajustes (item 13)

Excluir o card `Mapa de kLa e eficiência` da aba **Validação**. A informação deve pertencer a
**Modelos e Ajustes** e não ao ViewModel do ensaio em aquisição.

Na aba lateral **Mapa**, mesclar no card `Ensaios de origem`, abaixo da lista de ensaios:

- o texto resumido do domínio/intersecção;
- o texto de eficiência média, quando calculado;
- a tabela read-only com `N`, `Qg`, `kLa`, `P líq`, `P/V`, eficiência e região.

Somente texto e tabela entram nesse card. Não duplicar seletor nem botão de vínculo: a seleção do
mapa kLa e o comando `Calcular intersecção` continuam na aba **Modelo**, que é a fonte única dessa
operação.

Transferir a projeção da tabela para `PowerMapViewModel`, derivada da intersecção atual. Remover do
`PowerTestViewModel` as propriedades e comandos que ficarem sem consumidor somente depois de os
testes demonstrarem que nenhuma outra tela os usa. A invalidação da superfície ou a troca do mapa
kLa deve limpar/atualizar também o resumo e a tabela.

### 3.10 Fluxo único de tara nomeada (item 14)

Manter um único caminho para criar uma tara:

1. O operador clica `Medir nova tara`.
2. Antes de abrir/iniciar a varredura, o app solicita o nome do perfil em uma janela modal.
3. O nome é validado com as mesmas regras de arquivo/perfil existentes.
4. Se já existir, pedir confirmação explícita para substituir; cancelar mantém tudo intacto.
5. Após nome válido, abrir o assistente e permitir `Iniciar ensaio no ar`.
6. Ao concluir com sucesso, gravar a tara no ensaio atual e arquivá-la automaticamente na
   biblioteca com o nome informado.
7. Se a medição for cancelada ou falhar, preservar a tara válida anterior e não criar/substituir o
   perfil nomeado.

Remover:

- o campo permanente `Criar nova tara (salvar curva medida)`;
- o botão `Salvar nova tara`;
- a ambiguidade entre medir e arquivar.

Manter a seção de perfis existentes com `Selecionar`, `Usar no ensaio` e `Excluir`. Durante uma
medição, desabilitar troca/exclusão de perfil. O nome escolhido deve aparecer no cabeçalho/progresso
do assistente para o operador confirmar qual eixo está sendo medido.

## 4. Eliminação coordenada dos resíduos de flooding

A restauração do gráfico não deve restaurar o fluxo científico de flooding. A implementação deve
fazer uma busca coordenada e tratar:

- `PowerTestViewModel`/`PowerView`: nenhum cálculo, marcador ou controle de flooding;
- `PowerMapViewModel`/`PowerMapView`: remover a camada `FloodingBoundary`, checkboxes de Nienow e
  flooding, linhas no mapa, classificação visual `IsFlooded` e a chamada ativa a
  `ComputeFloodingBoundary`;
- `PowerImpellerComparisonViewModel`/builder/view: remover colunas, eficiência relativa e marcador
  de joelho de flooding dos artefatos novos;
- testes que hoje exigem `CurrentFloodingBoundary`, Nienow ou o ciclo por `Capturado`;
- documentação e tooltips que ainda descrevam detecção de flooding como funcionalidade ativa.

Compatibilidade:

- manter DTOs/campos opcionais legados se forem necessários para desserializar mapas e ensaios
  antigos;
- não apagar esses campos dos arquivos do usuário durante uma simples abertura;
- não popular campos legados em novas reconstruções/comparações;
- se houver migração de versão, ela deve ser não destrutiva e coberta por round-trip.

## 5. Arquivos e responsabilidades afetados

| Área | Arquivos principais | Responsabilidade da mudança |
|---|---|---|
| Layout de ensaio | `Views/PowerView.xaml` | ordem da tabela, botões do cabeçalho, cards movidos, seletor dos gráficos, tara |
| Renderização de ensaio | `Views/PowerView.xaml.cs` | gráfico `P_G/P₀ × Fl_G / Fr`, filtro de aceitos, linha teórica agregada |
| Estado do ensaio | `ViewModels/PowerTestViewModel.cs` | tradução, ciclo manual, invalidação de gráficos, fluxo nomeado de tara, remoção do card kLa |
| Domínio/análise | `Services/PowerTesting/PowerAnalysisEngine.cs` ou helper dedicado | Np teórico da associação e contratos de pontos aceitos |
| Mapa/modelos | `Views/PowerMapView.xaml(.cs)` | aviso de malha, conteúdo kLa no card de origem, limite de pontos e legendas |
| ViewModel do mapa | `ViewModels/PowerMapViewModel.cs` | tabela/resumo da intersecção, remoção do cálculo ativo de flooding |
| Comparação | `Views/PowerImpellerComparisonView.xaml(.cs)`, `ViewModels/PowerImpellerComparisonViewModel.cs`, `Services/PowerMapping/ImpellerComparisonBuilder.cs` | faixa de ações, chave externa, remoção de eficiência/flooding |
| Tema/controles | `Themes/*.xaml`, `Controls/StateDot*` somente se necessário | ícones vetoriais e contraste local do aviso |
| Diálogo | `Services/Dialogs/IDialogService.cs`/implementação existente | solicitar e validar o nome da tara antes da varredura |
| Testes | `tests/OpenTECHub.Tests/*Power*`, `ImpellerComparisonTests.cs`, `CompactLayoutTests.cs` | contratos funcionais, renderização, responsividade e compatibilidade |

## 6. Sequência de implementação recomendada

### Etapa 1 — Contratos de domínio e testes de regressão

1. Fixar testes para filtro `Accepted`, ciclo manual sem `Capturado` e tradução completa.
2. Implementar/testar o Np teórico agregado com um, dois iguais, dois diferentes e estágio sem Np.
3. Fixar o contrato de compatibilidade de leitura de documentos antigos com flooding.
4. Definir a função determinística de seleção de até 500 pontos e seus testes.

### Etapa 2 — Tabela, ações e gráficos do ensaio

1. Unificar ícone/texto da coluna de status e reordenar colunas.
2. Mover `Concluir`/`Interromper` para o cabeçalho.
3. Restaurar o segundo modo de gráfico sem Nienow/flooding.
4. Conectar alterações de status à invalidação do gráfico e verificar atualização ao vivo.

### Etapa 3 — Reorganização dos cards e fluxo de tara

1. Mover `Ponto único` para Aquisição.
2. Implementar o diálogo de nome e a gravação automática da tara medida.
3. Remover entrada/botão redundantes.
4. Mover tabela/resumo kLa para `PowerMapViewModel` e excluir o card de Validação.

### Etapa 4 — Modelos e comparação

1. Reposicionar o aviso de malha e corrigir contraste.
2. Reorganizar a faixa de comparação.
3. Remover eficiência relativa e resíduos ativos de flooding.
4. Externalizar a chave das séries comparativas.
5. Aplicar seleção limitada aos gráficos de modelos e remover legendas excessivas.

### Etapa 5 — Limpeza, documentação e validação visual

1. Remover propriedades/comandos órfãos somente após busca de referências.
2. Atualizar documentação da página de potência e decisões, se o esquema legado exigir ADR.
3. Executar testes direcionados, suíte completa, app real e inspeção de logs recentes.
4. Capturar evidência visual nos dois temas e tamanhos de aceite.

## 7. Testes necessários

### 7.1 Unidade e ViewModel

- status e motivo de parada nunca retornam texto de enum em inglês;
- ciclo manual `Aceito ↔ Rejeitado`, sem parada em `Captured`/`Reviewing`;
- corrida sem captura continua não aceitável;
- coleção de pontos plotáveis contém apenas `Accepted` e reage à troca de status;
- `Np_teórico,associação` obedece à fórmula em `D^5` e não aceita soma parcial;
- tara solicita nome antes da varredura, valida duplicidade e só grava perfil após sucesso;
- falha/cancelamento da tara preserva curva e perfil anteriores;
- tabela kLa é atualizada e invalidada junto com a intersecção;
- seleção gráfica tem no máximo 500 pontos, é determinística e preserva extremos/outliers.

### 7.2 Contratos XAML/renderização

- uma única coluna de status vetorial, imediatamente à esquerda da ação;
- `Data/hora` é a última coluna;
- `Concluir ensaio` e `Interromper` existem apenas no cabeçalho;
- `Ponto único` está em Aquisição, depois da tabela de condições;
- não existe card `Mapa de kLa e eficiência` em Validação;
- tabela/resumo kLa existem no card `Ensaios de origem`;
- aviso de malha fica abaixo das abas laterais e não no cabeçalho;
- não existem checkboxes/camada/legendas de Nienow/flooding na interface ativa;
- comparação não tem legenda sobre a área plotável nem coluna `Efic. rel. dispersão`;
- perfil compacto não corta botões, colunas ou a faixa de chave externa.

### 7.3 Integração e persistência

- abrir ensaio/mapa legado com campos de flooding sem exceção e sem reescrita destrutiva;
- salvar nova comparação/novo mapa sem gerar dados de flooding;
- exportações contêm somente dados aceitos e cabeçalhos atuais;
- mudança de status atualiza tabela, CSV, contadores e gráficos de forma consistente;
- troca de tema atualiza ícones, aviso e gráficos sem reiniciar a página.

## 8. Matriz de aceite visual e operacional

Validar o executável real, não apenas renderização em memória:

| Cenário | Aceite |
|---|---|
| 1280 × 720, 100%, claro/escuro | nenhuma ação cortada; tabela com rolagem local; aviso legível |
| 1920 × 1080, 100%, claro/escuro | ações de comparação em uma linha; gráficos sem sobreposição |
| 1920 × 1080, 150%, claro/escuro | mesmo comportamento lógico do perfil 1280 × 720 |
| Ensaio simulado em captura | gráficos continuam responsivos; só pontos aceitos aparecem |
| Aceitar/rejeitar ponto | atualização visual em até um ciclo do timer, sem reabrir a página |
| Dois impelidores iguais/diferentes | linha teórica corresponde ao Np agregado esperado |
| Tara nova | nome pedido primeiro; uma única ação cria e arquiva a curva |
| Mapa com malha alterada | aviso aparece no local novo e desaparece após reconstrução válida |
| Superfície 300 × 300 | cada gráfico desenha no máximo 500 pontos, sem mudar ajuste/exportação |

Depois da navegação completa pela página, revisar `Logs/Crash` e o log da execução recém-criada.
Não considerar a tarefa concluída apenas com build/testes automatizados.

## 9. Critério de pronto

A implementação estará pronta quando:

1. os 14 pedidos estiverem cobertos pelos contratos acima;
2. flooding/Nienow não forem recalculados nem apresentados, sem quebrar documentos antigos;
3. somente pontos aceitos alimentarem todos os gráficos e modelos;
4. Np teórico usar a associação completa de impelidores;
5. os fluxos de tara e de integração kLa tiverem uma única fonte de ação/estado;
6. testes direcionados e suíte completa passarem;
7. a matriz visual e um smoke real do executável passarem sem novos erros em log;
8. os dados locais existentes do operador permanecerem intactos e fora dos commits de código.

