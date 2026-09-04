# Integração da potência no OpenTEC-Hub

> **Atualização 2026-09-04:** a premissa histórica de nó somente-leitura/CN1 foi
> substituída pela migração coordenada do comando de rpm para P1-09 via Modbus.
> Para implementação, ordem de upload e gates atuais, use
> [`PLANO_MIGRACAO_RPM_MODBUS.md`](PLANO_MIGRACAO_RPM_MODBUS.md). O conteúdo
> abaixo permanece como registro da integração de telemetria.

**Data da decisão:** 2026-09-01  
**Escopo:** Delta ASDA-B2 → ESP32-S3 de potência → ESP32-S3 Hub v8 → aplicativo OpenTEC-Hub  
**Estado:** comunicação Modbus básica confirmada; telemetria dinâmica, enlace Wi-Fi e aplicativo ainda não validados de ponta a ponta.

## 1. Resultado já comprovado

Com o HW-097 novo, o ESP32-S3 recebeu resposta do Delta ASDA-B2 e leu os
parâmetros abaixo a **9600 baud, 8N2, slave 1**:

| Parâmetro | Valor lido | Interpretação |
|---|---:|---|
| P3-00 | `0x0001` | endereço Modbus 1 |
| P3-01 | `0x0011` | 9600 baud |
| P3-02 | `0x0066` | Modbus RTU, 8N2 |
| P3-05 | `0x0000` | mecanismo configurado |
| P3-07 | `0x0064` | atraso de resposta 100 |
| P1-01 | `0x0002` | modo de controle; não alterar |
| P0-00 | `0x03F6` | versão de firmware do drive |

Isto comprova a camada física, a direção do HW-097, a polaridade A/B, o
formato serial, o endereço e a leitura Modbus `03H`. Ainda **não** comprova:

- os endereços, escalas e sinal dos registradores de rpm, torque, carga e estado;
- o comportamento com o motor girando e sob carga;
- o funcionamento simultâneo de Modbus e Wi-Fi no nó de potência;
- a recepção pelo Hub v8 e pelo aplicativo;
- a estabilidade da alimentação de 5 V durante picos de transmissão Wi-Fi.

## 2. Arquitetura decidida

```text
Delta ASDA-B2
  CN1: comando de velocidade existente, inalterado
  CN3: Modbus RTU — leitura 03H + escrita 06H só em P0-45
       |
       v
HW-097 + ESP32-S3 de potência
  UART1: GPIO17 TX, GPIO18 RX, GPIO16 DE+/RE
  9600 8N2, slave 1
  polling Modbus e integração de energia local
       |
       | Wi-Fi STA + HTTP no SoftAP ModuloTECNAL_1
       | GET /servoData e GET /servoCommand
       v
ESP32-S3 Hub v8
  agrega os campos Servo* em GET /readData
       |
       | USB ou Wi-Fi
       v
Aplicativo OpenTEC-Hub
  parser, estado, painel, gráficos, registro e alarmes
```

O nó de potência é **leitura por `03H` mais uma única escrita `06H`**, revisto em
2026-09-02. A escrita tem um destino e só um: `P0-45 = 54` (endereço `005AH`), o
seletor do monitor de torque de retorno. Ele é volátil por design — o par
`P0-44`/`P0-45` é o "for PC Software", default `0x0` — e zera a cada
religamento do drive; sem reescrevê-lo, `P0-44` devolve a posição em pulsos e
responde Modbus normalmente, produzindo telemetria de torque silenciosamente
errada. A escrita é confirmada por leitura, e uma amostra sem o mapeamento
válido é descartada em vez de publicada.

A regra anterior ("apenas `03H`") era remanescente da estratégia de RS-232 e foi
revogada. O que permanece proibido é o resto: habilitação do servo, ganho,
limite, modo de controle e comando de velocidade continuam fora deste nó e fora
desta integração. O critério auditável passa a ser **"a única escrita é
`P0-45`"**, não "nenhuma escrita".

