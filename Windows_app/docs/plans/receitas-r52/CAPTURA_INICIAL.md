# R5.2 — Captura inicial das referências

RecipeRampInitialState congela referências e evidências com identidade de snapshot, execução e bloco. A captura direta exige uma reserva vigente de Recipe e barreira de transporte da geração atual. Não deduz temperatura, rotação, vazão ou OD comandados das leituras do processo. Registra aceitação de transporte como tal, sem promovê-la a confirmação física.

Para O₂ da cascata, CaptureRampInitialState do engine exige a cascata associada suspensa e sem passo em andamento. Captura a referência do PID e seu estado completo. Não usa OxygenMonitor nem OD medido como referência do controlador.

O árbitro agora permite capturar um subconjunto dentro da reserva, preservando a verificação de geração e drenagem. Isso permite combinar início atual confirmado com início explícito de outro parâmetro que ainda não tem estado comandado anterior. A política RestoreSnapshot exige todas as referências anteriores, mesmo em linhas com início explícito; ausência de estado impede essa modalidade.

Referências e comandos congelados são imutáveis e serializáveis. A trajetória usa o dicionário capturado; comandos posteriores não alteram esse início. Referência atual que atravessaria a faixa OFF proibida é rejeitada antes da trajetória.

Validação focada: 80 testes aprovados em rampas, autoridade, barreira e engine. Novos cenários cobrem os seis parâmetros diretos, leitura do processo diferente da referência, transporte atrasado, reserva antiga, subconjunto fora da reserva, roundtrip, início explícito sem referência anterior, política de restauração e captura da cascata pelo engine/coordenador reais.

Regressão completa final: **2371 aprovados, zero falhas e zero ignorados**, 1 min 25 s. [TRX final](evidence/recipes-r52-initial-capture-final-full.trx). Comando: `dotnet test Windows_app/tests/OpenTECHub.Tests/OpenTECHub.Tests.csproj --no-build --no-restore -p:EnableSourceLink=false -p:SelfContained=false -v quiet --logger "trx;LogFileName=recipes-r52-initial-capture-final-full.trx" --results-directory Windows_app/docs/plans/receitas-r52/evidence`.

A primeira regressão revelou uma janela de corrida no roteiro de eco do desvio biótico. [Diagnóstico e correção](../receitas-r42/SINCRONIZACAO_ECO_BIOTICO.md). O recibo e o TRX da execução com falha são preservados; o resultado final corresponde ao roteiro corrigido.

Release final: `dotnet build Windows_app/src/OpenTECHub/OpenTECHub.csproj -c Release -p:EnableSourceLink=false --no-restore -v quiet`; zero erros, 1417 avisos, 17,23 s.

Ainda falta conectar esta captura ao ciclo de vida executável do bloco, persistir registros de execução, aplicar a política de cancelamento e confirmar os alvos finais conforme a capacidade de cada destino. O bloco continua impedido de iniciar enquanto essa integração não estiver completa. Qualificação física permanece R6.2.
