# Firmware do TECNAL Hub ESP32-S3

Este repositório contém somente o firmware do Hub central ESP32-S3. O firmware do nó externo ASDA-B2 permanece no projeto de potência e não faz parte deste repositório.

## Diretórios

- `ESP32S3-HUB/`: firmware ativo e modularizado. O banner atual é
  `10.6.0-dev`, protocolo 10. A versão 10.6 corrige o caminho de comando do banho
  (prioridade stop > operação > setpoint, falhas por borda, posse do Hub) e exige o nó r3.2;
  a via de controle ainda requer validação física.
- `_old/`: snapshots históricos v1-v8, preservados sem edição.
- `docs/`: arquitetura, contrato HTTP e critérios de validação.
- `tests/contracts/`: verificações executáveis do contrato preservado.
- `tools/`: scripts de compilação e verificação.

## Regra de segurança

Compilação e testes de contrato não tornam o firmware pronto para campo. A validação física descrita em `docs/VALIDATION.md` é obrigatória. Para a rotação, o driver deve ser gravado antes do Hub e o caminho direto P1-09 precisa passar pelos testes de perda.
