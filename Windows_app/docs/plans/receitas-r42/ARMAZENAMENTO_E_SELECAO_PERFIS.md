# R4.2 — Armazenamento e seleção de perfis

Implementado armazenamento de versões imutáveis de perfis operacionais. Cada registro contém schema, identidade de instalação/protocolo/versão e hash do conteúdo. A gravação usa o escritor comum, barreira durável e leitura de confirmação, com reserva exclusiva do catálogo. Repetir conteúdo idêntico é idempotente; alterar a mesma versão exige uma nova versão. Arquivos inválidos, duplicados, renomeados, desconhecidos ou truncados impedem a carga do catálogo. O hash detecta alterações acidentais; não é assinatura ou comprovação de qualificação de bancada.

O catálogo valida todos os registros antes de publicar capacidades no registro operacional. A publicação em lote é atômica: conflitos não deixam perfis parcialmente habilitados. Perfis vencidos permanecem no histórico e deixam de ser disponíveis. Registro de outra instalação ou ambiente físico é recusado nesta entrega isolada.

O editor de Determinar kLa permite escolher explicitamente o perfil disponível para o protocolo ativo, exibe validade e limites e mantém os limites já configurados pelo usuário. Nenhum perfil é escolhido automaticamente. Uma referência ausente/vencida continua salva na receita e aparece como indisponível. Os campos contextuais permanecem ativos conforme protocolo e modo único/múltiplos; os identificadores são apresentados pelo seletor.

Validação focada: 58 testes aprovados, zero falhas (`evidence/recipes-r42-profile-editor-focused.trx`), incluindo reabertura, corrupção, gravação, isolamento, publicação atômica e edição/serialização. Comando: `dotnet test Windows_app/tests/OpenTECHub.Tests/OpenTECHub.Tests.csproj -p:SelfContained=false -p:EnableSourceLink=false --no-restore` com filtro dos perfis e editor de receitas. SourceLink foi desativado somente na execução por bloqueio do arquivo gerado no OneDrive.

Uma primeira regressão foi interrompida sem resultado final e registrou falha no teste biótico de cancelamento. O grupo de execução de pulsos foi reexecutado isoladamente e passou; uma nova regressão completa foi iniciada. A execução interrompida não constitui evidência de aprovação.

Pendências de R4.2: ligação do catálogo/provedor ao aplicativo, configuração explícita da instalação/cultivo e carga de perfis; resultados/progresso e navegação receita→sessão; pausa do bloco independente; verificação visual. Esta entrega não habilita atuação física nem conclui R4.2.
Regressão completa final: 2281 testes aprovados, zero falhas, 58 s. Evidência: `evidence/recipes-r42-profile-editor-full-final.trx`. Compilação XAML e testes de ViewModels aprovados; verificação visual ainda pendente.
