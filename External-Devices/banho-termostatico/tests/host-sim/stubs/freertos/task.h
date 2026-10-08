#pragma once
#include "FreeRTOS.h"
using TaskHandle_t = void*;
void vTaskDelay(unsigned long ms);
inline unsigned uxTaskGetStackHighWaterMark(void*) { return 4096; }
inline BaseType_t xTaskCreatePinnedToCore(void (*)(void*), const char*, unsigned, void*, int, TaskHandle_t*, int) { return pdPASS; }
