#pragma once

void i2cInit();
bool i2cBusClear();
bool sensorInit();
bool readSingleShot(int& mm);
void maybeRecover();
void i2cScanOnce(const char* tag);
