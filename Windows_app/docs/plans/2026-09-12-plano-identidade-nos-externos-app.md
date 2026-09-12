# Plano — Identidade de rede e alcance dos nós externos no aplicativo (consumo do `NodeRegistry` do Hub)

**Data:** 2026-09-12
**Escopo:** fazer o aplicativo Windows (`OpenTECHub`) consumir o que o Hub passou a saber com a
Fase 3 do plano de dispositivos externos (`External-Devices`, commits `044293c`…`efc71d9`): quem
está na rede do Hub, em qual IP, com qual MAC e qual firmware, e há quanto tempo. Hoje o app
mostra *se* cada nó está presente (`*Online`), *se* o Hub roteia para ele (`*CommEnabled`) e *se*
há comando pendente; **não mostra quem é o nó nem como alcançá-lo**. Para abrir o `/diag` de um
nó, gravar um firmware por OTA ou saber se a placa na bancada é a mesma de ontem, o operador
ainda precisa adivinhar o IP.
**Estado:** proposto — nada implementado. Depende de um incremento pequeno no firmware do Hub
(§3), que é pré-requisito do §4.A e independente do resto.
**Pré-leitura:** `docs/plans/PLANO_DISPOSITIVOS_EXTERNOS.md` (§2 contrato-alvo, §3.2
`ExternalDeviceStatus`, §5 interface), `docs/PROTOCOL.md` §2.0.1, `docs/CONVENTIONS.md`,
`ESP32S3-HUB/docs/WIRE_CONTRACT_V9.md`.

---

## 0. Como executar este plano em outra sessão

1. **Ponto de partida.** Ler este arquivo inteiro e os documentos da pré-leitura. Confirmar
   que o app não está rodando (`Get-Process OpenTECHub`) antes de qualquer `dotnet build`.
2. **Baseline.** `dotnet test Windows_app/OpenTECHub.slnx` (1363 aprovados em 12/09/2026; os
   flakes conhecidos de temporização estão listados em `docs/ROADMAP.md`). Hub:
   `python -m unittest discover tests/contracts` em `ESP32S3-HUB/` e compilação com
   `External-Devices/tools/.bin/arduino-cli.exe compile --config-file
   External-Devices/tools/arduino-cli.local.yaml --fqbn esp32:esp32:esp32s3 ESP32S3-HUB/ESP32S3-HUB`.
3. **Ordem de execução** (uma etapa = um commit, com testes): §3 (hub) → §4.A (fio) →
   §4.B (`ExternalDeviceStatus` + rastreador de identidade) → §4.C (simulador) → §4.D
   (Controle) → §4.E (Configurações › Conexão + `/nodes`) → §4.F (Eventos) → §4.G
   (proveniência) → §4.H (documentação e manual). §4.I é opcional. A §3 vai primeiro porque o
   parser precisa de quadros reais para os golden strings; enquanto o hub não é gravado, o
   simulador (§4.C) sustenta todo o resto.
4. **Comandos.** Build: `dotnet build OpenTECHub.slnx -c Debug`. Testes:
   `dotnet test tests/OpenTECHub.Tests --no-build`. Simulador:
   `dotnet run --project src/OpenTECHub.Simulator` (HTTP em localhost) e conectar por Wi-Fi.
5. **Critério de pronto.** Cada seção lista seus testes. Ao fim: `CHANGELOG.md` (nova
   versão), `ROADMAP.md`, `PHASE_LOG.md` (P3-09), `DECISIONS.md` (D-051), `PROTOCOL.md`
   §2.0.2, `ARCHITECTURE.md`, `DocumentationCatalog` (tópico Configurações › Conexão, o único
   ainda em falta no manual — D-047). Commits em branch `codex/phase3-nodes-identity` ou
   direto na `main` conforme o usuário pedir; **verificar a branch antes de cada commit**
   (o `.git` está no OneDrive).
