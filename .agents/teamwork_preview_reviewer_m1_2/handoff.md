# Relatório de Revisão Independente e Crítica Adversarial — Milestone 1
## Dispositivo 4: Sensor de Biomassa (`IMPLEMENTATION_PLAN_BIOMASSA.md` e `verify_plan_biomassa.py`)

**Revisor:** Reviewer 2 (Milestone 1)  
**Data:** 2026-09-13T16:39:00Z  
**Veredito Oficial:** **APPROVE** (Aprovado com Observações Técnicas para o Milestone 2)

---

### 1. Observation (Observações Diretas e Comprovadas)

Durante a auditoria independente do artefato `IMPLEMENTATION_PLAN_BIOMASSA.md` e do script de verificação `verify_plan_biomassa.py`, foram observados e executados diretamente os seguintes fatos, comandos e referências no código:

#### 1.1 Verificação Estrutural Automatizada do Plano
- **Comando:** `python verify_plan_biomassa.py`
- **Resultado:** Execução com Exit Code `0`.
  - Dimensão do documento: 831 linhas, 66.565 bytes.
  - Todas as 6 seções macro obrigatórias presentes (`1. Sumário Executivo`, `2. Arquitetura do Sistema...`, `3. Análise Técnica Detalhada`, `4. Matriz de Compatibilidade...`, `5. Roteiro de Implementação...`, `6. Critérios de Aceitação...`).
  - Todos os 15 itens de auditoria (§4.10 B01 a B13, além de B14 e B15) confirmados com conformidade metrológica de 100% (cada um contendo Causa Raiz/Localização, Plano de Ação/Justificativa e Classificação de Risco).
  - Checklist de homologação em bancada física (§4.11) com os 10 ensaios experimentais devidamente mapeado.

#### 1.2 Auditoria de Regressões e Testes de Contrato
- **Comando Hub:** `python -m unittest discover -s ESP32S3-HUB/tests/contracts/`
  - **Resultado:** `Ran 83 tests in 0.021s -- OK` (100% de aprovação, 0 falhas).
- **Comando App Windows:** `dotnet test Windows_app/tests/OpenTECHub.Tests --filter "FullyQualifiedName~Biomass"`
  - **Resultado:** `Aprovado! – Com falha: 0, Aprovado: 60, Ignorado: 0, Total: 60, Duração: 128 ms` (100% de aprovação).

#### 1.3 Verificação de Exatidão Técnica no Código Ativo (Firmware, Hub e Apps)
1. **B01 (Janela de Presença e Cadência):**
   - No Hub: `ESP32S3-HUB/ESP32S3-HUB/src/core/AppContext.h:371` define verbatim `const unsigned long BIOMASS_TIMEOUT = 10000;`.
   - `Telemetry.h:171–177` valida presença com `snapBiomassUpdate <= BIOMASS_TIMEOUT`.
   - `Telemetry.h:71,100` e `AppContext.h:359` já contêm a variável de captura `snapBiomassProbePeriodMs` / `biomassProbePeriodMs`, tornando a proposta de `biomassPresenceWindowMs(probePeriodMs)` perfeitamente análoga ao mecanismo de `distancePresenceWindowMs` em `AppContext.h:343`.
   - No Firmware: `Lifecycle.h:179–186` inibe heartbeat se `g_state == MEASURING`.
2. **B02 (Rotinas Bloqueantes e Flag de Aborto):**
   - `ServiceRuntime.h:1–19` define `delayServiced(uint32_t ms)`.
   - `FirmwareApp.cpp:141` já possui `volatile bool g_abortRequested = false;`.
   - `BlankingAndRange.h:7,15,29,41,75,220,230,281` já consome `g_abortRequested` para interrupção de laços.
   - `CommandCodec.h:138–139` já seta `g_abortRequested = true` sob o comando `"stop"`.
3. **B03 (Auto-Range e Smart Start):**
   - Hub `Commands.h:466–472` (`bioNewCmds`) mapeia apenas `biomassIt`, `biomassPwm`, `biomassGear`, `biomassEma`, `biomassProbePeriodMs`, omitindo `biomassAutoRange`.
   - Firmware `CommandCodec.h:221` invoca incondicionalmente `findOptimalBlankGear(startIt, startPwm)` no comando `"start"`, sobrescrevendo qualquer ajuste manual prévio.
   - Firmware `FirmwareApp.cpp:291` e `BlankingAndRange.h:131` contêm a função `bool blankIsValid(int itIndex, int pwmIndex)`.
4. **B04 (Vulnerabilidade Térmica do LED):**
   - `CommandCodec.h:80–81`:
     ```cpp
     pwmSetLevel(pwmIndex);
     if (g_state == IDLE) pwmSetDutyPercent(g_manualLedOn ? g_manualLedPct : 0.0f);
     ```
     Constatado: quando `g_state == MEASURING`, a linha 81 não executa, deixando o LED aceso continuamente em `g_config.pwmSettings[pwmIndex]` durante todo o período até a próxima amostra (até 25 s). O achado do plano é 100% verídico e crítico.
