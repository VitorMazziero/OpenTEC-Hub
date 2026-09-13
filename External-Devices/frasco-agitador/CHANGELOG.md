# Changelog — Frasco agitador

## Firmware v10 — segurança e identidade (2026-09-13)

- Removido o pulso autônomo de 100 % por 200 ms no boot; a ponte nasce freada.
- `ActivePot` passa a iniciar desabilitado após qualquer reset e só retorna por comando explícito.
- Inversões passam por rampa até zero antes da troca do lado da ponte e rampa de subida.
- Unificada a identidade `v10` em hello, diagnóstico e página OTA.
- Removidas as constantes de GPIO 34/35 que prometiam sensores de corrente não implementados.
- Adicionados testes estáticos de regressão; compilação dos cinco firmwares aprovada.

Validação física continua pendente conforme `docs/VALIDATION.md`.

## Reorganização de 2026-09-11

- Selecionada e nomeada a versão ativa rev H.
- Separados firmware, aplicativo, hardware, testes/evidências e histórico.
- Preservado o monólito ativo anterior em `archive/active-baseline`.
- Removidos cabeçalhos extensos e históricos embutidos do código ativo; decisões foram transferidas para Markdown.
- Criada estrutura modular por responsabilidade sem alteração intencional do contrato de fio.
- Compilado no ESP32 core 3.3.11 usando as bibliotecas compartilhadas da máquina de upload.

Validação de hardware permanece pendente.
