# Fluxômetro: ausências de telemetria, tráfego e dados brutos

Análise de 12/10/2026. Correções locais verificadas em software; os equipamentos não foram gravados nem o aplicativo do cultivo reiniciado.

## Revisão adicional de 12/10/2026

Concordância com a separação dos comandos por atuador, preservação do grupo de gás, redução do polling e painel selecionável. A revisão encontrou e corrigiu mais três pontos:

1. **Períodos perdidos na disputa de comunicação:** os relógios de comando, telemetria e anúncio eram avançados antes da aquisição do mutex. Agora só avançam quando a tentativa HTTP realmente começa; um mutex ocupado permite tentar novamente após a espera curta da tarefa. O relógio é relido depois do anúncio, que pode demorar.
2. **Desconexão forçada injustificada:** respostas HTTP 4xx/5xx ou falha isolada de `/flowData` não demonstram perda do rádio. O watchdog agora exige falhas consecutivas de transporte e 15 s sem resposta HTTP de nenhum endpoint. Antes de desconectar, revalida sob mutex a ausência de OTA, a preferência de reconexão, a associação e a idade da última resposta. Os contadores saturam em vez de transbordar.
3. **Supressão com estado online antigo:** o Hub agora verifica também a idade real da telemetria, pois `flowmeterCommOn` pode ainda estar verdadeiro antes da atualização do quadro agregado. Uma pendência idêntica continua preservada.

Versões identificadas para esta revisão: **Fluxômetro v12.1** e **Hub 10.9.1-dev**. Atualizar também o **OpenTEC-Hub para Windows** para aplicar os comandos por atuador e o painel de dados brutos. Nenhum outro firmware é necessário para estas alterações. O `Config.h` atual seleciona **ModuloTECNAL_2**; o binário gerado do Hub corresponde a esse módulo. Para outro módulo, recompilar com o seletor correto antes de gravar.

Verificação adicional: **17/17 testes do fluxômetro**, incluindo execução das tarefas reais com falhas HTTP e disputa simulada do mutex; **151/151 contratos do Hub**. Os **203 testes focados do aplicativo** passaram na implementação anterior, cujo código não foi alterado nesta revisão. Firmware e interface continuam sem qualificação física. O catálogo do aplicativo pode sinalizar v12.1 como versão ainda não validada em bancada; essa revisão não promove qualificação física.

Os números e descrições abaixo registram a primeira implementação; as regras e versões deste adendo a substituem nos pontos revisados.

Compilação final aprovada com ESP32 core 3.3.12: fluxômetro v12.1 usa 1.150.943 bytes de programa; Hub 10.9.1-dev usa 1.202.253 bytes. Binários exportados em `External-Devices/fluxometro/build/v12.1/flowmeter.ino.bin` e `ESP32S3-HUB/build/10.9.1-dev/ESP32S3-HUB.ino.bin` (ModuloTECNAL_2). Nenhuma gravação foi executada.

## Evidência do cultivo

Origem: `D:\OneDrive\PosDoc_Fapesp\_Cultivos Rhizopus oryzae\Cultivo_06-10-26-A. oryzae`, pastas `Sessoes` e `Logs`. O snapshot reproduzível em [evidence/2026-10-12-flowmeter-log-snapshot.json](evidence/2026-10-12-flowmeter-log-snapshot.json) contém os tamanhos e SHA-256 dos arquivos lidos e os intervalos completos. `tools/analyze_flow_link.py` lê os arquivos sem modificar o cultivo.

| Sessão | Linhas | Linhas com vazão negativa | Episódios | Mediana da duração amostrada | Maior duração amostrada |
|---|---:|---:|---:|---:|---:|
| Ensaio_2026-10-06_0939.txt | 4.382 | 148 | 28 | 2,4 s | 158,4 s |
| Ensaio_2026-10-07_1220.txt | 217.080 | 2.633 | 630 | 3,6 s | 296,4 s |

Um episódio começa na primeira amostra com `Flowmeter < 0` e termina na primeira amostra válida seguinte. A duração tem resolução da amostragem e não inclui o intervalo já transcorrido antes do primeiro valor negativo. Vazão negativa pode representar ausência de telemetria ou leitura inválida/falha de hardware: a sessão não grava `wifi_status`, RSSI, códigos de erro HTTP, `hw_status` ou `boot_id`. Portanto, os 630 episódios não são 630 perdas de associação Wi-Fi demonstradas, nem prova de interrupção física do gás.

