# H10 — Operação e prontidão do banho externo

**Status:** `NOT_READY_FOR_CULTURE`  
**Motivo:** H08 físico e H09 de identificação/sintonia ainda precisam de aprovação formal.

## Versões mínimas e rastreabilidade

| Componente | Referência |
|---|---|
| Hub | `10.5.1-dev`, incluindo a auditoria adicional de segurança |
| Nó do banho | firmware r3.1, contrato `/bathData` e cadência integrada máxima de 2 s |
| Android app | contrato r3 validado nos commits já registrados |
| Windows app | integração ainda planejada; não é pré-requisito para estes ensaios do Hub |

Registrar no relatório o hash exato de cada binário carregado; não aceitar uma combinação
binário↔commit desconhecida.

## Operação normal

1. Iniciar pela via UART original e confirmar presença do nó antes de habilitar a via externa.
2. Selecionar `tempControlMode=1`; o Hub envia `100B`, confirma a desabilitação original e
   descarta o setpoint do mesmo quadro.
3. Enviar novo `tempSetpoint` do reator; monitorar `Tempval`, validade, `BathPv`, ACK, `done`,
   `BathCascadeState`, `BathCascadePausedReason` e limites.
4. Para retornar à via original, desabilitar a cascata, aguardar novo setpoint e só então
   reativar a UART. `tempSetpoint=0` zera a cascata, mas não desliga o C404.

## Contingências obrigatórias

- PV do reator stale: pausar e congelar a integral; não aplicar fallback.
- Nó offline, ACK ausente ou erro: não enviar novo comando; investigar antes de resetar.
- Guarda suspensa, abort ou falha persistente: bloquear a cascata até reset/novo comando válido.
- Reboot: não retomar automaticamente e exigir novo setpoint.
- Alteração manual no C404: registrar o evento; a guarda deve restaurar o alvo sem substituir
  `Tempval` nem a referência do reator.
- O C404 não possui desligamento remoto nesta integração. Qualquer desligamento físico exige
  intervenção local do operador.

## Checklist de liberação

- [ ] H08 P01–P08 e gates G1–G9 aprovados com logs brutos.
- [ ] H09 executado em água, parâmetros assinados e limites confirmados.
- [ ] Manual e contingências treinados com o operador.
- [ ] Hashes dos binários conferidos contra os commits.
- [ ] Revisão de segurança aprovada; compilação e testes não são, sozinhos, aprovação de cultivo.

Até todos os itens estarem marcados, a classificação correta é `NOT_READY_FOR_CULTURE`.
