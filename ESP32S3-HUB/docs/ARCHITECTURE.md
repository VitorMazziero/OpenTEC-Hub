# Arquitetura do TECNAL Hub v9

## Escopo

Este repositório contém apenas o Hub central ESP32-S3. O nó ASDA-B2, o aplicativo OpenTEC-Hub e seus respectivos testes permanecem fora deste repositório.

## Inicialização e execução

`ESP32S3-HUB.ino` contém somente `setup()` e `loop()`. Ambos delegam para `FirmwareApp`. O firmware legado foi dividido nas seguintes responsabilidades:

- `core/AppContext.h`: includes, objetos, configuração de hardware, estado e declarações;
- `storage/Settings.h`: NVS, defaults e debounce preservados do v8;
- `protocol/Mailboxes.h`: ACK revisionado e estado desejado do fluxômetro e periféricos;
- `network/HttpServer.h`: SoftAP, endpoints, chunks, cache e ETag;
- `core/Runtime.h`: watchdog, setup, loop e envio periódico;
- `protocol/Commands.h`: USB, parser e aplicação de comandos;
- `sensor/Telemetry.h`: leitura e serialização agregada;
- `devices/AgitatorFoam.h`: agitador e interlock de espuma;
- `sensor/SensorUart.h`: setters, sincronização e UART OpenTEC;
- `devices/ServoDevice.{h,cpp}`: amostra atômica, presença, estado desejado
  revisionado do motor e fila de eventos Servo;
- `control/ExternalBathCascade.{h,cpp}`: controlador PI puro, filtro, limites,
  anti-windup, máquina de estados e snapshot da cascata térmica;
- `protocol/JsonUtils.{h,cpp}`: busca manual e conversões estritas;
- `protocol/HttpCommandQueue.{h,cpp}`: fila fixa entre callbacks HTTP e o loop.

Os arquivos legados `.h` formam deliberadamente uma única unidade de compilação por meio de `FirmwareApp.cpp`. Isso preserva ordem de inicialização e comportamento dos globais durante a migração v8-v9. Componentes novos já usam interfaces `.h/.cpp`; cada fragmento legado poderá ser convertido de forma independente somente depois da equivalência física.

## Concorrência

- `sensorSerialMutex` serializa a UART do módulo OpenTEC.
- `cmdMutex` protege mailboxes revisionadas e o estado do fluxômetro.
- `stateMutex` protege cache/ETag e snapshots dos dispositivos atualizados por callbacks.
- `ServoDevice` e `HttpCommandQueue` possuem mutexes próprios.
- `/command` apenas valida o frame completo e o coloca em fila; o `loop` executa todas as mutações, inclusive NVS.
- A telemetria copia o estado compartilhado sob mutex e monta o JSON após liberar a região crítica.
- O snapshot do banho é copiado sob `stateMutex`; a cascata e a montagem do JSON
  não executam alocação nem I/O dentro da região crítica.

## Via térmica externa

`TempControlRoute::UartModule` mantém a via histórica. `ExternalBath` só calcula
quando há referência nova, `Tempval` fresco, nó r3.2+ registrado/online, `bathComm`, modo auto,
SP de display confirmado e guarda não suspensa. A saída do PI é limitada (também à faixa
`sp_min/sp_max` do nó) e entregue pelo `BathCommandCoordinator` (`src/control`), puro e testado
no PC: três posições com prioridade `stop` > `mode/sync` > `setpoint`, um payload por push,
conclusão vinculada ao `cmd_id` do setpoint (ACK e depois `done`/`idle`) e cooldown após a
conclusão. Reboot, PV stale ou retorno à UART limpam a atuação transitória e não fazem
fallback automático para a placa original.

**Parada do banho:** `bathAbort`, `tempSetpoint=0` na via externa e `resetVariables` entregam
`stop` (abort + modo manual no nó), desligam a cascata e liberam a posse; via e `bathComm` não
mudam e o C404 fica no último SP. Um novo `tempSetpoint` religa a cascata e pede `mode=auto`.

**Falhas:** eventos de borda do coordenador (entrada em `error`/`aborted`, guarda `suspended`,
recusa não-`busy` do nó) e timeout de 300 s travam a cascata até `bathCascadeReset`, que limpa a
conclusão pendente, esquece o estado já visto e rearma a guarda do nó.

**Posse:** enquanto a cascata está ativa, cada resposta do `/bathData` leva `X-Hub-Owner: 1`; o
nó recusa comandos locais (exceto abort/stop). Um alvo divergente no nó é reenviado até três
vezes antes de `target_override`.

## Controle direto do ASDA-B2

`motorSetpoint` continua entrando no Hub como 0..1000 rpm, mas não é mais
traduzido em `1V`/`<N>A` na UART. `ServoDevice` retém o último estado desejado,
atribui `motor_cmd_id` e o serve continuamente em `/servoCommand`. O
ESP32S3-driver aplica P1-09 por Modbus `10H`, devolve ACK em `/servoData` e para
se o heartbeat expirar. A fila de oito posições fica reservada aos eventos
`reset_energy` e `poll_ms`; uma parada nunca espera nessa fila.

O Hub descarta um setpoint de motor persistido no boot. Compilação não substitui
os gates de perfil do drive, E-stop, timeout de comunicação e ensaio de bancada.

