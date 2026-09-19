#pragma once

#include <Arduino.h>

// Parsing manual de JSON plano, no mesmo padrao dos demais dispositivos externos
// (sem biblioteca JSON). Apenas objetos de um nivel com valores numericos ou
// strings curtas.
const char* findJsonValueStart(const char* json, const char* key);
long getJsonValue(const char* json, const char* key);
bool getJsonFloat(const char* json, const char* key, float& outVal);
bool getJsonString(const char* json, const char* key, char* out, size_t outLen);

// Resultado de um comando. `reply` recebe JSON de resposta; o retorno indica se ao
// menos uma chave valida foi reconhecida (acao ou configuracao).
bool processCommand(const char* payload, String& reply);
inline bool processCommand(const String& payload, String& reply) {
  return processCommand(payload.c_str(), reply);
}
String getConfigAsJson();
String getStatusAsJson();