Há tráfego redundante documentado. Em 08/10 foram enviados 41.508 comandos com vazão; 40.282 mantinham o mesmo degrau de 0,05 L/min do comando anterior (comparação retrospectiva, sem substituir as configurações vigentes naquele dia). No log de 12/10, 485 de 595 comandos mantinham exatamente a vazão anterior. A imagem do operador mostra rotação mudando de 221 para 222 rpm com vazão fixa em 1,9 L/min e novo `cmd_id` de gás.

Os arquivos de aplicação não contêm o diagnóstico HTTP/radio detalhado necessário para atribuir causalidade a cada ausência. O volume de comandos caiu muito nos logs mais recentes, e o fluxômetro continua consultando/publicando HTTP independentemente desses comandos. Tráfego redundante é um contribuinte plausível para carga; não é causa única confirmada das ausências.

## Achados e correções

### P1 — Recuperação de telemetria podia produzir a própria indicação de offline

Em `External-Devices/fluxometro/firmware/flowmeter/src/tasks/TaskRuntime.h`, ambas as tarefas liam/escreviam `g_hubFailStreak`. Uma falha de `/flowCommand` atrasava também `/flowData`; um sucesso de consulta podia limpar o contador mesmo se a telemetria continuasse falhando. O recuo da telemetria atingia efetivamente **8 segundos** (`500 ms × 2^4`), embora o teto declarado fosse 15 s. O Hub usa `FLOWMETER_TIMEOUT = 6000` em `src/core/AppContext.h` e declara ausência em `src/sensor/Telemetry.h`. Esse desencontro é demonstrável no código e compatível com ausências breves após falhas transitórias.

Correção: contadores independentes, locais a cada tarefa; cópias atômicas somente para diagnóstico. Recuo programado da telemetria limitado a 1 s; consulta de comandos mantém seu recuo independente. O watchdog de reassociação depende de falhas consecutivas de telemetria, que sucessos da consulta não apagam. A duração real ainda inclui HTTP, disputa do mutex e rádio; 1 s não é garantia de entrega física.

### P1 — Anúncio de presença falho podia repetir sem intervalo

Quando `hubAnnounced` era falso, `/nodeHello` podia ser tentado a cada volta da tarefa, sem respeitar os 30 s. Um Hub inacessível recebia mais tentativas justamente durante recuperação, e a tarefa gastava tempo antes de publicar dados.

Correção: anúncios não confirmados respeitam 5 s entre tentativas; confirmados mantêm 30 s; anúncio só começa quando a telemetria não está vencida. Esperas pelo mutex HTTP foram reduzidas de 2 s para 100 ms, preservando exclusão mútua. Falhas HTTP descartam o socket; respostas de telemetria são consumidas antes da reutilização.

### P2 — Mudança de RPM reenviava gás sem mudança de referência

`Windows_app/src/OpenTECHub/Services/Control/CascadeService.cs` comparava o JSON completo, emitindo novamente gás, válvulas e monitor de O₂ junto com qualquer mudança de RPM. `ESP32S3-HUB/ESP32S3-HUB/src/protocol/Mailboxes.h` gerava uma nova revisão para toda mensagem com campos de gás.

No fluxômetro, `CommandCodec.h` ignora retransmissões com o mesmo `cmd_id`; revisões novas executam o comando. Repetir um setpoint positivo atribui novamente o mesmo alvo e escreve os mesmos níveis das válvulas: não cria, por si, novo alvo de vazão nem zera o integral. Atualiza metadados/ACK e gasta processamento. Repetir zero pode executar novamente o caminho de corte/zeragem. A malha PI segue ajustando o DAC a cada 100 ms mesmo sem novos comandos: isso é controle local normal, não dependência de reenviar setpoints.

Correção no aplicativo: comparar separadamente gás, RPM e monitor de oxigênio. Gás inclui vazão e três válvulas como grupo atômico; qualquer alteração de vazão após o passo configurado, de rota ou de corte envia o grupo inteiro. Só RPM alterado envia apenas RPM; só alvo de O₂ alterado envia apenas seu monitor. Engajamento, retorno do enlace e retomada após suspensão forçam atualização completa. Mantidos quantização, limites configurados e controlador.

