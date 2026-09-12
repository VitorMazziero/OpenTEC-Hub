# Notas importadas — pump

Comentários extensos retirados do firmware ativo durante a reorganização.
O texto abaixo preserva o contexto histórico/técnico do monólito; valide-o
contra hardware antes de tratá-lo como especificação atual.

## Configuração e estado global

> Hub command acknowledgement (v4).
> 
> The hub keeps re-delivering a command until it sees this id come back on the data
> push, so the same JSON arrives several times by design. Applying it twice would be
> wrong for anything that resets the profile clock - mode, init_t and final_t all call
> resetOperationState() - so a repeat is acknowledged and then ignored.
> 
> 0 means "nothing applied yet"; the hub never issues id 0.

## storage/RuntimeStateStore.h

> Calculate Trigger Time
> current_t_min = (millis() - trigger) / 60000
> trigger = millis() - (current_t_min * 60000)
> We assume 'savedTime' is the time we were at.

