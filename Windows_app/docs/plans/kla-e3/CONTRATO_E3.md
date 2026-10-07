# E3 — núcleo determinístico

Data: 06/10/2026. Versão: **OpenTecDeterministicKlaV1**. Implementação C# pura, sem Python/rede neural no aplicativo.

## Entrada comum e ligação com E2

`IKlaDeterministicAnalysisEngine.AnalyzeDeterministic` recebe `KlaDeterministicRequest`. Captura única e múltipla usam exatamente esse núcleo; nenhuma condição exige mapa. `KlaDeterministicRequestFactory.FromRun` converte o CSV e os eventos confirmados de uma corrida.

OD científico é `CalibratedDoPercent`, original, sem suavização adicional. No CSV atual isso corresponde a `DOFiltered` (o runner grava ali `OxygenCalibrated`). `DORaw` é ADC e fica apenas como metadado. OD zero é válido: não existe substituição de zero por ADC.

E2 grava `transicoes-gas.json` com origem temporal e evidência de eco. Arquivos antigos sem eventos continuam na revisão legada; a ausência não autoriza inventar confirmação. O adapter exclui observações posteriores ao início da restauração, pois elas pertencem ao cultivo, não à condição do teste.

Todos os valores de exemplo do cultivo são padrões editáveis. O núcleo não contém N=50–1000, Q=0,5–16, OD usual=30–100 ou alvo=5–20 como limites científicos fixos. Critérios numéricos possuem configuração própria, persistida por análise; seus padrões são candidatos de desenvolvimento, não limites universais.

## Sequência determinística

1. Validar tempos estritamente crescentes, observações novas/válidas, ausência de lacunas acima do prazo escolhido e um único episódio de recuperação. Não interpolar, ordenar, duplicar nem eliminar amostras silenciosamente.
2. Rotular cinco fases pelos eventos confirmados e regras causais. Transiente usa duração configurada e, quando τ é conhecido, pelo menos 3τ. Patamar usa janela passada, duração e histerese. Rótulo respiratório é candidato; não valida consumo sozinho.
3. Aceitar correção manual de fases/janelas somente com origem registrada. A correção não pode inverter a direção de gás confirmada. Reanálise gera nova revisão pela proteção de histórico de E1.
4. Estimar Ceq em recuperação + patamar posterior imediato. Ajustar `Ceq − A·exp(−κt)` por projeção variável: mínimos quadrados ponderados de Ceq/A e busca determinística de κ. Não reportar κ como kLa final.
5. Usar pesos `1/sigma²`, com sigma decrescendo linearmente de 5 a 1 por padrão, compatível com `linear_ramp` da referência. Registrar configuração, limites, janela, convergência, método e erro padrão com os três parâmetros livres. Inflar erro padrão por correlação residual positiva.
6. Recusar sinal plano, solução na fronteira, covariância singular ou baixa informação. Não usar máximo observado nem Ceq=100 como fallback. Ceq manual deve ser explícito e torna a taxa condicionada à referência fornecida.
7. Avaliar janelas contíguas da recuperação: duração, pontos, força motriz, amplitude/ruído, R², erro da inclinação, autocorrelação, inclinações em subintervalos e sensibilidade a Ceq/extremos.
8. Buscar extremos em uma grade limitada e guardar todos os candidatos avaliados e motivos. Selecionar a maior duração qualificada, depois amplitude/ruído e índice inicial. Não escolher pelo kLa mais conveniente. Janela manual passa pelos mesmos critérios.
9. Calcular a leitura final por OLS de `ln(Ceq − OD)` **nos valores calibrados originais**. Um déficit inválido recusa a janela inteira.

Os resultados guardam entradas, eventos, configuração, fases/origem, candidatos, janela escolhida, regressões, diagnósticos e motivos. Não há remoção automática de outliers.

## OUR, balanço e unidades

```text
OUR [pp/h] = −3600 · slope(OD versus t)
OUR [mmol/L/h] = OUR [pp/h] · Cref [mmol/L] / 100
kLa [h⁻¹] = −3600 · slope(ln(Ceq − OD) versus t)
Ceq = C* − OUR/kLa                 [OUR e kLa nas mesmas unidades de tempo]
diagnóstico de balanço = [3600·dOD/dt + OUR(pp/h)] / (C* − OD)
```

OUR usa o trecho respiratório após transiente. O núcleo recusa stripping por N₂, inclinação não negativa, curvatura/limitação aparente, sinal insuficiente, mudanças de condição e correlação excessiva. OUR e kLa têm qualidade independente.

