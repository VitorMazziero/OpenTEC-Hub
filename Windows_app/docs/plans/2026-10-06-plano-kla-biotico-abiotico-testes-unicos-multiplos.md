# Plano de execução — kLa abiótico e biótico

Data: 06/10/2026. Aplicação: OpenTEC-Hub / Windows.

**Status: E0–E3 implementados e validados em software, com execução e evidências separadas para E2 e E3. Regressão conjunta: 306 aprovados; verificação final específica: 77 aprovados. E4–E7 pendentes. Não houve validação física ou liberação biótica em bancada.**

## 1. Diagnóstico e objetivo

**A interface atual não oferece um procedimento validado de determinação de kLa biótico durante o cultivo.** O runner implementa retirada de oxigênio por N₂ e recuperação com ar. Há um método para iniciar uma condição individual no ViewModel, e a matriz pode conter uma só condição, mas falta o modo explícito “Único”. A sequência atual continua exigindo confirmação da fonte de N₂.

O OUR existente é um sensor virtual condicionado à estabilidade: recebe kLa de um mapa ativo e estima consumo em regime aproximadamente estacionário. Esse OUR não é uma referência independente para determinar novamente o mesmo kLa. É necessário distinguir monitoramento de OUR por mapa de um ensaio dinâmico que mede consumo e transferência.

**Objetivo:** uma página “Determinar kLa” com abas **Abiótico / Biótico**, seleção **Único / Múltiplos**, aquisição e revisão comuns, protocolos operacionais específicos e um núcleo científico compartilhado, inicialmente sem rede neural. Salvar um resultado deve ser possível sem criar, carregar ou publicar mapa.

Para o cultivo previsto em 07/10/2026, não considerar o fluxo abiótico atual uma opção biótica apenas por alterar limites de OD. A liberação biótica depende dos testes de aquisição, retomada e validade científica definidos neste plano. Se não estiverem concluídos, manter a aquisição normal do cultivo e a possibilidade de análise posterior, sem habilitar automaticamente perturbações experimentais.

## 2. Evidências verificadas na aplicação

Os caminhos desta seção são relativos a `Windows_app/src/OpenTECHub/`.

| Componente | Situação observada | Alteração necessária |
|---|---|---|
| `Views/KlaDeterminationView.xaml` | Tela centrada em matriz, sequência, limites de OD e regressão log-linear | Modos explícitos, mantendo gráficos e revisão compartilhados |
| `ViewModels/KlaDeterminationViewModel.cs` | `StartConditionRunAsync` inicia uma condição; início direto e sequência verificam N₂ | Reaproveitar execução individual e separar pré-requisitos por protocolo |
| `Services/KlaTesting/KlaTestModels.cs` | Estados de N₂/recuperação; defaults incluem OD mínimo 15%, máximo 85% e desgaseificação a 700 rpm | Não herdar esses valores como parâmetros de cultivo |
| `Services/KlaTesting/KlaTestRunner.cs` | Obtém posse de agitação/aeração; fecha gases no início e antes da revisão | Criar restauração confirmada do cultivo antes da revisão biótica |
| `KlaTestRunner.cs` | Usa relógio monotônico e confirmação da comutação por telemetria; recuperação termina por OD máximo ou timeout | Preservar confirmação física; mudar término biótico |
| `KlaTestRunner.cs` | Copia OD a cada snapshot, sem condicionar a aquisição a uma nova amostra de O₂ | Não contar quadros de outros canais como novos pontos de OD |
| `Services/KlaTesting/KlaAnalysisEngine.cs` | Ajusta `Ceq − A exp(−κt)` e calcula kLa por OLS de `ln(Ceq − OD)` | Base reaproveitável; explicitar equilíbrio respiratório, fases, consumo e aplicabilidade |
| `Services/Control/OurSoftSensorService.cs` | Busca kLa no mapa ativo com vazão e agitação comandada | Evitar circularidade e suspender OUR condicionado durante perturbação |
| `OurSoftSensorService.cs` | Recria o sensor em qualquer evento `Settings.Changed` | Evitar perda incidental da integral por configurações não relacionadas ao OUR |
| `Services/Communication/CommandArbiter.cs` | Possui controle de propriedade; `Release` transfere para manual | Retomar proprietário/controlador anterior exige coordenação explícita |
| `Services/Recipes/RecipeEnums.cs` e catálogo | Há cascata por mapa; não há bloco dedicado de ensaio kLa no enum inspecionado | Definir serviço reutilizável para integração posterior |
| `KlaTestStore.cs` e `KlaTestFileContracts.cs` | Manifesto, condições, eventos, séries e análises já são persistidos | Evoluir esquema preservando leitura e resultados antigos |

Esta avaliação inspecionou código e documentação; não executou um ensaio físico. A arbitragem existente não comprova, sozinha, retomada correta da cascata ou recuperação em perda de comunicação.

## 3. Base científica local e decisão de reutilização

Pasta estudada: `C:\Users\vitor\OneDrive\Doutorado_CNPq\_Artigos_e_Coorientacoes\Artigos\06_kLa_Modelo`.

