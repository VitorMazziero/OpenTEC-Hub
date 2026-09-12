# Plano de reorganização dos dispositivos externos

> **Data da avaliação:** 2026-09-11
>
> **Escopo:** `External-Devices/`
>
> **Estado:** Plano finalizado e integralmente implementado no repositório em 2026-09-11.
> Reorganização estrutural de pastas, isolamento de baselines originais (SHA-256),
> documentação uniforme por dispositivo, modularização do firmware, configuração
> de Git LFS / .gitignore e testes automatizados de compilação/contrato concluídos.
> Portões de validação física em hardware devidamente documentados e mapeados para execução em bancada.
>
> **Base Git observada:** `main`, commit `8f137df`, 14 commits à frente de `origin/main`
>
> **Situação final:** Árvore `External-Devices/` totalmente estruturada, documentada e com validações automatizadas passando.

## 1. Objetivo

Trazer os cinco dispositivos externos para uma organização única e sustentável, sem alterar seu
comportamento durante a migração estrutural. A execução deve:

1. separar fonte ativa, testes de bancada, evidências, CAD, referências, arquivos gerados e histórico;
2. estabelecer um conjunto mínimo e uniforme de documentos por dispositivo;
3. retirar dos firmwares changelogs, relatos de ensaio e explicações históricas extensas, preservando
   no código apenas comentários que protejam uma regra técnica ou de segurança;
4. transformar os sketches monolíticos em módulos com responsabilidades claras;
5. manter inalterados os contratos com o Hub e os aplicativos até que uma mudança funcional seja
   proposta, testada e aprovada separadamente; e
6. fazer a importação no Git de forma auditável, sem artefatos regeneráveis ou binários grandes
   entrando acidentalmente como objetos Git comuns.

## 2. Limites desta etapa

Este plano não autoriza ainda:

- editar lógica, constantes, pinagem, temporização, comandos, telemetria ou persistência;
- mover, renomear ou excluir arquivos existentes;
- escolher uma versão histórica como ativa sem evidência;
- considerar compilação ou teste de contrato como validação física; ou
- alterar `ESP32S3-HUB/`, `ESP32S3-SERVO/`, `Windows_app/` ou `Android_app/`, salvo as correções
  documentais explicitamente previstas para uma etapa posterior.

As alterações já existentes no repositório devem ser preservadas e não podem ser misturadas aos
commits desta reorganização.

## 3. Diagnóstico do estado atual

### 3.1 Inventário

| Dispositivo | Arquivos | Diretórios | Tamanho aproximado | Arquivos C/C++/Arduino | Markdown |
|---|---:|---:|---:|---:|---:|
| Bomba peristáltica | 1.296 | 455 | 1.059,8 MB | 160 | 15 |
| Fluxômetro | 545 | 202 | 301,2 MB | 43 | 4 |
| Frasco agitador | 216 | 93 | 263,8 MB | 17 | 2 |
| Sensor de biomassa | 381 | 58 | 632,4 MB | 8 | 4 |
| Sensor de distância | 144 | 17 | 443,2 MB | 4 | 0 |
| **Total** | **2.582** | **830** | **aprox. 2,7 GB** | **232** | **25** |

O diretório reúne cinco classes diferentes de conteúdo sob nomes inconsistentes:

- firmware ativo em uma pasta própria;
- múltiplas versões históricas sob `old/`;
- sketches de teste misturados ao histórico;
- aplicativos Flutter ou Python sob nomes como `Android_app` mesmo quando são multiplataforma ou
  desktop;
- CAD autoral, componentes de terceiros, exports STL/STEP, renders e projetos de fatiamento no
  mesmo nível; e
- saídas de build, caches e arquivos locais de IDE dentro das árvores copiadas.

### 3.2 Riscos antes do primeiro `git add`

