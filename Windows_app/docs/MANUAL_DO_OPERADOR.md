# OpenTEC-Hub — Manual do Operador

> **Versão do Documento:** 1.0 · **Versão do Software:** 0.24.0+  
> **Data:** 05/09/2026  
> **Público-Alvo:** Operadores de biorreatores, pesquisadores de bioprocessos e técnicos de laboratório  
> **Compatibilidade de Hardware:** Plataforma Vitor Mazziero UNESP com módulo mestre ESP32-S3 Hub e periféricos inteligentes

---

## 1. Introdução e Arquitetura do Sistema

O **OpenTEC-Hub** é uma estação integrada de supervisão, controle avançado e ensaios cinéticos para biorreatores e fermentadores de bancada e escala piloto. O sistema reúne instrumentação em tempo real, controle multivariável em malha fechada, automação por receitas industriais e módulos metrológicos para caracterização hidrodinâmica ($k_L a$ e potência de impelidor).

### Topologia de Comunicação
A comunicação entre o computador de supervisão e os atuadores/sensores é intermediada pelo módulo mestre **ESP32-S3 Hub**:
- **Enlace de Supervisão (PC ↔ Hub):** Conexão via cabo USB (porta serial virtual CDC ACM em 115200 bps) ou rede Wi-Fi dedicada (soquetes TCP/IP).
- **Enlace com o Motor de Agitação (Hub ↔ Servo Drive):** Barramento serial industrial RS-485 operando protocolo Modbus RTU direto com o acionador Delta ASDA-B2.
- **Rede de Periféricos Inteligentes:** Módulos autônomos sem fio (fluxômetro mássico, sensor de biomassa óptico, bombas peristálticas externas dosadoras e agitadores de frascos) sincronizados periodicamente pelo Hub.

---

## 2. Instalação e Requisitos do Sistema

### Requisitos Mínimos
- **Sistema Operacional:** Windows 10 (Build 19041 ou superior) ou Windows 11 de 64 bits (`win-x64`).
- **Processador:** Intel Core i3 / AMD Ryzen 3 ou superior.
- **Memória RAM:** Mínimo de 4 GB (recomendado 8 GB).
- **Armazenamento:** 500 MB livres em disco.
- **Portas:** Pelo menos 1 porta USB 2.0/3.0 disponível para comunicação com a bancada.
- **Resolução de Vídeo:** Mínimo de 1280×720 pixels (otimizado para 1920×1080 Full HD).

### Instalação da Aplicação
1. Execute o arquivo instalador distribuído: `OpenTECHub_Setup_v0.24.0.exe`.
2. O instalador utiliza tecnologia **self-contained** (o ambiente de execução do .NET 10 já vem embutido), dispensando a instalação de qualquer runtime ou biblioteca externa adicional.
3. Siga o assistente de instalação em português. Por padrão, o aplicativo será instalado em `%LOCALAPPDATA%\Programs\OpenTEC-Hub` (ou `C:\Program Files\OpenTEC-Hub` se executado com privilégios de administrador).
4. Marque a opção para criar um atalho na Área de Trabalho para facilitar o acesso rotineiro.

### Driver de Comunicação USB (CH343 / WCH)
Se o computador ainda não possuir o driver da ponte USB-Serial do ESP32-S3 instalado:
1. Conecte o cabo USB do módulo Hub ao PC.
2. Caso a porta serial não seja reconhecida no Gerenciador de Dispositivos (código com triângulo amarelo), instale o driver oficial WCH CH343/CH340.
3. A porta será identificada como `WCH CDC COM Port` ou `Dispositivo Serial USB (COMx)`.

---

## 3. Inicialização e Gerenciamento de Workspaces