A leitura inicialmente falhou por indisponibilidade do OneDrive, mas depois foi possível acessar os documentos e componentes abaixo. O projeto científico não foi modificado.

### 3.1 O que existe no projeto

`PROJECT_STATE.md` e `docs/decisions.md`, reconciliados em setembro de 2026, deixam claro que os componentes de estimação existem, mas o método final e sua validação não estão fechados. A rede prevista identifica fases; não calcula diretamente kLa. Portanto, substituir a identificação neural por eventos e regras determinísticas é compatível com a separação arquitetural existente, sem afirmar que o método do artigo está concluído.

| Fonte local consultada | Aproveitamento proposto | Limite a preservar |
|---|---|---|
| `src/klacore/phases.py` | Cinco fases: `steady`, `gas_off_transient`, `gas_off_consumption`, `gas_on_transient`, `gas_on_recovery` | Mecanismo de retirada é metadado separado; N₂ não produz evidência de OUR |
| `estimation/core.py` | Compor equilíbrio, avaliação da informação, janela e cálculo final | Componente implementado não equivale a método validado |
| `estimation/equilibrium.py` | Estimar Ceq da recuperação por exponencial de assíntota livre, com qualidade e incerteza | Taxa exponencial auxiliar não substitui silenciosamente kLa final |
| `estimation/kernel.py` | OLS nos pontos originais de OD calibrado em janela contígua declarada | Não remover silenciosamente déficit inválido |
| `estimation/window.py` | Duração, amplitude/ruído, autocorrelação, sensibilidade a Ceq e estabilidade de inclinação | Limiares atuais são candidatos de desenvolvimento, não limites universais |
| `estimation/rates.py` | OUR pela inclinação negativa em trecho respiratório e recusa explícita de N₂ | Requer transferência residual desprezível ou quantificada |
| `pipeline/generator/physics.py` | Separar respiração, stripping e recuperação com equilíbrio reduzido pelo consumo | Verdade sintética é exclusiva da avaliação |

Os caminhos `estimation/` acima estão sob `src/klacore/`. A descrição do corpus informa **92 curvas: 75 abióticas e 17 bióticas**, estas de um único cultivo. Não há referência independente de kLa nem constante de sonda registrada. Usar essas curvas para reanálise, robustez e consistência; não apresentar concordância com estimativas anteriores como acurácia absoluta ou generalização biológica.

### 3.2 Métodos explícitos: seleção e restrições

| Método nos contratos locais | Papel no aplicativo |
|---|---|
| Brown–Stenstrom: exponencial com assíntota, condição inicial e taxa livres | Base para estimar Ceq; NLS completo como diagnóstico. Uso biótico é extensão estrutural sob OUR constante |
| Núcleo local de déficit logarítmico | Método principal proposto: Ceq → janela qualificada → OLS final nos dados originais |
| Cerri 2016, abordagem C | Referência para diagnóstico de região e sonda; seleção manual não deve ser apresentada como regra automática publicada |
| Cerri 2016, abordagem B | Diagnóstico futuro; a simetria `kLa ↔ ke` exige informação independente para identificar cada parâmetro |
| Torres 2017 | Comparador abiótico com C* calibrado e regras próprias. Não aplicar diretamente ao biótico nem chamar uma adaptação de reprodução fiel |
| Aroniada | Comparador condicionado a intervalo tardio, saturação/degrau e modelo de sonda requeridos; não é uma rota biótica pronta |
| Damiani 2014 | Referência de consumo e transferência com headspace. O contrato local requer informações adicionais, incluindo O₂ do headspace, volumes/partição e condições iniciais; não supor que o hardware atual as fornece |

**Decisão inicial:** adaptar para C# o núcleo determinístico do projeto, conservando a sequência equilíbrio → janela → OLS. Classificar fases por eventos do equipamento e critérios causais, acrescentar OUR independente e qualificação de validade. Não embarcar Python, rede neural, EKF ou ajuste irrestrito de todos os parâmetros na primeira versão.

Dar à adaptação nome e versão próprios, por exemplo `OpenTecDeterministicKlaV1`. Registrar diferenças dos comparadores publicados e não tratá-la como método final já validado do artigo.

## 4. Organização da interface

### 4.1 Layout compartilhado

```text
Determinar kLa                       [Abiótico] [Biótico]
Aquisição                           [Único] [Múltiplos]

Preparar → Estabilizar → Medir → Recuperar → Revisar

Condição / fila                     OD ao vivo, faixas de fase e ajuste
N, Q, limites                       OD | N medido | Q medido | tempo
Etapa atual e próxima ação          Resultado / qualidade / salvar
```

- Usar componentes e modelo de sessão comuns. A aba muda campos, etapas e validações, sem duplicar tela e algoritmo.
- Manter etapa atual, próxima ação e estado físico visíveis sem rolagem; parâmetros avançados ficam recolhidos.
- Mostrar OD com idade da última amostra. “Sem dados novos” não pode aparecer como zero ou leitura atualizada.
- Separar valores comandados de medidos; informação não disponível permanece explicitamente ausente.
- Mostrar regiões selecionadas e curvas calculadas sobre os pontos adquiridos, com legenda consistente.
- Bloquear troca de protocolo durante corrida; preservar rascunhos separados antes da execução.
- Diferenciar “salvo”, “aceito” e “cultivo restaurado”.

