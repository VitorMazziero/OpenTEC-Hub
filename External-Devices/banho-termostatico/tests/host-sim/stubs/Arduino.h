#pragma once
// Minimal Arduino surface for compiling SetpointManager/KeyPresser/AppContext on a host.
#include <cmath>
#include <cctype>
#include <cstdarg>
#include <cstdint>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <string>

#define LOW 0
#define HIGH 1
#define INPUT 0
#define OUTPUT 1
#define INPUT_PULLUP 2
#define IRAM_ATTR
#define PROGMEM

unsigned long millis();
void digitalWrite(int pin, int level);
int digitalRead(int pin);
void pinMode(int pin, int mode);
void noInterrupts();
void interrupts();
inline void delay(unsigned long) {}

class String {
 public:
  String() {}
  String(const char* c) : s_(c ? c : "") {}
  String(const std::string& s) : s_(s) {}
  String(float v, int digits) { char b[48]; snprintf(b, sizeof(b), "%.*f", digits, v); s_ = b; }
  String(unsigned long v) { s_ = std::to_string(v); }
  String(int v) { s_ = std::to_string(v); }
  size_t length() const { return s_.size(); }
  char operator[](size_t i) const { return s_[i]; }
  const char* c_str() const { return s_.c_str(); }
  String& operator=(const char* c) { s_ = c ? c : ""; return *this; }
  String& operator+=(const String& o) { s_ += o.s_; return *this; }
  String& operator+=(const char* c) { s_ += c; return *this; }
  String& operator+=(char c) { s_ += c; return *this; }
  bool operator==(const char* c) const { return s_ == c; }
  bool operator!=(const char* c) const { return s_ != c; }
  friend String operator+(const String& a, const String& b) { return String(a.s_ + b.s_); }
  friend String operator+(const char* a, const String& b) { return String(std::string(a) + b.s_); }
  void reserve(size_t n) { s_.reserve(n); }
  bool isEmpty() const { return s_.empty(); }
  int indexOf(const char* c, int from = 0) const {
    const size_t p = s_.find(c, static_cast<size_t>(from));
    return p == std::string::npos ? -1 : static_cast<int>(p);
  }
  int indexOf(char c, int from = 0) const {
    const size_t p = s_.find(c, static_cast<size_t>(from));
    return p == std::string::npos ? -1 : static_cast<int>(p);
  }
  String substring(int from, int to) const { return String(s_.substr(from, to - from)); }
 private:
  std::string s_;
};

extern bool g_serialVerbose;
struct SerialClass {
  int printf(const char* fmt, ...) {
    if (!g_serialVerbose) return 0;
    va_list ap; va_start(ap, fmt); int n = vprintf(fmt, ap); va_end(ap); return n;
  }
  void println(const char* s) { if (g_serialVerbose) ::printf("%s\n", s); }
  void println(const String& s) { println(s.c_str()); }
  void println() { if (g_serialVerbose) ::printf("\n"); }
};
extern SerialClass Serial;
