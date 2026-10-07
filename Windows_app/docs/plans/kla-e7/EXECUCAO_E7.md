# E7 — Validação, documentação e distribuição

Estado: validação de software e build candidata; etapa operacional ainda pendente.

## Software

Regressão integrada: 393 testes aprovados, sem falhas, abrangendo kLa, persistência bruta, arbitragem, OUR virtual, receitas, ajuda e renderização compacta. A verificação complementar de reabertura de sessão abiótica/biótica passou em dois testes. A revisão do ciclo de vida da API acrescentou uma verificação de instância fechada, levando inicialmente a 396 verificações distintas aprovadas; a suíte E6 passou em 14 casos. O teste posterior de intervalo indisponível em resultado recusado elevou o total atual a 397 verificações distintas. Recibos separados evitam somar novamente casos repetidos. Nenhum teste enviou comandos a equipamento real ou utilizou os arquivos originais do cultivo para migração.

| Área da matriz | Evidência/limite |
|---|---|
| Verdade/unidades | Casos analíticos e componentes Python congelados E0/E3; ADC diferente de OD, kLa em h⁻¹, OUR em pp/h e conversão somente com Cref/origem |
| Sonda/modelo | Resposta desconhecida condicionada; resposta lenta/não separável recusada; consumo variável e OTR residual explicitados nos testes determinísticos |
| Curva | Janela, Ceq, força motriz, sensibilidade e recusas auditáveis; IC OLS condicional. Não houve validação de cobertura total da incerteza em novos cultivos |
| Aquisição | OD novo por canal, gap/NaN/duplicação/tempo e watchdog; arquivo inválido não é convertido de ADC para OD ou completado com vazão zero |
| Controle/falhas | Simulação e mock de válvulas, cancelamentos e retorno/falha; posse compartilhada e fila bloqueada. Retorno físico em bancada pendente |
| OUR virtual | Suspensão/integral/retomada e hipóteses estacionárias testadas; não usado como verdade independente do kLa |
| Arquivos | Hashes E0, versões, revisões imutáveis, falhas de gravação e reabertura em serviços; fase antiga OpeningVent preservada como legado, sem atribuir-lhe a rota atual |
| UI | Quatro modos e revisão, gráficos no topo, 100/125/150 DPI, Tab/Escape e recusas; imagens são WPF offscreen com dados simulados. Avaliação interativa no PC da bancada pendente |
| Mapas/receitas | Importação opcional/contexto, réplica/tentativa, publicação preservada, idempotência e limites; integração executável de receitas ainda desabilitada |

A primeira execução ampla de E7 apontou três problemas: fase histórica OpeningVent, termos antigos na ajuda/teste e temporizador real no relógio simulado. Após correção, uma espera de banho/vazão expôs uma suposição de agendamento de 20 ms no teste. A condução por quadros assentados resolveu essa corrida de teste. O TRX aprovado posterior registra 393/393. As falhas anteriores são preservadas no histórico local e não são contadas como aprovação.

## Corpus de 92 curvas

`corpus-audit.json` registra versão/configuração do núcleo, grupo/partição E0, hashes e motivos para cada curva. O gerador lê o projeto científico sem modificá-lo e confere o hash do manifesto E0. Não aplica nova limpeza nem usa kLa legado como verdade.

Resultado da auditoria estrita: 92 inconclusivas, incluindo 17 bióticas. Os CSVs curados fornecem OD e metadados descritivos, mas não confirmações de comutação exigidas pelo contrato. Não se derivou uma confirmação física do mínimo de OD, nem se atribuiu C*=100%. Esse resultado verifica a recusa por informação ausente; **não mede erro, viés ou cobertura do estimador no corpus**. Reanálise numérica validada exige eventos/origem adicionais; novos cultivos independentes continuam necessários para biótico.

Reprodução:

