# Plano de implementação — calibração contínua em duas faixas do fluxômetro e da bomba externa

**Data:** 2026-09-13
**Estado:** Etapa 1 implementada e testada localmente; as etapas 2–12 e toda validação física continuam pendentes
**Escopo:** aplicativo Windows OpenTEC-Hub, Hub ESP32-S3, firmware do fluxômetro, firmware da bomba peristáltica, simulador, testes e documentação relacionada

## 1. Objetivo

Integrar, de ponta a ponta, calibrações contínuas em duas faixas para o fluxômetro e para a bomba peristáltica externa, preservando as particularidades de cada dispositivo:

1. Tornar editável o ponto de transição entre as curvas.
2. Garantir que não exista salto no valor calculado ao atravessar a transição.
3. Manter compatibilidade explícita com configurações e firmwares anteriores.
4. Criar, somente para a bomba, uma biblioteca de perfis de calibração identificados pelo nome da mangueira.
5. Manter separados os atos de medir, salvar localmente e enviar uma curva ao dispositivo.
6. Entregar a mudança em commits pequenos, verificáveis e ordenados.

## 2. Diagnóstico do estado atual

- O limiar `0.0545 V` está fixo no aplicativo em `FlowCalibrationCurve.SplitVoltage` e no firmware do fluxômetro em `FlowIo.h`.
- O Hub não transporta nem ecoa um limiar de transição do fluxômetro.
- O fluxômetro já ajusta duas curvas. O segmento inferior é ancorado no superior, preservando valor e derivada no limiar fixo.
- A bomba utiliza somente a relação linear `Q = slope · S + intercept`, tanto para converter vazão em velocidade quanto para estimar vazão e volume.
- Os pontos volumétricos da bomba são persistidos nas configurações do aplicativo. Cada aplicação confirmada também gera um recibo JSON.
- A biblioteca de perfis de tara dos ensaios de potência é a referência de interação e persistência para selecionar, criar, carregar, sobrescrever e excluir perfis.
- `COMANDOS_DISPOSITIVOS_EXTERNOS.md` ainda registra a decisão de manter a bomba linear. A nova implementação deverá substituir explicitamente essa decisão, sem apagar o histórico.

## 3. Decisões matemáticas

### 3.1 Fluxômetro

O valor `0.0545` não representa vazão; representa a tensão usada para selecionar o segmento da curva. A variável deverá ser denominada:

```text
flowTransitionVoltage
```

- Unidade: volts.
- Valor inicial e valor de migração: `0.0545`.
- Intervalo válido no dispositivo: `0 < Vt < 3.3 V`.

A curva permanece com a estrutura:

```text
V <= Vt: curva inferior quártica
V >  Vt: curva superior quadrática
```

O ajuste do segmento inferior continuará ancorado no segmento superior em `Vt`. Quando a curva estiver completa, devem ser preservadas:

- continuidade de valor: `Qbaixo(Vt) = Qalto(Vt)`;
- continuidade da primeira derivada: `Q'baixo(Vt) = Q'alto(Vt)`.

O valor exatamente igual a `Vt` pertence ao segmento inferior, mantendo a convenção atual.

#### Apresentação no aplicativo

A aba do fluxômetro deverá mostrar:

- campo editável **Tensão de transição (V)**;
- valor somente leitura **Vazão calculada na transição (L/min)**;
- quantidade de pontos válidos em cada segmento;
- salto calculado no limiar;
- linha vertical móvel em `Vt` no gráfico;
- mensagem clara quando um dos segmentos não tiver pontos suficientes.

### 3.2 Bomba peristáltica

Para que a vazão de transição seja a variável editável solicitada, a curva da bomba deverá ser parametrizada por um ponto comum `(St, Qt)`:

```text
Q(S) = Qt + mbaixo · (S - St), para S <= St
Q(S) = Qt + malto  · (S - St), para S >  St
```

Onde:

- `Qt`: vazão de transição editável, em mL/min;
- `St`: velocidade de transição ajustada, em unidades internas `S`;
- `mbaixo`: inclinação da faixa baixa;
- `malto`: inclinação da faixa alta.

As duas retas passam pelo mesmo ponto `(St, Qt)`. Portanto, a continuidade de valor é garantida por construção. É permitida uma mudança de inclinação no limiar, mas nunca um salto de vazão.

O valor exatamente igual a `St` pertence ao segmento inferior.

#### Ajuste da bomba

O ajuste deverá realizar uma busca limitada de `St`. Para cada candidato, as inclinações são calculadas por mínimos quadrados em torno do ponto fixo `(St, Qt)`, e a solução com menor soma de resíduos válida é selecionada.

Condições mínimas:

- `Qt > 0`;
- `0 < St < 1000`;
- inclinações positivas e finitas;
- pelo menos dois pontos com velocidades distintas em cada lado da transição;
- três ou mais pontos por faixa recomendados;
- presença de pontos próximos da transição;
- recusa se a solução extrapolar a faixa calibrada, ficar subdeterminada ou produzir conversão não monotônica.

#### Migração da reta atual

A calibração linear existente deverá ser convertida sem alterar o resultado numérico:

```text
mbaixo = malto = slope atual
St = 500
Qt = slope atual · 500 + intercept atual
```

Como as duas inclinações começam iguais, a curva migrada reproduz exatamente a reta anterior em toda a faixa.

## 4. Contrato de comunicação

### 4.1 Fluxômetro

#### Aplicativo → Hub

```json
{
  "flowTransitionVoltage": 0.0545,
  "a1": 0.0,
  "b1": 0.0,
  "k1": 0.0,
  "f1": 0.0,
  "c1": 0.0,
  "k2": 0.0,
  "f2": 0.0,
  "c2": 0.0
}
```

#### Hub → nó

```json
{
  "transition_v": 0.0545
}
```

O Hub continuará traduzindo e entregando os coeficientes já existentes no mesmo comando confiável.

#### Nó → Hub → aplicativo

```text
FlowTransitionVoltage
FlowmeterCalCrc
FlowCommandAck
```

#### Aplicação atômica

A curva completa e o novo limiar devem ser validados e aplicados como uma transação. O firmware deve rejeitar o quadro inteiro quando houver:

- limiar fora de `0 < Vt < 3.3 V`;
- qualquer coeficiente não finito;
- curva completa com descontinuidade superior à tolerância definida no protocolo;
- novo limiar sem os dois segmentos completos no mesmo quadro.

Comandos antigos que não tragam `flowTransitionVoltage` continuam usando o limiar armazenado. Uma migração de firmware inicializa esse campo em `0.0545 V`.

### 4.2 Bomba peristáltica

#### Aplicativo → Hub → nó