### O Que é um Workspace?
Um *Workspace* é uma pasta no computador onde o OpenTEC-Hub armazena todos os registros operacionais, incluindo:
- **Sessões de Cultivo (`Sessoes/`):** Arquivos CSV e gráficos com leituras brutas segundo a segundo.
- **Logs Operacionais (`Logs/`):** Histórico de eventos do sistema e relatórios de auditoria.
- **Receitas de Automação (`Receitas/`):** Arquivos JSON com procedimentos operacionais padrão.
- **Mapas e Campanhas de $k_L a$ (`Mapas/` e `Testes-kLa/`):** Ensaios de oxigenação.
- **Ensaios de Potência (`Testes-Potencia/` e `Mapas-Potencia/`):** Curvas de $N_p$ e aeração. Dentro de cada ensaio, `Taras-Brutas/` guarda as leituras de cada varredura de tara — inclusive as que foram canceladas ou não convergiram — e `Pontos-Unicos/` guarda cada conferência de ponto único com seu manifesto. Uma conferência feita sem nenhum ensaio aberto vai para `Testes-Potencia/Pontos-Unicos/`.
- **Configurações e Calibrações (`Configuracoes/`):** Arquivo `settings.json` com ganhos PID e dados metrológicos.

### Inicialização Padrão
Ao abrir o OpenTEC-Hub pelo atalho, se nenhum workspace estiver memorizado, uma janela solicitará a escolha da pasta de trabalho. A pasta recomendada padrão é `C:\Users\<SeuUsuario>\Documents\OpenTEC-Hub`.

### Opções Avançadas por Linha de Comando
Técnicos e integradores podem criar atalhos personalizados com parâmetros especiais:
- `--workspace "D:\MeusEnsaios\Biorreator01"`: Abre diretamente o workspace especificado.
- `--no-workspace-prompt`: Pula o diálogo de confirmação e usa o último diretório salvo.
- `--kla-test-file "caminho.csv" --kla-test-speed 5`: Inicia o aplicativo diretamente no módulo de determinação de $k_L a$ em modo de simulação/reprodução.

---

## 4. Conexão e Topologia de Comunicação

A tela inicial exibe a barra lateral de conexão:
1. **Seleção de Interface:**
   - **USB:** Selecione a porta COM correspondente no menu suspenso e clique em **Conectar**.
   - **Wi-Fi:** Digite o endereço IP do Hub na rede local do laboratório e clique em **Conectar**.
2. **Diagnóstico de Enlace:**
   - **LED Verde / Ao Vivo:** O Hub está enviando pacotes telemétricos regulares (frequência típica de 1 a 5 Hz).
   - **Indicador RTT (Latência):** Exibe o tempo de ida e volta da mensagem em milissegundos. Valores abaixo de 50 ms indicam excelente comunicação.
   - **Contador de Pacotes e Erros:** Permite monitorar se há perdas de quadros por interferência eletromagnética na bancada.

---

### Localizar um nó na rede do Hub

Cada dispositivo externo (fluxômetro, sensor de distância, bomba externa, sensor de absorbância e
frasco agitador) é uma placa Wi-Fi que se registra no Hub ao ligar. O aplicativo mostra onde cada uma
está em dois lugares:

1. **Controle → gaveta do dispositivo → cartão Rede:** `IP · fw` e, no tooltip, o MAC. Com o PC na
   rede Wi-Fi do Hub, **Abrir diagnóstico** abre `http://<ip>/diag` no navegador (uptime, heap, RSSI,
   falhas com o Hub) e **Copiar IP** põe o endereço na área de transferência. Por USB os botões ficam
   desabilitados com o motivo no tooltip — o app conhece o IP, mas o PC não está na rede do Hub.
2. **Configurações → Conexão → Nós na rede do Hub:** a tabela dos cinco nós com IP, MAC, firmware,
   estado e há quanto tempo o Hub os ouviu. Em Wi-Fi, **Atualizar** consulta o diretório do Hub
   (`/nodes`); por USB a tabela vem do quadro de telemetria.

### Saúde dos nós (RSSI, heap, uptime, falhas, OTA)

A mesma tabela mostra, para cada nó, a intensidade do Wi-Fi (**RSSI**), a memória livre (**Heap**),
o tempo desde a última inicialização (**Uptime**), o contador de falhas consecutivas ao falar com o
Hub (**Falhas c/ Hub**) e se há gravação de firmware em andamento (**OTA**), além de uma métrica
própria do nó (distância e offset, vazão e volume da bomba, absorbância…). Requer Hub `10.2`:

- **Em Wi-Fi**, o app consulta `/nodeDiag` junto com `/nodes` a cada 10 s enquanto a seção está
  aberta.
