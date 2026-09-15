# Levantamento Técnico: Requisitos de Aplicativos e Documentação para o Sensor de Biomassa (B01–B13)

**Autor:** Explorer 3 (Teamwork Preview — Apps & Documentation)  
**Data de Emissão:** 2026-09-13  
**Status:** Concluído / Investigação Analítica Read-Only  
**Escopo:** Subsistemas de Software (Python e Windows .NET/C# OpenTECHub), Documentação Técnica (`COMANDOS_DISPOSITIVOS_EXTERNOS.md` e `HUB_PROTOCOL_IMPROVEMENTS.md`) e Padrões de Planos de Implementação (`IMPLEMENTATION_PLAN_*.md`).

---

## 1. Introdução e Contextualização do Problema

O Sensor de Biomassa (Dispositivo 4) é um sensor óptico turbidimétrico/espectrofotométrico baseado no chip ALS VEML7700 (I²C `0x10`) e em um LED emissor controlado por PWM (GPIO 18, LEDC 2 kHz). Ele opera na frota de dispositivos externos sob o gateway **ESP32S3-HUB** (v10.2) e é supervisionado por dois ecossistemas de aplicativos:
1. **Cliente Python de Bancada / Laboratório (`External-Devices/sensor-biomassa/apps/desktop-python/`):** Aplicação para conexão direta com o nó via USB Serial (115200) ou Wi-Fi SoftAP (`192.168.7.1`).
2. **OpenTECHub Windows App (`Windows_app/`):** Sistema supervisório industrial multiprocesso (.NET 8 WPF / C#), comunicando-se via HTTP REST com o Hub Central (192.168.4.1).

A auditoria da Seção 4.10 de `COMANDOS_DISPOSITIVOS_EXTERNOS.md` catalogou 13 inconsistências e limitações primárias (**B01 a B13**) entre firmware, Hub e aplicativos, além dos itens complementares B14 e B15.

Este documento detalha o diagnóstico completo de software e documentação, fornecendo o mapa exato de alterações, tabelas arquiteturais fechadas e especificações de scripts de verificação para suportar a execução do plano mestre.

---

## 2. Investigação dos Aplicativos (Python e Windows .NET/C#)

### 2.1 Aplicativo Python Desktop (`External-Devices/sensor-biomassa/apps/desktop-python/`)

#### 2.1.1 Arquitetura e Transporte Atual
- **Módulo Núcleo (`pc_client/biomass_core.py`):**
  - Desacoplado de interface gráfica, opera headless para cultivos longos (dias/semanas).
  - Suporta dois transportes polimórficos (`Transport`): `SerialTransport` (linhas JSON contínuas via USB CDC / UART) e `HttpTransport` (REST polling no AP `192.168.7.1`).
  - **Tratamento de Gaps e Reconstrução Temporal:** O nó mantém ring buffer de 1.024 amostras com `seq` monotônico. Se o cliente sofrer desconexão temporária, detecta descontinuidade de sequência e faz backfill automático via `GET /api/history?since=N`.
  - **Sentinelas Ópticos já Nativos:**
    O Python já define explicitamente em `biomass_core.py:44-45`:
    ```python
    ABS_ERROR_BLANK = -99.0  # blank was 0 or saturated -> absorbance meaningless
    ABS_ERROR_ZERO = 9.9     # measured I == 0 -> effectively infinite absorbance
    ```
    E em `Sample.valid` (`biomass_core.py:104-106`):
    ```python
    @property
    def valid(self) -> bool:
        return ABS_ERROR_BLANK + 1 < self.absorbance < ABS_ERROR_ZERO - 0.001
    ```
- **Interface Gráfica (`pc_client/biomass_gui.py`):**
  - Suporta controle de auto-range via método `_on_auto_toggle()` (`biomass_gui.py:1795`), enviando `command: auto` ou `command: manual`.
  - Suporta rotina de branco guiada (`_send_blank`) com opção de cadenciamento térmico (`duty_pct`).
  - Suporta envio atômico de limiares (`low`, `high`, `opt`) e seleção de marcha manual (`_send_gear`).
- **Relação com os Itens B01–B13:**
  - **B01 (Janela de Presença):** Não afeta o cliente Python, pois ele se comunica diretamente com o nó (via AP `192.168.7.1` ou Serial), sem passar pelo filtro de timeout de 10 s do Hub.
  - **B03 (Auto-range):** O cliente Python já suporta perfeitamente os comandos `auto` e `manual`.
  - **B07 (Identidade):** Scripts de validação (`tests/check_firmware.py`) e cabeçalhos ainda mencionam rótulo histórico v5.0/v5.3, demandando alinhamento documental com `v11`.
  - **B13 (Sentinelas):** O cliente Python é a referência correta do ecossistema, já tratando `-99.0` e `9.9` como sentinelas inválidos.

---

### 2.2 Aplicativo Windows OpenTECHub (`Windows_app/` - .NET 8 / C#)

#### 2.2.1 Componentes Envolvidos no Fluxo de Biomassa
A cadeia de comunicação da biomassa no OpenTECHub percorre os seguintes módulos:
```
OpenTECHub UI (SynopticView / ControlView)
      │
      ▼
BiomassControlViewModel.cs (Controle, Telemetria e Fila de Sintonia)
BiomassCalibrationViewModel.cs (Assistente de Branco e Calibração)
      │
      ▼
CommandBuilders.cs / CommandKeys.cs (Construção de Comandos JSON)
      │
      ▼
CommandArbiter.cs (Arbitragem de Posse via ActuatorId.Biomass)
      │
      ▼
ConnectionManager.cs / HubProtocolClient.cs (HTTP REST /command ao Hub)
      │
      ▲ Telemetria via HTTP GET /readData
      │
TelemetryParser.cs ──> SensorReadings.cs ──> SensorSnapshot
      │
      ▼
AlarmService.cs (Supervisão de Alarmes: AlarmId.BiomassOffline)
RecipeEngine.ExternalDevices.cs (Execução de Receitas Automatizadas)
```

#### 2.2.2 Diagnóstico Detalhado por Item B01–B13 no Windows App

| Item | Status no Windows App | Arquivo Afetado e Linha | Diagnóstico e Impacto no App | Ação de Código Necessária |
|---|:---:|---|---|---|
| **B01** | 🔴 Falha Indireta | `BiomassControlViewModel.cs:754`<br>`AlarmService.cs:529-532` | Como o Hub derruba `BiomassOnline` após 10 s em `MEASURING` (período padrão 25 s), o app limpa a leitura para `"—"` e dispara o alarme *Absorbância offline* a cada amostra. | A correção primária reside no Hub (`biomassPresenceWindowMs`). No App Simulator (`WireCodec.cs:132`), refletir a janela dinâmica proporcional ao período. |
| **B03** | 🔴 Lacuna Crítica | `CommandKeys.cs:108-120`<br>`CommandBuilders.cs:850-895`<br>`BiomassControlViewModel.cs:124` | O operador pode selecionar a marcha `AcquisitionGainGearText` (0..31), mas o app não tem comando para desligar o auto-range (`biomassAutoRange`). O nó volta a trocar de marcha no próximo ciclo fora da faixa. | 1. Adicionar `CommandKeys.BiomassAutoRange = "biomassAutoRange"`.<br>2. Criar `CommandBuilders.BiomassAutoRange(bool autoRange)`.<br>3. Adicionar seletor/toggle no `BiomassControlViewModel.cs` para travar em modo manual. |
| **B06** | 🔴 Risco de Processo | `AlarmService.cs:227, 529-532` | Se o nó reiniciar durante uma cultura longa, ele volta em `IDLE`. Ele envia heartbeat (`idle=1`), logo `BiomassOnline` fica `true`. O alarme `BiomassOffline` **não dispara**, e a cultura continua desassistida sem medição. | Adicionar verificação no `AlarmService.cs` ou `BiomassControlViewModel.cs`: se `BiomassCommEnabled && BiomassOnline`, mas sem novas amostras válidas por $> 2 \times \text{probe_ms}$, sinalizar alerta de "Aquisição de Biomassa Interrompida". |
| **B07** | 🟢 Compatível | `NodeFirmwareCatalog.cs:40` | `[Biomass] = new(StringComparer.OrdinalIgnoreCase) { "v11" }`. Já valida estritamente `"v11"`. | Garantir suporte a `"v11"` e `"v11.0"` para paridade com o fluxômetro. |
| **B08** | 🟢 Resolvido | `Windows_app/docs/PROTOCOL.md` | Corrigido em 2026-09-13 (alinhado a `ema`, `probe_period` e `ReliableMailbox`). | Manter documentação atualizada. |
| **B09** | 🟡 Desvio Documental | `RecipeEngine.ExternalDevices.cs:101`<br>`RecipeEnums.cs:225` | Mensagem de log e documentação da ação `Blank` afirmam que a varredura leva "~15 s". Na tabela real (4 IT × 8 PWM), leva entre 20 s e 40 s (típico 30 s). Temporizadores de 15 s em receitas disparam `start` com o nó `Busy`. | Atualizar o texto de log e a documentação XML em `RecipeEngine.ExternalDevices.cs` e `RecipeEnums.cs` para alertar tempo real de 20–40 s (típico 30 s). |
| **B13** | 🔴 Falha de Apresentação | `SensorReadings.cs:20`<br>`TelemetryParser.cs:516`<br>`BiomassControlViewModel.cs:754`<br>`RecipeEngine.Devices.cs:132` | 1. Absorbância `-99.0` (branco saturado/inválido) cai em `< -1.0` e mostra `"—"` sem avisar que houve erro.<br>2. Absorbância `9.9` (luz zero / amostra opaca) é exibida como valor numérico válido `"9,900"`.<br>3. `RecipeEngine.Devices.cs:132` aceita `9.9` como amostra válida para liberar a receita de início! | 1. Criar sentinelas em `SensorReadings.cs` (`BiomassBlankInvalid = -99.0`, `BiomassZeroLight = 9.9`).<br>2. No `BiomassControlViewModel.cs`, exibir rótulos de alerta: `"Inválido (Branco)"` e `"Opaco (Luz Zero)"`.<br>3. Em `RecipeEngine.Devices.cs:132`, exigir leitura válida finita (`s.BiomassAbsorbance > -90.0 && s.BiomassAbsorbance < 9.89`). |

---

## 3. Investigação da Documentação e Decisões de Arquitetura

### 3.1 Anatomia da Seção "🔵 Diretrizes de Segurança e Decisões de Arquitetura Fechadas" em `COMANDOS_DISPOSITIVOS_EXTERNOS.md`

Ao inspecionar a Seção 1 (Bomba, linhas 63-72), Seção 2 (Distância, linhas 310-330) e Seção 3 (Fluxômetro, linhas 629-640), constata-se um padrão rigoroso e consistente de documentação sob o tópico `<X>.0 Painel de Navegação Rápida`:

#### Estrutura Canônica das Tabelas
1. **Cabeçalho:**
   `#### 🔵 Diretrizes de Segurança e Decisões de Arquitetura Fechadas`
   *Subtítulo explicativo:* `Decisões de engenharia aprovadas que definem o comportamento seguro do sistema.`
2. **Colunas Obrigatórias:**
   `| Decisão / Recurso | Onde Opera | Comportamento e Justificativa Técnica | Estado |`
3. **Padrão dos Estados Adotados:**
   - `🟢 Fechado (§X.Y #Z)`: Implementado em código e aprovado em suíte de testes.
   - `🟡 Pendente de ensaio (§X.11)`: Implementado em código, aguardando validação com bancada física.
   - `🔵 Decisão de projeto`: Escolha deliberada de engenharia / não-implementação, sem alteração de código pendente.

#### Situação Atual do Dispositivo 4 (Sensor de Biomassa)
Atualmente, a Seção 4.0 contém:
- `#### 🟢 Implementado e Integrado de Ponta a Ponta` (linhas 844-855)
- `#### 🟡 Implementado no Software, Aguardando Ensaio Físico na Bancada` (linhas 858-871)
- `#### 🔴 Lacunas, Inconsistências e Decisões (§4.10)` (linhas 874-888)

**Requisito de Migração Documental:**  
A tabela `🔴 Lacunas` deve ser resolvida e substituída pela tabela padronizada `#### 🔵 Diretrizes de Segurança e Decisões de Arquitetura Fechadas`, consolidando a resolução dos itens B01 a B13 e B14–B15.

---

### 3.2 Matriz de Decisões Fechadas Necessária para o Dispositivo 4 (Sensor de Biomassa)

Abaixo está a especificação exata da tabela que integrará a Seção 4.0 do manual `COMANDOS_DISPOSITIVOS_EXTERNOS.md`:

```markdown
#### 🔵 Diretrizes de Segurança e Decisões de Arquitetura Fechadas
*Decisões de engenharia aprovadas que definem o comportamento seguro do sistema.*

| Decisão / Recurso | Onde Opera | Comportamento e Justificativa Técnica | Estado |
|---|---|---|:---:|
| **Janela de presença proporcional a `probe_ms` (B01)** | Hub 10.2 (`AppContext.h`, `Telemetry.h`) | A presença `BiomassOnline` e a validade de amostra `validBiomass` utilizam `biomassPresenceWindowMs(probe_ms) = max(10000UL, (unsigned long)(probe_ms * 2.5f))`. Elimina a oscilação de presença e disparos falsos de `BiomassOffline` durante aquisições periódicas normais (25 s a 60 s). | 🟢 Fechado (§4.10 B01) |
| **Isolamento e atomicidade de rotinas bloqueantes (B02)** | Firmware v11 / Hub 10.2 / App | Varreduras de branco e busca de marcha executam sob `delayServiced()` alimentando WDT. Comandos no app são serializados atrás de `BiomassCommandPending`. O comando `stop` aborta localmente por flag atômica `g_abortRequested`. Não se injeta cliente HTTP reentrante na varredura óptica para proteger memória e estabilidade do Core 1. | 🟢 Fechado (§4.10 B02) |
| **Roteamento de auto-range e marcha manual (B03)** | Hub 10.2 / App Windows | O Hub roteia a chave `biomassAutoRange` (`{"command":"auto"|"manual"}`). O aplicativo expõe seletor explícito de modo automático vs manual. Ao travar marcha manual, o auto-range é desativado no nó (`g_autoRange = false`), evitando substituição involuntária de marcha. | 🟢 Fechado (§4.10 B03) |
| **Corte térmico imediato do LED após `set_gear` (B04)** | Firmware v11 (`CommandCodec.h`) | `setManualGear()` força `pwmSetDutyPercent(0.0f)` ao ajustar nova marcha fora de IDLE (em `MEASURING`) e reprograma `g_nextReadTime`, impedindo que o LED fique aceso continuamente no intervalo entre pulsos e garantindo conformidade com o limite térmico de duty cycle $\le 8\%$. | 🟢 Fechado (§4.10 B04) |
| **Gravação NVS seletiva de limiares e período (B05)** | Firmware v11 (`CommandCodec.h`) | Alterações de `low`, `high`, `opt` e `probe_period` acionam `saveConfig()`. A persistência ocorre apenas mediante alteração efetiva dos valores em relação à RAM, preservando a vida útil da memória flash NOR contra desgastes por reenvio de quadros. | 🟢 Fechado (§4.10 B05) |
| **Supervisão contra reinício silencioso pós-queda (B06)** | Windows App (`AlarmService.cs`) | O sensor nasce em `IDLE` por segurança após queda de energia. O aplicativo monitora o estado ativo de aquisição e dispara alarme de processo caso o nó esteja online e habilitado mas sem receber amostras por $> 2 \times \text{probe_ms}$, prevenindo a interrupção oculta de monitoramento biológico. | 🟢 Fechado (§4.10 B06) |
| **Identidade unificada de release `v11` (B07)** | Firmware v11 / Docs / App | Constante `FW_VERSION "v11"` unificada no banner serial, `/nodeHello`, página web OTA (`LocalHttpApi.h`), `/diag` e documentação técnica. O catálogo `NodeFirmwareCatalog` do Windows App valida a versão oficial. | 🟢 Fechado (§4.10 B07) |
| **Temporização estendida da rotina de branco (B09)** | Windows App (`RecipeEngine.ExternalDevices.cs`) | Documentação de receitas e avisos de operação atualizados para o tempo real medido da varredura completa não cadenciada (20 a 40 s, típico 30 s), prevenindo envios prematuros de `start` enquanto o nó opera em `BLANKING`. | 🟢 Fechado (§4.10 B09) |
| **Invalidação estrita do branco por alteração óptica (B10)** | Firmware v11 / Nó | Reconfigurações de hardware (`set_it`, `set_pwm`, `pwm_preset`) alteram a resposta do fototransistor e invalidam a matriz de referência $I_0$, forçando retorno a `IDLE`. Decisão mantida por rigor metrológico. | 🔵 Decisão de projeto |
| **Isolamento de rede e comandos destrutivos (B11)** | Hub 10.2 / Nó | Comandos `hub_off` e `factory` não são expostos pelo Hub, permanecendo exclusivos de acesso físico USB serial e SoftAP local, prevenindo desativação remota acidental do canal de comunicação. | 🔵 Decisão de projeto |
| **Saneamento de comando vestigial `test_period` (B12)** | Hub 10.2 (`Commands.h`) | Remoção do repasse da chave `test_period` na ausência de chaves de teste contínuo (`test_on`/`test_off`), eliminando tráfego ocioso na caixa de correio confiável. | 🟢 Fechado (§4.10 B12) |
| **Tratamento discriminado de sentinelas de erro óptico (B13)** | Windows App (`BiomassControlViewModel`, `RecipeEngine`) | O aplicativo converte absorbâncias de erro ($-99.0$ e $9.9$) em estados textuais de alerta no supervisor (`"Inválido (Branco)"` e `"Opaco (Luz Zero)"`). O motor de receitas rejeita $9.9$ como dado de partida válido. | 🟢 Fechado (§4.10 B13) |
| **Preservação do payload da query string GET (B14)** | Firmware v11 / Hub 10.2 | Metadados diagnósticos secundários (`hd_mode`, `sat`, `boot_id`) permanecem na rota serial/AP local. O push para o Hub preserva o tamanho de query string $< 320$ bytes até a migração coordenada para JSON POST. | 🔵 Decisão de projeto |
| **Sequenciamento atômico de sintonia de marcha (B15)** | Windows App (`CommandBuilders.cs`) | O construtor `BiomassTuning` emite obrigatoriamente a seleção de marcha linear (`biomassGear`) antes das reconfigurações de slot (`biomassIt`, `biomassPwm`), garantindo que o slot pretendido seja o alvo da escrita. | 🟢 Fechado (§4.10 B15) |
```

---

### 3.3 Requisitos de Atualização em `HUB_PROTOCOL_IMPROVEMENTS.md`

Ao inspecionar `HUB_PROTOCOL_IMPROVEMENTS.md`, identificam-se as seguintes seções afetadas:
1. **Seção "Feitos que não estavam na lista (2026-09-12/13)":**
   - Atualizar o item sobre janela de presença: registrar que a janela proporcional ao período ecoado já foi aplicada tanto para a Distância (D03) quanto para a Biomassa (B01: `biomassPresenceWindowMs(probe_ms)`).
2. **Seção "Pontos frágeis observados (revistos)":**
   - Atualizar a entrada sobre rotinas bloqueantes da biomassa (B02) e janelas fixas (B01), marcando a resolução da janela e a mitigação por serialização no app.
3. **Seção "Sequência recomendada (atualizada)":**
   - Atualizar o item 2: marcar B01 e B03 como concluídos no Hub e no App.

---

## 4. Comparativo com Planos Existentes e Especificação do Plano de Biomassa

### 4.1 Estrutura Comparada de `IMPLEMENTATION_PLAN_FLUXOMETRO.md` e `IMPLEMENTATION_PLAN_BOMBA.md`

Os dois documentos prévios adotaram uma estrutura de alta densidade técnica, estruturada em seções padronizadas:

```
IMPLEMENTATION_PLAN_<DISPOSITIVO>.md
├── 1. Sumário Executivo & Diagnóstico Geral
├── 2. Arquitetura do Sistema e Cadeia de Comunicação (Diagrama ASCII e Topologia de 4 Camadas)
├── 3. Análise Detalhada e Plano Técnico por Item (F01–F16 ou 1.10.1–1.10.12)
│      ├── Problema Declarado e Causa Raiz
│      ├── Localização Exata no Código (Arquivo e Linhas)
│      ├── Impacto Sistêmico Cruzado (Firmware ↔ Hub ↔ Apps)
│      ├── Decisão Técnica de Ação (Implementação com Diffs ou Justificativa Técnica Fundamentada)
│      └── Classificação de Risco, Prioridade e Segurança
├── 4. Matriz de Compatibilidade e Diretrizes de Arquitetura Fechadas
├── 5. Roteiro de Execução em Fases (Roadmap por Componente)
└── 6. Critérios de Aceitação e Protocolo de Verificação (Checklist de Bancada Física)
```

### 4.2 Especificação dos Scripts de Verificação (`verify_plan_*.py`)

Os scripts `verify_plan.py` (fluxômetro) e `verify_plan_bomba.py` (bomba) operam como avaliadores automatizados de completude (*Agent-as-Judge verification*), checando:
1. **Existência física e tamanho do arquivo:** Falha se o arquivo não existir ou for menor que 1 kB.
2. **Presença das 6 Seções Macro:** Verificação por expressões regulares ignorando caixa.
3. **Varredura exaustiva de cada ID de inconsistência:**
   - Verifica cabeçalho específico de cada item (`### B01`, `### B02`, ..., `### B13`).
   - Verifica subcritérios no corpo do item:
     - Presença de causa raiz / problema declarado / localização exata.
     - Presença de plano de modificação / decisão técnica / justificativa técnica.
     - Presença de prioridade / classificação de segurança.
4. **Tabela de Auditoria e Código de Saída:** Exibição tabular no console e retorno de código 0 (sucesso absoluto) ou 1 (falha de conformidade).

Para o Sensor de Biomassa, o script `verify_plan_biomassa.py` deverá auditar obrigatoriamente os 13 itens principais (**B01 a B13**) e o checklist de bancada (§4.11).

---

## 5. Matriz de Alterações de Código Necessárias por Componente

Para subsidiar os executores subsequentes, a tabela abaixo mapeia as alterações necessárias em cada repositório/componente:

### 5.1 Componente: Firmware do Nó (`External-Devices/sensor-biomassa/firmware/biomass-sensor/`)
- `src/protocol/CommandCodec.h`:
  - **B04:** Em `setManualGear()`, forçar `pwmSetDutyPercent(0.0f)` se `!g_manualLedOn`, e reprogramar `g_nextReadTime` se em `MEASURING`.
  - **B05:** Chamar `saveConfig()` após atribuição de `low`, `high`, `opt` e `refresh_ms`/`probe_ms`/`probe_period`.
- `src/api/LocalHttpApi.h`:
  - **B07:** Substituir `<p>Running: <b>Biomass Sensor Firmware v5.3</b></p>` por `String(FW_VERSION)` dinâmico.

### 5.2 Componente: Gateway Central (`ESP32S3-HUB/ESP32S3-HUB/`)
- `src/core/AppContext.h`:
  - **B01:** Implementar função inline:
    ```cpp
    inline unsigned long biomassPresenceWindowMs(uint32_t probePeriodMs) {
      unsigned long dyn = (unsigned long)(probePeriodMs * 2.5f);
      return dyn > 10000UL ? dyn : 10000UL;
    }
    ```
- `src/sensor/Telemetry.h`:
  - **B01:** Atualizar `biomassOnline` e `validBiomass` para utilizar `biomassPresenceWindowMs(snapBiomassProbePeriodMs)`.
- `src/protocol/Commands.h`:
  - **B03:** Adicionar suporte e tradução de `biomassAutoRange`:
    ```cpp
    if (json.indexOf("\"biomassAutoRange\"") != -1) {
      int autoVal = getValueFromJson(json, "biomassAutoRange").toInt();
      biomassCommand = "\"command\":\"" + String(autoVal != 0 ? "auto" : "manual") + "\"";
      biomassCmdFound = true;
    }
    ```
  - **B12:** Remover encaminhamento órfão de `test_period`.

### 5.3 Componente: Aplicativos (Windows OpenTECHub)
- `Windows_app/src/OpenTECHub.Protocol/CommandKeys.cs`:
  - **B03:** Adicionar `public const string BiomassAutoRange = "biomassAutoRange";`.
- `Windows_app/src/OpenTECHub.Protocol/CommandBuilders.cs`:
  - **B03:** Adicionar builder `BiomassAutoRange(bool autoRange)`.
- `Windows_app/src/OpenTECHub.Protocol/SensorReadings.cs`:
  - **B13:** Adicionar sentinelas de erro de absorbância:
    ```csharp
    public const double BiomassBlankInvalid = -99.0;
    public const double BiomassZeroLight = 9.9;
    ```
- `Windows_app/src/OpenTECHub/ViewModels/BiomassControlViewModel.cs`:
  - **B03:** Expor propriedade e comando de alternância de auto-range.
  - **B13:** Formatar texto de absorbância com tratamento discriminado para sentinelas de erro.
- `Windows_app/src/OpenTECHub/Services/Alarms/AlarmService.cs`:
  - **B06:** Implementar alerta de aquisição estagnada/reiniciada em IDLE.
- `Windows_app/src/OpenTECHub/Services/Recipes/RecipeEngine.ExternalDevices.cs` & `RecipeEngine.Devices.cs`:
  - **B09:** Atualizar texto de log sobre tempo de varredura do branco.
  - **B13:** Exigir absorbância finita não-sentinela em `AwaitBiomassMeasuringAsync`.

---

## 6. Conclusão e Próximos Passos

1. O plano de implementação `IMPLEMENTATION_PLAN_BIOMASSA.md` e seu verificador `verify_plan_biomassa.py` possuem todos os elementos e referências para elaboração imediata.
2. A separação dos commits solicitada pelo usuário no prompt mestre está perfeitamente alinhada com a arquitetura:
   - Commit 1: **Firmware** (`sensor-biomassa`).
   - Commit 2: **Hub** (`ESP32S3-HUB`).
   - Commit 3: **Apps** (`Windows_app` e testes).
3. A documentação técnica (`COMANDOS_DISPOSITIVOS_EXTERNOS.md` e `HUB_PROTOCOL_IMPROVEMENTS.md`) terá suas diretrizes fechadas formalmente registradas sob a tabela padrão azul, encerrando todas as pendências analíticas de B01 a B13.