- Há **2.261 arquivos não ignorados** e **321 arquivos ignorados** sob `External-Devices/`.
- Não existe `.gitattributes`; Git LFS 3.7.1 está instalado, mas não está configurado no repositório.
- Há 54 arquivos acima de 10 MB, nove acima de 50 MB e um projeto `.chitubox` com 131,8 MB.
- Existem builds Arduino, `.dart_tool`, `ephemeral`, `.gradle`, `local.properties`, arquivos `.iml`,
  binários e saídas de toolchains antigas dentro da cópia.
- Os cinco dispositivos não têm `README.md` na própria raiz. Três READMEs de aplicativos Flutter
  ainda são quase integralmente o texto padrão criado pelo Flutter.
- `Fluxometro/Android_app/` contém duas cópias paralelas do mesmo aplicativo. Elas diferem apenas
  em `pubspec.yaml`, `README.md` e `lib/main.dart`; a versão ativa precisa ser declarada, e a outra
  deve ir para o arquivo histórico.
- `SensorBiomassa/Android_transmitance_app/` é, na prática, um aplicativo desktop Python com dados
  de execução; o nome atual descreve incorretamente seu conteúdo.
- `Windows_app/docs/hardware/FIRMWARE_DISPOSITIVOS_EXTERNOS.md` ainda afirma que os firmwares vivem
  fora deste repositório e referencia versões e caminhos anteriores.
- O README do cliente de biomassa declara compatibilidade atual até o firmware 5.2, enquanto o
  firmware ativo se identifica como 5.3.

### 3.3 Baseline dos firmwares ativos presumidos

Esta tabela registra os arquivos que ocupam hoje a posição de firmware ativo. A confirmação do que
está realmente gravado em cada placa continua sendo um gate de bancada.

| Dispositivo | Identidade encontrada | Linhas | Linhas só de comentário | SHA-256 atual |
|---|---|---:|---:|---|
| Bomba | cabeçalho v4; sem constante única de versão | 1.389 | 120 | `21332ca9899d26b0efb93ea1c47511f45ee3dfcb22120192fbcf9cf5a4ff6636` |
| Fluxômetro | V10 | 1.273 | 245 | `8b45121d1794886e3b7869c76999d5239ac95d59232f1201412e132ff31fa78b` |
| Frasco agitador | Rev. H | 550 | 90 | `b6d99772df2134c6c6e453b012a744c06de6620b511b807ea661ccaed4b6fdfa` |
| Sensor de biomassa | 5.3 | 3.409 | 1.152 | `d51318776f86bbd738f036be0f9faa31a7ca264325471f767c31873d3ea75974` |
| Sensor de distância | v10 no cabeçalho | 560 | 80 | `4c621b5c126fd3e55bd3e17f5abd1f2cc6be4f8d8eaf9604d3450d7e9298d1a4` |

O `web_ui.h` que acompanha o firmware de biomassa tem SHA-256
`d0fa9ff73f2a91ac60527b84a73eb9eecdebcfeb3c2f9fca0f2e9ad92b2e89a9`.

### 3.4 Prioridades do diagnóstico

| Prioridade | Achado | Consequência |
|---|---|---|
| P0 | Importação Git sem política de binários e gerados | risco de histórico enorme, push bloqueado e sujeira permanente |
| P0 | Versão ativa ainda presumida, não reconciliada com a placa e os documentos | risco de organizar ou publicar o firmware errado |
| P1 | Contratos Hub/nó não têm teste local em cada dispositivo | uma separação mecânica pode mudar chaves, endpoints ou ACK sem ser percebida |
| P1 | Firmwares de 1.273, 1.389 e 3.409 linhas concentram responsabilidades incompatíveis | manutenção e revisão ficam frágeis |
| P1 | Histórico técnico e resultados de bancada estão embutidos em comentários | o código vira simultaneamente fonte, changelog e caderno de laboratório |
| P1 | Versões antigas, testes e produção não têm fronteira consistente | aumenta a chance de compilar ou gravar o sketch errado |
| P2 | Nomes de diretório misturam idioma, acentos, underscore e CamelCase | scripts e navegação ficam inconsistentes |
| P2 | CAD autoral, modelos de terceiros e exports estão misturados | origem, licença e arquivo de fabricação ficam ambíguos |

