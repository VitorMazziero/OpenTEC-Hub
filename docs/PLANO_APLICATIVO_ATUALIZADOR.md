# Plano — Atualizador unificado OpenTEC-Hub

Data: 12/10/2026. Estado: planejamento; implementação ainda não iniciada. Base inspecionada: commit `5225299`. Este documento propõe evolução do atualizador existente e não autoriza gravação de equipamentos.

## 1. Objetivo e decisão proposta

Oferecer um aplicativo Windows para selecionar equipamentos, identificar versões, compilar fontes locais, escolher imagens, gravar por Wi-Fi/USB e acompanhar a confirmação após reinício. Incluir também limpeza/publicação Release e geração do instalador ISS do OpenTEC-Hub.

**Recomendação:** ampliar o aplicativo Python/PySide6 existente em `External-Devices/tools/updater_app`, empacotando-o como executável independente. Reaproveitar descoberta, transporte OTA, execução em segundo plano e testes; separar o catálogo e os serviços do diretório de dispositivos externos quando o suporte ao Hub/servo entrar. Manter um ponto de entrada compatível no caminho antigo durante a migração.

O atualizador será uma ferramenta separada do aplicativo que controla o cultivo. O OpenTEC-Hub poderá oferecer um botão para abri-lo, sem hospedar a gravação dentro do processo que será substituído. Essa integração é uma etapa posterior, com estado de manutenção explícito.

Primeira entrega: uso local e offline com fontes ou imagens previamente disponíveis. Consulta e download de releases no GitHub entram depois da validação do fluxo local. Aplicativos Android/Flutter dos equipamentos ficam como extensão futura; os oito firmwares e o aplicativo Windows constituem o escopo inicial.

## 2. O que já existe e o que falta

| Área | Evidência atual | Trabalho necessário |
|---|---|---|
| Interface | PySide6; tabela dos seis nós, seleção, endereço manual, logs e progresso | Incluir Hub, servo e Windows; conservar resultado por linha ao terminar |
| Ações | Compilar, enviar e compilar/enviar, com fila em segundo plano | Preparar todos os artefatos antes de confirmar o lote; diferenciar gravação e confirmação |
| Descoberta | Leitura de `/nodes`; AP padrão e IP manual | Perfis dos módulos 1/2 e identificação direta do equipamento; selecionar a rede sem fallback silencioso |
| Imagens | `firmware.py` procura `.bin` e ordena pela data de modificação | Vincular cada trabalho a arquivo, tamanho, hash e metadados fixos; respeitar o padrão do dispositivo |
| Versões | Compara versão do fonte com a reportada pelo Hub | Mostrar separadamente instalada, declarada no fonte e vinculada ao binário; versão do fonte não identifica binário antigo |
| Confirmação | `jobs.py` encerra com sucesso após resposta OTA; nova leitura é manual | Aguardar reinício, identidade e contato novo antes de marcar atualização confirmada |
| Portabilidade | Caminhos do projeto derivados do arquivo; bibliotecas na GUI com padrão fixo em `C:` | Configuração por computador; detecção de ferramentas e bibliotecas; suporte a caminhos diferentes do OneDrive |
| Hub/servo | Scripts específicos do Hub; servo compilável/gravável por Arduino CLI | Adaptadores para OTA do Hub e USB do servo; conferir módulo-alvo e compatibilidade |
| Windows | `Build-Release.ps1`, `build_installer.ps1` e ISS existentes | Ações na interface, saída clara e atualização do aplicativo instalado fora de sua execução |
| Distribuição | `Build-ReleaseAssets.ps1` compacta fontes de firmware e copia apps | Pacotes próprios de atualização com binários e manifesto; ZIP de fonte não é imagem OTA |

Não foi executada a suíte existente nesta etapa de planejamento. A evidência acima é leitura do código, não qualificação física.

## 3. Catálogo dos alvos