## 3. Próximo passo recomendado

O próximo passo não é alterar o aplicativo ainda. Primeiro deve-se fechar o
contrato de telemetria na bancada:

1. Gravar o candidato em
   `Software/firmware-producao/ASDA_B2_Servo_Node/ASDA_B2_Servo_Node.ino`.
2. Com o Wi-Fi inicialmente desabilitado ou sem Hub, confirmar que o polling
   Modbus continua funcionando.
3. Registrar um ensaio curto em três estados: motor parado, motor girando sem
   carga adicional e motor sob uma condição de carga conhecida.
4. Conferir P0-09, P0-10, P0-44, P0-46 e P0-01 contra o painel/manual, incluindo
   sinal, quantidade de words, ordem das words e fator de escala.
5. Somente depois ligar o Hub v8 e observar `GET /readData`.

Critério de saída: rpm acompanha o valor do painel, torque/carga variam de forma
coerente, estado/alarme não produzem falsos positivos e `commErr` não cresce em
regime estável.

## 4. Contrato entre o nó de potência e o Hub v8

O Hub v8 já contém os handlers `/servoData` e `/servoCommand` e publica os
campos `Servo*` em `/readData`. O nó envia a cada segundo:

| Query parameter | Tipo | Semântica |
|---|---|---|
| `rpm` | float | rotação medida no drive |
| `torque_pct` | float | torque em % do nominal |
| `torque_nm` | float | torque convertido em N·m |
| `load_pct` | float | carga reportada pelo drive, % |
| `power_w` | float | potência mecânica estimada no eixo |
| `energy_wh` | float | integral local da potência mecânica |
| `state` | int | 0 OFF, 1 READY, 2 SON, 3 ALARM |
| `alarm` | int | código de alarme, 0 sem alarme |
| `ok` | uint32 | transações Modbus válidas acumuladas |
| `err` | uint32 | transações Modbus inválidas acumuladas |

`power_w` e `energy_wh` **não são potência e energia elétrica consumidas da
rede**. São grandezas mecânicas estimadas por torque × velocidade. A interface
deve usar explicitamente os rótulos “Potência mecânica estimada” e “Energia
mecânica acumulada”. Medição elétrica exigiria instrumentação elétrica própria.

O Hub publica sempre `ServoOnline`. Os demais valores só aparecem enquanto o
nó estiver dentro da janela de presença de 6 s. Zero rpm é um valor válido e
não pode ser confundido com ausência.

### Ajuste adicional necessário no Hub v8

Adicionar `ServoCommEnabled` a cada `/readData`, refletindo `servoCommOn`. Sem
esse eco, o aplicativo não consegue distinguir “nó ausente” de “roteamento
desabilitado no Hub”. Esta é uma pequena alteração no Hub v8, não no nó de
potência.

Os comandos existentes são administrativos e não atuam no drive:

| Comando do aplicativo para `/command` | Encaminhado ao nó | Efeito |
|---|---|---|
| `servoComm` | — | habilita/desabilita o roteamento no Hub |
| `resetServoEnergy` | `reset_energy` | zera a integral local |
| `servoPollMs` | `poll_ms` | muda o período, limitado a 250–10000 ms |

Como `/servoCommand` usa consumo-na-leitura e não possui `cmd_id`, o aplicativo
não deve anunciar confirmação imediata. Para o reset, mostrar “solicitação
enviada” e confirmar somente quando `ServoEnergyWh` retornar próximo de zero.

## 5. Alterações necessárias no aplicativo OpenTEC-Hub

Repositório atual:
`D:/OneDrive/PosDoc_Fapesp/Automacao_e_Controle/ProjetoTECNAL`.

### 5.1. Contrato e parser

Em `src/OpenTECHub.Protocol/CommandKeys.cs`:

- adicionar as chaves de telemetria `ServoOnline`, `ServoCommEnabled`,
  `ServoRpm`, `ServoTorquePct`, `ServoTorqueNm`, `ServoLoadPct`, `ServoPowerW`,
  `ServoEnergyWh`, `ServoState`, `ServoAlarm`, `ServoCommOk` e `ServoCommErr`;
