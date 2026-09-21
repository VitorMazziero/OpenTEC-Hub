#pragma once

#include <Arduino.h>

#define WL_CONNECTED 3

class IPAddress {
 public:
  String toString() const { return String("127.0.0.1"); }
};

class WiFiClass {
 public:
  int status() const { return WL_CONNECTED; }
  IPAddress localIP() const { return IPAddress(); }
};

extern WiFiClass WiFi;