### 4.2 Diferenças entre protocolos

| Etapa | Abiótico | Biótico |
|---|---|---|
| Preparação | N/Q, calibração e arranjo de gases/N₂ | N/Q, condição do cultivo, limites OD/tempo e plano de retorno |
| Remoção de O₂ | Desoxigenação com N₂ | Interrupção breve do ar, sem N₂, mantendo mistura autorizada |
| Consumo | Não calcular OUR do stripping | “Medindo consumo” somente após transiente e dentro dos limites |
| Recuperação | Ar, sem consumo biológico assumido | Ar com consumo presente |
| Finalização física | Política abiótica explicitada | Restauração confirmada antes da revisão |
| Resultado | kLa, Ceq, qualidade | kLa, OUR independente quando válido, Ceq respiratório e referência C* |

### 4.3 Modo único

- Cartão de condição sem exigir matriz ou mapa.
- “Usar condição atual” preenche N/Q; estabilidade precisa ser confirmada antes de iniciar.
- Uma execução produz uma corrida. “Repetir condição” cria outra tentativa vinculada, sem sobrescrever a anterior.
- Registrar condição original, condição ensaiada e condição de retorno quando forem diferentes.
- Salvar, exportar e consultar resultado individual independentemente do módulo de mapas.

### 4.4 Modo múltiplos

- Matriz com N, Q, réplicas, ordem e estado de cada tentativa.
- Distinguir “Somente selecionada”, “Pendentes”, “Fila inteira” e eventual “A partir da selecionada”.
- Mostrar quantidade de corridas e critérios de pausa entre elas.
- No biótico, exigir recuperação e intervalo mínimo antes do próximo pulso, com limites de tentativas e exposição total.
- Registrar tempo de cultivo e ordem: biomassa, morfologia e propriedades do caldo podem mudar durante a matriz.
- Permitir referência intercalada para avaliar deriva. Não reordenar automaticamente o desenho experimental sem registro.
- “Enviar resultados para mapa” é uma ação posterior e opcional. Executar uma matriz não obriga a produzir um mapa.

### 4.5 Ações durante o ensaio

- Ação principal contextual: iniciar, medir, recuperar ou revisar.
- Durante pulso biótico, manter **“Retomar aeração agora”** sempre acessível.
- Cancelamento biótico cancela a medida e inicia recuperação; não reutiliza automaticamente fechamento geral de gases.
- Emergência geral continua com prioridade máxima e sem confusão com cancelamento científico.
- Só mostrar “Cultivo retomado” após confirmação dos comandos, vazão e estado de controle exigidos. Falha de confirmação bloqueia nova corrida.

## 5. Contrato científico comum

### 5.1 Modelo, equilíbrio e unidades

Com tempo em segundos, `k` em s⁻¹ e `r` em concentração/s:

```text
dC/dt = k(C* − C) − r
kLa [h⁻¹] = 3600 k
Ceq = C* − r/k
C(t) = Ceq + [C(t0) − Ceq] exp[−k(t−t0)]
ln(Ceq − C(t)) = a − k(t−t0)    [recuperação ascendente válida]
```

No abiótico, `r=0`. No biótico, requerer consumo aproximadamente constante no episódio curto. `C*` é saturação física do meio para gás/temperatura/pressão; `Ceq` é equilíbrio da recuperação e pode ser menor devido ao consumo.

**Não somar OUR novamente à regressão que já usa Ceq respiratório**, pois isso contaria consumo duas vezes. O termo OUR é necessário quando se usa o balanço com C* físico.

Para `x` em pontos percentuais de uma referência constante `Cref`:

```text
C = Cref × x/100
OUR [pp/h] = −3600 × inclinação(x versus t), em trecho respiratório válido
OUR [mmol/L/h] = OUR [pp/h] × Cref [mmol/L]/100
kLa [h⁻¹] = [3600 dx/dt + OUR(pp/h)] / (x* − x)
```

A última expressão serve como diagnóstico pontual, sensível a derivação e pequena força motriz, não como estimador principal por média de razões.

Não converter para mmol/L/h sem referência válida e origem documentada. Não reutilizar silenciosamente o default atual de 0,21 mmol/L, documentado para 37 °C, em outro cultivo. Mudanças materiais de temperatura, pressão, volume ou gás exigem avaliar a hipótese de modelo constante; caso não atendida, recusar ou usar modelo explicitamente aprovado.

### 5.2 Aquisição e fases sem rede neural

1. Registrar ADC de OD, OD calibrado original, tempos, validade/atualização por canal, temperatura, N/Q, válvulas e identificadores de comando.
2. Usar relógio monotônico para durações/ajustes e UTC para rastreabilidade. Não interpolar lacunas para fabricar pontos independentes.
3. Guardar instante de pedido, eco e evidência física. Fechar válvula não elimina instantaneamente transferência de bolhas/headspace.
4. Separar fase científica de estado do runner: comutar gás cria região candidata, não comprova estabilização.
5. Aplicar detector causal com histerese, duração, estabilidade N/Q e indicadores de derivada/ruído; parametrizar tempo em segundos.
6. Usar as cinco classes do projeto, com mecanismo `respiration`/`nitrogen_stripping`, ciclo e origem do rótulo.
7. Permitir correção manual de janela/fase na revisão, gerando nova versão da análise.
8. Usar suavização para detecção e visualização; OLS final nos pontos originais de OD calibrado, sem confundir com ADC.

