# Plano de implementação — Testes de kLa por Gassing-Out

> **Auditoria da implementação — 2026-08-26:** o fluxo de software foi integrado e corrigido após
> revisão independente do commit `8f1a8ff`. A suíte completa registra 543 testes aprovados, 1
> ignorado e nenhum erro; o aplicativo WPF também alcançou o primeiro frame sem falha XAML. A
> liberação para uso experimental continua condicionada ao ensaio com ESP32-S3 v7 + fluxômetro
> v05, incluindo ACK, perda de comunicação, escolha física de `valve_1`/`valve_2` e parada segura.

## 1. Objetivo

Criar no OpenTEC-Hub uma nova página, **Determinar kLa**, dedicada à execução e à análise
de testes abióticos de transferência de oxigênio pelo método de Gassing-Out. A página deverá:

- executar automaticamente a remoção de O₂ com N₂ e a reoxigenação com ar;
- calcular o kLa pelo método log-linear do balanço de oxigênio dissolvido;
- usar uma região de análise escolhida pelo usuário, sem rede neural ou seleção automática;
- salvar cada teste de forma independente e reprodutível;
- permitir testes com ou sem associação a um mapa de kLa;
- importar condições de um mapa para formar a tabela de teste;
- permitir que a página Mapeamento kLa importe posteriormente os resultados aceitos;
- preservar cada replicata e sua curva bruta, sem depender de recibos de publicação.

Este documento substitui, para esta funcionalidade, a decisão antiga de que o Gassing-Out
dependeria de Torch e deveria ficar fora do app. A implementação descrita aqui não usa Torch,
TCN, redes neurais ou arquivos `.pth`.

## 2. Decisões congeladas

1. A reoxigenação será sempre realizada com **ar**.
2. A remoção de O₂ será realizada com **N₂** conectado a V1 ou V2 do fluxômetro.
3. O usuário escolherá na configuração do teste qual válvula contém N₂.
4. A válvula auxiliar não selecionada ficará fechada em todos os comandos automáticos.
5. Não haverá troca manual de mangueiras durante o teste.
6. O escopo inicial será somente de testes **abióticos**.
7. O parâmetro será chamado de `C_eq`, exibido como `C_eq (aproximadamente C* neste teste
   abiótico)`.
8. OUR não será estimada no escopo inicial; a queda de DO com N₂ será classificada como
   remoção por nitrogênio, nunca como consumo respiratório.
9. A região final de cálculo de kLa será sempre selecionada pelo usuário.
10. O resultado final será obtido por OLS sobre `ln(C_eq - DO)` usando os dados brutos.
11. `kLa_inst` será somente uma série diagnóstica e não será usado como resultado final.
12. Alterações válidas nos limiares durante a execução serão aplicadas imediatamente.
13. Cada linha da tabela terá sua própria quantidade de replicatas.
14. Cada replicata será preservada separadamente.
15. O teste não precisará estar relacionado a um mapa.
16. A importação entre Testes de kLa e Mapas será explícita e baseada em cópias versionadas,
    sem sincronização automática ou estado mutável compartilhado.

## 3. Evidência da auditoria

### 3.1 Rotina antiga da v6

A implementação de referência está em:

`D:\OneDrive\Doutorado_CNPq\_Automacao_Controle\_devices\OpenTEC_control\_Wifi Hub\Software\_Windows App\v.6\kLa_methods\kla_gassing_out_page.py`

Ela fornece referências úteis de fluxo operacional, mas os seguintes comportamentos não devem
ser reproduzidos:

- replicatas globais em vez de replicatas por condição;
- N₂ fixo em V1;
- aviso para troca manual de mangueira;
- gráficos apagados na transição entre N₂ e ar;
- armazenamento somente de DO e `kLa_inst` suavizados durante a reoxigenação;
- média de `kLa_inst` como resultado;
- TCN, arquivos `.pth` e seleção automática de região;
- janela de revisão com somente um gráfico;
- tempo mínimo oculto de 30 s antes de permitir o término.

### 3.2 Firmware atual

Foram considerados:

- Hub ESP32-S3 v7:
  `D:\OneDrive\Doutorado_CNPq\_Automacao_Controle\_devices\OpenTEC_control\_Wifi Hub\Software\_ESP32S3_firmware\OpenTEC_ESP32_v7\OpenTEC_ESP32_v7.ino`;
- Fluxômetro v05:
  `D:\OneDrive\Doutorado_CNPq\_Automacao_Controle\_devices\Fluxometro\Software\__controler_ESP32\flowmeter_OpenTECHUB_V05\flowmeter_OpenTECHUB_V05.ino`.

O protocolo atual já fornece:

- estado desejado completo de vazão e válvulas;
- `cmd_id` revisionado;
- retenção e reenvio até ACK;
- aplicação idempotente no fluxômetro;
- telemetria de V1, V2, `v_Flow`, setpoint e ACK;
- polling de comandos do fluxômetro a cada 100 ms.

Não é necessária uma modificação obrigatória de firmware para a primeira versão. O app deverá,
porém, confirmar o estado completo de gás antes de iniciar a contagem de cada fase.

### 3.3 Núcleo científico

O método deverá seguir:

`D:\OneDrive\Doutorado_CNPq\_Artigos_e_Coorientacoes\Artigos\06_kLa_Modelo\Documentacao\Algoritmo\A04_extensao_Ceq_e_recuperacao_de_ke.md`