- **Em USB**, o app pede ao Hub (`nodeDiag`) a cada 30 s com a seção aberta, ou ao clicar em
  **Atualizar saúde**; o Hub responde com o que tem em cache — ele mesmo consulta cada placa a cada
  30 s. O pedido não interfere em nenhum atuador nem em ensaio em andamento.

**Falhas c/ Hub ≥ 8** aparece como aviso de reassociação (o nó está tendo dificuldade de entregar o
push), nunca como alarme de processo. Um nó desligado fica com a idade da última coleta crescendo;
um nó que nunca se registrou aparece sem saúde ("—"). O rodapé diz há quanto tempo o Hub coletou.

Um firmware de nó fora do conjunto validado com esta versão do aplicativo aparece como aviso em texto
no cartão — não é alarme. Mudanças de identidade (nó registrado, IP que mudou, firmware ou placa
diferente) ficam registradas em **Eventos**. Para gravar um firmware novo num nó, use
`External-Devices/tools/Publish-OtaFirmware.ps1 -Device <nome>`, que descobre o IP pelo mesmo
diretório do Hub. Requer Hub `10.1.0-dev` ou superior; com um Hub anterior a tabela avisa.

## 4.1 Documentação dentro do aplicativo

O programa traz o próprio manual em **Configurações → Documentação**, logo abaixo de *Comandos do
equipamento*. Cada página é descrita primeiro pelo seu layout e depois controle a controle, com o
nome que aparece na tela. Os botões **“?”** espalhados pelas páginas abrem direto o assunto
correspondente.

Este manual em PDF/Markdown continua sendo a referência de instalação, procedimentos e diagnóstico;
a documentação interna é a referência de *tela*, para consulta durante a operação.

---

## 5. Sinótico e Painel de Monitoramento

O menu **Sinótico** exibe o diagrama animado em tempo real do biorreator:
- **Vaso Central:** Visualização do nível do líquido, formação de espuma e sentido de rotação do impelidor.
- **Cartões de Parâmetros de Processo:**
  - **Temperatura (°C):** Leitura de sonda PT100/termopar, setpoint de aquecimento e banda morta.
  - **pH:** Valor medido, setpoint desejado, diferencial ($\Delta pH$) e status das bombas de ácido/base.
  - **Oxigênio Dissolvido - DO (%):** Saturação relativa de $O_2$, setpoint e indicação da malha de controle.
  - **Vazão de Ar (L/min):** Vazão instantânea reportada pelo fluxômetro mássico e setpoint da válvula de controle.
  - **Agitação Mecânica (rpm):** Rotação do servo motor, torque atual (% do torque nominal) e potência calculada no eixo.
  - **Biomassa (Densidade Óptica / g/L):** Leitura do sensor de absorbância e concentração celular estimada.
- **Saúde Estatística dos Sensores:**
  - Cada variável de processo monitora continuamente o desvio padrão residual das últimas amostras, removendo a inclinação (tendência linear de subida/descida do cultivo).
  - Um selo visual indica se a leitura está **Estável**, com **Ruído Moderado** ou com **Ruído Elevado** (indicando necessidade de calibração, bolhas na sonda ou aterramento deficiente).

---

## 6. Operação e Controle de Processo

A página **Controle** permite atuar diretamente sobre cada periférico e malha do equipamento.

### Vazão de Ar e as válvulas A, B e C
As válvulas do arranjo de gás têm papel fixo: **A** leva ar ao reator (aspersor), **B** é a linha de
N₂ (ou nada, quando pinçada) e **C** é a purga de ar. B e C abrem e fecham juntas — estão no mesmo
canal elétrico — e o fluxômetro tem duas entradas, 1 e 2; qual delas aciona A é configuração
(**Configurações › Gás e válvulas**, padrão: entrada 2 → A, entrada 1 → B + C). Na gaveta **Vazão de
Ar** o operador escolhe a **entrada acionada**: *Fechado*, *Entrada 1 · B + C* ou *Entrada 2 · A*
(os rótulos seguem a ligação configurada). Abaixo, *Telemetria* mostra o que o fluxômetro está
fazendo com os mesmos nomes — *Reator (A)*, *Descarga + N₂ (B/C)*, *Fechado* — e denuncia dois
estados anômalos: *Gás sem destino* (setpoint acima de zero sem entrada aberta) e *A e B/C abertas*.
O expansor **Avançado** dá as entradas 1 e 2 uma a uma e *Fechar linha (v_Flow)*, para bancada:
qualquer combinação é enviada; as anômalas só geram aviso na tela e, se persistirem 3 s no eco, o
alarme correspondente.

