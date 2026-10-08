# Edição guiada dos blocos de kLa e rampa

O painel genérico estava ligado a `Fields`, ignorando a filtragem de `VisibleFields`. Foi corrigido: identificação e versão do perfil de kLa permanecem no documento, mas são escolhidas pelo seletor de perfil, sem entradas manuais duplicadas.

O kLa apresenta procedimento e ação após resultado inconclusivo, um grupo recolhível de repetição por qualidade e um grupo de limites do cultivo inicialmente aberto. Explicações esclarecem OUR, contagem da primeira tentativa, orçamento compartilhado no cultivo, intervalo entre ensaios e limites de tempo sem aeração. As unidades aparecem nos rótulos. Zero não herda limites do perfil: prazo do bloco e tempos máximos sem aeração exigem valores positivos. Nenhuma política ou limite de execução foi alterado.

Na rampa, o controle associado só aparece para uma linha de O₂ destinada à referência da cascata. Um seletor lista os blocos Controle de O₂ da receita; não escolhe automaticamente. Uma associação salva é preservada ao trocar de destino, mas fica inativa na configuração enquanto nenhuma linha usar a cascata. Um controle removido exige nova escolha.

Validação: 49 testes focados aprovados, incluindo troca de destino, associação removida, preservação do identificador, grupos sem duplicação e atualização dos motivos de repetição. Release compilado com 0 erros e 1382 avisos em `D:/Temp/OpenTECHub-recipe-guidance-release/`. A tentativa na saída habitual encontrou DLLs bloqueadas por processo aberto; nenhuma sessão foi interrompida. Verificação visual em execução permanece pendente; a execução de rampas continua bloqueada até a integração de ciclo de vida prevista em R5.1/R5.2.
