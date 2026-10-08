#pragma once

#include <Arduino.h>

// AP local sempre ativo; a tarefa HubLink liga/desliga a STA conforme o snapshot
// de `hub_enabled`; OTA conserva a associacao e suspende tentativas.
void checkWifi(bool hubEnabled);
// `hubOwner`, quando dado, recebe o cabeçalho X-Hub-Owner da resposta (posse r3.2).
bool httpGet(const String& url, int& code, String& body, String* hubOwner = nullptr);
