# Plano de implementação — calibração contínua em duas faixas do fluxômetro e da bomba externa

**Data:** 2026-09-13
**Estado:** Etapas 1–10 implementadas e auditadas em software; etapas 11–12 e toda validação física continuam pendentes
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

### 3.2 Bomba peristáltica — retificação arquitetural

A bomba espelha a família de equações do fluxômetro, trocando apenas a variável independente de tensão `V` para velocidade interna `S`:

```text
S <= St: Qbaixo(S) = a1·S⁴ + b1·S³ + k1·S² + f1·S + c1
S >  St: Qalto(S)  = k2·S² + f2·S + c2
```

- `St` é editável e define a separação das amostras;
- `Qt = Qbaixo(St) = Qalto(St)` é calculada, nunca um parâmetro livre;
- `Q'baixo(St) = Q'alto(St)`, portanto a união preserva valor e inclinação (`C0+C1`);
- `S = St` pertence ao segmento inferior;
- a inversa `Q → S` é obtida por bisseção limitada em `0 ≤ S ≤ 1000`, pois não existe inversa linear geral;
- cada perfil local corresponde a uma mangueira e contém os oito coeficientes, `St`, pontos e estatísticas; o nó mantém apenas a curva ativa.

O ajuste usa a mesma rotina ancorada do fluxômetro: primeiro ajusta a curva alta quadrática (linear quando há somente dois pontos) e depois ajusta a curva baixa até grau quatro com as duas restrições de continuidade. São exigidas pelo menos duas velocidades distintas em cada faixa; três ou mais pontos por faixa continuam recomendados.

Validações: coeficientes finitos, `0 < St < 1000`, continuidade C0+C1, vazão não negativa, crescimento monotônico e inversão limitada.

#### Migração da reta atual

A calibração linear existente deverá ser convertida sem alterar o resultado numérico:

```text
a1 = b1 = k1 = k2 = 0
f1 = f2 = slope atual
c1 = c2 = intercept atual
St = 500; Qt = Q(St)
```

Como os dois polinômios representam a mesma reta, a curva migrada reproduz exatamente o resultado anterior em toda a faixa.

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
  "pumpA1": 0.0,
  "pumpB1": 0.0,
  "pumpK1": 0.0,
  "pumpF1": 0.028,
  "pumpC1": 1.76,
  "pumpK2": 0.0,
  "pumpF2": 0.028,
  "pumpC2": 1.76,
  "pumpTransitionSpeed": 500.0
}
```

#### Nó → Hub → aplicativo

```text
PumpA1
PumpB1
PumpK1
PumpF1
PumpC1
PumpK2
PumpF2
PumpC2
PumpTransitionSpeed
PumpCalCrc
```

Os nove parâmetros formam uma única calibração e são validados/aplicados atomicamente. O aplicativo somente confirma a aplicação após ACK concluído, eco integral correspondente e CRC.

#### Compatibilidade

- A migração NVS converte `pumpSlope`/`pumpIntercept` e registros v3.11, mas o contrato operacional novo não reduz uma curva polinomial a retas.
- Em bomba anterior à 3.12 ou Hub anterior a 10.4, o envio polinomial fica bloqueado com mensagem de atualização necessária.
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
- `a1`, `b1`, `k1`, `f1`, `c1`;
- `k2`, `f2`, `c2`;
- `transitionSpeed`;
- CRC real do registro.

Na primeira inicialização sem esse registro, o firmware cria duas representações polinomiais da reta legada a partir de `g_config.pumpSlope` e `g_config.pumpIntercept`. Um registro v3.11 de duas retas também é migrado: preservam-se o trecho inferior e o ponto de transição em uma única reta C1 equivalente. O blob operacional atual permanece intacto, preservando perfis de dosagem, PID e checkpoints.

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
- bomba v3.12 ou posterior: curva polinomial dupla e perfis por mangueira disponíveis;
- bomba v3.11 ou anterior: mostrar a calibração legada ecoada, bloquear o envio polinomial e explicar a atualização necessária;
- Hub 10.3 ou posterior: transição editável do fluxômetro disponível;
- Hub 10.4 ou posterior: encaminhamento e ecos da calibração polinomial da bomba disponíveis.

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

Retirar o limiar fixo da estrutura matemática do fluxômetro e criar para a bomba a mesma família de curva em duas faixas, no domínio da velocidade, sem alterar ainda protocolo, firmware ou interface.

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

1. Criar um tipo imutável para a curva `(a1, b1, k1, f1, c1, k2, f2, c2, St)`.
2. Implementar `FlowFromSpeed(S)` com polinômio de quarto grau para `S <= St` e quadrático para `S > St`.
3. Implementar `SpeedFromFlow(Q)` por busca numérica monotônica, pois a inversa deixou de ser linear.
4. Implementar a migração matemática da reta copiando-a para as duas representações polinomiais.
5. Implementar o ajuste limitado de `St`, impondo continuidade C0+C1 entre os segmentos; `Qt` é derivado, nunca parâmetro livre.
6. Calcular resíduos por ponto, SSE, RMSE, R² global e estatísticas por segmento.
7. Retornar um resultado estruturado de validação em vez de permitir `NaN`, infinito ou curva não monotônica.

### Testes obrigatórios

- `Vt` diferente de `0.0545` realmente muda a classificação dos pontos do fluxômetro.
- Fluxômetro tem mesmo valor e mesma derivada dos dois lados de `Vt`.
- `V = Vt` usa a curva inferior.
- Bomba tem o mesmo valor e a mesma derivada dos dois lados de `St`.
- `S = St` usa a curva inferior.
- Q→S e S→Q são inversas dentro de tolerância em ambas as faixas.
- Migração da reta reproduz exatamente a resposta anterior em vários valores de `S`.
- Derivada não positiva, poucos pontos, `St` inválido e solução fora da faixa são recusados.

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
- Criados `PumpDualRangeCurve`, `PumpDualRangeMath` e `PumpFitResult`: curva quártica/quadrática contínua C0+C1 em `St`, conversão direta, inversão numérica, migração exata da reta legada, validação física e estatísticas de ajuste.
- O ajuste da bomba reutiliza o ajustador polinomial do fluxômetro no domínio `S -> Q`, busca `St` nas partições possíveis e seleciona a solução contínua e monotônica de menor SSE.
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

### Registro de implementação — 2026-09-13

Implementado nesta etapa, no firmware do fluxômetro e na suíte de testes de contrato:

- Firmware promovido para `v12.0` (`#define FW_VERSION "v12.0"` em `FirmwareApp.cpp`).
- Criada a variável ativa `flowTransitionVoltage` em RAM, inicializada a partir da EEPROM.
- Adicionado o campo `float transition_v` no fim de `CalibrationParams` (offset 64..67, totalizando 68 bytes no registro).
- Definido o Schema v7 com magic `0xCAFEBAC5` e preservado `CALIBRATION_MAGIC_V6 = 0xCAFEBAC4` para permitir migração limpa.
- Implementada migração transparente v6 → v7 em `CalibrationStore.h`, preservando byte a byte todos os 64 bytes pré-existentes (`a1..c2`, PI, feedforward, rampa, `dac_hold` e `max_flow`), gravando `transition_v = 0.0545f` e recalculando o CRC32 sobre os 64 bytes de payload.
- Adicionada validação defensiva pós-carga que restaura o padrão `0.0545 V` se o valor na EEPROM estiver fora de `(0, 3.3) V` ou corrompido.
- Removido integralmente o literal fixo `0.0545f` de `FlowIo.h`, adotando a verificação dinâmica `readFlowVoltage <= flowTransitionVoltage`.
- Estendida a área de staging do parser em `CommandCodec.h` com `hasTransitionV` e `stagedTransitionV`, aceitando as chaves `transition_v` e `flowTransitionVoltage` em `(0, 3.3) V`.
- Regra de atomicidade estrita: se `hasTransitionV` estiver presente, exige obrigatoriamente ambos os segmentos completos (`a1..c1` e `k2..c2`) no mesmo quadro; comandos sem `transition_v` mantêm o limiar previamente persistido.
- Validação matemática de continuidade antes da aplicação: rejeição de curvas completas com salto de valor $|Q_\text{alto}(V_t) - Q_\text{baixo}(V_t)| > 0.01\text{ L/min}$ ou salto de derivada $|Q'_\text{alto}(V_t) - Q'_\text{baixo}(V_t)| > 0.1\text{ L/min/V}$, ou contendo NaN/Inf.
- Aplicação atômica de limiar e coeficientes sob `commandMutex` com persistência em escrita única na EEPROM.
- Exposição de `transition_v` nos canais de auditoria e telemetria:
  - push periódico `/flowData` ao Hub em `TaskRuntime.h` (`&transition_v=%.4f`);
  - endpoint local de auditoria `/calibration` em `OtaService.h` (`"transition_v":%.4f`);
  - telemetria serial e WebSocket em `Lifecycle.h` (`"transition_v":%.4f`);
  - confirmação de comando direto em `WebSocketApi.h` (`"transition_v":%.4f`).