6. **Regras que não se negociam.** Fio do ESP32 congelado (golden strings intactos, chaves
   novas são *aditivas*); `InvariantCulture` no fio; `OpenTECHub.Protocol` nunca referencia
   WPF; código em inglês, interface em pt-BR; ausência de chave nunca vira falha na tela.

---

## 1. O que o Hub entrega hoje ao app — resposta direta

O Hub `10.0.1-dev` (`ESP32S3-HUB/ESP32S3-HUB/src/core/AppContext.h:368-419`) mantém
`g_deviceRegistry[DEV_COUNT]` com `ip`, `mac[18]`, `version[16]`, `lastHelloMs`, `lastDataMs`
e `registered` para os cinco nós. Do que ele sabe, o app recebe **só o IP**:

| Informação | Onde o Hub a publica | USB | Wi-Fi | O app consome? |
|---|---|---|---|---|
| IP do nó | `DistanceIP`, `AgitatorIP`, `PumpIP`, `FlowmeterIP`, `BiomassIP` no quadro agregado (`Telemetry.h:287-291`) — o mesmo `String` vai por `Serial.println` e por `GET /readData` | sim | sim | **não** — `TelemetryParser` ignora chaves não mapeadas; `TelemetryKeys` não as declara |
| MAC, versão de firmware | só em `GET /nodes` (`HttpServer.h:482-513`) | **não** | sim | **não** — o app nunca chama `/nodes`; o simulador não o expõe |
| Frescor (`age_ms`), `online` do registro | só em `GET /nodes` | **não** | sim | não |
| `registered`, `lastHelloMs`, `lastDataMs` | em lugar nenhum | — | — | — |
| Presença / roteamento / pendência | `*Online`, `*CommEnabled`, `*CommandPending` | sim | sim | sim — `ExternalDeviceStatus` |

Lacunas que este plano fecha, do lado do Hub (§3): MAC e versão não chegam a quem está em USB
(que é como a bancada costuma operar); `/nodes` não diz se o nó chegou a se registrar nem
oferece `hub_time_ms` para o app medir o frescor; não há teste de contrato nem documentação do
Hub para as cinco chaves `*IP` nem para `/nodeHello`/`/nodes` (`tests/contracts/test_json_keys.py`
e `docs/WIRE_CONTRACT_V9.md` não os mencionam); e `lastSensorJson.reserve(2048)`
(`Runtime.h:260`) é menor que o `jsonResponse.reserve(2560)` de `Telemetry.h:138`, de modo que
a cópia `lastSensorJson = jsonResponse` realoca quando o quadro passa de 2 KB — que é para
onde ele caminha.

**Alcance a partir do PC.** `/nodes`, o `/diag` de cada nó e o OTA (`POST /update`) só são
alcançáveis quando o PC está associado ao SoftAP do Hub (`192.168.4.x`). Em USB o app conhece
IP/MAC/firmware pelo quadro agregado mas **não alcança** os nós. O plano trata isso
explicitamente: ações de rede só aparecem quando `Medium == WiFi`; um proxy no Hub
(`GET /nodeDiag?dev=`) fica como item diferido (§3.H5), porque exige um cliente HTTP no Hub
dentro do `AsyncWebServer`, que é trabalho de outra ordem.

---

## 2. Objetivo, o que o app ganha e princípios

**G1 — Ver.** Em cada linha de dispositivo externo da página Controle e num painel único
em Configurações › Conexão: IP, MAC, versão de firmware e há quanto tempo o Hub ouviu o nó.
**G2 — Agir.** Em Wi-Fi: abrir o diagnóstico do nó (`http://<ip>/diag`) no navegador, copiar
o IP, e um atalho para a página de OTA do nó. Em USB os mesmos botões ficam desabilitados
com o motivo no tooltip, nunca escondidos.
**G3 — Registrar.** Eventos na página Eventos quando um nó se registra, muda de IP, muda de
firmware ou aparece com outro MAC (placa trocada); versões dos nós nos cabeçalhos de
proveniência dos ensaios e sessões, ao lado de `HubFirmwareVersion`.
**G4 — Avisar.** Firmware de nó fora do conjunto validado com esta versão do app →
aviso discreto (texto/tooltip), **não** alarme: um nó mais novo pode estar perfeitamente bem.