5. **B05 (Persistência NVS Coalescida):**
   - `CommandCodec.h:273–275` e `CommandCodec.h:403–453` modificam campos em `g_config` sem invocar `saveConfig()`.
6. **B07 (Identidade de Firmware):**
   - `FirmwareApp.cpp:25–26` define `FW_VERSION = "v11"` e `FW_NAME = "biomass_sensor_analog_v04_direct"`.
   - `LocalHttpApi.h:80` exibe no HTML estático `<p>Running: <b>Biomass Sensor Firmware v5.3</b></p>`.
7. **B12 (Código Vestigial `test_period`):**
   - Hub `Commands.h:460` contém `String testVal = getValueFromJson(json, "test_period");` sem que existam rotas para `test_on`/`test_off`.
8. **B13 (Sentinelas Numéricas):**
   - `MeasurementPipeline.h:110–125` emite $-99{,}0\text{ f}$ para erro de branco e $9{,}9\text{ f}$ para leitura zero de intensidade ($I = 0$).
   - App `BiomassControlViewModel.cs:754–761` formata diretamente `snapshot.BiomassAbsorbance.ToString("F3")`.
   - App `RecipeEngine.Devices.cs:132` valida apenas `s.BiomassAbsorbance > SensorReadings.NotReceived`.

#### 1.4 Apontamentos de Discrepâncias e Inconsistências Menores no Plano
- **Observação D1 (Nomenclatura de Struct em B05):**
  Em `IMPLEMENTATION_PLAN_BIOMASSA.md:448–451`, o snippet proposto utiliza `g_config.TARGET_RAW`. Na struct real (`FirmwareApp.cpp:152`, `CommandCodec.h:419`, `Stores.h:56`), o membro chama-se `OPTIMAL_TARGET_RAW`. Copiar o código sem ajuste causará erro de compilação C++.
- **Observação D2 (Caminho de Arquivo em B09):**
  Em `IMPLEMENTATION_PLAN_BIOMASSA.md:563`, cita-se `Windows_app/src/OpenTECHub.Protocol/RecipeEnums.cs:225`. O arquivo real reside em `Windows_app/src/OpenTECHub/Services/Recipes/RecipeEnums.cs` (a linha 225 está correta).
- **Observação D3 (PROGMEM em B07):**
  Em `IMPLEMENTATION_PLAN_BIOMASSA.md:525`, propõe-se concatenação `String(FW_VERSION)` dentro de `otaPage`. Como `otaPage` em `LocalHttpApi.h:76` é um `const char[] PROGMEM = R"rawliteral(...)rawliteral"`, deve-se alterar diretamente a string no literal PROGMEM ou interpolar em tempo de requisição em `handleOtaPage()`.

---

### 2. Logic Chain (Cadeia de Raciocínio Lógico)

1. **Premissa de Integridade e Não-Violação:**
   - Foi conduzida inspeção rigorosa quanto a padrões de trapaça (respostas de testes mockadas, facades, atalhos, logs fabricados).
   - O plano `IMPLEMENTATION_PLAN_BIOMASSA.md` não delega core work a ferramentas opacas nem utiliza respostas embutidas em código. A suíte de 83 testes em Python e 60 testes em C# executa asserts reais e dinâmicos contra os componentes do sistema. Não há violações de integridade.
2. **Conformidade com os Requisitos de Escopo (R1):**
   - O prompt original requeria analisar a Seção 4.10 de `COMANDOS_DISPOSITIVOS_EXTERNOS.md` e gerar um plano técnico de resolução ou justificativa fundamentada para os itens B01 a B13.
   - O plano cobriu todos os 13 itens obrigatórios e estendeu voluntariamente a análise para B14 e B15, além de detalhar o protocolo de ensaios em bancada física (§4.11).
   - Para cada item que demanda alteração (B01, B02, B03, B04, B05, B07, B09, B12, B13), o plano estipulou os pontos exatos de modificação no código; para os itens mantidos inalterados (B06, B08, B10, B11, B14, B15), forneceu sólida justificativa metrológica e de segurança física/perimetral.
3. **Análise de Risco e Segurança do Hardware:**
   - O diagnóstico do bug B04 (LED aceso em MEASURING após `set_gear`) é uma contribuição de alto valor para a preservação física dos emissores ópticos da TECNAL.
   - A proposta para B01 resolve a oscilação de presença no Hub sem violar o piso térmico de descanso escuro do sensor.
4. **Viabilidade Técnica das Propostas:**
   - Todas as propostas respeitam as restrições estritas do ecossistema: orçamento de memória flash do ESP32-S3 (evitando bibliotecas externas e reentrância de heap), separação de responsabilidades (Hub como roteador e o App como supervisor analítico) e o contrato de uma revisão por comando na caixa postal do Hub.

---