### 5.3 OUR independente

- Selecionar trecho contíguo após transiente de gás desligado, mantendo mistura e OD acima do piso definido para o cultivo.
- Ajustar `x=b+mt` e calcular `OUR=−3600m` em pp/h.
- Exigir queda identificável, amplitude/duração suficientes e ausência de mudanças relevantes de processo.
- Avaliar curvatura e estabilidade das inclinações em subintervalos. Queda reduzida por limitação de oxigênio não valida OUR constante.
- Verificar em bancada transferência residual de bolhas/headspace. Se não desprezível nem corrigida por referência independente, retornar consumo aparente ou não identificável.
- Proibir OUR de trecho com N₂; stripping inclui retirada física de O₂.
- Salvar janela, índices, incerteza condicional e hipóteses de validade.
- Nunca prolongar o pulso após limite operacional para melhorar uma regressão.

### 5.4 Ceq, janela e kLa

1. Recortar um episódio de recuperação e patamar posterior imediato, sem misturar ciclos.
2. Estimar Ceq por exponencial de assíntota livre; registrar pesos, limites, convergência, inicializações e sensibilidade.
3. No abiótico, permitir C* independente como alternativa explícita. No biótico, rotular a assíntota como equilíbrio respiratório.
4. Não usar máximo observado como Ceq nem fallback silencioso para 100%.
5. Buscar janelas contíguas após transientes, considerando força motriz, amplitude/ruído e estabilidade de inclinação.
6. Avaliar duração, amostras independentes, autocorrelação, sensibilidade a Ceq e a pequenas mudanças dos extremos.
7. Calcular kLa pelo OLS final de `ln(Ceq−x)`. Déficit inválido invalida a janela; não retirar pontos silenciosamente nem usar módulo para forçar logaritmo.
8. Exibir ajuste NLS completo e razão derivativa como diagnósticos; não escolher automaticamente o valor mais conveniente.
9. Comparar OUR independente com `kLa × (x*−xeq)` nas mesmas unidades e considerando incerteza. Incompatibilidade sinaliza falha das hipóteses.
10. Qualificar kLa e OUR separadamente. Ceq livre pode identificar uma taxa sob OUR constante sem medir OUR independentemente; isso não autoriza rotular o consumo como medido.

### 5.5 Sonda e identificabilidade

Modelo mínimo de observação: `τp dy/dt + y = C`. Se a sonda for mais lenta que o processo, a cauda pode refletir a sonda, não kLa.

- Registrar tipo de sonda, calibração, filtragem e constante de resposta independente quando conhecida.
- Usar `ke/τp` recuperado pelo método local como diagnóstico condicionado, não autocalibração garantida.
- Preferir ensaio independente da resposta da sonda sob condições relevantes.
- Caso se ajuste modelo com sonda, fixar/restringir τp com evidência independente e avaliar sensibilidade.
- Não ajustar irrestritamente kLa, OUR, C* e τp juntos a uma curva curta. A simetria do modelo Cerri é um exemplo documentado de ambiguidade.
- Retornar “kLa não identificável” quando não houver evidência para separar resposta da sonda e transferência.

### 5.6 Qualidade e término

Separar qualidade científica (`válido`, `condicionado`, `inconclusivo`), decisão do operador (`aceito`, `rejeitado`) e estado físico (`restaurado`, `não confirmado`). Aceite manual não transforma estimativa inconclusiva em válida.

- Gas-off termina no primeiro entre piso de OD, queda máxima, tempo máximo, pedido do operador ou falha de aquisição/atuador.
- Considerar antecipação pela queda projetada durante atraso de sonda/comando, com margem conservadora validada.
- Recuperação termina por retorno à banda de operação e estabilidade, ou informação suficiente seguida de restauração; não exigir 85% universalmente.
- Timeout/informação insuficiente causa recuperação e resultado inconclusivo.
- R² alto não basta: avaliar resíduos, incerteza, constância e identificabilidade.
- IC do OLS é condicional; não cobre sozinho incertezas de Ceq, sonda, janela e autocorrelação. Acrescentar sensibilidade e validar reamostragem em blocos ou alternativa documentada.

## 6. Protocolo operacional e concorrência

### 6.1 Estados propostos

```text
Pré-verificação → Reservar atuadores → Salvar estado do cultivo
→ Estabilizar condição → Interromper ar → Aguardar transiente
→ Medir consumo → Retomar ar → Recuperar OD
→ Restaurar condição/controlador → Confirmar retomada → Revisar
```

Cancelamento/limite em estado ativo leva à recuperação. Falha de confirmação gera falha explícita e bloqueia próxima corrida. A revisão biótica não mantém ar desligado.

### 6.2 Regras de comando e retorno