Princípios que valem para cada seção:

- **Aditivo e tolerante.** Chaves novas no quadro são opcionais. Um Hub anterior a elas deixa
  a identidade em *desconhecida*, e a tela diz isso — nunca "offline", nunca vazio-sem-explicação.
- **Sticky e anulável**, como `HubFirmwareVersion`: identidade publicada uma vez não é esquecida
  dentro do enlace; `MarkHubUnavailable` limpa a *visão* (VM), não o parser.
- **Um lugar para a semântica**: `ExternalDeviceStatus` (D-021/D-022, §3.2 do plano de
  dispositivos externos) ganha a identidade de rede; as cinco VMs só a alimentam.
- **O fio manda.** O IP que vale é o que o Hub extraiu da conexão TCP; o app não infere nada.

---

## 3. Alterações no Hub (firmware `10.1.0-dev`, `HUB_PROTOCOL_VERSION` permanece 10)

Chaves aditivas não mudam o contrato — mesma política do `ServoControlCapable`/`ServoMotor*`.

### H1 — Identidade dos nós no quadro agregado (`src/sensor/Telemetry.h`)

Junto das cinco chaves `*IP` já emitidas, publicar por nó, **apenas quando
`g_deviceRegistry[i].registered`** (padrão condicional das dez chaves `Servo*` de amostra):

| Chave | Tipo | Origem |
|---|---|---|
| `DistanceNodeVer`, `AgitatorNodeVer`, `PumpNodeVer`, `FlowmeterNodeVer`, `BiomassNodeVer` | string ≤ 15 | `version[]` |
| `DistanceNodeMac`, `AgitatorNodeMac`, `PumpNodeMac`, `FlowmeterNodeMac`, `BiomassNodeMac` | string 17 | `mac[]` |

Snapshot sob `stateMutex` junto com os IPs (copiar `version`/`mac`/`registered` para locais,
como já se faz com `snapDistIp`…), montar o JSON fora do mutex. Custo: ~50 bytes por nó
registrado, ~250 bytes com os cinco — ao 115200 baud isso são ~22 ms a mais por quadro na
USB; aceitável a 1 s de período, e é o motivo de emitir só para nós registrados. As chaves
`*IP` continuam incondicionais (o app usa `0.0.0.0` como "nunca visto").

**Não** emitir `age_ms` no quadro: presença já é `*Online`, e frescor fino fica em `/nodes`.

### H2 — `GET /nodes` completo (`src/network/HttpServer.h`)

Acrescentar por entrada `registered` (bool), `last_hello_ms`, `last_data_ms` (millis do Hub,
0 = nunca) e, no objeto raiz, `hub_time_ms`, para que o cliente calcule idade sem depender de
`age_ms` (que hoje devolve `999999` como sentinela). Buffer `char resp[768]` → `1024`
(pior caso por entrada com os campos novos ≈ 170 bytes × 5 + raiz). Opcional: `?dev=<nome>`
devolve só uma entrada, para o app consultar um nó sem baixar os cinco.

### H3 — Reservas coerentes

`jsonResponse.reserve(2560)` → `3072` e `lastSensorJson.reserve(2048)` → `3072`
(`Telemetry.h:138`, `Runtime.h:260`), com o comentário de pior caso atualizado
(v10.1: identidade dos nós, ~+250 bytes). O `cachedJson` de `/readData` copia de
`lastSensorJson`, então as três strings devem ter a mesma reserva.

### H4 — Contrato e documentação do Hub

- `tests/contracts/test_json_keys.py`: as cinco `*IP` como chaves obrigatórias; as dez
  `*NodeVer`/`*NodeMac` como condicionais (presentes ⇔ registrado). `test_http_frames.py`:
  rota `/nodes` e `/nodeHello` (400 sem `dev`, 400 com `dev` desconhecido, 200 com o JSON de
  confirmação).
