# Hardware — Frasco agitador

Driver do motor, direção, PWM e potenciômetro local. CAD em `hardware/cad/source/agitator`.

O firmware v10 usa PWM nos GPIO 25/26, enables nos GPIO 27/13 e potenciômetro no GPIO 36. Ele não implementa nem reivindica entradas de corrente nos GPIO 34/35; qualquer instrumentação futura depende de confirmar primeiro o circuito montado.

Pinagem, níveis elétricos, alimentação e direção dos atuadores permanecem definidos pelo firmware ativo e pelos arquivos técnicos importados. Não foi feita alteração elétrica nesta reorganização. Antes de gravar, conferir variante da placa, alimentação, terra comum e estado seguro das saídas.
