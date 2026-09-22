# Changelog do Hub

## 10.5.1-dev — auditoria adicional banho/Hub

- falhas do nó e timeout de execução de 300 s ficam travados até reset explícito;
- conclusão usa o par `ack_cmd_id`/`done` uma única vez, sem renovar cooldown em pushes repetidos;
- `/bathData` exige nó r3.1 previamente registrado no mesmo IP e enums conhecidos;
- novo comando de mesmo valor rearma a UART após troca de via e `resetVariables` envia `100B`;
- telemetria expõe IDs, pendência e idade da conclusão; reserva JSON elevada para 3584 bytes;
- aritmética de idade de `/nodes` permanece correta no rollover de `millis()`.

## 10.5.0-dev — cascata térmica externa

- adiciona `bath`/r3, `bathBox`, `/bathData` e `/bathCommand`;
- torna `Tempval` uma amostra real, válida e temporal do reator;
- adiciona PI puro, limites, slew, anti-windup, troca break-before-make e cooldown após `done`;
- publica diagnóstico completo da cascata com `null` para valores inválidos;
- persiste somente configuração/rota/comunicação e nunca retoma atuação após reboot;
- mantém a versão do protocolo HTTP em 10 por compatibilidade aditiva.

Compilação e contratos não equivalem à aprovação de bancada, sintonia ou liberação para cultivo.