e usar como referência de paridade:

`D:\OneDrive\Doutorado_CNPq\_Artigos_e_Coorientacoes\Artigos\06_kLa_Modelo\Algoritmo\klacore\estimation\`

O código Python é uma referência científica e de testes. Ele não será embarcado no aplicativo.

## 4. Estrutura do workspace

### 4.1 Estrutura atual confirmada

As alterações mais recentes estabeleceram uma raiz global de workspace com subpastas físicas em
português, sem acentos:

```text
<Workspace>/
├── Configuracoes/
├── Mapas/
├── Receitas/
├── Logs/
├── Sessoes/
└── Backups/
```

Essa estrutura está declarada em
[`AppPaths`](../src/OpenTECHub/Services/Persistence/AppSettings.cs) e verificada por
[`WorkspaceDirectoryTests`](../tests/OpenTECHub.Tests/WorkspaceDirectoryTests.cs).

### 4.2 Estrutura proposta

Adicionar `Testes-kLa` como uma pasta irmã de `Mapas` e `Sessoes`:

```text
<Workspace>/
├── Configuracoes/
├── Mapas/
├── Testes-kLa/
├── Receitas/
├── Logs/
├── Sessoes/
└── Backups/
```

Responsabilidades:

- `Mapas`: documentos de mapeamento, pontos incorporados, superfícies, gradientes e trajetórias;
- `Testes-kLa`: campanhas de Gassing-Out, curvas brutas, análises e resultados;
- `Sessoes`: registros gerais de processo que não pertencem ao armazenamento científico
  estruturado de um teste de kLa;
- `Logs`: diagnóstico do aplicativo;
- `Backups`: pacotes de backup e restauração.

Os testes de kLa não devem ser armazenados dentro de `Mapas`, pois:

- podem existir sem mapa;
- um mesmo teste pode fornecer pontos para mais de um mapa;
- um mapa pode combinar testes realizados em campanhas diferentes;
- curva bruta e revisão científica têm ciclo de vida diferente da superfície publicada.

### 4.3 Alterações em `AppPaths`

Adicionar:

```csharp
public static string KlaTestsDirectory =>
    Path.Combine(DataDirectory, "Testes-kLa");
```

`EnsureDirectories()` deverá criar essa pasta. A página Configurações deverá exibi-la na lista de
pastas do workspace como **Testes de kLa**, com ação **Abrir pasta**.

O teste automatizado da estrutura deverá esperar sete pastas em vez de seis.

### 4.4 Correção prévia de isolamento dos testes

Durante a auditoria, `workspace.txt` foi encontrado apontando para uma pasta temporária de teste já
removida. `WorkspaceDirectoryTests` chama `AppPaths.InitializeWorkspace()` e persiste o caminho no
perfil real do usuário.

Antes da implementação de Testes de kLa, criar uma forma de inicialização não persistente para
testes, por exemplo:

```csharp
using var workspace = AppPaths.OverrideForTests(tempPath);
```

ou um parâmetro interno `persist: false`. O teste deve restaurar o estado estático anterior e nunca
escrever no `workspace.txt` real. Este é um gate de pré-implementação, porque a nova suíte também
precisará criar estruturas temporárias de `Testes-kLa`.

## 5. Identidade e criação de um teste

### 5.1 Sem seletor de pasta

Não será aberto um Windows Explorer para escolher a pasta de cada teste. O workspace global já
define a raiz de armazenamento.

A página terá o campo obrigatório **Nome do teste**. Ao criar o teste:

```text
<Workspace>/Testes-kLa/<Nome do teste>/
```

será criado automaticamente.

### 5.2 Regra do nome

O nome mostrado na interface deverá corresponder ao nome físico da pasta. Portanto o app não deve
alterá-lo silenciosamente nem acrescentar timestamp automaticamente.

Regras:

- permitir espaços, hífens, sublinhados e caracteres portugueses válidos no Windows;
- recusar `\ / : * ? " < > |`, nomes reservados do Windows, ponto final e espaço final;
- comparar nomes sem diferenciar maiúsculas e minúsculas;
- recusar nome vazio;
- se a pasta já existir, oferecer **Abrir teste existente** ou solicitar outro nome;
- nunca sobrescrever um teste existente;
- bloquear a alteração direta do nome após a primeira corrida;
- uma futura ação explícita **Renomear teste** deverá renomear pasta e referências de forma
  transacional.

### 5.3 Teste independente e associação opcional

O manifesto terá associação opcional:

```text
MapaVinculadoId: Guid?
MapaVinculadoNome: string?
MapaVinculadoFingerprint: string?
```

O vínculo serve para:

- registrar a origem das condições;
- facilitar filtros e importação posterior;
- abrir rapidamente o mapa relacionado.

Ele não torna o mapa proprietário do teste e não provoca atualização automática.

## 6. Estrutura interna de `Testes-kLa`

Uma pasta de teste poderá conter várias condições e replicatas:

```text
Testes-kLa/
└── Teste Meio 1 - Agosto/
    ├── teste.json
    ├── tabela-condicoes.json
    ├── eventos.jsonl
    ├── serie-global.csv
    ├── resumo-resultados.csv
    └── Corridas/
        ├── N0050_Q00p50_Rep01/
        │   ├── dados-brutos.csv
        │   ├── analise.json
        │   └── resultado.csv
        ├── N0050_Q00p50_Rep02/
        │   ├── dados-brutos.csv
        │   ├── analise.json
        │   └── resultado.csv
        └── N0400_Q06p25_Rep01/
            ├── dados-brutos.csv
            ├── analise.json
            └── resultado.csv
```