| Alvo | Via inicial | Endereço/rede direta | Particularidade |
|---|---|---|---|
| Hub | OTA Wi-Fi; USB para recuperação | `ModuloTECNAL_1` ou `ModuloTECNAL_2`, `192.168.4.1` | Imagem associada ao módulo selecionado em `Config.h`; respeitar recusas de processo ativo |
| Fluxômetro | OTA Wi-Fi | `Floxometro_AP`, `192.168.10.1` | Início do upload fecha gás e zera referência |
| Bomba | OTA Wi-Fi | `FeedPump`, `192.168.6.1` | Identificar estado e conferir preservação da calibração |
| Frasco agitador | OTA Wi-Fi | `MotorBoeco`, `192.168.4.1` | Mesmo IP do AP do Hub; endereço sozinho não identifica o alvo |
| Biomassa | OTA Wi-Fi | `BiomassSensor`, `192.168.7.1` | Preservar configuração/calibração suportada pelo firmware |
| Distância | OTA Wi-Fi | `Distance Sensor`, `192.168.5.1` | Normalizar nome do diretório do Hub e nome de diagnóstico |
| Banho | OTA Wi-Fi | `Banho Termostatico`, `192.168.8.1` | Preservar o fluxo direto pelo Wi-Fi do banho; verificar estado de controle |
| Servo ASDA-B2 | USB | Porta COM escolhida pelo operador | Firmware atual não oferece OTA; conferir `MODULO_TECNAL_ALVO` |
| OpenTEC-Hub Windows | Publicação e instalador local | Pasta do projeto ou instalação selecionada | Salvar/encerrar sessão pelo aplicativo; não encerrar processo à força |

Pelo Wi-Fi do Hub, consultar `/nodes` para descobrir os IPs dos nós e comunicar diretamente com eles. Isso não pressupõe um proxy de upload no Hub. Pelo AP próprio, consultar o nó selecionado; o operador troca de rede quando necessário. Não prometer atualização simultânea de vários APs com um único adaptador Wi-Fi.

Na primeira versão, a conexão à rede é feita pelo operador, com nome da rede esperada visível. Troca automática de Wi-Fi será opcional e posterior; não deve mudar a rede durante upload.

## 4. Fluxo da interface

1. **Abrir:** detectar a raiz do projeto pelo local do executável ou permitir escolher a pasta. Restaurar configurações desta máquina sem depender da unidade `C:`/`D:`.
2. **Escolher origem:** código local para compilar; pacote de atualização; ou imagem existente. Atualizar o repositório será uma ação separada, sem executar `git pull` implicitamente.
3. **Encontrar equipamentos:** selecionar módulo 1/2 e conexão pelo Hub, AP direto ou USB. Mostrar nome, endereço/COM, identidade, versão, idade do contato e possibilidade de atualização.
4. **Selecionar ação:** “Compilar”, “Enviar imagem” ou “Compilar e enviar”. Para Windows: “Limpar e gerar Release”, “Gerar instalador” e, posteriormente, “Instalar atualização”.
5. **Preparar:** compilar todos os itens solicitados, validar arquivos e compatibilidade, resolver ferramentas e fixar o plano. Uma compilação falha não usa silenciosamente um binário anterior.
6. **Revisar envio:** apresentar equipamento físico, módulo, versão instalada, versão candidata quando conhecida, arquivo, conexão e efeito da atualização. Só então confirmar a gravação do lote.
7. **Executar:** uma gravação por vez, com progresso real de bytes, mensagens copiáveis, resultado por item e orientação de conexão. Pausar nos itens que exigem trocar de rede ou conectar USB.
8. **Confirmar:** distinguir imagem transferida, aceita pelo firmware, reinício aguardado e versão confirmada em contato novo. Ao final, oferecer relatório e repetição apenas dos itens pendentes.

Tabela principal: **Selecionar | Equipamento | Módulo | Conexão | Versão instalada | Versão candidata | Situação | Ação**. IP, hash, compilador, arquivos e protocolo ficam nos detalhes, sem sobrecarregar a tela principal. Diferenciar “não encontrado”, “versão desconhecida” e “não compatível”.

Não colorir igualdade entre fonte e equipamento como prova de que o arquivo selecionado corresponde a eles. Não rotular compilação como atualização instalada. O botão “Atualizar selecionados” deve montar uma fila, não disparar envios paralelos.

## 5. Arquitetura proposta

