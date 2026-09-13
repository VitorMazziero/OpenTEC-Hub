# Validação — Frasco agitador

## Automatizada concluída

- compilação do firmware ativo;
- verificação de preservação do manifesto SHA-256;
- verificação estática de rotas e campos críticos contra o Hub;
- regressões estáticas de boot freado, knob bloqueado, inversão por zero e identidade única v10;
- baseline monolítico mantido para auditoria.

## Bancada pendente

1. Gravar a placa correta e capturar versão/toolchain.
2. Confirmar boot sem pulso de PWM, knob bloqueado, reconexão e ausência de reset cíclico.
3. Comparar telemetria e comandos com o baseline.
4. Testar perda de pacote, reinício do nó e reinício do Hub.
5. Validar rampa de inversão, sentidos, duty × rotação e estado seguro do atuador.
6. Executar soak test e arquivar logs em `tests/evidence`.

O potenciômetro só disputa autoridade após `ActivePot:1`. Parada segura, boot freado e inversão por zero ainda precisam de teste físico.
