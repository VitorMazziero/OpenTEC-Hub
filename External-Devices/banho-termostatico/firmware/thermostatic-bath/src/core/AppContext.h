#pragma once

#include <Arduino.h>
#include <WebServer.h>

enum WifiReconnectState { WF_IDLE, WF_CONNECTING };

enum Key : uint8_t { KEY_STAR = 0, KEY_UP = 1, KEY_DOWN = 2, KEY_ENTER = 3, KEY_COUNT = 4 };

// Qual tecla abre a edicao do SP e qual confirma. 0 = nenhuma (setas agem direto
// na tela principal, como o manual descreve), 1 = *, 2 = ENTER.
enum KeyRole : uint8_t { ROLE_NONE = 0, ROLE_STAR = 1, ROLE_ENTER = 2 };

// Fonte da verdade do setpoint: sombra (malha aberta, contagem de toques) ou display
// (malha fechada, decodificado do display inferior do C404).
enum SpSource : uint8_t { SP_SOURCE_SHADOW = 0, SP_SOURCE_DISPLAY = 1 };

// Modo de operacao frente as teclas fisicas. Manual: o operador pode mudar o SP no
// painel e o no so reporta o desvio. Automatico: o no reverte qualquer desvio do
// alvo comandado assim que o painel fica parado (so no modo display).
enum BathMode : uint8_t { MODE_MANUAL = 0, MODE_AUTO = 1 };

// Parametros ajustaveis em tempo de execucao e persistidos na NVS.
struct BathConfig {
  uint16_t pressMs      = 150;   // rele fechado
  uint16_t gapMs        = 150;   // rele aberto entre toques
  uint16_t menuMs       = 400;   // espera apos a tecla que abre a edicao
  uint16_t settleMs     = 1500;  // espera apos confirmar, antes de verificar
  float    stepC        = 0.1f;  // graus por toque (d.P = 1 casa no C404)
  float    spMin        = 5.0f;  // = in.L do C404; limite do "home"
  float    spMax        = 90.0f; // = in.H do C404
  uint8_t  enterKey     = ROLE_STAR;
  uint8_t  confirmKey   = ROLE_ENTER;
  uint8_t  spSource     = SP_SOURCE_SHADOW;
  uint8_t  senseEnabled = 0;
  uint8_t  senseMask    = 0x06;  // teclas com linha de sensoriamento ligada: bit0 *, bit1 ▲, bit2 ▼, bit3 ENTER
  uint8_t  hubEnabled   = 1;     // NVS preserva a escolha explicita de modo local.
  // Segmento aceso = nivel LOW. Padrao 0: no C404 deste banho o pad do segmento vai ao
  // GPIO so por 20 k em serie e o aceso chega em ~2,0-2,4 V (HIGH).
  uint8_t  dispSegLow   = 0;
  uint8_t  dispDigLow   = 1;     // digito ativo = nivel LOW (so no modo 0)
  uint8_t  dispSegLead  = 0;     // 1: segmentos mudam antes da linha de digito (so no modo 0)
  // Modo do leitor: 0 = linhas de digito (ISR nas bordas de 1A..1D); 1 = janelas contadas
  // a partir das bordas de 2DISP, sem linhas de digito (C404 deste banho: ~1 ms por
  // janela, 4 janelas num banco e 4 + LEDs no outro).
  uint8_t  dispMode     = 1;
  uint16_t dispSlotUs   = 1023;  // duracao de uma janela de digito (us), modo 1
  uint8_t  dispSpBank   = 0;     // nivel de 2DISP em que o display do SP e varrido, modo 1
  // Casas decimais que o C404 mostra (d.P). Leitura com o ponto em outro lugar, ou sem
  // ponto, e invalida: um ponto decimal perdido transformava 23.4 em 234 (2026-09-26).
  uint8_t  dispDecimals = 1;
  uint16_t homeMargin   = 20;    // toques extras de ▼ no "home"
  uint32_t sendPeriodMs = 1000;
  // Tecla mantida (auto-repeticao do C404) com o display fechando a malha. So no
  // modo display: sem leitura nao ha como contar o que a auto-repeticao fez.
  uint8_t  holdEnabled   = 1;
  uint16_t holdMinSteps  = 15;   // distancia minima (toques) para valer a pena manter a tecla
  uint16_t holdStopSteps = 3;    // soltar a esta distancia do alvo, mais a compensacao de atraso
  uint16_t holdLagMs     = 80;   // display -> decisao -> rele aberto; vezes a taxa medida = folga
  uint16_t holdSettleMs  = 600;  // espera apos soltar antes de reler o display (> 300 ms do filtro)
  uint16_t holdStallMs   = 2500; // display parado por este tempo com a tecla mantida = soltar
  // Modos. O gesto ▲+▼ mantidas (fisicamente, com sensoriamento) alterna o modo;
  // 0 desliga o gesto. O guarda do modo automatico espera o display parado e as
  // teclas soltas por guardDelayMs antes de reverter um desvio.
  uint16_t modeHoldMs    = 1000;
  uint16_t guardDelayMs  = 5000;
  // Intervalo entre avaliacoes do guarda (display x alvo). Pedido do operador: 10 s. O
  // leitor do display continua rodando (a sombra e o /status dependem dele); so a
  // decisao de reverter fica espacada. Reacao a uma mudanca manual: 10-20 s.
  uint16_t guardCheckMs  = 10000;
};

extern WebServer server;
extern BathConfig g_cfg;

// Estado do setpoint. g_spShadow e o que o firmware acredita estar no C404; g_spKnown
// cai para false sempre que essa crenca deixa de ser confiavel (intervencao manual
// detectada, reboot no meio de uma sequencia, abort). Sao persistidos.
extern float g_spShadow;
extern bool  g_spKnown;
extern float g_spTarget;      // ultimo SP comandado (persistido): referencia do modo automatico
extern uint32_t g_lastCmdId;
extern uint8_t g_mode;        // BathMode, persistido

// Atividade manual detectada pelo sensoriamento das teclas.
extern uint32_t g_manualPressCount;
extern unsigned long g_manualActivityMs;

extern String g_lastKnownSsid;
extern WifiReconnectState g_wifiState;
extern unsigned long g_wifiNextActionMs;
extern unsigned long WIFI_RECONNECT_PERIOD_MS;

extern volatile bool g_otaInProgress;
extern unsigned long g_otaLastChunkMs;
extern const unsigned long OTA_STALL_TIMEOUT_MS;
extern unsigned long g_otaRebootAtMs;
extern String g_otaRejectReason;

extern volatile uint8_t g_hubFailStreak;

// Posse do Hub (r3.2). O Hub declara em toda resposta do /bathData se a cascata dele
// esta dona do banho (cabecalho X-Hub-Owner). Enquanto a posse vale, a API local so
// aceita abort/stop; ela expira sozinha quando o Hub para de responder, entao um Hub
// desligado nunca prende o banho.
constexpr unsigned long HUB_OWNERSHIP_TIMEOUT_MS = 10000;
extern volatile bool g_hubOwnerFlag;
extern volatile unsigned long g_hubOwnerSeenMs;
bool hubOwnershipActive(unsigned long now);

// Ultimo comando do Hub recusado pelo parser (publicado no /bathData para o Hub nao
// reentregar as cegas ate o timeout). Escrito e lido apenas no loop principal.
extern uint32_t g_hubRejectCmdId;
extern char g_hubRejectErr[32];
constexpr uint8_t LINK_WATCHDOG_FAILS = 8;
extern volatile bool g_hubAnnounced;

const char* keyName(Key k);
bool keyFromName(const char* name, Key& out);