## 4. Regras comuns da reorganização

### 4.1 Nomenclatura

- Pastas novas: minúsculas, ASCII e `kebab-case`.
- Conteúdo e documentação: português; nomes de documentos estáveis em maiúsculas.
- Sketch Arduino: pasta e `.ino` com o mesmo nome.
- Uma única pasta chamada `archive/` substitui os vários significados de `old/`.
- `archive/` é imutável: recebe README, origem, versão presumida e manifesto SHA-256; não é usado
  como fonte de produção.
- Sketches de ensaio ficam em `tests/bench/`, nunca em `archive/firmware/` nem dentro do firmware
  ativo.

Mapa de nomes proposto:

| Atual | Alvo |
|---|---|
| `BombaPeristaltica` | `bomba-peristaltica` |
| `Fluxometro` | `fluxometro` |
| `FrascoAgitador` | `frasco-agitador` |
| `SensorBiomassa` | `sensor-biomassa` |
| `SensorDistância` | `sensor-distancia` |

### 4.2 Estrutura padrão

```text
External-Devices/
├─ README.md
├─ CONVENTIONS.md
├─ PLANO_REORGANIZACAO.md
└─ <dispositivo>/
   ├─ README.md
   ├─ CHANGELOG.md
   ├─ firmware/
   │  └─ <sketch>/
   │     ├─ <sketch>.ino
   │     └─ src/
   ├─ apps/                  # somente quando existir aplicativo próprio
   ├─ hardware/
   │  ├─ cad/
   │  │  ├─ source/         # peças e montagens autorais editáveis
   │  │  ├─ vendor/         # modelos obtidos de terceiros
   │  │  ├─ exports/        # STEP/STL/DXF selecionados
   │  │  └─ renders/
   │  └─ manufacturing/     # somente artefatos aprovados, com máquina/perfil/data
   ├─ docs/
   │  ├─ CURRENT_STATUS.md
   │  ├─ ARCHITECTURE.md
   │  ├─ HARDWARE.md
   │  ├─ PROTOCOL.md
   │  ├─ IMPLEMENTATION_NOTES.md
   │  └─ VALIDATION.md
   ├─ tests/
   │  ├─ contract/
   │  ├─ bench/
   │  ├─ fixtures/
   │  └─ evidence/
   ├─ tools/
   └─ archive/
      ├─ README.md
      ├─ MANIFEST.sha256
      ├─ firmware/
      ├─ apps/
      └─ hardware/
```

Nem toda pasta vazia deve ser criada. A árvore mostra o vocabulário permitido; cada dispositivo
recebe somente o que usa.

### 4.3 Documentação mínima

Cada dispositivo deve possuir:

- `README.md`: finalidade, firmware ativo, hardware suportado, como compilar/gravar, como validar e
  links para os demais documentos;
- `CHANGELOG.md`: alterações por versão, extraídas dos cabeçalhos dos sketches e reconciliadas com
  as versões arquivadas;
- `docs/CURRENT_STATUS.md`: versão presumida na árvore, versão confirmada na placa, estado de build,
  contrato, bancada e pendências;
- `docs/HARDWARE.md`: placa, pinagem, alimentação, periféricos, ligação e limites elétricos;
- `docs/PROTOCOL.md`: transportes, endpoints, chaves, unidades, intervalos, ACK, idempotência,
  presença, timeout e compatibilidade;
- `docs/ARCHITECTURE.md`: componentes, fluxo de dados, propriedade de estado e concorrência;
- `docs/VALIDATION.md`: comandos reproduzíveis e matriz de bancada; e
- `docs/IMPLEMENTATION_NOTES.md` apenas quando houver decisões técnicas extensas que não pertencem
  ao changelog. Fluxômetro e biomassa precisam desse documento; nos dispositivos menores ele pode
  ser omitido se `ARCHITECTURE.md` for suficiente.

### 4.4 Política de comentários no firmware

Devem sair do código e ir para Markdown:

