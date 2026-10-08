# Pacote de decisões — finalização de kLa e receitas autônomas

Data: 08/10/2026. Contexto: [PLANO_FINALIZACAO.md](PLANO_FINALIZACAO.md) e [AUDITORIA_FINAL_RECEITAS.md](AUDITORIA_FINAL_RECEITAS.md).

Responda na linha **Resposta** de cada item (letra da opção ou texto livre). Itens marcados **(recomendado)** indicam a opção que eu seguiria sem outra orientação.

## Já decidido

| Item | Decisão | Registro |
|---|---|---|
| A-04 · Rampas no ambiente físico | Ficam habilitadas para teste em bancada de todos os destinos | D-058; roteiro em [ROTEIRO_BANCADA_RAMPAS.md](ROTEIRO_BANCADA_RAMPAS.md) |
| A-06 · Avisos de estilo | Chaves opcionais em linhas únicas; gate de zero avisos | D-059; compilação agora com zero avisos |
| Q1–Q4 | Restauração com reconexão e 3 tentativas; Hub mantém comandos; kLa autônomo físico sem bloqueios nem qualificação; pasta própria com nomes legíveis | D-060 a D-062 |

---

## Q1 · O que fazer quando a restauração de um ensaio biótico não pode ser confirmada (A-05)

**Você está certo:** se o ensaio não pode ser concluído (cancelamento, prazo, falha de análise, pausa, saída da cascata), o aplicativo **sempre** tenta primeiro restaurar as condições anteriores ao ensaio: rota de gás, vazão Q, agitação N, referências e controladores capturados no início. Isso já está implementado e testado nos dois protocolos.

A pergunta trata de outro caso: **a restauração foi enviada, mas o aplicativo não consegue comprovar que ela aconteceu.** Exemplos:

- o fluxômetro não informa a vazão restaurada dentro do prazo;
- a válvula não devolve eco;
- o servo não confirma a rota ou a rotação;
- o enlace com o Hub cai durante o retorno;
- a telemetria de OD para de chegar.

Nesse ponto o aplicativo não sabe o estado real do reator. Hoje ele faz o mesmo nos dois protocolos: envia **motor 0 rpm, gás fechado e monitor de O₂ desligado**, passa o controle para Manual e encerra a receita com falha. No abiótico isso é adequado. No biótico, deixa o cultivo **sem ar e sem agitação** até alguém intervir.

| Opção | Comportamento no biótico | Risco |
|---|---|---|
| A **(recomendado)** | Reenviar como comando de segurança a condição anterior ao ensaio (ar ao reator pela válvula A, Q e N do snapshot), travar o alarme "Retorno não confirmado", passar para Manual e encerrar a receita | Se o problema for o próprio atuador, o reenvio pode não ter efeito; o alarme exige ação do operador |
| B | Manter o comportamento atual: parar motor e gás | O cultivo fica sem oxigênio |
| C | Não enviar nada além do que já foi enviado; apenas alarme e Manual | Estado final depende do último comando aceito |

Abiótico: manter a parada total, salvo indicação contrária.

**Resposta (08/10/2026):** A, com até **três tentativas**. Se o enlace cair, aguardar a reconexão e reenviar: com a cascata ativa antes do ensaio, retomar a cascata; sem controle automático, reenviar o último setpoint anterior ao ensaio. Esgotadas as tentativas, manter apenas o alarme (sem parar motor e gás). O fluxômetro é o candidato mais provável a atrasos de poucos segundos.

## Q2 · Comportamento do Hub e dos nós quando o aplicativo perde a comunicação

O aplicativo não consegue garantir a aeração depois de perder o enlace (plano kLa §6.3). O resultado depende do firmware. Você sabe o que acontece hoje?

| Opção | Significado |
|---|---|
| A | O Hub mantém os últimos comandos (motor, válvulas e vazão continuam) |
| B | O Hub tem watchdog e leva tudo a um estado seguro (qual?) |
| C | Não sei; incluir como teste obrigatório na bancada **(recomendado se houver dúvida)** |

Isso define se o ensaio biótico autônomo pode ser liberado: se a recuperação depende apenas de comandos que podem não chegar, o biótico deve continuar bloqueado.

**Resposta (08/10/2026):** A. O Hub mantém os últimos comandos (firmware atual em `ESP32S3-HUB`); com o PC desligado durante a cascata, o ESP32-S3 continua nos últimos valores; após falta de energia do módulo, retoma um estado próximo do último.

## Q3 · Execução autônoma de kLa em bancada física

Hoje os blocos **Determinar kLa** e **Periodicidade** só executam no modo simulação (`--kla-test-file`). Isso é intencional no código: perfis, fábrica de execução e engine exigem `IsIsolatedSimulation`. Testar esses blocos no reator real exige uma entrega de código (pacote F9), não apenas uma configuração.

| Opção | Escopo |
|---|---|
| A **(recomendado)** | Implementar o modo bancada físico apenas para o **abiótico**; o biótico continua bloqueado por `KlaActuationRelease` até cumprir E7 |
| B | Abiótico e biótico físicos, com o biótico exigindo confirmação explícita de sessão de bancada a cada execução |
| C | Manter apenas simulação até concluir E7 |

