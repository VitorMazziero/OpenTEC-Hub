# R5.2 — Referências representáveis de pH e pressão

O firmware atual lê `pressureReference` com `toInt()` e envia uma referência inteira ao módulo (`Commands.h` e `Runtime.h`). Para pH, `Runtime.h` formata `pHReference` com duas casas decimais antes do comando UART. A trajetória não deve finalizar esperando um valor que esse caminho não representa.

`RecipeRampReferenceQuantization` centraliza a representação do motor, pressão e pH. Pressão conserva a conversão existente por truncamento; pH usa duas casas decimais, com arredondamento dos pontos médios para longe de zero. O destino direto, a reconstrução da trajetória na captura durável e a validação do resultado terminal usam a mesma regra. A referência solicitada permanece no registro de configuração; a confirmação registra a referência representável aplicada.

Exemplos: pressão solicitada de 100,9 corresponde a referência 100; pH solicitado de 6,805 corresponde a 6,81. Um resultado terminal com o alvo não representável é recusado. O comando da rampa já carrega o valor quantizado, evitando uma segunda interpretação numérica diferente na validação local.

pH zero desliga a dosagem no firmware. Uma referência positiva que arredondaria para zero é recusada antes de obter recibo inicial. Trajetórias de pH entre zero e operação também são recusadas antes da atuação; desligamento não é um ponto contínuo da malha de dosagem. Isso não altera os antigos blocos de setpoint nem os limites do firmware.

Testes verificam trajetória, comando, preservação da configuração original, resultado terminal quantizado, rejeição de alvo positivo convertido em OFF e ausência de recibo inicial nessa condição. Confirmações de pH/pressão e integração completa de execução/retorno/cancelamento continuam pendentes. A precisão da rota nativa de temperatura ainda precisa ser conciliada no contexto da composição dos destinos.

Regressão completa final: 2401 testes aprovados, sem falhas ([TRX](evidence/recipes-r52-wire-quantization-final-full.trx)). Release compilado em `D:/Temp/OpenTECHub-ramp-wire-release/`, com 0 erros e 1557 avisos na compilação incremental. Firmware preservado; esta entrega não qualifica atuação física.
