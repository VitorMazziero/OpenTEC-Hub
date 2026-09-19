#pragma once

// Duas areas na NVS: `bath_cfg` (parametros) e `bath_st` (setpoint-sombra,
// confianca e marca de sequencia em curso). O estado muda a cada sequencia; a
// configuracao raramente. Separar evita regravar os parametros a cada comando.
void loadNvsConfig();
void saveNvsConfig();
void resetNvsConfig();
void loadNvsState();
void saveNvsState(bool sequenceBusy);