- adicionar os comandos `servoComm`, `resetServoEnergy` e `servoPollMs`.

Em `src/OpenTECHub.Protocol/CommandBuilders.cs`:

- criar builders tipados para habilitar o roteamento, solicitar reset da
  energia e alterar o intervalo de polling;
- validar `servoPollMs` entre 250 e 10000 ms antes de serializar;
- manter formatação numérica invariável à cultura.

Em `src/OpenTECHub.Protocol/SensorReadings.cs`:

- incluir todos os valores do servo em `SensorReadings` e `SensorSnapshot`;
- incluir `HasServoTelemetry`, `ServoOnline` e `bool? ServoCommEnabled`;
- inicializar grandezas numéricas com `NotReceived`, exceto contadores/estado
  que devem ter um sentinel explícito (`-1`) antes da primeira amostra;
- copiar todos os campos em `Snapshot()`.

Em `src/OpenTECHub.Protocol/TelemetryParser.cs`:

- adicionar `ParserConfig.ServoTimeout`, ligeiramente maior que a janela do
  Hub, apenas como fallback para Hubs antigos;
- implementar `ParseServo` usando a mesma regra de presença dos outros nós;
- chave `ServoOnline` presente = estado do Hub é autoritativo;
- chave ausente e valores nunca vistos = `HasServoTelemetry == false`, isto é,
  “Hub antigo/sem evidência”, não “servo desconectado”;
- `ServoOnline:false` invalida rpm, torque, carga, potência, energia, estado e
  alarme, evitando mostrar uma última amostra congelada como atual;
- rejeitar NaN, infinito e valores fisicamente impossíveis sem transformar
  leitura ausente em zero;
- manter `ServoCommOk`/`ServoCommErr` monotônicos por sessão, exceto após reboot
  detectado do nó.

### 5.2. Estado, apresentação e comandos

Criar `src/OpenTECHub/ViewModels/ServoDriveViewModel.cs` com:

- `ExternalDeviceStatus` para separar solicitado, roteado e presente;
- leituras formatadas de rpm, torque %, torque N·m, carga %, potência W e
  energia Wh;
- texto de estado OFF/READY/SON/ALARM e código de alarme;
- taxa de erro derivada dos deltas de `ServoCommOk` e `ServoCommErr`, sem usar o
  total histórico bruto como taxa;
- comandos “Habilitar comunicação”, “Zerar energia” e período de amostragem;
- nenhum comando de velocidade, enable do drive ou escrita Modbus.

Registrar o view-model em `src/OpenTECHub/App.xaml.cs`, injetá-lo em
`ControlViewModel` e descartá-lo junto com os demais assinantes de telemetria.

Em `src/OpenTECHub/Views/ControlView.xaml`:

- adicionar um painel compacto “Servo drive / Potência” na área de dispositivos
  externos;
- reutilizar `ExternalDeviceChips` para aguardando telemetria, offline e
  divergência de roteamento;
- manter controles desabilitados se o Hub estiver desconectado;
- deixar o reset separado de qualquer ação de parada segura, pois ele só zera
  um acumulador de dados;
- destacar ALARM sem interpretar o código até a tabela do manual ser fechada.

No detalhe de agitação, preservar duas grandezas diferentes:

- **setpoint de agitação**: valor comandado pelo sistema atual;
- **rpm medida no servo**: feedback do drive.

Não substituir silenciosamente o canal `MotorRpm` atual, que hoje representa
comando. Criar um canal separado `ServoRpm`; só após validação pode-se decidir
qual deles será a leitura principal no sinótico.

### 5.3. Gráficos e histórico em memória

Em `src/OpenTECHub/Services/Telemetry/TelemetryHistory.cs`:

- adicionar `ServoRpm`, `ServoTorquePct`, `ServoTorqueNm`, `ServoLoadPct`,
  `ServoPowerW` e `ServoEnergyWh` ao enum `TelemetryChannel`;