```powershell
python Windows_app/tools/kla_e0_reference.py verify
python Windows_app/tools/kla_e7_corpus.py '<pasta 06_kLa_Modelo>' Windows_app/build/kla-e7-corpus-input.json
dotnet run --project Windows_app/tools/KlaCorpusAudit -c KlaE4Validation -- Windows_app/build/kla-e7-corpus-input.json Windows_app/docs/plans/kla-e7/corpus-audit.json
```

## Liberação e build

`KlaActuationRelease` bloqueia início biótico físico antes dos comandos. O aplicativo só abre essa passagem no modo de reprodução offline isolada. Preparação, importação e revisão continuam disponíveis. O ensaio abiótico permanece acessível. Não há bloco periódico habilitado nem adaptador de receitas registrado.

Build candidata produzida em `Windows_app/build/OpenTEC-Hub-kla-E7-20261007`, versão `0.26.5-dev.83+82e23ca`, commit de fontes `82e23ca716e6736614be4aa06212e3adaea47de0`, Release/win-x64 autossuficiente, sem ReadyToRun. Manifesto de 437 arquivos em `build-receipt.json`; DLL publicada idêntica à DLL usada nos testes Release. Verificação dessa compilação: 39 testes de núcleo/API e 14 de layout aprovados. Há avisos de estilo não fatais; o publish terminou com sucesso. A pasta anterior `build/OpenTEC-Hub` e os arquivos do cultivo são preservados. O recibo distingue commit das fontes, build e critérios ainda pendentes. Esta é uma candidata de revisão das entregas A/B do plano, não liberação biótica C/D/E.

Ajuda contextual atualizada; procedimento consolidado em [KLA_ASSAY.md](../../processes/KLA_ASSAY.md).

## Bancada ainda necessária

Antes de liberar atuação biótica supervisionada, registrar versão do app/firmware, arranjo A/B/C e isolamento de N₂, calibração de OD/vazão/agitação, parâmetros editados e condição de controle inicial. Verificar desvio mantendo v_flow aberto, confirmação do retorno de ar e retomada da agitação/controlador. Repetir cancelamento em preparação, consumo, comutação, reoxigenação e recuperação, além de perda de eco/OD. Cada caso deve terminar com retorno medido confirmado ou falha explícita que bloqueie a fila. Preservar dados completos e eventos, inclusive falhas.

Também permanecem pendentes a validação experimental de sonda/modelo em novos cultivos, cobertura de incerteza além do IC condicional e a avaliação interativa de teclado/DPI no PC operacional. Não inferir esses resultados de mock, replay ou captura de tela.

## Seleção da pasta de dados

Leitura da configuração de workspace em 07/10/2026: `C:\Users\vitor\Downloads\Nova pasta`. A build lê a pasta selecionada pelo aplicativo, não presume que a pasta do executável é a pasta do cultivo. Não se alterou essa seleção. Para usar dados/calibrações do cultivo de 06/10, conferir a seleção na interface. Nenhum arquivo do cultivo foi usado para teste de migração.

## Complemento de ruído e apresentação

Auditoria exploratória de 160 trajetórias sintéticas concluída, com viés, dispersão, cobertura condicional e recusas por cenário. Resultados e limites em [NOISE_AUDIT.md](NOISE_AUDIT.md). Sem mudança dos critérios do núcleo. A apresentação do IC foi corrigida para não mostrar como disponível o intervalo de uma taxa recusada pelo balanço. Dois testes específicos passaram; um caso novo eleva a contagem distinta para 397. Pacote complementar produzido em `Windows_app/build/OpenTEC-Hub-kla-E7-20261007-b`, versão `0.26.5-dev.86+735dcc1`, commit de fontes `735dcc1970da4d84f90b966e3c18c7adaacbe5c1`. Os 16 testes de revisão/layout em Release passaram e a DLL testada é idêntica à publicada. Manifesto de 437 arquivos em `build-receipt-b.json`. O publish complementar não executou analisadores; a compilação anterior registrou os avisos de estilo. Build original e primeira candidata preservadas.
