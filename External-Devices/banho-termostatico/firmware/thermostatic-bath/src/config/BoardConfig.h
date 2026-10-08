#pragma once

#include <Arduino.h>

// Pinagem do ESP32-S3 DevKit. Evita 0/3/45/46 (strapping), 19/20 (USB) e 26-37
// (flash/PSRAM). Todos os pinos aqui sao apenas sugestao inicial; o plano do
// dispositivo (plano_controle_remoto_C404_ESP32S3.md) autoriza remapear.
namespace BoardConfig {
// Reles HW-280 IN1..IN4, jumpers em H: GPIO HIGH aciona (RelayActiveLow = false).
// Bancada 2026-09-24: no modo L o IN tem pull-up para o VCC de 5 V do modulo e o
// GPIO em 3,3 V deixava 1,7 V sobre o LED do opto (LEDs acesos fracos em repouso,
// rele sem soltar). No modo H o repouso e 0 V, e no reset (GPIO em alta impedancia)
// o rele fica aberto. true so volta a valer com jumpers em L e VCC do modulo em 3,3 V.
constexpr bool RelayActiveLow = false;
// Mapa 2026-09-25: rele N no ponto CHN do C404 (CH1 = *, CH2 = ENTER, CH3 = ▲,
// CH4 = ▼), IN1..IN4 nos GPIO 4..7.
constexpr int RelayStarPin  = 4;   // IN1 -> rele 1 -> CH1 (tecla *)
constexpr int RelayEnterPin = 5;   // IN2 -> rele 2 -> CH2 (tecla ENTER)
constexpr int RelayUpPin    = 6;   // IN3 -> rele 3 -> CH3 (tecla ▲)
constexpr int RelayDownPin  = 7;   // IN4 -> rele 4 -> CH4 (tecla ▼)

// Sensoriamento das teclas: linha CH de cada tecla do C404 via divisor resistivo de
// alta impedancia (docs/WIRING.md secao 4). Mapa medido em 2026-09-24: CH1 = *,
// CH2 = ENTER, CH3 = ▲, CH4 = ▼; o outro terminal das quatro teclas e o +5 V do C404
// e cada CH tem pull-down (~10 k). Logo a tecla e ativa em HIGH: solta ~0 V, fechada
// (fisica ou rele) 5 V -> ~3 V no GPIO. Ativado por `sense_enabled`; `sense_mask`
// diz quais linhas estao ligadas (padrao: so ▲ e ▼, tomadas nos bornes NO dos reles
// 3 e 4). GPIO sem pull interno: o divisor fixa o nivel.
constexpr int SensePins[4] = {1, 2, 42, 47};   // ordem: *, ▲, ▼, ENTER
constexpr bool SenseActiveHigh = true;

// Leitura do display multiplexado do C404 (pontos A..G, PD, linhas de digito, 2DISP).
// Segmentos na ordem A B C D E F G PD; 2DISP escolhe o banco (display superior/
// inferior). Ativado por `sp_source = 1` (display). CH1..CH4 da placa sao as TECLAS,
// nao digitos (bancada 2026-09-24): as linhas de digito ligadas a DigitPins ainda
// estao por identificar (candidatas 1A..1D; docs/WIRING.md secao 3.2).
// Medicao 2026-09-25 (media DC com o painel em " 34.x" / " 33.6"): os 14 pontos comutam
// (nenhum parado em 0 ou 5 V). Hipotese de mapa, a confirmar pela captura /capture:
// digitos 1A 1B 1C 1D -> GPIO 16 17 18 21, 2DISP -> GPIO 38 (banco), 1L -> GPIO 39.
constexpr int SegmentPins[8]  = {8, 9, 10, 11, 12, 13, 14, 15};
constexpr int DigitPins[4]    = {16, 17, 18, 21};   // 1A 1B 1C 1D
constexpr int DigitLineCount  = 4;
constexpr int DisplaySelectPin = 38;   // 2DISP; -1 se cada digito tiver linha propria
// 1L (provavel varredura dos LEDs de sinalizacao). Nao entra na decodificacao; so na
// captura /capture, para mostrar a fatia da varredura que nao e digito. -1 desliga.
constexpr int DisplayLedLinePin = 39;
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
constexpr int HubWifiChannel = 6;
constexpr const char* AccessPointSsid = "Banho Termostatico";
constexpr const char* HubUrl = "http://192.168.4.1/bathData";
constexpr const char* HubHelloUrl = "http://192.168.4.1/nodeHello";
constexpr const char* DeviceKey = "bath";
constexpr const char* FirmwareVersion = "r3.3";
constexpr const char* FirmwareTag = "BathClient r3.3 (direct WiFi reconnect, Hub re-registration, OTA link preserved)";

// Valor de partida do setpoint-sombra quando a NVS esta vazia (pedido do projeto:
// o C404 do banho esta em 30.0 na entrega). Ajustavel por `sync_sp`.
constexpr float InitialSetpointC = 30.0f;
}
