#pragma once

#include <Arduino.h>

#include "../core/AppContext.h"

// Aciona os reles do HW-280 como toques discretos ou como tecla mantida, sem
// bloquear o laco principal. A fila guarda passos (tecla, repeticoes, pausa extra
// apos o passo); o motor fecha o rele por `pressMs`, abre por `gapMs` e chama o
// callback a cada toque concluido. Um passo de "hold" mantem o rele fechado ate
// `keypadRelease()` ou ate o teto do passo, e nao chama o callback: o numero de
// toques que o C404 contou durante a auto-repeticao so o display sabe.
// Nunca ha dois reles fechados ao mesmo tempo.
void keypadInit();
bool keypadEnqueue(Key key, uint16_t count, uint16_t extraGapMs = 0);
bool keypadEnqueueHold(Key key, uint32_t maxMs, uint16_t extraGapMs = 0);
void keypadRelease();
void keypadClear();
void keypadResetCounters();
bool keypadBusy();
bool keypadHolding();
unsigned long keypadHoldMs(unsigned long now);
bool keypadRelayActive(Key key);
uint32_t keypadPressesDone();
uint32_t keypadPressesTotal();
uint32_t keypadUnconfirmedPresses();
void keypadSetOnPress(void (*callback)(Key key));
void keypadService(unsigned long now);
