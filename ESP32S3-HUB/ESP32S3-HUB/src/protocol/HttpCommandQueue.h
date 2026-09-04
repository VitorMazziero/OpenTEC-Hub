#pragma once

#include <Arduino.h>

class HttpCommandQueue {
 public:
  static constexpr uint8_t kCapacity = 8;

  bool begin();
  bool enqueue(const String &command);
  bool dequeue(String &command);
  uint8_t depth() const;

 private:
  mutable SemaphoreHandle_t mutex_ = nullptr;
  String items_[kCapacity];
  uint8_t head_ = 0;
  uint8_t count_ = 0;
};

