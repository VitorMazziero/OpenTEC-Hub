#pragma once

#include <Arduino.h>

// AP local sempre ativo; STA para o Hub so quando `hub_enabled = 1`. O Hub ainda
// nao conhece o dispositivo `bath`, entao o padrao e desligado.
void checkWifi();
bool httpGet(const String& url, int& code, String& body);
