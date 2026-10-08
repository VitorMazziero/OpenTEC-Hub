# R5.2 — Preparação coordenada

`RecipeResourceCoordinator.PrepareRampAsync` serializa a preparação com os ensaios. Confere identidade e recursos, recusa conflito com outro produtor e suspende apenas a cascata associada à referência de O₂. Reserva e drena os destinos antes de capturar o estado inicial.

A captura deve corresponder à execução, invocação, bloco, configuração e estado da cascata suspensa. Depois de gravar e reler o estado inicial, o coordenador verifica novamente a autoridade e o controle, registra o produtor da rampa e libera a reserva. Só então retoma a cascata. A espera e a preparação não acumulam tempo ativo da rampa.

Falha de armazenamento ou captura incompatível não registra um produtor executável. Uma reserva drenada é liberada; uma drenagem que falha permanece fechada até recuperação/parada segura. Emergência ou perda de autoridade impede retomada do controle.

Quatro testes focados e 2427 testes na regressão completa passaram: preparação durável seguida de suspensão pelo ensaio, falha de armazenamento, captura de outra invocação e captura da referência da cascata selecionada enquanto pausada. Evidência: `evidence/recipes-r52-preparation-full.trx`.

O aplicativo compilou em Release com zero erros, em `D:/Temp/OpenTECHub-ramp-preparation-release/`.

A ligação desta preparação ao executor do bloco e a recuperação após cancelamento ainda estão pendentes. Este incremento não habilita a execução da rampa no aplicativo.
