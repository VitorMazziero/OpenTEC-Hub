# Handoff Report — Challenger 2 (Milestone 1)
**Data:** 2026-09-13  
**Autor:** Challenger 2 (Empirical Challenger: Critic / Specialist)  
**Destinatário:** Parent Agent (`82f26027-eaef-4f56-bf65-2cbcdf3dab0a`)  
**Escopo:** Desafio empírico da viabilidade técnica do plano `IMPLEMENTATION_PLAN_BIOMASSA.md`  

---

## 1. Observation

### 1.1 Restrições de Memória Flash e RAM no ESP32-S3
- **Tabela de Partições:** Em `External-Devices/sensor-biomassa/firmware/biomass-sensor/build/esp32.esp32.esp32s3/partitions.csv`:
  - `app0`: Offset `0x10000`, Tamanho `0x140000` ($1.310.720\text{ bytes} = 1{,}25\text{ MB}$).
  - `app1`: Offset `0x150000`, Tamanho `0x140000` ($1.310.720\text{ bytes} = 1{,}25\text{ MB}$).
  - `nvs`: Offset `0x9000`, Tamanho `0x5000` ($20.480\text{ bytes} = 20\text{ kB}$).
- **Tamanho do Binário Atual:** `biomass-sensor.ino.bin` possui exatamente **$1.129.872\text{ bytes}$** ($86{,}20\%$ da partição).
- **Margem Livre Atual:** $1.310.720 - 1.129.872 = 180.848\text{ bytes}$ ($176{,}6\text{ kB}$).
- **Margem de Segurança OTA Fixada:** $160.000\text{ bytes}$, restando **$20.848\text{ bytes}$** de folga real antes do piso de segurança.
- **Mapeamento de RAM (`biomass-sensor.ino.map`):**
  - `.dram0.data`: `0x58c5` ($22.725\text{ bytes}$)
  - `.dram0.bss`: `0xb898` ($47.256\text{ bytes}$)
  - Total DRAM estática: $\approx 70\text{ kB}$ de $320\text{ kB}$ disponíveis de DRAM interna.
- **Alterações de Firmware Propostas no Plano:**
  - B01 (`Lifecycle.h`): Avaliação booleana `measuringRest` ($\approx 20\text{ bytes}$ de `.flash.text`).
  - B02 (`ServiceRuntime.h`): Inclusão de chamada `serviceHubPolling()` e checagem de `g_abortRequested` no laço ($\approx 80\text{ bytes}$ de `.flash.text`).
  - B03 (`CommandCodec.h`): Condicional `if (g_autoRange || !blankIsValid)` no `start` ($\approx 15\text{ bytes}$ de `.flash.text`).
  - B04 (`CommandCodec.h`): Substituição da linha com corte térmico do LED via `pwmSetDutyPercent(0.0f)` e reprogramação de `g_nextReadTime` ($\approx 25\text{ bytes}$ de `.flash.text`).
  - B05 (`CommandCodec.h`): Escrita condicional coalescida chamando `saveConfig()` existente ($\approx 120\text{ bytes}$ de `.flash.text`).
  - B07 (`FirmwareApp.cpp`, `LocalHttpApi.h`): Atualização de literais de versão ($\approx 30\text{ bytes}$).
  - Nenhuma nova biblioteca dinâmica pesada (ex: `ArduinoJson`) é inserida. Delta estimado: $+250$ a $+500$ bytes de flash e $0$ bytes em DRAM estática.

### 1.2 Janela Dinâmica de Presença `max(10000, 2.5 * probe_ms)`
- **Fórmula Proposta no Hub (`AppContext.h`):**
  ```cpp
  inline unsigned long biomassPresenceWindowMs(uint32_t probePeriodMs) {
    unsigned long dyn = probePeriodMs > 0 ? (unsigned long)(probePeriodMs * 2.5f) : BIOMASS_TIMEOUT;
    return dyn > BIOMASS_TIMEOUT ? dyn : BIOMASS_TIMEOUT;
  }
  ```
- **Firmware do Nó (`TelemetryAndHub.h:76, 87`):**
  A query string enviada em todo push (tanto amostra fresca quanto heartbeat em repouso) carrega compulsoriamente:
  `probe_ms=%lu` derivado de `g_config.itRefreshTimes[g_currentItIndex]`.