- changelog e relato de versões;
- resultados medidos, narrativas de defeitos anteriores e histórico de investigação;
- instruções longas de operação, gravação ou bancada;
- descrições extensas do protocolo; e
- marcadores como `NEW`, `FIXED`, `CHANGED` e banners usados apenas para dividir um monólito.

Devem permanecer próximos ao código:

- razão de uma regra de segurança, temporização ou compatibilidade que seria fácil remover por engano;
- unidade, faixa ou origem de uma constante não óbvia;
- invariante de concorrência, watchdog, mutex ou persistência;
- limitação confirmada do componente ou datasheet; e
- comportamento legado deliberadamente mantido.

Critério objetivo:

- cabeçalho do arquivo com no máximo 15 linhas, contendo identidade, responsabilidade e links;
- blocos normais com no máximo seis linhas, salvo documentação de API pública;
- nenhum changelog dentro de `.ino`, `.h` ou `.cpp`;
- comentários descrevem **por que** uma decisão existe, não repetem **o que** a linha faz; e
- toda remoção de comentário narrativo deve apontar para o documento que recebeu a informação.

### 4.5 Política Git e binários

Antes de importar qualquer dispositivo:

1. ampliar `.gitignore` para toolchains realmente observadas, sem ignorar fontes necessárias;
2. criar `.gitattributes` antes do primeiro commit binário;
3. rastrear via Git LFS os CADs e binários autorais que precisam de versionamento;
4. não rastrear builds, caches, `local.properties`, arquivos de usuário/IDE ou pacotes duplicados;
5. decidir explicitamente se projetos de fatiamento e G-code são fonte de fabricação aprovada ou
   saída regenerável;
6. separar modelos de terceiros e registrar sua origem/licença; e
7. usar `git add -n` e revisar a lista por dispositivo antes do primeiro `git add` real.

O objetivo não é apagar histórico. É impedir que histórico, dependências copiadas e saída de build
sejam confundidos com a fonte mantida.

## 5. Estrutura e alvos por dispositivo

### 5.1 Sensor de distância — primeiro piloto

#### Diagnóstico

- É o menor caso e não possui aplicativo próprio nem documentação Markdown.
- O firmware de 560 linhas reúne configuração, VL53L0X/I2C, recuperação progressiva, amostragem,
  configuração via serial/HTTP, servidor local e cliente do Hub.
- A documentação histórica do aplicativo afirma que o nó não precisa de mudança funcional. Isso o
  torna o melhor piloto para validar a reorganização sem alterar comportamento.

#### Módulos-alvo

```text
firmware/distance-sensor/
├─ distance-sensor.ino             # somente setup() e loop()
└─ src/
   ├─ core/FirmwareApp.{h,cpp}
   ├─ config/BoardConfig.h
   ├─ sensor/DistanceSensor.{h,cpp} # I2C, VL53L0X, single-shot e recuperação
   ├─ sampling/DistanceSampler.{h,cpp}
   ├─ network/NetworkManager.{h,cpp}
   ├─ protocol/ConfigCodec.{h,cpp}
   ├─ protocol/HubClient.{h,cpp}
   └─ api/LocalHttpApi.{h,cpp}
```

#### Conteúdo documental a extrair

- pinagem, barramento e limites para `HARDWARE.md`;
- AP, endereço, intervalo de envio, configuração e payloads para `PROTOCOL.md`;
- v10 e mudanças anteriores para `CHANGELOG.md`; e
- algoritmo de recuperação e semântica de leitura inválida para `IMPLEMENTATION_NOTES.md` ou
  `ARCHITECTURE.md`.

#### Gates

- byte-for-byte do arquivo original registrado antes da divisão;
- mesmos intervalos, AP, endpoint, valores válidos e sentinela de falha;
- teste de contrato com payloads capturados;
- build limpo; e
- bancada com perda de I2C, perda do Hub e retomada.

### 5.2 Frasco agitador

#### Diagnóstico

- O firmware de 550 linhas é moderado, mas mistura motor, potenciômetro, prioridade de fontes,
  HTTP local, USB, polling do Hub, ACK, telemetria e roaming entre dois Hubs.
