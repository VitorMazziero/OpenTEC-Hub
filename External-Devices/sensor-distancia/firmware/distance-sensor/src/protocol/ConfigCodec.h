#pragma once

#include <Arduino.h>

long getJsonValue(String json, String key);
void processConfigUpdate(String payload);
String getConfigAsJson();
