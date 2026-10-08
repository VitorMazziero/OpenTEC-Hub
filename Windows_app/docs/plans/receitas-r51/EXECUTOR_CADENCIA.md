# R5 — Executor de trajetória e cadência

LinearSetpointRampExecutor aplica quadros pelo tempo ativo comum. Cadência mínima usa delay monotônico depois do término do despacho; atraso não acumula comandos atrasados. Quadros quantizados iguais são deduplicados, mas a transição para alvo final é explícita. Suspensão do relógio impede novos quadros.

O destino deve revalidar rota/autoridade e aplicar o quadro sob a barreira de suspensão. False significa nenhuma referência aplicada. Somente um quadro final efetivamente aplicado segue para ConfirmFinalAsync; a conclusão aguarda essa confirmação e verifica cancelamento. O adaptador deve fornecer prazo limitado e confirmação própria de cada parâmetro. A interface não transforma envio em confirmação.

Dois testes focados aprovados com relógio virtual: atraso de cinco segundos produz apenas o quadro atual; pausa de duas horas mantém o tempo ativo; tempos finais diferentes chegam aos alvos; conclusão espera confirmação; quadro indisponível não conta como aplicado; cancelamento interrompe confirmação; executor não permite reutilização.

R5 permanece parcial: adaptador do engine/reservas, registro durável, editor e confirmações das rotas reais ainda pendentes. O relógio suspenso é condição de apresentação/cálculo; a exclusão final de comandos compete à barreira do destino. Não atribuir validação física a estes testes.

Release compilado sem erros. Suíte completa não repetida neste escopo; última regressão completa anterior: 2352 testes.