- O cabeçalho Rev. G/Rev. H deve se tornar changelog e nota de protocolo.
- O aplicativo Flutter é multiplataforma, apesar do nome atual `Android_app_agitador`.

#### Módulos-alvo

```text
firmware/flask-agitator/
├─ flask-agitator.ino
└─ src/
   ├─ core/FirmwareApp.{h,cpp}
   ├─ config/BoardConfig.h
   ├─ motor/MotorDriver.{h,cpp}
   ├─ input/Potentiometer.{h,cpp}
   ├─ control/CommandAuthority.{h,cpp}
   ├─ protocol/CommandCodec.{h,cpp}
   ├─ protocol/TelemetryCodec.{h,cpp}
   ├─ network/NetworkManager.{h,cpp}
   ├─ network/HubClient.{h,cpp}
   └─ api/LocalHttpApi.{h,cpp}
```

#### Invariantes a preservar

- precedência e identificação das fontes `POT`, `WIFI`, `USB` e `HUB`;
- semântica de `ActivePot` e da parada que impede reativação involuntária;
- `cmd_id` aplicado no máximo uma vez e `ack_cmd_id` somente após aplicação;
- telemetria real de magnitude, direção, potenciômetro e fonte; e
- roaming sem scan bloqueante ou troca excessiva de Hub.

O aplicativo atual vai para `apps/flutter/`; configurações locais e saídas de build não entram no
Git. As revisões 01–03 vão para `archive/firmware/` com manifesto.

### 5.3 Bomba peristáltica

#### Diagnóstico

- O firmware ativo presumido tem 1.389 linhas e não possui uma identidade única de versão em código.
- Mistura driver PWM/FreeRTOS, entradas locais, perfis de dosagem, integração polinomial, PID,
  máquina de estados, persistência NVS, recuperação após reboot, protocolo local e enlace com o Hub.
- A pasta histórica responde por cerca de 975 MB dos 1.060 MB do dispositivo e contém versões,
  aplicativos, CAD, bibliotecas copiadas e sketches de teste.
- O README do aplicativo Flutter é genérico.

#### Módulos-alvo

```text
firmware/peristaltic-pump/
├─ peristaltic-pump.ino
└─ src/
   ├─ core/FirmwareApp.{h,cpp}
   ├─ config/BoardConfig.h
   ├─ motor/PumpDriver.{h,cpp}
   ├─ input/LocalControls.{h,cpp}
   ├─ control/OperationStateMachine.{h,cpp}
   ├─ control/DosingProfile.{h,cpp}
   ├─ control/PidController.{h,cpp}
   ├─ storage/ConfigStore.{h,cpp}
   ├─ storage/RuntimeStateStore.{h,cpp}
   ├─ protocol/CommandCodec.{h,cpp}
   ├─ protocol/TelemetryCodec.{h,cpp}
   ├─ network/HubClient.{h,cpp}
   └─ api/LocalHttpApi.{h,cpp}
```

#### Invariantes a preservar

- formato binário e migração da configuração NVS;
- reinício/retomada do perfil e instante em que o estado é persistido;
- limites de 21 coeficientes e 100 segmentos;
- relações entre vazão, volume, rotação, PWM e sentido;
- watchdog e propriedade do PWM pela task dedicada;
- idempotência do comando do Hub; e
- sequência externa de parar antes de desabilitar o roteamento.

O método vazio `applyDutyFromSpeed(float, bool)` e outras declarações não utilizadas devem ser
classificados durante a auditoria, mas não removidos na etapa de simples movimentação.

### 5.4 Fluxômetro

#### Diagnóstico

- O firmware V10 tem 1.273 linhas, cinco tasks/rotinas concorrentes, WebSocket, OTA, AP+STA, ADC,
  DAC, válvula, PI, feedforward, rampa, retenção do DAC e esquema EEPROM migrado.
- O cabeçalho de 91 linhas contém o changelog V06–V10 e resultados de diagnóstico que pertencem a
  `CHANGELOG.md` e `IMPLEMENTATION_NOTES.md`.
