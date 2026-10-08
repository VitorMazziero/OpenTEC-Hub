# Roteiro de bancada — kLa autônomo em receitas (R6.2 / E7)

Data: 08/10/2026. Decisões: [D-060 a D-062](../DECISIONS.md). Build: instalador `OpenTECHub_Setup_v0.27.1`.

Objetivo: comprovar no reator os blocos **Determinar kLa** e **Periodicidade**, nos protocolos abiótico e biótico, e os caminhos de retorno. A aprovação em software está na [auditoria final](AUDITORIA_FINAL_RECEITAS.md).

## Preparação (uma vez)

1. Abra o aplicativo sem `--kla-test-file` (ambiente físico) e conecte o Hub.
2. Em **Determinar kLa › Configurações**, ajuste os valores decididos (Q6): agitação de remoção 700 rpm, OD alvo da remoção 20 %, desoxigenação máxima 5 min, término da reoxigenação 85 %, reoxigenação máxima 10 min. O bloco autônomo lê esses valores no início de cada ensaio.
3. O nome da receita é o identificador dos ensaios (não há mais painel de preparação); escolha um nome que reconheça na pasta.
4. No bloco **Determinar kLa**, o perfil já vem como **operador · atual**. Padrões: OD mínimo biótico 5 %, prazo de retorno 600 s, intervalo entre ensaios 30 s, demais limites "0 = não aplicável", resultado inconclusivo continua após restaurar.
5. Resultados: `Testes-kLa/Receitas-automaticas/<receita>_<data>_<Abiotico|Biotico>_<Unico|Matriz>_N…_Q…/Corridas/N0300_Q02p00_Rep01/`. Também aparecem em **Determinar kLa › Carregar ensaio**.

## Casos

| # | Caso | Receita | Resultado esperado | OK? |
|---|---|---|---|---|
| K1 | Abiótico único, condição atual | `kla-abiotico-unico.recipe.json` com "condições atuais" | N₂ até 20 %, ar, reoxigenação até 85 %; retorno a N/Q e rota anteriores confirmado; pasta com data e N/Q | |
| K2 | Abiótico matriz | `kla-abiotico-matriz.recipe.json` | Duas condições × duas réplicas; entre corridas, controle devolvido e 30 s de intervalo | |
| K3 | Biótico único | `kla-biotico-unico.recipe.json` | Ar desviado; corte termina no piso de 5 % ou no tempo máximo; retorno e OD estável antes de liberar | |
| K4 | Biótico matriz | `kla-biotico-matriz.recipe.json` | Mesma lógica por condição; nenhuma espera por operador | |
| K5 | Periódico com Controle de O₂ | `kla-periodico-2h-4h.recipe.json` com períodos curtos (ex.: 5 min e 10 min) | Cascata suspensa durante o ensaio e retomada sem salto; disparos sem acúmulo; cada disparo em pasta própria (`_disparo01`, `_disparo02`…) | |
| K6 | Pausa durante o ensaio | Pausar em K3 | Ensaio interrompido, retorno executado, receita parada até retomar | |
| K7 | Parar a receita no corte de ar | Parar em K1 e em K3 | Retorno ao estado anterior confirmado antes do fim | |
| K8 | Fluxômetro cai no retorno | Desligar o fluxômetro por ~10 s ao fim de K3 | Retorno reenviado até 3 vezes; log mostra "tentativa n/3"; se confirmar, receita segue | |
| K9 | Retorno não confirmado | Manter o fluxômetro desligado em K3 | Após 3 tentativas: alarme **Retorno do ensaio kLa não confirmado**; motor e gás **não** são parados no biótico; posse volta ao Manual | |
| K10 | Perda do enlace PC–Hub | Desligar o Hub ou desconectar o USB durante K3 e religar após 1–2 min | Receita **mantida** com "Receita aguardando dispositivo"; Hub mantém os últimos comandos; ao reconectar, retorno ao estado anterior (até 3 tentativas) e a cascata volta a comandar sem salto (D-065) | |
| K13 | Fluxômetro cai com cascata ativa | Desligar o fluxômetro por ~30 s com o Controle de O₂ (Automático ou receita) | Cascata continua engatada; alarme de fluxômetro offline; ao voltar, segue regulando | |
| K14 | Sonda de O₂ sem leitura | Desconectar a sonda por ~30 s com cascata ativa | Cascata em espera sem comandar; ao voltar a leitura, retoma sem salto | |
| K15 | Desconexão pelo operador | Botão Desconectar durante cascata | Volta ao Manual (comportamento de segurança mantido) | |
| K11 | Emergência | Parada de emergência durante K3 | Saídas seguras; nenhum religamento pelo ensaio | |
| K12 | Recalcular | Em uma sessão de K1/K3, **Criar cópia editável para recalcular** | Cópia `_edicao` em `Testes-kLa`; curva visível; recálculo e decisão manual disponíveis; original inalterada | |

## Critério

Registre data, receita, valores observados, pasta da sessão e alarmes em `RECIBO_BANCADA_KLA.md` neste diretório. Um caso reprovado vira correção ou nova decisão; a liberação física permanece por decisão do operador (D-061).