- Usar o arbiter existente, reservando aeração/agitação como conjunto e verificando propriedade antes de atuar.
- Salvar setpoints, leituras, rotas, proprietário anterior, modo/ponto da cascata e contexto de receita, além de plano de recuperação validado.
- Coordenar suspensão dos consumidores concorrentes de N/Q; não apenas tomar posse enquanto a receita continua avançando.
- Manter N constante durante consumo/recuperação da condição; não herdar 700 rpm da desgaseificação.
- Restaurar a condição do cultivo e confirmar antes de transferir explicitamente ao proprietário anterior. Liberar para manual não retoma automaticamente cascata/receita.
- Retomar cascata sem salto e com tratamento de sua memória integral; validar antes de automatizar.
- Preservar controles não conflitantes conforme protocolo. Registrar intervenções de alimentação, pH/antiespumante que possam invalidar janela, sem desligá-los indiscriminadamente.
- Congelar calibração e arranjo de gases por corrida. Mudança crítica não se aplica silenciosamente a uma aquisição em curso.

### 6.3 Perda de comunicação e reinício

Verificar em bancada perda de conexão, queda do app, perda de energia, vazão ausente e válvula sem eco. O app não consegue garantir aeração depois de perder comunicação. Validar watchdog/estado seguro físico; manter ensaio biótico indisponível se a recuperação necessária depender exclusivamente de comandos que não podem mais chegar.

Ao reabrir sessão interrompida, carregar os dados como interrompidos e reconciliar com estado físico. Não reenviar automaticamente a última sequência de gás.

## 7. Convivência com o OUR atual

- Introduzir estado “Medição de kLa em andamento” no serviço e tela OUR.
- Suspender emissão/integral do OUR condicionado durante perturbação e recuperação. Usar lacunas explícitas, nunca zero ou repetição de leitura antiga como nova.
- Preservar integral prévia e impedir ponte trapezoidal através da lacuna.
- Na retomada, reiniciar somente a janela de estabilidade necessária, conservando total/histórico.
- Comparar configurações relevantes em `OnSettingsChanged`; eventos alheios ao OUR não devem apagar acumulados. Mudanças científicas relevantes criam segmento versionado ou reset explícito.
- Distinguir `OUR_condicionado_mapa` de `OUR_dinamico_ensaio` no histórico.
- Não usar OUR derivado do mapa como entrada independente para medir o kLa desse mesmo mapa.
- Aceitar um teste não deve atualizar mapa nem cascata automaticamente. Referência operacional exige operação posterior e validade temporal/condicional explícita.

## 8. Arquitetura, dados e compatibilidade

### 8.1 Responsabilidades propostas

| Parte | Responsabilidade |
|---|---|
| `KlaAssayDefinition` | Tipo, modo, condições e configurações imutáveis da execução |
| `KlaProtocolPolicy` | Pré-requisitos, comandos, limites e finalização específicos |
| `KlaTestRunner` | Orquestração comum, aquisição e eventos |
| `KlaPhaseDetector` | Fases determinísticas a partir de observações/eventos |
| `KlaAnalysisEngine` | Equilíbrio, janela, OLS, OUR e qualidade, em funções puras |
| `KlaAssayCoordinator` | Posse de atuadores, suspensão de automações/OUR e retorno |
| `KlaTestStore` | Escrita durável, esquema, importação e revisões |
| Tela/ViewModel | Preparação, progresso e revisão dos quatro modos |

Os novos nomes são propostas. Cada corrida é a mesma unidade independente: único executa uma unidade; múltiplos agenda unidades. Não criar runners científicos duplicados.

### 8.2 Esquema persistido

Evoluir a versão após confirmar as versões existentes. Preservar nomes atuais: `teste.json`, `tabela-condicoes.json`, `eventos.jsonl`, `serie-global.csv`, `resumo-resultados.csv`; por corrida, `dados-brutos.csv`, `analise.json`, `resultado.csv`.

Adicionar:

- Tipo/protocolo, modo, mecanismo de retirada e versão.
- IDs de cultivo, sessão, condição, tentativa, ciclo e receita quando houver.
- Tempo de cultivo, meio/volume informado, sonda, calibração e Cref/C* com unidade e origem.
- N/Q solicitados e medidos, temperatura e dados disponíveis; ausentes continuam nulos.
- Estado inicial, plano de retorno, confirmações e falhas de restauração.
- Tempos de pedidos/ecos/transições observadas e frescor/qualidade por canal.
- OUR pp/h e concentração quando válida; Ceq separado de C*; τp/ke com origem.
- Índices/janelas, origem da seleção, exclusões justificadas, resíduos, incertezas e motivos de recusa.
- Versões de algoritmo/configuração/build, hash do bruto e histórico imutável de análise.
- Qualidade científica, decisão do operador, término físico e envio opcional ao mapa.

O armazenamento atual usa `AppPaths.KlaTestsDirectory = DataDirectory/Testes-kLa`. Mostrar o caminho resolvido da sessão e permitir abrir a pasta; não presumir que seja a pasta do executável.

Arquivos antigos devem abrir como protocolo legado compatível, com informação não registrada marcada desconhecida. Preservar resultados antigos; reanalisar gera nova revisão. Manter origem real/simulação explícita, sem misturá-las em mapas operacionais.