- gravar NaN quando o servo estiver ausente/offline;
- manter `MotorRpm` como comando para não quebrar séries e análises existentes.

Em `src/OpenTECHub/ViewModels/ChartsViewModel.cs` e no seletor do sinótico:

- expor as novas séries com unidades corretas;
- evitar colocar seis séries novas por padrão; deixar seleção explícita;
- usar “Potência mecânica estimada (W)” e “Energia mecânica acumulada (Wh)”.

### 5.4. Persistência de dados

`SessionLogger.cs` declara o TSV legado como contrato fixo. Não inserir colunas
Servo no meio desse arquivo. Criar um sidecar versionado por sessão, por exemplo
`servo-power.tsv`, e registrá-lo no manifesto tratado por `SessionFiles.cs`.

Cabeçalho mínimo recomendado:

```text
time_min\tservo_online\trpm\ttorque_pct\ttorque_nm\tload_pct\tpower_w\tenergy_wh\tstate\talarm\tcomm_ok\tcomm_err
```

Regras:

- ponto decimal e cultura invariável;
- timestamp alinhado ao mesmo `TimeMinutes` do frame principal;
- vazio/NaN para leitura ausente, nunca zero artificial;
- metadados com versão do contrato, versão do Hub e identificação do ensaio;
- energia acumulada reiniciada deve gerar evento no journal.

### 5.5. Alarmes e eventos

Em `ShellViewModel`/kernel de alarmes e `EventJournal`:

- evento quando o nó passa online/offline;
- aviso quando a fração de erros Modbus ultrapassar limite decidido após a
  bancada, usando janela temporal e histerese;
- alarme quando `ServoState == ALARM` ou `ServoAlarm != 0`;
- evento de reset de energia solicitado e posteriormente observado;
- não disparar alarme apenas porque um Hub antigo não publica `ServoOnline`.

### 5.6. Simulador e testes obrigatórios

Atualizar `src/OpenTECHub.Simulator/WireCodec.cs` para simular:

- Hub antigo sem chaves Servo;
- nó online parado (rpm e potência iguais a zero);
- nó online em movimento;
- nó offline com valores omitidos;
- alarme e crescimento de `ServoCommErr`;
- reset observado de energia.

Adicionar testes em:

- `TelemetryParserTests.cs`: presença, offline, invalidation, zero legítimo,
  strings numéricas, valores inválidos e cultura pt-BR;
- `WireFormatTests.cs`: comandos e nomes exatos das chaves;
- `TelemetryTests.cs`: novas séries e gaps NaN;
- novo `ServoDriveViewModelTests.cs`: estados, comandos, confirmação observada
  do reset, desconexão do Hub e alarme;
- testes de persistência do sidecar e leitura de sessões sem o novo arquivo;
- teste visual/contratual do painel em `ControlView`.

Compatibilidade obrigatória: o aplicativo novo deve continuar funcionando com
Hub v7/v8 antigo, e sessões antigas devem continuar abrindo sem `servo-power.tsv`.

## 6. Sequência de implementação

### Gate A — registradores dinâmicos no drive

- [x] rpm parado e girando conferida contra o painel _(2026-09-02: 0 rpm parado;
      ~96,6 rpm lidos contra ~96 no painel a 100 rpm comandados — a diferença é
      erro de ganho do comando analógico do CN1, não de escala)_
- [x] torque/carga conferidos _(1,4..2,3 % a vazio, coerentes entre `P0-44`,
      `P0-10` e `P0-11`; N·m e W batem com o cálculo manual)_
- [ ] ~~sinais de torque conferidos~~ — **não testável nesta bancada**: a placa
      controladora do CN1 é de código fechado e só comanda um sentido. Ver nota
      no `PLANO_TESTES_DOIS_ESP32S3.md`
- [x] estado e alarme conferidos _(READY→SON acompanhando a energização;
      `P0-01` = 0 em todas as amostras)_
