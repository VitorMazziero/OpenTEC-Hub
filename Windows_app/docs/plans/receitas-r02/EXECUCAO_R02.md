# R0.2 — Invocação de receita e pulso E6

Base: `be213dc`. Data: 07/10/2026.

Implementado `KlaRecipePulseMapper`/`KlaRecipePulseBinding`: cada condição/réplica/tentativa gera um request Single, com identidade estável independente do payload, hash da invocação, política, snapshot e instalação. Reenvio preserva o ID; parâmetros diferentes são recusados pelo diário E6. A fila interativa e seus defaults não são copiados para execução autônoma. Prazo de remoção incompatível com orçamento é recusado, sem redução silenciosa.

Capacidades verificam instalação, perfil/versão e protocolo antes de reservar exposição. A declaração de capacidade física não abre a passagem biótica E7. Nenhum adaptador de produção foi registrado nesta etapa.

Diário E6 agora grava envelope v2 e lê arrays legados, sem atribuir identidade de receita ou restauração inexistentes. Versões desconhecidas são recusadas. `PersistenceFailed` foi acrescentado sem renumerar estados existentes; erro terminal de disco preserva a evidência física do resultado e bloqueia novas solicitações.

Conclusão de pulso exige retorno confirmado também para abiótico. Requests de receita exigem o ID do snapshot restaurado e recibo de persistência. A política aceita `Valid` por padrão; `Conditional` requer motivos explicitamente autorizados e OUR obrigatório segue sua qualidade própria. `OperatorDecision` não é alterado. O timer de deadline usa `TimeProvider`; aguardar término tem operação própria e cancelar observação não cancela aquisição.

Validação:

```powershell
dotnet test Windows_app/tests/OpenTECHub.Tests/OpenTECHub.Tests.csproj -p:SelfContained=false --no-restore --filter "FullyQualifiedName~KlaRecipePulseTests|FullyQualifiedName~KlaAssayApiTests|FullyQualifiedName~RecipeExecutionContractTests" -v quiet --logger "trx;LogFileName=recipes-r02.trx" --results-directory Windows_app/docs/plans/receitas-r02/evidence
```

**61 aprovados, 0 falhas, 0 ignorados**, incluindo migração, mudança de payload, capacidades incompatíveis, quatro combinações de protocolo/captura, qualidade, falha de disco, snapshot incorreto, deadline injetado e observação cancelada. Evidência: `evidence/recipes-r02.trx`. Duas falhas iniciais eram expectativas de tipo de exceção mais estreitas que os erros reais de JSON/arquivo em Windows; foram corrigidas mantendo as verificações do bloqueio e da evidência.

Limites: o recibo ainda é um contrato fornecido pelo executor; barreira durável real por sessão pertence a R3.1. Captura automática e restauração do snapshot pertencem a R1.3, adaptação de autoria/contadores a R2.2. Próxima etapa: R1.1, reservas por bloco e transferência atômica no árbitro comum.
