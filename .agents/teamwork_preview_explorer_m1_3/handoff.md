# Relatório de Handoff — Explorer 3: Apps e Documentação para B01–B13 (Sensor de Biomassa)

## 1. Observation

Durante a investigação detalhada dos subsistemas de software (Python e Windows OpenTECHub), da documentação normativa e dos planos de implementação anteriores, foram observados os seguintes fatos:

1. **Janela de Presença Fixa no Hub e Desconexão em MEASURING (B01):**
   - `ESP32S3-HUB/ESP32S3-HUB/src/core/AppContext.h:371`: `const unsigned long BIOMASS_TIMEOUT = 10000;` fixa a janela de presença em 10 segundos.
   - `ESP32S3-HUB/ESP32S3-HUB/src/sensor/Telemetry.h:171-176`:
     ```cpp
     bool biomassOnline = snapBiomassUpdate > 0 && (millis() - snapBiomassUpdate <= BIOMASS_TIMEOUT);
     bool validBiomass = false;
     if (snapBiomassComm && snapBiomassSampleUpdate > 0) {
         unsigned long age = millis() - snapBiomassSampleUpdate;
         if (age <= BIOMASS_TIMEOUT) validBiomass = true;
     }
     ```
   - Em contraste, o período padrão em medição (`probe_ms`) é de 25.000 ms (`BiomassControlViewModel.cs:130`). Em `MEASURING`, o nó só empurra `/biomassData` a cada `probe_ms`, fazendo com que `biomassOnline` e `validBiomass` fiquem falsos por 15 segundos entre cada amostra, limpando as leituras no app (`BiomassControlViewModel.cs:756`) e acionando o alarme `AlarmId.BiomassOffline` (`AlarmService.cs:529-532`).

2. **Marcha Manual e Falta de Roteamento de Auto-Range no Hub/App (B03):**
   - No firmware do nó (`External-Devices/sensor-biomassa/firmware/biomass-sensor/src/protocol/CommandCodec.h:236-239`), o nó suporta `{"command":"auto"}` e `{"command":"manual"}` (e `{"auto":1}`, `{"manual":1}`).
   - No Hub (`ESP32S3-HUB/ESP32S3-HUB/src/protocol/Commands.h:466-472`), a lista `bioNewCmds[]` só mapeia `biomassIt`, `biomassPwm`, `biomassGear`, `biomassEma` e `biomassProbePeriodMs`. Não existe chave nem rota para `auto` ou `manual`.
   - No Windows App, `CommandKeys.cs` e `CommandBuilders.cs` não possuem constantes ou métodos para `biomassAutoRange`. O usuário pode alterar `AcquisitionGainGearText`, mas como o nó está em auto-range, a marcha é sobrescrita no próximo ciclo fora de faixa ou ao emitir `start` (Smart Start em `CommandCodec.h:221`).

3. **LED Aceso Contínuo Após `set_gear` em MEASURING (B04):**
   - `External-Devices/sensor-biomassa/firmware/biomass-sensor/src/sensor/Veml7700Driver.h:246-252`: `pwmSetLevel(pwmIndex)` chama `pwmSetDutyPercent(g_config.pwmSettings[pwmIndex])`, ligando o LED no duty configurado.
   - `External-Devices/sensor-biomassa/firmware/biomass-sensor/src/protocol/CommandCodec.h:80-81`:
     ```cpp
     pwmSetLevel(pwmIndex);
     if (g_state == IDLE) pwmSetDutyPercent(g_manualLedOn ? g_manualLedPct : 0.0f);
     ```
     Se `g_state == MEASURING`, a linha 81 é ignorada, mantendo o LED energizado continuamente até a próxima amostra (até 25 s), violando o teto térmico de 8% de duty.

4. **Ausência de Persistência NVS para Limiares e Período (B05):**
   - `External-Devices/sensor-biomassa/firmware/biomass-sensor/src/protocol/CommandCodec.h:403-453`: As chaves `low`, `high`, `opt` e `refresh_ms`/`probe_ms`/`probe_period` alteram variáveis na struct em RAM `g_config`, mas não invocam `saveConfig()`. Os parâmetros só persistem se um comando de calibração posterior salvar o blob.