- `docs/WIRE_CONTRACT_V9.md` §"Campos agregados": tabela das quinze chaves; nova seção
  "Registro de nós: `/nodeHello` e `/nodes`" com o contrato do plano da Fase 3.
- `Config.h`: `HUB_FIRMWARE_VERSION "10.1.0-dev"`.
- `External-Devices/tools/Test-HubDeviceContracts.ps1` já verifica as rotas; acrescentar a
  verificação das chaves `*NodeVer`/`*NodeMac` na `Telemetry.h`.

### H5 — Diferido: proxy de diagnóstico para USB

`GET /nodeDiag?dev=<nome>` no Hub que faz `GET http://<ip>/diag` no nó e repassa o corpo.
Daria ao app em USB o RSSI, heap e `hub_fail_streak` de cada nó. Fica fora deste plano:
cliente HTTP síncrono dentro de um handler do `AsyncWebServer` bloqueia a tarefa de rede;
precisaria de uma tarefa própria com fila e cache — dimensionar depois de medir a necessidade
na bancada.

**Commit sugerido:** `feat(hub): publicar versao e MAC dos nos no quadro agregado e completar /nodes (10.1.0-dev)`.

---

## 4. Alterações no aplicativo

### A. Camada de fio — `OpenTECHub.Protocol`

1. **`TelemetryKeys`** (`CommandKeys.cs`): quinze constantes novas em bloco próprio
   "External-node identity (Hub 10.1)", com o comentário de que são aditivas, sticky e
   anuláveis, e que `0.0.0.0` significa "nunca visto".
2. **`ExternalNodeIdentity`** (novo `record` em `SensorReadings.cs`):
   `string? Ip` (null quando ausente ou `0.0.0.0`), `string? Mac`, `string? FirmwareVersion`.
   `SensorReadings`/`SensorSnapshot` ganham `DistanceNode`, `AgitatorNode`, `PumpNode`,
   `FlowmeterNode`, `BiomassNode` (`ExternalNodeIdentity`, default `Empty`), copiados em
   `Snapshot()`.
3. **`TelemetryParser.ParseNodeIdentity(root)`**, chamado onde `ParseHubIdentity` é chamado:
   por nó, lê as três chaves com `TryGetPropertyCaseInsensitive`; string vazia ou `0.0.0.0`
   → null; ausência **mantém** o valor anterior (sticky). Nenhum efeito sobre `*Online`.
4. **`HubNodeDirectoryClient`** (novo, `OpenTECHub.Protocol/HubNodeDirectoryClient.cs`):
   `Task<HubNodeDirectory?> FetchAsync(string ip, CancellationToken)` fazendo
   `GET http://<ip>/nodes` com `HttpClient` próprio e timeout de 1 s; DTOs
   `HubNodeDirectory(long HubTimeMs, IReadOnlyList<HubNodeEntry> Nodes)` e
   `HubNodeEntry(string Dev, string? Ip, string? Mac, string? Version, bool Online,
   bool Registered, long? LastHelloMs, long? LastDataMs)`. **Fora do `ITransport`** de
   propósito: o contrato do transporte tem um único dono (`ConnectionManager`) e `/nodes` é
   diagnóstico, não telemetria. Tolerante a Hubs `10.0.x` (sem `registered`/`hub_time_ms`).

**Testes** (`TelemetryParserTests` / novo `ExternalNodeIdentityTests`): chaves parseadas;
ausentes → `Empty` no primeiro quadro e valor anterior nos seguintes; `0.0.0.0` → `Ip` null;
case-insensitive; quadro real de Hub 10.0.1 (`*IP` sem `*NodeVer`) parseia sem exceção;
**golden strings inalterados**. `HubNodeDirectoryClient`: JSON da §H2, JSON de 10.0.x,
corpo inválido → null sem lançar.

### B. Semântica — `ExternalDeviceStatus` e rastreador de identidade