| Componente | Responsabilidade |
|---|---|
| Catálogo compartilhado | Chaves, aliases de identidade, placa, módulo, transporte, caminhos relativos, padrão de binário e recursos de cada alvo |
| Perfis locais | Raiz do projeto, Arduino CLI, bibliotecas, .NET, Inno Setup, redes, módulo e portas; fora da configuração do cultivo |
| Preparação de artefatos | Compilar, capturar saída, conferir resultado e gerar manifesto; não escolher arquivo por data como decisão de gravação |
| Descoberta e identidade | `/nodes`, diagnóstico direto, telemetria do Hub e identificação USB disponível; registrar limitações de versões antigas |
| Planejador | Validar dependências entre versões, ordenar o lote e indicar mudanças de rede/USB |
| Executor | Máquina de estados persistida, exclusão entre operações, timeouts por etapa e progresso |
| Adaptadores | OTA dos nós, OTA do Hub, USB Arduino CLI e publicação/instalação Windows |
| Verificador | Conferir retorno após reinício, identidade e versão reportada; registrar evidência disponível |
| Relatórios | Histórico por execução, logs técnicos e resumo exportável, com caminhos e hashes |
| Interface | Tabela, detalhes, ações e recuperação; tarefas lentas fora da thread da janela |

Local proposto para o aplicativo unificado: `tools/updater_app/`. Caminho antigo continua como lançador durante a transição. O catálogo deve ter uma fonte de verdade consumida pela GUI e pelas ferramentas de linha de comando; a migração deve preservar os parâmetros públicos dos scripts atuais.

Preferir APIs de processo/HTTP/serial estruturadas, com argumentos separados. Reutilizar `Build-Release.ps1` e `build_installer.ps1` para Windows. Não montar comandos de terminal por concatenação de caminhos digitados pelo operador.

## 6. Manifesto e vínculo entre imagem e equipamento

Cada artefato preparado deve registrar: versão do esquema; chave do alvo; módulo quando aplicável; placa/FQBN; versão de firmware declarada; versão de protocolo; commit de origem; alterações locais quando houver; versões da toolchain/bibliotecas; caminho relativo; tipo de imagem; tamanho; SHA-256; restrições de compatibilidade; e situação de validação em software/bancada. Registrar o esquema de armazenamento/calibração quando a versão o declarar.

O trabalho usa uma cópia local fixa do artefato e revalida seu hash antes da gravação. A pasta do código não é fonte de versão para um binário importado. Uma imagem sem manifesto pode entrar por seleção manual, com versão candidata desconhecida e identificação do alvo exigida; nome e extensão sozinhos não comprovam compatibilidade.

Recusar imagens `merged`, `bootloader` e `partitions` no caminho OTA. USB usa o conjunto de arquivos exportado para a placa, sem reutilizar arbitrariamente o arquivo OTA como pacote completo. Verificar tamanho/partição quando esses dados forem disponíveis.

O mesmo número de versão depois da gravação não comprova o hash executado. Usar contato novo e identificador de build/boot quando houver; se o firmware não reportar hash/commit, apresentar somente “versão reportada confirmada”, com essa limitação no relatório. Não declarar verificação byte a byte do firmware em execução sem suporte do dispositivo.

Pacotes baixados futuramente devem ter origem confiável e manifesto autenticado; SHA-256 detecta alteração do arquivo, mas não autentica sozinho o publicador. A etapa online depende de uma definição explícita da publicação desses pacotes.

## 7. Rede, estado do processo e ordem de atualização

- Conferir identidade imediatamente antes do upload, inclusive após troca de Wi-Fi/IP. `/update` respondendo não basta: Hub e agitador compartilham `192.168.4.1`, e nomes no diagnóstico podem diferir das chaves de `/nodes`.
- Fazer leitura somente dos estados de controle disponíveis; respeitar recusas do firmware. Processo Windows fechado não demonstra que os atuadores estão parados. Se não houver estado verificável, exigir preparação de manutenção identificada como manual, sem afirmar que o software comprovou parada.
- Não enviar comandos automáticos para zerar referências, desengajar cascatas ou contornar a recusa OTA. A parada do processo é uma ação do operador no aplicativo de controle.
- Serializar gravações. No modo pela rede do Hub, atualizar nós antes do Hub por padrão para manter a descoberta; incompatibilidades declaradas prevalecem sobre essa ordem e podem exigir versão intermediária.
- Servo e Hub que participam de uma mudança de protocolo precisam de ordem explícita no pacote; preservar a orientação atual de servo antes do Hub na migração correspondente. Não assumir que essa regra vale para todo pacote futuro.
- Dependência falha bloqueia seus dependentes. Por padrão, pausar o lote em falha ou identidade incerta, permitindo continuar apenas itens comprovadamente independentes. Não repetir flash automaticamente após timeout com resultado desconhecido: verificar o dispositivo antes.
- Manter uploads sem consultas concorrentes ao mesmo nó; retomada usa registros para reverificar o estado real, não reenvia a fila antiga sem inspeção.