## 9. Etapas de execução

### E0 — Contrato e referência de regressão

- [x] Registrar versões/commits da aplicação e fontes científicas; preservar alterações existentes de calibração.
- [x] Selecionar curvas e arquivos legados v1/v2 para testes de compatibilidade (v1 real; v2 sintético explicitamente identificado).
- [x] Registrar padrões editáveis N/Q/OD e sonda; registrar amostragem pelos tempos monotônicos, sem fixar frequência universal.
- [x] Separar alvo de remoção e critérios configuráveis de retomada; exigir os valores necessários na sessão operacional, sem limite biológico universal.
- [x] Fixar contrato de equações/unidades, Ceq, seleção, recusa e incerteza.
- [x] Registrar discrepâncias bibliográficas: paginação de Damiani no contrato local difere do registro PubMed; identificar artigo pelo DOI.

Entrega: especificação testável, fixtures e separação entre desenvolvimento e avaliação final.

Execução E0 registrada em [CONTRATO_E0.md](kla-e0/CONTRATO_E0.md). Base técnica congelada no instante de E0. O responsável posteriormente informou os padrões editáveis abaixo; a aquisição usa tempos monotônicos, sem impor frequência universal. Tempo numérico de resposta da sonda e frequência real em bancada não foram medidos. Setpoints/defaults salvos não se tornam limites universais do algoritmo.

Atualização posterior do responsável: todos os valores abaixo são padrões editáveis na configuração do usuário, não limites fixos do algoritmo. N=50–1000 rpm, Q=0,5–16 L/min, OD usual=30–100%, sonda polarográfica com resposta média; remoção a 100 rpm até alvo de 5–20%. A constante numérica da sonda é opcional. Procedimento e distinção entre faixa usual e alvo de ensaio estão no [contrato E1](kla-e1/CONTRATO_E1.md); o snapshot E0 permanece preservado.

### E1 — Contratos e modelo de sessão

- [x] Adicionar `Abiotic/Biotic` e `Single/Multiple` sem quebrar tags legadas.
- [x] Separar política operacional da fila de condições.
- [x] Fazer teste único usar o mesmo caminho de corrida do múltiplo.
- [x] Separar qualidade kLa/OUR, decisão do operador e estado físico.
- [x] Versionar leitura/gravação e migração sem modificar resultados históricos.

Arquivos: `KlaTestModels.cs`, interfaces de `KlaTesting`, `KlaTestFileContracts.cs`, `KlaTestStore.cs`. Aceite: gravação/leitura dos quatro modos e arquivos antigos, sem comando físico.

Execução e procedimento detalhados em [CONTRATO_E1.md](kla-e1/CONTRATO_E1.md). Esquema novo 3; arquivos antigos mantêm versão e valores. A definição biótica registra ar para escape com fluxômetro ligado e N₂ fechado. A atuação correspondente foi implementada em E2; a interface para preparação biótica depende de E4.

### E2 — Aquisição qualificada e controle

- [x] Aceitar apenas novas amostras válidas de O₂; registrar demais eventos sem duplicar OD.
- [x] Implementar timeout de OD separado do heartbeat geral.
- [x] Registrar latências, calibração e metadados por corrida.
- [x] Criar coordenador de posse/suspensão/retomada por protocolo.
- [x] Validar pré-condições e bloquear início com estado necessário desconhecido.
- [x] Implementar recuperação antecipada, limites e confirmação de retorno em todas as saídas.
- [x] Suspender/retomar OUR sem apagar integral.

Arquivos: runner, consumidores do `CommandArbiter`, `OurSoftSensorService`, serviços de cascata e telemetria. Aceite: testes de todas as saídas demonstram restauração ou falha explícita; revisão biótica não deixa intencionalmente ar desligado.

Execução de software registrada em [EXECUCAO_E2.md](kla-e2/EXECUCAO_E2.md). Aquisição qualificada, rota biótica e retomada possuem testes. Bancada/E4/E6 permanecem pendentes; receitas concorrentes são bloqueadas.

### E3 — Núcleo determinístico

- [x] Congelar entradas/saídas dos componentes Python para testes de paridade C#.
- [x] Implementar fases por eventos e regras causais, com origem registrada.
- [x] Adaptar Ceq, eliminar fallback silencioso e separar C* físico.
- [x] Implementar busca de janela auditável e OLS final original.
- [x] Implementar OUR respiratório e recusa de stripping.
- [x] Acrescentar sonda, balanço e diagnósticos de identificabilidade.
- [x] Definir qualidade/recusa e sensibilidade, preservando análise legada versionada.

Arquivos: `KlaAnalysisEngine.cs` e componentes puros em `Services/KlaTesting/`. Aceite: paridade dos componentes e casos analíticos independentes. Limiares do projeto são candidatos; mudança exige registro e validação.

Contrato de implementação em [CONTRATO_E3.md](kla-e3/CONTRATO_E3.md). A paridade cobre OLS/OUR e Ceq sem suavização; detector/seleção são adaptações C# versionadas. API e adapter disponíveis para E4; revisão legada preservada. A validação final conjunta está registrada nos recibos E2/E3.