Correção no Hub: comando operacional idêntico preserva a revisão pendente e suas configurações, sem criar nova fila. Primeira comunicação, nova rota/vazão/corte, configuração explícita e tentativa para nó offline continuam aceitos. ACK e recuperação de reboot permanecem na máquina existente.

Correção no fluxômetro: consultas de comandos de 4 para 2 Hz; telemetria permanece em 2 Hz. Carga nominal cai de 6 para 4 requisições HTTP/s, excluindo anúncios, retries e outros dispositivos. A latência nominal de descoberta de um novo comando passa de até 250 para até 500 ms, mais HTTP e escalonamento. A malha PI local permanece em 10 Hz. `/diag` e `/status` preservam `hub_fail_streak`, agora exclusivamente telemetria, e acrescentam `command_fail_streak`.

### P2 — Dados brutos não selecionáveis e rolagem por mensagem

Em `Windows_app/src/OpenTECHub/Views/EventsView.xaml`, mensagens eram `TextBlock` em `ListBox` com rolagem lógica por item. Uma mensagem longa ultrapassava o painel, e a roda pulava para outra mensagem.

Correção: `TextBox` somente leitura, fonte monoespaçada de 10 DIP, quebra de linha e seleção/cópia por Ctrl+C ou menu de contexto. Ctrl+A dentro de uma mensagem seleciona seu conteúdo inteiro. Rolagem contínua em pixels permite percorrer uma mensagem longa. O botão Pausar existente estabiliza os dados durante inspeção/cópia; permanecem no máximo 100 mensagens.

## Validação

- Aplicativo: **203/203** testes focados de cascata, WP7, nomes/tema e interação do painel passaram. Regressões exercitam RPM sem gás, grupo de gás completo, reenvio após reconexão e seleção integral/rolagem de 80 pixels dentro de uma mensagem longa. Captura visual inspecionada.
- Fluxômetro: **13/13** testes passaram, incluindo cinco cenários que compilam e executam as funções reais de comunicação/mailbox com relógio e HTTP simulados: falha só de comandos, falha persistente de telemetria, falha de anúncio, recuperação transitória e deduplicação preservando pendências/configurações/retry offline.
- Hub: **151/151** contratos existentes passaram.
- Firmwares Hub e fluxômetro compilaram com ESP32 core **3.3.12**; Async TCP **3.5.0** e ESP Async WebServer **3.12.1**. Hub: 1.202.249 bytes de programa; fluxômetro: 1.150.455 bytes. As versões antigas citadas em parte da documentação não substituem esse resultado da toolchain instalada.
- Aplicativo compilou; advertência preexistente CS0108 de `PumpPlanChart.Style`. `git diff --check` passou.

## Aplicação e confirmação física

Entrega preparada para commit/push, publicação Windows Release win-x64 self-contained e instalador Inno Setup. As versões finais são fluxômetro v12.1 e Hub 10.9.1-dev (ModuloTECNAL_2), conforme o adendo. Nenhuma placa foi gravada, nem houve validação de bancada.

A revisão da fila do Windows confirmou que falhas de transporte recolocam o comando na fila e que valores novos prevalecem na combinação dos setpoints pendentes. A redução de tráfego foi implementada na origem da cascata, preservando os comandos sequenciados, as prioridades de segurança e as tentativas de recuperação. O catálogo de versões validadas em bancada permanece sem promoção de v12.1.

A exclusão recursiva de bin/obj foi bloqueada pela política automática de execução. Foi usada a limpeza padrão `dotnet clean` para Debug e Release; arquivos extras podem permanecer. A publicação destinada ao instalador será feita em uma pasta nova sob `release-assets`, evitando empacotar resíduos de publicações anteriores. O instalador deve ser compilado após o commit para incorporar seu identificador real.

Para confirmar a redução das ausências após aplicar o aplicativo e ambos os firmwares, registrar a mesma sessão com diagnósticos HTTP/rádio e `hw_status`/`boot_id`. Comparar quantidade e duração de ausências em janelas equivalentes. Capturar `/diag` no IP do nó e sua serial permite distinguir consulta falha, telemetria falha e associação Wi-Fi; uma mudança de `boot_id` permite identificar reboot. Também verificar interrupção real do Hub, retorno da referência e comportamento das válvulas. A correção elimina os mecanismos de software identificados, mas não comprova resolução de interferência, alimentação ou defeito físico.
