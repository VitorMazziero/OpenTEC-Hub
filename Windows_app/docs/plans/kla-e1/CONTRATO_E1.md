# E1 — contratos e modelo de sessão

Data: 06/10/2026. Esquema novo: **3**. Base científica: `OpenTecDeterministicKlaV1-E0.1`.

Este contrato incorpora o procedimento descrito pelo usuário após E0. E1 implementa modelos, persistência e a unidade comum de corrida. A execução física biótica será implementada em E2, o cálculo completo em E3 e a interface compartilhada em E4.

Atualização do usuário: todos os valores de exemplo (N/Q, OD, agitação e alvo de remoção) são padrões editáveis. Os valores efetivos pertencem à sessão, não são limites universais nem constantes do algoritmo. Novos campos serão expostos em E4, junto aos controles existentes.

## 1. Procedimento experimental acordado

### Preparação comum

1. Definir protocolo **Abiótico** ou **Biótico** e captura **Única** ou **Múltipla**.
2. Definir a condição de recuperação/medição: agitação N e vazão de ar Q.
3. Aguardar OD estável no equilíbrio inicial, denominado **Ceq inicial**. Registrar N/Q e OD medidos, além dos comandos.
4. Definir o alvo de OD ao final da remoção, escolhido pelo operador na faixa configurada (padrão **5–20%**). O valor exato pertence à sessão; não é inferido do setpoint normal de controle.
5. Durante a remoção, manter agitação baixa configurada (padrão **100 rpm**). A agitação usada para calcular o kLa da recuperação é a condição N definida no item 2; os dois valores são registrados separadamente.

### Abiótico — retirada por N₂

1. Retirar o ar do reator e realizar desoxigenação por **N₂**, usando o arranjo de válvulas já estabelecido no aplicativo.
2. Acompanhar o decaimento até o alvo de OD configurado.
3. Aguardar estabilização de OD na região de baixa concentração, conforme critérios do protocolo abiótico.
4. Preparar o ar no escape e aguardar estabilização da vazão pelo procedimento/algoritmo de válvulas já existente.
5. Comutar o ar estabilizado para o reator e estabelecer a agitação da condição de teste. Registrar pedidos, ecos e transientes.
6. Capturar a recuperação de OD até um equilíbrio final identificável.
7. Selecionar região após transientes em que a regressão log-linear seja adequada e o kLa instantâneo apresente estabilidade.
8. Determinar kLa no balanço **sem consumo biológico**. O decaimento por N₂ não fornece OUR.

### Biótico — retirada por respiração

1. **Não desligar o fluxômetro nem fechar `v_flow`.** Manter a vazão ligada e estável, desviando o ar do reator para o **escape**.
2. Manter **N₂ desligado/fechado** durante todo o procedimento biótico. O escape não pode implicar entrada de N₂ pela linha compartilhada.
3. Manter agitação de remoção em **100 rpm** e registrar o decaimento do OD devido ao consumo até o alvo configurado de **5–20%**.
4. Determinar OUR no trecho respiratório válido, após o transiente. Verificar se transferência residual e alterações fisiológicas impedem interpretar a inclinação como consumo independente.
5. Como o ar/fluxômetro já permanecem ligados e estáveis, **comutar a rota do escape para o reator**. “Religar” significa restabelecer entrada de ar no reator; não reiniciar `v_flow`.
6. Estabelecer N da condição de recuperação, capturar a curva de reoxigenação e distinguir transientes de válvula, mistura e sonda da região válida.
7. Calcular kLa considerando **OUR no balanço**. A segunda ocorrência de “abiótico” na descrição do usuário foi interpretada como “biótico” neste ponto.
8. Selecionar região linear / kLa instantâneo aproximadamente constante e acompanhar estabilização no **Ceq final respiratório**.
9. Concluir a medida, restaurar/confirmar a condição do cultivo e só então apresentar a etapa de revisão operacional.

**Diferença essencial:** no abiótico há stripping por N₂ e preparação/estabilização do ar; no biótico há respiração com ar desviado, fluxo contínuo e N₂ fechado. O modelo de sessão não trata esses procedimentos como variantes de um mesmo comando “fechar todos os gases”.

O arranjo B/C do aplicativo compartilha saída. A implementação E2 deve verificar como a ausência de N₂ é assegurada fisicamente ao usar o escape, sem presumir que um bit de válvula seja suficiente para fechar uma fonte independente.

## 2. Faixas informadas e significado dos critérios

| Parâmetro | Valor informado |
|---|---|
| Agitação de recuperação/condição | 50–1000 rpm |
| Vazão de ar | 0,5–16 L/min |
| OD usual de controle no cultivo | 30–100% |
| Agitação de remoção | 100 rpm |
| Alvo final da remoção | Valor configurável entre 5 e 20% |
| Sonda atual | Polarográfica, resposta descrita como média |
| Constante numérica de resposta | Desconhecida; opcional |

**A faixa de controle de 30–100% não é o limite inferior do ensaio.** O alvo experimental de 5–20% é um campo distinto e autorizado pela descrição do procedimento. Não rejeitar um alvo de 10% porque está abaixo da faixa usual de controle.

O termo anterior “limites biológicos” foi substituído aqui por **critérios de retomada da aeração**: OD alvo/mínimo, queda ou duração que terminam a etapa de remoção. O alvo de OD já foi especificado; valores adicionais de timeout/intercorridas podem ser configurados na etapa operacional. E1 permite preparar e salvar a sessão sem inventá-los.