### Árbitro de Comandos e Dono de Atuação
Para evitar conflitos catastróficos em que um operador envie um comando que contrarie uma automação em andamento, o sistema possui um **Árbitro de Comandos** com três níveis de posse:
1. **Manual (`Manual`):** O operador humano tem posse dos controles na tela.
2. **Automático (`Automatic`):** Uma malha interna (como a Cascata de Oxigênio) detém a posse temporária daquele atuador.
3. **Receita (`Recipe`):** Uma receita em execução tem prioridade exclusiva sobre os atuadores designados.

> [!NOTE]
> Quando um atuador está sob posse de uma **Receita** ou da **Automação**, os botões e campos manuais correspondentes ficam visualmente travados, exibindo uma etiqueta informando quem detém a posse física.

### Confirmação Ágil de Comandos (Enter e Perda de Foco)
Para garantir máxima agilidade ao operador paramentado na bancada:
- Ao digitar um novo valor de setpoint em qualquer campo (temperatura, agitação, vazão de ar, dosagem), basta pressionar a tecla **`Enter`** ou simplesmente clicar em outro campo (**perda de foco**).
- O comando é validado contra os limites de engenharia de segurança e enviado instantaneamente ao hardware.

### Configuração dos nós externos pela gaveta (offset, sintonia, aquisição)

Cada nó externo persiste parâmetros próprios; o app os edita **sempre pelo Hub** (nunca direto no
nó), e um campo só fica habilitado quando o nó **ecoa** o valor aplicado — enquanto o eco não chega,
o campo mostra "aguardando eco do nó". O valor ecoado aparece ao lado do campo (`Telemetria: …`);
é ele, e não o que foi digitado, que vale.

