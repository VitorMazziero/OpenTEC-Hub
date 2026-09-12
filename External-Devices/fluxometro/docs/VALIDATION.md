# Validação — Fluxômetro

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
6. Executar soak test e arquivar logs em `tests/evidence`.

O FQBN preserva opções da compilação importada. Calibração, válvulas, OTA e concorrência entre comandos Hub/diretos exigem bancada.