Qualquer tecnologia/modelo de sonda pode ser registrado. τp não é campo obrigatório para criar a sessão nem requisito genérico para calcular uma curva. A avaliação científica posterior deve distinguir taxa de transferência de atraso instrumental quando este afeta a região selecionada; sonda desconhecida não pode ser tratada automaticamente como ideal. A implementação não restringe ensaios à sonda atual.

## 3. Contratos implementados

| Tipo | Responsabilidade |
|---|---|
| `KlaAssayProtocol` | Abiótico / Biótico |
| `KlaCaptureMode` | Único / Múltiplos, independente do protocolo |
| `KlaProtocolPolicy` | Rota/mecanismo de remoção, manutenção do fluxômetro, N₂ e necessidade de preparação do ar |
| `KlaProtocolSettings` | Faixa usual, agitação/alvo de remoção, sonda e critérios de retomada |
| `KlaAssayDefinition` | Configurações e fila planejada imutáveis |
| `KlaAssayCondition` | Condição planejada sem contadores de execução; preserva proveniência de mapa |
| `KlaRunDefinition` | Snapshot imutável de uma corrida, comum a captura única/múltipla |
| `KlaRunOutcome` | Qualidade kLa/OUR, valores de OUR, decisão do operador e estado físico separados |

Captura única exige uma condição e uma corrida planejada. Uma repetição produz outra corrida/tentativa, não altera o bruto anterior. Captura múltipla admite uma ou mais condições e réplicas; uma matriz de uma condição continua distinta do modo único explícito.

A criação não exige mapa. A faixa `CurrentCultivation` registra os valores informados nesta sessão; ela é aplicada somente quando escolhida na definição, sem restringir retroativamente dados históricos ou outros equipamentos.

O runner abiótico existente executa ambos os modos pela mesma função `StartRunAsync`; a captura única valida correspondência com a condição planejada. Uma sessão biótica pode ser criada/carregada, mas não cai no runner abiótico/N₂ enquanto sua política física não estiver implementada.

## 4. Persistência e compatibilidade

- Novos manifestos usam esquema **3**, com protocolo, modo e parâmetros específicos.
- A estrutura de CSV permanece compatível com v2; E1 acrescenta metadados JSON.
- Na pasta da corrida, `definicao-corrida.json` registra a configuração inicial congelada antes dos comandos. O resumo também carrega a definição quando disponível.
- Alterar a condição/configurações da sessão não modifica o snapshot de uma corrida iniciada.
- v1/v2 continuam abrindo com seu esquema original e valores anteriores. O modo legado é interpretado em memória como múltiplos, sem afirmar que uma seleção explícita foi registrada.
- A natureza legada `Biotico/Biótico/Biotic` é reconhecida; não deve ser transformada silenciosamente em abiótico.
- A qualidade antiga não é convertida retroativamente em validação científica sob o contrato novo. OUR/restauração ausentes permanecem não registrados.
- Versão futura desconhecida é recusada em vez de ser reinterpretada pelo esquema atual.
- `analise-rev-NNN.json` não pode ser sobrescrito com conteúdo diferente. Reanálise usa nova revisão e preserva arquivos anteriores.
- Recarregar uma análise cientificamente válida com decisão pendente não a aceita automaticamente.
- Rejeição pelo operador não apaga a qualidade científica explicitamente registrada; decisão e qualidade são campos distintos.

## 5. Balanço e etapas posteriores

```text
dC/dt = kLa(C*−C)−OUR
Ceq final = C*−OUR/kLa, para consumo constante
ln(Ceq final−C) = a−kLa·t, em região de recuperação válida
```

No abiótico OUR=0. No biótico o consumo deve entrar no balanço, seja explicitamente ao usar C* físico, seja implicitamente pela redução para Ceq respiratório. Não adicionar OUR duas vezes. A estabilidade do kLa instantâneo auxilia a seleção; não substituir avaliação da informação por R² isolado.

Agitação de remoção e recuperação podem diferir conforme a configuração deste protocolo. E3 deve avaliar se o consumo medido permanece representativo durante a recuperação e identificar alterações/transferência residual; dados e transientes devem ficar registrados para essa avaliação.

E2 implementará o comando por rota, confirmação física, aquisição e retomada. E3 implementará identificação de fases, OUR, Ceq/janelas e diagnósticos. E4 apresentará as quatro modalidades na interface. E1 não executa medição biótica ou muda o comportamento físico do protocolo abiótico atual.

Para a integração E3, a revisão atual do ViewModel ainda usa `DORaw` em seus ajustes. A fixture v2 diferencia ADC e OD calibrado para tornar essa ambiguidade testável. A troca explícita para OD calibrado pertence à integração científica; E1 não altera resultados históricos por uma reanálise automática.

## 6. Validação

Casos adicionados cobrem quatro modos sem mapa, extremos da faixa, condição inválida sem criação parcial, snapshot imutável, leitura v1/v2, sonda genérica, ausência de τp, separação de decisão/qualidade/restauração, protocolo do escape e preservação de revisão.

Testes do runner verificam que captura única passa pela unidade de execução atual e que sessão biótica não envia comandos abióticos. Referências E0 permanecem congeladas.

Execução sugerida na raiz:

```powershell
dotnet test Windows_app/tests/OpenTECHub.Tests/OpenTECHub.Tests.csproj --filter 'FullyQualifiedName~Kla'
python Windows_app/tools/kla_e0_reference.py verify
```

Resultados efetivamente executados estão no recibo de validação desta pasta.
