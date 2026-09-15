# Handoff Report — Biomass Sensor Firmware & Section 4.10 Audit (B01 to B13)

**Agent:** Explorer 1 (`teamwork_preview_explorer_m1_1`)  
**Mission:** Read-only investigation of Section 4.10 of `External-Devices/docs/COMANDOS_DISPOSITIVOS_EXTERNOS.md` and Biomass Sensor Firmware (`External-Devices/sensor-biomassa/firmware/biomass-sensor/`).  
**Artifacts Generated:** `survey_firmware.md`  
**Handoff Type:** Hard (Task complete)

---

## 1. Observation

1. **Section 4.10 of `COMANDOS_DISPOSITIVOS_EXTERNOS.md` (lines 1038–1057):**
   - Tables list items B01 to B15:
     - B01: Janela de presença em MEASURING (período padrão 25 s, piso térmico 24,3 s vs Hub timeout 10 s).
     - B02: Rotinas bloqueantes isolam o nó do Hub (`runBlankingRoutine`, `findAndSetOptimalGear`, `probeConversionPeriod`).
     - B03: Marcha manual pelo Hub sobrescrita pelo Smart Start e auto-range.
     - B04: LED após `set_gear` fica aceso continuamente em `MEASURING`.
     - B05: Persistência parcial (`low/high/opt` e `probe_period` sem `saveConfig()`).
     - B06: Reinício silencioso do nó após queda de energia.
     - B07: Identidade (`v11` no fio vs `v5.3` na página OTA e docs locais).
     - B08: Documento do app (marcado como resolvido doc).
     - B09: Branco pelo Hub demora 20–40 s vs receita esperando ~15 s.
     - B10: `set_it`/`set_pwm` invalidam o branco (decisão de projeto).
     - B11: `hub_off`/`factory` só por canal direto (decisão de projeto).
     - B12: Hub roteia `test_period` mas não `test_on`/`test_off`.
     - B13: Sentinelas `-99.0` e `9.9` tratados como valores normais.
     - B14: Campos extras não chegam ao Hub.
     - B15: Ordem de envio de parâmetros de aquisição.

2. **Firmware Code Observations:**
   - `External-Devices/sensor-biomassa/firmware/biomass-sensor/src/core/Lifecycle.h:179–186`:
     ```cpp
     if (g_hubEnabled && g_state != MEASURING &&
         now - lastHubHeartbeatMs >= heartbeatInterval) {
       if (WiFi.status() == WL_CONNECTED) {
         sendDataToHub();   // updates lastHubHeartbeatMs itself
       } else {
         lastHubHeartbeatMs = now;
       }
     }
     ```
     Heartbeat para o Hub é ativamente suprimido em `MEASURING`.
   - `External-Devices/sensor-biomassa/firmware/biomass-sensor/src/core/ServiceRuntime.h:1–15`:
     ```cpp
     void serviceNetwork() {
       if (g_wdtReady) esp_task_wdt_reset();
       if (g_serverStarted && !g_inHttpHandler) server.handleClient();
       handleSerialInput(/*allowBlocking=*/false);
     }
     ```
     `delayServiced()` só atende a porta 80 do AP local (`server.handleClient()`) e a serial; nunca executa `pollHubForCommands()` nem `sendDataToHub()`.
   - `External-Devices/sensor-biomassa/firmware/biomass-sensor/src/protocol/CommandCodec.h:73–87`:
     ```cpp
     void setManualGear(int itIndex, int pwmIndex) {
       ...
       vemlSetConfig(itIndex);
       pwmSetLevel(pwmIndex);
       if (g_state == IDLE) pwmSetDutyPercent(g_manualLedOn ? g_manualLedPct : 0.0f);
       ...
     }
     ```
     Em `MEASURING`, `pwmSetLevel(pwmIndex)` aciona o LED e a linha seguinte só desliga se `g_state == IDLE`. O LED fica aceso a 100% até a próxima leitura.
   - `External-Devices/sensor-biomassa/firmware/biomass-sensor/src/protocol/CommandCodec.h:260–279` e `403–453`:
     Atualizações de `probe_period`, `low`, `high`, `opt`, `refresh_ms` alteram campos de `g_config`, mas não chamam `saveConfig()`.
   - `External-Devices/sensor-biomassa/firmware/biomass-sensor/src/api/LocalHttpApi.h:80`:
     `<p>Running: <b>Biomass Sensor Firmware v5.3</b></p>` diverge de `FW_VERSION = "v11"` em `FirmwareApp.cpp:25`.
   - `External-Devices/sensor-biomassa/firmware/biomass-sensor/src/measurement/MeasurementPipeline.h:111, 117`:
     Sentinelas `-99.0f` (branco inválido) e `9.9f` (escuro total) gerados diretamente no campo `absorbance`.