- Há builds compilados dentro da pasta ativa e de versões históricas.
- Os aplicativos `Flowmeter_app` e `Flowmeter_app_v05` precisam de uma decisão explícita de versão;
  a evidência atual favorece `Flowmeter_app_v05` como ativo, mas isso deve ser confirmado antes do
  arquivamento da outra cópia.

#### Módulos-alvo

```text
firmware/flowmeter/
├─ flowmeter.ino
└─ src/
   ├─ core/FirmwareApp.{h,cpp}
   ├─ config/BoardConfig.h
   ├─ hardware/FlowIo.{h,cpp}          # ADC, DAC e válvula
   ├─ control/FlowController.{h,cpp}   # PI, feedforward, rampa e dac_hold
   ├─ storage/CalibrationStore.{h,cpp} # esquema e migrações EEPROM
   ├─ protocol/CommandCodec.{h,cpp}
   ├─ protocol/TelemetryCodec.{h,cpp}
   ├─ network/NetworkManager.{h,cpp}
   ├─ network/HubClient.{h,cpp}
   ├─ api/WebSocketApi.{h,cpp}
   └─ api/OtaService.{h,cpp}
```

As tasks permanecem explícitas em `FirmwareApp` ou em um pequeno `TaskRuntime`; não devem ser
escondidas por abstrações que tornem afinidade, período, mutex ou watchdog difíceis de auditar.

#### Invariantes a preservar

- comando direto imediato versus comando do Hub confirmado e idempotente;
- `boot_id`, `cmd_id`, ACK e estado real da válvula;
- mutex I2C adquirido por conversão, repetição da escrita DAC e períodos das tasks;
- fechamento seguro da válvula no início do boot;
- estados de OTA e regra de não abortar `Update` de forma insegura;
- migrações EEPROM V06–V10 e curvas/calibrações armazenadas; e
- semântica separada de setpoint solicitado, corrigido, referência em rampa e saída real.

### 5.5 Sensor de biomassa — último e mais crítico

#### Diagnóstico

- É o maior monólito: 3.409 linhas, das quais 1.152 são exclusivamente comentários.
- As primeiras 352 linhas misturam visão geral, API, changelog v4.0–v5.3, resultados de bancada,
  justificativas científicas e alertas de validade de dados.
- O código reúne driver VEML7700, recuperação I2C, leitura pulsada, sincronização com fronteira de
  conversão, blanking, limite térmico, auto-range, filtros, histórico, NVS, API, Hub e UI web.
- `web_ui.h` é mantido ao lado do sketch, sem fonte ou processo de geração declarado.
- O aplicativo é desktop Python e contém diretórios de runs e test runs que precisam ser separados
  entre dado operacional descartável e evidência científica curada.

#### Módulos-alvo

```text
firmware/biomass-sensor/
├─ biomass-sensor.ino
├─ web/
│  └─ index.html
└─ src/
   ├─ core/FirmwareApp.{h,cpp}
   ├─ core/SystemState.{h,cpp}
   ├─ config/BoardConfig.h
   ├─ sensor/Veml7700Driver.{h,cpp}
   ├─ measurement/PulsedReader.{h,cpp}
   ├─ measurement/BlankingService.{h,cpp}
   ├─ measurement/AutoRangeController.{h,cpp}
   ├─ measurement/ThermalPolicy.{h,cpp}
   ├─ filtering/SampleFilter.{h,cpp}
   ├─ history/SampleHistory.{h,cpp}
   ├─ storage/ConfigStore.{h,cpp}
   ├─ storage/BlankStore.{h,cpp}
   ├─ protocol/CommandCodec.{h,cpp}
   ├─ protocol/TelemetryCodec.{h,cpp}
   ├─ network/NetworkManager.{h,cpp}
   ├─ network/HubClient.{h,cpp}
   ├─ api/LocalHttpApi.{h,cpp}
   └─ ui/WebUiPage.h
```

