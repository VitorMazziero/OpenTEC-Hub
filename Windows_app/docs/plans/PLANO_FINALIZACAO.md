# Plano de finalização — kLa abiótico/biótico e receitas autônomas

Data: 08/10/2026. Base: `main` em `a5470ae`, mais as correções desta revisão. Escopo: [plano kLa de 06/10](2026-10-06-plano-kla-biotico-abiotico-testes-unicos-multiplos.md) (E0–E7), [plano de receitas de 07/10](2026-10-07-receitas-kla-autonomo-e-rampas.md) (R0.1–R6.2) e pendências gerais do aplicativo. Auditoria detalhada: [AUDITORIA_FINAL_RECEITAS.md](AUDITORIA_FINAL_RECEITAS.md).

## 1. Situação verificada

| Plano | Etapas | Situação em software | Pendente |
|---|---|---|---|
| kLa 06/10 | E0–E6 | Implementadas e validadas | — |
| kLa 06/10 | E7 | Parte de software, ajuda e build candidata concluídas | Matriz experimental: sonda, cultivos independentes, eventos de gás do corpus, avaliação interativa e bancada; liberação biótica supervisionada |
| Receitas 07/10 | R0.1–R4.1 | Critérios conferidos na auditoria final | — |
| Receitas 07/10 | R4.2, R5.1, R5.2 | Critérios conferidos; ressalvas A-04 e A-08 | Decisão sobre rampas físicas; verificação interativa no aplicativo |
| Receitas 07/10 | R6.1 | Regressão integrada aprovada após A-01; exemplos e renderização verificados | A-06, A-07 e A-08 |
| Receitas 07/10 | R6.2 | — | Bancada, qualificação por instalação/protocolo/perfil e caminho de capacidade física |

Por construção, a execução autônoma de kLa só é possível em ambiente isolado (`--kla-test-file`): perfis, fábrica de execução e engine exigem `IsIsolatedSimulation`. O bloqueio biótico físico de E7 (`KlaActuationRelease`) continua ativo. A habilitação física exige uma entrega de código própria em R6.2, não apenas uma configuração.

## 2. Achados e soluções

| ID | Gravidade | Achado | Solução | Estado |
|---|---|---|---|---|
| A-01 | Média | Captura WPF intermitente na regressão completa (2507/2508) | Coleção `WpfRendering` sem paralelismo | Corrigido |
| A-02 | Baixa | CS8602 e CS9124 no código novo | Correção local | Corrigido |
| A-03 | Média | Critério R5 de relógio civil sem teste | Teste dedicado | Corrigido |
| A-04 | Alta | Rampas habilitadas no ambiente físico sem gate R6.2 | Decisão D-058: manter habilitadas para bancada; [roteiro](ROTEIRO_BANCADA_RAMPAS.md) | Decidido |
| A-05 | Média | Falha de retorno no biótico corta aeração e agitação | D-060: três tentativas; biótico mantém comandos e alarme | Corrigido |
| A-06 | Média | 2120 avisos IDE0011; gate 0.25.0 exige zero | D-059 + 17 avisos xUnit corrigidos; compilação com zero avisos | Corrigido |
| A-07 | Baixa | Testes regravam evidências versionadas | D-064: diretório temporário + `OPENTEC_UPDATE_EVIDENCE=1` | Corrigido |
| A-08 | Média | Fluxo autônomo nunca exercitado no aplicativo; não há perfil de simulação para teste manual | D-062: perfil do operador; [roteiro de bancada](ROTEIRO_BANCADA_KLA.md) | Finalizado (pendente validação de bancada) |
| A-09 | Baixa | Documentos de estado desatualizados | Atualização | Corrigido |
| A-10 | — | E7 experimental e R6.2 | Roteiros de bancada | Finalizado (pendente validação de bancada) |
| A-11 | Alta | Nome de sessão automática repetido fazia falhar o 2º disparo periódico | Nome com data, protocolo, N/Q e disparo | Corrigido |
| A-12 | Média | Execução física marcada como simulação | Origem pelo ambiente | Corrigido |

## 3. Pacotes de execução

Cada pacote termina com commit isolado, testes relevantes, regressão completa quando tocar código de produção e recibo curto neste diretório.

**F1 — Correções da auditoria (concluído).** A-01, A-02, A-03 e A-09. Aceite: regressão completa sem falhas, zero avisos CS, documentos de estado atualizados.

**F2 — Decisões do autor.** A-04 e A-06 decididos em 08/10/2026 (D-058, D-059). As demais perguntas estão no [pacote de decisões](PACOTE_DECISOES.md). Texto original:

1. A-04: rampas no ambiente físico. Opção recomendada: gate por destino e instalação, liberando primeiro temperatura nativa/banho, motor e vazão após bancada; O₂ da cascata, pH e pressão depois.
2. A-05: falha de retorno. Opção recomendada: no biótico, comando de segurança com ar ao reator (válvula A) na vazão e rotação do snapshot, alarme travado e liberação para Manual; manter a parada total no abiótico.
3. A-06: estilo de chaves. Opção recomendada: `csharp_prefer_braces = when_multiline:suggestion`, mantendo zero avisos CS/CA como gate.