Nomes físicos permanecerão em português sem acentos nos arquivos de contrato. A interface poderá
mostrar acentos normalmente.

### 6.1 `teste.json`

Deverá conter:

- versão do schema;
- `TestId` estável;
- nome do teste e nome da pasta;
- estado: rascunho, em execução, interrompido ou concluído;
- data de criação, início, última alteração e término;
- natureza inicialmente fixa em `Abiotico`;
- referência opcional ao mapa;
- válvula de N₂ selecionada;
- configurações iniciais;
- revisão atual das configurações;
- versão do app, protocolo e algoritmo;
- lista ordenada de condições e corridas;
- hashes dos arquivos consolidados;
- motivo de interrupção, se aplicável.

### 6.2 `tabela-condicoes.json`

Preserva a tabela editável e sua proveniência:

- `ConditionId` estável;
- N em rpm;
- Qg em L/min;
- número solicitado de replicatas;
- quantidade concluída, aceita, rejeitada e pendente;
- origem manual ou mapa;
- mapa e versão de origem, quando aplicável;
- ordem definida pelo usuário;
- estado da condição.

### 6.3 `serie-global.csv`

Começa antes do primeiro comando e termina somente ao encerrar o teste. Deve incluir:

- purga com N₂;
- espera de ACK;
- troca de gases;
- reoxigenação;
- revisão;
- intervalos entre corridas;
- alterações de configuração;
- abortos e reconexões.

Campos mínimos:

- horário UTC e tempo monotônico;
- `TestId`, `RunId`, `ConditionId` e replicata;
- fase operacional;
- DO bruta/calibrada e DO de exibição;
- Q medida e comandada;
- N comandada;
- V1, V2 e `v_Flow`;
- `FlowCommandId`, `FlowCommandAck` e `FlowCommandPending`;
- revisão da configuração;
- código de evento e motivo de transição.

### 6.4 Pasta de corrida

`dados-brutos.csv` deverá conter toda a corrida, incluindo N₂, intertravamentos e ar. Não salvar
somente o trecho escolhido nem somente séries suavizadas.

`analise.json` deverá conter:

- revisão da análise;
- região de `C_eq`;
- região manual de kLa;
- índices e tempos usados;
- configurações de suavização;
- ajuste auxiliar de `C_eq`;
- OLS final, resíduos e métricas;
- kLa, erros e sensibilidade a `C_eq`;
- flags de qualidade;
- decisão do usuário;
- justificativa de aceitação com advertência ou de rejeição;
- hash de `dados-brutos.csv`.

Reanalisar uma curva cria nova revisão dentro de `analise.json` ou em um histórico de revisões. Um
mapa sempre importará uma revisão identificada, nunca simplesmente “o resultado atual”.

## 7. Importação bidirecional recomendada

### 7.1 Princípio

Recomenda-se o fluxo solicitado, mas como **duas importações unidirecionais explícitas**:

```text
Mapa --cópia de N,Q--> Teste de kLa --resultados aceitos--> Mapa
```

Não criar sincronização ao vivo e não compartilhar a mesma coleção mutável entre as páginas.

Benefícios:

- o teste continua reproduzível mesmo se o mapa mudar;
- o mapa não é alterado durante uma corrida;
- repetir ou reanalisar uma curva não muda silenciosamente uma superfície publicada;
- é possível auditar qual revisão entrou em qual mapa;
- testes independentes continuam possíveis.

### 7.2 Teste importa tabela de um mapa

Na página Determinar kLa, oferecer:

- **Criar tabela manualmente**;
- **Importar condições de um mapa**.

A segunda ação abrirá um seletor interno dos mapas existentes no workspace, não o Windows
Explorer.

Importar do mapa copia somente:

- Qg;
- N;
- identificador do mapa;
- fingerprint da versão usada;
- identificação da condição de origem, quando disponível.

Não copiar:

- kLa já presente;
- superfície;
- gradiente;
- trajetória;
- estado publicado;
- resultados de outras campanhas.

Depois da importação, o usuário poderá:

- escolher quais condições executar;
- alterar o número de replicatas por linha;
- adicionar condições manuais;
- remover condições da campanha;
- ordenar a tabela.

Essas edições não alteram o mapa de origem.

#### 7.2.1 Importar um ensaio completo já realizado

A janela **Importar Teste** também permite selecionar uma pasta externa que contenha o contrato
completo (`teste.json`, `tabela-condicoes.json` e `Corridas/`). A importação:

- copia a pasta para `Testes-kLa/` sem alterar a origem;
- recusa pastas sem manifesto e links de sistema de arquivos;
- reconhece uma campanha já importada pelo `TestId`;
- reconcilia as corridas no disco com o manifesto;
- reconstrói condições, replicatas, kLa e estado a partir de `dados-brutos.csv` e `analise.json`;
- escolhe a tentativa aceita mais recente quando houver mais de uma pasta para a mesma replicata;
- permite abrir cada linha pelo botão de curva, alterar regiões/Ceq e salvar uma nova revisão.

Carregar uma campanha concluída serve apenas para inspeção: não muda seu estado para `Running`.
Uma nova execução só marca a campanha como ativa quando uma condição é efetivamente iniciada.

