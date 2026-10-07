# E0 — contrato e referência de regressão

Versão congelada: `OpenTecDeterministicKlaV1-E0.1`. Data: 06/10/2026.

**Estado:** base técnica capturada e verificável. Envelope operacional do cultivo pendente de informações do responsável. Este contrato prepara E1–E3; não habilita protocolo biótico na aplicação.

## 1. Versões e preservação

| Item | Referência capturada |
|---|---|
| Aplicação HEAD | `bf2129b91d59b5f80ae214bafd41947637363101` |
| Descrição Git | `v2026.09.27-11-gbf2129b-dirty` — não é versão limpa publicada |
| Projeto científico HEAD | `7d91a7be107e9ce52347d8c69f63e8b341432c00` |
| Estado científico | Sem mudanças locais no instante da captura |
| SDK .NET | `10.0.400` |
| Persistência atual | v1 legado; v2 acrescenta temperatura e RPM medidos |
| Algoritmo atual | `LogLinear_OLS_v2` |

O [baseline](../../../tests/fixtures/kla-e0/baseline.json) registra SHA-256 dos componentes usados, estado prévio do checkout e hashes dos arquivos já modificados, incluindo calibração. O commit não identifica sozinho os bytes analisados: o checkout da aplicação contém alterações locais preservadas. Não houve commit, reset ou mudança dos serviços operacionais nesta etapa.

Os limites operacionais pendentes e observações de configuração estão em [operating-envelope.json](../../../tests/fixtures/kla-e0/operating-envelope.json). O hash da configuração foi capturado sem copiar credenciais ou alterar a pasta do cultivo.

## 2. Referências selecionadas e papel de cada uma

Os arquivos ficam em `Windows_app/tests/fixtures/kla-e0/` e são copiados somente para a saída do projeto de testes; não entram nos mapas/perfis do aplicativo.

| Referência | Origem e papel | O que não demonstra |
|---|---|---|
| `legacy-v1/` | Cópia byte a byte de `OpenTEC-Hub/Testes-kLa/Ensaio Alivio Parada`, incluindo manifesto, eventos e bruto | Não é referência independente de kLa nem protocolo atual de bancada |
| `analytic-v2/abiotic/` | Recuperação ideal, C*=Ceq=100%, kLa=72 h⁻¹; ADC propositalmente distinto de OD | Não reproduz sensor real |
| `analytic-v2/biotic-constant-our/` | C*=100%, Ceq=75%, OUR=1800 pp/h, kLa=72 h⁻¹ | É teste algébrico do kernel; manifesto compatível com leitor atual não representa suporte operacional biótico |
| `analytic-v2/shifted-time/` | Mesmo modelo com origem de tempo deslocada em 123 s | Não valida relógio/latência de equipamento |
| `experimental-development/ALR5L-01.csv` e `ALR5L-02.csv` | Duas curvas abióticas originais do corpus, mesmo grupo | Não são duas unidades experimentais independentes |
| `experimental-development/SC0708-02.csv` | Uma curva biótica original para desenvolvimento/inspeção | Sem verdade independente de kLa ou τp |

Não foram encontrados ensaios v2 reais nas pastas de testes kLa inspecionadas da aplicação e do cultivo. Por isso, o v2 foi criado analiticamente e declarado sintético, mantendo cabeçalho atual e colunas opcionais ausentes/presentes. Não converter v1 artificialmente e apresentá-lo como medição v2.

`analytical-cases.json` fixa valores verdadeiros, janela inclusiva [10,120] s (ou [133,243] s), 101 pontos, 56 usados e tolerância de 10⁻⁶ h⁻¹ no kernel ideal. Essa tolerância numérica não é promessa de precisão experimental.

O legado v1 contém fases históricas que o enum atual não conhece, como `OpeningVent`. O leitor atual as converte para `Idle`. Preservar bytes e registrar a limitação; reconhecimento/migração de fases históricas é trabalho futuro. A referência v1 verifica abertura e ausência honesta de metadados, não equivalência semântica das fases.

## 3. Separação entre desenvolvimento e avaliação

[split-manifest.json](../../../tests/fixtures/kla-e0/split-manifest.json) atribui as 92 curvas por grupo `assay:batch`: **28 para desenvolvimento e 64 reservadas, ainda não avaliadas**. A escolha é por proveniência, não por desempenho ou resultados de modelo.

- As 11 curvas ALR5L e todas as 17 bióticas pertencem ao desenvolvimento.
- O grupo inteiro acompanha a curva selecionada; irmãos nunca são repartidos entre desenvolvimento e reserva.
- As 17 bióticas pertencem a um cultivo. Não existe holdout biótico independente nesse corpus.
- A reserva abiótica não deve ser usada para ajustar limiares; avaliação final só após congelamento do método.
- Avaliação biótica final precisa de novos cultivos e desenho experimental declarado, com agrupamento por cultivo/aquisição.
- Metadata de estimativas do estudo original não constitui verdade para pontuar acurácia. Pode permanecer como proveniência, sem entrar no cálculo/seleção.
- Se a reserva for consultada para desenvolvimento, registrar isso e redefinir uma avaliação independente; não continuar chamando-a de avaliação final.

