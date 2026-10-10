#pragma once

// Build and wire-contract identity.
//
// 10.0.0: motorSetpoint continua sendo a referencia 0..1000 rpm do operador.
// motorControlMode seleciona 0=UART/CN1 tradicional ou 1=Modbus direto. A
// troca e break-before-make, sempre passa por zero e e confirmada pelo driver.
// A mudanca Hub<->driver e incompatível e por isso incrementa o protocolo.
// 10.1.0: identidade dos nos externos no quadro agregado (*IP, *NodeVer, *NodeMac)
// e /nodes completo. Chaves aditivas: o protocolo continua 10.
// 10.2.0: caixa confiavel da distancia por carona no push e ecos de config dos nos.
// 10.3.0: transicao editavel do fluxometro v12; 10.4.0: calibracao polinomial da bomba v3.12.
// 10.5.0: telemetria completa e diagnostico da cascata termica externa.
// 10.7.0: PI do banho restrito ao ajuste fino (estado `approaching`); chaves aditivas.
// 10.7.1: idade das leituras sem estouro (o PI do banho nunca entrava).
// 10.8.0: `bathSetpoint`, SP direto do C404 com a cascata parada; chave aditiva.
#define HUB_FIRMWARE_VERSION "10.8.0-dev"
#define HUB_PROTOCOL_VERSION 10

// ===================================================================
// MODULO ALVO DESTA GRAVACAO
// ===================================================================
// Esta e a UNICA linha que muda entre os dois modulos.
//
//   1 = ModuloTECNAL_1        2 = ModuloTECNAL_2
//
// Nao existe um firmware "do modulo 1" e outro "do modulo 2": e o
// mesmo codigo, com um seletor. Manter duas versoes divergiria a cada
// correcao futura, e a diferenca real entre os modulos e uma string.
//
// So o SSID depende do modulo. O roteamento do servo NAO entra aqui,
// de proposito: ele e preferencia de IMPLANTACAO, nao de build. O
// ModuloTECNAL_1 tem o no do driver durante os testes e nao tem
// depois deles, entao o valor certo muda sem que o modulo mude. Isso
// vive na NVS e se ajusta em runtime:
//
//   {"servoComm":0}   desliga o roteamento neste Hub
//   {"servoComm":1}   religa
//
// Compilar essa escolha jogaria fora justamente a distincao que a v9
// acrescentou ao contrato: ServoCommEnabled separa "no ausente" de
// "roteamento desligado".
#define MODULO_TECNAL 2

#if MODULO_TECNAL == 1
  #define WIFI_SSID     "ModuloTECNAL_1"
  #define WIFI_PASSWORD "ModuloTECNAL_1"
#elif MODULO_TECNAL == 2
  #define WIFI_SSID     "ModuloTECNAL_2"
  #define WIFI_PASSWORD "ModuloTECNAL_2"
#else
  #error "MODULO_TECNAL deve ser 1 ou 2"
#endif

// ATENCAO: este e o unico ponto do HUB, mas nao e o unico ponto do
// SISTEMA. Todo no que se associa a este SoftAP carrega o SSID gravado
// no proprio firmware e precisa ser regravado junto, senao fica
// procurando o modulo antigo para sempre:
//
//   - no de potencia ASDA-B2 .... MODULO_TECNAL_ALVO em
//     ESP32S3-SERVO/Software/firmware-producao/ASDA_B2_Servo_Node/
//   - fluxometro, biomassa, bomba, agitador e sensor de distancia ....
//     cada um no seu proprio sketch
//
// O IP nao muda: softAPConfig() fixa 192.168.4.1 nos dois modulos.
// O SSID efetivo e ecoado no boot; confira o banner das duas placas.

// Input and serialization limits.
#define JSON_BUFFER_LEN 256
#define MAX_HTTP_PAYLOAD 2048