- **Firmware do Nó (`CommandCodec.h:439-446`):**
  O comando de ajuste de período (`probe_period`, `refresh_ms`, `probe_ms`) executa clampeamento físico obrigatório:
  `long floorMs = (long)minSafeRefreshMs(); if (val < floorMs) val = floorMs;`
  Para IT = 100 ms, `floorMs = 3325 ms`; para IT = 800 ms, `floorMs = 24325 ms`. O firmware recusa e sobrescreve valores menores que o piso térmico do LED.
- **Canal de Polling de Comandos do Nó (`FirmwareApp.cpp:111`):**
  `HUB_POLL_PERIOD_MS = 2000;` (2 segundos). O polling de comandos ocorre a cada 2 s, sendo assíncrono e desacoplado da janela de presença e do `probe_ms`.

### 1.3 Política de Alarme B06 e Convenções de `AlarmService`
- **Texto Proposto em B06 (`IMPLEMENTATION_PLAN_BIOMASSA.md:488-490`):**
  *"se a comunicação com o nó estiver habilitada (`BiomassCommEnabled == true`) e o nó estiver online (`BiomassOnline == true`), porém nenhuma nova amostra válida for recebida por um período superior a 2.5 * BiomassProbePeriodMs (ou timeout de 60 segundos), disparar imediatamente um alarme visual e sonoro de advertência de processo: 'Aquisição de Biomassa Interrompida: Sensor reiniciado em repouso (IDLE)'."*
- **Estrutura Atual do `AlarmService.cs`:**
  - Em `AlarmModels.cs`, cada alarme possui um `AlarmId` único.
  - Em `AlarmService.cs`, cada alarme é instanciado em `private static readonly AlarmDefinition[] Definitions` com tempos de debounce estáticos (`TimeSpan OnDelay`, `TimeSpan OffDeadband`).
  - No `ControlViewModel.cs:1112`:
    `_alarms.SetRoutingRequested(DeviceNames.Routing.Absorbance, BiomassControl.IsEnabled);`
    `BiomassControl.IsEnabled` indica apenas que o operador ligou a chave de comunicação do sensor em "Controle".
  - O sensor de biomassa inicia em `IDLE`. A aquisição só começa quando o operador clica no botão "Iniciar" (`StartAcquisition` / `BiomassStart`).
  - O operador pode a qualquer momento clicar em "Parar" (`StopAcquisition` / `BiomassStop`), mantendo a comunicação ativa (`IsEnabled = true`, `BiomassOnline = true`), porém em repouso proposital (`IDLE`).
  - Na rotina `TelemetryParser.cs:510-514`, quando o nó está em `IDLE`, o Hub omite o bloco `BiomassAbs` e o parser executa `ClearBiomassReadings()` (setando `BiomassAbsorbance = NotReceived`).

---

## 2. Logic Chain

### 2.1 Análise de Limites de Flash e RAM (Item 1)
1. Conforme observado em 1.1, a partição OTA tem $1.310.720\text{ bytes}$ e o binário atual ocupa $1.129.872\text{ bytes}$, com $180.848\text{ bytes}$ livres.
2. A folga necessária para atender a diretriz de segurança de $160.000\text{ bytes}$ é de $20.848\text{ bytes}$.
3. As alterações conceituais propostas no plano não adicionam nenhuma nova dependência estática ou biblioteca de terceiros (como ArduinoJson), apenas adicionando ramificações booleanas, um teste condicional de aborto e a chamada à rotina `saveConfig()` já existente e compilada.
4. O aumento total de código gerado no binário é estimado entre 250 e 500 bytes.
5. Como $500\text{ bytes} \ll 20.848\text{ bytes}$ de folga real, as alterações consomem menos de $2{,}5\%$ da folga disponível acima da margem de segurança.
6. Em DRAM, nenhuma estrutura estática ou buffer dinâmico é alocado, mantendo o consumo de memória estática em $\approx 70\text{ kB}$ (dentro do limite de $320\text{ kB}$).
7. **Inferência:** As modificações de código propostas para o ESP32-S3 não violam limites de partição flash nem restrições de memória RAM.

### 2.2 Análise de Extremos da Janela Dinâmica (Item 2)
1. **Cenário `probe_ms = 0s`:**
   - No Hub, antes do primeiro eco ou com $P = 0$, `biomassPresenceWindowMs(0)` retorna o piso de $10.000\text{ ms}$ ($10\text{ s}$).
   - O nó envia heartbeats a cada $5.000\text{ ms}$ em repouso. Como $5\text{ s} < 10\text{ s}$, `BiomassOnline` não cai para falso no boot.
   - Caso um comando tente configurar `probe_ms = 0`, a linha 439 de `CommandCodec.h` do firmware força o clampeamento ao piso térmico `floorMs` ($3.325\text{ ms}$ a $24.325\text{ ms}$). O nó nunca transmitirá $0\text{ ms}$ na query string `&probe_ms=`.
