# Instalar os aplicativos

Arquivos em [DOWNLOADS.md](../DOWNLOADS.md). Antes de instalar, confira o SHA-256 com o `SHA256SUMS.txt` da versão.

## TECNAL-Hub para Windows (aplicativo principal)

Windows 10 ou 11, 64 bits.

1. Baixe e execute `OpenTECHub_Setup.exe`. O aplicativo traz o próprio .NET; não é preciso instalar mais nada.
2. Guarde a pasta de dados (receitas, calibrações, resultados) **fora** de pastas sincronizadas como OneDrive, que travam arquivos abertos.
3. Antes de atualizar, faça uma cópia da pasta de dados.

O aplicativo fala com o Hub pela USB ou pela rede Wi-Fi do Hub (`ModuloTECNAL_1` ou `ModuloTECNAL_2`).

## TECNAL-Hub para Android

Instale `OpenTEC-Hub_Android.apk` (permita "instalar apps desconhecidos" para o navegador ou o gerenciador de arquivos). Desinstalar o app apaga os dados dele: exporte o que precisar antes de trocar de versão.

## Apps diretos de manutenção (Android)

`Fluxometro_Android.apk`, `Bomba_Android.apk` e `Banho_Android.apk` falam com o equipamento pela rede Wi-Fi própria dele, sem passar pelo Hub. Servem para manutenção e diagnóstico; a operação normal é pelo aplicativo do Hub.

## Estação do sensor de biomassa (Windows)

Execute `Biomassa_Windows_Setup.exe`. Serve para calibrar e adquirir dados diretamente do sensor de biomassa.

## Atualizador de firmware

Veja [Gravar firmware nas placas](GRAVAR_FIRMWARE.md).
