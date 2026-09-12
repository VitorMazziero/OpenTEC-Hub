> **Nota (12/09/2026).** Documento físico do arranjo, promovido do rascunho `v2`. A implementação no
> aplicativo está em `2026-09-12-plano-valvulas-abc-ensaios.md` ([D-053](../DECISIONS.md),
> [P3-11](../history/PHASE_LOG.md)). A ligação elétrica descrita aqui — **MOSFET 1 → B + C, MOSFET 2
> → A** — é o **padrão configurável** do app (Configurações › Gás e válvulas, que mostra o
> fluxograma `valvulas.png`); se a bancada estiver ao contrário, muda-se lá, não no código.

# Sistema fixo de válvulas para ensaios de potência e determinação de \(k_La\)

## 1. Descrição geral

O sistema foi desenvolvido para permitir a utilização de uma mesma linha de entrada de gás e do mesmo aspersor do biorreator em dois tipos de experimento:

1. ensaios de potência, utilizando ar; e
2. ensaios de determinação do coeficiente volumétrico de transferência de oxigênio, \(k_La\), utilizando nitrogênio durante a etapa de desoxigenação e ar durante a etapa de reaeração.

A configuração também foi concebida para solucionar um problema operacional do fluxômetro de ar. Ao ser acionado um novo setpoint, o equipamento apresenta um pulso transitório de vazão que pode atingir aproximadamente **2 a 5 vezes o valor do setpoint** antes de estabilizar. A válvula C atua como uma linha de desvio/descarga, impedindo que esse pulso seja enviado diretamente ao biorreator.

Além disso, o acionamento elétrico foi simplificado para dois canais de potência:

- **MOSFET 1:** aciona simultaneamente as válvulas **B e C**;
- **MOSFET 2:** aciona a válvula **A**.

As válvulas B e C, portanto, possuem sempre o mesmo estado de acionamento. A válvula A opera no estado complementar.

---

## 2. Legenda dos componentes

- **1 — Fluxômetro:** responsável pela medição e controle da vazão de ar fornecida ao sistema.
- **2 — T da linha de ar:** ponto de derivação da linha de ar entre a alimentação do biorreator e a linha de descarga.
- **C — Válvula de saída:** válvula de descarga ou *bypass* da linha de ar. Sua principal função é permitir que o fluxo de ar seja desviado para a atmosfera durante o período de estabilização do fluxômetro, evitando que o pulso inicial de vazão alcance o biorreator.
- **A — Válvula de entrada:** controla a passagem do ar proveniente do fluxômetro para a linha comum que alimenta o biorreator.
- **5 — T de junção ar/N₂:** ponto de união entre a linha de ar e a linha de nitrogênio utilizada no ensaio de \(k_La\).
- **6 — Aspersor do biorreator:** responsável pela dispersão do gás no meio líquido.
- **B — Válvula de nitrogênio:** controla a alimentação de N₂ ao biorreator durante a etapa de desoxigenação do ensaio de \(k_La\).

---

## 3. Arquitetura de acionamento elétrico

O sistema possui dois estados lógicos principais, definidos por dois MOSFETs.

### Estado 1 — estabilização/desoxigenação

- MOSFET de **B + C: ligado**
- MOSFET de **A: desligado**
- B: aberta
- C: aberta
- A: fechada

Nesse estado, o ar proveniente do fluxômetro é descarregado pela válvula C, enquanto a válvula A impede sua entrada no biorreator. Quando a linha de N₂ está pressurizada, a válvula B permite simultaneamente a alimentação de nitrogênio ao reator.

### Estado 2 — ensaio com ar

- MOSFET de **B + C: desligado**
- MOSFET de **A: ligado**
- B: fechada
- C: fechada
- A: aberta

Nesse estado, a linha de nitrogênio e a linha de descarga ficam fechadas, e o ar estabilizado passa a ser direcionado ao biorreator.

A lógica de controle é, portanto, complementar:

\[
A = \neg(B=C)
\]

ou, em termos dos canais de potência,

\[
MOSFET_A = \neg MOSFET_{B+C}
\]

considerando a lógica de acionamento em que MOSFET ligado corresponde à abertura das válvulas associadas.

---

## 4. Função das válvulas no sistema

### 4.1. Válvula A — entrada de ar no biorreator

