#pragma once
#include "FreeRTOS.h"
using QueueHandle_t = void*;
inline QueueHandle_t xQueueCreate(int, size_t) { return reinterpret_cast<void*>(1); }
inline BaseType_t xQueueSend(QueueHandle_t, const void*, int) { return pdTRUE; }
inline BaseType_t xQueueReceive(QueueHandle_t, void*, int) { return 0; }
