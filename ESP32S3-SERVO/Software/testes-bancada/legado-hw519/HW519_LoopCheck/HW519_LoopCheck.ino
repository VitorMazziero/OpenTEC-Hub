#include <Arduino.h>

/*
 * Teste eletrico do laco D10 -> HW-519 -> A/B -> HW-519 -> D11.
 *
 * Nao usa UART nenhuma no barramento: so nivel logico. Por isso nao
 * tem restricao de baud, de formato, de paridade nem de temporizacao,
 * e funciona mesmo que a direcao automatica do modulo seja lenta.
 *
 * ------------------------------------------------------------------
 * O ARTEFATO QUE ESTA VERSAO EXISTE PARA ELIMINAR
 * ------------------------------------------------------------------
 * A versao anterior lia o D11 como INPUT puro -- alta impedancia. Se o
 * TXD do modulo NAO estiver ligado ao D11, o pino fica flutuante, e o
 * jumper do D10 correndo ao lado acopla capacitivamente o suficiente
 * para o flutuante seguir o vizinho. Amostrando 400 us depois da
 * borda, isso produz 1000/1000 -- exatamente o mesmo numero de uma
 * conexao boa. O resultado nao provava nada.
 *
 * O pull-up interno do AVR (20 a 50 kohm) separa os dois casos:
 *
 *   pino flutuante  -> o pull-up vence o acoplamento capacitivo.
 *                      D11 fica em ALTO mesmo com D10 em BAIXO.
 *
 *   saida real (RO) -> push-pull do transceptor vence 30 kohm sem
 *                      esforco. D11 acompanha o D10 como antes.
 *
 * Logo, o veredito esta numa unica pergunta:
 *
 *   COM PULL-UP LIGADO E D10 EM BAIXO, O D11 CAI?
 *     cai   -> o TXD do modulo esta mesmo no D11. Laco real.
 *     nao   -> nao esta. Estavamos surdos o tempo todo, e todo
 *              "silencio" registrado ate aqui e inconclusivo.
 *
 * Somente leitura no sentido Modbus: nao existe quadro valido aqui,
 * so um pulso quadrado. O drive descarta como erro de enquadramento,
 * e o CN3 e porta de telemetria, nao de comando.
 */

constexpr uint8_t PIN_TX = 10;  // vai ao RXD do HW-519
constexpr uint8_t PIN_RX = 11;  // vem do TXD do HW-519

uint16_t sampleHigh(uint8_t level) {
  digitalWrite(PIN_TX, level);
  delay(20);

  uint16_t high = 0;
  for (uint16_t index = 0; index < 200; ++index) {
    if (digitalRead(PIN_RX) == HIGH) {
      ++high;
    }
    delayMicroseconds(50);
  }
  return high;
}

void describe(uint16_t high) {
  Serial.print(high);
  Serial.print(F("/200  ("));
  if (high >= 195) {
    Serial.print(F("estavel em 1"));
  } else if (high <= 5) {
    Serial.print(F("estavel em 0"));
  } else {
    Serial.print(F("INSTAVEL"));
  }
  Serial.println(F(")"));
}

// Devolve quantos ciclos o D11 acompanhou, de CYCLES, em cada nivel.
void toggleTest(uint16_t *followedLow, uint16_t *followedHigh, uint16_t cycles) {
  *followedLow = 0;
  *followedHigh = 0;

  for (uint16_t index = 0; index < cycles; ++index) {
    digitalWrite(PIN_TX, LOW);
    delayMicroseconds(400);
    if (digitalRead(PIN_RX) == LOW) {
      ++(*followedLow);
    }

    digitalWrite(PIN_TX, HIGH);
    delayMicroseconds(400);
    if (digitalRead(PIN_RX) == HIGH) {
      ++(*followedHigh);
    }
  }
}

void runPass(uint8_t rxMode, const __FlashStringHelper *title) {
  constexpr uint16_t CYCLES = 500;

  pinMode(PIN_RX, rxMode);
  digitalWrite(PIN_TX, HIGH);
  delay(30);

  Serial.println();
  Serial.println(title);

  Serial.print(F("  D10 em ALTO  -> D11 alto em "));
  describe(sampleHigh(HIGH));
  Serial.print(F("  D10 em BAIXO -> D11 alto em "));
  describe(sampleHigh(LOW));

  digitalWrite(PIN_TX, HIGH);
  delay(20);

  uint16_t low = 0;
  uint16_t high = 0;
  toggleTest(&low, &high, CYCLES);

  Serial.print(F("  Chaveando: acompanhou BAIXO em "));
  Serial.print(low);
  Serial.print('/');
  Serial.print(CYCLES);
  Serial.print(F(", ALTO em "));
  Serial.print(high);
  Serial.print('/');
  Serial.println(CYCLES);

  digitalWrite(PIN_TX, HIGH);
}