### E4 — Interface comum

- [ ] Criar abas e seletor de captura.
- [ ] Implementar cartão único e matriz sobre a mesma definição de condição.
- [ ] Mostrar etapa, próximo evento e ação principal contextual.
- [ ] Reusar gráfico com faixas, dados, ajuste e limites.
- [ ] Exibir OUR apenas quando aplicável, distinguindo aparente/validado.
- [ ] Exibir restauração antes da revisão, caminho salvo e qualidade.
- [ ] Reanálise manual gera revisão; exportação independe de mapa.
- [ ] Verificar teclado, janela reduzida e DPI 100%, 125%, 150%.

Arquivos: `KlaDeterminationView.xaml`, ViewModel e componentes compartilhados. Aceite: quatro fluxos em simulação sem ações cortadas ou estados ambíguos.

### E5 — Sequências e mapas opcionais

- [ ] Separar condição, réplica e tentativa na fila.
- [ ] Bloquear próxima corrida até recuperação confirmada.
- [ ] Limitar repetição automática e exposição; resultado ruim não autoriza pulsos indefinidos.
- [ ] Permitir encerrar fila preservando histórico.
- [ ] Implementar envio opcional de aceitos ao mapa e filtros de compatibilidade.
- [ ] Registrar/proteger diferenças de meio, cultivo, tempo e simulação.
- [ ] Não tratar um ponto como superfície N×Q identificada nem sobrescrever mapa ativo automaticamente.

Arquivos: runner, ViewModel, `KlaMapImportHelper.cs` e modelos de mapas. Aceite: único/múltiplos funcionam sem mapa; mapa biótico não mistura contextos silenciosamente.

### E6 — Contrato para receitas futuras

- [ ] Expor API independente de UI: criar, iniciar, observar, cancelar com recuperação e obter resultado.
- [ ] Definir request com protocolo, condição atual/explícita, limites, deadline e política de falha.
- [ ] Usar ID idempotente para impedir pulso duplicado após reconexão.
- [ ] Definir retornos concluído, inconclusivo, cancelado e falha de restauração.
- [ ] Documentar lock de atuadores, intervalo mínimo e limite por cultivo.
- [ ] Especificar futuro bloco `KlaAssay` no enum, catálogo, validador e engine; habilitar somente após validação.
- [ ] Distinguir periodicidade de réplicas imediatas; registrar execução adiada/pulada e não acumular pulsos atrasados.

Entrega inicial: contrato e testes da API. Editor/agenda periódica podem ser entrega posterior, sem refazer algoritmo.

### E7 — Validação, documentação e distribuição

- [ ] Executar matriz da seção 10 e registrar evidências.
- [ ] Atualizar ajuda contextual e procedimentos kLa/OUR.
- [ ] Produzir build identificável após critérios aprovados.
- [ ] Preservar versão anterior e dados; não usar arquivos originais do cultivo para testar migração.
- [ ] Confirmar reabertura com histórico intacto e sem iniciar atuadores.
- [ ] Validar em bancada antes de liberar biótico supervisionado.

Dependências: E0→E1; E2/E3 sustentam E4; E5 depende de E2–E4; E6 usa contratos E1/E2; E7 exige validação operacional e científica. Aparência não deve preceder aquisição confiável e retomada.

## 10. Matriz de testes e critérios de aceite

| Grupo | Casos | Resultado esperado |
|---|---|---|
| Equações | r=0; r constante; Ceq abaixo de 85%; origem de tempo deslocada | Recuperar taxa no modelo ideal e equivalência abiótica em r=0 |
| Unidades | s/h, pp/h, mmol/L/h, Cref ausente | Conversões corretas e concentração indisponível quando não justificável |
| Sonda | rápida/lenta, k≈ke, atraso e filtro | Condicionamento/recusa quando não identificável |
| Consumo | variável, limitação OD, transferência residual, N₂ | Não apresentar hipóteses violadas como OUR validado |
| Curva | ruído, outliers, janela curta, saturação, recuperação incompleta | Recusas auditáveis, sem limpeza silenciosa ou Ceq=100 automático |
| Aquisição | quadros sem novo OD, NaN, duplicação, gap e mudança de relógio civil | Sem falsa estabilidade; relógio monotônico |
| Controle | cascata/receita concorrente, cancelamento em cada fase, perda de eco | Posse coerente, restauração ou falha explícita e fila bloqueada |
| Falhas | OD para mas outros sensores seguem; comunicação cai; reinício | Timeout por canal, dados preservados, sem reinício automático do pulso |
| OUR virtual | início/fim, settings irrelevante e lacuna longa | Integral preservada, sem integração através do ensaio |
| Arquivos | legados, revisão, falha de escrita, hash e importação | Histórico preservado; “salvo” só após escrita confirmada |
| UI | quatro modos, limite e falha de retorno, DPI | Ação clara, retomada acessível, sem sucesso físico falso |
| Mapas/receitas | sem mapa, envio opcional, request repetido | Sem circularidade ou perturbação duplicada |

