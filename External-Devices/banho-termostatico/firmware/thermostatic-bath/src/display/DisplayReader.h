#pragma once

#include <Arduino.h>

// Decodifica o display multiplexado de 7 segmentos do C404 amostrando as linhas
// de segmento a cada transicao das linhas de digito. Entrega PV (display superior)
// e SP (display inferior) como float, so quando os quatro digitos formam um numero
// e o mesmo quadro foi visto em leituras consecutivas.
void displayInit();
void displayService(unsigned long now);
bool displayPvValid();
float displayPv();
bool displaySpValid();
float displaySp();
bool displayAlive();
uint32_t displayFrameCount();
// Leitura "ao vivo" do SP: decodifica a ultima varredura que se repetiu identica
// duas vezes seguidas (coerencia de quadro), sem o filtro de estabilidade de
// ~300 ms. E o que permite acompanhar o SP enquanto uma tecla esta mantida.
bool displayLiveSp(float& out);
uint32_t displayLiveFrameCount();
uint8_t displayRawSegments(uint8_t digit);
String displayText();   // digitos decodificados; '?' marca padrao desconhecido
