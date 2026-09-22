# Auditoria adicional — integração Hub e banho externo

**Data:** 2026-09-22

**Base:** Hub `10.5.1-dev` + banho `r3.1`
**Estado:** correções de software aplicadas; validação física H08/H09 permanece obrigatória.

## Problemas corrigidos

| Problema | Efeito potencial no processo | Correção aplicada |
|---|---|---|
| Falha deixava de valer assim que o nó voltava ao normal | retomada automática sem decisão do operador | fault travado até `bathCascadeReset` |
| Cada push em `done` renovava o timestamp | cooldown nunca terminava | conclusão vinculada uma vez ao `ack_cmd_id` enviado |
| Setpoint igual não rearmava UART após troca | via original selecionada sem comando efetivo | novo comando de sessão força `flagTempDirty` |
| `resetVariables` não enviava desabilitação | placa original podia manter atuação | envio de `100B` pela fila UART |
| `/bathData` aceitava string/IP conectado sem vínculo | estado inválido ou nó errado podia alimentar a cascata | IP, registro, r3.1 e enums validados antes de mutação/ACK |
| Período de push podia exceder a janela de 5 s | pausas e reinicializações cíclicas do PI | cadência integrada limitada a 2 s |
| Comando podia executar indefinidamente | cascata bloqueada sem diagnóstico | timeout de 300 s vira fault travado |
| Rollover afetava reconexão, OTA e idade | perda de comunicação ou diagnóstico incorreto | comparações por diferença temporal |
| ACK e conclusão não eram distintos no agregado | aplicativo podia mostrar confirmação como conclusão | IDs, pendência e idade publicados |

## Riscos residuais para o controle geral

1. **O C404 não desliga remotamente.** Ao retornar à via UART, o banho mantém o último SP.
   O operador deve interromper/isolar fisicamente a circulação externa antes de reativar a
   via original; o software não consegue provar essa condição.
2. **Último SP sobrevive à perda do Hub.** Queda de Wi‑Fi, Hub ou nó pausa novos comandos,
   mas não remove o setpoint aplicado no C404. O processo precisa de limite físico independente.
3. **`Tempval` é um único ponto de realimentação.** Sensor deslocado, preso ou descalibrado
   pode induzir alvo inadequado. Há detecção de ausência/faixa, não redundância de plausibilidade.
4. **`BathPv` e `Tempval` medem pontos diferentes.** `BathPv` permanece diagnóstico; usá-lo
   como substituto esconderia o atraso entre banho e reator.
5. **Duas malhas e grande inércia.** Sintonia agressiva, circulação, metabolismo e isolamento
   podem causar oscilação ou saturação mesmo com software correto.
6. **Desgaste de relés/teclas.** Banda pequena ou ruído aumenta comandos por hora; H09 deve
   medir essa frequência antes da liberação.
7. **Rede sem autenticação criptográfica.** IP+versão reduz injeção acidental, mas um cliente
   com acesso ao SoftAP ainda pode se registrar como banho.
8. **Intervenção manual e guarda automática.** Em auto, mudança local pode ser revertida;
   manutenção deve selecionar manual ou suspender a cascata.
9. **Timeout de 300 s é operacional.** Sequência legítima mais longa entra em fault; offsets e
   velocidade real do C404 precisam ser confirmados no H08.
10. **`100B` ainda requer evidência integrada.** A exclusividade térmica e a ausência de atuação
    simultânea precisam de captura física durante H08.

## Condição para iniciar o Windows App

O contrato de software está congelado em Hub `10.5.1-dev` e nó `r3.1`. O Windows App deve
tratar `BathCommandCompletionPending`, estado `fault`, motivo de pausa, versões mínimas e o
aviso de isolamento físico. A classificação continua `NOT_READY_FOR_CULTURE` até H08/H09 e
o checklist H10 serem aprovados.