```json
{
  "pumpSlopeLow": 0.028,
  "pumpSlopeHigh": 0.030,
  "pumpTransitionSpeed": 500.0,
  "pumpTransitionFlow": 15.76
}
```

#### Nó → Hub → aplicativo

```text
PumpSlopeLow
PumpSlopeHigh
PumpTransitionSpeed
PumpTransitionFlow
PumpCalibrationCrc
```

Os quatro parâmetros formam uma única calibração e devem ser validados e aplicados atomicamente. O aplicativo somente considera a aplicação confirmada quando receber o eco completo correspondente ao pedido. Aceitação do despacho ou ACK isolado não confirma a curva.

#### Compatibilidade

- Firmware novo continua aceitando `pumpSlope` e `pumpIntercept`.
- Um comando legado converte a calibração para duas inclinações iguais e recalcula `(St, Qt)`.
- O aplicativo novo não deve reduzir silenciosamente uma curva dupla para um firmware 3.10.
- Em bomba anterior à 3.11, a edição e o envio da curva dupla ficam bloqueados com mensagem de atualização necessária.
- Uma calibração nova deve ser recusada enquanto a bomba estiver executando ou aguardando um perfil, para que a integração de volume não mude no meio do ciclo.

## 5. Persistência nos firmwares

### 5.1 Fluxômetro

- Promover o firmware para v12.
- Promover a EEPROM de schema v6 para schema v7.
- Acrescentar `transition_v` ao registro e ao CRC.
- Migrar v6 → v7 preservando `a1..c2`, PI, feedforward, rampa, `dac_hold` e `max_flow`.
- Inicializar somente o novo campo com `0.0545 V`.
- Expor o valor em `/calibration`, telemetria direta e push ao Hub.
- Remover o literal `0.0545f` do caminho de avaliação em `FlowIo.h`.

### 5.2 Bomba

O novo registro não deverá simplesmente aumentar `PumpConfig`. O carregamento atual exige que o blob tenha exatamente o tamanho da estrutura; acrescentar campos diretamente faria instalações 3.10 voltarem aos padrões e perderem perfil, PID e calibração.

Implementar um registro de calibração versionado e separado no NVS, contendo:

- magic/schema;
- `slopeLow`;
- `slopeHigh`;
- `transitionSpeed`;
- `transitionFlow`;
- CRC real do registro.

Na primeira inicialização sem esse registro, o firmware cria a curva dupla a partir de `g_config.pumpSlope` e `g_config.pumpIntercept`. O blob operacional atual permanece intacto, preservando perfis de dosagem, PID e checkpoints.

## 6. Perfis de calibração de mangueira

Os perfis existirão somente na biblioteca do aplicativo. O firmware da bomba mantém apenas uma calibração ativa por vez.

### 6.1 Diretório

```text
<Workspace>\Calibracoes\BombaExterna\Perfis\<nome>.json
```

### 6.2 Conteúdo do perfil

```text
schemaVersion
profileId
name
createdUtc
modifiedUtc
transitionFlowMlMin
transitionSpeedUnits
lowSlope
highSlope
calibrationPoints[]
fitStatistics
algorithmVersion
optionalNotes
lastAppliedUtc
lastAppliedPumpFirmware
```

Cada ponto registra velocidade, duração, volume coletado, vazão calculada e instante de aquisição. O perfil também congela os coeficientes e as estatísticas do ajuste para auditoria.

### 6.3 Regras de armazenamento

- Reutilizar as regras de nomes de arquivo e a escrita atômica usadas pela biblioteca de perfis de tara.
- Não acoplar os DTOs ou diretórios de calibração da bomba aos arquivos de tara.
- Persistir no workspace o ID/nome do último perfil selecionado.
- Um perfil corrompido não pode impedir a abertura ou listagem dos perfis saudáveis.
- Arquivo corrompido deve ser identificado como inválido ou colocado em quarentena, sem exclusão silenciosa.
- O arquivo deve ter `schemaVersion` e migração explícita para revisões futuras.

## 7. Fluxo da interface da bomba

No topo do painel lateral da calibração da bomba, incluir:

- ComboBox **Perfil de mangueira**;
- botão **Carregar**;
- botão **Salvar**;
- botão **Salvar como**;
- botão **Excluir**.

Regras de interação:

1. Selecionar um item apenas altera a seleção; não carrega nem envia automaticamente.
2. **Carregar** substitui os pontos e a curva do editor. Se houver alterações locais não salvas, solicita confirmação.
3. **Salvar** sobrescreve atomicamente o perfil selecionado.
4. **Salvar como** exige nome válido e não sobrescreve um nome existente sem confirmação.
5. **Excluir** exige confirmação.
6. Excluir um perfil não modifica a curva ativa no nó.
7. Trocar, carregar, salvar ou excluir perfil nunca aciona a bomba.
8. **Salvar e enviar curva** permanece a única ação que altera a calibração do nó.
9. Após o eco completo, o recibo registra perfil, curva, pontos, versões de app/Hub/nó e CRC.
10. O controle manual para preencher a mangueira e a aquisição volumétrica permanecem independentes da biblioteca.

## 8. Compatibilidade e capacidades

O aplicativo deverá usar as identidades e versões dos nós para decidir quais recursos liberar:

- fluxômetro v12 ou posterior: limiar editável e eco disponível;
- fluxômetro anterior: mostrar o limiar legado como `0.0545 V`, sem prometer persistência editável;
- bomba v3.11 ou posterior: curva dupla disponível;
- bomba v3.10 ou anterior: mostrar a curva linear ecoada, bloquear envio duplo e explicar a atualização necessária;
- Hub 10.3 ou posterior: encaminhamento e ecos novos disponíveis.

Não realizar downgrade silencioso, aproximação de curva ou confirmação otimista.

## 9. Estratégia geral de execução

A implementação deve avançar da matemática pura para os firmwares, depois para o transporte pelo Hub e somente então para a interface. Essa ordem permite testar cada contrato antes de a próxima camada depender dele.

```text
Modelos matemáticos
        ↓
Firmwares dos nós
        ↓
Hub e protocolo de fio
        ↓
Parser e simulador do aplicativo
        ↓
Persistência local e perfis
        ↓
Interfaces de calibração
        ↓
Integração, documentação e bancada
```

### Regras para todos os commits

1. Inspecionar `git status --short --branch` antes de iniciar a etapa.
2. Preservar alterações e arquivos não rastreados que não pertençam a este plano.
3. Alterar somente os arquivos da etapa em execução.
4. Executar os testes direcionados antes do commit.
5. Executar `git diff --check` e revisar o diff completo.
6. Adicionar ao índice somente os arquivos relacionados à etapa.
7. Não misturar documentação final com implementação intermediária, exceto comentários de contrato indispensáveis ao código.
8. Não fazer squash ao final. Os commits devem permanecer separados, ordenados e incluídos na mesma branch de integração.
9. Não marcar validação física como concluída a partir de compilação, testes ou simulador.

