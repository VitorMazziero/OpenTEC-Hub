# Firmware do TECNAL Hub ESP32-S3

Este repositório contém somente o firmware do Hub central ESP32-S3. O firmware do nó externo ASDA-B2 permanece no projeto de potência e não faz parte deste repositório.

## Diretórios

- `ESP32S3-HUB/`: firmware ativo e modularizado. O banner atual é
  `10.7.0-dev`, protocolo 10. A versão 10.7 restringe o PI do banho ao ajuste fino (estado
  `approaching` longe da referência). A 10.6 corrigiu o caminho de comando do banho
  (prioridade stop > operação > setpoint, falhas por borda, posse do Hub) e exige o nó r3.2;
  a via de controle ainda requer validação física.
- `_old/`: snapshots históricos v1-v8, preservados sem edição.
- `docs/`: arquitetura, contrato HTTP e critérios de validação.
- `tests/contracts/`: verificações executáveis do contrato preservado.
- `tools/`: scripts de compilação e verificação.

## Gravação por Wi-Fi (10.7)

Com o 10.7 gravado uma vez por USB, as próximas versões podem ir pela rede do próprio módulo
(`ModuloTECNAL_1`/`_2`, Hub em `192.168.4.1`). Com o PC nessa rede:

```powershell
.\tools\ota_upload.ps1
```

O script compila, exporta `build\ota\ESP32S3-HUB.ino.bin` e envia para `POST /update`.
Também dá para abrir `http://192.168.4.1/update` no navegador (PC ou celular) e enviar o
`.ino.bin` exportado pela Arduino IDE (*Sketch > Export Compiled Binary*). A imagem só troca
depois de verificada; envio interrompido ou recusado mantém o firmware atual. O Hub recusa o
envio enquanto comanda um processo (cascata do banho com setpoint do reator, motor com rotação)
e aceita só o arquivo `ESP32S3-HUB*.bin` do app (não `merged`, `bootloader` nem `partitions`).
Exige o esquema de partições padrão (duas partições de app de 1,25 MB); a gravação por USB
continua igual.

## Regra de segurança

Compilação e testes de contrato não tornam o firmware pronto para campo. A validação física descrita em `docs/VALIDATION.md` é obrigatória. Para a rotação, o driver deve ser gravado antes do Hub e o caminho direto P1-09 precisa passar pelos testes de perda.
