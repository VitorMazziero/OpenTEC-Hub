#pragma once

#include <Arduino.h>

// Pinagem do ESP32-S3 DevKit. Evita 0/3/45/46 (strapping), 19/20 (USB) e 26-37
// (flash/PSRAM). Todos os pinos aqui sao apenas sugestao inicial; o plano do
// dispositivo (plano_controle_remoto_C404_ESP32S3.md) autoriza remapear.
namespace BoardConfig {
// Reles HW-280 IN1..IN4, jumper CENTRAL-L: GPIO LOW aciona o rele.
constexpr int RelayStarPin  = 4;   // tecla * (Navegacao entre Blocos)
constexpr int RelayUpPin    = 5;   // tecla ▲ (Incremento)
constexpr int RelayDownPin  = 6;   // tecla ▼ (Decremento)
constexpr int RelayEnterPin = 7;   // tecla ENTER (Selecao e Confirmacao)

// Sensoriamento das teclas: lado "quente" de cada tecla do C404 via divisor
// resistivo de alta impedancia (ver docs/WIRING.md secao 3b). Nivel LOW = tecla
// (fisica ou rele) fechada. Ativado em tempo de execucao por `sense_enabled`;
// `sense_mask` diz quais linhas estao ligadas (montagem atual: so ▲ e ▼, tomadas
// nos bornes NO dos reles 2 e 3 do HW-280). Sem pull-up interno: o divisor fixa
// o nivel, e o pull-up de ~45 k do ESP32 levantaria o "pressionado" a ~1,9 V.
constexpr int SensePins[4] = {1, 2, 42, 47};   // ordem: *, ▲, ▼, ENTER

// Leitura do display multiplexado do C404 (pontos 2DISP, A..G, PD, CH1..CH4).
// Segmentos na ordem A B C D E F G PD; linhas de digito CH1..CH4; 2DISP escolhe o
// banco (display superior/inferior). Ativado por `sp_source = 1` (display).
constexpr int SegmentPins[8]  = {8, 9, 10, 11, 12, 13, 14, 15};
constexpr int DigitPins[4]    = {16, 17, 18, 21};
constexpr int DigitLineCount  = 4;
constexpr int DisplaySelectPin = 38;   // 2DISP; -1 se cada digito tiver linha propria
constexpr int DigitCount = 8;          // DigitLineCount * (DisplaySelectPin >= 0 ? 2 : 1)
// Indices de digito (linha + DigitLineCount*banco), da esquerda para a direita.
constexpr uint8_t PvDigits[4] = {0, 1, 2, 3};
constexpr uint8_t SpDigits[4] = {4, 5, 6, 7};

// Indicacao e troca de modo sem tocar no C404. LED externo (docs/WIRING.md secao 5:
// GPIO 40 -> 330 R -> LED -> GND) aceso no modo automatico, apagado no manual. Botao
// proprio na caixa (para GND, pull-up interno; mantido por `mode_hold_ms`) e opcional,
// -1 desliga. O gesto ▲+▼ no painel continua valendo com o sensoriamento das teclas.
// Pinos livres no S3: 39, 41 (JTAG, so se nao usar).
constexpr int ModeButtonPin = -1;
constexpr int ModeLedPin    = 40;

constexpr const char* HubSsidA = "ModuloTECNAL_1";
constexpr const char* HubSsidB = "ModuloTECNAL_2";
constexpr const char* AccessPointSsid = "Banho Termostatico";
constexpr const char* HubUrl = "http://192.168.4.1/bath";
constexpr const char* HubHelloUrl = "http://192.168.4.1/nodeHello";
constexpr const char* DeviceKey = "bath";
constexpr const char* FirmwareTag = "BathClient r2 (AP+STA, C404 keypad, display reader, hold, modes)";

// Valor de partida do setpoint-sombra quando a NVS esta vazia (pedido do projeto:
// o C404 do banho esta em 30.0 na entrega). Ajustavel por `sync_sp`.
constexpr float InitialSetpointC = 30.0f;
}