1. **`ExternalDeviceStatus`** ganha `[ObservableProperty] ExternalNodeIdentity Node`
   (default `Empty`) e os derivados: `HasNodeIdentity`, `NodeIpText` (`—` quando null),
   `NodeFirmwareText`, `NodeMacText`, `NetworkSummaryText`
   (`"192.168.4.3 · fw 3.8"` / `"Identidade de rede desconhecida (Hub anterior à 10.1)"`),
   `NodeDiagnosticsUri` (`http://<ip>/diag`, null sem IP). `Update(...)` recebe a identidade;
   `MarkHubUnavailable` volta a `Empty`. `FirmwareAdvisoryText` (G4) vem do item 3.
2. **`NodeIdentityTracker`** (novo, `Services/Communication/`, puro): recebe `SensorSnapshot`
   e emite `NodeIdentityChange(DeviceName, Kind, Before, After)` com `Kind ∈ {Registered,
   IpChanged, FirmwareChanged, MacChanged}`; primeiro aparecimento é `Registered`; nada é
   emitido quando a identidade some (isso já é `IsOffline`). Testável sem WPF.
3. **`NodeFirmwareCatalog`** (novo, `Services/Communication/`): por dispositivo, o **conjunto**
   de versões validadas com este app (o que os cinco firmwares enviam hoje em `/nodeHello`: `distance: v10`;
   `agitator: v10`; `pump: 3.8`; `flowmeter: v10`; `biomass: v10`), lido de uma constante versionada —
   versões não são ordenáveis entre si (`v10`, `3.8`, `rev-h`), por isso é igualdade a um
   conjunto, não comparação. Fora do conjunto → `FirmwareAdvisoryText =
   "Firmware {ver} não validado com esta versão do app"`; dentro → null. Sem alarme.
4. As cinco VMs (`FlowControlViewModel`, `BiomassControlViewModel`, `PumpControlViewModel`,
   `FlaskAgitatorViewModel`, o `Status` de distância em `FoamControlViewModel`) passam a
   chamar `Status.Update(..., snapshot.<X>Node)`.

**Testes** (`ExternalDeviceTests`, novo `NodeIdentityTrackerTests`): textos com e sem
identidade; `MarkHubUnavailable` limpa; tracker: registro → `Registered`; `.3`→`.5` →
`IpChanged`; mesma identidade em 100 quadros → nada; catálogo: validada/não validada/nula.

### C. Simulador — `OpenTECHub.Simulator`

1. `DeviceModel`: por nó externo, `Ip`/`Mac`/`Version` determinísticos (`192.168.4.2`…`.6`,
   MACs fixos, versões do catálogo) e `Registered = ExternalNodesOnline && <habilitado>`.
2. `WireCodec`: emite as cinco `*IP` (sempre; `0.0.0.0` quando não registrado) e as dez
   `*NodeVer`/`*NodeMac` só quando registrado — espelho fiel da §H1.
3. `HttpEndpoint`: rota `/nodes` (§H2), e o comentário de "only three endpoints" passa a listar
   quatro. Fault injection nova: `node-ip-change` (renumera os IPs a cada N s) para exercitar
   o §F ao vivo; `hub-legacy` (omite as quinze chaves) para ver o caminho "Hub anterior à 10.1".
4. `HeadlessRunner`/testes do simulador: quadro contém as chaves; `/nodes` responde.

### D. Página Controle — gavetas dos dispositivos externos (`Views/ControlView.xaml`)

Nas cinco gavetas (Vazão de Ar, Distância, Bomba Externa, Absorbância, Frasco Agitador), um
bloco **"Rede"** abaixo dos ecos `Telemetria: {0}` já existentes:

- Linha `Rede: {NetworkSummaryText}` com tooltip `MAC {NodeMacText}`.
- `FirmwareAdvisoryText` como nota discreta (`TextMuted`), só quando não nulo.
- Dois botões pequenos: **Abrir diagnóstico** (abre `NodeDiagnosticsUri` no navegador via
  `Process.Start` com `UseShellExecute`, encapsulado em `IFileInteractionService.OpenUri` para
  ser testável) e **Copiar IP**. Habilitados por `MultiDataTrigger` sobre
  `Status.HasNodeIdentity` × `ConnectionViewModel.Medium == WiFi`; em USB, tooltip
  "Disponível apenas com o PC conectado à rede Wi-Fi do Hub".