Conflitos:

- condições duplicadas por `(N,Q)` serão mostradas numa prévia;
- o padrão recomendado é mesclar duplicatas e manter um único `ConditionId` no teste;
- comparar N e Q numericamente, com tolerância de armazenamento declarada;
- ordenar por N crescente e depois Q crescente.

### 7.3 Mapa importa resultados de Testes-kLa

Na página Mapeamento kLa, separar duas ações atualmente conceitualmente diferentes:

- **Importar mapa...**: continua importando um documento `.kla.json`;
- **Importar resultados de testes...**: procura testes dentro de `Testes-kLa`.

O seletor interno deverá mostrar:

- nome e data do teste;
- associação de mapa, se houver;
- estado concluído ou interrompido;
- condições disponíveis;
- replicatas aceitas, advertidas, rejeitadas e já importadas;
- kLa, incerteza, `R²`, revisão da análise e caminho relativo.

O usuário selecionará quais replicatas incorporar.

Recomendação: importar **replicatas individuais aceitas**, não apenas a média consolidada. O mapa
calcula média, desvio-padrão e `n` por condição. Isso preserva variabilidade, permite exclusões
auditáveis e evita pseudo-replicação oculta.

Cada medição importada deverá guardar:

```text
MeasurementId
SourceTestId
SourceRunId
SourceAnalysisRevision
AirflowLpm
AgitationRpm
KlaPerHour
SlopeStandardError
ConditionalConfidenceInterval
AnalysisR2
RawRelativePath
AnalysisRelativePath
RawSha256
ImportedAtUtc
Included
ExclusionReason
```

Deduplicação será feita por:

```text
(SourceTestId, SourceRunId, SourceAnalysisRevision)
```

Se uma análise posterior gerar nova revisão, a interface deverá apresentar:

- manter a revisão já importada;
- substituir pela nova revisão;
- importar como uma revisão alternativa excluída por padrão.

Nenhuma substituição será silenciosa.

### 7.4 Teste sem mapa

Um teste independente deverá permitir:

- montar a tabela manualmente;
- executar todas as condições;
- revisar e salvar resultados;
- ser encerrado sem criar mapa;
- ser importado futuramente por qualquer mapa compatível.

Um vínculo a mapa poderá ser adicionado depois, mas continuará sendo metadado opcional.

### 7.5 Teste relacionado a mapa

Quando a tabela veio de um mapa, o teste registrará a relação, mas o retorno dos resultados ainda
exigirá a ação explícita **Importar resultados de testes...** na página Mapeamento kLa.

Isso é preferível à atualização automática porque publicar um mapa o torna disponível para
controle. Uma aquisição experimental não deve modificar silenciosamente dados que alimentam um
controlador.

## 8. Modelo de dados

### 8.1 Domínio de Testes-kLa

Criar modelos independentes dos modelos de superfície:

```text
KlaTestDocument
KlaTestSettings
KlaTestCondition
KlaTestRun
KlaAnalysisRevision
KlaResultSummary
KlaMapReference
```

O teste é proprietário das curvas e análises. O mapa é proprietário das medições que decidiu
incorporar, das superfícies e das trajetórias.

### 8.2 Domínio do mapa

Estender o documento do mapa com registros de medição/proveniência, mantendo `KlaAnchor` como
entrada agregada do cálculo de superfície.

Fontes possíveis:

```text
Manual
TesteKla
Migrado
```

Ao agrupar replicatas por `(N,Q)`:

- calcular média aritmética;
- calcular desvio-padrão amostral quando `n > 1`;
- mostrar `n`;
- enviar uma média por condição para a superfície;
- manter cada medição individual no documento.

### 8.3 Invalidação científica

Importar, remover ou substituir uma medição deverá:

- modificar o fingerprint dos dados experimentais;
- marcar superfície e trajetória como desatualizadas;
- preservar o último perfil que já estava disponível para controle;
- exigir novo cálculo e nova publicação antes de alterar o controle.

Publicar significa **disponibilizar para controle**. Não criar recibo na interface.

## 9. Nova página e navegação

Adicionar a entrada lateral **Determinar kLa** ao lado de **Mapeamento kLa**.

A página terá:

1. Cabeçalho operacional;
2. painel lateral de teste, tabela e configurações;
3. três gráficos verticais sincronizados;
4. drawer lateral de revisão.

### 9.1 Cabeçalho

Mostrar continuamente:

- nome e estado do teste;
- caminho `Testes-kLa/<Nome>`;
- mapa relacionado ou `Sem mapa associado`;
- condição e replicata atuais;
- fase operacional;
- DO atual e limiares;
- gás comandado e confirmado;
- Q e N;
- rotação de desgaseificação;
- válvula de N₂;
- estado de gravação.

### 9.2 Painel de criação

Campos e ações:

- Nome do teste;
- Natureza: `Abiótico` nesta primeira versão;
- Válvula do N₂: V1 ou V2;
- **Criar tabela manualmente**;
- **Importar condições de um mapa**;
- observações;
- **Criar teste**.

O diretório e `teste.json` devem existir e estar graváveis antes de qualquer comando físico.

### 9.3 Tabela de condições

| Estado | N (rpm) | Qg (L/min) | Replicatas | Concluídas | kLa médio | Origem |
|---|---:|---:|---:|---:|---:|---|
| Pendente | 50 | 0,50 | 3 | 0 | — | Manual |
| Pendente | 50 | 6,25 | 2 | 0 | — | Mapa Meio 1 |

