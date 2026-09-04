# Aceitação em bancada — Ensaios de potência de impelidor

> **Versão:** 1.0 · **Escrito:** 2026-09-04
> Fecha o item **8.4** de [PLANO_ENSAIOS_POTENCIA_IMPELIDOR.md](PLANO_ENSAIOS_POTENCIA_IMPELIDOR.md) —
> o único item do plano que continua aberto, e o único que o simulador não pode fechar.
>
> **Docs:** [PLANO](PLANO_ENSAIOS_POTENCIA_IMPELIDOR.md) · [HARDWARE_VALIDATION](HARDWARE_VALIDATION.md) · [PROTOCOL](PROTOCOL.md) · [DECISIONS](DECISIONS.md)

---

## O princípio que governa este documento

> **O simulador fechou o portão de software. Ele não é evidência de comportamento físico.**

Tudo abaixo já passa contra o `DeviceModel` e contra o runner real em teste automatizado. O que se
prova aqui é outra coisa: que o ACK que o app espera é o ACK que o firmware manda, que a válvula que
o app pensa ter aberto é a que abriu, que o torque que o app lê corresponde ao eixo girando, e que o
corte de gás e a parada de motor acontecem no metal.

**Segurança.** Todo bloco comanda motor e gás reais. Rode com o vaso preparado — **água, nunca
cultura** —, um operador na `⛔ Parada segura` e a possibilidade de cortar energia. A faixa de
rotação é **15–1000 rpm** e **zero desabilita o motor e trava o teclado do módulo** (§2.6, §19 do
plano): se alguma etapa levar o setpoint a 0, pare e registre como falha.

---

## Como ler um bloco

Cada bloco lista **pré-requisitos**, **passos** e **critérios de aceite**. Registre para cada um: a
build do firmware, o transporte (USB/Wi-Fi), a versão exata do OpenTEC-Hub (`Directory.Build.props`),
a pasta do ensaio em `Testes-Potencia/`, a exportação de Eventos e fotos do arranjo quando o
critério for físico. Arquive em `docs/evidence/hardware/potencia/<bloco>/`.

Os blocos estão em **ordem de dependência**: P-2 assume P-1 aprovado, e assim por diante.

---

## Antes de começar — capturas que não se recuperam depois

| # | Capturar | Por quê |
|---|---|---|
| B-1 | **Torque de repouso** com o eixo parado e o motor habilitado, 60 s de telemetria | É o zero do sistema. Sem ele não há como separar offset de sinal |
| B-2 | **Placa do servo**: torque nominal (`T_nom`) e relação do CN1 | O `Np` absoluto depende de `T_nom`; o valor de placa é a referência contra a qual a calibração é julgada (§9.1) |
| B-3 | **Geometria medida**, não nominal: `T` do vaso, `D` de cada impelidor, altura de líquido, chicanas | O `Np` e o `Re` são calculados sobre estes números; erro aqui vira erro científico silencioso |
| B-4 | **Vazão real do fluxômetro** em 3 setpoints, cronometrada ou por bolhômetro | Confirma que `FlowRate` em L/min reais é o que sai na linha (§11) |

---

## Bloco P-1 — Telemetria de torque e faixa de rotação

**Objetivo:** provar que o app lê o eixo, e que a faixa e o piso de rotação são respeitados no metal.

**Pré-requisitos:** vaso com água no volume de trabalho declarado, impelidor montado, Hub conectado.

| # | Teste | Passos | Critério de aceite |
|---|---|---|---|
| P-1.1 | Telemetria chega e é plausível | Página **Potência**, sem ensaio aberto. Observar o card ao vivo com o motor parado, depois a 200, 400 e 600 rpm | `ServoRpm`, `ServoTorqueNm` e `ServoPowerW` atualizam; a rotação **medida** segue o setpoint dentro da tolerância; o torque cresce monotonicamente com a rotação |
| P-1.2 | O app usa a rotação **medida**, não o setpoint | Impor 400 rpm e comparar o `N` do card com o tacômetro/leitura do drive | O `N` exibido é o medido (§19: usar `motorSetpoint` como `N` é armadilha conhecida) |
| P-1.3 | Piso de 15 rpm | Iniciar um ensaio e usar **Parar e revisar**; depois **Abortar** | O motor recua para **15 rpm**, nunca 0. O teclado do módulo continua respondendo |
| P-1.4 | Teto de 1000 rpm | Tentar planejar uma condição acima de 1000 rpm | A tabela recusa antes de comandar, com mensagem |
| P-1.5 | Preflight diz a verdade | Desconectar o Hub com a página aberta | A faixa de preflight fica âmbar e nomeia o motivo real (Hub desconectado / servo sem amostra recente) |

---

## Bloco P-2 — Calibração de torque e tara

**Objetivo:** transformar `Np` relativo em `Np` absoluto, e medir o atrito parasita que o
impelidor de baixa demanda esconde (§9).

