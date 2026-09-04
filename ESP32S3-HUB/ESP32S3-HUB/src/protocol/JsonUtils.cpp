#include "JsonUtils.h"

#include <errno.h>
#include <math.h>
#include <stdlib.h>

namespace {

bool fullyConsumed(const char *end) {
  while (*end != '\0' && isspace(static_cast<unsigned char>(*end))) ++end;
  return *end == '\0';
}

// indexOf tambem encontra o token dentro de um VALOR string. Em
// {"pump_command":"start","mode":2} a busca por "start" casava dentro do valor
// e o parser seguia ate o proximo ':', devolvendo o 2 do "mode". Um token so e
// chave quando o caractere significativo anterior e '{' ou ','.
bool isKeyPosition(const String &json, int quotePos) {
  int index = quotePos - 1;
  while (index >= 0 && isspace(static_cast<unsigned char>(json.charAt(index)))) --index;
  return index < 0 || json.charAt(index) == '{' || json.charAt(index) == ',';
}

}  // namespace

namespace JsonUtils {

bool getRaw(const String &json, const char *key, String &value) {
  const String token = String("\"") + key + "\"";
  int keyPos = json.indexOf(token);
  while (keyPos >= 0 && !isKeyPosition(json, keyPos)) {
    keyPos = json.indexOf(token, keyPos + 1);
  }
  if (keyPos < 0) return false;

  const int colon = json.indexOf(':', keyPos + token.length());
  if (colon < 0) return false;

  int start = colon + 1;
  while (start < static_cast<int>(json.length()) && isspace(json.charAt(start))) ++start;
  if (start >= static_cast<int>(json.length())) return false;

  int end = start;
  if (json.charAt(start) == '"') {
    ++start;
    end = json.indexOf('"', start);
    if (end < 0) return false;
  } else {
    while (end < static_cast<int>(json.length()) &&
           json.charAt(end) != ',' && json.charAt(end) != '}') {
      ++end;
    }
  }

  value = json.substring(start, end);
  value.trim();
  return value.length() > 0;
}

bool containsKey(const String &json, const char *key) {
  String ignored;
  return getRaw(json, key, ignored);
}

bool parseBool(const String &raw, bool &value) {
  String normalized = raw;
  normalized.trim();
  normalized.toLowerCase();
  if (normalized == "1" || normalized == "true") {
    value = true;
    return true;
  }
  if (normalized == "0" || normalized == "false") {
    value = false;
    return true;
  }
  return false;
}

bool parseInt(const String &raw, int32_t &value) {
  errno = 0;
  char *end = nullptr;
  const long parsed = strtol(raw.c_str(), &end, 10);
  if (errno == ERANGE || end == raw.c_str() || !fullyConsumed(end) ||
      parsed < INT32_MIN || parsed > INT32_MAX) return false;
  value = static_cast<int32_t>(parsed);
  return true;
}

bool parseUInt(const String &raw, uint32_t &value) {
  if (raw.length() == 0 || raw.charAt(0) == '-') return false;
  errno = 0;
  char *end = nullptr;
  const unsigned long parsed = strtoul(raw.c_str(), &end, 10);
  if (errno == ERANGE || end == raw.c_str() || !fullyConsumed(end) || parsed > UINT32_MAX) return false;
  value = static_cast<uint32_t>(parsed);
  return true;
}

bool parseFiniteFloat(const String &raw, float &value) {
  errno = 0;
  char *end = nullptr;
  const float parsed = strtof(raw.c_str(), &end);
  if (errno == ERANGE || end == raw.c_str() || !fullyConsumed(end) || !isfinite(parsed)) return false;
  value = parsed;
  return true;
}

}  // namespace JsonUtils

