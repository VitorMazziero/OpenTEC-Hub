#pragma once
// Leitura de valores num JSON plano, sem dependencia de Arduino: compila no PC
// (tests/host-sim) com o mesmo codigo que roda na placa.

#include <cctype>
#include <cerrno>
#include <cmath>
#include <cstdint>
#include <cstdlib>
#include <cstring>

inline const char* findJsonValueStart(const char* json, const char* key) {
    if (!json || !key) return nullptr;
    const size_t klen = strlen(key);
    const char* p = json;
    while ((p = strstr(p, key)) != nullptr) {
        if (p > json && *(p - 1) == '"' && *(p + klen) == '"') {
            const char* afterQuote = p + klen + 1;
            while (*afterQuote && isspace(static_cast<unsigned char>(*afterQuote))) afterQuote++;
            if (*afterQuote == ':') {
                const char* valStart = afterQuote + 1;
                while (*valStart && isspace(static_cast<unsigned char>(*valStart))) valStart++;
                return valStart;
            }
        }
        p += klen;
    }
    return nullptr;
}

inline bool jsonHasKey(const char* json, const char* key) {
    return findJsonValueStart(json, key) != nullptr;
}

inline long getJsonValue(const char* json, const char* key) {
    const char* val = findJsonValueStart(json, key);
    if (!val || *val == '"') return -999999;
    char* endPtr = nullptr;
    long result = strtol(val, &endPtr, 10);
    if (endPtr == val) return -999999;
    return result;
}

inline float getJsonFloatValue(const char* json, const char* key) {
    const char* val = findJsonValueStart(json, key);
    if (!val || *val == '"') return NAN;
    char* endPtr = nullptr;
    float result = strtof(val, &endPtr);
    if (endPtr == val) return NAN;
    return result;
}

inline double getJsonDoubleValue(const char* json, const char* key) {
    const char* val = findJsonValueStart(json, key);
    if (!val || *val == '"') return NAN;
    char* endPtr = nullptr;
    double result = strtod(val, &endPtr);
    if (endPtr == val) return NAN;
    return result;
}

// Inteiro sem sinal de 32 bits, exato. Recusa sinal, fracao, vazio e estouro. O cmd_id do Hub
// passa de 1e8; lido como float perdia os ultimos digitos (3.12.1).
inline bool parseJsonUint32(const char* json, const char* key, uint32_t& out) {
    const char* val = findJsonValueStart(json, key);
    if (!val || *val < '0' || *val > '9') return false;
    char* endPtr = nullptr;
    errno = 0;
    const unsigned long long v = strtoull(val, &endPtr, 10);
    if (endPtr == val || errno == ERANGE || v > 0xFFFFFFFFULL) return false;
    if (*endPtr == '.' || *endPtr == 'e' || *endPtr == 'E') return false;
    out = static_cast<uint32_t>(v);
    return true;
}

// Copia uma string JSON (sem escapes) para buf. Falso se ausente ou nao for string.
inline bool getJsonStringInto(const char* json, const char* key, char* buf, size_t len) {
    const char* val = findJsonValueStart(json, key);
    if (!val || *val != '"' || len == 0) return false;
    val++;
    const char* endQ = strchr(val, '"');
    if (!endQ) return false;
    size_t n = static_cast<size_t>(endQ - val);
    if (n >= len) n = len - 1;
    memcpy(buf, val, n);
    buf[n] = '\0';
    return true;
}