### 3. Caveats (Ressalvas e Recomendações Críticas para o Milestone 2)

Embora o plano analítico do Milestone 1 esteja plenamente aprovado, o revisor e crítico adversarial registra as seguintes **ressalvas obrigatórias para os implementadores do Milestone 2**:

1. **Ressalva Adversarial — Temporização Óptica e Polling no Core 1 (B02):**
   No arquivo `Veml7700Driver.h:100–145` (`readDirectPulsedWithBaseline`), a rotina chama `delayServiced(LED_SETTLE_MS)` e `delayServiced(BOUNDARY_POLL_MS)` com o LED aceso (`pwmSetLevel(pwmIndex)`).
   *Risco:* Se `serviceHubPolling()` fizer requisição HTTP Wi-Fi (`httpGet`) enquanto o LED estiver ligado, qualquer latência de rede manterá o LED aceso além do tempo de integração planejado, descalibrando a leitura e gerando estresse térmico.
   *Mitigação Mandatória no M2:* A rotina `serviceHubPolling()` deve incluir a guarda estrita:
   ```cpp
   if (g_targetPct > 0.0f) return; // NUNCA realizar tráfego de rede com o LED energizado!
   ```
2. **Ressalva Adversarial — Reentrância de Comandos durante Varredura de Branco (B02):**
   `pollHubForCommands()` executa `processJsonCommand(body)`. Quando em `BLANKING`, a recepção de comandos não-abortivos (como novos `start` ou `set_it`) deve ser rejeitada imediatamente para impedir corrupção de variáveis globais de medição.
3. **Ressalva de Compilação C++ (B05):**
   Na implementação do M2 em `CommandCodec.h`, utilizar o identificador real `g_config.OPTIMAL_TARGET_RAW` (em vez de `TARGET_RAW`).
4. **Ressalva de Renderização HTML OTA (B07):**
   Em `LocalHttpApi.h:80`, atualizar diretamente o literal de texto na string `PROGMEM otaPage` para `"Biomass Sensor Firmware v11.0"`, preservando o buffer estático sem alocações dinâmicas de heap.
5. **Ressalva do Script de Teste Legado (`check_firmware.py`):**
   O script `check_firmware.py` contido em `apps/desktop-python/pc_client/tests/` testa asserções textuais de uma versão legada do firmware (v4/v5) e falha 7 verificações na base modular v11 atual. Ele **não deve** ser utilizado como critério bloqueante no CI sem prévia atualização de seus padrões de busca.

---

### 4. Conclusion (Conclusão e Veredito)

- **Veredito:** **APPROVE**
- **Fundamentação:**
  1. O artefato `IMPLEMENTATION_PLAN_BIOMASSA.md` atende com rigor exemplar a todos os requisitos de escopo do Milestone 1 (Requisito R1 do prompt original e Seção 4.10 do manual de engenharia).
  2. O script de verificação `verify_plan_biomassa.py` é genuíno, funcional e aprova 100% das seções e itens B01 a B15.
  3. Não foram detectadas quaisquer violações de integridade, hardcoding fraudulento ou atalhos.
  4. As suítes de teste de regressão do Hub (83 testes) e do aplicativo Windows (60 testes) executam com 100% de sucesso.
  5. Os erros e limitações de hardware/software foram diagnosticados com precisão cirúrgica de linhas de código, e o roteiro de 4 fases para o Milestone 2 está pronto para execução.

---

### 5. Verification Method (Método de Verificação Independente)

Para auditar e reproduzir integralmente esta revisão, execute os seguintes comandos a partir da raiz do repositório (`d:\OneDrive\PosDoc_Fapesp\Automacao_e_Controle\ProjetoTECNAL`):

1. **Auditoria Automatizada do Plano de Biomassa:**
   ```bash
   python verify_plan_biomassa.py
   ```
   *Critério de Sucesso:* Saída com taxa de conformidade de 100.0% e Exit Code 0.
2. **Auditoria de Contratos do Hub ESP32-S3:**
   ```bash
   python -m unittest discover -s ESP32S3-HUB/tests/contracts/
   ```
   *Critério de Sucesso:* 83 testes executados com status `OK`.
3. **Auditoria de Testes de Biomassa no Windows App:**
   ```bash
   dotnet test Windows_app/tests/OpenTECHub.Tests --filter "FullyQualifiedName~Biomass"
   ```
   *Critério de Sucesso:* 60 testes executados com 0 falhas.
4. **Inspeção Manual dos Arquivos de Referência:**
   - Plano: `IMPLEMENTATION_PLAN_BIOMASSA.md`
   - Código Nó: `External-Devices/sensor-biomassa/firmware/biomass-sensor/src/protocol/CommandCodec.h`
   - Código Hub: `ESP32S3-HUB/ESP32S3-HUB/src/core/AppContext.h` e `Commands.h`
   - Código App: `Windows_app/src/OpenTECHub/ViewModels/BiomassControlViewModel.cs`
