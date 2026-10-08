# R4.2 — Eco do desvio biótico no simulador integrado

A regressão durante R5.2 registrou 2370 aprovações e uma falha: o cenário biótico normal esgotou a espera pelos comandos de recuperação em KlaRecipeAssayExecutionTests, linha 181 da revisão anterior. [TRX diagnóstico](../receitas-r52/evidence/recipes-r52-initial-capture-full.trx).

O teste aguardava qualquer primeiro comando antes de emitir o eco do desvio. BeginBioticRemoval emite motor antes de gás. DispatchFlowOrAbort estabelece o mínimo de confirmação a partir do último ID observado; um eco precoce de gás pode se tornar a linha de base e deixar o teste esperando um ID posterior que seu roteiro não fornece. A falha observada é compatível com essa janela; o TRX não preserva a sequência de comandos para provar que foi sua única causa.

O roteiro agora aguarda o CommandSent de vazão na fase DivertingAir antes de emitir o eco do desvio. O sinal usa continuations assíncronas. Prazo de três segundos, limites científicos, número de amostras e critérios de confirmação permanecem iguais. Nenhuma alteração no runner ou no protocolo físico.

Os dez cenários focados da execução de pulso passaram após a correção. A regressão completa final é registrada no recibo da captura R5.2. Este ajuste corrige a ordem do roteiro de teste; não atribui qualificação física à execução simulada.
