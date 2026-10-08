# R5.2 — Correção do destino de O₂

A inspeção do firmware atual encontrou `oxygenMonitor` convertido por `toInt() != 0` em `oxyOn` (`ESP32S3-HUB/ESP32S3-HUB/src/protocol/Commands.h`). Portanto, essa chave é um interruptor; não existe referência numérica do monitor. A opção anterior era incompatível com o comportamento real e foi retirada do editor e da construção de comandos da rampa.

O₂ na rampa usa exclusivamente `ActiveCascadeReference`, com associação a um bloco de cascata. Os demais parâmetros continuam sem associação. O contrato recusa `MonitorReference`; o membro do enum mantém seu valor para reconhecer dados antigos e produzir erro, sem reinterpretá-los silenciosamente como referência de cascata. O conteúdo salvo permanece preservado até a correção explícita no editor.

Esta correção substitui menções anteriores a rampas do monitor nos recibos de construção de destinos, despacho e edição. Aqueles registros descrevem incrementos anteriores; não qualificam essa modalidade para execução. A medição de OD não é usada como prova de alteração do setpoint do controlador.

Testes verificam recusa do contrato, recusa do comando direto, rejeição de configuração antiga após serialização e ausência da opção no editor, mantendo o seletor de cascata contextual à linha de O₂. A execução completa de rampas continua pendente da integração do ciclo de vida.

Regressão completa final: 2395 testes aprovados, sem falhas ([TRX](evidence/recipes-r52-oxygen-contract-final-full.trx)). Release compilado em `D:/Temp/OpenTECHub-oxygen-contract-release/`, com 0 erros e 1553 avisos na compilação incremental. Sem alteração de firmware ou qualificação física neste escopo.