5. **Tratamento de Sentinelas de Absorbância no App (B13):**
   - No firmware do nó (`src/core/FirmwareApp.h` / `pc_client/biomass_core.py:44-45`), os valores sentinelas são `ABS_ERROR_BLANK = -99.0` e `ABS_ERROR_ZERO = 9.9`.
   - No Windows App (`BiomassControlViewModel.cs:754-761`), `snapshot.BiomassAbsorbance <= SensorReadings.NotReceived` (onde `NotReceived = -1.0`). Para `-99.0`, limpa a tela sem avisar erro de branco. Para `9.9`, `9.9 > -1.0`, formatando como número `"9,900"`.
   - Em `RecipeEngine.Devices.cs:132`:
     ```csharp
     s => !s.HasBiomassTelemetry || (s.BiomassOnline && s.BiomassAbsorbance > SensorReadings.NotReceived)
     ```
     A leitura de sentinela `9.9` satisfaz a condição `> -1.0`, liberando a receita inadvertidamente.
   - No cliente Python (`biomass_core.py:104-106`), o sentinela já é tratado corretamente via `Sample.valid`.

6. **Anatomia das Diretrizes Fechadas na Documentação:**
   - Em `External-Devices/docs/COMANDOS_DISPOSITIVOS_EXTERNOS.md`: Seção 1.0 (Bomba, linhas 63-72), Seção 2.0 (Distância, linhas 310-330) e Seção 3.0 (Fluxômetro, linhas 629-640) possuem a subseção canônica `#### 🔵 Diretrizes de Segurança e Decisões de Arquitetura Fechadas` com colunas `| Decisão / Recurso | Onde Opera | Comportamento e Justificativa Técnica | Estado |`.
   - Na Seção 4.0 (Biomassa, linhas 874-888), existe atualmente a tabela provisória `#### 🔴 Lacunas, Inconsistências e Decisões (§4.10)`.

7. **Padrão de Planos de Implementação Anteriores:**
   - `External-Devices/docs/Planos/IMPLEMENTATION_PLAN_FLUXOMETRO.md` e `IMPLEMENTATION_PLAN_BOMBA.md` seguem um padrão de 6 seções macros: 1. Sumário Executivo; 2. Arquitetura do Sistema; 3. Análise Detalhada; 4. Matriz de Compatibilidade; 5. Roteiro em Fases; 6. Critérios de Aceitação.
   - Seus scripts de validação (`verify_plan.py` e `verify_plan_bomba.py`) validam a existência dos IDs em seções, a presença das 6 seções e termos de causa raiz, plano de modificação e segurança.

---

## 2. Logic Chain

1. **Da Observação 1 (B01):** Como o Hub possui um timeout fixo de 10 s (`BIOMASS_TIMEOUT`), mas o nó de biomassa em `MEASURING` transmite amostras espaçadas pelo período configurável `probe_ms` (mínimo térmico ~24,3 s, padrão 25 s, máximo 60 s), o nó inevitavelmente fica "ausente" para o Hub entre as amostras. A solução idêntica à já aprovada e aplicada para o Sensor de Distância (D03) em `AppContext.h:343` (`distancePresenceWindowMs = max(3 s, 2.5 * send_period)`) resolve completamente a instabilidade de `BiomassOnline` sem exigir alterações no firmware do nó, bastando implementar `biomassPresenceWindowMs(probe_ms) = max(10000UL, (unsigned long)(probe_ms * 2.5f))`.
2. **Da Observação 2 (B03):** O nó já implementa os comandos `auto` e `manual` (`CommandCodec.h:236-239`), mas o Hub não os roteia e o app não os emite. Logo, a adição da chave `biomassAutoRange` em `Commands.h` do Hub e nos construtores do OpenTECHub (`CommandKeys.cs`, `CommandBuilders.cs`, `BiomassControlViewModel.cs`) é suficiente e não requer alterações de memória ou flash no firmware do nó.
3. **Da Observação 3 (B04) e 4 (B05):** O firmware do nó em `CommandCodec.h` apresenta dois bugs clássicos de máquina de estados: não apagar o PWM do LED se não estiver em `IDLE` durante `setManualGear()`, e esquecer de chamar `saveConfig()` nos ramos de limiares e período. A correção é estritamente pontual nessas linhas do nó.
4. **Da Observação 5 (B13) e (B06):** No OpenTECHub, os sentinelas `-99.0` e `9.9` precisam ser formalmente tratados como estados de exceção visual (`"Inválido (Branco)"` e `"Opaco (Luz Zero)"`) e bloqueados nas receitas de início, exatamente como já faz o cliente Python em `biomass_core.py`. Um alarme de supervisão para nó ligado e habilitado mas sem receber dados por $> 2 \times \text{probe_ms}$ protege contra quedas silenciosas em culturas longas.
5. **Das Observações 6 e 7:** A documentação em `COMANDOS_DISPOSITIVOS_EXTERNOS.md` deve converter a tabela 4.0 de `🔴 Lacunas` para a tabela canônica `🔵 Diretrizes de Segurança e Decisões de Arquitetura Fechadas`, e `HUB_PROTOCOL_IMPROVEMENTS.md` deve registrar a aplicação de B01 e B03. O plano `IMPLEMENTATION_PLAN_BIOMASSA.md` e o script `verify_plan_biomassa.py` devem espelhar rigorosamente o formato de 6 seções e validação automatizada estabelecido nos dispositivos 1 e 3.

