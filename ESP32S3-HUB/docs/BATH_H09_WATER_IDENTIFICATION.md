# H09 — Identificação e sintonia com água

**Status:** `NOT_RUN / PENDING_H08`  
**Objetivo:** obter parâmetros aprovados para a cascata externa sem confundir ensaio de água com
validação de cultivo.

## Pré-condições e segurança

- Usar somente água, com parada de emergência disponível; não iniciar com cultura.
- H08 P01–P08 e os gates G1–G9 devem estar aprovados.
- Registrar o commit do Hub (`10.5.0-dev`), o firmware r3 e a configuração completa.
- Os defaults (`Kp=0,5`, `Ti=600 s`, bias `+0,6 °C`) são apenas ponto de partida, não valores de produção.

## Procedimento

1. Registrar volume, vazão, agitação, temperatura ambiente, isolamento e posição dos sensores.
2. Fazer degraus positivos e negativos de `0,5`, `1` e `2 °C`, sem exceder os limites do C404.
3. Estimar ganho, atraso morto e constante de tempo a partir de `Tempval` (PV do reator) e do
   `BathCommandSetpoint`; manter `BathPv` como diagnóstico independente do C404.
4. Começar com `Ki=0`; somente depois aplicar integral lenta (`Ti` crescente), verificando erro
   estacionário, sobressinal, tempo de acomodação e comandos por hora.
5. Repetir com perda/retorno de rede, perda/retorno de PV e saturação para confirmar pausa,
   anti-windup e retomada sem degrau.

## Ficha mínima de dados

| Campo | Registro |
|---|---|
| Identificação | ensaio, data/hora, operador, commit, Hub/nó |
| Processo | volume, vazão, agitação, ambiente, isolamento |
| Sinais | timestamp, `TempSetpoint`, `Tempval`, validade/stale, `BathPv`, `BathCommandSetpoint` |
| Eventos | ACK, `done`, pausa, fault, saturação, perda de rede/PV |
| Resultado | ganho, atraso, constante de tempo, sobressinal, acomodação, comandos/hora |

## Critérios de aceite

- erro estacionário inferior a `0,2 °C` após sintonia aprovada;
- sem oscilação sustentada e dentro dos limites absoluto, relativo e de slew;
- intervalo mínimo entre comandos respeitado e acionamentos/hora documentados;
- falha de PV/rede congela a integral e retoma sem degrau;
- parâmetros finais (`Kp`, `Ti`, bias, período, filtro, limites e banda) aprovados e assinados.

Nenhum valor de sintonia deve ser apresentado como produção antes da ficha e da aprovação do
responsável pelo processo.
