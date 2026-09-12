#pragma once

#include <Arduino.h>

void checkWifi();
bool httpGet(const String& url, int& code, String& body);
