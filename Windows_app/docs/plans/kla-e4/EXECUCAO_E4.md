# E4 — Interface comum

Implementação em software de 06/10/2026. A validação física do equipamento permanece em E7.

## Organização e procedimento

Abiótico/Biótico e Único/Múltiplos compartilham preparação, gráficos, aquisição e revisão. A condição única usa a mesma definição de N e Q da matriz. Um resultado pode ser salvo e exportado sem mapa.

O cabeçalho reúne seletores e leituras numa faixa compacta. Preparação, execução e revisão ocupam a lateral; o gráfico de oxigênio recebe a altura disponível. Regressão e diagnóstico ficam em abas, com opção “Ver todos”. Em janela baixa, apenas o gráfico selecionado aparece.

O procedimento biótico aparece como Preparar → Medir consumo → Reoxigenar → Restaurar cultivo → Revisar. Durante o consumo, o fluxômetro continua ligado e o ar vai para o escape. Reoxigenar comuta as válvulas para o reator; não liga novamente o fluxômetro. A confirmação interna de comutação continua necessária. N₂ deve estar fechado e isolado na fonte.

No abiótico, a remoção por N₂ e a pré-estabilização do ar continuam específicas do protocolo. Os valores de preparo, faixas, prazos e características da sonda são editáveis.

## Revisão científica e persistência

Novas sessões usam o núcleo determinístico de E3 com OD calibrado e eventos de gás confirmados. Ausência desses eventos não autoriza reconstrução artificial das fases. Revisões históricas preservam a análise e a versão salvas.

A revisão mostra janela, fases, Ceq, kLa, OUR quando aplicável, qualidade e hipóteses. Resultado indisponível aparece como “—”. OUR aparente não se torna validado apenas por ajustar uma curva. Janelas e hipóteses manuais geram revisões persistidas; exportação é independente de mapa. Aceitar um resultado biótico exige retorno físico confirmado.

O caminho da sessão é apresentado com ação para abrir a pasta. Salvar revisão, aceitar e rejeitar usam números de revisão distintos, preservando o histórico.

## Verificação

Os testes de interface cobrem quatro combinações de protocolo/captura, confirmação de N₂, definição imutável, reanálise, exportação e bloqueio por restauração. A renderização WPF cobre janela de 936×534 e 1680×980 e DPI 100%, 125% e 150%. As imagens em `docs/evidence/ui-kla-e4` são renderizações da aplicação com dispositivos simulados; não comprovam atuação em bancada.

Os diálogos possuem ciclo de navegação por Tab, foco inicial e fechamento por Escape. A avaliação física de teclado em uma janela interativa e a liberação operacional pertencem às verificações finais de E7.

Fila avançada, compatibilidade dos mapas e receitas permanecem nas etapas E5/E6.