**F3 — Gate físico das rampas (dispensado por D-058; reabrir só se a bancada reprovar um destino).** `RecipeRampQualification` por instalação e destino/rota; `RecipeEngine.CanStart` recusa linhas não qualificadas no ambiente físico; o editor mostra o motivo. Aceite: simulação inalterada; físico sem qualificação recusado antes de qualquer comando; qualificação vencida ou de outra instalação recusada.

**F4 — Política de falha biótica (A-05).** `SafeStopAndRelease` passa a usar a política do protocolo da autoridade não devolvida. Aceite: testes nos dois protocolos; emergência continua prevalecendo; nenhum religamento após revogação de segurança.

**F5 — Gate de avisos (A-06) — concluído.** Aplicar a decisão; `dotnet build -c Release` com zero avisos e `dotnet format --verify-no-changes` limpo. Commit exclusivamente de estilo, sem mudança de comportamento.

**F6 — Evidências fora da árvore (A-07).** `TestPaths.EvidenceRoot`: diretório temporário por padrão, árvore `docs/` apenas com `OPENTEC_UPDATE_EVIDENCE=1`. Ajustar `ScreenshotCaptureTests`, `DocumentationEvidenceTests`, `CalibrationAcquisitionTests`, `KlaCommonRenderingTests`, `KlaAutomaticHistoryRenderingTests` e `KlaRecipeApplicationHostTests`. Aceite: regressão completa sem arquivos modificados em `git status`.

**F7 — Smoke interativo em simulação (A-08).** Ferramenta externa em `tools/` que lê `AutomacaoKla/simulacao/context.json` e grava perfis sintéticos abiótico/biótico marcados como simulação, mantendo a decisão de R4.2 de que o aplicativo não cria perfis. Roteiro: abrir com `--kla-test-file`, importar perfis, abrir os sete exemplos, executar único abiótico, matriz biótica, agenda curta (minutos) e rampa de O₂ com a cascata, testar pausa, parada e emergência. Registrar capturas e diário. Aceite: resultados visíveis no histórico, navegação receita → sessão e nenhuma condição de erro silenciosa.

**F8 — Documentação de operação.** Manual do Operador e ajuda contextual para Determinar kLa, Periodicidade e Rampa linear; `CHANGELOG [Unreleased]`; gate 0.25.0 em `CURRENT_STATUS.md`.

**F9 — R6.2 / E7 bancada.** Roteiro de E7 ampliado: cessão/retomada da receita, rotas de gás e retorno, exposição, perda de comunicação, persistência e rampas por destino. Depois, entrega de código para capacidade física qualificada por instalação/protocolo/perfil, mantendo `KlaActuationRelease` até cumprir os critérios bióticos. Aceite: evidência física documentada; sem ela, somente o ambiente simulado permanece liberado.

**F10 — Liberação.** Versão, tag, instalador, rollback e pacote de evidências conforme o gate de `CURRENT_STATUS.md`.

Ordem: F1 → F2 → (F3, F4, F5, F6 em paralelo) → F7 → F8 → F9 → F10.

## 4. Recibo desta revisão

- Primeira regressão completa (Release, `a5470ae`): 2507 aprovadas, 1 falha intermitente (A-01).
- Regressão completa após A-01/A-02: **2508 aprovadas, 0 falhas**, zero avisos CS.
- Teste novo de A-03 (`RecipeRampActiveClockTests`): 3/3 aprovados.
- Após D-059 e correção de 17 avisos xUnit: compilação da solução em Release com **0 avisos e 0 erros**; regressão completa **2509 aprovadas, 0 falhas** (`receitas-r61/evidence/auditoria-final-full.trx`).
- Instalador intermediário: `installer/Output/OpenTECHub_Setup_v0.26.5-dev.186.exe` (código `530d500`), publicação self-contained win-x64; smoke de inicialização do executável publicado com `--exit-after-ms`: saída 0, log sem erros.
- Respostas Q1–Q4 do [pacote de decisões](PACOTE_DECISOES.md) recebidas; implementação no pacote F11 abaixo.

Nenhuma atuação física foi executada nesta revisão.

## 5. Estado final (08/10/2026)

| Pacote | Estado |
|---|---|
| F1 Correções da auditoria | Concluído |
| F2 Decisões do autor | Concluído — Q1–Q15 respondidas; D-058 a D-064 |
| F3 Gate físico das rampas | Dispensado (D-058) |
| F4 Política de falha biótica | Concluído (D-060) |
| F5 Gate de avisos | Concluído (zero avisos) |
| F6 Evidências fora da árvore | Concluído (D-064) |
| F7 Smoke interativo | Substituído pelo [roteiro de bancada do kLa](ROTEIRO_BANCADA_KLA.md) — finalizado (pendente validação de bancada) |
| F8 Documentação de operação | Concluído: Manual §10, CHANGELOG 0.27.0, CURRENT_STATUS |
| F9 R6.2 / E7 bancada | Código e liberação física concluídos (D-061); **pendente validação de bancada** |
| F10 Liberação | `v0.27.0` — instalador e recibo abaixo |

Regressão completa após as decisões: **2516 aprovadas, 0 falhas**; compilação da solução em Release com **0 avisos**. Simulações do banho r3.3 e 135 contratos do Hub aprovados.

