# Receitas de exemplo

Estes arquivos são exemplos de software no formato `.recipe.json` do OpenTEC-Hub. Os valores são ilustrativos. Não incluem perfil operacional qualificado nem evidência de bancada.

| Arquivo | Comportamento |
|---|---|
| `kla-abiotico-unico.recipe.json` | Um ensaio abiótico a 300 rpm e 2 L/min |
| `kla-biotico-unico.recipe.json` | Um ensaio biótico nas mesmas condições, exigindo OUR válido |
| `kla-abiotico-matriz.recipe.json` | Duas condições, 300 rpm/2 L/min e 400 rpm/3 L/min, com duas réplicas cada |
| `kla-biotico-matriz.recipe.json` | A mesma matriz no protocolo biótico, exigindo OUR válido |
| `kla-periodico-2h-4h.recipe.json` | Cascata em paralelo ao agendador; primeiro kLa em 2 h e período de 4 h; saída da cascata em 24 h |
| `rampas-tempos-distintos.recipe.json` | Vazão 2→3 L/min em 10 min, agitação 300→400 rpm em 15 min e temperatura 25→30 °C em 30 min |
| `cascata-kla-periodico-rampa.recipe.json` | Agenda 2 h/4 h e rampa da referência de O₂ para 40%, associada à cascata, em 12 h de tempo ativo |

Para abrir, copie os arquivos desejados para a pasta **Receitas** da área de dados configurada no aplicativo e abra **Minhas Receitas**. O caminho dessa pasta aparece na lista de pastas de dados em Configurações; por padrão ela fica em `Documentos/OpenTEC-Hub/Receitas`. Os exemplos não são copiados automaticamente para a biblioteca do operador.

Antes de executar, selecione o perfil no bloco **Determinar kLa**, confira protocolo, instalação, calibrações, tolerâncias e limites. `selecionar-perfil-qualificado` é um marcador; não é um perfil instalado. O provedor concreto exige um perfil disponível e compatível. Revise também os limites de tentativa e exposição: os exemplos permitem uma tentativa por réplica; aumentar esse número exige orçamento e perfil compatíveis.

Prepare as malhas, rotas e o estado anterior que deverão ser capturados. A referência explícita da rampa determina o início da trajetória, mas a política **Restaurar referências iniciais** precisa de comandos anteriores aceitos para produzir um retorno verificável. O arquivo não habilita malhas implicitamente.

No exemplo periódico, o agendador dispara em 2/6/10/14/18/22 h enquanto a cascata permanece ativa. Slots perdidos não são acumulados. O ensaio cede e devolve os atuadores automaticamente e os resultados são gravados antes de liberar a próxima execução. A condição de saída de 24 h encerra o grupo e aguarda o retorno de trabalhos ativos.

No exemplo com rampa de O₂, as ligações do grafo definem os ramos paralelos. O parâmetro `cascadeNodeId=casc` corresponde à seleção **Controle de O₂ que receberá a rampa** e define o destinatário. A rampa começa após uma espera de 60 s para o controle iniciar; durante kLa, congela o tempo ativo. Portanto, sua duração no relógio de parede inclui as pausas do ensaio. Se a cascata terminar antes do alvo, o ramo da rampa é encerrado após o retorno, sem executar blocos posteriores.

Verificação automatizada: sete arquivos lidos pelo `RecipeSerializer`, sem erros no `RecipeValidator`, com contrato preservado após serialização e nova leitura. Isso verifica os arquivos; a matriz de aquisição e falhas está documentada em `../../receitas-r52/GRAFO_CONCORRENTE.md`. Verificação visual interativa e qualificação física permanecem separadas.
