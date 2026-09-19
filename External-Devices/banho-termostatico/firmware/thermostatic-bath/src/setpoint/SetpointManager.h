#pragma once

#include <Arduino.h>

#include "../core/AppContext.h"

// Traduz pedidos de setpoint em sequencias de toques e mantem o setpoint-sombra.
// Uma sequencia e: tecla de entrada (opcional) -> pernas de ▲/▼ -> tecla de
// confirmacao (opcional) -> assentamento -> verificacao (display, quando ativo).
// Uma perna e um hold (tecla mantida, o display fecha a malha) ou N toques.
enum SeqState : uint8_t { SEQ_IDLE = 0, SEQ_RUNNING = 1, SEQ_SETTLING = 2, SEQ_DONE = 3, SEQ_ERROR = 4, SEQ_ABORTED = 5 };
enum SeqKind : uint8_t { KIND_NONE = 0, KIND_SETPOINT = 1, KIND_HOME = 2, KIND_RAW = 3 };
// Sub-fase de uma sequencia em curso (informativa fora deste modulo).
enum SeqPhase : uint8_t { PHASE_NONE = 0, PHASE_ENTER = 1, PHASE_PLAN = 2, PHASE_HOLD = 3, PHASE_HOLD_SETTLE = 4, PHASE_PRESSES = 5 };

void setpointInit();
bool setpointRequestAbsolute(float sp, String& err);
bool setpointRequestDelta(float delta, String& err);
bool setpointSync(float sp, String& err);
bool setpointHome(bool hasTarget, float target, String& err);
bool setpointRawPress(Key key, uint16_t count, String& err);
bool setpointRawHold(Key key, uint32_t holdMs, String& err);
void setpointAbort();
void setpointService(unsigned long now);

SeqState setpointState();
SeqKind setpointKind();
SeqPhase setpointPhase();
const char* setpointStateName();
const char* setpointKindName();
const char* setpointPhaseName();
const String& setpointLastError();
bool setpointBusy();
// Diagnostico da ultima sequencia `setpoint`: distancia planejada em toques, se o
// plano previa hold, rodadas de hold feitas e taxa de auto-repeticao medida.
long setpointPlannedPresses();
bool setpointPlannedHold();
uint8_t setpointHoldRounds();
float setpointHoldRateStepsPerS();