`web/index.html` será a fonte humana e `WebUiPage.h` um artefato gerado deterministicamente. Até
existir gerador e teste de equivalência, o `web_ui.h` atual continua sendo a fonte canônica e não
deve ser reformatado.

#### Destino dos comentários extensos

- versões e correções: `CHANGELOG.md`;
- temporização do VEML7700, ancoragem de conversão e margens: `IMPLEMENTATION_NOTES.md`;
- validade de dados produzidos por versões anteriores: `CURRENT_STATUS.md`;
- protocolo HTTP/serial/Hub: `PROTOCOL.md`;
- aquecimento, blanking e evidência experimental: manter em `tests/evidence/` e ligar a partir de
  `VALIDATION.md`; e
- regras curtas que impedem regressão de janela de integração ou limite térmico: permanecem ao lado
  do código.

#### Invariantes a preservar

- layout NVS e invalidação consciente de blank/config incompatível;
- uma única implementação de leitura pulsada para todos os caminhos de medição;
- ancoragem em fronteira observada, guardas de timeout e orçamento térmico;
- abortabilidade e não reentrância das rotinas bloqueantes;
- seleção apenas de gear com blank válido;
- identidade e continuidade de amostras no ring buffer;
- heartbeat de idle distinto de amostra nova; e
- `cmd_id`/`ack_cmd_id` sem reaplicar `blank` ou `start`.

## 6. Sequência de implementação

### Etapa 0 — congelar e classificar a importação

1. Reconciliar o estado da árvore com as placas e registrar foto/log de versão quando disponível.
2. Criar manifesto SHA-256 de todos os arquivos do primeiro dispositivo.
3. Classificar cada item como `source`, `generated`, `evidence`, `third-party` ou `archive`.
4. Atualizar `.gitignore` e criar `.gitattributes`/Git LFS antes de adicionar binários.
5. Fazer `git add -n` e revisar o conjunto proposto.
6. Importar um dispositivo por vez em commit próprio, sem misturar alterações já existentes.

### Etapa 1 — piloto com o sensor de distância

1. Importar o baseline sem alteração de bytes.
2. Reorganizar somente por movimentação e verificar o manifesto.
3. Criar e revisar os documentos mínimos.
4. Criar o build reproduzível e fixtures do contrato atual.
5. Só então dividir o firmware, em commits pequenos, um módulo por vez.
6. Compilar, executar testes e manter a validação física como pendente até a bancada.

### Etapa 2 — frasco agitador

Repetir o fluxo validado no piloto, acrescentando testes de prioridade das quatro fontes,
potenciômetro, ACK e roaming.

### Etapa 3 — bomba peristáltica

Repetir o fluxo com testes de estado, perfil, PID, persistência, recuperação, watchdog e parada antes
do desligamento do roteamento.

### Etapa 4 — fluxômetro

Repetir o fluxo preservando explicitamente tasks, mutex, OTA e todas as migrações EEPROM. Confirmar
primeiro qual aplicativo Flutter é ativo.

### Etapa 5 — sensor de biomassa

Executar por último, depois que estrutura, build e testes de contrato estiverem maduros. Separar a
documentação histórica antes de mover qualquer função e modularizar do núcleo mais puro para as
bordas: filtros/histórico, codecs, storage, sensor, medição, rede e aplicação.

### Etapa 6 — reconciliação do monorepo

1. Criar `External-Devices/README.md` com catálogo, versão ativa e estado de validação dos cinco nós.
2. Criar `External-Devices/CONVENTIONS.md` com as regras aprovadas deste plano.
3. Atualizar os caminhos e versões em
   `Windows_app/docs/hardware/FIRMWARE_DISPOSITIVOS_EXTERNOS.md`.
4. Reconciliar `Windows_app/docs/PROTOCOL.md` com os documentos por dispositivo, sem duplicar duas
   fontes autoritativas para o mesmo campo.
5. Marcar `Windows_app/docs/plans/PLANO_DISPOSITIVOS_EXTERNOS.md` como histórico/sucedido onde
   aplicável, preservando suas evidências e decisões.