## 10. Etapa 0 — preparar a execução e congelar a linha de base

### Objetivo

Registrar o estado anterior à mudança e impedir que defeitos preexistentes sejam atribuídos à nova calibração.

### Dependências

Nenhuma.

### Procedimento

1. Registrar branch, commit inicial e estado da árvore.
2. Identificar arquivos modificados ou não rastreados preexistentes e excluí-los do escopo.
3. Executar a suíte atual do aplicativo.
4. Compilar em Release o aplicativo, o Hub e os dois firmwares com os mesmos ambientes usados pelo projeto.
5. Abrir o aplicativo atual com workspace isolado diretamente em **Calibrações**.
6. Guardar os logs dessa execução como linha de base, sem incorporá-los ao Git se forem artefatos temporários.
7. Registrar as versões iniciais: Hub 10.2, fluxômetro v11 e bomba v3.10.

### Verificações

- Suíte atual do aplicativo com resultado conhecido.
- Testes de contrato atuais do Hub.
- Compilação dos dois firmwares sem a modificação.
- Ausência de novo crash na abertura da página de calibrações.

### Critério de saída

Existe uma linha de base reproduzível e as alterações preexistentes estão claramente separadas.

### Commit

Nenhum. Esta etapa é somente diagnóstica.

## 11. Etapa 1 — implementar os modelos matemáticos compartilhados

### Objetivo

Retirar o limiar fixo da estrutura matemática do fluxômetro e criar a curva contínua em duas faixas da bomba sem alterar ainda protocolo, firmware ou interface.

### Arquivos principais

- `Windows_app/src/OpenTECHub/Services/Calibration/CalibrationMath.cs`
- novo arquivo de modelo da curva da bomba, caso a separação mantenha `CalibrationMath.cs` pequeno;
- `Windows_app/tests/OpenTECHub.Tests/CalibrationTests.cs`
- `Windows_app/tests/OpenTECHub.Tests/PumpCalibrationTests.cs`

### Tarefas — fluxômetro

1. Substituir `FlowCalibrationCurve.SplitVoltage` constante por uma propriedade de instância, por exemplo `TransitionVoltage`.
2. Fazer `Evaluate`, cálculo de descontinuidade e seleção de segmento usarem essa propriedade.
3. Alterar `FitFlowCurve` e `FitLowSegmentContinuous` para receberem `Vt` explicitamente.
4. Manter o valor legado `0.0545` somente como default nomeado de migração, não como regra de avaliação.
5. Validar `Vt` antes do ajuste.
6. Preservar o ancoramento C1 do segmento inferior no superior.

### Tarefas — bomba

1. Criar um tipo imutável para a curva `(mbaixo, malto, St, Qt)`.
2. Implementar `FlowFromSpeed(S)` com seleção pelo `St`.
3. Implementar `SpeedFromFlow(Q)` com seleção pelo `Qt`.
4. Implementar a migração matemática da reta para duas inclinações iguais.
5. Implementar o ajuste limitado de `St` para um `Qt` fornecido pelo usuário.
6. Calcular resíduos por ponto, SSE, RMSE, R² global e estatísticas por segmento.
7. Retornar um resultado estruturado de validação em vez de permitir `NaN`, infinito ou curva não monotônica.

### Testes obrigatórios

- `Vt` diferente de `0.0545` realmente muda a classificação dos pontos do fluxômetro.
- Fluxômetro tem mesmo valor e mesma derivada dos dois lados de `Vt`.
- `V = Vt` usa a curva inferior.
- Bomba tem o mesmo valor nos dois segmentos em `(St, Qt)`.
- `S = St` usa a curva inferior.
- Q→S e S→Q são inversas dentro de tolerância em ambas as faixas.
- Migração da reta reproduz exatamente a resposta anterior em vários valores de `S`.
- Inclinação zero/negativa, poucos pontos, `Qt` inválido e solução fora da faixa são recusados.

### Critério de saída

Os modelos são puros, independentes de UI e protocolo, e todos os testes matemáticos passam.

### Commit

```text
feat(calibration): add continuous dual-range curve models
```

### Registro de implementação — 2026-09-13

Implementado nesta etapa, exclusivamente no aplicativo Windows e em modelos puros:

- `FlowCalibrationCurve` agora carrega `TransitionVoltage` por instância; `0.0545 V` ficou como `DefaultTransitionVoltage` de migração e a API anterior permanece apenas como compatibilidade obsoleta.
- `FitFlowCurve` e `FitLowSegmentContinuous` recebem e validam `Vt`; avaliação, descontinuidade e o ancoramento C1 usam o valor da curva.
- Criados `PumpDualRangeCurve`, `PumpDualRangeMath` e `PumpFitResult`: curva contínua `(mbaixo, malto, St, Qt)`, conversões diretas/inversas, migração exata da reta legada, validação física e estatísticas de ajuste.
- O ajuste da bomba busca `St` dentro das partições possíveis entre as velocidades medidas, além de candidatos de fronteira, e seleciona a solução contínua válida de menor SSE.
- Adicionados testes para classificação e continuidade do fluxômetro, continuidade e inversão da bomba, recuperação de uma curva dupla conhecida, rejeições e migração linear.
- Verificação executada: `dotnet test Windows_app/tests/OpenTECHub.Tests/OpenTECHub.Tests.csproj --filter "FullyQualifiedName~CalibrationTests|FullyQualifiedName~PumpCalibrationTests" --no-restore` — 45 testes aprovados.

Não implementado nesta etapa: protocolo, Hub, firmware, persistência de perfis, controles de interface, simulador e validação física. Esses itens permanecem explicitamente nas etapas seguintes.

## 12. Etapa 2 — tornar a transição persistente no firmware do fluxômetro

### Objetivo

Promover o fluxômetro para v12, persistir `Vt`, utilizá-lo na leitura e expô-lo para auditoria.

### Dependências

Etapa 1 concluída, para que o contrato matemático já esteja fechado.

### Arquivos principais

- `External-Devices/fluxometro/firmware/flowmeter/src/core/FirmwareApp.cpp`
- `External-Devices/fluxometro/firmware/flowmeter/src/hardware/FlowIo.h`
- `External-Devices/fluxometro/firmware/flowmeter/src/storage/CalibrationStore.h`
- `External-Devices/fluxometro/firmware/flowmeter/src/protocol/CommandCodec.h`
- `External-Devices/fluxometro/firmware/flowmeter/src/tasks/TaskRuntime.h`
- `External-Devices/fluxometro/firmware/flowmeter/src/core/Lifecycle.h`
- `External-Devices/fluxometro/firmware/flowmeter/src/api/OtaService.h`
- `External-Devices/fluxometro/firmware/flowmeter/src/api/WebSocketApi.h`