## 4. Domínio e dados conhecidos

| Grandeza | Evidência disponível | Estado para execução biótica |
|---|---|---|
| N/Q do cultivo | Configuração salva: motor 0/desabilitado, vazão 0,5 L/min, máximo configurado 50 L/min | Não são faixa experimental aprovada |
| OD de operação | Setpoint salvo 0/desabilitado | Pendente; zero não pode ser adotado como condição de operação |
| Temperatura/volume | Temperatura salva 0/desabilitada; volume não estabelecido nesta captura | Pendente |
| Sonda | Corpus selecionado: óptica no ALR5L e polarográfica no SC0708, sem modelo/τp | Modelo e τp do cultivo pendentes |
| Amostragem | ALR5L: mediana 0,25 s; SC0708: mediana 1,8 s, p95 2,4 s, gap máximo 4,2 s | Não determina frequência real do hardware atual |
| Cref | Configuração OUR contém 0,21 mmol/L | Default de origem específica; não é calibração físico-química deste cultivo |
| Piso, queda, duração e retorno | Sem valores autorizados na sessão | Pendentes; nenhum default biológico aplicado |

Domínio matemático E0: séries alinhadas, finitas, tempos estritamente crescentes, OD calibrado em pp, parâmetros constantes por episódio e unidades explícitas. Não impor 0–100% como limite de calibração universal: separar faixa instrumental e limite biológico. As fixtures ideais usam OD entre 10 e 100%, N=400 rpm e Q=3 L/min como dados sintéticos, sem autorizar essas condições no cultivo.

O envelope mantém nulos: volume, temperatura, N/Q mínimo/máximo, OD operacional/piso, queda máxima, duração gas-off/recuperação, intervalo entre ensaios, modelo/τp, frequência/frescor e responsável/data. `bioticExecutionApproved=false` é estado de referência do E0; não é um novo bloqueio implementado no app.

Para fechar esse item, o responsável deve fornecer valores e unidades, origem dos limites, condição de retorno e evidência de resposta/estado seguro do equipamento. Registrar em uma nova revisão de envelope; preservar o snapshot E0.1.

## 5. Contrato científico para E1–E3

### Equações e unidades

```text
t: segundos; k: s⁻¹; x: pontos percentuais (pp)
dC/dt = k(C*−C)−r
Ceq = C*−r/k
C(t) = Ceq + [C(t0)−Ceq] exp[−k(t−t0)]
kLa(h⁻¹) = −3600 × inclinação de ln(Ceq−x) versus t
OUR(pp/h) = −3600 × inclinação de x versus t no trecho respiratório
OUR(mmol/L/h) = OUR(pp/h) × Cref(mmol/L)/100
OUR(pp/h) = kLa(h⁻¹) × (x*−xeq), diagnóstico sob hipóteses constantes
```

Abiótico: r=0; biótico: r aproximadamente constante no episódio. C* é saturação física, Ceq é equilíbrio operacional respiratório. Não somar OUR ao déficit que já usa Ceq. Não usar máximo observado nem fallback silencioso C*=100%. Sem Cref validado, saída em concentração é nula, não zero.

### Entrada, janela e estimação

1. Aceitar somente novas amostras válidas de OD. Guardar ADC e calibrado separadamente; tempos monotônicos e UTC com papéis distintos.
2. Fases por eventos confirmados e regras causais. Comutação inicia transiente; não prova ausência de bolhas ou resposta estabilizada.
3. OUR exige janela respiratória contígua, sem N₂, após transiente e sem limitação de O₂. Transferência residual desconhecida impede rótulo de consumo independente validado.
4. Ceq é ajustado em uma recuperação e patamar seguinte. Exponencial serve para equilíbrio; taxa auxiliar fica identificada como diagnóstico.
5. Selecionar uma janela contígua identificável, registrar índices inclusivos e versão/origem da regra. Suavização auxilia seleção/visualização; OLS usa OD calibrado original.
6. Todos os pontos da janela devem ter déficit acima do piso numérico. Não excluir silenciosamente, usar módulo ou clipping.
7. Separar identificação de kLa da identificação de OUR. Ausência de OUR independente não equivale automaticamente a ausência de taxa identificável, mas exige qualificação das hipóteses biológicas.
8. Sonda desconhecida/lenta pode dominar a taxa. Não ajustar livremente kLa, ke, OUR e C* ao mesmo trecho sem evidência independente.

### Política inicial de informação

Valores abaixo são **candidatos congelados de desenvolvimento**, transcritos de `WindowSearchConfig`, não validação biológica. E3 deve portá-los com testes antes de ajustar. Alteração exige nova versão e avaliação com conjunto de desenvolvimento.

