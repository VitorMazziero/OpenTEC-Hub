# Plano de integração — Contemp C404 + ESP32-S3

## Objetivo

Adicionar controle remoto do **setpoint** do banho ultratermostático sem alterar o controle PID original do C404.

A temperatura da dorna será monitorada por um sistema/sensor já existente.  
**Não será instalado sensor de temperatura adicional no ESP32.**

---

## Arquitetura adotada

```text
                 CONTEMP C404
        ┌────────────────────────┐
        │ Controle PID original  │
        │                        │
        │  *    ▲    ▼    ENTER │
        └──┬────┬────┬─────┬─────┘
           │    │    │     │
           │ contatos secos dos relés
           │    │    │     │
        ┌──┴────┴────┴─────┴─────┐
        │    Módulo HW-280        │
        │      4 relés / 5 V      │
        └──────────┬──────────────┘
                   │ IN1...IN4
              ┌────┴─────┐
              │ ESP32-S3 │
              │   Wi-Fi  │
              └──────────┘
```

O C404 continua responsável por:

- leitura do seu sensor original;
- controle PID;
- aquecimento/refrigeração;
- proteções e funcionamento local.

O ESP32-S3 apenas **simula o pressionamento das teclas**.

---

## Hardware escolhido

- 1 × ESP32-S3 DevKit
- 1 × módulo de 4 relés **HW-280**
- Relés do módulo: **JQC3F-05VDC-C**
- Fios finos para ligação ao painel do C404
- Alimentação 5 V para ESP32 + módulo de relés

O IRLZ44N e os relés individuais não serão utilizados.

---

## Configuração do HW-280

Cada canal possui seleção `H/L`.

Foi adotado:

```text
jumper entre CENTRAL e L
```

para os quatro canais.

Portanto, os relés serão acionados por nível lógico **LOW**.

```text
GPIO HIGH -> relé desligado
GPIO LOW  -> relé acionado
```

---

## Ligação entre ESP32-S3 e HW-280

Mapeamento inicial sugerido:

```text
ESP32-S3        HW-280

GPIO 4   ------ IN1  -> tecla *
GPIO 5   ------ IN2  -> tecla ▲
GPIO 6   ------ IN3  -> tecla ▼
GPIO 7   ------ IN4  -> tecla ENTER

GND      ------ DC-/GND do módulo
5 V      ------ DC+ do módulo
```

> Os GPIOs podem ser alterados posteriormente no firmware.

---

## Ligação dos relés aos botões do C404

Usar somente os terminais:

```text
COM + NO
```

de cada relé.

O terminal `NC` não será utilizado.

Cada relé funciona como um segundo botão conectado em paralelo ao botão físico:

```text
             botão original
A o------------/ ------------o B
  |                         |
  +------ COM  RELÉ  NO ----+
```

Quando o relé fecha `COM-NO`, o C404 interpreta como se o botão tivesse sido pressionado.

---

## Botões ▲ e ▼ — terminal comum confirmado

O teste de continuidade com multímetro confirmou que os botões **▲ e ▼ compartilham um terminal comum**.

Assim, existem **7 conexões elétricas únicas**, e não 8.

Estrutura:

```text
                     COMUM ▲/▼
                         |
                 +-------+-------+
                 |               |
             COM relé ▲       COM relé ▼
                 |               |
                NO              NO
                 |               |
            terminal ▲      terminal ▼
```

O fio comum pode ser soldado uma única vez na placa do C404 e bifurcado externamente para os dois relés.

---

## Total de fios entre painel C404 e módulo de relés

```text
Tecla *      -> 2 fios
Tecla ▲      -> 1 fio próprio + comum
Tecla ▼      -> 1 fio próprio + mesmo comum
Tecla ENTER  -> 2 fios
-----------------------------------------
Total        -> 7 fios únicos
```

---

## Teste elétrico realizado nos botões

Com o C404 desligado da tomada e multímetro em continuidade:

- botão solto -> circuito aberto;
- botão pressionado -> continuidade;
- terminal comum entre ▲ e ▼ confirmado.

Isso valida a ligação dos relés em paralelo com os botões.

---

## Alimentação

Foram identificados na placa do C404 pontos marcados:

```text
+5V
GND
```

A fonte interna do C404 utiliza um conversor flyback baseado em **TNY264PN**.

### Situação atual

É possível testar ESP32 + HW-280 a partir desses 5 V, mas a capacidade disponível da fonte do C404 ainda não foi confirmada.

Como somente **um relé será acionado por vez e por poucos milissegundos**, a carga dos relés é reduzida, porém o ESP32-S3 pode apresentar picos de corrente durante o Wi-Fi.

Antes de adotar definitivamente os 5 V internos:

1. medir a tensão `+5V -> GND` com o C404 sozinho;
2. conectar somente o ESP32 e verificar estabilidade;
3. ativar Wi-Fi e observar a tensão;
4. conectar o HW-280;
5. acionar um relé por vez;
6. verificar se não há queda relevante de tensão ou reset do C404.

Se houver instabilidade, utilizar uma **fonte 5 V dedicada**.

---

## Comportamento do firmware

Para simular um toque:

```cpp
digitalWrite(RELAY_PIN, LOW);
delay(150);
digitalWrite(RELAY_PIN, HIGH);
```

Pulso inicial recomendado:

```text
150–200 ms
```

O firmware deverá manter todos os relés desligados (`HIGH`) durante inicialização/reset.

---

## Sequência de desenvolvimento

1. Montar ESP32-S3 + HW-280.
2. Configurar os quatro canais em `L`.
3. Testar um canal do relé fora do C404.
4. Ligar `COM/NO` do primeiro relé em paralelo com uma tecla.
5. Confirmar que um pulso de 150–200 ms equivale a um toque físico.
6. Repetir para as quatro teclas.
7. Implementar interface Wi-Fi.
8. Implementar rotina para alteração automática do setpoint.
9. Validar comportamento após reboot e perda de Wi-Fi.

---

## Princípio de segurança adotado

Falha do ESP32 ou perda de Wi-Fi não deve interromper o controle térmico:

```text
ESP32 desligado/falha
        |
        v
relés abertos
        |
        v
botões físicos continuam funcionando
        |
        v
C404 continua controlando o banho normalmente
```

O ESP32 não comandará diretamente aquecimento, refrigeração ou o sensor original do C404.
