# OpenTEC-Hub — Manual do Operador

> **Versão do Documento:** 1.0 · **Versão do Software:** 0.24.0+  
> **Data:** 05/09/2026  
> **Público-Alvo:** Operadores de biorreatores, pesquisadores de bioprocessos e técnicos de laboratório  
> **Compatibilidade de Hardware:** Plataforma TECNAL / USP com módulo mestre ESP32-S3 Hub e periféricos inteligentes

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
- **Ensaios de Potência (`Testes-Potencia/` e `Mapas-Potencia/`):** Curvas de $N_p$ e aeração.
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

---

## 8. Ensaios Especiais

### A. Determinação Abiótica de $k_L a$ (Gassing-Out Dinâmico)
O módulo **Determinação de kLa** automatiza o ensaio de transferência de oxigênio gás-líquido:
1. **Configuração da Campanha:** Defina as condições de ensaio na matriz (combinações de vazão de ar em L/min e agitação em rpm) e o número de réplicas.
2. **Execução Automática:**
   - O sistema aciona a desoxigenação com $N_2$ até atingir o limite inferior configurado (ex.: $< 5\%$).
   - Aguarda o tempo de estabilização pós-nitrogênio baseado na derivada da curva de oxigênio (garantindo ausência de microbolhas residuais).
   - Comuta para injeção de ar (com estabilização prévia de vazão na linha de alívio para evitar transientes na reoxigenação).
   - Registra a subida da curva de oxigênio dissolvido em alta frequência.
3. **Análise Log-Linear e Aceite:**
   - O algoritmo OLS calcula automaticamente a inclinação da curva $\ln(C^* - C_L)$ vs tempo, estimando $k_L a$ ($h^{-1}$), $R^2$, resíduos e intervalos de confiança de 95%.
   - Se o modo **Aceite Automático** estiver ativo, o runner avança sozinho para a próxima condição experimental da sequência.

### B. Mapeamento de Potência de Impelidor
O módulo **Potência** realiza a caracterização hidrodinâmica mecânica do vaso:
1. **Tara Mecânica:** Realiza a curva de torque em vazio (sem líquido) em várias rotações para descontar atrito de mancais e selos mecânicos.
2. **Ensaio Não Gaseificado ($N_p \times Re$):** Mede o torque estático e dinâmico com o volume de líquido nominal, determinando o Número de Potência $N_p$ característico do impelidor.
3. **Ensaio Gaseificado ($P_G / P_0$ e Flooding):** Varia a vazão de gás e rotação, mapeando a perda de potência por cavitação de bolhas e detectando o limite de inundação (*flooding*) do impelidor.

---

## 9. Elaboração e Execução de Receitas

No módulo **Receitas**, o usuário pode desenhar bateladas automatizadas conectando blocos de processo:
- **Blocos Básicos:** Espera temporizada, Rampa de temperatura, Degrau de agitação, Pulso de alimentação de nutriente.
- **Blocos Condicionais:** Aguardar pH atingir determinado valor, aguardar consumo de oxigênio, dosagem automática por sensor de espuma.
- **Interlocks de Segurança:** A receita verifica continuamente se as sondas estão online. Se um periférico configurado como obrigatório perder conexão por mais de 8 segundos, a receita entra em pausa de segurança (*Hold*) e notifica o operador.

---

## 10. Alarmes, Históricos e Diagnóstico de Falhas

### Monitor de Alarmes
- O sistema classifica os desvios em: **Informativo** (azul), **Alerta** (amarelo) e **Crítico** (vermelho).
- Alarmes críticos emitem aviso sonoro contínuo no computador da sala de controle. O operador pode clicar em **Silenciar** para pausar o áudio por 5 minutos enquanto corrige a causa física.

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

*OpenTEC-Hub — Desenvolvido pela TECNAL / USP. Todos os direitos reservados.*