### Tarefas

1. Alterar a identidade do firmware para v12.
2. Acrescentar `transition_v` ao fim do registro `CalibrationParams`.
3. Criar magic/schema v7, mantendo os identificadores antigos para migração.
4. Implementar migração específica v6 → v7:
   - preservar todos os campos anteriores;
   - inicializar somente `transition_v = 0.0545f`;
   - recalcular CRC;
   - persistir uma única vez.
5. Validar defensivamente o limiar carregado antes de copiá-lo para a variável ativa.
6. Substituir o literal em `FlowIo.h` pela variável ativa.
7. Estender a área de staging do parser com `transition_v`.
8. Se o quadro contém `transition_v`, exigir os dois segmentos completos.
9. Antes da aplicação, calcular o valor e a derivada dos segmentos em `Vt` e rejeitar descontinuidade fora das tolerâncias do protocolo.
10. Aplicar limiar e coeficientes sob o mesmo mutex e realizar uma única gravação na EEPROM.
11. Incluir `transition_v` no CRC da calibração.
12. Incluir o limiar em:
    - `/calibration`;
    - push `/flowData`;
    - telemetria serial/WebSocket;
    - ACK direto, quando aplicável.
13. Garantir que comandos antigos sem `transition_v` mantenham o limiar persistido.

### Testes obrigatórios

- Compilação do firmware no alvo ESP32 utilizado pelo projeto.
- Teste ou contrato estático cobrindo ausência do literal operacional `0.0545f` em `FlowIo.h`.
- Migração v6 → v7 preserva byte a byte os campos antigos relevantes.
- Frame completo válido aplica todos os campos.
- Frame com limiar inválido ou curva descontínua não altera nenhum campo.
- Reenvio do mesmo `cmd_id` continua idempotente.
- `/calibration` e telemetria mostram o limiar persistido e o CRC atualizado.

### Critério de saída

O firmware compila, migra sem perda, usa `Vt` dinâmico e nunca aplica parcialmente uma curva nova.

### Commit

```text
feat(flowmeter): persist configurable calibration transition
```

## 13. Etapa 3 — implementar a calibração dupla no firmware da bomba

### Objetivo

Promover a bomba para v3.11 e substituir a reta operacional por uma curva contínua de duas faixas, preservando configuração, perfil de dosagem, PID e checkpoint existentes.

### Dependências

Etapa 1 concluída.

### Arquivos principais

- `External-Devices/bomba-peristaltica/firmware/peristaltic-pump/src/core/FirmwareApp.cpp`
- `External-Devices/bomba-peristaltica/firmware/peristaltic-pump/src/storage/ConfigStore.h`
- novo módulo de armazenamento da calibração, se necessário;
- `External-Devices/bomba-peristaltica/firmware/peristaltic-pump/src/control/SensorAndConversion.h`
- `External-Devices/bomba-peristaltica/firmware/peristaltic-pump/src/control/OperationController.h`
- `External-Devices/bomba-peristaltica/firmware/peristaltic-pump/src/network/HubClient.h`
- `External-Devices/bomba-peristaltica/firmware/peristaltic-pump/src/protocol/TelemetryCodec.h`
- `External-Devices/bomba-peristaltica/firmware/peristaltic-pump/src/core/Lifecycle.h`

### Tarefas

1. Alterar identidade e diagnóstico para v3.11.
2. Manter o layout de `PumpConfig` v3.10 inalterado.
3. Criar um registro NVS separado para calibração v2 com magic/schema e CRC real.
4. Na ausência do registro novo:
   - ler `pumpSlope` e `pumpIntercept` legados;
   - configurar `mbaixo = malto = pumpSlope`;
   - usar `St = 500`;
   - calcular `Qt = pumpSlope · 500 + pumpIntercept`;
   - salvar o novo registro sem alterar os outros campos de `PumpConfig`.
5. Atualizar `mlminToSpeedUnits` para escolher a inclinação por `Qt`.
6. Atualizar `speedUnitsToMlmin` para escolher a inclinação por `St`.
7. Garantir que `pwmDutyToMlmin` use a nova conversão, pois ela alimenta `PumpFlow` e `PumpVol`.
8. Criar staging dos quatro campos da calibração nova.
9. Exigir os quatro campos no mesmo quadro.
10. Recusar valores não finitos, inclinações não positivas, `St` fora de `(0,1000)` e `Qt <= 0`.
11. Recusar alteração durante `OP_RUNNING` ou `OP_WAITING`.
12. Aplicar e persistir atomicamente os quatro campos.
13. Manter `pumpSlope` + `pumpIntercept` como comando legado, convertendo-o em duas inclinações iguais.
14. Ecoar os quatro campos e o CRC no push ao Hub e em `/readData`.
15. Preservar parada manual, `speed_ms`, potenciômetros, integração de volume e retomada de perfil.

### Testes obrigatórios

- Compilação do firmware no alvo usado pela bomba.
- Migração da reta mantém a mesma Q para `S = 0, 1, 250, 500, 750, 1000`.
- Migração não altera modo, pontos de perfil, PID ou checkpoint.
- Q→S e S→Q usam a faixa correta.
- Volume integrado atravessa `St` sem salto.
- Frame incompleto ou inválido não modifica a calibração.
- Calibração durante perfil ativo é recusada.
- Comando legado continua funcionando e resulta em duas inclinações iguais.
- Reboot mantém curva e CRC.

### Critério de saída

A bomba v3.11 executa a curva dupla com continuidade e compatibilidade, sem perda de nenhuma configuração v3.10.

### Commit

```text
feat(pump): support continuous dual-range calibration
```

## 14. Etapa 4 — integrar os contratos no Hub

### Objetivo

Fazer o Hub 10.3 transportar e ecoar os novos campos mantendo as garantias atuais de fila confiável, ACK e presença.

### Dependências

Etapas 2 e 3 concluídas.

### Arquivos principais

- `ESP32S3-HUB/ESP32S3-HUB/Config.h`
- `ESP32S3-HUB/ESP32S3-HUB/src/core/AppContext.h`
- `ESP32S3-HUB/ESP32S3-HUB/src/protocol/Mailboxes.h`
- `ESP32S3-HUB/ESP32S3-HUB/src/protocol/Commands.h`
- `ESP32S3-HUB/ESP32S3-HUB/src/network/HttpServer.h`
- `ESP32S3-HUB/ESP32S3-HUB/src/sensor/Telemetry.h`
- `ESP32S3-HUB/tests/contracts/test_node_commands.py`
- `ESP32S3-HUB/tests/contracts/test_http_frames.py`
- `ESP32S3-HUB/tests/contracts/test_json_keys.py`

### Tarefas — fluxômetro

