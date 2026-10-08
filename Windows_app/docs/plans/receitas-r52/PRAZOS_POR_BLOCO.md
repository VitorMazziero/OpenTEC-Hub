# Prazos de confirmação por bloco

Quando a configuração congelada contém critérios de conclusão, o executor usa `TimeoutSeconds` para a confirmação final e para a operação independente de retorno. O retorno mantém seu próprio cancelamento e sua própria contagem de prazo; não herda o token já cancelado da trajetória. Preparação e intervalo de despacho continuam sendo parâmetros da composição.

Configurações antigas sem critérios usam os prazos fornecidos ao executor. Dessa forma, um prazo padrão menor não encerra prematuramente uma rampa cujo bloco autoriza uma espera maior.

O teste integrado aguarda o envio da referência final e só então libera feedback após 200 ms, com prazo padrão de 50 ms e prazo de bloco de 1 s. Confirma o resultado persistido antes do avanço ao próximo bloco. O teste de suspensão aguarda qualquer referência intermediária válida, pois o relógio real pode avançar entre a preparação e o primeiro despacho; as verificações de congelamento e ausência de comandos durante a pausa permanecem.

A composição do aplicativo e os demais cenários de encerramento continuam pendentes. Esta entrega não encerra R5.2 nem qualifica equipamentos.