- Criada a suíte de testes de contrato [`test_firmware_v12_contract.py`](file:///D:/OneDrive/PosDoc_Fapesp/Automacao_e_Controle/ProjetoTECNAL/External-Devices/fluxometro/tests/test_firmware_v12_contract.py) cobrindo ausência do literal em `FlowIo.h`, versão do firmware, magics v6/v7, layout binário da struct, simulação da migração v6 → v7 byte a byte, cobertura do CRC32, exposição em todos os endpoints e validações atômicas/continuidade do parser (8/8 testes aprovados).
- Verificação de compilação: firmware compilado com sucesso no alvo ESP32 via `arduino-cli` (código 0, 87% de flash, 15% de RAM, 0 erros e 0 avisos).
- Regressão: 43 testes de calibração do Windows App aprovados sem alterações em seu escopo.

Não implementado nesta etapa: alterações no Hub 10.4 (Etapa 4), protocolo do Windows App (Etapa 5), UI do fluxômetro (Etapa 7) e calibração da bomba (Etapa 3).

## 13. Etapa 3 — implementar a calibração dupla no firmware da bomba

### Objetivo

Promover a bomba para v3.12 e substituir a reta operacional pelas equações quártica/quadrática C0+C1 do fluxômetro, preservando configuração, perfil de dosagem, PID e checkpoint existentes.

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

1. Alterar identidade e diagnóstico para v3.12.
2. Manter o layout de `PumpConfig` v3.10 inalterado.
3. Criar um registro NVS separado `pump_poly_cal` com magic/schema e CRC real.
4. Na ausência do registro novo:
   - ler `pumpSlope` e `pumpIntercept` legados;
   - configurar ambos os segmentos como `Q = pumpSlope * S + pumpIntercept`;
   - usar `St = 500`;
   - derivar `Qt` pela avaliação da curva em `St`;
   - salvar o novo registro sem alterar os outros campos de `PumpConfig`.
5. Atualizar `mlminToSpeedUnits` com bisseção limitada da curva monotônica.
6. Atualizar `speedUnitsToMlmin` para avaliar a quártica ou a quadrática conforme `St`.
7. Garantir que `pwmDutyToMlmin` use a nova conversão, pois ela alimenta `PumpFlow` e `PumpVol`.
8. Criar staging dos nove campos da calibração nova.
9. Exigir os nove campos no mesmo quadro.
10. Recusar valores não finitos, descontinuidade C0/C1, `St` fora de `(0,1000)`, vazão negativa e curva não monotônica.
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

A bomba v3.12 executa a curva polinomial com continuidade C0+C1 e compatibilidade de migração, sem perda de nenhuma configuração v3.10/v3.11.

### Commit

```text
feat(pump): support continuous dual-range calibration
```

### Registro de implementação — 2026-09-13

Implementado nesta etapa, no firmware da bomba peristáltica e na suíte de testes de contrato:

- Firmware final promovido para `v3.12` em identidade, diagnóstico, banner e `sendHubHello`.
- Preservação estrita do layout binário de `PumpConfig` v3.10 (chave NVS `"config"` intacta com 18 campos legados), prevenindo qualquer corrupção de perfis salvos, PID, checkpoints e evitando reset para padrões de fábrica.
- `CalibrationStore.h` usa registro polinomial isolado `pump_poly_cal`, magic `PMP3` e CRC32 sobre nove floats; o registro `PMP2`/`pump_cal` permanece somente como fonte de migração.
- Implementada migração transparente da reta legada e do registro v3.11: ambos geram representações polinomiais C0+C1; a reta é preservada exatamente e, para duas retas incompatíveis com C1, preservam-se o trecho inferior e o ponto de transição.
- O novo registro é persistido em `"pump_poly_cal"` com CRC32 sem tocar nos demais campos de `PumpConfig`.
- Atualizadas as conversões matemáticas em `SensorAndConversion.h`:
  - `mlminToSpeedUnits`: inversão numérica por bisseção limitada da curva monotônica;
  - `speedUnitsToMlmin`: avaliação por Horner do quarto grau para $S \le S_t$ e do quadrático para $S > S_t$;
  - `pwmDutyToMlmin`: atualizado para usar `speedUnitsToMlmin`, garantindo estimativa contínua de vazão e integração de volume sem salto ao cruzar $S_t$.
- Staging e validação atômica no parser JSON em `OperationController.h`:
  - suporte às chaves `pumpA1..pumpC2` e `pumpTransitionSpeed`, com aliases nativos `a1..c2` e `transition_speed`;
  - regra de atomicidade estrita: exige obrigatoriamente os 9 parâmetros no mesmo quadro, rejeitando quadros parciais;
  - validação defensiva: finitude, $S_t \in (0, 1000)$, continuidade C0+C1, vazão não negativa e monotonicidade;
  - interlock de segurança: rejeita qualquer alteração de calibração durante operação ativa da bomba (`g_opState == OP_RUNNING || g_opState == OP_WAITING`).
- Os campos lineares legados permanecem somente para leitura/migração; o contrato v3.12 não aceita atualização parcial ou downgrade silencioso.
- Exposição nos canais de auditoria e telemetria:
  - push periódico ao Hub inclui `a1..c2`, `transition_speed` e `cal_crc`, mantendo `slope` e `intercept` legados somente para diagnóstico;
  - endpoint local `/readData` e serial serializam os nove parâmetros e CRC.
  - `/diag` e OTA identificados como `v3.11`.
- Preservados integralmente: controle por potenciômetro, comando por tempo `speed_ms`, watchdog de comunicação do Hub com backoff exponencial, tarefas assíncronas do FreeRTOS e rotinas de salvamento/recuperação de checkpoint de dosagem.
- Criada a suíte `test_firmware_v312_contract.py`, cobrindo versão, registro, migrações, validação do registro persistido, Horner, bisseção, atomicidade, C0+C1, monotonicidade, telemetria e interlocks (11/11 aprovados).
- Verificação de compilação: firmware compilado com sucesso no alvo ESP32 via `arduino-cli` (código 0, 83% de flash, 16% de RAM, 0 erros e 0 avisos).
- Regressão: suíte de contrato do fluxômetro v12 (8/8 testes aprovados) e 43 testes de calibração do Windows App aprovados.

Não implementado nesta etapa: contratos de transporte no Hub 10.4 (Etapa 4), integração no protocolo do Windows App (Etapa 5) e UI da bomba com biblioteca de mangueiras (Etapas 8 e 9).

## 14. Etapa 4 — integrar os contratos no Hub

### Objetivo

Fazer o Hub 10.4 transportar e ecoar os contratos do fluxômetro v12 e da bomba v3.12 mantendo fila confiável, ACK e presença.

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
3. Ler os nove ecos e o CRC no `/pumpData`.
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

### Registro de implementação — 2026-09-13

Implementado nesta etapa, no firmware do Hub ESP32-S3 e na sua suíte de testes de contrato:

- Firmware final do Hub promovido para `10.4.0-dev`, mantendo `HUB_PROTOCOL_VERSION 10`; a versão 10.3 permanece o marco mínimo do limiar do fluxômetro.
- Roteamento da calibração de dupla faixa do fluxômetro v12:
  - Adicionados `pendingFlowTransitionVoltage`, `desiredFlowTransitionVoltage = 0.0545f` e `flowmeterTransitionVoltage = NAN` em `AppContext.h`;
  - Parser de comando `/command` (`queueReliableFlowCommandFromJson` em `Mailboxes.h`) aceita `flowTransitionVoltage` e `transition_v`, enfileirando atomicamente sob `cmdMutex` na mesma revisão de comando que os coeficientes da curva (`a1`, `b1`, `k1`, `f1`, `c1`, `k2`, `f2`, `c2`);
  - Serializador `buildFlowCommandLocked` emite `"transition_v":%.4f` quando `pendingFlowTransitionVoltage` está ativa;
  - Endpoint `/flowData` em `HttpServer.h` lê o parâmetro `transition_v`, limpa a pendência `pendingFlowTransitionVoltage` apenas quando o comando do fluxômetro for confirmado (`flowCommandAck`), preserva a reimposição de estado desejado em detecção de reboot silencioso do nó e expõe o eco recebido;
  - Telemetria agregada em `Telemetry.h` publica `FlowTransitionVoltage` condicionado à presença online do fluxômetro e à observação do valor reportado no boot atual.
- Roteamento da calibração polinomial da bomba peristáltica v3.12 com `a1..c1`, `k2..c2` e `transition_speed`:
  - Adicionados `pumpA1..pumpC2`, `pumpTransitionSpeed` e `pumpCalCrc` em `AppContext.h`;
  - Whitelist de `Commands.h` estendida com as nove chaves do app e seus aliases nativos;
  - Tradução mantém os nove parâmetros no mesmo payload `pumpCommand`, com controle de revisão e ACK;
  - `/pumpData` lê `a1..c2`, `transition_speed` e `cal_crc` sob `cmdMutex`;
  - `Telemetry.h` publica `PumpA1..PumpC2`, `PumpTransitionSpeed` e `PumpCalCrc` somente com nó online e ecos observados neste boot.
- Verificação da reserva de memória de telemetria:
  - O payload JSON de telemetria agregada permanece estritamente compatível com `HUB_TELEMETRY_JSON_RESERVE = 3072` bytes.
- Suíte de testes de contrato atualizada e executada:
  - `verify_contract.py` corrigido para localizar diretório `old` / `_old`, e `V10_KEYS` estendido com os ecos polinomiais e CRC.
  - `test_node_registry.py` atualizado para assertar identidade `10.4.0-dev`;
  - `test_node_commands.py` estendido com testes de roteamento e validação de `PumpCommandTests` e verificações estáticas de `NodeCommandSourceContractTests` (87/87 testes aprovados);
  - `test_json_keys.py` estendido com os frames reais de calibração dupla em `REAL_FRAMES` e checagens de telemetria;
  - Executado `verify_contract.py`: 10 hashes legados OK, endpoints/chaves/regras estáticas v10 OK, fixtures HTTP, presença e fila Servo OK.
- Compilação no alvo ESP32-S3 via `arduino-cli`:
  - Sketch compilado com sucesso (código 0, 86% de flash [1.128.088 bytes de 1.310.720], 15% de RAM dinâmica [50.624 bytes de 327.680]).
- Testes de regressão:
  - Fluxômetro v12: 8/8 testes aprovados (`test_firmware_v12_contract.py`).
  - Bomba peristáltica v3.12: 11/11 testes aprovados (`test_firmware_v312_contract.py`).
  - Windows App matemática/calibração: 43/43 testes aprovados (`dotnet test`).

Não implementado nesta etapa: contratos tipados no protocolo C# do Windows App (Etapa 5), integração nos viewmodels e interface do usuário (Etapas 6–9), e validação física.

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
   - curva completa da bomba com oito coeficientes polinomiais e `St`.
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

### Registro de implementação — 2026-09-13

Implementado nesta etapa, no protocolo C#, parser de telemetria, simulador e suíte de testes do Windows App:

- Declaração tipada de constantes em `OpenTECHub.Protocol.CommandKeys`:
  - Comandos: `FlowTransitionVoltage = "flowTransitionVoltage"`; para a bomba, `PumpA1`, `PumpB1`, `PumpK1`, `PumpF1`, `PumpC1`, `PumpK2`, `PumpF2`, `PumpC2` e `PumpTransitionSpeed`.
  - Telemetria (`TelemetryKeys`): `FlowTransitionVoltage = "FlowTransitionVoltage"`, `PumpSlopeLow = "PumpSlopeLow"`, `PumpSlopeHigh = "PumpSlopeHigh"`, `PumpTransitionSpeed = "PumpTransitionSpeed"`, `PumpTransitionFlow = "PumpTransitionFlow"`, `PumpCalCrc = "PumpCalCrc"`.
- Builders atômicos em `OpenTECHub.Protocol.CommandBuilders` com validação defensiva rigorosa:
  - `FlowCalibration(maxFlow, a1, b1, k1, f1, c1, k2, f2, c2, transitionVoltage)`: valida finitude, $0 < V_t < 3.3\text{ V}$, $Q_\text{max} > 0$ e serializa na ordem exata de chaves exigida pelo Hub e fluxômetro.
  - `PumpDualRangeCalibration(slopeLow, slopeHigh, transitionSpeed, transitionFlow)`: valida finitude, inclinações $> 0$, velocidade $0 < S_t < 1000$, vazão $Q_t > 0$ e restrição física $Q_t - m_\text{low} \cdot S_t \ge 0$ (evitando vazão negativa em repouso), emitindo atomicamente os 4 campos no mesmo quadro.
- Modelos de dados em `OpenTECHub.Protocol.SensorReadings`:
  - Adicionadas propriedades anuláveis (`double?` e `long?`) a `SensorReadings` e `SensorSnapshot`: `FlowTransitionVoltage`, `PumpSlopeLow`, `PumpSlopeHigh`, `PumpTransitionSpeed`, `PumpTransitionFlow`, `PumpCalCrc`.
  - Preservação da semântica onde `null` representa ausência de telemetria (dispositivo legado ou offline) e não o valor `0.0`.
- Parser não-pegajoso em `OpenTECHub.Protocol.TelemetryParser`:
  - Parse de `FlowTransitionVoltage` em `ParseFlow` e dos 5 campos da bomba em `ParsePump`.
  - Limpeza estrita a cada quadro: propriedades não presentes no JSON corrente são redefinidas para `null`, impedindo estado fantasma ou eco persistente.
- Atualização do simulador em `OpenTECHub.Simulator`:
  - `DeviceModel`: campos `FlowTransitionVoltage`, `PumpSlopeLow`, `PumpSlopeHigh`, `PumpTransitionSpeed`, `PumpTransitionFlow`, `PumpCalCrc`, `PumpCommandPending` e método estático `CalculatePumpCalibrationCrc` calculando CRC32 IEEE 802.3 determinístico sobre os 16 bytes de floats em IEEE 754 little-endian.
  - `WireCodec.BuildTelemetry`: emissão condicional dos 6 novos campos quando `Scenario != Scenario.LegacyHub`, além de consumo atômico de `PumpCommandPending` via `ConsumePumpCommandPending()`.
  - `WireCodec.ApplyCommand`: parse e validação dos comandos atômicos de calibração dupla, acionando flags de pendência e rejeitando quadros incompletos ou com restrições físicas violadas (preservando a calibração anterior intacta).
- Testes automatizados adicionados e aprovados:
  - Em `WireFormatTests.cs`: validação do formato wire e rejeição de parâmetros inválidos/out-of-bounds/violação física para os builders de fluxo e bomba, bem como conformidade literal das constantes de `CommandKeys` e `TelemetryKeys`.
  - Em `SimulatorNodeConfigTests.cs`: testes de parsing dos 6 novos ecos, garantia de não-pegajosidade (reset para `null` no quadro seguinte), aplicação e rejeição defensiva no simulador, consumo de flag pendente e supressão de todos os novos campos no cenário `Scenario.LegacyHub`.
  - Suíte completa do .NET executada: 1.640 testes aprovados, 0 falhas.
  - Verificação de regressão nos firmwares: `verify_contract.py` (87/87 OK), `test_firmware_v12_contract.py` (8/8 OK), `test_firmware_v311_contract.py` (9/9 OK).

Não implementado nesta etapa: persistência de perfis por mangueira no aplicativo (Etapa 6), ViewModel e telas XAML (Etapas 7 e 8) e testes end-to-end de UI (Etapas 9–10).

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

### Registro de implementação — 2026-09-13

Implementado nesta etapa, na camada de serviços, persistência, contratos de arquivo e testes unitários do Windows App:

- **Modelos de domínio (`PumpCalibrationProfile.cs`):**
  - `PumpCalibrationProfile`: entidade versionada (`schemaVersion = 2`) por mangueira, com `LowA..LowC`, `HighK..HighC`, `TransitionSpeedUnits`, pontos, estatísticas, notas e metadados de aplicação. O schema 1 de duas retas é lido e migrado em memória.
  - `PumpCalibrationProfileSummary`: projeção leve para listagem rápida sem carregar coleções de pontos, incluindo flag `IsCompatible` para proteção de schema futuro e parâmetros essenciais da curva.
  - `PumpFitStatistics`: métricas estatísticas de aderência da curva ($R^2$, RMSE, SSE) globais e discriminadas por segmento de velocidade/vazão.
- **Regras e segurança de arquivos (`PumpProfileFileContracts.cs`):**
  - `ValidateProfileName`: validação defensiva estrita contra nomes vazios/whitespace, tamanho fora do intervalo [2, 100], caracteres inválidos de arquivo (`*`, `?`, `:`, `<`, `>`, `|`, `"`, `/`, `\`), sequências de travessia de diretório (`..`), pontos ou espaços no final do nome e palavras reservadas de dispositivo do Windows (`CON`, `PRN`, `AUX`, `NUL`, `COM1`–`COM9`, `LPT1`–`LPT9`).
  - `ProfileFileName`: sufixo padronizado `.json`.
  - `JsonOptions`: codec camelCase indentado com tolerância a comentários e vírgulas finais.
- **Armazenamento atômico e seguro (`IPumpCalibrationProfileStore` e `PumpCalibrationProfileStore.cs`):**
  - Diretório centralizado em `AppPaths.PumpProfilesDirectory` (`<Workspace>\Calibracoes\BombaExterna\Perfis`).
  - Escrita atômica via arquivo temporário (`.tmp-<guid>`) seguido de `File.Move(..., overwrite: true)` com retentativas defensivas contra locks transitórios do SO.
  - Rejeição de sobrescrita acidental: salvar sobre perfil existente sem `overwrite: true` lança `InvalidOperationException`.
  - Proteção de schema futuro: arquivos com `schemaVersion > 1` são reportados com `IsCompatible = false` na listagem, rejeitados no `LoadProfile` e protegidos contra sobrescrita mesmo com `overwrite: true`.
  - Isolamento de arquivos corrompidos: arquivos mal formatados ou com JSON corrompido são capturados e logados, sem impedir o carregamento e listagem dos perfis íntegros. `LoadProfile` encapsula erros de deserialização em `InvalidOperationException` descritivo.
  - Migração inicial idempotente (`EnsureDefaultProfileMigrated`): converte a calibração linear legada em dois polinômios lineares idênticos no perfil `"Padrão.json"`; perfis schema 1 são normalizados para schema 2 ao carregar/salvar.
- **Configurações globais e DI (`AppSettings.cs`, `App.xaml.cs`):**
  - Adicionado `SelectedProfileName` em `PumpControlSettings` para armazenar exclusivamente o identificador/nome do perfil ativo no aplicativo.
  - Adicionado `PumpProfilesDirectory` e sua criação automática em `AppPaths.EnsureDirectories()`.
  - Registrado `IPumpCalibrationProfileStore` como singleton no contêiner de injeção de dependência em `App.xaml.cs`.
- **Suíte de testes automatizados (`PumpCalibrationProfileStoreTests.cs`):**
  - 31 testes unitários aprovados (em `[Collection("AppPaths")]` com workspace isolado via `AppPaths.OverrideForTests`):
    - Ciclo completo de CRUD (salvar, listar, carregar, sobrescrever e excluir).
    - Rejeição e segurança de nomes inválidos (23 cenários via teoria de testes).
    - Tolerância a arquivos corrompidos sem contaminação dos perfis válidos.
    - Preservação e bloqueio de sobrescrita de schema futuro.
    - Validação de consistência física da curva na persistência.
    - Idempotência da migração de perfil padrão a partir de configurações legadas.
  - Suíte completa do .NET executada: 1.671 testes aprovados, 0 falhas.
  - Verificação de regressão nos firmwares: `verify_contract.py` (87/87 OK), `test_firmware_v12_contract.py` (8/8 OK), `test_firmware_v311_contract.py` (9/9 OK).

Não implementado nesta etapa: ViewModel e telas XAML (Etapas 7 e 8) e testes end-to-end de UI (Etapas 9–10).

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

### Registro de implementação — 2026-09-13

Implementado nesta etapa, na interface de usuário, ViewModel, persistência e testes do Windows App:

- **Configurações e persistência (`AppSettings.cs`):**
  - Adicionada a propriedade `FlowTransitionVoltage` (com valor padrão `FlowCalibrationCurve.DefaultTransitionVoltage = 0.0545 V`) dentro de `CalibrationSettings`.
  - Migração implícita de arquivos de configuração pré-existentes: caso o campo esteja ausente, o valor padrão `0.0545` é atribuído sem perda de integridade.
  - O método `PersistPoints` em `FlowCalibrationViewModel` salva `FlowTransitionVoltage` localmente junto com os pontos medidos sem emitir comandos ao hardware.
- **ViewModel reativo (`FlowCalibrationViewModel.cs`):**
  - Adicionadas as propriedades observáveis: `TransitionVoltageText`, `TransitionVoltage`, `TransitionVoltageError`, `IsTransitionVoltageValid`, `IsTransitionVoltageEditable`, `TransitionVoltageUnsupportedReason`, `CanEditTransitionVoltage`, `TransitionFlowText`, `LowPointCount`, `HighPointCount` e `PointDistributionText`.
  - Validação de entrada: aceita números positivos no intervalo estrito $(0, 3.3)\text{ V}$. Formatações inválidas ou números fora da faixa exibem mensagem de erro sem destruir ou sobrescrever a última curva válida em memória.
  - Reatividade e recálculo contínuo: ao alterar `TransitionVoltage`, os pontos são redistribuídos dinamicamente entre as faixas baixa e alta, o ajuste polinomial duplo é recalculado imediatamente e a interface é atualizada (equações dinâmicas e indicador de continuidade no ponto exato $V_t$).
  - Detecção de capacidade do nó legado: ao receber telemetria de nó com versão de firmware inferior a `12.0` ou sem suporte a calibração com limiar, desabilita a edição com mensagem explicativa e mantém o padrão `0.0545 V`.
  - Envio atômico de 10 parâmetros (`SendCurve`): monta o payload completo via `CommandBuilders.FlowCalibration` (`flowA`, `flowB`, `flowC`, `flowD`, `flowK`, `flowHighA`, `flowHighB`, `flowHighC`, `flowHighK`, `flowTransitionVoltage`).
  - Bloqueio de curvas parciais e verificação de continuidade: rejeita envio se faltarem coeficientes em qualquer dos segmentos ou se a descontinuidade na transição for superior a $0.01\text{ L/min}$.
  - Confirmação rigorosa por telemetria: retenção do estado de envio pendente caso o firmware envie ACK sem ecoar os parâmetros; emissão de aviso visual e recusa de confirmação se o eco de `flowTransitionVoltage` divergir do valor solicitado; confirmação exibida apenas após correspondência integral de ACK e parâmetros ecoados.
  - Ação `Restaurar padrão`: redefine `TransitionVoltage` para `0.0545 V` e atualiza a curva.
- **Visualização gráfica e interface (`CalibrationView.xaml`, `CalibrationView.xaml.cs`):**
  - Adicionado controle numérico com label de unidade para $V_t$, contadores dinâmicos de pontos por faixa, vazão teórica no limiar e indicadores visuais de erro ou aviso de compatibilidade.
  - Atualização do ScottPlot (`RedrawFlowCurve`): o traçado segmentado é dividido exatamente em `TransitionVoltage`, e a linha vertical tracejada indicativa de transição acompanha dinamicamente o valor de $V_t$ configurado.
- **Suíte de testes automatizados (`CalibrationTests.cs`):**
  - Adicionados testes unitários cobrindo o fluxo completo de $V_t$:
    - `TransitionVoltage_Editing_RefitsCurve_AndMovesPointsBetweenSegments`
    - `TransitionVoltage_InvalidText_PreservesLastValidCurve_AndShowsError`
    - `SavePoints_PersistsTransitionVoltage_WithoutSendingCommands`
    - `SendCurve_SendsAtomic10ParameterCommand_WithTransitionVoltage`
    - `SendCurve_RejectsPartialOrDiscontinuousCurve`
    - `Telemetry_AckWithoutEcho_KeepsPendingState`
    - `Telemetry_AckWithMismatchedEcho_EmitsWarningAndRefusesConfirmation`
    - `Telemetry_AckWithMatchingEcho_ConfirmsCurveAndThreshold`
    - `LegacyNode_DisablesTransitionVoltageEditing_WithExplanation`
    - `Settings_DefaultTransitionVoltage_MigratesLegacySettings`
  - Atualização de testes existentes para o frame atômico de 10 parâmetros.
  - Verificação de layout compacto aprovada em 1024 × 640 DIP (e no limite de 936 × 534 DIP).
  - 55/55 testes de calibração aprovados; 87/87 testes de contrato do Hub e contratos de firmware mantidos 100% íntegros.

Não implementado nesta etapa: Tela e ViewModel de calibração da bomba com perfis de mangueira (Etapa 8).

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

1. Substituir os campos lineares por `St` e pelas equações quártica/quadrática ajustadas.
2. Manter `St` editável e apresentar `Qt = Q(St)` como resultado derivado do ajuste.
3. Classificar visualmente pontos de faixa baixa e alta.
4. Mostrar estatísticas globais e por segmento.
5. Desenhar as duas curvas polinomiais, o ponto de transição e os pontos volumétricos.
6. Preservar pontos e última curva válida enquanto o usuário digita um `St` inválido.
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

1. Enviar os nove parâmetros em um único comando.
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

### Registro de implementação — 2026-09-13

Implementado e auditado nesta etapa, no aplicativo Windows:

- **Curva e ajuste contínuos (`PumpCalibrationViewModel.cs`):**
  - o editor usa `PumpDualRangeCurve`, com `St` editável, `Qt` derivada e equações quártica/quadrática calculadas por `PumpDualRangeMath` com o mesmo ajuste C0+C1 do fluxômetro;
  - a curva válida anterior permanece visível durante entrada temporariamente inválida, mas envio e salvamento ficam bloqueados quando os pontos atuais não produzem ajuste válido;
  - pontos são classificados por `S ≤ St` e `S > St`, com resíduos e estatísticas globais e por segmento;
  - a migração linear mantém exatamente a reta legada como dois polinômios lineares idênticos e `St = 500`.
- **Biblioteca de perfis de mangueira:**
  - listagem usa `PumpCalibrationProfileSummary`, sem abrir arrays de pontos de todos os arquivos;
  - selecionar não carrega, não salva e não envia; carregar substitui explicitamente curva e pontos e pede confirmação antes de descartar edição local;
  - salvar, salvar como e excluir respeitam nomes seguros, confirmação de sobrescrita/exclusão e escrita atômica do store da etapa 6;
  - carregar usa os coeficientes congelados no perfil, inclusive quando não há pontos suficientes para reajuste, e deixa o editor inicialmente sem marca de alteração;
  - excluir arquivo local não envia comando nem modifica a curva ativa no nó;
  - `lastAppliedUtc` e firmware aplicado só são atualizados depois da confirmação integral e somente quando a curva salva no perfil corresponde à curva enviada.
- **Capacidades e segurança operacional:**
  - envio polinomial exige Hub 10.4+ e bomba 3.12+; versões com prefixo `v` e sufixo de desenvolvimento são interpretadas sem habilitação otimista;
  - firmware ou Hub legado, identidade ainda desconhecida e bomba com perfil ativo ou aguardando execução bloqueiam o envio com explicação;
  - o frame atômico usa `pumpA1`, `pumpB1`, `pumpK1`, `pumpF1`, `pumpC1`, `pumpK2`, `pumpF2`, `pumpC2` e `pumpTransitionSpeed`;
  - aceitação do dispatcher, ACK isolado, eco parcial ou eco sem CRC não confirmam a calibração;
  - confirmação requer fim da pendência de comando, eco correspondente dos nove parâmetros e `PumpCalCrc`; divergência ou timeout não persistem calibração nem geram recibo;
  - a obrigação de parada após perda de conexão e o controle manual de preenchimento sem criação de ponto foram preservados.
- **Interface e gráfico (`CalibrationView.xaml` e `.xaml.cs`):**
  - mantido o espelhamento da aba do fluxômetro: gráfico grande à esquerda, resumo abaixo e um único painel lateral para perfis, pontos, aquisição e preenchimento;
  - curvas quártica/quadrática, transição móvel e pontos volumétricos são desenhados pelo modelo compartilhado;
  - removidos bindings para propriedades inexistentes e corrigida a tabela compacta para não exceder o painel em 936 × 534 DIP;
  - textos de ajuda foram alinhados às equações `a1..c1`, `k2..c2`, `St` editável e `Qt` derivada.
- **Persistência e recibo:**
  - settings passam a manter a curva dupla confirmada com defaults numericamente equivalentes à reta legada;
  - recibo inclui perfil, pontos, estatísticas, curva solicitada e ecoada, CRC, versões do aplicativo, Hub e bomba e instante UTC.
- **Verificações antes do commit funcional `918ed15`:**
  - 65/65 testes direcionados de bomba, store de perfis, contrato do painel e layout próprio aprovados;
  - 109/109 testes combinados de bomba, perfis, documentação, recursos XAML, painel e layout próprio aprovados;
  - `git diff --check` aprovado para os arquivos da etapa.

Correções da auditoria sobre a implementação parcial recebida: removida a fórmula documental divergente, corrigido o estado sujo após carregar, carregada a curva congelada do perfil, adicionados os bloqueios de capacidade/operação, exigidos ACK concluído e CRC, corrigido o timeout sem eco, eliminado fallback silencioso ao salvar curva inválida e reparados bindings/layout da aba.

Não implementado nesta etapa: validação ponta a ponta e execução Release (Etapa 9), atualização documental ampla (Etapa 10) e validações físicas (Etapas 11–12).

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

### Registro de implementação — 2026-09-13

Etapa 9 concluída em software. A integração foi auditada após os commits funcionais da Etapa 8; as duas correções encontradas foram mantidas em commits próprios, separadas deste registro documental:

- `8c81a52 test(pump-calibration): isolate hose profile fixtures`:
  - cada teste da calibração da bomba passa a usar um diretório temporário exclusivo para os perfis;
  - eliminada a interferência entre testes paralelos e entre a suíte e o workspace real do operador;
  - corrigida a falha intermitente `UnauthorizedAccessException` observada na primeira execução completa.
- `55b20ae fix(settings): allow compact node table scrolling`:
  - a tabela de nós do Hub passa a possuir rolagem horizontal local quando suas colunas não cabem na largura disponível;
  - corrigido o único overflow encontrado pelos contratos de layout compacto, sem alterar larguras globais nem outras páginas.

**Evidência automatizada:**

- 257/257 testes direcionados de matemática, protocolo, parser, fluxômetro, bomba, perfis, documentação, recursos XAML e layouts aprovados após a correção da tabela de nós;
- 1.683/1.683 testes da solução `Windows_app/OpenTECHub.slnx` aprovados, sem falhas nem testes ignorados, após isolar o store de perfis usado pelos testes da bomba;
- 87/87 verificações do contrato do Hub aprovadas, incluindo golden strings, chaves modernas e legadas e preservação dos endpoints existentes;
- 8/8 testes estáticos do firmware v1.2 do fluxômetro aprovados;
- 11/11 testes estáticos do firmware v3.12 da bomba aprovados;
- cenários de simulador moderno e legado cobertos pela suíte, incluindo parser/codec, presença e capacidade dos nós, ausência de chaves modernas, bloqueios de compatibilidade, carregamento local de perfil sem envio, confirmação completa por ACK/eco/CRC e emissão de recibo.

**Compilações:**

- Hub ESP32-S3 recompilado após a correção polinomial: 1.130.724 bytes de flash (86%) e 50.656 bytes de RAM (15%);
- bomba peristáltica v3.12 recompilada após a correção polinomial: 1.100.055 bytes de flash (83%) e 55.116 bytes de RAM (16%);
- fluxômetro compilado pelo script oficial: 1.148.603 bytes de flash (87%) e 52.288 bytes de RAM (15%);
- o script agregado também compilou com sucesso os demais nós externos, sem converter esse resultado em validação funcional deles;
- build Release final do aplicativo aprovado com zero erros; permaneceram 38 avisos de estilo, sem aviso funcional de compilação.

**Execução Release:**

- executável Release iniciado com workspace temporário explícito e navegação direta para `calibrations`;
- processo permaneceu responsivo, apresentou a janela `OpenTEC-Hub` e foi encerrado normalmente;
- o log novo não contém `Fatal`, `Unhandled`, `XamlParseException`, erro de binding, falha de DI nem violação de thread de UI;
- a ausência de controlador em `COM1` foi registrada como condição esperada desta execução sem bancada;
- houve um aviso não fatal de orçamento do primeiro frame (6.557 ms para orçamento de 2.000 ms), sem crash ou falha das calibrações; ele não constitui evidência de desempenho em máquina de produção.

Conforme orientação do usuário, não foi feita uma segunda rodada de inspeção visual manual em temas claro/escuro: eventuais ajustes visuais serão reportados pelo próprio usuário. A cobertura mantida nesta etapa é composta pelos contratos automatizados de layout compacto/amplo, recursos e temas, mais a abertura real do Release. Nenhuma captura preexistente foi incorporada ou revertida por estes commits.

**Revalidação final — 2026-09-14:** após a retificação do modelo da bomba, o Release foi recompilado e aberto diretamente em `calibrations` com workspace temporário; encerrou automaticamente com código 0 e o log novo não apresentou `Fatal`, `Unhandled`, `XamlParseException` nem erro de binding. Hub 10.4, bomba v3.12 e fluxômetro v12.0 foram recompilados a partir do código final; os números acima correspondem a essa rodada.

Não implementado nesta etapa: atualização documental ampla (Etapa 10) e validações físicas com Hub, fluxômetro e bomba reais (Etapas 11–12). Aprovação de testes, contratos, builds e simulador não substitui bancada.

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
4. Documentar a curva baixa quártica, a curva alta quadrática, `St` editável, `Qt` derivada e continuidade C0+C1 da bomba.
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
- `Windows_app/docs/DECISIONS.md`: decisão sobre `Vt`, `St`, equações espelhadas, continuidade, migração e biblioteca no PC.
- `Windows_app/docs/hardware/HARDWARE_VALIDATION.md`: matriz de ensaios por transição e mangueira.
- `Windows_app/docs/CHANGELOG.md`: mudanças entregues e versões compatíveis.
- `Windows_app/docs/ROADMAP.md`: software concluído e bancada pendente.
- `Windows_app/docs/PONTOS_DE_MELHORIA_EXPOSICAO_NOS.md`: capacidades, ecos e pendências.
- `Windows_app/src/OpenTECHub/Services/Documentation/DocumentationCatalog.cs`: ajuda interna coerente com a interface final.

### Hub ESP32-S3

- `ESP32S3-HUB/docs/WIRE_CONTRACT_V9.md`: tradução app → Hub → nós e ecos.
- `ESP32S3-HUB/docs/VALIDATION.md`: casos de contrato, ACK, persistência e compatibilidade.
- `ESP32S3-HUB/README.md`: Hub 10.4 e capacidades novas.

### Firmware do fluxômetro

- `External-Devices/fluxometro/docs/PROTOCOL.md`: `transition_v`, eco, CRC e EEPROM v7.
- `External-Devices/fluxometro/README.md`: firmware v12 e comportamento da transição.
- `External-Devices/fluxometro/CHANGELOG.md`: migração e compatibilidade.

### Firmware da bomba

- `External-Devices/bomba-peristaltica/docs/PROTOCOL.md`: equações polinomiais, comandos, ecos e compatibilidade.
- `External-Devices/bomba-peristaltica/README.md`: firmware v3.12 e calibração ativa.
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

### Registro de implementação — 2026-09-13

Etapa concluída após auditoria integral das etapas 1–10 e correção de uma premissa matemática incorreta identificada pelo usuário.

- O plano foi retificado: a bomba não usa duas retas unidas em `(St, Qt)`. Ela usa a mesma família do fluxômetro, com segmento inferior quártico, superior quadrático e continuidade C0+C1 em `St`; `Qt` é consequência das equações.
- O contrato ponta a ponta foi promovido para bomba `v3.12` e Hub `10.4`: oito coeficientes mais `pumpTransitionSpeed`, ecos integrais e `PumpCalCrc`.
- O aplicativo passou a ajustar as curvas com a rotina compartilhada do fluxômetro, validar monotonicidade/não negatividade, inverter `Q→S` por bisseção e persistir perfis schema 2 por mangueira.
- Perfis schema 1, a reta legada e o registro NVS v3.11 são migrados sem alterar os blobs operacionais. Como duas retas com inclinações diferentes não podem preservar C1, a migração mantém o trecho inferior e o ponto de transição como reta única equivalente.
- O simulador reproduz o novo quadro atômico, os ecos e as recusas matemáticas. O firmware rejeita registros NVS inválidos, quadros parciais, curvas descontínuas, não monotônicas ou recebidas durante operação ativa.
- A auditoria corrigiu a classificação visual dos pontos da bomba: as faixas agora são separadas por `SpeedUnits <= St`, e não pela comparação dimensionalmente incorreta entre vazão e `St`.
- A auditoria do fluxômetro encontrou e corrigiu confirmação incompleta: o aplicativo agora exige ACK concluído, eco de `Vt` e `FlowmeterCalCrc`, além de bloquear Hub `<10.3` e fluxômetro `<12`.
- Documentos mestre, protocolos, processos, matrizes, ajuda interna, changelogs e planos históricos foram alinhados; referências às retas permanecem somente como diagnóstico histórico ou migração.
- Verificação de software: 1.683/1.683 testes .NET, 88/88 contratos do Hub e 11/11 contratos da bomba aprovados. Isso não substitui as etapas físicas 11–12.

Commits separados desta conclusão:

1. `fix(pump-calibration): mirror flowmeter polynomial equations`
2. `feat(device-contract): transport polynomial pump calibration`
3. `test(calibration): cover polynomial pump profiles and echoes`
4. `docs(calibration): record audited stages one through ten`

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
| 3 | Bomba v3.12 | 1 | `feat(pump)` |
| 4 | Hub 10.4 | 2 e 3 | `feat(hub)` |
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

- [x] Não existe limiar operacional fixo em `FlowIo.h` ou no modelo do aplicativo.
- [x] EEPROM v7 preserva todos os campos da v6.
- [x] O novo registro NVS da bomba não altera o blob `PumpConfig` v3.10.
- [x] Q→S, S→Q e duty→Q usam a mesma calibração dupla.
- [x] Frames completos são aplicados atomicamente.
- [x] Frames inválidos não deixam estado parcial.
- [x] Comandos legados têm comportamento documentado e testado.

### App e perfis

- [x] Fluxômetro permite editar `Vt` somente quando o nó suporta a capacidade.
- [x] Bomba permite editar `St` e mostra `Qt=Q(St)` derivado.
- [x] Os dois gráficos mostram corretamente segmentos e transição nos contratos automatizados; a inspeção visual adicional foi dispensada pelo usuário.
- [x] Selecionar, carregar, salvar ou excluir perfil não envia comandos.
- [x] Só **Salvar e enviar curva** modifica o nó.
- [x] Perfis usam escrita atômica, schema e nomes seguros.
- [x] Recibos incluem pedido, eco, CRC, perfil e versões.
- [x] Aquisição volumétrica e controle manual continuam funcionais.

### Validação de software

- [x] Testes matemáticos aprovados.
- [x] Contratos do Hub aprovados.
- [x] Três firmwares compilados: Hub, fluxômetro e bomba.
- [x] Testes direcionados do aplicativo aprovados.
- [x] Suíte completa aprovada.
- [x] Clean e build Release aprovados.
- [x] Executável Release aberto em smoke test automatizado.
- [x] Logs novos sem erros fatais, XAML, binding, DI ou thread de UI.

### Documentação e entrega

- [x] Documento mestre atualizado nas seções 1 e 3.
- [x] Protocolos de app, Hub e nós usam as mesmas chaves.
- [x] Decisão linear antiga da bomba foi explicitamente superada.
- [x] Ocorrências remanescentes de `0.0545` foram classificadas.
- [x] Estados 🟢, 🔵, 🟡 e 🔴 refletem evidência real.
- [x] Commits estão separados, ordenados e presentes na branch final.
- [x] Árvore de trabalho está limpa quanto ao escopo; capturas e arquivos não relacionados preexistentes permanecem preservados.

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