| Critério | Candidato E0.1 |
|---|---|
| Mínimo numérico | 3 pontos, 2 s |
| Qualidade principal | 8 pontos, 10 s |
| Piso do déficit | 0,05 pp |
| Piso estimativa de ruído | 0,01 pp |
| R² principal | ≥0,95 |
| Amplitude relativa ao ruído no endpoint | z≥3 |
| SE relativa da inclinação | ≤0,20 |
| Autocorrelação lag1 máxima | 0,80, conforme métrica do componente de origem |
| Variação de inclinação em janelas aninhadas | ≤0,20 |
| Sensibilidade relativa a Ceq | ≤0,20; passo de avaliação 0,10 pp |
| Fração perturbada máxima | 0,10 |

Equilíbrio: candidato mínimo 20 pontos, R²≥0,90, limite numérico superior 110%, peso `linear_ramp` com razão 5, conforme configuração local. Não copiar a seleção de cauda offline para causalidade online. O fallback por concordância de equilíbrio permanece desabilitado inicialmente; não promover resultados condicionados a válidos automaticamente.

OUR: candidato mínimo 10 pontos e inclinação negativa identificável, conforme `rates.py`. Duração/amplitude e margem de segurança dependem da amostragem real e envelope ainda pendentes.

Separar graus científicos de aceite humano. “Não identificável” é uma saída válida do programa e não permite aceitação científica por insistência do operador.

### Recusas e diagnósticos obrigatórios

| Motivo estável proposto | Trigger | Saída |
|---|---|---|
| `invalid_series` | NaN, desalinhamento, tempo não crescente | Sem estimativa |
| `stale_oxygen` | Sem novo OD dentro do limite definido | Encerrar aquisição e solicitar recuperação |
| `insufficient_information` | Duração/amplitude/pontos insuficientes | Inconclusivo |
| `invalid_driving_force` | Ceq−x≤piso dentro da janela | Janela inválida, sem limpeza silenciosa |
| `equilibrium_unidentified` | Ajuste não convergente ou assíntota instável | Sem kLa principal |
| `nonpositive_rate` | Inclinação de recuperação não negativa | Sem kLa válido |
| `our_not_applicable_to_nitrogen` | Remoção por N₂ | OUR nulo/inaplicável |
| `residual_transfer_unknown` | Gas-off sem evidência de OTR desprezível | Consumo aparente/condicionado |
| `nonconstant_consumption` | Curvatura/limitação/perturbação significativa | Modelo constante não válido |
| `probe_process_unidentifiable` | Não separam taxa de sonda e transferência | Sem kLa validado |
| `operating_condition_changed` | N/Q/temperatura/gás variam materialmente | Invalidar janela/episódio afetado |
| `restoration_unconfirmed` | Retorno físico não confirmado | Falha operacional, bloquear nova corrida |

Esses códigos são contrato de comportamento futuro. E0 não altera o runner para emiti-los.

### Incerteza e validação

- Exibir IC OLS como condicional à janela, Ceq e modelo; não chamar de incerteza total.
- Avaliar perturbação de Ceq, extremos da janela, sonda e autocorrelação separadamente.
- Sem medir essas fontes, não interpretar IC estreito como validação do ensaio.
- Critério ideal de engenharia: ≤1% de erro em casos analíticos identificáveis; paridade do kernel ideal congelada em 10⁻⁶ h⁻¹.
- Casos adversos devem retornar recusa correta mesmo com R² alto.
- Validar cobertura/viés com verdade sintética e novos experimentos; resultados legados não são verdade independente.

## 6. Discrepância bibliográfica

O contrato local Damiani informa `111(9):1873–1877`. O registro primário [PubMed, PMID 24838309](https://pubmed.ncbi.nlm.nih.gov/24838309/) identifica **2014;111(10):2120–2125**, DOI **10.1002/bit.25258**, autores Andrew L. Damiani, Min Hea Kim e Jin Wang. Usar DOI/PMID e os dados do registro primário nas citações do aplicativo; preservar o texto local como proveniência da discrepância, sem editar o projeto científico nesta etapa.

O contrato local de Damiani é reimplementação de equações, não reprodução numérica da tabela experimental, e demanda informação de headspace ausente no corpus. Não usar sua presença no código como evidência de método biótico pronto.

## 7. Execução e critérios de fechamento

Na raiz do repositório:

```powershell
python Windows_app/tools/kla_e0_reference.py verify
dotnet test Windows_app/tests/OpenTECHub.Tests/OpenTECHub.Tests.csproj --filter FullyQualifiedName~KlaE0ReferenceTests
```

A captura só é permitida uma vez por versão; o script recusa sobrescrever baseline existente. A verificação não exige acesso ao OneDrive científico, não executa modelo neural e não se comunica com equipamentos.

Fechamento técnico: hashes íntegros, separação de grupos, leitura v1/v2 e regressões analíticas aprovadas. Fechamento operacional: resposta do responsável com limites e sonda, seguida de revisão do envelope. **O E0 completo permanece com esse item pendente; E1 de contratos pode avançar sem habilitar execução biótica.**
