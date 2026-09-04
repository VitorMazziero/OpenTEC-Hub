#include <Arduino.h>

/*
 * O driver do HW-519 esta mesmo excursionando A e B?
 *
 * ------------------------------------------------------------------
 * POR QUE A VERSAO ANTERIOR NAO SERVIA
 * ------------------------------------------------------------------
 * Ela segurava o DI em nivel BAIXO por 8 s e esperava ver o driver
 * prender a linha no estado de espaco. So que a direcao automatica
 * destes modulos e disparada por BORDA, com um RC: ela habilita o
 * driver na descida do DI e o solta pouco depois. Nivel estatico nao
 * mantem o driver ligado -- o RC descarrega e ele solta, com o DI
 * ainda em baixo. O teste media polarizacao nos dois casos, e por isso
 * dava o mesmo valor sempre.
 *
 * ------------------------------------------------------------------
 * O QUE ESTA VERSAO FAZ
 * ------------------------------------------------------------------
 * Compara REPOUSO contra CHAVEAMENTO CONTINUO. O RC nunca descarrega
 * enquanto houver bordas, entao o driver fica ativo de verdade.
 *
 *   Fase A, LED APAGADO: DI em alto, parado. A linha fica so na
 *       polarizacao. E a leitura de referencia.
 *
 *   Fase B, LED ACESO: DI chaveando em onda quadrada de 50%, com meio
 *       periodo de 104 us -- o tempo de bit de 9600 baud. Se o driver
 *       funciona, ele passa metade do tempo em marca e metade em
 *       espaco, e um multimetro de continua le a MEDIA: perto de zero.
 *
 * LEITURA DO RESULTADO, com o multimetro em continua entre A e B:
 *
 *   apagado alto (ex. 4,6 V) e aceso perto de ZERO
 *       -> o driver funciona. Transmissao provada de ponta a ponta.
 *
 *   os dois valores praticamente IGUAIS
 *       -> o driver nao chaveia nunca. O sinal entra no modulo (o LED
 *          de RXD pisca) e nao sai na linha.
 *
 * A diferenca esperada e grande e nao depende de multimetro bom.
 *
 * Nao ha quadro Modbus valido aqui, so uma onda quadrada. O drive
 * descarta como erro de enquadramento. O CN3 e porta de telemetria,
 * nao de comando.
 */

constexpr uint8_t PIN_TX = 10;   // vai ao RXD/DI do HW-519
constexpr uint8_t PIN_LED = 13;  // LED da propria placa
constexpr uint32_t PHASE_MS = 8000;
constexpr uint16_t HALF_BIT_US = 104;  // meio periodo = 1 bit em 9600

void setup() {
  Serial.begin(115200);
  delay(300);

  pinMode(PIN_TX, OUTPUT);
  pinMode(PIN_LED, OUTPUT);
  digitalWrite(PIN_TX, HIGH);
  digitalWrite(PIN_LED, LOW);

  Serial.println();
  Serial.println(F("=== O driver do HW-519 excursiona A e B? ==="));
  Serial.println(F("Multimetro em tensao continua, pontas em A e B."));
  Serial.println();
  Serial.println(F("  LED APAGADO = DI parado em alto = repouso"));
  Serial.println(F("  LED ACESO   = DI chaveando a 9600 = driver ativo"));
  Serial.println();
  Serial.println(F("Se o driver funciona, a leitura com o LED aceso cai"));
  Serial.println(F("para perto de ZERO: e a media de marca e espaco."));
  Serial.println(F("Se os dois valores forem iguais, ele nunca chaveia."));
  Serial.println();
}

void loop() {
  // Fase de repouso: DI parado em alto.
  digitalWrite(PIN_LED, LOW);
  digitalWrite(PIN_TX, HIGH);
  Serial.print(millis());
  Serial.println(F("  LED APAGADO -- repouso, anote A-B"));
  delay(PHASE_MS);

  // Fase de chaveamento: onda quadrada continua, o RC nunca descarrega.
  digitalWrite(PIN_LED, HIGH);
  Serial.print(millis());
  Serial.println(F("  LED ACESO   -- chaveando, anote A-B"));

  const uint32_t startMs = millis();
  while ((millis() - startMs) < PHASE_MS) {
    digitalWrite(PIN_TX, LOW);
    delayMicroseconds(HALF_BIT_US);
    digitalWrite(PIN_TX, HIGH);
    delayMicroseconds(HALF_BIT_US);
  }

  digitalWrite(PIN_TX, HIGH);
}
