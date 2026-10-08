# R5.2 — Precisão da referência de temperatura

A captura inicial da rampa registra a rota de temperatura junto com as referências e o estado anterior. A trajetória, os comandos e a confirmação terminal usam uma casa decimal para o módulo nativo e duas para o banho externo. Uma troca de rota impede a aplicação do quadro capturado.

O comando `tempSetpointExact: true` permite atualizar passos de 0,01 °C sem a tolerância histórica dos comandos comuns. O Hub preserva o eco de referência zero após desligar o banho; isso permite verificar o desligamento sem exigir temperatura física zero. Referências positivas de banho sem comando confirmado continuam indisponíveis.

Receitas antigas com referências de temperatura já representáveis em uma casa decimal continuam legíveis. Para referências com precisão maior, o registro inicial precisa identificar a rota. A confirmação exige eco da referência com tolerância de 0,005 °C, além dos critérios de processo e disponibilidade existentes.

Verificação: 54 testes focados de rampas, 2.416 testes na regressão completa e 135 contratos do Hub passaram. Os cinco testes de confirmação de temperatura foram repetidos após acrescentar o caso de eco divergente por 0,01 °C. O aplicativo compilou em Release sem erros, e o firmware do Hub compilou para ESP32-S3. A integração do ciclo de execução e a qualificação física permanecem pendentes.