1. Aceitar `flowTransitionVoltage` no quadro do aplicativo.
2. Armazenar valor desejado e flag pendente sob `cmdMutex`.
3. Traduzir para `transition_v` no comando do nó.
4. Manter limiar e coeficientes no mesmo payload e na mesma revisão.
5. Ler `transition_v` no `/flowData` e publicar `FlowTransitionVoltage`.
6. Preservar `FlowCommandAck`, reentrega e reimposição após reboot.

### Tarefas — bomba

1. Acrescentar os quatro campos novos à whitelist da bomba.
2. Repassá-los juntos pela `pumpBox` sem renomeação acidental ou truncamento.
3. Ler os quatro ecos e o CRC no `/pumpData`.
4. Publicar os ecos apenas depois de o nó tê-los informado neste boot.
5. Não manter valores antigos como se fossem eco atual quando o nó estiver offline.

### Testes obrigatórios

- `python ESP32S3-HUB/tools/verify_contract.py`.
- Suíte de `ESP32S3-HUB/tests/contracts`.
- Payload do fluxômetro contém todos os campos na mesma revisão.
- Payload da bomba contém os quatro campos na mesma mailbox.
- ACK incorreto não fecha a fila.
- Nó offline não produz eco falso.
- Nó legado continua gerando telemetria sem os novos campos.
- Compilação do Hub sem aumento de payload acima das reservas documentadas.

### Critério de saída

Os contratos novos atravessam o Hub sem alterar o comportamento dos nós legados ou das filas existentes.

### Commit

```text
feat(hub): relay dual-range calibration contracts
```

## 15. Etapa 5 — atualizar protocolo, parser e simulador do aplicativo

### Objetivo

Representar os novos comandos e ecos de forma tipada no aplicativo antes de ligá-los à interface.

### Dependências

Etapa 4 concluída.

### Arquivos principais

- `Windows_app/src/OpenTECHub.Protocol/CommandKeys.cs`
- `Windows_app/src/OpenTECHub.Protocol/CommandBuilders.cs`
- `Windows_app/src/OpenTECHub.Protocol/TelemetryParser.cs`
- `Windows_app/src/OpenTECHub.Protocol/SensorReadings.cs`
- `Windows_app/src/OpenTECHub.Simulator/DeviceModel.cs`
- `Windows_app/src/OpenTECHub.Simulator/WireCodec.cs`
- `Windows_app/tests/OpenTECHub.Tests/WireFormatTests.cs`
- `Windows_app/tests/OpenTECHub.Tests/TelemetryParserTests.cs`
- `Windows_app/tests/OpenTECHub.Tests/TelemetryTests.cs`
- testes de simulador relacionados aos nós externos.

### Tarefas

1. Declarar as chaves novas de comando e telemetria.
2. Criar builders atômicos:
   - curva completa do fluxômetro com `Vt`;
   - curva completa da bomba com `(mbaixo, malto, St, Qt)`.
3. Validar finitude e faixas no builder, antes de serializar.
4. Acrescentar propriedades anuláveis aos snapshots para diferenciar ausência de zero.
5. Fazer o parser limpar ecos não presentes no quadro atual, evitando estado pegajoso.
6. Atualizar o simulador moderno para aceitar, aplicar e ecoar as curvas.
7. Criar cenário legado sem os novos ecos.
8. Simular ACK separado do eco, para testar a confirmação não otimista.
9. Atualizar golden strings sem modificar chaves antigas.

### Testes obrigatórios

- Serialização exata dos dois comandos completos.
- Builders recusam `NaN`, infinito e faixas inválidas.
- Parser lê todos os ecos novos.
- Campos ausentes resultam em `null`, não em valores anteriores.
- Simulador aplica curva válida e preserva a anterior após quadro inválido.
- Cenário legado permanece funcional.

### Critério de saída

O protocolo do aplicativo representa fielmente os contratos dos firmwares e permite testar a integração sem hardware.

### Commit

```text
feat(protocol): expose dual-range calibration telemetry
```

## 16. Etapa 6 — implementar o armazenamento de perfis da bomba

### Objetivo

Criar a biblioteca local, versionada e atômica de perfis de calibração por mangueira, ainda sem alterar o XAML.

### Dependências

Etapas 1 e 5 concluídas.

### Arquivos principais

- novos modelos em `Windows_app/src/OpenTECHub/Services/Calibration`;
- nova interface e implementação de store em `Windows_app/src/OpenTECHub/Services/Calibration` ou `Services/Persistence`;
- `Windows_app/src/OpenTECHub/Services/Persistence/AppSettings.cs`;
- `Windows_app/src/OpenTECHub/App.xaml.cs` para registro no contêiner;
- novos testes de store em `Windows_app/tests/OpenTECHub.Tests`.

### Tarefas

1. Definir `PumpCalibrationProfile` e `PumpCalibrationProfileSummary`.
2. Definir `IPumpCalibrationProfileStore` com operações de listar, carregar, salvar e excluir.
3. Centralizar o diretório em `AppPaths`.
4. Validar nomes contra caracteres inválidos, nomes reservados do Windows, espaços finais e colisão sem distinção de maiúsculas.
5. Escrever em arquivo temporário e substituir atomicamente o destino.
6. Preservar o arquivo anterior se a serialização ou substituição falhar.
7. Tratar arquivos de schema conhecido e rejeitar schema futuro sem sobrescrever.
8. Isolar arquivo corrompido sem impedir a listagem dos demais.
9. Guardar no settings somente o ID/nome selecionado, nunca uma segunda cópia divergente da curva.
10. Migrar os pontos e a reta atuais para um perfil inicial uma única vez.
11. Tornar a migração idempotente.
12. Registrar o serviço na injeção de dependência.

### Testes obrigatórios

- Criar, listar, carregar, sobrescrever e excluir.
- Recusar nome inválido e traversal de diretório.
- Não sobrescrever sem autorização explícita.
- Manter arquivo anterior quando a escrita falhar.
- Um JSON corrompido não esconde perfis saudáveis.
- Schema futuro é preservado e reportado como incompatível.
- Migração linear cria exatamente um perfil e pode rodar novamente sem duplicar.
- Store respeita o workspace selecionado.

### Critério de saída

A biblioteca funciona isoladamente, sem emitir comandos e sem depender da interface.

### Commit

```text
feat(pump-calibration): add hose profile store
```

## 17. Etapa 7 — tornar a transição do fluxômetro editável no aplicativo

### Objetivo

Integrar `Vt` ao ViewModel, gráfico, persistência e envio da calibração do fluxômetro.

### Dependências

Etapas 1, 4 e 5 concluídas.

### Arquivos principais

