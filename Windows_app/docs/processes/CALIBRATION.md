# Calibração e controle de pH

Este documento é o contrato de implementação e o procedimento operacional da página
**Calibrações**. Ele foi reconstruído a partir do aplicativo Windows v.6, do protocolo
JSON e do firmware ESP32-S3. A interface nova conserva as equações e as chaves de v.6,
mas recusa entradas inválidas e separa claramente **medir**, **calibrar** e **atuar**.

## 1. Três ciclos de vida diferentes

| Canal | Onde a curva é aplicada | O que chega ao módulo | Persistência |
|---|---|---|---|
| pH | No parser do aplicativo: `pH = slope * raw + intercept` | Cada valor calibrado aceito volta como `{"pHCal":"6.98"}` para o display/controlador | `Calibration.PHSlope`, `PHIntercept` |
| Oxigênio | No parser do aplicativo: `O2 = max(a * raw + b, 0)` | Não existe comando de coeficientes em v.6; `oxygenMonitor` é controle/monitoramento, não calibração | `Calibration.OxygenA`, `OxygenB` |
| Vazão de ar | No firmware dedicado do fluxômetro v12.0 | Dois segmentos completos + `flowTransitionVoltage` | Pontos certificados ficam no aplicativo; curva e `Vt` são enviados explicitamente ao equipamento |
| Bomba externa | No firmware da bomba v3.12 | `pumpA1..pumpC2`, `pumpTransitionSpeed` | Perfis por mangueira ficam no PC; o nó persiste somente a última curva enviada |

Consequência: `pHCal` não habilita a bomba e `pHSetpoint` não calibra a sonda. São
mensagens diferentes, ainda que ambas façam parte do subsistema de pH.

## 2. Por que o pH estava somente para leitura

A primeira fase da interface expôs pH como KPI porque a aquisição, o filtro, a
calibração e o eco `pHCal` já existiam. O painel de controle foi adiado, embora o
protocolo e o firmware já implementassem o comando completo de v.6. Esse adiamento
deixou o aplicativo incoerente: ele entregava ao módulo o valor calibrado usado pelo
controle, mas não dava ao operador acesso aos parâmetros do próprio controle.

A correção é um comando atômico com cinco campos:

```json
{
  "pHSetpoint": 7.0,
  "pHError": 0.17,
  "pHOperation": 5.0,
  "pHMix": 20.0,
  "pHIntensity": 500.0
}
```

`pHIntensity` conserva a transformação de v.6: velocidade da bomba em porcentagem
vezes 10. Desligar envia o mesmo estado completo, com `pHSetpoint:0` e
`pHIntensity:0`; assim não sobra um parâmetro antigo escondido no controlador.

Faixas validadas antes do envio, conforme firmware:

- referência: 1 a 14 pH; zero somente como codificação de desligado;
- banda inativa: maior que 0 e menor que 2 pH;
- tempo operando e tempo de mistura/repouso: inteiros de 1 a 999 s;
- velocidade apresentada: 0 a 99%, enviada como 0 a 990.

Texto vazio, valor não numérico ou fora da faixa bloqueia o comando. O aplicativo não
repete o comportamento antigo de substituir silenciosamente uma digitação inválida
por pH 7.

## 3. Calibração de pH

### 3.1 Pré-condições

1. Conexão ativa, módulo de sensores online e leitura bruta de pH válida.
2. Controle de pH suspenso. A interface envia o estado completo de desligamento antes
   de iniciar; uma sonda em tampão fora do reator nunca pode acionar dosagem.
3. Tampões válidos, dentro do prazo e à temperatura conhecida; sonda lavada entre os
   pontos e apenas seca por contato, sem fricção.
4. O operador confirma cada troca de solução. Nenhuma etapa muda de tampão sozinha.

### 3.2 Aquisição reproduzida de v.6

- janela de estabilidade padrão: 20 quadros aceitos;
- critério padrão: desvio-padrão amostral bruto menor que 5 contagens ADC;
- média final: 20 quadros aceitos;
- cada quadro de telemetria conta uma vez. Não são duplicados valores enquanto se
  aguarda a próxima emissão do equipamento.

Esses critérios e os filtros de spike usam contagens ADC brutas. Aplicar uma curva não
reescalona os filtros. A interface mantém esse aviso visível; qualquer conversão para
unidades de engenharia exige comparação de bancada antes de mudar o comportamento de v.6.

Na calibração de dois pontos, para referências `r1`, `r2` e médias brutas `x1`, `x2`:

```text
slope     = (r1 - r2) / (x1 - x2)
intercept = r1 - slope * x1
```

Médias indistinguíveis recusam a calibração. v.6 substituía esse caso por `slope=1`,
o que podia instalar uma curva sem significado físico.

Na calibração de um ponto, a inclinação vigente é mantida:

```text
intercept = referência - slope_atual * média_bruta
```

O resultado permanece **proposto** até o operador pressionar **Aplicar no app**. Só
então os coeficientes são persistidos, o parser passa a usá-los e o fluxo normal de
telemetria envia o próximo `pHCal` ao módulo.

Perder a conexão tanto durante a espera pelo tampão quanto durante a aquisição encerra o
procedimento como recusado. A bomba permanece desligada e nenhum coeficiente é alterado.

## 4. Calibração de oxigênio

