# Simulação de Testes de kLa com arquivo experimental

## Objetivo

O TECNAL-Hub pode ser iniciado em um modo offline dedicado à página **Determinar kLa**. Nesse modo,
nenhuma conexão USB ou Wi-Fi é tentada e nenhum comando alcança hardware. A interface e a máquina
de estados, entretanto, usam o mesmo `CommandArbiter`, o mesmo `KlaTestRunner`, os mesmos ACKs e os
mesmos contratos de persistência usados em campo.

## Arquivo avaliado

Arquivo padrão do lançador:

```text
D:\OneDrive\Doutorado_CNPq\_Artigos_e_Coorientacoes\Artigos\06_kLa_Modelo\Dados\Testes bioticos e abioticos\TRANSF O2 P1 2026_0,5 VVM.txt
```

Características verificadas:

- 2.092 amostras tabuladas;
- tempo entre 26,96 e 96,90 min;
- OD entre 4,540% e 99,908%;
- cadência predominante próxima de 2 s;
- primeira sequência: desoxigenação de 9,122% até 4,540%, seguida por reoxigenação;
- rotações registradas entre 50 e 800 rpm;
- fluxômetro registrado como `-1`, portanto indisponível no arquivo.

O arquivo é adequado para testar OD, gráficos, transições por limiar, revisão e cálculo. Vazão,
válvulas, estado online, `FlowCommandId`, `FlowCommandAck` e `FlowCommandPending` são simulados a
partir dos comandos enviados pelo app. Esses campos não são apresentados como medições do arquivo.

## Como iniciar

Dê duplo clique em:

```text
tools\RunKlaFileSimulation.cmd
```

O lançador usa o arquivo avaliado e velocidade `10×`. Também aceita outro arquivo e outra
velocidade:

```text
tools\RunKlaFileSimulation.cmd "D:\dados\outro-teste.txt" 5
```

Alternativamente, use diretamente:

```text
TecnalHub.exe --kla-test-file "D:\dados\teste.txt" --kla-test-speed 10
```

A velocidade aceita valores de `0.1×` a `100×`.

## Comportamento esperado

1. O app abre diretamente em **Determinar kLa**.
2. O título, a conexão e o cabeçalho exibem claramente **SIMULAÇÃO kLa**.
3. A primeira leitura aparece, mas o arquivo permanece pausado enquanto o teste é configurado.
4. Ao iniciar uma condição, o fechamento geral recebe ACK simulado.
5. Como a primeira OD é 9,122% e o padrão de `DO mínima` é 5%, o app abre N₂ e reproduz o trecho
   descendente.
6. Ao atingir 5%, o app fecha N₂, confirma o intertravamento e abre ar.
7. O trecho ascendente alimenta OD, log-linear e kLa instantâneo.
8. Ao atingir `DO máxima`, o app fecha o gás, confirma o ACK e abre a revisão científica.
9. Os arquivos gerados continuam sendo gravados normalmente em `Testes-kLa/<nome-do-teste>`.

Ao iniciar outra corrida, o simulador procura o próximo trecho descendente disponível. Isso permite
percorrer sucessivos ciclos do arquivo sem depender do momento em que o operador concluiu a revisão.

## Formato aceito

O arquivo deve ser separado por tabulações e possuir, no mínimo:

```text
Time ...    Oxygen
```

As colunas `Temperature ...` e `Motor ...` são aproveitadas quando presentes. Linhas inválidas são
ignoradas. O arquivo precisa conter pelo menos cinco amostras válidas.

## Limites da simulação

- Não comprova temporização, falha elétrica ou fechamento físico das válvulas.
- Não valida comunicação entre ESP32-S3 v7 e fluxômetro v05.
- Vazão e ACKs são emulados de forma determinística.
- A rotação do arquivo é metadado experimental; a rotação comandada pelo teste continua sendo
  registrada pelo runner porque o protocolo real não oferece eco confiável desse setpoint.
- O resultado de kLa é apropriado para testar o fluxo analítico e a interface, não para substituir a
  validação física do conjunto reator, sonda e fluxômetro.