**Pré-requisitos:** P-1 aprovado. Massa aferida e braço medido para a calibração estática.

| # | Teste | Passos | Critério de aceite |
|---|---|---|---|
| P-2.1 | Calibração estática | Assistente de calibração; aplicar torque conhecido pelo braço e massa | O ajuste grava `calibracao-torque.json` com escala e offset; a escala fica dentro de ±20% do valor de placa, ou a divergência é explicada e registrada |
| P-2.2 | Curva de tara no ar | Assistente de tara, **impelidor girando seco**, cobrindo a faixa do ensaio | Grava `tara.json` com `P_vazio(N)` e o ruído `σ_τ(N)` por rotação; a curva é monotônica |
| P-2.3 | A tara pertence a este conjunto | Trocar um impelidor e tentar iniciar sem refazer a tara | O runner recusa: "A tara pertence a outro conjunto de impelidores" |
| P-2.4 | Modo relativo é rotulado | Iniciar um ensaio sem calibração, confirmando modo relativo | O cabeçalho e o CSV marcam o resultado como **relativo**; nenhum `Np` é apresentado como absoluto (§19) |

---

## Bloco P-3 — Varredura não gaseificada e o `Np(Re)`

**Objetivo:** o ensaio básico ponta a ponta no metal, com a parada adaptativa decidindo sozinha.

**Pré-requisitos:** P-2 aprovado.

| # | Teste | Passos | Critério de aceite |
|---|---|---|---|
| P-3.1 | Varredura completa | Gerar varredura 200→600 rpm, passo 50, sem gás. Iniciar e deixar correr | Todas as condições capturam; cada ponto para por **confiança** (IC₉₅ dentro do alvo) e não por `t_max`, na maioria dos pontos |
| P-3.2 | O regime é respeitado | Observar a fase de cada ponto | A captura só começa depois que a rotação **medida** entra na banda e o torque assenta (§19: capturar antes do torque assentar enviesa a média) |
| P-3.3 | Platô turbulento | Ao fim, olhar a curva `Np×Re` | Há platô acima de `Re ≈ 10⁴`, e o `Np` do platô é compatível com a literatura do impelidor usado (Rushton ≈ 5,0 com chicanas) dentro da incerteza declarada |
| P-3.4 | Persistência e proveniência | Abrir a pasta do ensaio | `ensaio.json`, `resumo-resultados.csv` e as pastas de corrida existem; o CSV traz `N`, `τ_líq`, `P`, `Np`, `Re` e o desvio de cada ponto |
| P-3.5 | Pausa, pulo e repetição | Durante a varredura: pausar, retomar, pular uma condição, rejeitar e repetir um ponto | Cada ação faz no metal o que a tela diz; nenhuma delas leva o motor a 0 |

---

## Bloco P-4 — Gás, válvulas e `P_G/P₀`

**Objetivo:** o protocolo de gás — o ponto onde app e firmware mais podem divergir (§11).

**Pré-requisitos:** P-3 aprovado. Fluxômetro online e linha montada. B-4 capturado.

| # | Teste | Passos | Critério de aceite |
|---|---|---|---|
| P-4.1 | Recusa sem fluxômetro | Planejar condição gaseificada com o fluxômetro offline e tentar iniciar | O preflight recusa e nomeia o fluxômetro |
| P-4.2 | Troca de estado por ACK | Executar uma condição gaseificada observando o log de comandos | A troca segue **fechar → confirmar → novo estado → confirmar**; a confirmação exige `FlowCommandPending==false` e `FlowCommandAck==FlowCommandId` |
| P-4.3 | Roteamento físico | Em cada estado, conferir **na tubulação** qual válvula está aberta | A válvula fisicamente aberta é a que o app indica. Divergência aqui é falha bloqueante |
| P-4.4 | Vazão entregue | Comparar `FlowRate` com a captura B-4 nos mesmos setpoints | A vazão medida bate com a de referência dentro da incerteza do método |
| P-4.5 | Pulso de vazão na abertura | Observar `FlowRate` no instante da comutação | O pulso existe e **não** entra na janela de média: a captura só conta depois do regime |
| P-4.6 | `P_G/P₀` com P₀ real | Rodar a condição não gaseificada de referência na mesma rotação, depois a gaseificada | A razão sai entre 0 e 1, com `IC₉₅` propagado; a proveniência de `P₀` aparece no ponto |
| P-4.7 | Corte seguro do gás | **Parar e revisar** no meio de uma condição gaseificada | Vazão vai a 0, válvulas fecham, motor recua a 15 rpm — nessa ordem |
| P-4.8 | Estabilização no alívio *(se a montagem existir)* | Ligar a opção e repetir P-4.6 | O app estabiliza a vazão no alívio a 15 rpm antes de comutar para o reator; a comutação só ocorre com a vazão dentro da tolerância |

