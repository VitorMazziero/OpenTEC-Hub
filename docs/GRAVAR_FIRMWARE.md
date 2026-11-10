# Gravar firmware nas placas

Use o **Atualizador** (`OpenTEC-Updater.zip`, em [DOWNLOADS.md](../DOWNLOADS.md)). Ele baixa a release, confere o SHA-256 e o manifesto de cada imagem e confere a placa (tipo, MAC e, no Hub, o módulo) antes de gravar.

## Antes de gravar

- Parar o processo do biorreator e anotar calibrações e configurações do equipamento.
- No Hub, saber o **módulo** (1 ou 2): a imagem de um módulo não serve para o outro. Nunca renomear um arquivo de um módulo para o outro.
- Os seis equipamentos usam a mesma imagem nos dois módulos.

## Instalar o Atualizador

1. Instalar o Python 3.12 para Windows (marcar "Add Python to PATH").
2. Extrair `OpenTEC-Updater.zip` numa pasta fora de OneDrive, Dropbox ou similares.
3. Executar `Iniciar-Atualizador.bat`. Na primeira vez ele instala as dependências (`requirements.txt`).

## Gravar por Wi-Fi (OTA), o caminho normal

1. Ligar o Hub e o equipamento.
2. Conectar o computador à rede do Hub (`ModuloTECNAL_1` ou `ModuloTECNAL_2`).
3. No Atualizador, aba de releases: **Consultar releases online**, escolher a versão e baixar as imagens. Baixar não grava nada.
4. Na tabela de dispositivos: **Consultar nós pelo Hub**, conferir a versão instalada, selecionar o equipamento e enviar. O Atualizador confirma o reinício, a versão e o SHA da imagem em execução.

Se o resultado vier como **indeterminado** (comum no Hub, que reinicia durante a confirmação), não reenviar: consultar a versão de novo e só então decidir.

## Gravar por USB

Só para a primeira gravação de uma placa, ou para recuperar uma placa que não sobe. Usa o bootloader e a tabela de partições da release, além da aplicação.

- Requer o `esptool` do core ESP32 da Espressif instalado no computador (no Atualizador: **Preparar compilador neste PC**).
- Selecionar a porta em **Consultar portas USB**.
- Placas com conversor CH340 às vezes não entram no modo de gravação sozinhas: segurar o botão BOOT ao iniciar a gravação.

## O que não fazer

- Não gravar um arquivo que não veio de uma release deste repositório.
- Não interromper uma gravação USB. Se cair, repetir pela USB.
