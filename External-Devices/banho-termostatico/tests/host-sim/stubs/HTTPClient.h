#pragma once
#include <Arduino.h>
class HTTPClient {
 public:
  void begin(const String&) {}
  void setReuse(bool) {}
  void setTimeout(int) {}
  void collectHeaders(const char**, int) {}
  int GET() { return 200; }
  String getString() { return "{}"; }
  String header(const char*) { return "0"; }
  void end() {}
};
