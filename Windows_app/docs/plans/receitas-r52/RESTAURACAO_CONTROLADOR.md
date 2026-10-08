# R5.2 — Restauração do controlador de O₂

O controlador agora restaura o snapshot completo do PID: referência, ganhos, esforço, integral, histórico das medidas e erros, estado da derivada e termos exibidos. A distribuição entre agitação e vazão também é reconstruída para os modos de janelas, atuador único e trajetória de kLa.

Toda a entrada é desserializada e validada antes de alterar o controlador. Ganhos, limites e números devem ser válidos; campos obrigatórios, janelas e tabela precisam estar completos. Uma entrada inválida não altera parcialmente o estado. A restauração deve ser chamada com a barreira de cálculo/atuação fechada.

Os testes comparam o snapshot inteiro antes/depois e o próximo passo calculado em todos os quatro modos. Também verificam snapshots inválidos sem alteração parcial, cópia independente dos históricos e retomada sem salto na derivada. 148 testes de cascata e 2436 testes na regressão completa passaram. Evidência: `evidence/recipes-r52-controller-restoration-full.trx`.

O aplicativo compilou em Release com zero erros, em `D:/Temp/OpenTECHub-controller-restoration-release/`.

Este incremento fornece a operação de restauração do controlador. A recuperação reservada dos comandos diretos, confirmação, registro terminal e ligação ao ciclo do bloco permanecem pendentes. O bloco da rampa continua desabilitado no engine.