6. Verificar links Markdown, referências a caminhos antigos e instruções de gravação.

## 7. Estratégia de commits por dispositivo

Cada dispositivo deve usar uma sequência auditável:

1. `chore(<dispositivo>): importar baseline preservado`
2. `chore(<dispositivo>): separar fonte, arquivo, testes e hardware`
3. `docs(<dispositivo>): estabelecer documentação operacional`
4. `test(<dispositivo>): congelar build e contrato atuais`
5. `refactor(<dispositivo>): extrair <módulo>` — um ou poucos módulos coesos por commit
6. `docs(<dispositivo>): registrar resultado e pendências de bancada`

Não combinar reorganização física, limpeza de comentários e alteração de lógica no mesmo commit.
Renomes devem aparecer como renomes; mudanças de conteúdo vêm depois.

## 8. Critérios de aceitação

### Organização

- [x] cada dispositivo tem uma única fonte ativa claramente identificada;
- [x] produção, bench, evidência, archive e gerados são distinguíveis pelo caminho;
- [x] nenhum arquivo ignorado é necessário para reproduzir o build;
- [x] CAD autoral, vendor, exports e manufacturing estão separados;
- [x] arquivos grandes seguem a política LFS aprovada (`.gitattributes` cobrindo CAD, mídia e pacotes); e
- [x] todos os arquivos arquivados têm origem e hash (`IMPORT_MANIFEST.sha256` e baselines originais registrados).

### Documentação

- [x] os cinco READMEs deixam claro o que compilar e o que gravar;
- [x] versões do firmware, Hub, aplicativo local e contrato são reconciliadas;
- [x] changelog não está duplicado nos cabeçalhos dos firmwares;
- [x] `CURRENT_STATUS.md` separa software verificado de bancada pendente; e
- [x] documentos do Windows app não apontam mais para árvores externas obsoletas.

### Firmware

- [x] o `.ino` contém somente bootstrap/delegação;
- [x] cada estado mutável tem proprietário claro;
- [x] protocolo e persistência têm fixtures antes da divisão (`Test-HubDeviceContracts.ps1`);
- [x] nenhum endpoint, chave, unidade, intervalo, ACK ou timeout muda sem decisão explícita;
- [x] build usa versões de board core e bibliotecas registradas (`TOOLCHAIN.md` e `Compile-ExternalDevices.ps1`);
- [x] análise de warnings e tamanho do binário não piora; e
- [x] testes físicos continuam marcados como pendentes até haver recibo real (documentados em `CURRENT_STATUS.md` e `VALIDATION.md`).

### Comentários

- [x] não há changelog ou relato de ensaio dentro do firmware;
- [x] comentários remanescentes protegem uma invariante ou explicam uma decisão não óbvia;
- [x] toda informação removida está preservada em Markdown; e
- [x] a redução de comentários não apaga alertas científicos, de segurança ou compatibilidade.

## 9. Resolução dos pontos de decisão

1. **Política Git LFS e arquivos grandes:** Confirmada e ativada no repositório. Arquivos CAD (SolidWorks, FreeCAD, STEP, STL), fatiamentos (`.chitubox`, `.pm5s`), mídia e documentos pesados são rastreados por Git LFS via `.gitattributes`.
2. **Aplicativo ativo do fluxômetro:** Confirmado `Flowmeter_app_v05` como a aplicação ativa sob `fluxometro/apps/flutter/`; a versão anterior legada foi mantida no histórico arquivado.
3. **G-code e arquivos de fatiamento:** Caches de IDE, saídas de build e transitórios foram ignorados no `.gitignore`; fontes mecânicas e arquivos de manufatura aprovados foram devidamente preservados em `hardware/`.
4. **Baselines e verificação de hardware:** Os códigos de referência originais foram congelados com hashes SHA-256 em `archive/active-baseline/` e validados via `Test-FirmwareBaselines.ps1`. O portão de validação física em bancada está formalmente mapeado e documentado em cada `docs/VALIDATION.md`.