- [x] contadores estáveis em ensaio contínuo _(690 s, 1638 leituras, err=0 com o
      motor parado)_

### Gate B — nó de potência

- [ ] candidato compila para ESP32S3 Dev Module
- [ ] a única escrita no binário/código auditado é `P0-45` (`06H` em `005AH`);
      nenhum outro endereço é escrito
- [ ] Modbus continua durante queda de Wi-Fi
- [ ] energia não salta após queda/reconexão
- [ ] 5 V não sofre brownout com Wi-Fi transmitindo

### Gate C — Hub v9

Executado em 2026-09-02 com hub em COM7 (`HubFirmwareVersion 9.0.0-dev`,
`HubProtocolVersion 9`) e nó em COM9.

- [x] `/servoData` recebe a amostra _(`ServoCommOk` avançando, `ServoCommErr:0`)_
- [x] `/readData` publica `ServoOnline:false` com nó ausente _(60 quadros
      consecutivos a 2,0 s com o nó desconectado, sem lacuna)_
- [x] publica valores coerentes com nó presente _(200 e 600 rpm comandados pelo
      próprio hub e medidos de volta pelo caminho independente)_
- [x] volta a offline após 6 s sem push _(medido entre 6,0 e 8,0 s; a resolução
      é o quadro de 2 s)_
- [x] `ServoCommEnabled` é publicado sempre _(presente em todos os quadros, com
      nó presente e ausente)_
- [ ] demais dispositivos e ACK do flowmeter continuam inalterados — **não
      exercitado**: nenhum outro nó está na bancada. Os campos de todos eles
      aparecem íntegros no quadro, mas `false`/ausentes por não haver hardware.
- [x] comandos hub→nó entregues e efetivados _(`resetServoEnergy` com queda
      observada de `ServoEnergyWh`; `servoPollMs` com efeito medido na taxa)_

### Gate D — núcleo do aplicativo

- [ ] parser, snapshot, comandos e simulador implementados
- [ ] testes novos e suíte completa verdes
- [ ] Hub antigo não gera falso “offline”

### Gate E — interface e dados

- [ ] painel distingue solicitado/roteado/presente
- [ ] setpoint e rpm medida permanecem distintos
- [ ] potência/energia são rotuladas como mecânicas estimadas
- [ ] gráficos mostram gaps quando o nó fica offline
- [ ] sidecar é gravado e reaberto
- [ ] app é iniciado e logs WPF recentes são inspecionados

### Gate F — validação de ponta a ponta

- [ ] motor parado, girando e sob carga aparecem corretamente no aplicativo
- [ ] alarme real ou simulado aparece e é removido corretamente
- [ ] desconexão/reconexão de cada elo é observável
- [ ] CN1 continua comandando velocidade normalmente
- [ ] nenhum parâmetro do drive é escrito pelo nó de potência

Somente após Gate F o conjunto deve ser tratado como integração de campo. Build,
testes e leitura estática não substituem a validação física.

## 7. Estrutura da pasta de potência

```text
ESP32S3-SERVO/
├── PLANO_INTEGRACAO_POTENCIA_OPENTECHUB.md
├── CAD-conector-C3/
├── Delta-ASDA-B2-Catalog.pdf
├── Manual_ASDA-B2_EN_20180509.pdf
├── Software/
│   ├── README.md
│   ├── firmware-producao/
│   │   └── ASDA_B2_Servo_Node/
│   ├── testes-bancada/
│   │   ├── esp32-s3/
│   │   ├── arduino-uno/
│   │   ├── pc/
│   │   └── legado-hw519/
│   └── documentacao/
└── tmp/                    artefatos temporários de consulta aos PDFs
```

O arquivo que deve evoluir para o ESP32-S3 instalado junto ao driver é apenas:

`Software/firmware-producao/ASDA_B2_Servo_Node/ASDA_B2_Servo_Node.ino`.

Os demais sketches são evidência/diagnóstico de bancada e não devem ser usados
como firmware de produção.
