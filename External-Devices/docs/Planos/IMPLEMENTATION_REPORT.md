# Relatório da reorganização

## Resultado em 2026-09-11

Os cinco dispositivos foram movidos para a estrutura padronizada `apps/`, `firmware/`, `hardware/`, `tests/`, `docs/` e `archive/`. As versões ativas ficaram isoladas de sketches de bancada e versões históricas. Os monólitos originais ativos foram preservados em `archive/active-baseline`.

## Firmware

| Dispositivo | Estrutura ativa | Build |
|---|---|---|
| Distância | módulos `.h/.cpp` | aprovado; 82% flash, 15% RAM global |
| Agitador | módulos `.h/.cpp` | aprovado; 81% flash, 14% RAM global |
| Bomba | fragmentos privados por responsabilidade | aprovado; 83% flash, 16% RAM global |
| Fluxômetro | fragmentos privados por responsabilidade | aprovado; 87% flash, 15% RAM global |
| Biomassa | fragmentos privados por responsabilidade | aprovado; 83% flash, 21% RAM global |

Todos foram compilados com Arduino CLI 1.5.1, ESP32 core 3.3.11 e bibliotecas compartilhadas em `D:\OneDrive\Documentos\Arduino\libraries`. O código do `ESP32S3-HUB` não foi alterado nesta reorganização.

## Compatibilidade

- seis baselines de firmware/UI conferidos pelos hashes SHA-256 originais;
- 10 rotas Hub↔nós e campos críticos conferidos estaticamente;
- `cmd_id` e `ack_cmd_id` preservados onde existentes;
- UI HTML da biomassa extraída para `web/index.html` e cabeçalho gerado verificado;
- ArduinoJson 7.4.3 afeta diretamente apenas o agitador; demais parsers são manuais.

## Aplicativos

- Biomassa: sete programas de teste passaram com console UTF-8.
- Bomba Flutter: teste passou; análise possui 57 avisos informativos legados.
- Fluxômetro Flutter: análise limpa e teste passou.
- Agitador Flutter: teste passou; análise possui uma depreciação informativa.
- Dois testes Flutter ainda derivados do template foram corrigidos para montar as telas reais.

## Git e artefatos

Git LFS foi inicializado localmente e `.gitattributes` cobre formatos CAD, mídia, documentos e pacotes grandes dentro de `External-Devices`. Caches, builds e logs transitórios estão ignorados. Os manifestos `IMPORT_MANIFEST.sha256` são recibos da importação, não uma expectativa da árvore após a padronização documental.

## Gate aberto

Não houve gravação nem ensaio com hardware. Presença, sensores, atuadores, persistência, temporização, reconexão, comandos idempotentes, estados seguros e soak test continuam pendentes conforme `docs/VALIDATION.md` de cada dispositivo.