**Resposta (08/10/2026):** B, **sem** confirmação explícita. Abiótico e biótico físicos sem bloqueadores de segurança; decisão do operador, que acompanha a bancada diretamente.

## Q4 · Como um perfil operacional físico deve ser criado (se Q3 = A ou B)

O perfil congela protocolo, limites, critérios de qualidade e confirmações de montagem (fonte de N₂ e, no biótico, isolamento de N₂). Hoje ele só pode ser importado de um arquivo JSON e não há criação dentro do aplicativo, por decisão de R4.2.

| Opção | Fluxo |
|---|---|
| A **(recomendado)** | Tela "Qualificar perfil" em Configurações: você preenche os limites, confirma a montagem e informa a evidência (por exemplo, a sessão manual de bancada que validou o retorno). Validade de N dias; troca de versão ao alterar qualquer limite |
| B | Somente importação de arquivo JSON preparado fora do aplicativo |

Se escolher A, informe a validade padrão em dias.

**Resposta (08/10/2026):** Sem qualificação de perfil. Ensaios salvos em pasta separada dentro de Testes-kLa, com data, réplica e valores definidos no nome; importáveis pelo próprio aplicativo para editar, recalcular o kLa e visualizar a curva. Pipeline simples, sem bloqueadores de execução.

## Q5 · Limites do perfil biótico do seu cultivo

Não há valores universais; o plano exige valores do seu sistema. Referências registradas em E0/E1: N 50–1000 rpm, Q 0,5–16 L/min, OD usual 30–100 %. Preencha ou indique "definir na bancada".

| Parâmetro | Valor |
|---|---|
| Piso de OD durante o corte de ar (%) | |
| Queda máxima de OD (pontos percentuais) | |
| Tempo máximo sem ar no reator por tentativa (s) | |
| Prazo máximo de recuperação (s) | |
| OD mínimo para considerar o cultivo recuperado (%) | |
| Intervalo mínimo entre ensaios (s) | |
| Máximo de tentativas no cultivo | |
| Tempo total máximo sem ar no cultivo (s) | |

**Resposta (08/10/2026):** piso de OD 5 %; queda máxima não necessária; tempo sem ar por tentativa não aplicável (o piso de 5 % encerra o corte); recuperação 600 s; intervalo entre ensaios 30 s de estabilização; tentativas e tempo total no cultivo não aplicáveis. **Aplicado (D-062):** padrões do bloco; o tempo máximo sem ar usa como teto de segurança o tempo máximo de desoxigenação da página kLa (5 min).

## Q6 · Limites do perfil abiótico

Referência de E1: remoção com N₂ a 100 rpm até alvo de OD de 5–20 %.

| Parâmetro | Valor |
|---|---|
| Agitação durante a remoção (rpm) | |
| OD alvo da remoção (%) | |
| Tempo máximo de desoxigenação (min) | |
| OD de término da reoxigenação (%) | |
| Tempo máximo de reoxigenação (min) | |

**Resposta (08/10/2026):** remoção a 700 rpm; alvo de remoção 20 % (o N₂ é cortado em 20 % e o OD acomoda perto de 12 % pela inércia da sonda); desoxigenação máxima 5 min; término da reoxigenação 85 %; reoxigenação máxima 600 s. Determinação ao vivo da curva fica como proposta futura (projeto `06_kLa_Modelo`). **Aplicado:** esses valores são lidos da página Determinar kLa › Configurações; ajuste-os lá uma vez.

## Q7 · Política de qualidade para aceitar uma réplica automaticamente

| Opção | Regra |
|---|---|
| A **(recomendado)** | Aceitar somente kLa `Válido`; condicional nunca conta como réplica |
| B | Aceitar também `Condicional` para motivos que você listar |

Motivos que podem gerar nova tentativa automática (marque os aceitos): janela insuficiente, ruído excessivo, condição instável. Padrão atual: nenhum, uma tentativa por réplica.

**Resposta (08/10/2026):** A. **Explicação de "condicional":** o kLa recebe uma de três qualidades. *Válido*: todos os critérios científicos atendidos. *Condicional*: há valor numérico, mas com ressalva registrada — por exemplo janela curta, tempo de resposta da sonda comparável ao processo, Ceq sensível à escolha da janela ou OUR não confirmado de forma independente. *Inconclusivo*: não há estimativa confiável. Com A, só *Válido* conta como réplica; o condicional fica salvo e visível, mas não é selecionado.

## Q8 · Resultado inconclusivo dentro da receita

| Opção | Comportamento |
|---|---|
| A **(recomendado)** | Restaurar e interromper a receita (padrão atual) |
| B | Restaurar, registrar advertência e continuar sem resultado |

A escolha continua configurável por bloco; a pergunta define o padrão.

**Resposta (08/10/2026):** B — restaurar, registrar advertência e continuar sem resultado. Aplicado como padrão do bloco (D-062).

## Q9 · Tolerâncias de confirmação das rampas