Sem chip novo: uma linha mostra um chip (regra de `ShowRoutingChipOnly`), e identidade não é
condição de alarme. `CompactLayoutTests` cobre o bloco em 936 × 534 DIP.

### E. Configurações › Conexão — painel "Nós na rede do Hub"

Abaixo de "Período de telemetria", um `ListView` com as colunas **Dispositivo · IP · MAC ·
Firmware · Estado · Visto há**, alimentado por um `HubNodesViewModel` novo:

- **Fonte primária:** o quadro agregado (funciona em USB e Wi-Fi; "Visto há" vem de
  `*LastSeenAt` do parser, já existente por nó).
- **Enriquecimento em Wi-Fi:** `HubNodeDirectoryClient.FetchAsync` a cada 10 s enquanto
  `Medium == WiFi` e a seção Conexão estiver selecionada (`SelectedSection.Id`, que a
  view já usa para mostrar cada seção), trazendo `registered`, `last_hello_ms`/`last_data_ms` e o `age` calculado
  contra `hub_time_ms`. Botão **Atualizar** manual; texto de rodapé "Consulta a `/nodes`
  disponível apenas por Wi-Fi; por USB a tabela usa o quadro de telemetria."
- Hub anterior à 10.1: colunas MAC/Firmware mostram `—` e um aviso único no topo
  ("O Hub {HubFirmwareVersion} não publica a identidade dos nós; atualize para 10.1").
- Popover de conexão (`ConnectionPopoverContractTests`): acrescentar a contagem
  "{n}/5 nós registrados" ao lado dos contadores de quadros/comandos — só texto.

**Testes:** `HubNodesViewModelTests` (linhas a partir do snapshot; enriquecimento só em
Wi-Fi; aviso de Hub antigo; `Atualizar` desabilitado em USB); `ConnectionPopoverContractTests`
(o contador existe e some sem Hub); `CompactLayoutTests` para a tabela.

### F. Página Eventos — o que o `NodeIdentityTracker` viu

`EventJournal` assina o tracker (ou o `DeviceService.TelemetryReceived` e chama o tracker
internamente) e escreve `AuditSource.Connection`:

| Mudança | Severidade | Mensagem |
|---|---|---|
| `Registered` | Info | `Nó {nome} registrado em {ip} (firmware {ver})` — detalhe: MAC |
| `IpChanged` | Info | `IP do nó {nome} mudou de {a} para {b}` |
| `FirmwareChanged` | Warning | `Firmware do nó {nome} mudou de {a} para {b}` |
| `MacChanged` | Warning | `Nó {nome} responde com outro MAC ({a} → {b}): placa trocada?` |

É a observabilidade que motivou as Fases 1–3 (reassociações do Link Watchdog, DHCP
renumerando após reinício): passa a ficar em `eventos.jsonl`, com hora, sem ninguém
precisar estar olhando o monitor serial. Teste: `EventJournalTests` — sequência de snapshots
produz exatamente os quatro eventos, sem duplicatas em quadros idênticos.

### G. Proveniência — versões dos nós ao lado de `HubFirmwareVersion`

- `ServoSessionLogFormat.BuildPreamble` e o cabeçalho da sessão: linha
  `# nodes: distance=v10@192.168.4.2 pump=3.8@192.168.4.3 …` (só nós com identidade).
- `PowerTestDocument` e o documento de determinação de kLa: campo
  `ExternalNodeFirmware` (`Dictionary<string,string>`), preenchido ao abrir a corrida como
  `HubFirmwareVersion` já é (`PowerTestRunner.cs:281-286`). Leitor tolera ausência.
- Motivo: o fluxômetro `v05` e o servo são atores dos ensaios do manuscrito; o recibo deve
  dizer com que firmware o dado foi produzido.

