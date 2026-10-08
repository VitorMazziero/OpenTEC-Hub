#pragma once

#include <Arduino.h>

#define WL_CONNECTED 3

class IPAddress {
 public:
  String toString() const { return String("127.0.0.1"); }
};

class WiFiClass {
 public:
  int connectionStatus = WL_CONNECTED;
  unsigned begins = 0, disconnects = 0;
  bool radioOff = false;
  int channel = 0;
  String ssid;
  int status() const { return connectionStatus; }
  String SSID() const { return ssid; }
  String macAddress() const { return "00:11:22:33:44:55"; }
  void disconnect(bool off, bool) {
    ++disconnects;
    radioOff = radioOff || off;
    connectionStatus = 0;
  }
  void begin(const char* name, const char* password, int ch) {
    if (strcmp(name, password)) std::abort();
    ++begins;
    ssid = name;
    channel = ch;
  }
  IPAddress localIP() const { return IPAddress(); }
};

extern WiFiClass WiFi;