Uma rampa só termina quando cada destino confirma o alvo final dentro da tolerância, estável pelo tempo indicado. Padrões atuais:

| Critério | Atual | Seu valor |
|---|---|---|
| Temperatura | ±0,5 °C | |
| Agitação | ±2 rpm | |
| Vazão | ±0,1 L/min | |
| pH | ±0,05 | |
| Pressão | ±0,5 kPa | |
| Tempo de estabilidade | 10 s | |
| Intervalo máximo entre amostras | 5 s | |
| Prazo de confirmação | 300 s | |

Observação: ±2 rpm pode ser estreito para o ruído do servo; o banho pode precisar de mais de 300 s para estabilizar.

**Resposta (08/10/2026):** agitação ±5 rpm, vazão ±0,2 L/min, pH ±0,2 (só critério de conclusão da rampa, não a histerese do controle), pressão ±1 kPa; temperatura mantida em ±0,5 °C. **Explicação do prazo de confirmação:** depois de enviar o valor final, a rampa espera que a *medição do processo* fique dentro da tolerância durante o tempo de estabilidade (10 s). No banho, a medição é a temperatura do **reator**, não a do banho. Se isso não acontecer dentro do prazo, a rampa termina com falha em vez de declarar conclusão. **Aplicado provisoriamente:** 900 s. Responda se prefere outro valor (por exemplo 1200 s).

## Q10 · Intervalo mínimo entre comandos da rampa

A rampa envia somente mudanças representáveis pelo dispositivo, mas o intervalo mínimo atual entre comandos é **100 ms**. Para banho e fluxômetro via Wi-Fi isso pode congestionar o enlace.

| Opção | Intervalo |
|---|---|
| A **(recomendado)** | 1 s para todos os destinos |
| B | 2 s para banho/vazão e 1 s para os demais |
| C | Manter 100 ms |

**Resposta (08/10/2026):** A — 1 s para todos os destinos. Aplicado (D-063).

## Q11 · Política padrão ao cancelar uma rampa

| Opção | Comportamento |
|---|---|
| A **(atual)** | Manter as últimas referências enviadas |
| B | Restaurar as referências do início da rampa |

**Resposta (08/10/2026):** A — manter as últimas referências. Padrão mantido.

## Q12 · Evidências de teste dentro do repositório (A-07)

A cada regressão, os testes de renderização regravam cerca de 60 PNGs versionados em `docs/evidence` e `docs/plans/*/evidence`. Por isso o `git status` mostra imagens modificadas sem alteração real.

| Opção | Comportamento |
|---|---|
| A **(recomendado)** | Gravar em pasta temporária por padrão; atualizar a evidência versionada só com `OPENTEC_UPDATE_EVIDENCE=1` |
| B | Manter como está |

**Resposta (08/10/2026):** A. Aplicado (D-064). Duplicatas de sincronização do OneDrive (`*-DESKTOP-J4OP7IO*.png`, `*-NOTEBOOK-ACER-ASPIRE*.png`) e relatórios intermediários não citados foram retirados da árvore.

## Q13 · Perfil sintético para testar o modo simulação (A-08)

Para executar os blocos de kLa na build atual, é preciso abrir com `--kla-test-file` e importar um perfil que corresponda ao `InstallationId` do seu workspace (criado no primeiro uso, em `Receitas/AutomacaoKla/simulacao/context.json`).

| Opção | Comportamento |
|---|---|
| A **(recomendado)** | Script em `tools/` que lê esse `context.json` e gera perfis abiótico e biótico marcados como simulação; o aplicativo continua sem criar perfis |
| B | Você prepara os perfis manualmente |

**Resposta (08/10/2026):** A. Com D-061/D-062 os blocos executam na build comum sem perfil importado; o gerador de perfil sintético tornou-se desnecessário.

## Q14 · Git

1. Posso enviar ao GitHub (`main`) os commits locais desta auditoria? Hoje há 3 commits de código/teste e os de documentação a seguir.
2. Há trabalho paralelo não incluído: alterações no firmware e na documentação do banho termostático, cerca de 200 TRX de evidência não rastreados em `receitas-r2*/r3*/r4*` e `Screenshot 2026-10-07 100744.png` na raiz. O que devo fazer?
   - A: não tocar (você cuida);
   - B: commitar os TRX de evidência em um commit separado;
   - C: outra orientação.

**Resposta (08/10/2026):** 1 — enviar. 2 — avaliar o trabalho paralelo e, se os testes passarem, commitar separadamente. **Aplicado:** BathClient r3.3 (simulações e 135 contratos do Hub aprovados) em commit próprio; os quatro relatórios citados por recibos foram versionados.

## Q15 · Versão da próxima liberação

A build atual é `0.26.5-dev.N` (MinVer). Ao concluir a bancada, qual tag usar?

| Opção | Tag |
|---|---|
| A **(recomendado)** | `v0.27.0`, com kLa abiótico/biótico, receitas autônomas e rampas |
| B | `v0.26.5` |

**Resposta (08/10/2026):** A — `v0.27.0`.