## 8. Estados, falhas e recuperação

Estados mínimos: **Na fila → Preparando → Pronto para revisão → Validando destino → Enviando → Aceito → Aguardando reinício → Confirmado**. Estados alternativos: **Aguardando rede/USB, Bloqueado, Falhou, Cancelado, Resultado desconhecido, Dependência pendente**.

Na compilação: tempo transcorrido, último log, atividade conhecida, botão de cancelamento e limite configurável. Silêncio do compilador não representa progresso nem falha por si só; timeout nunca autoriza usar imagem antiga. Encerrar somente o processo/subprocessos iniciados pelo atualizador.

Na gravação: porcentagem representa bytes transmitidos, não aceitação nem reinício. “Parar após este dispositivo” é a ação normal para interromper o lote; abortar upload em curso deve ser uma ação explícita, conforme a recuperação suportada pelo firmware. Não retomar uma transferência OTA parcial como se fosse um download.

Após timeout/reinício não observado: manter resultado desconhecido e oferecer reconectar, consultar versão e orientação USB. Não garantir rollback automático: ele depende do firmware/bootloader e não está demonstrado universalmente. Reinstalar uma imagem anterior é uma operação manual compatível com protocolo e dados persistidos.

Calibração/configuração: preservar pelos caminhos normais de atualização sem apagar flash. Quando houver exportação suportada, oferecer cópia antes/depois; quando não houver, declarar indisponibilidade. Uma imagem anterior não restaura automaticamente NVS alterada por uma migração. Mudança incompatível de esquema exige procedimento e ensaio próprios.

## 9. Windows: build, ISS e atualização instalada

Separar três operações visíveis:

1. **Limpar e gerar Release:** verificar aplicativo fechado; remover apenas `bin/obj` dos projetos .NET permitidos, validar caminhos dentro da raiz e executar publicação `Release win-x64 self-contained`. Detectar falhas/arquivos bloqueados e não prosseguir com limpeza parcial silenciosa. Não apagar firmwares, instaladores anteriores ou dados do cultivo.
2. **Gerar instalador:** usar a publicação escolhida, conferir DLL/versão/commit e executar o ISS existente. Falta de Inno Setup deve produzir falha clara, sem anunciar instalador gerado. Registrar caminho e hash do EXE.
3. **Instalar atualização:** preparar pacote fora da pasta em uso, solicitar salvamento e encerramento normal da aplicação; executar instalador separado e verificar versão instalada. Não fechar cultivo à força, não iniciar controles/receitas automaticamente e não substituir arquivos em uso. Manter dados e configuração do operador.

O executável do atualizador deve funcionar sem Python instalado no computador de operação; Arduino CLI, .NET SDK e Inno Setup são necessários apenas para as respectivas compilações. Enviar pacotes prontos não exige SDK de compilação. Detectar e mostrar ferramentas ausentes somente nas ações que dependem delas.

## 10. Etapas e critérios de conclusão

| Etapa | Entrega | Critério para avançar |
|---|---|---|
| P0 — Baseline | Executar testes atuais; documentar identidade/endpoints, imagem e estado por dispositivo; registrar limites da primeira versão | Matriz dos oito firmwares e Windows conferida no código; falhas existentes registradas |
| P1 — Catálogo e portabilidade | Catálogo compartilhado, perfis locais, seleção da raiz e ferramentas, migração compatível | Mesmas ações em raiz de desktop/notebook e caminho com espaços; nenhuma unidade/usuário obrigatório |
| P2 — Preparação | Artefatos fixos, manifesto e fases compilar/enviar separadas; confirmação depois da compilação | Binário errado/antigo/alterado, módulo incompatível e compilação falha não produzem envio |
| P3 — Rede e OTA | Hub + seis nós; identificação; modo Hub/AP; preflight e verificação após reinício | Servidores simulados cobrem identidade trocada, resposta de erro, desconexão, reboot e confirmação sem cache |
| P4 — USB e planejamento | Servo e recuperação USB dos ESP32; portas; dependências e fila persistida | Placa/porta errada, desconexão e falha anterior bloqueiam o fluxo correto; nenhuma promessa de OTA no servo |
| P5 — Windows | Limpeza/Release/ISS e instalação separada; recibos dos binários | App ativo/arquivos bloqueados/SDK ausente/ISCC falho não geram sucesso; instalação de teste preserva dados |
| P6 — Distribuição | Executável independente, instalador/ZIP, guia e relatório exportável | Abre em PC de teste sem Python; atualiza pacote local offline; ferramentas opcionais identificadas corretamente |
| P7 — Bancada | Executar matriz física e registrar evidências por dispositivo/módulo | Reboot, versão, retorno de comunicação e persistência confirmados; interrupção e recuperação USB ensaiadas |
| P8 — Releases online | Manifestos publicados, seleção de versão, download/cache e conferência da origem | Pacotes incompletos/incompatíveis/adulterados recusados; funciona offline após download; gravação nunca automática |

