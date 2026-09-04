#include "HttpCommandQueue.h"

bool HttpCommandQueue::begin() {
  if (mutex_ == nullptr) mutex_ = xSemaphoreCreateMutex();
  return mutex_ != nullptr;
}

bool HttpCommandQueue::enqueue(const String &command) {
  if (mutex_ == nullptr || xSemaphoreTake(mutex_, pdMS_TO_TICKS(50)) != pdTRUE) return false;
  if (count_ == kCapacity) {
    xSemaphoreGive(mutex_);
    return false;
  }
  const uint8_t tail = (head_ + count_) % kCapacity;
  items_[tail] = command;
  ++count_;
  xSemaphoreGive(mutex_);
  return true;
}

bool HttpCommandQueue::dequeue(String &command) {
  if (mutex_ == nullptr || xSemaphoreTake(mutex_, pdMS_TO_TICKS(50)) != pdTRUE) return false;
  if (count_ == 0) {
    xSemaphoreGive(mutex_);
    return false;
  }
  command = items_[head_];
  items_[head_] = "";
  head_ = (head_ + 1) % kCapacity;
  --count_;
  xSemaphoreGive(mutex_);
  return true;
}

uint8_t HttpCommandQueue::depth() const {
  if (mutex_ == nullptr || xSemaphoreTake(mutex_, pdMS_TO_TICKS(50)) != pdTRUE) return 0;
  const uint8_t value = count_;
  xSemaphoreGive(mutex_);
  return value;
}