Regras:

- replicatas por linha;
- ordenação numérica N crescente, depois Q crescente;
- Q e N da corrida ativa congelados quando o ar for confirmado;
- condições futuras permanecem editáveis;
- o usuário pode repetir, rejeitar, pular ou encerrar.

## 10. Protocolo de gases

Adicionar:

```text
NitrogenValve = Valve1 | Valve2
```

V1 e V2 deverão ser tratadas como saídas auxiliares genéricas. O significado físico vem da
configuração do teste.

| Estado | `flowSetpoint` | V1 | V2 | `v_Flow` |
|---|---:|---:|---:|---:|
| Tudo fechado | 0 | 0 | 0 | 1 |
| N₂ em V1 | 0 | 1 | 0 | 1 |
| N₂ em V2 | 0 | 0 | 1 | 1 |
| Alívio em V1 | Q | 1 | 0 | 0 |
| Alívio em V2 | Q | 0 | 1 | 0 |
| Ar em Q | Q | 0 | 0 | 0 |

As duas linhas de alívio existem apenas em bancadas com a válvula de alívio instalada
logo depois do fluxômetro (§ 11.2). Nelas o gás já passa pelo medidor — `v_Flow = 0` —
mas sai para a atmosfera em vez de entrar no reator. A válvula de alívio ocupa a saída
auxiliar que o N₂ não usa; a mesma saída para os dois é recusada.

Não enviar `flowmeterComm` junto do quadro de vazão — ele vai em quadro próprio, ao ligar ou
desligar a malha de aeração, porque é o que o Hub v7 republica como `FlowControlEnabled`
(ver [PROTOCOL §3.1](PROTOCOL.md)).

Toda troca de gás será intertravada:

1. enviar tudo fechado;
2. aguardar ACK e telemetria das três válvulas;
3. enviar o novo estado;
4. aguardar ACK e telemetria correspondentes;
5. somente então iniciar a fase e seu relógio.

A confirmação exige:

- `FlowCommandPending == false`;
- `FlowCommandAck == FlowCommandId`;
- setpoint dentro da tolerância;
- V1 correta;
- V2 correta;
- `v_Flow` correto;
- fluxômetro online;
- telemetria recente.

## 11. Máquina de estados

```text
Criar ou abrir teste
  → conferir tabela
  → pré-voo
  → preparar condição
      ├─ DO <= DO de corte → fechar gases → confirmar → abrir ar
      └─ DO > DO de corte  → fechar gases → confirmar → abrir N₂
                                                   ↓
                                      atingir DO de corte
                                                    ↓
                                       fechar N₂ → confirmar
                                                    ↓
                         atraso mínimo + |dDO/dt| estável por N leituras
                                                    ↓
         ├─ sem alívio → abrir ar → confirmar
         └─ com alívio → abrir alívio em Q → confirmar
                              → |Q_medida − Q| ≤ tolerância por N leituras
                              → fechar alívio (ar entra no reator) → confirmar
                                                   ↓
                                            reoxigenação
                                                   ↓
                              DO máxima ou Parar e analisar
                                                   ↓
                                         fechar gases → revisar
                                                   ↓
                         aceitar / rejeitar / repetir / próxima
```

Estados internos:

- `Idle`;
- `Preflight`;
- `ClosingAllGas`;
- `OpeningNitrogen`;
- `Deoxygenating`;
- `ClosingNitrogen`;
- `WaitingForDOStability`;
- `OpeningVent`;
- `StabilizingVentFlow`;
- `OpeningAir`;
- `Reoxygenating`;
- `StoppingRun`;
- `Reviewing`;
- `Accepted`;
- `Rejected`;
- `PreparingNextRun`;
- `Completed`;
- `Aborting`;
- `Faulted`.

### 11.1 Alterações ao vivo

A engrenagem do card **Limiares de Operação** concentra todos os parâmetros do teste: DO de corte
do N₂, DO final, rotação e válvula do N₂, tempos máximos, atraso/derivada/confirmações pós-N₂,
suavização, `C_eq`, faixa automática e aceite automático. Valores válidos são aplicados ao vivo; a
válvula de N₂ fica bloqueada durante uma corrida para evitar uma troca de linha sem intertravamento.

- DO de corte alterado durante N₂: reavaliar imediatamente e fechar N₂ se o critério já
  estiver satisfeito;
- DO máxima alterada durante ar: encerrar a reoxigenação imediatamente se o critério já estiver
  satisfeito;
- rotação de desgaseificação alterada durante N₂: enviar novo setpoint imediatamente;
- suavização alterada: recalcular somente séries derivadas;
- configuração de `C_eq`: recalcular durante a revisão;
- atraso mínimo, janela/limiar da derivada, número de confirmações e tempo máximo pós-N₂:
  aplicar à espera corrente sem abrir gás antecipadamente;
- toda alteração aceita cria uma revisão registrada nos arquivos.

As transições são progressivas. Alterar DO de corte durante a reoxigenação não retorna o processo
para N₂.

A rotação de alívio, a tolerância de vazão, o número de confirmações e a espera máxima do alívio
seguem a mesma regra: valem para a próxima admissão de ar, sem antecipar a corrente. A válvula
do alívio, como a do N₂, fica bloqueada durante uma corrida.