**Testes:** `SessionLoggerTests`/`RawDataIntegrityTests` — preâmbulo com e sem identidade;
`PowerTestStoreTests` — round-trip do campo, documento antigo sem o campo carrega.

### H. Documentação e manual

- `PROTOCOL.md` §2.0.2 "External-node identity `[hub-patch 10.1]`": tabela das quinze
  chaves, regras de parsing (sticky, anuláveis, `0.0.0.0`), e o contrato de `/nodes`
  reproduzido do `WIRE_CONTRACT_V9.md`.
- `ARCHITECTURE.md`: `HubNodeDirectoryClient`, `NodeIdentityTracker`, `NodeFirmwareCatalog`.
- `DECISIONS.md` **D-051** — "Identidade de rede dos nós: aditiva, sticky, sem alarme; ações
  de rede só por Wi-Fi; `/nodes` fora do `ITransport`".
- `CHANGELOG.md`, `ROADMAP.md` (item novo em Phase 3 ou Phase 5 › Field readiness, com o
  recibo de bancada pendente), `PHASE_LOG.md` **P3-09**, `CURRENT_STATUS.md`.
- `DocumentationCatalog`: tópico **Configurações › Conexão** (o que faltava na D-047),
  escrito layout-primeiro, controle a controle, incluindo o painel "Nós na rede do Hub" e os
  botões das gavetas; `DocumentationEvidenceTests` gera a evidência.
- `MANUAL_DO_OPERADOR.md`: seção "Localizar um nó na rede do Hub" (IP → `/diag` → OTA via
  `Publish-OtaFirmware.ps1`, que já descobre o IP sozinho).

### I. Opcional — OTA a partir do app (não incluído; registrar como diferido)

Botão "Atualizar firmware…" na gaveta que faz `POST http://<ip>/update` com um `.bin`
escolhido. Tudo o que precisa já existe (`FileInteractionService`, IP conhecido), mas a
ferramenta `Publish-OtaFirmware.ps1` cobre o caso hoje com verificação de baseline e
headroom, que o app não tem. Revisitar quando o operador pedir; anotar em "Explicitly
deferred" do `ROADMAP.md`.

---

## 5. Ordem, commits e esforço

| # | Etapa | Commit | Esforço | Depende de |
|---|---|---|---|---|
| 1 | §3 H1–H4 | `feat(hub): publicar versao e MAC dos nos no quadro agregado e completar /nodes (10.1.0-dev)` | P | — |
| 2 | §4.A | `feat(protocol): identidade de rede dos nos externos (chaves *IP/*NodeVer/*NodeMac) e cliente /nodes` | M | 1 (golden strings) |
| 3 | §4.B | `feat(devices): identidade de rede em ExternalDeviceStatus, NodeIdentityTracker e catalogo de firmware` | M | 2 |
| 4 | §4.C | `feat(simulator): identidade dos nos, /nodes e faults node-ip-change/hub-legacy` | P | 2 |
| 5 | §4.D | `feat(controle): bloco Rede nas gavetas dos dispositivos externos` | M | 3 |
| 6 | §4.E | `feat(configuracoes): painel Nos na rede do Hub e contador no popover de conexao` | M | 3, 4 |
| 7 | §4.F | `feat(eventos): registrar registro, mudanca de IP/firmware/MAC dos nos` | P | 3 |
| 8 | §4.G | `feat(proveniencia): versoes dos nos nos cabecalhos de sessao e ensaios` | P | 3 |
| 9 | §4.H | `docs: identidade dos nos (PROTOCOL 2.0.2, D-051, P3-09, manual Conexao)` | M | 2–8 |

P ≈ meio período; M ≈ um período. Total ≈ 5–6 períodos, sem bancada.

---

## 6. Verificação

### 6.1 Automatizada