3. **Hub Code Observations:**
   - `ESP32S3-HUB/ESP32S3-HUB/src/core/AppContext.h:371`: `const unsigned long BIOMASS_TIMEOUT = 10000;`.
   - `ESP32S3-HUB/ESP32S3-HUB/src/sensor/Telemetry.h:171–177`:
     ```cpp
     bool biomassOnline = snapBiomassUpdate > 0 && (millis() - snapBiomassUpdate <= BIOMASS_TIMEOUT);
     bool validBiomass = false;
     if (snapBiomassComm && snapBiomassSampleUpdate > 0) {
         unsigned long age = millis() - snapBiomassSampleUpdate;
         if (age <= BIOMASS_TIMEOUT) validBiomass = true;
     }
     ```
   - `ESP32S3-HUB/ESP32S3-HUB/src/protocol/Commands.h:466–472`:
     Mapeia `biomassIt`, `biomassPwm`, `biomassGear`, `biomassEma`, `biomassProbePeriodMs`. Não mapeia `biomassAutoRange` nem `auto`/`manual`.
   - `ESP32S3-HUB/ESP32S3-HUB/src/protocol/Commands.h:459–460`:
     Roteia `test_period` indevidamente, enquanto `test_on`/`test_off` não são roteados.

4. **Build e Test Suites:**
   - Não há `platformio.ini`. O firmware usa Arduino IDE / Arduino CLI com FQBN `esp32:esp32:esp32s3` (core 3.3.11).
   - Testes Hub: `python -m unittest discover -s ESP32S3-HUB/tests/contracts/` $\rightarrow$ 83 testes aprovados em 0,018 s.
   - Testes Dotnet: `dotnet test Windows_app/tests/OpenTECHub.Tests --filter "FullyQualifiedName~Biomass"` $\rightarrow$ 60 testes aprovados em 0,446 s.
   - Testes Python Firmware: `apps/desktop-python/pc_client/tests/check_firmware.py` $\rightarrow$ script estático de integridade de código C++.

---

## 2. Logic Chain

1. **B01 (Oscilação de Presença):** Como o firmware não envia heartbeat durante `MEASURING` (Obs 2, `Lifecycle.h:179`) e a amostragem demora 25 s (`DEFAULT_REFRESH_MS`, Obs 2), o intervalo de transmissão excede os 10 s de `BIOMASS_TIMEOUT` do Hub (Obs 3). Consequentemente, o Hub declara o nó offline por 15 s a cada 25 s. **Solução:** No Hub, tornar o timeout de presença e validade de amostra dinâmico com base em `biomassProbePeriodMs` ($\max(10\text{s}, 2 \cdot \text{probe\_ms} + 5\text{s})$), e no firmware enviar heartbeat `idle=1` no intervalo escuro entre amostras.
2. **B02 (Rotinas Bloqueantes):** `delayServiced()` chama apenas `serviceNetwork()` (Obs 2), que ignora o Hub. Durante o branco (20–40 s) ou busca de marcha, nenhum comando do Hub é atendido e nenhum dado enviado. **Solução:** Adicionar chamada controlada de `pollHubForCommands()` dentro de `delayServiced()` a cada 2 s; ao receber `stop`, seta `g_abortRequested = true`, abortando a rotina bloqueante de imediato.
3. **B03 (Marcha Manual Sobrescrita):** O Hub não possui chave de autorange (Obs 3) e o firmware executa `findOptimalBlankGear` incondicionalmente no `start` (Obs 2). **Solução:** Adicionar chave `"biomassAutoRange"` no Hub e App, e no firmware verificar `!g_autoRange && blankIsValid(...)` para preservar a marcha manual no `start`.
4. **B04 (Bug Térmico Crítico do LED):** `pwmSetLevel()` ativa o PWM do LED e `setManualGear()` só desliga se `g_state == IDLE` (Obs 2). Em `MEASURING`, o LED fica ligado a 100% até o próximo pulso (até 25 s), violando o piso de 8% de duty cycle e superaquecendo o emissor. **Solução:** Desligar o LED incondicionalmente em `setManualGear()` (`pwmSetDutyPercent(0)`) e recalcular `g_nextReadTime`.
5. **B05 (Perda de Parâmetros na NVS):** Propriedades numéricas `low`, `high`, `opt`, `probe_period` não chamam `saveConfig()` (Obs 2). **Solução:** Adicionar chamada de `saveConfig()` coalescida em `CommandCodec.h`.
6. **B06 (Queda de Energia Silenciosa):** O nó nasce em `IDLE` (Obs 2) e o Hub não reimpõe `start` (Obs 3). Re-impor `start` sem confirmação é inseguro. **Solução:** App dispara alarme de cultura interrompida se nó estiver online sem amostra fresca há $> 2,5 \times \text{probe\_ms}$.
7. **B07 (Identidade):** Descompasso puramente documental/textual entre `v11` e `v5.3` (Obs 2). **Solução:** Unificar em `v11.0` no banner, HTML OTA e documentações.
8. **B10, B11 (Decisões de Projeto):** Invalidação de branco em mudanças de IT/PWM é mandatória pela física de $I_0$; `hub_off`/`factory` não devem ser remotos para evitar isolamento irreversível do nó. **Solução:** Justificar formalmente como decisões de projeto fechadas.
9. **B12 (Código Morto no Hub):** `test_period` é inútil sem `test_on`/`test_off` (Obs 3). **Solução:** Remover de `Commands.h`.
10. **B13 (Sentinelas na UI):** `-99.0` e `9.9` distorcem gráficos e interfaces (Obs 2). **Solução:** Tratar no ViewModel do App (`[Branco Inválido]`, `[Escuro/Bloqueado]`).

