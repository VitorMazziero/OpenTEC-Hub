#pragma once

#include <Arduino.h>

// Enlace assíncrono com o OpenTEC-Hub. Toda chamada HTTP roda numa tarefa
// própria; o loop principal apenas publica snapshots e consome comandos já
// recebidos. Assim um timeout de rede nunca prolonga um toque no C404.
void hubLinkInit();
void hubLinkPublishSnapshot(unsigned long now);
void hubLinkServiceMainLoop();
uint32_t hubLinkMinFreeStackBytes();