- `Windows_app/src/OpenTECHub/ViewModels/FlowCalibrationViewModel.cs`
- `Windows_app/src/OpenTECHub/Views/CalibrationView.xaml`
- `Windows_app/src/OpenTECHub/Views/CalibrationView.xaml.cs`
- `Windows_app/src/OpenTECHub/Services/Persistence/AppSettings.cs`
- `Windows_app/tests/OpenTECHub.Tests/CalibrationTests.cs`
- `Windows_app/tests/OpenTECHub.Tests/CompactLayoutTests.cs`
- `Windows_app/tests/OpenTECHub.Tests/DetailPaneContractTests.cs`

### Tarefas

1. Acrescentar `FlowTransitionVoltage` ao settings com default `0.0545`.
2. Migrar settings antigos pela ausência do campo.
3. Expor texto editável, erro de validação, vazão na transição e contagem de pontos por faixa.
4. Recalcular a curva ao alterar `Vt`, sem persistir ou enviar automaticamente.
5. Preservar a curva anterior quando o texto estiver temporariamente inválido durante digitação.
6. Fazer equações, mensagens e continuidade usarem o limiar corrente.
7. Mover a linha vertical do gráfico para o limiar corrente.
8. Persistir pontos e `Vt` em **Salvar pontos**.
9. Enviar curva completa e `Vt` em **Salvar e enviar curva**.
10. Bloquear envio se a curva estiver parcial ou descontínua no novo contrato.
11. Detectar versão do nó/Hub e desabilitar edição quando a capacidade não existir.
12. Só mostrar confirmação após ACK e eco do limiar; registrar CRC disponível.
13. Manter captura, setpoint de ensaio, ajuste fino e safe-stop atuais.

### Testes obrigatórios

- Edição de `Vt` redistribui os pontos e redesenha o gráfico.
- Texto inválido não destrói a última curva válida.
- Salvar localmente não envia comando.
- Enviar produz um único quadro completo.
- ACK sem eco mantém estado pendente.
- Eco diferente do solicitado gera aviso e não confirma.
- Nó legado bloqueia edição com explicação.
- Layout funciona em 1024 × 640 DIP e DPIs suportados.

### Critério de saída

A calibração do fluxômetro deixa de depender de `0.0545` hardcoded em todo o aplicativo e continua fiel ao fluxo visual existente.

### Commit

```text
feat(flow-calibration): make transition voltage editable
```

## 18. Etapa 8 — integrar curvas e perfis na calibração da bomba

### Objetivo

Substituir o editor linear da bomba pelo fluxo contínuo em duas faixas e incorporar a biblioteca de mangueiras sem reintroduzir cards legados.

### Dependências

Etapas 1, 3, 4, 5 e 6 concluídas.

### Arquivos principais

- `Windows_app/src/OpenTECHub/ViewModels/PumpCalibrationViewModel.cs`
- `Windows_app/src/OpenTECHub/Views/CalibrationView.xaml`
- `Windows_app/src/OpenTECHub/Views/CalibrationView.xaml.cs`
- `Windows_app/src/OpenTECHub/Services/Persistence/AppSettings.cs`
- `Windows_app/src/OpenTECHub/Services/Documentation/DocumentationCatalog.cs`
- `Windows_app/tests/OpenTECHub.Tests/PumpCalibrationTests.cs`
- `Windows_app/tests/OpenTECHub.Tests/CompactLayoutTests.cs`
- `Windows_app/tests/OpenTECHub.Tests/DetailPaneContractTests.cs`

### Organização visual obrigatória

Manter o espelhamento da aba do fluxômetro:

- gráfico grande no painel principal esquerdo;
- resumo e ação da curva abaixo do gráfico;
- um único card lateral de pontos e aquisição;
- controles de perfil no topo do painel lateral;
- ensaio automático e controle manual dentro do painel lateral;
- nenhuma restauração dos cards antigos de telemetria ou pré-visualizações fixas.

### Tarefas — estado e ajuste

1. Substituir os campos lineares por `Qt`, `St`, `mbaixo` e `malto`.
2. Manter `Qt` editável e apresentar `St` como resultado do ajuste.
3. Classificar visualmente pontos de faixa baixa e alta.
4. Mostrar estatísticas globais e por segmento.
5. Desenhar as duas retas, o ponto de transição e os pontos volumétricos.
6. Preservar pontos e última curva válida enquanto o usuário digita um `Qt` inválido.
7. Impedir envio com ajuste inválido, poucos pontos ou bomba em execução.

### Tarefas — perfis

1. Carregar resumos do store sem abrir todos os arquivos.
2. Implementar seleção sem efeito operacional.
3. Implementar **Carregar** com proteção contra alterações locais não salvas.
4. Implementar **Salvar** e **Salvar como** com confirmação de sobrescrita.
5. Implementar **Excluir** com confirmação.
6. Manter a curva ativa no nó quando o arquivo local for excluído.
7. Marcar editor como modificado após alteração de ponto, `Qt` ou metadado.
8. Atualizar `lastAppliedUtc` somente depois do eco completo.

### Tarefas — envio e recibo

1. Enviar os quatro parâmetros em um único comando.
2. Aguardar eco de todos os parâmetros e CRC.
3. Comparar floats com tolerância definida e documentada.
4. Não persistir “aplicado” quando houver apenas aceitação do dispatcher ou ACK.
5. Expandir o recibo com:
   - ID e nome do perfil;
   - pontos e estatísticas;
   - curva solicitada e curva ecoada;
   - CRC;
   - versões do aplicativo, Hub e bomba;
   - instante UTC.
6. Preservar o comportamento de parada devida após perda de conexão.
7. Preservar o controle manual para preenchimento da mangueira sem geração de ponto.

### Testes obrigatórios

- Ajuste correto com pontos dos dois lados de `Qt`.
- Alterar `Qt` refaz o ajuste e o gráfico.
- Carregar perfil substitui o editor, mas não envia.
- Salvar/excluir não aciona bomba nem altera nó.
- Mudança de seleção não perde edição sem confirmação.
- Envio bloqueado em firmware legado ou perfil operacional ativo.
- ACK parcial não confirma.
- Eco completo confirma, persiste e gera recibo correto.
- Perda de conexão mantém a obrigação de parada.
- Controle manual não cria ponto.
- Os textos e cards legados permanecem ausentes.
- Layout e tema passam pelos testes visuais/estruturais existentes.

### Critério de saída

A aba da bomba oferece calibração contínua em duas faixas e biblioteca de mangueiras, mantendo o mesmo padrão visual e operacional da aba do fluxômetro.

### Commit

```text
feat(pump-calibration): add dual-range profile workflow
```

## 19. Etapa 9 — validar a integração ponta a ponta em software

### Objetivo

Verificar app ↔ Hub ↔ simulador/nós e corrigir defeitos de integração antes de atualizar o estado documental.

