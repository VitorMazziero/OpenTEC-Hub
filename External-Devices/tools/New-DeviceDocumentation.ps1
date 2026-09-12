[CmdletBinding()]
param()

$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$utf8 = [System.Text.UTF8Encoding]::new($false)

$devices = @(
    [ordered]@{ Slug='bomba-peristaltica'; Name='Bomba peristáltica'; Version='v4'; Board='ESP32'; Firmware='peristaltic-pump'; App='apps/flutter'; Protocol='Pull `GET /pumpCommand`; push `GET /pumpData`; entrega confiável por `cmd_id` e `ack_cmd_id`.'; Hardware='Controle PWM do motor, leitura de velocidade/vazão e estado persistente. Consulte os arquivos CAD em `hardware/cad/source/watson-marlow-integration`.'; Modules='`hardware/PwmRuntime.h`, `control/OperationController.h`, `control/SensorAndConversion.h`, `storage/RuntimeStateStore.h`, `storage/ConfigStore.h`, `protocol/TelemetryCodec.h`, `network/HubClient.h`'; Caveat='A modularização mantém uma única unidade de tradução para preservar globais e inicialização. O perfil de dosagem e a parada física precisam de bancada.' },
    [ordered]@{ Slug='fluxometro'; Name='Fluxômetro'; Version='v10'; Board='ESP32'; Firmware='flowmeter'; App='apps/flutter'; Protocol='Pull `GET /flowCommand`; push `GET /flowData`; `cmd_id`/`ack_cmd_id`, `boot_id` e comandos diretos possuem recibos separados.'; Hardware='Entradas analógicas de vazão e saídas das válvulas. Manuais e referências ficam em `hardware/references`; CAD em `hardware/cad/source/flowmeter-50lmin`.'; Modules='`core/Lifecycle.h`, `tasks/TaskRuntime.h`, `protocol/CommandCodec.h`, `hardware/FlowIo.h`, `storage/CalibrationStore.h`, `api/WebSocketApi.h`, `api/OtaService.h`'; Caveat='O FQBN preserva opções da compilação importada. Calibração, válvulas, OTA e concorrência entre comandos Hub/diretos exigem bancada.' },
    [ordered]@{ Slug='frasco-agitador'; Name='Frasco agitador'; Version='rev H'; Board='ESP32-S3'; Firmware='flask-agitator'; App='apps/flutter'; Protocol='Registro `GET /agitatorHello`, pull `GET /agitatorCommand` e push `GET /agitatorData`; comandos usam ArduinoJson e `cmd_id`/`ack_cmd_id`.'; Hardware='Driver do motor, direção, PWM e potenciômetro local. CAD em `hardware/cad/source/agitator`.'; Modules='`motor/MotorDriver`, `input/Potentiometer`, `protocol/CommandCodec`, `network/NetworkManager`, `network/HubClient`, `api/LocalHttpApi`, `core/FirmwareApp`'; Caveat='O comando do Hub e o potenciômetro disputam autoridade conforme `ActivePot`. Parada segura e retomada local precisam de teste físico.' },
    [ordered]@{ Slug='sensor-biomassa'; Name='Sensor de biomassa'; Version='v5.3'; Board='ESP32-S3'; Firmware='biomass-sensor'; App='apps/desktop-python'; Protocol='Pull `GET /biomassCommand`; push `GET /biomassData`; heartbeat `idle=1`; entrega confiável por `cmd_id`/`ack_cmd_id`.'; Hardware='Sensor óptico, LED/PWM, faixa automática e armazenamento de calibração. CAD em `hardware/cad/source/biomass-sensor`.'; Modules='`sensor/Veml7700Driver.h`, `measurement/MeasurementPipeline.h`, `measurement/BlankingAndRange.h`, `filtering/SampleFilter.h`, `history/SampleHistory.h`, `storage/Stores.h`, `protocol/CommandCodec.h`, `protocol/TelemetryAndHub.h`, `api/LocalHttpApi.h`'; Caveat='Blanking e aquisição são operações sensíveis e parcialmente bloqueantes. A UI humana está em `web/index.html`; `web_ui.h` é gerado.' },
    [ordered]@{ Slug='sensor-distancia'; Name='Sensor de distância'; Version='v10'; Board='ESP32'; Firmware='distance-sensor'; App='—'; Protocol='Push `GET /distance?distance=<valor>&time=<segundos>`; não recebe comandos do Hub.'; Hardware='Sensor VL53L0X e configuração persistente. CAD em `hardware/cad/source/distance-sensor`.'; Modules='`sensor/DistanceSensor`, `network/NetworkManager`, `protocol/ConfigCodec`, `api/LocalHttpApi`, `core/FirmwareApp`'; Caveat='O sentinela `-1` e os tempos de presença/intertravamento são semântica de segurança e precisam permanecer separados.' }
)

function Write-Markdown([string]$path, [string]$content) {
    $directory = Split-Path -Parent $path
    if (-not (Test-Path -LiteralPath $directory)) { New-Item -ItemType Directory -Path $directory | Out-Null }
    [System.IO.File]::WriteAllText($path, $content.Trim() + "`n", $utf8)
}