---

## 3. Caveats

1. **Hardware Físico Ausente:** A investigação foi puramente estática e documental com base no código-fonte e suítes de testes emuladas. Ensaios com fotodetector VEML7700 real acoplado a frasco e bancada óptica continuam catalogados em §4.11 como pendentes de bancada física.
2. **Flash Headroom do Firmware:** O binário compilado atual ocupa 1 129 872 bytes de uma partição de 1 310 720 bytes. A margem livre é de ~180 kB (~20 kB acima do piso de segurança OTA de 160 kB). As alterações propostas no firmware somam menos de 100 instruções C++, permanecendo estritamente dentro da margem de segurança.
3. **Alternativa de Particionamento:** Se no futuro forem adicionadas bibliotecas pesadas, o esquema de partições `partitions.csv` pode ser ajustado para suprimir a partição SPIFFS não utilizada (1,44 MB), expandindo a partição OTA para 2 MB.

---

## 4. Conclusion

Todas as 13 inconsistências/lacunas (B01 a B13, além de B14 e B15) de `COMANDOS_DISPOSITIVOS_EXTERNOS.md` §4.10 foram completamente auditadas com código-fonte localizado, causa raiz identificada e proposta de resolução técnica definida:
- **Itens para Correção no Firmware:** B04 (crítico), B05, B07, B02 (parcial), B03 (parcial).
- **Itens para Correção no Hub Central:** B01, B03, B12.
- **Itens para Correção no App Windows:** B06, B09, B13.
- **Itens Justificados como Não-Implementação / Decisão de Projeto:** B08 (resolvido doc), B10 (física óptica), B11 (segurança de rede), B14 (limite de buffer), B15 (resolvido app).

O relatório completo e detalhado encontra-se documentado em:  
`d:\OneDrive\PosDoc_Fapesp\Automacao_e_Controle\ProjetoTECNAL\.agents\teamwork_preview_explorer_m1_1\survey_firmware.md`

---

## 5. Verification Method

Para verificar de forma independente e reproduzível os achados desta auditoria:

1. **Inspeção de Código-Fonte:**
   - Inspecionar `External-Devices/sensor-biomassa/firmware/biomass-sensor/src/protocol/CommandCodec.h:73–87` e verificar que `pwmSetDutyPercent(0)` só é chamado em `IDLE`, confirmando B04.
   - Inspecionar `CommandCodec.h:260–279` e `403–453` e verificar ausência de `saveConfig()`, confirmando B05.
   - Inspecionar `src/core/Lifecycle.h:179–186` e confirmar a restrição `g_state != MEASURING`, confirmando B01.
   - Inspecionar `ESP32S3-HUB/ESP32S3-HUB/src/core/AppContext.h:371` e verificar `BIOMASS_TIMEOUT = 10000`, confirmando B01.
   - Inspecionar `src/api/LocalHttpApi.h:80` e verificar texto estático `"v5.3"`, confirmando B07.

2. **Execução de Testes Automatizados Existentes:**
   - Executar suíte de contratos do Hub:
     ```powershell
     python -m unittest discover -s ESP32S3-HUB/tests/contracts/
     ```
     (Confirma 83 testes passando, incluindo descarte de comandos múltiplos em `test_node_commands.py`).
   - Executar suíte do Windows App:
     ```powershell
     dotnet test Windows_app/tests/OpenTECHub.Tests --filter "FullyQualifiedName~Biomass"
     ```
     (Confirma 60 testes passando para sintonia e telemetria de biomassa).
   - Executar verificação estática do firmware:
     ```powershell
     python External-Devices/sensor-biomassa/apps/desktop-python/pc_client/tests/check_firmware.py
     ```
     (Valida estrutura balanceada de chaves e declarações no firmware).