A válvula A define se a linha de ar está ou não conectada ao biorreator.

Quando A está aberta, o ar proveniente do fluxômetro atravessa o T da linha de ar, segue pela válvula A, alcança o T de junção ar/N₂ e é encaminhado ao aspersor do biorreator.

Quando A está fechada, o biorreator fica isolado da linha de ar. Essa condição é utilizada durante:

- a estabilização inicial da vazão do fluxômetro; e
- a etapa de desoxigenação com N₂ no ensaio de \(k_La\).

A válvula A impede, portanto, que o pulso inicial de vazão produzido pelo fluxômetro seja transmitido ao reator.

### 4.2. Válvula B — alimentação de nitrogênio

A válvula B controla a comunicação entre a linha de nitrogênio e o T de junção localizado antes do aspersor.

Como B é acionada pelo mesmo MOSFET de C, ela abre e fecha simultaneamente com a válvula de descarga.

No ensaio de \(k_La\), essa associação é funcionalmente conveniente: durante a desoxigenação, B permanece aberta fornecendo N₂ ao biorreator, enquanto C permanece aberta descarregando o ar até que o fluxômetro estabilize.

### 4.3. Válvula C — descarga e estabilização da linha de ar

A válvula C é o elemento utilizado para contornar o principal problema operacional do fluxômetro.

Ao aplicar um setpoint de vazão, o fluxômetro pode produzir um pulso inicial de aproximadamente **2 a 5 vezes o valor nominal definido**. Se esse pulso fosse encaminhado diretamente ao biorreator, poderia provocar:

- aumento transitório da vazão superficial de gás;
- alteração momentânea do regime hidrodinâmico;
- aumento transitório da retenção gasosa;
- perturbação da pressão da linha;
- alteração da dispersão de bolhas;
- distorção do instante inicial do experimento;
- introdução de erro na comparação entre diferentes condições experimentais.

Durante a estabilização, A permanece fechada e C permanece aberta. Assim, o ar fornecido pelo fluxômetro é descarregado pela linha de saída, sem atingir o biorreator.

Como B e C estão eletricamente associadas, B também permanece aberta nesse estado.

---

## 5. Ensaio de potência — sem utilização de nitrogênio

Nos ensaios de potência, a intenção é utilizar apenas ar.

Entretanto, devido ao acoplamento elétrico entre B e C, a abertura de C implica necessariamente a abertura de B. Portanto, para que a etapa de estabilização continue sendo efetivamente **sem N₂**, a alimentação de nitrogênio deve estar **isolada a montante** da válvula B, por exemplo com a fonte de N₂ despressurizada, regulador fechado ou válvula manual de isolamento fechada.

Essa condição é importante: se a linha de N₂ permanecer pressurizada, a abertura conjunta de B e C poderá introduzir nitrogênio no biorreator durante a estabilização do fluxômetro.

### 5.1. Estabilização da vazão

Com a fonte de N₂ isolada:

- A permanece **fechada**;
- B permanece **aberta**, porém sem fornecimento de N₂;
- C permanece **aberta**.

O ar segue o percurso:

\[
\text{Fluxômetro} \rightarrow 2 \rightarrow C \rightarrow \text{descarga}
\]

Nesse período, qualquer pulso inicial gerado pelo fluxômetro é descarregado por C.

A vazão é monitorada até que permaneça estável no setpoint experimental.

### 5.2. Início do ensaio de potência

Após a estabilização da vazão, os dois canais de acionamento são comutados para o estado complementar:

- A: **fechada → aberta**
- B: **aberta → fechada**
- C: **aberta → fechada**

O ar passa então a percorrer:

\[
\text{Fluxômetro} \rightarrow 2 \rightarrow A \rightarrow 5 \rightarrow 6 \rightarrow \text{biorreator}
\]

O início do ensaio é marcado operacionalmente pela combinação de dois eventos:

1. confirmação de que a vazão do fluxômetro se encontra estável no setpoint;
2. comutação das válvulas para A aberta e B/C fechadas.

Dessa forma, o transiente do controlador de vazão não é incorporado à condição experimental.

---

## 6. Ensaio de \(k_La\) — utilização de nitrogênio

O ensaio de \(k_La\) utiliza a mesma linha de entrada do biorreator, com alternância entre N₂ e ar.

