# R0 — Contratos de receitas kLa e rampas

Implementação: 07/10/2026, sobre `84d246d`. Escopo: contratos, serialização e validação de domínio.

## Contratos entregues

- `RecipeInvocationContext`: cultivo, hash SHA-256 da receita, execução, bloco, invocação e ciclo. A chave idempotente identifica a invocação; o fingerprint identifica seu conteúdo.
- `KlaRecipeRequest`: definição comum E1, perfil/versionamento de qualidade E3, tentativas/exposição, deadline de aquisição, política de falha e retorno obrigatório ao estado anterior.
- `KlaRecipeResult` / `KlaRecipeAttemptResult`: sessão, condição, réplica, tentativa, kLa em h⁻¹, IC condicional, OUR com unidade no nome do campo, qualidades independentes, motivos, autoria automática e confirmações de retorno/persistência.
- `KlaReturnSnapshot`: referências anteriores N/Q, rota/configuração de gás, proprietários e identidade de seus executores, configurações completas de comando e snapshots versionados de controladores. Agitação e aeração são obrigatórias; os demais recursos afetados também devem ser capturados pelo adapter de R1.
- `LinearSetpointRampDefinition`: linhas independentes com variável/unidade, origem do início, alvo e tempo final em segundos ativos relativos ao início comum. Destino da referência de O₂ explícito. Sem interpolação/atuação nesta entrega.
- `PeriodicBlockSchedule` / `PeriodicBlockInvocation`: primeiro disparo relativo à entrada no bloco de periodicidade em ramo paralelo, período fixo, identidade de agendador/alvo/slot e descarte de slots vencidos. Exemplo 2 h + 4 h: slots em 2, 6, 10 h. O request e o resultado conservam o mesmo contexto, verificado em `ValidateAgainst`.
- `RecipeActuatorHandoff`: protocolo de cessão entre ramos `Requested → CascadeQuiesced → AssayActive → Restoring → CascadeResumed`, com fases terminais de falha de restauração e parada de emergência. Cada transição incrementa a revisão, registra confirmação/autoria e impede ativação do ensaio antes da pausa ou retomada antes da restauração.

`KlaRecipeResult.ValidateAgainst(request)` verifica proveniência, snapshot de retorno, política, cobertura das réplicas e limites de tentativas. `Validate` isolado permite ler resultados parciais/falhos; o consumidor deve usar `ValidateAgainst` antes de tratar a execução como conforme ao request.

## Retorno obrigatório

Não existe opção de retorno a valores fixos ou de liberação silenciosa para Manual. Um alvo legado de agitação diferente do snapshot é rejeitado. Sucesso, seleção de tentativa e nova tentativa requerem restauração confirmada e salvamento confirmado. Uma falha de recuperação permanece distinta de um resultado científico inconclusivo.

O snapshot representa o estado operacional anterior, não a reversão da evolução biológica do cultivo. Configurações de controlador devem permitir retomada coerente com o adapter correspondente; um JSON de estado não comprova sozinho recuperação física. R1 deverá capturar e validar essas configurações, transferir a posse e registrar evidências de confirmação. Emergência sempre prevalece sobre retomada normal.

Na cascata da receita, a suspensão deve congelar atualizações e comandos, impedir acúmulo de erro durante o ensaio e retomar os parâmetros do mesmo loop com relógio/histórico rebaseados. `RecipeEngine.Cascade` ainda não implementa essa suspensão. O coordenador de R1 deverá obter confirmação de parada do último passo PID antes de ceder N/Q, restaurar o estado anterior e só então confirmar retomada. A `Saída Loop` aceita hoje condições de encerramento e conserva esse significado. R4 introduzirá o bloco de Periodicidade no ramo paralelo.

## Versionamento e compatibilidade

`RecipeContractSerializer` usa envelope `schemaVersion: 1`, `kind` e `payload`. Tipos: `klaRecipeRequest`, `klaRecipeResult` e `linearSetpointRamp`. Rejeita versões/tipos desconhecidos, campos desconhecidos, enums numéricos/desconhecidos, propriedades duplicadas e payloads incompletos. Não há versão anterior desse novo envelope a migrar.

