# Estado atual — Banho termostático

**Atualizado:** 2026-09-21

## Implementado e validado em software

- Firmware ativo: `firmware/thermostatic-bath` r3.1 — `BathClient r3.1`.
- Compilação com ESP32 core 3.3.11 e FQBN `esp32:esp32:esp32s3`: 1 083 125 B de flash
  (82%) e 48 248 B de RAM global (14%).
- Controle do C404: setpoint/hold/toques/correção/abort, display, modos e guarda.
- Enlace r3.1 do Hub:
  - tarefa FreeRTOS própria para hello/push/HTTP;
  - snapshot protegido e fila fixa de comandos;
  - `/bathData`, `ver=r3.1`, MAC real, fase/erro/display/mode/guard/ACK observáveis;
  - com `hub_enabled=1`, período efetivo limitado a 2 s para respeitar a janela do Hub;
  - push não é suspenso durante hold;
  - `hub_enabled=0` por padrão até a integração.
- `tests/host-sim`: 34 cenários passam com os fontes reais de setpoint, guarda, teclado,
  parser e contexto. Inclui três reentregas do mesmo `cmd_id` causando uma única sequência e
  comando recusado sem avanço do ACK.
- Aplicativo Android próprio do banho: `apps/flutter`, versão 1.1.0+2, quatro abas
  (Operação, Modos, Bancada, Config), reentrega idempotente, traço de 10 min, diagnóstico e
  aviso de firmware incompatível. `flutter analyze`, `flutter test` e build de APK passam.
- Aplicativo desktop `bath_app.py` testado contra servidor HTTP simulado (CLI e janela).

## Não implementado

- O Hub 10.4 ainda não possui `DEV_BATH`, `/bathData`, `bathBox`, segunda via térmica nem
  controlador de cascata.
- O Windows App 0.26.4 ainda não possui protocolo, simulador, interface, alarmes, receitas ou
  registros do banho.
- A integração do banho no aplicativo geral `Android_app/` do Hub não faz parte do app Android
  próprio citado acima e não foi implementada aqui.

Planos:

- visão geral: `../../docs/Planos/IMPLEMENTATION_PLAN_BANHO.md`;
- Hub consolidado: `../../docs/Planos/IMPLEMENTATION_PLAN_BANHO_HUB.md`;
- Windows App: `../../docs/Planos/IMPLEMENTATION_PLAN_BANHO_WINDOWS_APP.md`;
- app Android próprio: `../../docs/Planos/IMPLEMENTATION_PLAN_BANHO_APP_ANDROID.md`.

## Pendente de validação física

- Gates G1–G9 de `VALIDATION.md`: nenhum fechado.
- Relés, temporização real, display, sensoriamento, NVS, Wi-Fi/OTA e guarda contra o C404 real.
- Enlace r3.1 durante hold de 60 s, stack watermark e reentrega por perda de Wi-Fi após o Hub
  implementar o contrato.
- App Android contra o dispositivo real, incluindo G3b/G7b.
- Identificação térmica e sintonia com água antes de qualquer cultivo.

## Estado da montagem conhecido

- Relés, display e fonte ligados em 2026-09-19.
- Sensoriamento de `▲`/`▼` ainda deve ser ligado nos bornes `NO` dos relés 2 e 3 para GPIO
  2/42, com conferência de nível antes de conectar ao ESP32.
- `sense_mask=6` é padrão; habilitar `sense_enabled=1` somente após a conferência.
- Não usar o ponto `5VA` do C404; usar fonte 5 V dedicada.

## Decisões que a bancada ainda resolve

- `enter_key` e `confirm_key` reais;
- `step_c` e comportamento em `in.L`;
- atraso, taxa, aceleração e cauda da auto-repetição (`hold_*`);
- efeito de `▲+▼` juntas e viabilidade do gesto de modo;
- polaridade, fase e ordem dos dígitos do display;
- confirmação de que `Tempval` mede o reator e `100B` desabilita a via térmica original.

Build e testes comprovam coerência de software, não segurança térmica, acionamento físico nem
desempenho do processo.
