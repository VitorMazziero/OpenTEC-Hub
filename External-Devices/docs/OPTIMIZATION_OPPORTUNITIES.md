# Oportunidades de otimização

Este registro reúne melhorias encontradas durante a reorganização. Nenhuma delas foi aplicada ao comportamento do firmware nesta etapa.

## Prioridade alta

1. Substituir gradualmente os fragmentos privados de bomba, fluxômetro e biomassa por módulos `.h/.cpp` com estado explícito e interfaces pequenas. A extração atual preserva uma única unidade de tradução para reduzir risco de regressão.
2. [Concluído] Redução drástica de alocações `String` em loops e telemetria: substituídas concatenações dinâmicas de `String` em loops periódicos (1–10 Hz) nos 5 nós por buffers estáticos de pilha com `snprintf` e pré-alocação controlada com `reserve()`.
3. Criar testes nativos para parsers manuais com campos fora de ordem, espaços, números negativos, strings contendo nomes de chaves, JSON truncado e duplicidade de chaves.
4. Separar estado de comunicação, estado físico aplicado e estado desejado nos firmwares ainda baseados em globais.

## Prioridade média

1. Unificar backoff, timeout, detecção do Hub e política de reconexão em uma pequena biblioteca interna versionada.
2. Gerar automaticamente as estruturas C++ e fixtures de contrato a partir de uma especificação comum, mantendo os nomes atuais no fio.
3. Adicionar telemetria de diagnóstico de fila: revisão pendente, tentativas, idade do comando e último erro HTTP.
4. Medir stack de cada task FreeRTOS e remover margens apenas com evidência de bancada e soak test.
5. Automatizar análise/testes dos aplicativos Flutter e Python em CI depois de fixar suas versões de SDK.
6. Eliminar os avisos do Flutter 3.41.5 encontrados nos aplicativos legados (`withOpacity`, `activeColor`, parâmetros `key`, tipos privados expostos, `BuildContext` após `await`, `print` e construtores sem `const`) em uma mudança exclusiva de manutenção de UI.
7. [Concluído] O agitador foi desacoplado de `ArduinoJson` e migrado integralmente para parser/serializador manual, eliminando todas as advertências de depreciação de biblioteca e reduzindo o consumo de memória flash.

## Prioridade baixa

1. Normalizar mensagens de log e níveis sem misturar logs com respostas de protocolo.
2. Converter o HTML embarcado da biomassa em pipeline minificado opcional, mantendo `web/index.html` como fonte humana e verificando equivalência funcional.
3. Deduplicar ativos CAD e documentos históricos por hash, sem remover evidência única.

## Gates

Cada otimização deve manter fixtures de fio, compilar nos mesmos FQBNs, passar teste cruzado com o Hub e receber validação em hardware. Otimização de memória ou latência não justifica alteração silenciosa do contrato.
