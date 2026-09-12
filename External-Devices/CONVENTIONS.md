# Convenções dos dispositivos externos

## Estrutura

Os nomes de diretório usam `kebab-case` sem acentos. O sketch principal tem o mesmo nome da pasta do firmware. Código ativo fica em `firmware/<nome>/src`, separado por responsabilidade. Fragmentos privados `.h` incluídos por `FirmwareApp.cpp` são uma etapa transitória para preservar uma única unidade de tradução; novos módulos independentes devem usar pares `.h/.cpp`.

## Comentários e histórico

Comentários no código devem explicar restrições, invariantes, segurança ou decisões não óbvias. Históricos de versão, listas extensas de alterações, planos e explicações operacionais pertencem a Markdown. O estado observado fica em `docs/CURRENT_STATUS.md`; mudanças realizadas em `CHANGELOG.md`; propostas ainda não implementadas nos documentos transversais.

## Compatibilidade

Rotas HTTP, nomes e tipos de campos, unidades, sentinelas, periodicidade, `cmd_id` e `ack_cmd_id` são contrato de fio. Reorganização não autoriza alterá-los. Toda proposta futura deve ser documentada, versionada e implantada com compatibilidade explícita entre Hub, dispositivo e aplicativo.

## Artefatos

Não versionar caches, diretórios de build, logs transitórios ou dependências restauráveis. Arquivos CAD, imagens, documentos e pacotes binários cobertos por `.gitattributes` usam Git LFS. O manifesto SHA-256 em `archive/IMPORT_MANIFEST.sha256` é um recibo imutável do conteúdo recebido; ele não descreve a árvore corrente depois das edições documentais. Os seis baselines de firmware/UI que devem permanecer byte a byte intactos são verificados por `tools/Test-FirmwareBaselines.ps1`.

## Política de Commits

Toda alteração de código, teste ou documentação agora termina com um commit atômico e auditável. As regras mandatórias para commits são:
1. **Ciclo Fechado**: toda tarefa concluída deve rodar as validações (`Test-FirmwareBaselines.ps1`, `Test-HubDeviceContracts.ps1`, `Compile-ExternalDevices.ps1`) e ser finalizada com um commit imediato antes de iniciar a próxima tarefa.
2. **Atomicidade e Escopo**: não misturar mudanças estruturais, alterações em nós diferentes ou refatorações de código no mesmo commit.
3. **Padrão Semântico**: mensagens no formato Conventional Commits: `<tipo>(<escopo>): <descrição em português>`, onde `<tipo>` é `feat`, `fix`, `refactor`, `perf`, `docs`, `test` ou `chore`.
4. **Isolamento**: alterações preexistentes no repositório (ex.: `Windows_app` ou `ESP32S3-HUB`) nunca devem ser misturadas aos commits de `External-Devices`.
