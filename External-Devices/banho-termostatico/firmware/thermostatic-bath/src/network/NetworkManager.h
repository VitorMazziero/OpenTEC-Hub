#pragma once

#include <Arduino.h>

// AP local sempre ativo; a tarefa HubLink liga/desliga a STA conforme o snapshot
// de `hub_enabled`. O padrão continua desligado até a integração de bancada.
void checkWifi(bool hubEnabled);
bool httpGet(const String& url, int& code, String& body);