foreach ($device in $devices) {
    $deviceRoot = Join-Path $root $device.Slug
    $firmwarePath = "firmware/$($device.Firmware)"
    $appLine = if ($device.App -eq '—') { 'Não há aplicativo dedicado nesta pasta.' } else { "Aplicativo ativo: ``$($device.App)``." }

    Write-Markdown (Join-Path $deviceRoot 'README.md') @"
# $($device.Name)

Versão ativa: **$($device.Version)**. Placa de compilação: **$($device.Board)**.

- Firmware: ``$firmwarePath``
- $appLine
- Hardware: ``hardware/``
- Evidências/testes: ``tests/`` quando aplicável
- Baseline preservado e histórico: ``archive/``

## Começar

    Leia ``docs/CURRENT_STATUS.md`` antes de gravar hardware, ``docs/PROTOCOL.md`` antes de tocar em comunicação e ``docs/VALIDATION.md`` para os gates. Compile todos os dispositivos com ``tools\Compile-ExternalDevices.ps1`` a partir de ``External-Devices``.

O código ativo foi reorganizado sem mudança intencional de comportamento. O monólito original permanece em ``archive/active-baseline`` para comparação e os hashes importados estão em ``archive/IMPORT_MANIFEST.sha256``.
"@

    Write-Markdown (Join-Path $deviceRoot 'CHANGELOG.md') @"
# Changelog — $($device.Name)

## Reorganização de 2026-09-11

- Selecionada e nomeada a versão ativa $($device.Version).
- Separados firmware, aplicativo, hardware, testes/evidências e histórico.
- Preservado o monólito ativo anterior em ``archive/active-baseline``.
- Removidos cabeçalhos extensos e históricos embutidos do código ativo; decisões foram transferidas para Markdown.
- Criada estrutura modular por responsabilidade sem alteração intencional do contrato de fio.
- Compilado no ESP32 core 3.3.11 usando as bibliotecas compartilhadas da máquina de upload.

Validação de hardware permanece pendente.
"@

    Write-Markdown (Join-Path $deviceRoot 'docs\CURRENT_STATUS.md') @"
# Estado atual — $($device.Name)

- Firmware ativo: ``$firmwarePath`` ($($device.Version))
- Compilação: aprovada em 2026-09-11 com ESP32 core 3.3.11
- Contrato com o Hub: preservado estaticamente; consulte ``PROTOCOL.md``
- Baseline: preservado por SHA-256 em ``archive/active-baseline``
- Bancada/hardware: **pendente**

$($device.Caveat)

Este arquivo descreve o que está demonstrado hoje. Build não comprova sensores, atuadores, temporização real, Wi-Fi, persistência ou segurança física.
"@

    Write-Markdown (Join-Path $deviceRoot 'docs\ARCHITECTURE.md') @"
# Arquitetura — $($device.Name)

O sketch principal é uma entrada mínima que delega a ``core/FirmwareApp``. Responsabilidades ativas:

$($device.Modules)

Nos firmwares extraídos para fragmentos privados, esses arquivos são incluídos por ``FirmwareApp.cpp`` para manter ordem de declaração, globais e semântica do monólito. A evolução para compilação independente está registrada em ``../../docs/OPTIMIZATION_OPPORTUNITIES.md``.
"@

    Write-Markdown (Join-Path $deviceRoot 'docs\PROTOCOL.md') @"
# Protocolo — $($device.Name)

## Contrato vigente

$($device.Protocol)

Os nomes, tipos, unidades, sentinelas, rotas e semântica de confirmação são compatibilidade de fio. O código atual de ``ESP32S3-HUB/ESP32S3-HUB`` é a contraparte autoritativa. Alterações sugeridas ficam em ``../../docs/HUB_PROTOCOL_IMPROVEMENTS.md`` e não estão implementadas.

## JSON

Consulte a matriz transversal em ``../../docs/HUB_PROTOCOL_IMPROVEMENTS.md``. Um HTTP 200 confirma recebimento da requisição; ``ack_cmd_id`` confirma a revisão aplicada, não necessariamente a conclusão física de um atuador.
"@

    Write-Markdown (Join-Path $deviceRoot 'docs\HARDWARE.md') @"
# Hardware — $($device.Name)

$($device.Hardware)

Pinagem, níveis elétricos, alimentação e direção dos atuadores permanecem definidos pelo firmware ativo e pelos arquivos técnicos importados. Não foi feita alteração elétrica nesta reorganização. Antes de gravar, conferir variante da placa, alimentação, terra comum e estado seguro das saídas.
"@

    Write-Markdown (Join-Path $deviceRoot 'docs\VALIDATION.md') @"
# Validação — $($device.Name)

## Automatizada concluída

- compilação do firmware ativo;
- verificação de preservação do manifesto SHA-256;
- verificação estática de rotas e campos críticos contra o Hub;
- baseline monolítico mantido para auditoria.

## Bancada pendente

1. Gravar a placa correta e capturar versão/toolchain.
2. Confirmar boot, reconexão e ausência de reset cíclico.
3. Comparar telemetria e comandos com o baseline.
4. Testar perda de pacote, reinício do nó e reinício do Hub.
5. Validar sensores/atuadores em limites e estado seguro.
6. Executar soak test e arquivar logs em ``tests/evidence``.

$($device.Caveat)
"@

    Write-Markdown (Join-Path $deviceRoot 'archive\README.md') @"
# Arquivo histórico — $($device.Name)

- ``active-baseline/`` contém a versão monolítica que originou o firmware reorganizado.
- ``legacy/`` contém versões anteriores, experimentos e materiais preservados.
- ``IMPORT_MANIFEST.sha256`` registra os hashes antes da movimentação.

Nada em ``archive`` é selecionado automaticamente para build ou gravação. Não corrigir versões históricas no lugar; qualquer retomada deve ser copiada para uma nova área ativa com origem registrada.
"@
}

Write-Output "Generated standardized documentation for $($devices.Count) devices."
