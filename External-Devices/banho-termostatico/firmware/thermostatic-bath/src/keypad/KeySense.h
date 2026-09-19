#pragma once

#include <Arduino.h>

#include "../core/AppContext.h"

// Leitura opcional das linhas das teclas do C404. Um toque visto sem o rele
// correspondente acionado e intervencao manual: conta em g_manualPressCount e,
// no modo sombra, invalida g_spKnown (o firmware perdeu a conta).
void keySenseInit();
bool keySenseWired(Key key);      // linha ligada (sense_enabled e bit em sense_mask)
bool keySenseRawActive(Key key);
bool keySensePressed(Key key);
// Ha quanto tempo ▲ e ▼ estao ambas pressionadas manualmente (0 se nao estao).
// E o gesto de troca de modo; o SetpointGuard decide quando ele conta.
unsigned long keySenseArrowsHeldMs(unsigned long now);
void keySenseService(unsigned long now);
