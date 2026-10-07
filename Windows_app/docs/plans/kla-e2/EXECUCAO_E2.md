# E2 — aquisição qualificada e controle

Data: 06/10/2026. Implementação de software; comprovação física em bancada pertence a E7.

## Valores de preparação

N=50–1000 rpm, Q=0,5–16 L/min, OD usual=30–100%, remoção a 100 rpm e alvo de 5–20% são **padrões editáveis**, não constantes universais. A sessão guarda a faixa escolhida pelo usuário. Os contratos aceitam outras faixas finitas e coerentes. E4 apresentará todos os novos campos no layout comum; os campos existentes continuam na interface atual.

Para iniciar o biótico, a definição deve conter OD alvo, tempo máximo sem ar, tempo máximo de recuperação e faixa de retomada. A sonda pode ser de qualquer tecnologia; τ conhecido é opcional. O tempo de resposta conhecido entra na antecipação da retomada, junto com o prazo de confirmação do comando. A ausência de τ não significa sonda ideal.

## Aquisição

- Somente quadros com `OxygenUpdated`, OD calibrado/ADC finitos e não negativos e um novo instante monotônico entram na série de OD. Zero calibrado é uma leitura válida.
- Quadros de outros canais continuam fornecendo confirmações e são registrados no diário, sem duplicar OD na série global ou bruta.
- O prazo do canal de oxigênio é independente do heartbeat geral. Telemetria de outros sensores não prolonga esse prazo.
- `aquisicao.json` guarda coeficientes da calibração, UTC, prazo de OD e condição inicial de retorno. Mudança de calibração durante uma corrida interrompe a medida.
- Pedidos/confirmacões de gás e mudanças de fase registram tempo monotônico, IDs/eco e latência no diário. O CSV v2 permanece compatível.

## Execução biótica

1. Verificar conexão, OD novo, equilíbrio inicial na faixa configurada, servo medido, ar estável no reator e eco do fluxômetro.
2. Exigir `nitrogenIsolationConfirmedUtc`: confirmação da **isolação física da fonte de N₂**. B/C compartilham uma saída; abrir escape não comprova fechar uma fonte independente.
3. Guardar vazão inicial e agitação de retorno (informada ou medida). Suspender cascata e OUR; obter posse apenas de agitação e aeração.
4. Comandar a agitação de remoção configurada e desviar a vazão inicial para B/C, com `v_Flow=0` (bit do protocolo: válvula principal aberta). O fluxômetro permanece ligado.
5. Confirmar o desvio, medir o decaimento e retomar no primeiro alvo/queda/tempo atingido ou projetado. O prazo não pode ser prolongado para melhorar a curva.
6. Comutar para A e aplicar N/Q da condição de teste. Registrar confirmação como origem da recuperação.
7. Após recuperação estável na faixa configurada, ou cancelamento/timeout/falha de aquisição, solicitar a condição inicial de retorno. Cancelamento biótico não chama fechamento geral nem motor zero.
8. Confirmar rota, eco, vazão medida, servo medido e OD estável na faixa. Retomar cascata quando anteriormente engajada e OUR com histórico interrompido.
9. Liberar revisão somente depois da confirmação. `estado-fisico.json` guarda retomada pendente/confirmada/falha, inclusive após reabertura.

Perda de comunicação/posse ou ausência de confirmação retorna falha explícita. O aplicativo não pode confirmar uma recuperação sem telemetria. Falha bloqueia nova corrida nessa sessão. Reinício não dispara automaticamente um pulso. Encerramento durante pulso solicita retorno quando possível e registra falta de confirmação.

## Concorrência e compatibilidade

Receitas e outros proprietários não são deslocados silenciosamente. O início com posse de receita é recusado; a pausa atual de receitas não é tratada como suspensão segura de um bloco em andamento. Integração periódica pertence a E6.

A cascata fica sem computar sobre a perturbação e volta com inicialização sem salto. OUR conserva sua integral acumulada, interrompe continuidade e aquece novamente após retomada. Alterações de configurações não relacionadas ao OUR não recriam seu sensor. Configuração científica do próprio OUR mantém a política anterior de reinicialização explícita.

O abiótico mantém N₂/preparação por C e fechamento confirmado antes da revisão. Não inicia sobre cascata engajada. Novas fases foram acrescentadas ao fim do enum, preservando valores anteriores.

## Validação e pendências

48 testes específicos passaram na primeira validação isolada: runner, OUR e cascata. A regressão conjunta aprovou 306 testes; a verificação final específica aprovou 77, incluindo a agitação de remoção editável no abiótico. Evidências e hashes estão em [validation-receipt.json](validation-receipt.json).

Não houve envio de comandos a equipamento real. E4 ainda deve expor protocolo, confirmação da isolação de N₂, configuração e retomada na tela comum. E7 deve verificar em bancada a montagem, latências reais, confirmação do servo/fluxo, transferência residual e saídas de falha. Esses itens não estão marcados como concluídos.