Não haverá tempo mínimo oculto: o atraso pós-N₂ é explícito e configurável. Após esse atraso, a
inclinação é estimada por regressão linear na janela temporal configurada; ar só abre quando
`abs(dDO/dt)` fica abaixo do limiar pelo número solicitado de leituras consecutivas. Se a espera
máxima expirar, a corrida fecha gases e segue para revisão, sem forçar a abertura de ar.

### 11.2 Estabilização no alívio (montagem opcional)

Ao abrir o fluxômetro no setpoint de ensaio, o medidor entrega um pulso de ar bem acima da
vazão pedida e leva alguns segundos para assentar. Sem tratamento, esse pulso entra no reator
exatamente no instante em que a corrida começa, e o `t₀` do ajuste log-linear cai sobre uma
vazão que não é a declarada.

A montagem opcional resolve isso no hardware: uma válvula de alívio ligada imediatamente após o
fluxômetro, comandada pela saída auxiliar que o N₂ não usa. O comportamento é ligado por
checkbox, porque é bancada secundária e a maioria das montagens não a possui.

Quando ligada, a sequência entre a estabilização pós-N₂ e a reoxigenação passa a ser:

1. abrir a válvula de alívio **e** comandar o fluxômetro na vazão da condição — o pulso sai
   pelo alívio, não pelo reator. A agitação vai para a *rotação de alívio* configurável
   (padrão `50 rpm`, mínimo do motor), **não** para a rotação do ensaio: sem gás borbulhando,
   manter a rotação do ensaio reoxigenaria o meio por aeração superficial e estragaria o `C₀`;
2. aguardar a vazão medida ficar dentro de `± tolerância` (padrão `0,2 L/min`) do setpoint por
   um número configurável de leituras consecutivas — uma excursão zera a contagem;
3. fechar o alívio preservando o setpoint já assentado e comandar a rotação do ensaio. Nenhum
   novo pulso de vazão é gerado, porque o fluxômetro não muda de alvo: apenas o destino do gás
   muda;
4. confirmar o estado de gás e só então iniciar `Reoxygenating`, que é o `t₀` da corrida.

Os pontos das duas fases de alívio são gravados como qualquer outra amostra, mas ficam fora do
ajuste: a análise usa exclusivamente `Reoxygenating`. Se a vazão não assentar dentro da espera
máxima, a corrida fecha os gases e vai para revisão em vez de admitir ar instável. Parar durante
o alívio fecha o fluxômetro pelo mesmo caminho de parada segura das demais fases.

## 12. Propriedade e segurança

Adicionar:

```text
CommandOwner.KlaAssay
```

O ensaio reivindicará aeração e agitação. Também deverá impedir alterações concorrentes na
configuração de oxigênio enquanto depende dessa leitura.

Pré-voo:

- workspace e pasta do teste graváveis;
- Hub conectado;
- sensor de O₂ válido e recente;
- fluxômetro online;
- nenhum comando de vazão pendente;
- cascata desativada;
- receita parada;
- `DOmin < DOmax`;
- Q e N válidos;
- válvula de N₂ selecionada;
- estado inicial de válvulas conhecido.

Abortar em:

- perda do Hub;
- perda do fluxômetro;
- DO inválida ou obsoleta;
- timeout de ACK;
- perda de propriedade;
- falha de gravação;
- estado físico incompatível;
- exceção do serviço.

Abortar envia `FlowSafeStop`, motor zero, registra o motivo e libera a propriedade. A interface não
afirmará confirmação física da rotação porque o firmware atual não devolve eco do motor.

Durante a revisão normal, gases ficam fechados e a agitação permanece na rotação da condição. Ao
finalizar ou abortar o teste, o motor vai para zero.

## 13. Gráficos

Usar três gráficos empilhados e com eixo X sincronizado.

### 13.1 DO

- DO bruta/calibrada;
- DO suavizada somente para exibição;
- linhas de DO mínima e máxima;
- fundo por fase: N₂, espera pós-N₂, intertravamento, ar e revisão;
- marcadores de ACK e alterações de configuração.

A curva não será apagada na troca N₂ → ar.

### 13.2 kLa instantâneo

```text
kLa_inst(t) = 3600 * (dC/dt) / (C_eq - C)
```

- série diagnóstica bruta quando definida;
- série diagnóstica suavizada;
- lacunas onde a força motriz for insuficiente;
- nenhum valor inválido substituído por zero;
- nunca usar a média desta série como kLa final.

### 13.3 Log-linear

```text
y(t) = ln(C_eq - C(t))
```

- pontos derivados da DO bruta;
- reta OLS na região manual;
- região selecionada destacada;
- atualização ao mover limites ou alterar `C_eq`;
- acesso aos resíduos e métricas.

### 13.4 Isolamento entre corridas

- cada corrida terá buffers próprios;
- plottables antigos serão removidos ou desvinculados;
- uma corrida concluída nunca receberá novos pontos;
- a visão padrão mostrará somente a corrida atual;
- **Teste completo** mostrará a série global separadamente;
- comparação de replicatas será opcional.

## 14. Revisão científica

O drawer de revisão terá duas regiões independentes:

1. região usada para estimar `C_eq`, no gráfico de DO;
2. região manual de kLa, refletida em kLa instantâneo e log-linear.

### 14.1 Ajuste de `C_eq`

```text
C_m(t) = C_eq - A * exp(-kappa * t)
```