### Dependências

Etapas 2 a 8 concluídas.

### Procedimento

1. Executar testes direcionados de matemática, protocolo, parser, fluxo, bomba, perfis e layout.
2. Executar todos os contratos Python do Hub.
3. Compilar Hub, fluxômetro e bomba em seus alvos reais.
4. Executar a suíte completa `Windows_app/OpenTECHub.slnx`.
5. Fazer clean e build Release do aplicativo.
6. Abrir o executável Release com workspace temporário e navegação direta para calibrações.
7. Instanciar as duas abas e alternar entre elas em tema claro e escuro.
8. No simulador moderno:
   - editar e enviar `Vt`;
   - verificar ACK e eco;
   - criar dois perfis de bomba;
   - carregar um perfil sem envio;
   - enviar curva e verificar recibo.
9. No cenário legado:
   - confirmar leitura da curva antiga;
   - confirmar bloqueio das funções incompatíveis;
   - confirmar ausência de crash por chaves ausentes.
10. Examinar somente logs gerados após o início desta validação.
11. Corrigir qualquer `Fatal`, `Unhandled`, `XamlParseException`, erro de binding, falha de DI ou violação de thread de UI antes de prosseguir.
12. Reverter screenshots e outros artefatos rastreados modificados apenas pela execução dos testes, salvo quando a atualização de evidência fizer parte do commit documental.

### Critério de saída

- Todos os testes aprovados.
- Todos os componentes compilados.
- Aplicativo Release abre e renderiza as abas sem erro.
- Simulador moderno e legado apresentam os estados esperados.
- Nenhum defeito de runtime permanece aberto.

### Commit

Correções descobertas nesta etapa devem ser incorporadas ao commit funcional responsável, quando isso ainda for seguro e não publicado. Se os commits já estiverem compartilhados, criar commits `fix(...)` separados; nunca esconder uma correção dentro do commit documental.

## 20. Etapa 10 — atualizar documentação e matrizes de estado

### Objetivo

Fazer todos os documentos descreverem o contrato implementado e separar claramente evidência de software de validação física.

### Dependências

Etapa 9 concluída.

### Documento mestre dos dispositivos externos

`External-Devices/docs/COMANDOS_DISPOSITIVOS_EXTERNOS.md`

1. Substituir a decisão de manter a bomba estritamente linear.
2. Atualizar conversões, comandos, persistência, telemetria e versões das seções 1 e 3.
3. Explicar que `0.0545` é tensão de transição, não vazão.
4. Documentar `(mbaixo, malto, St, Qt)` e a continuidade da bomba.
5. Registrar que os perfis ficam no PC e apenas uma curva fica ativa no nó.
6. Acrescentar os novos ensaios aos checklists.
7. Atualizar as tabelas com a política:
   - 🟢 para implementação integrada e testada em software;
   - 🔵 para decisões arquiteturais fechadas;
   - 🟡 para bancada ainda pendente;
   - 🔴 para lacunas abertas.

### Aplicativo Windows

- `Windows_app/docs/PROTOCOL.md`: novas chaves, ecos, compatibilidade e golden strings.
- `Windows_app/docs/processes/CALIBRATION.md`: procedimentos completos das curvas e perfis.
- `Windows_app/docs/UI_DESIGN.md`: disposição e estados da interface.
- `Windows_app/docs/DECISIONS.md`: decisão sobre `Vt`, `(St, Qt)`, continuidade, migração e biblioteca no PC.
- `Windows_app/docs/hardware/HARDWARE_VALIDATION.md`: matriz de ensaios por transição e mangueira.
- `Windows_app/docs/CHANGELOG.md`: mudanças entregues e versões compatíveis.
- `Windows_app/docs/ROADMAP.md`: software concluído e bancada pendente.
- `Windows_app/docs/PONTOS_DE_MELHORIA_EXPOSICAO_NOS.md`: capacidades, ecos e pendências.
- `Windows_app/src/OpenTECHub/Services/Documentation/DocumentationCatalog.cs`: ajuda interna coerente com a interface final.

### Hub ESP32-S3

- `ESP32S3-HUB/docs/WIRE_CONTRACT_V9.md`: tradução app → Hub → nós e ecos.
- `ESP32S3-HUB/docs/VALIDATION.md`: casos de contrato, ACK, persistência e compatibilidade.
- `ESP32S3-HUB/README.md`: Hub 10.3 e capacidades novas.

### Firmware do fluxômetro

- `External-Devices/fluxometro/docs/PROTOCOL.md`: `transition_v`, eco, CRC e EEPROM v7.
- `External-Devices/fluxometro/README.md`: firmware v12 e comportamento da transição.
- `External-Devices/fluxometro/CHANGELOG.md`: migração e compatibilidade.

### Firmware da bomba

- `External-Devices/bomba-peristaltica/docs/PROTOCOL.md`: curva dupla, comandos, ecos e compatibilidade.
- `External-Devices/bomba-peristaltica/README.md`: firmware v3.11 e calibração ativa.
- `External-Devices/bomba-peristaltica/CHANGELOG.md`: registro NVS, migração e campos novos.

### Planos históricos

Planos antigos que descrevem `0.0545 V` como constante imutável devem receber uma nota de supersessão e um link para este documento. Não reescrever resultados históricos como se já tivessem usado o novo limiar.

### Verificações

- Buscar globalmente referências operacionais a `0.0545`, `pumpSlope` e `pumpIntercept`.
- Classificar cada ocorrência remanescente como default legado, compatibilidade, histórico ou erro.
- Executar testes de documentação existentes.
- Conferir versões e nomes de chaves em todos os documentos contra o código.

### Critério de saída

Não há contradição entre documento mestre, protocolos dos nós, contrato do Hub, documentação do app e implementação.

### Commit

```text
docs(calibration): document editable transitions and hose profiles
```

## 21. Etapa 11 — validação física do fluxômetro

### Objetivo

Demonstrar que a transição editável e a continuidade matemática correspondem ao comportamento do equipamento real.

### Dependências

Etapas 2, 4, 7, 9 e 10 concluídas e firmware gravado no equipamento.

### Procedimento

1. Registrar identificação do nó, versões, CRC inicial, padrão certificado, condições ambientais e rota pneumática.
2. Testar inicialmente o valor migrado `Vt = 0.0545 V`.
3. Coletar pontos suficientes abaixo, próximos e acima de `Vt`.
4. Repetir a campanha com pelo menos um segundo valor tecnicamente justificável de `Vt`.
5. Para cada curva, medir em `Vt−ε`, `Vt` e `Vt+ε`.
6. Comparar padrão externo, `FlowVoltage`, `FlowRate` e curva reconstruída no aplicativo.
7. Reiniciar nó e Hub e confirmar limiar, coeficientes e CRC.
8. Ler `/calibration` e comparar com o recibo do aplicativo.
9. Registrar resíduos e incerteza do padrão; não usar somente inspeção visual.

