# Evidência — documentação no aplicativo e reorganização da página de Potência

Referente a [D-047](../../DECISIONS.md) · capturado em 10/09/2026.

## Gerado pela suíte

Estes arquivos são **reproduzidos**, não colecionados: `DocumentationEvidenceTests` renderiza a
árvore visual real e regrava cada um a cada execução da suíte. Uma captura desatualizada de uma
tela já corrigida é pior do que nenhuma, então nada aqui é copiado à mão.

| Arquivo | O que mostra |
|---|---|
| `documentacao-painel.png` | Configurações → Documentação, assunto **Painel**, ocupando a página inteira |
| `documentacao-controle.png` | O mesmo, assunto **Controle** |
| `documentacao-potencia-tara.png` | O destino de um botão “?” da página de Potência |
| `potencia-montagem.png` | Aba Montagem com o card **Ensaio** dividido em subcards e o botão **Carregar Ensaio** |
| `potencia-validacao.png` | Aba Validação sem os parágrafos explicativos, com a tabela de correlação legível |

Para regravar: `dotnet test Windows_app/OpenTECHub.slnx --filter DocumentationEvidenceTests`.

## Origem — sessão do operador em 09/09/2026

As telas que motivaram o trabalho, capturadas na bancada. São o estado **anterior** e a
referência do que a documentação precisa descrever.

| Arquivo | O que mostra |
|---|---|
| `origem-painel-2026-09-09.png` | Painel: cartões de parâmetros internos e dispositivos externos, dois gráficos, barra de ferramentas e barra de estado |
| `origem-controle-variaveis-2026-09-09.png` | Controle: tabela das variáveis internas e início dos dispositivos externos |
| `origem-controle-dispositivos-2026-09-09.png` | Controle: dispositivos externos completos e a barra de predefinições e parada segura |

As capturas de cada linha **expandida** da página Controle — servo, temperatura, pH, oxigênio,
nutrientes, antiespumante, vazão de ar, distância, bomba externa, absorbância e frasco agitador —
foram fornecidas pelo operador na revisão de 10/09/2026 e são a fonte do que o tópico *Controle*
descreve campo a campo. Elas ainda não estão nesta pasta: chegaram como imagens na revisão, não
como arquivos. Salvando-as aqui com o prefixo `origem-controle-detalhe-<variável>.png`, a
tabela acima passa a cobrir todo o material de origem.