v.6 expõe diretamente `a` e `b`; não há assistente dedicado no código de referência.
A interface nova apenas guia a obtenção desses mesmos dois coeficientes lineares:

1. estabilizar no primeiro padrão (normalmente 0%);
2. capturar a leitura bruta aceita;
3. estabilizar no segundo padrão (normalmente 100%);
4. capturar a leitura bruta aceita;
5. revisar e aplicar explicitamente.

A captura é direta e usa o quadro aceito atual; v.6 não fornece um assistente de
estabilidade de O2. A estabilização física do zero/span continua sendo responsabilidade
explícita do operador e do padrão usado.

Para referências `r1`, `r2` e leituras brutas `x1`, `x2`:

```text
a = (r2 - r1) / (x2 - x1)
b = r1 - a * x1
```

Pontos repetidos, não finitos ou com referências iguais são recusados. Aplicar altera
somente o parser do aplicativo; não existe um comando de calibração de O2 no protocolo
v.6.

## 5. Calibração da vazão de ar

### 5.1 Captura de um ponto

1. Informar a vazão real indicada pelo padrão externo.
2. **Preparar ponto** envia o estado de v.6: fluxômetro habilitado, setpoint inicial
   igual à vazão de referência, válvulas auxiliar e N2 fechadas e `v_Flow` derivado.
3. Ajustar o setpoint para cima/baixo até o padrão externo atingir a referência.
4. **Capturar tensão** coleta 10 quadros distintos de `FlowVoltage` e grava a média.
5. Repetir em toda a faixa de trabalho e em ambos os lados da tensão de transição `Vt` escolhida.

Durante a captura, seleção, edição e remoção de pontos ficam bloqueadas. Se o link cair,
a média parcial é descartada e o setpoint preparado passa a estado desconhecido; depois de
reconectar é obrigatório preparar o ponto novamente.

O valor real vem do padrão externo; `FlowRate` do próprio equipamento não é aceito
como verdade de calibração.

### 5.2 Curva do fluxômetro v12.0

A regressão usa `x = tensão` e `y = vazão real`, com divisão editável em `Vt`. O valor `0.0545 V` é somente o default e a migração de curvas antigas:

- `x <= Vt`: segmento inferior quártico, com os termos efetivamente ajustados conforme os pontos disponíveis;
- `x > Vt`: quadrático com 3 ou mais pontos; linear com exatamente 2 pontos
  (`k2 = 0`).

```text
y = k*x^2 + f*x + c
```

Os pontos, `Vt` e o gráfico são revisados antes de **Salvar e enviar curva**. O aplicativo
envia atomicamente os dois segmentos completos e a transição; não existe aplicação parcial.
A conclusão exige ACK, eco de `FlowTransitionVoltage` e CRC. **Parar ensaio de vazão**
envia o safe-stop completo e fecha ambas as válvulas.

### 5.3 Curva e perfis da bomba externa v3.12

1. Criar ou selecionar um perfil identificado pela mangueira; carregar o perfil altera
   apenas a proposta local e nunca envia comando.
2. Definir `St` e coletar pontos volumétricos em pelo menos duas velocidades distintas de
   cada lado. Cada ponto guarda `S`, duração, volume e `Q = V/(Δt/60)`.
3. O ajuste calcula os coeficientes dos dois segmentos com continuidade C0+C1 em `St`;
   `Qt=Q(St)` é somente um resultado:

```text
S <= St: Q = a1*S^4 + b1*S^3 + k1*S^2 + f1*S + c1
S >  St: Q = k2*S^2 + f2*S + c2
```

4. **Salvar perfil** grava nome, pontos, ajuste e curva congelada no PC, sem tocar no nó.
5. **Salvar e enviar curva** transmite os oito coeficientes e `St` em um quadro. Só após
   `PumpCommandPending=false`, nove ecos iguais e `PumpCalCrc` o app persiste a curva
   confirmada e grava o recibo.

Na primeira instalação não existe curva presumida: sem perfil e sem pontos válidos, o editor
mostra `—` nas equações, o gráfico não desenha linha e **Salvar e enviar curva** permanece
bloqueado. Cada segmento do gráfico ocupa somente a faixa de velocidades efetivamente medida,
como na aba do fluxômetro; não há extrapolação visual até `S=0` ou `S=1000`.

A frota é instalada com o contrato atual único. O aplicativo não negocia versões nem mostra
mensagens de “aguardando a versão do Hub” nesse cartão, e não lê, migra ou envia calibração
linear antiga. O envio é bloqueado somente por desconexão, bomba offline, operação
`RUNNING`/`WAITING`, comando pendente ou ajuste inválido/incompleto.

## 6. Estados de recusa e limite de validação

A interface distingue:

- **sem conexão**: nenhum procedimento que dependa de aquisição pode começar;
- **módulo offline / canal ausente**: há conexão, mas não existe amostra válida;
- **aguardando operador**: a próxima solução ou padrão precisa ser confirmado;
- **estabilizando / amostrando**: aquisição em andamento, cancelável;
- **resultado proposto**: calculado, ainda não aplicado;
- **aplicado no app** ou **enviado ao equipamento**: ação explícita concluída.

Testes automatizados e simulador comprovam cálculos, comandos e recusas. Eles não
substituem a verificação com tampões, analisador de O2, padrão externo de vazão e o
bioreator real.