- ajuste exponencial limitado;
- limite superior padrão de 110%;
- `R²` mínimo padrão de 0,90;
- pesos uniformes, rampa linear ou proporcionais ao tempo;
- ênfase opcional na cauda;
- entrada manual de `C_eq`;
- `kappa` é auxiliar e nunca será reportado como kLa.

Implementação C# determinística, sem SciPy:

- busca limitada em `kappa`;
- para cada `kappa`, mínimos quadrados ponderados para `C_eq` e A;
- refinamento unidimensional;
- covariância calculada pelo Jacobiano final.

### 14.2 kLa final

```text
ln(C_eq - C) = beta_0 + beta_1 * t
kLa = -3600 * beta_1
```

Regras:

- dados brutos;
- intervalo contíguo;
- nenhum ponto removido silenciosamente;
- inclinação negativa;
- força motriz acima do piso;
- Q e N constantes;
- resultado com `R²`, RMSE, resíduos, número de pontos e duração.

Incertezas:

- erro-padrão da inclinação;
- intervalo condicional da regressão;
- sensibilidade separada a `C_eq +/- SE`;
- não descrever o intervalo condicional como incerteza completa do estimador.

### 14.3 Qualidade e decisão

Estados:

- aceitável;
- aceitável com advertência;
- inconclusivo/não identificável.

Resultados matematicamente inválidos não poderão ser importados por um mapa. Resultados com
advertência exigirão justificativa explícita. Não haverá etapa de recibo ou validação separada: a
decisão da revisão e as métricas pertencem à própria análise.

### 14.4 `k_e`

Implementar somente em etapa posterior, como opção experimental:

- mesma região do kLa;
- origem no ACK da entrada de ar;
- resultado separado;
- não incorporado ao mapa;
- marcado como não validado até haver teste independente da resposta do sensor.

## 15. Backup e restauração

`BackupService` deverá incluir `Testes-kLa` recursivamente e preservar CSV, JSON e JSONL. O backup
atual de `Mapas` copia apenas JSON e não cobre os novos dados científicos.

Requisitos:

- backup completo preserva curvas e análises;
- manifesto informa quantidade de testes, corridas e tamanho total;
- restauração nunca sobrescreve silenciosamente um `TestId` existente;
- conflito oferece manter ambos, substituir explicitamente ou cancelar;
- referências do mapa usam caminhos relativos ao workspace;
- mover ou restaurar o workspace mantém os vínculos internos.

Se o volume de dados tornar o backup lento, separar no futuro **Backup completo** e **Backup de
configuração**, mas o padrão científico deve incluir `Testes-kLa`.

## 16. Serviços e componentes propostos

```text
Services/KlaTesting/
├── IKlaTestStore.cs
├── KlaTestStore.cs
├── IKlaTestRunner.cs
├── KlaTestRunner.cs
├── IKlaAnalysisEngine.cs
├── KlaAnalysisEngine.cs
├── KlaTestModels.cs
└── KlaTestFileContracts.cs

ViewModels/
└── KlaDeterminationViewModel.cs

Views/
├── KlaDeterminationView.xaml
└── KlaDeterminationView.xaml.cs
```

Responsabilidades:

- `KlaTestStore`: nomes, pastas, manifestos, recuperação e escrita atômica;
- `KlaTestRunner`: máquina de estados e atuação;
- `KlaAnalysisEngine`: cálculo científico puro, sem WPF e sem hardware;
- `KlaDeterminationViewModel`: coordenação de UI e comandos;
- code-behind: somente integração visual dos gráficos e seletores.

O serviço de execução deverá usar `TimeProvider`, permitindo testes determinísticos sem esperas
reais.

## 17. Sequência de implementação

### Etapa 0 — Corrigir isolamento do workspace

- impedir testes de escrever no `workspace.txt` real;
- restaurar estado estático após cada teste;
- confirmar as sete pastas portuguesas.

Gate: executar `WorkspaceDirectoryTests` sem alterar o workspace configurado do usuário.

### Etapa 1 — Contratos de pasta e modelos

- adicionar `KlaTestsDirectory`;
- criar `KlaTestDocument` e contratos de arquivo;
- validar nomes e conflitos;
- implementar criação, abertura e recuperação.

Gate: round-trip completo de um teste vazio e de um teste interrompido.

### Etapa 2 — Importação Mapa → Teste

- seletor interno de mapas;
- prévia das condições;
- cópia de N e Q com proveniência;
- mesclagem de duplicatas;
- replicatas por linha.

Gate: editar o teste importado não altera o mapa de origem.

### Etapa 3 — Núcleo científico

- portar ajuste de `C_eq`;
- implementar OLS log-linear;
- implementar `kLa_inst` diagnóstico;
- implementar incerteza e qualidade;
- criar fixtures de paridade com `klacore`.

Gate: resultados C# reproduzem fixtures Python dentro das tolerâncias declaradas.

### Etapa 4 — Protocolo e segurança

- generalizar V1/V2;
- adicionar `NitrogenValve`;
- adicionar `CommandOwner.KlaAssay`;
- confirmar estado completo por ACK;
- implementar intertravamento e safe-stop.

Gate: nenhuma aquisição começa sem confirmação física do estado de gás.

### Etapa 5 — Máquina de estados e arquivos

- execução independente da interface;
- configurações ao vivo versionadas;
- gravação incremental;
- fluxo por condição e replicata;
- recuperação de interrupção.