```powershell
# Hub
python -m unittest discover ESP32S3-HUB/tests/contracts
.\External-Devices\tools\.bin\arduino-cli.exe compile --config-file .\External-Devices\tools\arduino-cli.local.yaml --fqbn esp32:esp32:esp32s3 .\ESP32S3-HUB\ESP32S3-HUB
powershell.exe -ExecutionPolicy Bypass -File .\External-Devices\tools\Test-HubDeviceContracts.ps1

# App
dotnet test Windows_app/OpenTECHub.slnx
```

Novos testes esperados: ~35 (parser 8, cliente `/nodes` 4, `ExternalDeviceStatus` 5,
tracker 5, catálogo 3, `HubNodesViewModel` 5, jornal 2, proveniência 3, layout/contratos 2).

### 6.2 Simulador (sem bancada)

1. `dotnet run --project src/OpenTECHub.Simulator` → conectar por Wi-Fi → Controle: as cinco
   gavetas mostram `Rede: 192.168.4.x · fw …`; Configurações › Conexão lista os cinco com
   MAC/firmware e "Visto há" andando.
2. Fault `node-ip-change` → Eventos mostra `IP do nó … mudou`, uma vez por mudança.
3. Fault `hub-legacy` → gavetas dizem "Identidade de rede desconhecida"; painel mostra o
   aviso de Hub antigo; nenhuma exceção, nenhum chip vermelho.
4. Conectar por serial virtual → botões de rede desabilitados com o tooltip; tabela preenchida
   a partir do quadro.

### 6.3 Bancada (recibo pendente, junto com o do plano de 11/09)

1. Gravar o Hub `10.1.0-dev`; ligar os cinco nós; `curl http://192.168.4.1/nodes` mostra os
   cinco com `registered:true`.
2. App em USB: gavetas com IP/firmware reais; painel com MAC. App em Wi-Fi: **Abrir
   diagnóstico** abre o `/diag` do nó certo; `Publish-OtaFirmware.ps1 -Device pump` descobre
   o mesmo IP que a tela mostra.
3. Reiniciar um nó: Eventos registra `registrado` de novo (e `IP mudou` se o DHCP renumerou).
4. Medir o tamanho do quadro com os cinco registrados (`/readData` → `Content-Length`) e anotar
   em `docs/evidence/`; deve ficar abaixo de 2,4 KB.

---

## 7. Riscos e decisões em aberto

| Risco / decisão | Tratamento |
|---|---|
| Quadro agregado crescendo (~+250 bytes) sobre a USB a 115200 baud | Emissão condicional a `registered`; reserva 3072; medir em bancada (§6.3.4). Se preciso, próximo passo é emitir `*NodeMac` só a cada N quadros — o parser já é sticky. |
| Segundo cliente HTTP no SoftAP (`/nodes` a cada 10 s) | Só em Wi-Fi, só com a seção visível, timeout 1 s, falha silenciosa → tabela cai para o quadro. Nunca dentro do `_ioGate` do transporte. |
| `Process.Start` de URL a partir da UI | Encapsulado em `IFileInteractionService.OpenUri`; testes usam o fake. |
| Versão de firmware "não validada" assustar o operador | Texto `TextMuted`, sem chip, sem alarme, com o catálogo documentado no manual. |
| `agitator` registra via `/agitatorHello` com `rev-h` fixo no Hub (`HttpServer.h:442`) | O nó já envia `/nodeHello` com `ver` próprio (`d7e760e`); manter o legado, mas o `recordDeviceActivity` do `/agitatorHello` **não** deve sobrescrever uma versão já informada pelo `/nodeHello` — passar `ver` vazio ali. Incluir na §3 H1. |
| Nome do dispositivo no fio (`distance`, `pump`…) ≠ nome na tela (`DeviceNames`) | Mapeamento único em `NodeIdentityTracker`/`HubNodesViewModel` via `DeviceNames`; teste de nomenclatura (`DeviceNamingTests`) cobre os cinco. |
| Hub em USB e PC também no Wi-Fi do Hub | `Medium` reflete o enlace de telemetria; se for USB, as ações ficam desabilitadas mesmo que o Wi-Fi alcançasse — conservador e previsível. Revisitar com o §H5 se incomodar. |
