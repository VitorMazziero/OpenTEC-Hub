# R5.2 — Confirmação da referência de O₂ da cascata

O componente `RecipeRampCascadeDestination` liga o executor da trajetória ao controlador ativo pelo engine. Aceita a linha de referência da cascata; componentes para os destinos diretos permanecem pendentes. O componente deve operar dentro de `RecipeRampGuardedDestination`, que mantém a barreira do produtor durante aplicação e confirmação.

A confirmação produz evidência tipada `ControllerReference`. Exige alvo final previamente aplicado pelo componente, referência correspondente no controlador, receita em execução, posse de N/Q/O₂ e barreira da cascata aberta. Não usa o OD medido nem o valor do monitor de O₂; não envia comandos para confirmar.

As chamadas ficam vinculadas à execução capturada ao criar o componente. O engine verifica essa identidade sob sua própria trava, junto da aplicação ou confirmação. Assim, a mudança de execução não deixa uma janela para aplicar referências antigas na receita nova.

Cada nova aplicação ou tentativa de confirmação limpa o recibo anterior. Suspensão pelo ensaio, mudança de referência, perda de posse e pausa não promovem conclusão. A barreira de rampa respeita um destino que devolve confirmação pendente, em vez de transformar esse retorno em sucesso.

O seletor de controle associado no editor permanece restrito a uma linha com parâmetro O₂ e destino Referência da cascata. Os demais parâmetros e o monitor de O₂ não exibem associação.

Validação: 44 testes focados aprovados (engine, destino protegido e persistência terminal). Cobertura nova inclui confirmação sem aplicação anterior, referência divergente, controlador ausente, ensaio ativo, pausa, posse perdida, execução diferente e confirmação pendente do destino. Release compilado em `D:/Temp/OpenTECHub-ramp-cascade-confirmation-release/`, com 0 erros e 1495 avisos. A regressão completa não foi repetida neste incremento.

Integração do ciclo de vida, consumo do recibo na gravação terminal e componentes de confirmação para destinos diretos continuam pendentes. A execução de rampas permanece bloqueada; esta entrega não qualifica hardware.