Gate: falha de link ou arquivo produz teste recuperável e hardware em parada segura.

### Etapa 6 — Página e gráficos

- navegação **Determinar kLa**;
- criação manual ou por mapa;
- três gráficos sincronizados;
- drawer de revisão;
- estado operacional permanente.

Gate: nova corrida não reutiliza séries nem seletores da anterior.

### Etapa 7 — Importação Teste → Mapa

- ação separada de importar resultados;
- seleção de replicatas e revisões;
- deduplicação;
- proveniência e caminhos relativos;
- agregação por condição;
- invalidação de superfície/trajetória.

Gate: importar resultado não altera automaticamente o perfil já disponível para controle.

### Etapa 8 — Backup e integração final

- incluir `Testes-kLa` no backup;
- restaurar e resolver conflitos;
- atualizar documentação e roadmap;
- iniciar o WPF real após alterações de recursos e XAML.

Gate: teste → revisão → encerramento → reinício do app → importação pelo mapa preserva todos os
dados e referências.

### Etapa 9 — Comissionamento físico

- testar N₂ em V1;
- testar N₂ em V2;
- registrar latência até ACK;
- verificar ausência de sobreposição de gases;
- desconectar Hub e fluxômetro em cada fase;
- confirmar safe-stop;
- testar DO inicial abaixo e acima da mínima.

## 18. Testes de aceitação

### Persistência e workspace

- criar `Testes-kLa` junto às demais pastas;
- nome da interface igual ao nome da pasta;
- recusar nome inválido, reservado ou duplicado;
- nenhum Explorer aberto na criação do teste;
- teste automatizado não altera `workspace.txt` real;
- recuperar teste interrompido;
- backup e restauração preservam curvas.

### Integração com mapas

- criar tabela manual sem mapa;
- importar N/Q de mapa sem copiar kLa;
- editar tabela importada sem alterar mapa;
- importar resultados de teste não vinculado;
- importar resultados de teste vinculado;
- importar replicatas individuais;
- impedir duplicação da mesma revisão;
- tratar nova revisão explicitamente;
- invalidar superfície sem alterar perfil ativo.

### Execução

- DO inicial abaixo da mínima inicia ar após intertravamento;
- DO inicial acima da mínima inicia N₂;
- N₂ em V1;
- N₂ em V2;
- outra válvula sempre fechada;
- alteração de DO mínima durante N₂ produz transição imediata;
- fechamento de N₂ não abre ar antes do atraso mínimo;
- queda residual ou lag da sonda reinicia a contagem de estabilidade;
- derivada estável pelo número configurado de leituras abre ar;
- expiração da espera máxima pós-N₂ encerra a corrida para revisão sem abrir ar;
- com alívio ligado, o ar só entra no reator depois de a vazão medida assentar na faixa;
- uma excursão de vazão durante o alívio zera a contagem de confirmações;
- fechar o alívio preserva o setpoint assentado e não gera novo pulso;
- a espera no alívio corre na rotação de alívio; a rotação do ensaio só é enviada no fechamento;
- alívio na mesma saída do N₂ recusa a corrida antes de reivindicar atuadores;
- parada durante o alívio fecha o fluxômetro e abre a revisão;
- expiração da espera máxima do alívio encerra a corrida para revisão sem admitir ar;
- alteração de DO máxima durante ar encerra imediatamente;
- alteração de rotação durante N₂ envia novo comando;
- parada manual abre revisão;
- ACK atrasado não inicia relógio;
- ACK incompatível não é aceito;
- perda de link aborta com segurança;
- falha de gravação aborta sem perda silenciosa.

### Ciência e gráficos

- resultado final vem da OLS bruta;
- `kLa_inst` nunca substitui o resultado;
- região inválida é recusada sem remoção silenciosa de pontos;
- curva insuficiente fica inconclusiva;
- alteração de suavização não muda os dados brutos;
- N₂ e ar permanecem visíveis na mesma curva de DO;
- nova corrida não recebe séries antigas;
- três gráficos usam a mesma região e eixo temporal;
- recarregar a análise reproduz o mesmo resultado e hash.
- carregar ensaio concluído não altera seu estado persistido;
- importar pasta completa reconstrói matriz, replicatas e tentativas aceitas;
- trocar de ensaio ou linha limpa as séries anteriores antes de desenhar a curva selecionada.

### WPF

- build e suíte completos;
- testes de binding e comandos;
- lançamento real do app após alteração de XAML;
- inspeção do log em caso de `XamlParseException`;
- teste visual em tema claro e escuro.

## 19. Critérios de conclusão

A funcionalidade estará concluída quando:

1. um usuário criar um teste apenas informando seu nome;
2. a pasta correspondente surgir em `Testes-kLa`;
3. a tabela puder ser manual ou importada de um mapa;
4. V1 ou V2 puder ser usada para N₂;
5. todas as fases aguardarem confirmação física;
6. a curva completa, incluindo N₂, for preservada;
7. o usuário selecionar a região e obter kLa log-linear reprodutível;
8. cada replicata e revisão permanecer separada;
9. um mapa puder importar resultados selecionados com proveniência;
10. o perfil ativo não mudar sem novo cálculo e publicação;
11. backup, restauração e reinício preservarem os dados;
12. não houver Torch, rede neural, TCN, `.pth`, recibo ou troca manual de mangueira.