void setup() {
  Serial.begin(115200);
  delay(300);

  pinMode(PIN_TX, OUTPUT);
  digitalWrite(PIN_TX, HIGH);
  delay(50);

  Serial.println();
  Serial.println(F("=== Laco TTL: D10 -> conversor -> D11 ==="));
  Serial.println(F("Serve para o HW-519 e para o MAX3232."));

  /*
   * Quatro assinaturas distintas, e o veredito antigo confundia todas.
   * O que separa uma da outra:
   *
   *   segue sem pull-up E com pull-up   ligacao solida
   *   segue sem, nao segue com          caminho resistivo (megaohms)
   *   leitura aleatoria, ignora o D10   pino FLUTUANTE, desconectado
   *   cravado em alto nas duas          segurado por algo; driver nao
   *                                     chaveia (ou o modulo nao ecoa)
   */
  pinMode(PIN_RX, INPUT);
  const uint16_t plainWhenHigh = sampleHigh(HIGH);
  const uint16_t plainWhenLow = sampleHigh(LOW);

  Serial.println();
  Serial.println(F("PASSAGEM 1 -- D11 como INPUT (controle)"));
  Serial.print(F("  D10 ALTO  -> D11 alto em "));
  describe(plainWhenHigh);
  Serial.print(F("  D10 BAIXO -> D11 alto em "));
  describe(plainWhenLow);

  digitalWrite(PIN_TX, HIGH);
  pinMode(PIN_RX, INPUT_PULLUP);
  delay(30);
  const uint16_t pulledWhenLow = sampleHigh(LOW);

  Serial.println();
  Serial.println(F("PASSAGEM 2 -- D11 como INPUT_PULLUP (discriminador)"));
  Serial.print(F("  D10 BAIXO -> D11 alto em "));
  describe(pulledWhenLow);

  digitalWrite(PIN_TX, HIGH);
  pinMode(PIN_RX, INPUT);

  const bool followsPlain = (plainWhenHigh >= 190) && (plainWhenLow <= 10);
  const bool floatingHigh = (plainWhenHigh > 20) && (plainWhenHigh < 180);
  const bool floatingLow = (plainWhenLow > 20) && (plainWhenLow < 180);

  Serial.println();
  Serial.println(F("------------------------------------------------------------"));
  if (followsPlain && pulledWhenLow <= 10) {
    Serial.println(F("LIGACAO SOLIDA. O D11 acompanha o D10 e aguenta o"));
    Serial.println(F("pull-up de 30 kohm. So uma saida push-pull faz isso."));
    Serial.println(F("O caminho esta provado: silencio no Modbus e legitimo."));
  } else if (followsPlain) {
    Serial.println(F("CAMINHO RESISTIVO. Acompanha estatico, mas o pull-up"));
    Serial.println(F("de 30 kohm ganha dele. Conducao da ordem de megaohms:"));
    Serial.println(F("solda fria, fio partido dentro da capa, ou contato"));
    Serial.println(F("oxidado. Refazer a ligacao do D11."));
  } else if (floatingHigh && floatingLow) {
    Serial.println(F("PINO FLUTUANTE. A leitura e aleatoria e nao muda com o"));
    Serial.println(F("D10 -- e ruido, nao sinal. O D11 nao esta conectado a"));
    Serial.println(F("nada. Conferir as DUAS pontas do jumper: o furo no"));
    Serial.println(F("Arduino e o pino no modulo."));
  } else if (plainWhenHigh >= 190 && plainWhenLow >= 190) {
    Serial.println(F("D11 CRAVADO EM ALTO. Ha algo segurando o no, mas ele"));
    Serial.println(F("nao responde ao D10. Ou o driver nao chaveia, ou o"));
    Serial.println(F("modulo simplesmente nao ecoa -- caso do HW-519, em que"));
    Serial.println(F("o RE segue o DE e o receptor desliga ao transmitir."));
    Serial.println(F("Neste caso o teste e INCONCLUSIVO, nao negativo."));
  } else {
    Serial.println(F("PADRAO AMBIGUO. Repetir; se persistir, contato"));
    Serial.println(F("intermitente."));
  }
  Serial.println(F("------------------------------------------------------------"));
}

void loop() {
  delay(1000);
}