Os documentos de receita continuam no schema v1 existente, com aliases históricos e defaults preservados. Não recebem snapshots nem estado de execução. Mudança deliberada: tipo de bloco desconhecido/ausente agora produz `RecipeFormatException`, em vez de desaparecer do fluxo. Os tipos futuros `KlaAssay` e `LinearSetpointRamp` ainda não estão no catálogo e são rejeitados até seus pacotes de integração; não podem executar como no-op.

`Snapshot(request)` faz uma cópia por serialização para desligar configurações aninhadas do editor. Os campos novos usam records, propriedades init e coleções imutáveis. O fingerprint é SHA-256 de JSON com propriedades ordenadas; é detector de divergência, não mecanismo de execução idempotente. Deduplicação persistida entra em R2/R3. A agenda usa tempo monotônico decorrido desde a entrada no bloco de Periodicidade, incluindo a duração do ensaio; slots perdidos são pulados, sem repetir pulsos ao retomar.

## Políticas e limites

O contrato não fornece defaults universais de exposição, número de tentativas ou recuperação. Os valores obrigatórios pertencem ao perfil operacional qualificado. Somente motivos recuperáveis estruturados podem integrar a política; sua classificação e aplicação serão implementadas em R2. Critérios bióticos de proteção devem estar completos e as políticas não podem excedê-los.

OUR válido não é requisito do abiótico. Resultado condicional exige motivos autorizados pelo perfil quando selecionado. A opção legada `AutoAcceptRuns` é rejeitada em requests de receita para impedir duas autoridades de aceitação.

Rampas rejeitam variável duplicada, início ambíguo, alvo fora do envelope, duração não positiva/não finita e destino de O₂ fora de contexto. Limites reais da rota e disponibilidade do controlador serão validados na execução em R5.

`ValidateResolvedStart` valida novamente a trajetória depois da captura do início atual. Onde zero significa desligado e a faixa operacional começa acima de zero, não permite interpolar entre zero e a faixa operacional; a transição de liga/desliga deve usar os blocos apropriados.

## Verificação

Testes dedicados em `RecipeExecutionContractTests`, junto a `RecipeDomainTests` e `KlaSessionContractTests`: round-trip dos quatro modos, snapshot/fingerprint, JSON desconhecido/duplicado, retorno incompatível, política biótica, tentativas versus réplicas, resultados parciais, autoria automática, salvamento/recuperação obrigatórios, cobertura do request, política condicional/OUR, unidades e tempos de rampa e compatibilidade de receita legada.

Comando de validação: `dotnet test Windows_app/tests/OpenTECHub.Tests/OpenTECHub.Tests.csproj -p:SelfContained=false --no-restore --filter "FullyQualifiedName~RecipeExecutionContractTests|FullyQualifiedName~RecipeDomainTests|FullyQualifiedName~KlaSessionContractTests"`.

Verificação anterior ampliada, incluindo todos os testes com `Recipe` no nome e contratos de sessão kLa: **154 aprovados, 0 falhas, 0 ignorados**. Inclui 23 casos de R0 antes da extensão de agenda. Comando: `dotnet test Windows_app/tests/OpenTECHub.Tests/OpenTECHub.Tests.csproj -p:SelfContained=false --no-restore --filter "FullyQualifiedName~Recipe|FullyQualifiedName~KlaSessionContractTests"`. Os novos casos cobrem 2/6/10 h, ausência de fila de slots perdidos, identidade do slot e sequência de handoff.

A tentativa de validar essa extensão no checkout compartilhado encontrou arquivos novos de E5/E6/E7 ainda incompletos, fora deste pacote (`KlaSequence.cs`, `KlaAssayApiContracts.cs` e dependências), que impedem a compilação do aplicativo. Não atribuir a esta extensão a contagem anterior de 154; repetir o filtro após estabilizar essas mudanças. Os arquivos de outro trabalho não foram incluídos no commit deste pacote.

O build de testes usa o runtime instalado (`SelfContained=false`); a primeira tentativa self-contained sem restore encontrou pacotes de runtime não baixados. Isso não exigiu alteração do projeto de distribuição.

R0 não registra serviços na aplicação, não habilita blocos e não altera o algoritmo científico ou o comportamento de recuperação do runner. Qualificação física e execução autônoma pertencem a R1–R6.