A associação elétrica de B e C favorece justamente a etapa de transição entre desoxigenação e reaeração: enquanto o biorreator recebe N₂ por B, o ar pode ser estabilizado simultaneamente e descartado por C.

---

## 7. Etapa de desoxigenação e estabilização simultânea da linha de ar

Durante a etapa de desoxigenação:

- A permanece **fechada**;
- B permanece **aberta**;
- C permanece **aberta**.

Nesse estado, existem dois circuitos gasosos simultâneos e funcionalmente separados.

### Linha de nitrogênio

O N₂ segue diretamente para o biorreator:

\[
\mathrm{N_2} \rightarrow B \rightarrow 5 \rightarrow 6 \rightarrow \text{biorreator}
\]

O nitrogênio introduzido pelo aspersor reduz progressivamente a concentração de oxigênio dissolvido no meio.

### Linha de ar

Simultaneamente, o fluxômetro de ar pode ser acionado no setpoint que será utilizado posteriormente na etapa de reaeração.

Como A está fechada, o ar não alcança o biorreator. Em vez disso, ele é descarregado pela válvula C:

\[
\text{Fluxômetro} \rightarrow 2 \rightarrow C \rightarrow \text{descarga}
\]

Essa configuração permite que o pulso inicial do fluxômetro ocorra fora do biorreator.

Durante essa etapa, o único estado elétrico necessário é:

\[
MOSFET_{B+C} = ON
\]

\[
MOSFET_A = OFF
\]

---

## 8. Condição para o início do ensaio de \(k_La\)

O início da etapa de reaeração não ocorre imediatamente após o acionamento do setpoint do fluxômetro.

Primeiro, a vazão de ar deve atingir e permanecer estável no valor experimental definido.

Quando essa condição é atingida, realiza-se uma única transição lógica entre os dois canais de potência:

\[
MOSFET_{B+C}: ON \rightarrow OFF
\]

\[
MOSFET_A: OFF \rightarrow ON
\]

Consequentemente:

- **A: fechada → aberta**
- **B: aberta → fechada**
- **C: aberta → fechada**

Esse instante marca o início efetivo da etapa de reaeração e pode ser definido como:

\[
t=0
\]

do ensaio de \(k_La\).

A partir desse instante, o percurso gasoso passa a ser:

\[
\text{Fluxômetro} \rightarrow 2 \rightarrow A \rightarrow 5 \rightarrow 6 \rightarrow \text{biorreator}
\]

Como o fluxômetro já se encontra estabilizado antes da abertura de A, o biorreator recebe imediatamente uma vazão próxima ao valor de setpoint, sem o pulso inicial de 2 a 5 vezes a vazão nominal.

---

## 9. Importância da comutação para a determinação de \(k_La\)

Após a substituição do N₂ por ar, a concentração de oxigênio dissolvido aumenta em função da transferência de massa gás-líquido.

Para um sistema no qual o consumo biológico de oxigênio seja inexistente ou desprezível, a dinâmica pode ser representada por:

\[
\frac{dC_L}{dt}=k_La(C_L^*-C_L)
\]

em que:

- \(C_L\) é a concentração de oxigênio dissolvido no líquido;
- \(C_L^*\) é a concentração de oxigênio correspondente à saturação nas condições do ensaio;
- \(k_La\) é o coeficiente volumétrico de transferência de oxigênio.

Sob condições aproximadamente constantes de \(C_L^*\) e \(k_La\), a expressão integrada é:

\[
\ln\left(
\frac{C_L^*-C_L}
{C_L^*-C_{L,0}}
\right)
=
-k_La t
\]

A definição precisa de \(t=0\) é importante para a qualidade da estimativa de \(k_La\).

Se o pulso inicial do fluxômetro fosse enviado ao biorreator, a condição de vazão durante os primeiros instantes não corresponderia ao setpoint nominal. Consequentemente, a taxa inicial de transferência de oxigênio poderia ser artificialmente elevada, introduzindo um transiente de origem instrumental na curva de reaeração.

O uso da válvula C reduz esse problema porque permite que o fluxômetro atinja o regime estacionário antes de ser conectado ao biorreator.

---

## 10. Sequência operacional resumida