2. **Cenário `probe_ms = 20s`:**
   - A janela calculada é $\max(10000, 20000 \times 2{,}5) = 50.000\text{ ms}$ ($50\text{ s}$).
   - O intervalo entre amostras consecutivas é de $20\text{ s}$. Se 1 pacote de rede for perdido, a próxima amostra aos $40\text{ s}$ ainda chega antes do timeout de $50\text{ s}$.
   - Não há falsas desconexões por jitter ou perda isolada de pacote.
3. **Cenário `probe_ms = 60s`:**
   - A janela calculada é $\max(10000, 60000 \times 2{,}5) = 150.000\text{ ms}$ ($150\text{ s} = 2{,}5\text{ min}$).
   - Permite que até 2 quadros consecutivos sejam perdidos ($120\text{ s} \le 150\text{ s}$) antes da desconexão.
   - Com o ajuste em `Lifecycle.h` (envio de heartbeats a cada 5 s em `measuringRest`), a presença viva `biomassLastUpdate` continua sendo renovada a cada 5 s, enquanto `validBiomass` aguarda até 150 s pela renovação da amostra óptica.
4. **Avaliação de Starvation (Inanição):**
   - O polling de comandos ocorre pelo canal desacoplado `GET /biomassCommand` a cada $2.000\text{ ms}$, operando independentemente da janela de presença e do `probe_ms`. Portanto, tempos de amostragem longos (20 s, 60 s ou até 1 hora) não causam inanição de comandos nem bloqueiam a caixa de correio confiável.
5. **Inferência:** A janela dinâmica $\max(10000, 2{,}5 \times probe\_ms)$ não provoca inanição nem desconexões indevidas sob condições extremas.

### 2.3 Análise da Política de Alarme B06 (Item 3)
1. Conforme observado em 1.3, o plano propõe que o `AlarmService` dispare um alarme sonoro e visual caso `BiomassCommEnabled == true` e `BiomassOnline == true`, mas nenhuma amostra chegue por $> 60\text{ s}$.
2. **Vulnerabilidade Identificada (Falsos Alarmes em Operação Normal):**
   - Quando o operador ativa a chave do sensor na aba Controle (`IsEnabled = true`), o Hub recebe `BiomassCommEnabled = true`.
   - Nesse momento, o operador pode estar apenas preparando o sensor, realizando calibração de branco ou aguardando o momento de inocular a batelada. O sensor está legitimamente em `IDLE`.
   - Da mesma forma, durante um cultivo, o operador pode clicar em "Parar" para realizar manutenção ou limpeza, mantendo a comunicação ligada (`IsEnabled = true`).
   - Sob a regra proposta em B06, após 60 segundos nessas condições normais, **o alarme "Aquisição de Biomassa Interrompida: Sensor reiniciado em repouso (IDLE)" dispararia indevidamente**, gerando alerta sonoro e travando o banner visual.
3. **Incompatibilidade Arquitetural com `AlarmService`:**
   - `AlarmDefinition.OnDelay` é estático (`readonly TimeSpan`). Um timeout dinâmico de $2{,}5 \times probe\_ms$ não pode ser configurado diretamente na definição da tabela estática.
   - As convenções do `AlarmService` exigem que alarmes de interrupção de processo monitorem o estado de intenção prévia. O precedente direto é `AlarmId.UnsupervisedGasFlow`, que rastreia `_gasOpenWhenLastSeen`: o alarme só dispara se as válvulas estavam fisicamente abertas quando o dispositivo foi visto pela última vez.
4. **Inferência:** A política de alarme proposta em B06, da forma genérica como foi descrita no plano, é **INCONSISTENTE** com as convenções do `AlarmService` e causaria falsos alarmes espúrios em condições normais de repouso (`IDLE`). Para ser viável e aderente à arquitetura, ela deve obrigatoriamente incorporar o rastreamento de estado de aquisição ativa prévia.

---