- **Distância → Configuração do nó:** offset de instalação (mm) e períodos de amostragem e envio
  (ms), com **Restaurar padrões** (apaga a NVS do sensor). Os quatro vão numa mensagem só; o sensor
  só acorda para enviar, então o comando é entregue na resposta ao próximo push (indicador "comando
  em trânsito"). Ver §7.D.
- **Vazão de Ar → Sintonia do controlador:** Kp, Ki, ganho e offset de feedforward e taxa de rampa
  do controlador de vazão, com a tensão de saída (V) e o setpoint corrigido ao lado. **Quando:** se
  a vazão não regula para baixo depois de uma parada segura, ou oscila ao trocar de destino. **Como:**
  com o alívio aberto e sem ensaio em andamento (o árbitro recusa durante um ensaio), mudar um ganho
  de cada vez, esperar o eco e observar a vazão medida por alguns minutos. Os valores ficam
  persistidos no nó e são gravados na proveniência dos ensaios seguintes.
- **Absorbância → Parâmetros de aquisição:** tempo de integração (25 a 800 ms), PWM do LED, marcha
  óptica (0–31), fator EMA e período da sonda. O app envia **um comando por vez** (a marcha primeiro)
  e mostra "n de m enviados"; pode ser cancelado. Alterar IT ou PWM invalida o branco: capture-o de
  novo antes de medir.

### Procedimento de Parada Segura Global (*Safe Stop*)
No cabeçalho superior direito de qualquer tela, encontra-se o botão de emergência **Parada Segura (Safe Stop)**:
- Ao ser acionado, o coordenador de segurança **revoga instantaneamente a posse** de qualquer receita em andamento, malha cascata ou ensaio cinético.
- Todos os atuadores físicos recebem o comando de corte imediato: rotação para `0 rpm`, aquecimento desligado, vazão de ar para `0 L/min` e bombas peristálticas paradas.
- O sistema emite um registro de auditoria no log e coloca o equipamento em estado de segurança estável.

---

## 7. Procedimentos de Calibração

O menu **Calibração** fornece assistentes passo a passo para garantir a rastreabilidade metrológica das medições:

### A. Calibração do Eletrodo de pH
1. **Calibração em 1 Ponto (Ajuste de Desvio/Offset):**
   - Mergulhe o eletrodo no tampão de calibração pH 7.00.
   - Aguarde a leitura de tensão do conversor A/D estabilizar.
   - Pressione o botão **Calibrar Ponto 1**.
2. **Calibração em 2 Pontos (Ajuste de Sensibilidade/Slope):**
   - Execute o Ponto 1 no tampão pH 7.00.
   - Lave o eletrodo com água destilada e mergulhe no segundo tampão (pH 4.01 para fermentações ácidas ou pH 9.21 para cultivos neutros/alcalinos).
   - Aguarde a estabilização térmica e de milivolts e pressione **Calibrar Ponto 2**.
   - O sistema calculará a eficiência da sonda ($mV/pH$). Eficiências abaixo de 85% indicarão aviso de sonda envelhecida ou suja.

### B. Calibração da Sonda de Oxigênio Dissolvido (DO)
1. **Ponto Zero (0% $O_2$):**
   - Mergulhe a sonda em solução de sulfito de sódio ($Na_2SO_3$ a 5%) ou barboteie gás nitrogênio puro ($N_2$) no vaso até zerar o sinal.
   - Clique em **Calibrar Zero**.
2. **Ponto de Saturação (100% $O_2$):**
   - Preencha o biorreator com o meio ou água pura na temperatura de trabalho do processo.
   - Ligue a agitação no valor nominal (ex.: 300 rpm) e aeração máxima (ex.: 2 L/min) com ar atmosférico até o sinal parar de subir.
   - Informe a pressão atmosférica local (ou deixe o valor barométrico automático) e a salinidade do meio.
   - Clique em **Calibrar 100%**.

### C. Calibração das Bombas Peristálticas
1. Posicione a mangueira da bomba dosadora sobre uma proveta graduada ou balança analítica.
2. Defina uma rotação fixa (ex.: 50 rpm) e acione o teste de temporização de calibração por 60 segundos.
3. Meça o volume ou massa de líquido transferido.
4. Digite o volume real aferido no campo correspondente; o sistema calculará o coeficiente de vazão em $mL/rot$ ou $mL/min$.

### D. Offset do sensor de distância
1. Com o reator no nível de referência, leia a distância em **Controle → Distância** (`Telemetria`).
2. Meça a distância real (régua ou gabarito) entre o sensor e a superfície.
3. Em **Configuração do nó**, digite em **Offset (mm)** a diferença (real − lida) somada ao offset
   atual e confirme com `Enter`. O campo fica "aguardando eco" até o próximo envio do sensor (≤ 2 s
   em operação normal; mais sob backoff).
4. Confira que `Telemetria: <offset>` acompanha e que a distância lida passou a bater com a real.
   O valor é gravado na NVS do sensor e sobrevive a reinícios; **Restaurar padrões** volta ao de
   fábrica.

### D'. Calibração de vazão de ar: por onde sai o ar
Durante a calibração da curva do fluxômetro o ar sai pela **descarga C** (a entrada B + C é
acionada) — mantenha o N₂ fechado na fonte. Marque *Calibrar pelo reator (A)* para soprar pelo
aspersor. O arranjo e a rota ficam gravados com os pontos.

### E. Calibração da bomba externa (peristáltica do Hub)
1. Em **Calibrações → Bomba externa**, leia os coeficientes vigentes (ecoados pelo nó): a bomba
   aplica `Q [mL/min] = slope · S + intercept`, onde **S é a velocidade interna 0–1000** (o firmware a
   converte em PWM 155–1023) — não é o PWM bruto.
2. Acione a bomba em pelo menos três velocidades (por exemplo S = 250, 500 e 1000) por tempo
   cronometrado sobre uma balança; converta massa em volume pela densidade e calcule a vazão.
3. Ajuste a reta (slope, intercept) e digite os dois valores; a prévia mostra a vazão prevista nos
   três pontos. **Aplicar** envia ao nó pelo Hub.
4. O recibo `Calibracoes/bomba-externa-<data>.json` (pedido, eco aplicado, firmware do Hub e da bomba)
   é gravado **só depois do eco**; sem eco em 15 s o app avisa e nada é persistido.
5. **Zerar volume** (na gaveta Bomba Externa) zera o acumulador `PumpVol` no nó; o app espera o
   quadro seguinte para confirmar, não zera localmente.

---

## 8. Ensaios Especiais

### A. Determinação Abiótica de $k_L a$ (Gassing-Out Dinâmico)
O módulo **Determinação de kLa** automatiza o ensaio de transferência de oxigênio gás-líquido no
arranjo A/B/C (§6): a linha B vai ao cilindro de N₂.
1. **Configuração da Campanha:** Defina as condições de ensaio na matriz (combinações de vazão de ar em L/min e agitação em rpm) e o número de réplicas.
2. **Pré-voo:** ao iniciar a sequência, confirme **"N₂ aberto na fonte"** — a fonte é manual e o app
   não a enxerga; a confirmação vai ao manifesto e ao jornal. A linha *Arranjo: A na entrada 2 ·
   B/C na entrada 1* mostra a ligação em uso.
3. **Execução Automática** (as fases aparecem no cabeçalho):
   - *Fechando todas as válvulas* — o intertravamento confirma tudo fechado pelo fluxômetro.
   - *Abrindo N₂ (B/C)* e *Desoxigenando* — o N₂ entra por B (setpoint zero, só nitrogênio) com a
     rotação de desgaseificação até o DO chegar ao piso (DO mínimo mais a antecipação, se
     configurada). Se o DO já estiver no piso ao iniciar, esta fase é pulada.
   - *Ar por C · estabilizando* — a vazão do ensaio é pedida **na mesma saída B/C**: o ar sai pela
     purga C enquanto o N₂ segue entrando por B. O ensaio espera a vazão assentar **e** o piso de DO
     ficar plano (derivada), os dois ao mesmo tempo. Nada entra no reator ainda.
   - *Comutando para o reator (A)* — uma única frame fecha B/C e abre A com o setpoint já
     assentado. A confirmação pelo fluxômetro é o **t = 0**; a vazão e o DO desse instante ficam
     gravados na corrida.
   - *Reoxigenando* — registra a subida do DO em alta frequência até o limiar superior; fecha tudo e
     abre a revisão.
3. **Análise Log-Linear e Aceite:**
   - O algoritmo OLS calcula automaticamente a inclinação da curva $\ln(C^* - C_L)$ vs tempo, estimando $k_L a$ ($h^{-1}$), $R^2$, resíduos e intervalos de confiança de 95%.
   - Se o modo **Aceite Automático** estiver ativo, o runner avança sozinho para a próxima condição experimental da sequência.

### B. Mapeamento de Potência de Impelidor
O módulo **Potência** realiza a caracterização hidrodinâmica mecânica do vaso:
1. **Tara Mecânica:** Realiza a curva de torque em vazio (sem líquido) em várias rotações para descontar atrito de mancais e selos mecânicos.
2. **Ensaio Não Gaseificado ($N_p \times Re$):** Mede o torque estático e dinâmico com o volume de líquido nominal, determinando o Número de Potência $N_p$ característico do impelidor.
3. **Ensaio Gaseificado ($P_G / P_0$ e Flooding):** Varia a vazão de gás e rotação, mapeando a perda de potência por cavitação de bolhas e detectando o limite de inundação (*flooding*) do impelidor.
   No arranjo A/B/C (§6) a linha B fica **pinçada ou desconectada** — só ar; o cilindro de N₂ nunca é
   aberto. Toda condição gaseificada passa por **Ar por C · estabilizando**: a vazão é pedida na
   saída B/C, o pulso de partida do fluxômetro sai pela purga C, e só a vazão assentada (na banda por
   N leituras ou pelo critério de estabilidade) é comutada para o reator (A) numa única frame; a
   captura começa depois da confirmação e da rotação da condição. Os parâmetros ficam em
   *Parâmetros de captura › Pré-estabilização por C* (tolerância, amostras, σ e |erro|, rotação em C,
   teto — 500 s por padrão). O chip **Malha de gás** mostra *Fechado · Reator (A) · Descarga + N₂
   (B/C) · Ar por C · estabilizando · Gás sem destino · A e B/C abertas*.

---

## 9. Banho externo C404

Na linha **Temperatura**, o campo **Setpoint do reator** é a referência do reator. Ao selecionar
**Banho externo C404**, confirme primeiro a via e a comunicação; somente depois aplique o setpoint.
`Tempval` é a temperatura real do reator, enquanto `BathPv` é a temperatura do banho. A cascata,
os ganhos PI, a guarda, a saturação e o motivo de pausa são calculados e supervisionados no Hub.

Liberar ou abortar a cascata não desliga fisicamente o C404. Se houver falha, PV inválida,
desvio persistente ou nó offline, corrija a causa indicada pelo alarme antes de retomar. A via,
os parâmetros de painel e as séries de gráfico podem ser restaurados na próxima sessão, mas o
aplicativo nunca envia comandos automaticamente ao abrir.

**Onde fica cada controle.** Abra a linha **Temperatura**: a caixa *Via do setpoint de
temperatura* escolhe entre *Banho original (UART)* e *Banho externo (C404)*; o setpoint é sempre o
da própria linha. *Banho externo habilitado no Hub* (rodapé da gaveta) liga a troca de comandos
entre o Hub e o nó do banho — é pré-requisito para escolher a via externa; com a cascata ativa,
desligá-lo equivale a *Parar banho*.

**Estados do banho.** Dois controles independentes:

| Guarda do C404 | Cascata do Hub | Comportamento | Comandos locais (celular, `/ui`) |
|---|---|---|---|
| manual | desligada | o C404 fica com o operador; nada é revertido (estado após **Parar banho**) | livres |
| automática | desligada | a guarda defende o último SP comandado; mudanças no painel são desfeitas | livres |
| automática | ativa | o Hub calcula o SP do C404 a partir de `Tempval`; painel revertido | bloqueados (só Abortar) |
| manual | ativa | a cascata **aguarda** ("guarda do C404 em manual") e não envia nada | bloqueados |
| automática | falha | o Hub para de enviar; o C404 mantém o último SP | bloqueados |

A cascata só controla com a guarda em automático. Ao enviar um novo **Setpoint do reator** na via
externa, o Hub pede o modo automático ao nó uma vez.

**Parar banho.** Desliga a cascata, aborta a sequência em curso e deixa o C404 em manual no último
SP; a via e a comunicação não mudam. Desligar a linha de temperatura na via externa e a parada de
emergência têm o mesmo efeito. Para retomar, envie um novo setpoint do reator.

**Falha da cascata.** O cartão mostra o motivo (banho recusou o comando, erro na sequência do
C404, guarda suspensa, comando não concluído em 300 s, alvo alterado por fora). Corrija a causa e
use **Reset falha**; o reset também devolve a guarda do C404 ao automático.

**Receitas.** Na via externa, um bloco de temperatura só segue quando a temperatura do reator
(`Tempval`, válida) permanece dentro de ±0,5 °C do alvo por 30 s contínuos. Enquanto isso a
receita mostra "aguardando o reator atingir…"; pule o bloco ou pare a receita se necessário.

## 10. Elaboração e Execução de Receitas

No módulo **Receitas**, o usuário pode desenhar bateladas automatizadas conectando blocos de processo:
- **Blocos Básicos:** Espera temporizada, Rampa de temperatura, Degrau de agitação, Pulso de alimentação de nutriente.
- **Blocos Condicionais:** Aguardar pH atingir determinado valor, aguardar consumo de oxigênio, dosagem automática por sensor de espuma.
- **Interlocks de Segurança:** A receita verifica continuamente se as sondas estão online. Se um periférico configurado como obrigatório perder conexão por mais de 8 segundos, a receita entra em pausa de segurança (*Hold*) e notifica o operador.

### Determinar kLa, Periodicidade e Rampa linear (v0.27.0)

- **Determinar kLa:** escolha Abiótico ou Biótico e Único (condições atuais ou N/Q definidos) ou Múltiplos (matriz). O bloco usa as configurações da página **Determinar kLa › Configurações** (agitação de remoção, OD alvo, tempos máximos). No biótico, o corte de ar termina no OD mínimo do bloco (padrão 5 %). Ao fim de cada ensaio, as condições anteriores (rota de gás, N, Q, cascata) são restauradas e confirmadas; se a confirmação falhar, o retorno é reenviado até três vezes. Se ainda assim não confirmar, aparece o alarme **Retorno do ensaio kLa não confirmado**: no biótico, motor e gás ficam nos últimos comandos; confira o reator.
- **Periodicidade:** ligada em paralelo ao Controle de O₂, dispara o kLa no primeiro tempo e depois a cada período. Disparos perdidos (pausa, ensaio longo) são pulados, sem acúmulo. Durante o ensaio a cascata é suspensa e depois retomada sem salto.
- **Rampa linear de referências:** cada linha vai do valor inicial ao final no seu próprio tempo, com no máximo um comando por segundo. Conclui quando a medição do processo fica dentro da tolerância por 10 s (prazo padrão 900 s).
- **Queda do enlace com o Hub:** a cascata, a receita e o ensaio em andamento são mantidos; o Hub segue com os últimos comandos e a receita mostra "Receita aguardando dispositivo". Ao reconectar, o controle volta sem salto e o ensaio restaura as condições anteriores. Desconectar pelo botão continua devolvendo tudo ao Manual.
- **Gráficos do sinóptico:** passe o mouse perto da linha para ver valor e tempo em horas. Picos isolados de rotação, vazão, temperatura e outros canais medidos são suprimidos só no desenho; os arquivos guardam o dado bruto.
- **Resultados:** cada execução cria `Testes-kLa/Receitas-automaticas/<nome da receita>_<data>_<protocolo>_<Unico|Matriz>_N…_Q…` (o nome da receita identifica os ensaios; não há painel de preparação), com as corridas em `Corridas/N0300_Q02p00_Rep01`. Abra pela página Determinar kLa (Carregar ensaio) ou pelo botão da receita. Para revisar a janela e recalcular o kLa, use **Criar cópia editável para recalcular**; a sessão original permanece intacta.

---

## 11. Alarmes, Históricos e Diagnóstico de Falhas

### Monitor de Alarmes
- O sistema classifica os desvios em: **Informativo** (azul), **Alerta** (amarelo) e **Crítico** (vermelho).
- Alarmes críticos emitem aviso sonoro contínuo no computador da sala de controle. Em **Silenciar áudio (10 min)** o som pausa por 10 minutos enquanto você corrige a causa física; ao fim da pausa o som volta e o botão fica disponível de novo.
- A barra de alarmes (Reconhecer, Silenciar e a lista) fica no topo da página **Eventos**. Em qualquer página, um **ponto no canto do ícone de Eventos** avisa que há alarme a reconhecer: vermelho para falha crítica ativa, âmbar para aviso ou falha que já voltou ao normal.
- "Gás aberto sem supervisão" só é acionado depois de 15 s com o fluxômetro fora do Hub; "Fluxômetro offline" avisa em 2 s.

### Exportação de Dados e Históricos
- Na tela **Histórico** ou **Gráficos**, utilize o botão **Exportar CSV** para salvar todas as variáveis minuto a minuto para análise em Excel, Origin ou MATLAB.

### Relatórios de Pânico (Crash Reporting)
Caso ocorra qualquer erro imprevisto ou falha grave no sistema:
1. Uma janela de erro informará a ocorrência e o caminho do arquivo de pânico gerado.
2. O sistema grava um relatório completo com data e hora (`crash_YYYYMMDD_HHMMSS.log`) contendo a pilha de chamada do erro, estado da memória e detalhes da máquina.
3. Os arquivos são gravados em:
   - **No Workspace:** `{SeuWorkspace}\Logs\Crash\`
   - **Na pasta do usuário (Contingência):** `%LOCALAPPDATA%\OpenTEC-Hub\CrashDumps\`
4. Ao acionar o suporte técnico, anexe este arquivo `.log` para diagnóstico rápido.

---

*OpenTEC-Hub — Desenvolvido por Vitor Mazziero UNESP. Todos os direitos reservados.*
