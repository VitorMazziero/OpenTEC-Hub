# R5.2 — Evidência do retorno após interrupção

O resultado terminal de versão 2 inclui `Recovery`, separado das confirmações do alvo final da rampa. Para registrar `RestoredSnapshot`, exige a identidade da captura inicial, confirmação de todas as referências anteriores e o mesmo estado completo da cascata capturada, quando aplicável.

Referências positivas dos destinos diretos exigem leitura de processo dentro da tolerância. Desligamento pode usar leitura da referência zero; a cascata usa confirmação da referência do controlador. Aceite do transporte não comprova recuperação. As confirmações precisam estar entre a captura inicial e o término registrado.

Novas gravações de retorno restaurado sem essas evidências são recusadas. Históricos de versão 1 continuam legíveis, mas `HasVerifiedRecovery` permanece falso quando não têm a nova prova. Campos adicionais ausentes não mudam a serialização dos demais resultados antigos.

66 testes focados e 2428 testes na regressão completa passaram. O novo cenário rejeita referência do alvo final no lugar da anterior, captura incorreta, destinos ausentes, transporte sem leitura, leitura fora da tolerância e confirmação anterior à captura; também verifica a leitura de histórico antigo sem promovê-lo a retorno verificado. Evidência: `evidence/recipes-r52-recovery-evidence-full.trx`.

O aplicativo compilou em Release com zero erros, em `D:/Temp/OpenTECHub-ramp-recovery-evidence-release/`.

A execução da restauração, a conexão do ciclo ao engine e a habilitação do bloco permanecem pendentes. Este incremento fecha a exigência de evidência no armazenamento, sem declarar retorno físico qualificado.