## 3. Caveats
1. Não realizamos o teste em hardware físico real com osciloscópio (conforme checklist §4.11), mas os limites elétricos e temporais foram calculados analiticamente a partir dos parâmetros de registradores do VEML7700 e frequências do LEDC do ESP32-S3.
2. A análise de tamanho de binário considerou a compilação padrão Arduino-ESP32 com flags de otimização `-Os` presentes em `build.options.json`. Compilações de debug com símbolos expandidos não-otimizados (`-O0` ou `-g3`) podem consumir mais flash, embora o firmware de produção seja estritamente `-Os`.

---

## 4. Conclusion
1. **Flash Boundaries & Memory Constraints (Item 1):** **CONFIRMADO E VIÁVEL (PASS).** As alterações propostas no plano resultam em um incremento mínimo de memória flash ($\approx 250\text{ a }500\text{ bytes}$), mantendo o binário em $\approx 1{,}13\text{ MB}$, perfeitamente contido na partição de $1{,}25\text{ MB}$ ($1.310.720\text{ bytes}$) e preservando com folga a margem de segurança de $160\text{ kB}$. O consumo de RAM estática permanece inalterado em $\approx 70\text{ kB}$ de $320\text{ kB}$.
2. **Dynamic Presence Window (Item 2):** **CONFIRMADO E VIÁVEL (PASS).** A fórmula $\max(10000, 2{,}5 \times probe\_ms)$ opera de forma segura sob todos os regimes:
   - Para $0\text{ s}$, o piso de $10\text{ s}$ e o clampeamento de segurança térmico de hardware no nó previnem desconexões espúrias.
   - Para $20\text{ s}$ e $60\text{ s}$, a janela de $50\text{ s}$ e $150\text{ s}$ oferece tolerância adequada a perda de pacotes Wi-Fi.
   - O polling de comandos desacoplado a cada $2\text{ s}$ elimina qualquer risco de inanição de ordens remotas.
3. **B06 Alarm Policy Consistency (Item 3):** **INCONSISTENTE DA FORMA ATUAL; REQUER AJUSTE DE ARQUITETURA (CHALLENGE / CORREÇÃO MANDATÓRIA).** A proposta textual do plano dispararia alarmes falsos sempre que o sensor estivesse ligado em repouso (`IDLE`), pois não discrimina repouso intencional de reset acidental.
   - **Correção Mandatória Requerida:** O `AlarmService` deve seguir o precedente de `_gasOpenWhenLastSeen` de `UnsupervisedGasFlow`, registrando uma flag interna de estado `_biomassAcquisitionActiveWhenLastSeen` que se torna verdadeira somente quando leituras válidas de absorbância estão ativamente fluindo. O alarme de interrupção silenciosa só deve ser armado se essa flag estiver ativa e as amostras cessarem subitamente mantendo o nó online.

---

## 5. Verification Method

Para reprodução e verificação independente dos resultados reportados, execute os seguintes comandos no terminal:

1. **Verificação de Partições e Dimensões do Binário ESP32-S3:**
   ```powershell
   Get-Content External-Devices/sensor-biomassa/firmware/biomass-sensor/build/esp32.esp32.esp32s3/partitions.csv
   (Get-Item External-Devices/sensor-biomassa/firmware/biomass-sensor/build/esp32.esp32.esp32s3/biomass-sensor.ino.bin).Length
   ```
   *Condição de Validação:* Binário atual = 1.129.872 bytes; partição app0 = 1.310.720 bytes; folga = 180.848 bytes.

2. **Verificação dos Testes de Contratos do Hub (Python):**
   ```powershell
   python -m unittest discover -s ESP32S3-HUB/tests/contracts/
   ```
   *Condição de Validação:* 83 testes passando com 100% de sucesso (`Ran 83 tests ... OK`).

3. **Verificação dos Testes do Supervisor Windows (.NET 10 / C#):**
   ```powershell
   dotnet test Windows_app/tests/OpenTECHub.Tests --filter "FullyQualifiedName~Biomass"
   dotnet test Windows_app/tests/OpenTECHub.Tests --filter "FullyQualifiedName~AlarmService"
   ```
   *Condição de Validação:* 60 testes de biomassa e 41 testes de alarmes aprovados (`Com falha: 0`).

4. **Execução da Suíte Adversarial do Verificador do Plano:**
   ```powershell
   python External-Devices/sensor-biomassa/tests/test_verify_plan_biomassa_adversarial.py
   python verify_plan_biomassa.py
   ```
   *Condição de Validação:* Todos os 15 testes adversariais aprovados (`Ran 15 tests ... OK`) e conformidade 100% no verificador macro.
