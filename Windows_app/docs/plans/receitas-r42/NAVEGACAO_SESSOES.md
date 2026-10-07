# R4.2 — Navegação receita → sessão

A página de receitas acompanha os resultados terminais do engine por invocação. O painel “Resultados kLa” preserva conclusão, ressalvas, inconclusão, cancelamento e falhas, com número de tentativas/seleções, contexto do cultivo/bloco/disparo, motivo, retorno e gravação independentes. A atualização ocorre nos eventos de estado/bloco e no acompanhamento do tempo; uma nova execução remove registros antigos da lista desta execução.

“Abrir sessão e tentativas” usa a identidade da sessão armazenada e o visualizador comum. O Shell só navega quando a sessão solicitada foi carregada. Resultados terminais são usados para evitar reabrir manifestos de aquisição em andamento. A proteção existente contra trocar um ensaio manual em execução permanece no visualizador.

O teste de navegação verifica falha de restauração visível, gravação independente, deduplicação, abertura da sessão correta, recusa de resultado externo à lista e limpeza para nova execução. A ligação do provedor ao aplicativo, progresso durante aquisição, configuração de instalação/cultivo, pausa independente e verificação visual ainda pertencem às pendências de R4.2.

Validação focada: 86 testes aprovados, zero falhas (`evidence/recipes-r42-navigation-focused.trx`). Compilação do aplicativo em Release com `dotnet build Windows_app/src/OpenTECHub/OpenTECHub.csproj -c Release -p:EnableSourceLink=false --no-restore`: zero erros, 1211 avisos de análise/compilação. Nenhuma verificação visual ou física foi promovida a partir dessa compilação.

Regressão completa desta revisão: **2308 aprovados, zero falhas e zero ignorados**, em `evidence/recipes-r42-navigation-full.trx`. Executada com `-p:EnableSourceLink=false -p:SelfContained=false --no-restore`. A opção SourceLink é exclusiva da invocação, para contornar o arquivo gerado bloqueado pela sincronização do workspace.