| Etapa | MOSFET A | MOSFET B+C | A | B | C | Condição no biorreator |
|---|---:|---:|---:|---:|---:|---|
| Potência — estabilização do ar* | OFF | ON | Fechada | Aberta | Aberta | Sem ar do fluxômetro; N₂ deve estar isolado a montante |
| Potência — ensaio | ON | OFF | Aberta | Fechada | Fechada | Ar estabilizado |
| \(k_La\) — desoxigenação + estabilização do ar | OFF | ON | Fechada | Aberta | Aberta | N₂ no reator; ar descartado por C |
| \(k_La\) — início da reaeração | ON | OFF | Aberta | Fechada | Fechada | Ar estabilizado |
| \(k_La\) — aquisição da reaeração | ON | OFF | Aberta | Fechada | Fechada | Ar no setpoint experimental |

\* Para que o ensaio de potência permaneça sem N₂, a alimentação de nitrogênio deve estar isolada antes da válvula B enquanto B/C estiverem abertas.

---

## 11. Lógica funcional do sistema

A arquitetura possui essencialmente dois estados válidos.

### Estado de estabilização/desoxigenação

\[
\boxed{
A=\mathrm{fechada}, \qquad
B=\mathrm{aberta}, \qquad
C=\mathrm{aberta}
}
\]

### Estado de ensaio com ar

\[
\boxed{
A=\mathrm{aberta}, \qquad
B=\mathrm{fechada}, \qquad
C=\mathrm{fechada}
}
\]

Dessa forma, B e C não precisam ser comandadas independentemente.

A relação de complementaridade entre os dois MOSFETs deve ser preservada pelo controle:

\[
\boxed{MOSFET_A=\neg MOSFET_{B+C}}
\]

Essa lógica evita que A permaneça aberta ao mesmo tempo que B e C durante a operação normal.

---

## 12. Considerações de implementação do controle

Como os dois estados são mutuamente exclusivos, é recomendável implementar o software de controle como uma **máquina de estados**, em vez de comandar individualmente cada válvula.

Uma implementação conceitual pode possuir apenas dois estados:

- `ESTABILIZACAO_N2_BYPASS`
- `AERACAO_REATOR`

No primeiro:

- A = OFF;
- B+C = ON.

No segundo:

- A = ON;
- B+C = OFF.

Essa abordagem reduz a possibilidade de combinações inválidas de válvulas.

Na transição entre os estados, deve-se considerar o tempo real de resposta das válvulas eletromecânicas. Caso exista risco de comunicação momentânea entre a linha de ar e a linha de N₂ devido a diferenças no tempo de abertura/fechamento, pode ser útil caracterizar experimentalmente os tempos de atuação e, se necessário, implementar um pequeno intervalo de comutação seguro. A necessidade e a duração desse intervalo devem ser definidas a partir do comportamento das válvulas reais.

---

## 13. Vantagens da configuração proposta

A utilização das três válvulas com dois canais de potência permite:

- utilizar um único aspersor para os ensaios de potência e \(k_La\);
- evitar desconexões e reconexões de mangueiras entre experimentos;
- manter geometria e condições de dispersão de gás mais reprodutíveis;
- estabilizar previamente o fluxômetro no setpoint experimental;
- desviar o pulso inicial de vazão para a linha de saída;
- impedir que transientes de 2 a 5 vezes o setpoint sejam aplicados ao biorreator;
- realizar simultaneamente a desoxigenação por N₂ e a estabilização da linha de ar no ensaio de \(k_La\);
- estabelecer um instante experimental de início mais bem definido;
- reduzir o número de canais eletrônicos necessários para o acionamento;
- simplificar a lógica de controle para dois estados complementares;
- melhorar a reprodutibilidade entre ensaios;
- reduzir a influência da dinâmica do controlador de vazão sobre a estimativa de \(k_La\).

Em particular, a válvula C não funciona apenas como uma descarga convencional. Ela constitui um **bypass de estabilização**, permitindo desacoplar a dinâmica transitória do fluxômetro da dinâmica do biorreator.

O acoplamento elétrico de B e C é coerente com a sequência do ensaio de \(k_La\): enquanto o N₂ é enviado ao biorreator, a linha de ar permanece em bypass; quando a vazão de ar está estabilizada, B e C são fechadas simultaneamente e A é aberta, iniciando a etapa de reaeração.
