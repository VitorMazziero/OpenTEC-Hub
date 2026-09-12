# Propostas de melhoria da comunicação com o Hub

## Status

Documento de propostas futuras. **Nada abaixo altera o protocolo atual.** O contrato vigente continua sendo o código atual em `ESP32S3-HUB/ESP32S3-HUB` e os firmwares ativos, incluindo rotas, query strings, nomes de campos e `cmd_id`/`ack_cmd_id`.

## Matriz JSON atual

| Componente | Estratégia ativa | Impacto do ArduinoJson 7.4.3 |
|---|---|---|
| Hub | parser/construção manual | nenhum |
| Bomba | parser/construção manual | nenhum |
| Fluxômetro | parser/construção manual | nenhum |
| Agitador | parser/construção manual (desacoplado do ArduinoJson) | nenhum |
| Biomassa | parser/construção manual | nenhum |
| Distância | parser/construção manual | nenhum |

## Propostas compatíveis

1. Publicar `protocol_version` e `firmware_version` como campos opcionais de telemetria. Hubs antigos ignorariam os campos; o Hub novo poderia sinalizar incompatibilidade ao operador.
2. Acrescentar `boot_id` a todos os dispositivos, como já ocorre no fluxo mais recente, para distinguir reinício de perda temporária de pacote.
3. Padronizar diagnósticos opcionais: `uptime_ms`, `free_heap`, `last_error`, `command_source` e contador monotônico de telemetria.
4. Validar numericamente todos os campos no Hub, rejeitando `NaN`, infinito, estouro e booleanos fora de `0/1` antes de atualizar o estado publicado.
5. Introduzir testes golden compartilhados para cada comando e telemetria, incluindo ordem irrelevante das chaves e campos opcionais.

## Propostas que exigem migração coordenada

1. Trocar telemetria em query string por `POST application/json`. Benefícios: encoding correto, payload maior, validação uniforme e menor ambiguidade. Exige período dual `GET`/`POST`, métricas de adoção e remoção somente após todos os nós serem atualizados.
2. Substituir parsers manuais por uma camada JSON única. Fazer por dispositivo, com fixtures de regressão antes da troca; não misturar essa mudança com controle físico.
3. Definir envelope versionado `{device, version, seq, boot_id, payload}`. Só adotar com compatibilidade dual e sem renomear os campos internos durante a transição.
4. Autenticar comandos e limitar replay. Requer provisionamento de chave e tratamento explícito de recuperação; não deve ser acrescentado unilateralmente ao Hub.

## Pontos frágeis observados

- Parsers baseados em `indexOf()`/`substring()` podem confundir chave com texto dentro de valor, não interpretam escapes e dependem de delimitadores simples.
- Parte dos comandos usa nomes diferentes entre PC→Hub e Hub→nó; essa tradução deve permanecer documentada e testada.
- `cmd_id`/`ack_cmd_id` confirma aplicação, mas não representa necessariamente conclusão física do movimento.
- HTTP 200 confirma recebimento do push, não qualidade ou plausibilidade da amostra.

## Sequência recomendada

Primeiro criar fixtures e telemetria de versão; depois robustecer validação numérica; em seguida padronizar biblioteca de protocolo; por último avaliar `POST` JSON e autenticação. Cada etapa precisa de build, simulator/contract test, bancada e soak test separados.