MVP funcional: P0–P3, cobrindo sete equipamentos por OTA. Cobertura completa dos oito firmwares, build Windows e distribuição local: P0–P7. Consulta online é incremento posterior. Cada etapa deve ter commit isolado e evidência de verificação; não chamar a cobertura de completa antes do USB do servo e dos ensaios físicos.

## 11. Matriz de validação

| Cenário | Resultado exigido |
|---|---|
| Só compilar | Nenhuma descoberta HTTP obrigatória ou gravação; artefato e manifesto identificados |
| Só enviar | Usa exatamente a imagem escolhida; nenhum build implícito |
| Compilar/enviar com build falho | Não envia binário anterior; dependentes permanecem bloqueados |
| Fonte mudou depois de compilar | Mostra diferença; não relabela o binário com a versão do novo fonte |
| Hub e agitador em `192.168.4.1` | Equipamento diferente do selecionado bloqueia o envio |
| Módulo 1/2 incompatível | Recusa imagem para o outro módulo antes de gravar |
| HTTP 200 com corpo de recusa | Não marca aceitação; interpreta o contrato do alvo |
| Bytes enviados e socket caiu | Resultado desconhecido até consulta nova; não repete flash sozinho |
| `/nodes` guarda versão antiga | Não conclui usando registro anterior ao upload/reboot |
| Mesma versão em duas builds | Não afirma hash executado sem evidência reportada pelo nó |
| Cancelar e reabrir atualizador | Histórico preservado; revalida equipamento antes de qualquer repetição |
| USB removido ou porta ocupada | Falha clara; não escolhe outra COM silenciosamente |
| Limpeza Windows | Alvos resolvidos e restritos a `bin/obj`; fontes, cultivo, imagens e instaladores preservados |
| Atualização Windows | Aplicativo fechado normalmente; versão instalada confere; dados preservados |
| Desktop/notebook | Raízes globais diferentes; mesmos comandos/ações relativos; configurações locais inválidas detectadas |
| Bancada por equipamento | Atualiza, reinicia, reporta versão, recupera comunicação e preserva configurações suportadas |
| Falha física durante OTA | Recuperação demonstrada no equipamento; não inferida apenas de testes de software |

Usar os testes existentes como baseline e adicionar somente verificações dos comportamentos acima. Sem mudanças no firmware para a primeira implementação, aproveitar os diagnósticos existentes; se um alvo não oferecer identificação suficiente, registrar bloqueio/limitação e planejar extensão específica antes de autorizar atualização automática desse alvo.

## 12. Arquivos de referência e próximo passo

- GUI atual: `External-Devices/tools/updater_app/README.md`, `updater/devices.py`, `compiler.py`, `jobs.py`, `firmware.py`, `ota.py`, `hub.py`, `mainwindow.py`.
- Nós externos: `External-Devices/tools/Publish-OtaFirmware.ps1`.
- Hub: `ESP32S3-HUB/tools/compile.ps1`, `tools/ota_upload.ps1`, `ESP32S3-HUB/Config.h`, `src/network/OtaUpdate.h`, `src/network/HttpServer.h`.
- Servo: `ESP32S3-SERVO/Software/firmware-producao/ASDA_B2_Servo_Node/`.
- Windows: `Windows_app/tools/Build-Release.ps1`, `installer/build_installer.ps1`, `installer/OpenTECHub_Setup.iss`.
- Distribuição: `tools/Build-ReleaseAssets.ps1`, `DOWNLOADS.md`.
- Operação: `docs/GUIA_COMPILAR_E_ATUALIZAR.md`.

Próximo passo de implementação: **P0**, seguido de **P1**. Este pedido entrega somente o plano; nenhum serviço de atualização, código de controle ou firmware foi alterado.