Reutilizar testes existentes: `KlaAnalysisEngineTests`, `KlaTestRunnerTests`, `KlaRunnerSimulatorTests`, `KlaTestStoreTests`, `KlaPlaybackDeviceServiceTests`, `KlaDeterminationViewModelTests`, `CommandArbiterTests`, `OurSoftSensorTests`, `OurSoftSensorScientificTests` e `RecipeEngineTests`. Acrescentar casos analíticos independentes, sem apenas espelhar implementação.

Metas iniciais propostas, a confirmar em E0/E3:

- Trajetórias analíticas sem ruído, sonda desprezível/conhecida e hipóteses satisfeitas: erro relativo até 1% para kLa/OUR com sinal identificável.
- Paridade Python/C#: tolerâncias por grandeza/solver mais rigorosas que essa meta científica.
- Cenários com ruído: reportar viés, dispersão, cobertura dos intervalos e recusa por cenário; não perseguir taxa global de aceite.
- Casos adversos devem gerar condicionamento/recusa mesmo com R² alto.
- Separar desenvolvimento e avaliação por aquisição/cultivo; ciclos irmãos não são unidades experimentais independentes.
- Reanalisar 92 curvas sem herdar resultados antigos como verdade. As 17 bióticas não substituem novos cultivos de validação.
- Verificar recuperação física em bancada para saídas normais/cancelamentos. Falha física deve ser detectada, nunca mascarada como retorno concluído.

## 11. Ordem de liberação

| Entrega | Escopo | Condição de liberação |
|---|---|---|
| A | Abiótico único/múltiplos, layout comum e aquisição qualificada | Regressão abiótica e persistência aprovadas |
| B | Análise biótica offline com Ceq/OUR/janelas | Testes científicos e revisão de curvas |
| C | Biótico único operacional | Limites, sonda, controle e bancada aprovados |
| D | Biótico múltiplos e destino opcional a mapas | Recuperação, deriva e exposição validadas |
| E | Testes periódicos em receitas | API idempotente, arbitragem e falhas aprovadas |

Não prometer C para amanhã com base apenas em build/testes unitários. Prioridade imediata: limites reais, aquisição rastreável e ensaio de retomada. Se houver coleta experimental, guardar curva completa e eventos para reanálise sem apresentar estimativa não validada como medição pronta.

## 12. Fontes e rastreabilidade

### Aplicação

- [Tela](../../src/OpenTECHub/Views/KlaDeterminationView.xaml)
- [ViewModel](../../src/OpenTECHub/ViewModels/KlaDeterminationViewModel.cs)
- [Runner](../../src/OpenTECHub/Services/KlaTesting/KlaTestRunner.cs)
- [Análise](../../src/OpenTECHub/Services/KlaTesting/KlaAnalysisEngine.cs)
- [Modelos](../../src/OpenTECHub/Services/KlaTesting/KlaTestModels.cs)
- [OUR condicionado](../../src/OpenTECHub/Services/Control/OurSoftSensor.cs)
- [Integração OUR](../../src/OpenTECHub/Services/Control/OurSoftSensorService.cs)
- [Arbitragem](../../src/OpenTECHub/Services/Communication/CommandArbiter.cs)

### Projeto local

Relativos à pasta `06_kLa_Modelo`:

- `PROJECT_STATE.md`, `docs/decisions.md`: escopo e limites da pesquisa.
- `src/klacore/estimation/{core,equilibrium,kernel,rates,window}.py`: componentes consultados.
- `src/klacore/phases.py`, `src/pipeline/generator/physics.py`: fases e mecanismos físicos.
- `src/comparadores/{torres2017,cerri2016,aroniada2019,damiani2014,brownstenstrom1980}/LEIA-ME_*.md`: contratos de reprodução/aplicabilidade.
- `data/experimental/curated/LEIA-ME_conjunto_de_curvas.md`: composição e limites do corpus.

### Referências identificadas

- [Damiani, Kim e Wang — An improved dynamic method to measure kLa in bioreactors](https://pubmed.ncbi.nlm.nih.gov/24838309/), DOI 10.1002/bit.25258: referência de método dinâmico com consumo; detalhes avaliados pelo contrato local.
- [Torres et al. — Automated algorithm to determine kLa considering system delay](https://doi.org/10.1002/jctb.5157): identificação e requisitos conforme contrato local.
- [Cerri et al. — A new approach for kLa determination by gassing-out method in pneumatic bioreactors](https://doi.org/10.1002/jctb.4937): identificação e limites conforme contrato local.
- [Aroniada et al. — Estimation of volumetric mass transfer coefficient: review and novel methodology](https://www.sciencedirect.com/science/article/pii/S1369703X19303973): dinâmica da sonda, complementada pelo contrato local.
- Brown LC, Stenstrom MK, *Proposed Modifications of K2-Temperature Relation*, 1980: referência identificada no contrato local; uso com OUR constante é extensão matemática, não validação biótica feita pelos autores.

Esta tarefa leu documentos/componentes locais e registros/resumos bibliográficos disponíveis. Não executou novamente benchmarks nem verificou integralmente todos os PDFs. Escolhas de produto e critérios propostos precisam passar pelas etapas de validação antes de uso operacional.