---

## 3. Caveats

1. **Headroom de Memória Flash do Nó:** O nó de biomassa possuía apenas ~5,3 kB livres antes do limite de partição em 2026-09-12. As alterações no nó propostas (B04 apagar LED e B05 chamar `saveConfig`) utilizam funções já existentes (`pwmSetDutyPercent` e `saveConfig`), consumindo praticamente zero bytes adicionais de flash.
2. **Cliente Python:** O cliente Python não interage com o Hub, operando via AP próprio ou USB CDC. Nenhuma alteração de protocolo é exigida no Python para suportar o Hub, apenas a sincronização de strings de versão e documentação.

---

## 4. Conclusion

A investigação técnica dos aplicativos e da documentação para os itens B01–B13 está **100% concluída**.
O arquivo detalhado de diagnóstico `survey_apps_docs.md` foi gerado em `.agents/teamwork_preview_explorer_m1_3/survey_apps_docs.md`.
Estão especificados com exatidão:
- As modificações de código para o Firmware (`CommandCodec.h`, `LocalHttpApi.h`).
- As modificações para o Hub (`AppContext.h`, `Telemetry.h`, `Commands.h`).
- As modificações para o OpenTECHub (`CommandKeys.cs`, `CommandBuilders.cs`, `SensorReadings.cs`, `BiomassControlViewModel.cs`, `AlarmService.cs`, `RecipeEngine.cs`).
- A tabela oficial de 12 decisões fechadas a ser inserida em `COMANDOS_DISPOSITIVOS_EXTERNOS.md` §4.0.
- A estrutura e os testes do verificador `verify_plan_biomassa.py` para homologar `IMPLEMENTATION_PLAN_BIOMASSA.md`.

---

## 5. Verification Method

Para verificar independentemente os achados e a consistência deste relatório:

1. **Inspeção de Arquivos do Relatório:**
   - `view_file` no relatório completo: `.agents/teamwork_preview_explorer_m1_3/survey_apps_docs.md`.
2. **Verificação de Contratos do Hub:**
   - Executar a suíte de testes de contrato do Hub:
     `pytest ESP32S3-HUB/tests/contracts/test_node_commands.py -k biomass`
3. **Verificação da Suíte de Testes do OpenTECHub (Windows App):**
   - Executar os testes de biomassa no .NET:
     `dotnet test Windows_app/tests/OpenTECHub.Tests/OpenTECHub.Tests.csproj --filter "FullyQualifiedName~Biomass"`
4. **Verificação dos Scripts de Validação de Plano Anteriores:**
   - `python verify_plan.py` (deve retornar Exit Code 0 para o Fluxômetro).
   - `python verify_plan_bomba.py` (deve retornar Exit Code 0 para a Bomba).
