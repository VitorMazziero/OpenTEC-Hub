#include "EventLog.h"

#include <Arduino.h>
#include <stdarg.h>
#include <stdio.h>
#include <string.h>

namespace {
constexpr size_t EVENT_LOG_LINES = 64;
constexpr size_t EVENT_LOG_WIDTH = 144;

char g_lines[EVENT_LOG_LINES][EVENT_LOG_WIDTH];
size_t g_next = 0;
size_t g_count = 0;

void store(const char* text) {
  char* slot = g_lines[g_next];
  int n = snprintf(slot, EVENT_LOG_WIDTH, "%10lu ", static_cast<unsigned long>(millis()));
  if (n < 0) n = 0;
  size_t pos = static_cast<size_t>(n) < EVENT_LOG_WIDTH ? static_cast<size_t>(n) : EVENT_LOG_WIDTH - 1;
  for (const char* p = text; *p && pos + 1 < EVENT_LOG_WIDTH; ++p) {
    if (*p != '\n' && *p != '\r') slot[pos++] = *p;
  }
  slot[pos] = '\0';
  g_next = (g_next + 1) % EVENT_LOG_LINES;
  if (g_count < EVENT_LOG_LINES) ++g_count;
}
}  // namespace

void logPrintf(const char* fmt, ...) {
  char buf[EVENT_LOG_WIDTH];
  va_list args;
  va_start(args, fmt);
  vsnprintf(buf, sizeof(buf), fmt, args);
  va_end(args);
  Serial.print(buf);
  store(buf);
}

void logPrintln(const char* line) {
  Serial.println(line);
  store(line);
}

size_t eventLogText(char* out, size_t cap) {
  if (cap == 0) return 0;
  size_t used = 0;
  const size_t first = (g_next + EVENT_LOG_LINES - g_count) % EVENT_LOG_LINES;
  for (size_t i = 0; i < g_count; ++i) {
    const char* line = g_lines[(first + i) % EVENT_LOG_LINES];
    const size_t len = strlen(line);
    if (used + len + 2 > cap) break;
    memcpy(out + used, line, len);
    used += len;
    out[used++] = '\n';
  }
  out[used] = '\0';
  return used;
}