### Critério de saída

- Ausência de salto acima da tolerância de bancada definida.
- Persistência confirmada após reboot.
- Eco e CRC coerentes com a curva aplicada.
- Evidência anexada ao checklist de hardware.

### Commit

Somente evidências e atualização de estado, após ensaio realmente executado:

```text
test(flowmeter): record configurable transition bench validation
```

## 22. Etapa 12 — validação física da bomba e dos perfis de mangueira

### Objetivo

Demonstrar a validade da curva dupla, da transição e da troca controlada entre perfis em mangueiras reais.

### Dependências

Etapas 3, 4, 6, 8, 9 e 10 concluídas e firmware gravado no equipamento.

### Procedimento por mangueira

1. Registrar nome do perfil, material, diâmetro interno, lote/identificação, fluido, temperatura e recipiente de medição.
2. Preencher a mangueira com o controle manual e remover bolhas antes de medir.
3. Definir `Qt` dentro da faixa operacional pretendida.
4. Coletar pelo menos três pontos na faixa baixa e três na faixa alta.
5. Repetir pontos próximos de `Qt` e pelo menos um ponto intermediário de cada faixa.
6. Conferir volumes e tempos digitados antes de registrar cada ponto.
7. Ajustar e salvar o perfil sem enviar; confirmar que a curva do nó não mudou.
8. Enviar explicitamente e conferir ACK, ecos e CRC.
9. Medir abaixo, na vizinhança e acima de `Qt`.
10. Executar uma dosagem contínua de dez minutos e comparar volume real com `PumpVol`.
11. Reiniciar a bomba e confirmar a calibração ativa.
12. Repetir todo o procedimento para pelo menos uma segunda mangueira.
13. Alternar entre os dois perfis e comprovar que somente **Salvar e enviar curva** altera o nó.
14. Excluir uma cópia local de teste e comprovar que a curva ativa no nó permanece inalterada.

### Critério de saída

- Curvas monotônicas e contínuas dentro da tolerância definida.
- Erro volumétrico aceitável em ambas as faixas e na transição.
- Perfil correto reaplicado após troca de mangueira.
- Reboot mantém a última curva enviada.
- Perfis locais e curva ativa no nó permanecem semanticamente separados.

### Commit

Somente evidências e atualização de estado, após ensaio realmente executado:

```text
test(pump): record hose profile bench validation
```

## 23. Matriz resumida de dependências e entregas

| Etapa | Entrega principal | Depende de | Commit |
|---:|---|---|---|
| 0 | Linha de base | — | nenhum |
| 1 | Matemática contínua | 0 | `feat(calibration)` |
| 2 | Fluxômetro v12 | 1 | `feat(flowmeter)` |
| 3 | Bomba v3.11 | 1 | `feat(pump)` |
| 4 | Hub 10.3 | 2 e 3 | `feat(hub)` |
| 5 | Protocolo/parser/simulador | 4 | `feat(protocol)` |
| 6 | Store de perfis | 1 e 5 | `feat(pump-calibration)` |
| 7 | UI do fluxômetro | 1, 4 e 5 | `feat(flow-calibration)` |
| 8 | UI e perfis da bomba | 1, 3, 4, 5 e 6 | `feat(pump-calibration)` |
| 9 | Integração de software | 2–8 | correções específicas, se necessárias |
| 10 | Documentação | 9 | `docs(calibration)` |
| 11 | Bancada do fluxômetro | 2, 4, 7, 9 e 10 | `test(flowmeter)` |
| 12 | Bancada da bomba | 3, 4, 6, 8, 9 e 10 | `test(pump)` |

## 24. Checklist final do executor

### Código e persistência

- [ ] Não existe limiar operacional fixo em `FlowIo.h` ou no modelo do aplicativo.
- [ ] EEPROM v7 preserva todos os campos da v6.
- [ ] O novo registro NVS da bomba não altera o blob `PumpConfig` v3.10.
- [ ] Q→S, S→Q e duty→Q usam a mesma calibração dupla.
- [ ] Frames completos são aplicados atomicamente.
- [ ] Frames inválidos não deixam estado parcial.
- [ ] Comandos legados têm comportamento documentado e testado.

### App e perfis

- [ ] Fluxômetro permite editar `Vt` somente quando o nó suporta a capacidade.
- [ ] Bomba permite editar `Qt` e mostra `St` ajustado.
- [ ] Os dois gráficos mostram corretamente segmentos e transição.
- [ ] Selecionar, carregar, salvar ou excluir perfil não envia comandos.
- [ ] Só **Salvar e enviar curva** modifica o nó.
- [ ] Perfis usam escrita atômica, schema e nomes seguros.
- [ ] Recibos incluem pedido, eco, CRC, perfil e versões.
- [ ] Aquisição volumétrica e controle manual continuam funcionais.

### Validação de software

- [ ] Testes matemáticos aprovados.
- [ ] Contratos do Hub aprovados.
- [ ] Três firmwares compilados: Hub, fluxômetro e bomba.
- [ ] Testes direcionados do aplicativo aprovados.
- [ ] Suíte completa aprovada.
- [ ] Clean e build Release aprovados.
- [ ] Executável Release aberto e inspecionado.
- [ ] Logs novos sem erros fatais, XAML, binding, DI ou thread de UI.

### Documentação e entrega

- [ ] Documento mestre atualizado nas seções 1 e 3.
- [ ] Protocolos de app, Hub e nós usam as mesmas chaves.
- [ ] Decisão linear antiga da bomba foi explicitamente superada.
- [ ] Ocorrências remanescentes de `0.0545` foram classificadas.
- [ ] Estados 🟢, 🔵, 🟡 e 🔴 refletem evidência real.
- [ ] Commits estão separados, ordenados e presentes na branch final.
- [ ] Árvore de trabalho está limpa, exceto itens preexistentes documentados.

## 25. Condição de encerramento

### Implementação de software concluída

Pode ser declarada somente quando:

1. etapas 0 a 10 estiverem concluídas;
2. app, Hub e dois firmwares compartilharem o mesmo contrato;
3. migrações e compatibilidade legada estiverem cobertas por testes;
4. clean/build Release, suíte completa e execução real do aplicativo estiverem aprovados;
5. documentação estiver coerente;
6. commits estiverem separados e incluídos na branch final;
7. árvore de trabalho estiver limpa quanto ao escopo.

### Liberação para uso físico concluída

Exige adicionalmente as etapas 11 e 12. Antes disso, o software pode estar pronto, mas as linhas correspondentes de bancada devem permanecer 🟡 e a entrega não deve ser descrita como validada fisicamente.