---

## Bloco P-5 — Flooding

**Objetivo:** confirmar que o joelho que o app detecta é o afogamento que se vê no vaso.

**Pré-requisitos:** P-4 aprovado. Vaso com visibilidade da região do impelidor.

| # | Teste | Passos | Critério de aceite |
|---|---|---|---|
| P-5.1 | Varredura de vazão a `N` fixo | Fixar uma rotação e varrer `Q_g` até passar do afogamento | `P_G/P₀` cai com `Fl_G` e apresenta joelho |
| P-5.2 | O joelho é visível | **Observar o vaso** na vazão do joelho detectado, e fotografar | A transição de dispersão para afogamento acontece na vizinhança do ponto detectado. Registrar a discrepância se houver |
| P-5.3 | Nienow como referência, não como verdade | Comparar o `(Fl_G)_F` experimental com o teórico exibido | A comparação é apresentada como desvio relativo; o app não substitui um pelo outro |
| P-5.4 | Ajuste manual do joelho | Marcar outro ponto como flooding oficial e reprocessar | O documento registra o método como **manual** e mantém o valor automático rastreável |

---

## Bloco P-6 — Mapa, correlação e escalonamento

**Objetivo:** a síntese da fase 3 sobre dados reais. Não comanda hardware — depende dele.

**Pré-requisitos:** P-3 e P-4 aprovados, e um mapa de kLa medido na mesma montagem.

| # | Teste | Passos | Critério de aceite |
|---|---|---|---|
| P-6.1 | Ida kLa → Potência | Na página **Potência**, importar as condições do mapa de kLa com referências P₀ | As condições caem exatamente nas coordenadas do mapa, marcadas como origem **Mapa** |
| P-6.2 | Superfície sobre dados reais | Executar a varredura importada e reconstruir a superfície | A malha cobre o retângulo das âncoras; fora do fecho convexo permanece indefinida |
| P-6.3 | Correlação van't Riet | Vincular o mapa de kLa e ajustar | `R²` e os erros-padrão de `K`, `α`, `β` são exibidos; `α` e `β` caem na faixa de literatura (`α ≈ 0,4–0,7`, `β ≈ 0,2–0,5`) — **ou** a divergência é registrada como achado |
| P-6.4 | Paridade | Olhar o gráfico de paridade | A maioria dos pontos cai na faixa ±15%; os de fora estão destacados |
| P-6.5 | Volta Potência → kLa | Exportar o mapa enriquecido | Nasce uma **revisão nova**; o documento kLa original permanece byte-idêntico |
| P-6.6 | Escalonamento recusa o indeterminado | Pedir escalonamento sem regra de gás | O cálculo é recusado com o motivo, sem número na tela |

---

## Bloco P-7 — Segurança e convivência

**Objetivo:** o ensaio de potência não é o único dono da bancada.

| # | Teste | Passos | Critério de aceite |
|---|---|---|---|
| P-7.1 | Disputa de posse | Com a cascata de O₂ ativa, tentar iniciar um ensaio de potência | Recusa nomeando o dono atual da agitação |
| P-7.2 | `⛔ Parada segura` durante o ensaio | Acionar a parada segura no meio de uma captura | Gás cortado, motor a 15 rpm, ensaio marcado como interrompido com motivo |
| P-7.3 | Guarda de torque | Planejar uma condição que exceda o limite de torque e observar | A condição é interrompida ao ultrapassar o guarda; o ensaio não continua às cegas |
| P-7.4 | Queda de link no meio | Desconectar o USB durante uma captura | O ensaio para com motivo registrado; nada fica comandado sem supervisão |

---

## O que o simulador já cobriu (não repetir aqui)

Estes têm teste automatizado verde e não precisam de bancada, exceto para confirmar o
comportamento físico já listado acima: máquina de estados do runner, portas de parada adaptativa,
propagação de incerteza, hierarquia de `P₀`, matemática de Nienow, reconstrução da superfície,
regressão multivariável, importador bidirecional, recusas do escalonamento, contratos de UI e
ausência de vazamento nas assinaturas dos gráficos.

---

## Ordem de prioridade se faltar tempo

1. **P-1** e **P-4.3** — telemetria correta e válvula certa. Sem estes dois, todo dado colhido é suspeito.
2. **P-4.7** e **P-7.2** — os cortes de segurança.
3. **P-2** — sem calibração e tara, o `Np` é relativo e o ensaio perde o objetivo científico.
4. **P-3** — a varredura básica.
5. **P-5** e **P-6** — ciência de fase 2 e 3.

---

## Registro de resultado

Ao concluir, marque o item **8.4** no plano com a data e o commit, e anexe aqui a tabela preenchida
com aprovado/reprovado por linha. Um bloco reprovado **não** é fechado com ressalva: vira achado no
plano, com o comportamento observado descrito ao lado do esperado.
