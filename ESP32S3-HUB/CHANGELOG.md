# Changelog do Hub

## 10.5.0-dev — cascata térmica externa

- adiciona `bath`/r3, `bathBox`, `/bathData` e `/bathCommand`;
- torna `Tempval` uma amostra real, válida e temporal do reator;
- adiciona PI puro, limites, slew, anti-windup, troca break-before-make e cooldown após `done`;
- publica diagnóstico completo da cascata com `null` para valores inválidos;
- persiste somente configuração/rota/comunicação e nunca retoma atuação após reboot;
- mantém a versão do protocolo HTTP em 10 por compatibilidade aditiva.

Compilação e contratos não equivalem à aprovação de bancada, sintonia ou liberação para cultivo.
