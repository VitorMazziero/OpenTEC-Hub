#pragma once

#include <Arduino.h>

#include "../core/AppContext.h"

// Modo manual/automatico e o guarda do modo automatico.
//
// Os reles ficam em paralelo com as teclas: nada impede fisicamente o operador de
// mudar o SP no painel. No modo manual o no so reporta o desvio entre o display e
// o ultimo alvo comandado (g_spTarget). No modo automatico o guarda espera o
// painel parar (display estavel e teclas soltas por guard_delay_ms) e reverte o
// desvio com uma sequencia de setpoint comum. Precisa do modo display: na sombra
// nao ha como ver a mudanca.
//
// O modo muda por comando ({"mode":"auto"}) ou pelo gesto ▲+▼ mantidas por
// mode_hold_ms, que so o sensoriamento das teclas enxerga.
enum GuardState : uint8_t { GUARD_OFF = 0, GUARD_WATCH = 1, GUARD_PENDING = 2, GUARD_CORRECTING = 3, GUARD_SUSPENDED = 4 };

void guardInit();
void guardService(unsigned long now);
bool guardSetMode(uint8_t mode, const char* source);
// Um comando de setpoint aceito pelo operador zera a suspensao e o desvio pendente.
void guardNotifyCommand();

GuardState guardState();
const char* guardStateName();
const char* modeName(uint8_t mode);
bool modeFromName(const char* name, uint8_t& out);
// Display − alvo, em graus, quando o display esta legivel.
bool guardDeviation(float& outC);
uint32_t guardCorrections();