Transferência residual não verificada produz **consumo aparente/condicionado**, nunca OUR validado. Mudança de agitação entre remoção e recuperação exige declarar/verificar representatividade do consumo; o núcleo não presume essa equivalência.

C* físico e Ceq respiratório são campos separados. A regressão com Ceq já incorpora OUR constante: não somar OUR novamente. Quando C* tem origem independente e OUR é válido/representativo, conferir `kLa·(C*−Ceq)` contra OUR medido, considerando a tolerância configurada e a incerteza disponível. Incompatibilidade recusa kLa, sem apagar o valor independente de OUR.

Conversão para concentração exige Cref positivo e origem; não herda 0,21 mmol/L do sensor virtual. Ausência de Cref deixa concentração nula. Diagnósticos pontuais usam secante centrada, com bordas indisponíveis e nenhuma média de razões substituindo OLS.

## Sonda, hipóteses e incerteza

Qualquer tecnologia/modelo de sonda é aceita. τ ausente permite calcular taxa **condicionada**, salvo evidência independente de resposta desprezível. τ conhecido demasiadamente lento em relação à taxa estimada retorna não identificabilidade; não ajustar kLa/OUR/C*/τ irrestritamente para forçar identificação.

Este núcleo não autocalibra a sonda nem estima τ por uma cauda possivelmente dominada pelo instrumento. O critério de separação `k·τ` é configurável. Condições de processo não verificadas independentemente também condicionam a leitura.

Intervalos são **OLS condicionais** à janela/equilíbrio e hipóteses; não são cobertura validada da incerteza total, da escolha adaptativa de janela ou da fisiologia. Sensibilidades a Ceq/extremos e autocorrelação são registradas separadamente. Valor recusado permanece nulo no relatório científico; campos numéricos obrigatórios do formato legado são apenas uma projeção de compatibilidade e devem ser apresentados com a qualidade correspondente.

## Paridade e diferenças da referência Python

`tools/kla_e3_reference.py` executou os componentes do projeto local, commit `7d91a7be107e9ce52347d8c69f63e8b341432c00`, e congelou entradas/saídas em `tests/fixtures/kla-e3-python-v1.json`, com hashes das fontes e versões NumPy/SciPy. O gerador recusa sobrescrever uma referência existente.

Paridade verificada: OLS original/erro padrão/R², OUR respiratório e recusa de N₂, Ceq/taxa auxiliar do ajuste exponencial sem suavização. Casos incluem recuperação abiótica, Ceq respiratório=75%, deslocamento temporal e ruído determinístico. OLS usa tolerâncias até 1e−9 na leitura; Ceq até 1e−4 pp e taxa auxiliar até 1e−6 s⁻¹.

Diferenças deliberadas: regras de fase causais em C#, busca limitada com prioridade por duração, ajuste sobre OD original sem Savitzky–Golay e exponencial contínua também no patamar. Não é reprodução integral do seletor Python, dos comparadores publicados nem do método final do artigo. A grade/refinamento determinístico substitui o solver SciPy; amplitude e limites são registrados por configuração, sem ajuste neural.

## Integração, compatibilidade e aceite

O mesmo `KlaAnalysisEngine` disponibiliza a API nova e a API legada. `KlaDeterministicResult.ToRevision` cria revisão versionada com decisão do operador pendente e estado físico não registrado; E2 fornece o estado físico. Salvar análise não opera atuadores nem aceita resultado automaticamente.

A análise legada permanece disponível para arquivos/resultados históricos. A nova API e seu adapter estão prontos para E4; a tela atual ainda chama a revisão legada e não foi convertida integralmente nesta etapa. E4 deve ligar seleção, diagnósticos e qualidade ao layout compartilhado, sem sobrescrever análises antigas.

Testes cobrem paridade congelada, verdade analítica, OUR=1800 pp/h com kLa=72 h⁻¹/Ceq=75%, unidades, zero/ADC distinto, sonda, transferência residual, OUR variável, balanço incompatível, lacunas/NaN/duplicatas, eventos/ciclos, causalidade e persistência da auditoria. Validação científica inicial: 21 testes aprovados. Regressão conjunta: 306 aprovados; verificação final específica: 77 aprovados. Evidências e hashes constam em [validation-receipt.json](validation-receipt.json). Corpus experimental completo, cobertura de incerteza, validação de sondas e ensaios físicos permanecem em E7.
