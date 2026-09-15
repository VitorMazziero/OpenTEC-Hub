# Relatório Técnico de Remediação de Controle e Segurança: F04, F06, F12 e F14

**Agente:** `teamwork_preview_explorer` (Explorer Remedy 1 — Iteration 2)  
**Data:** 2026-09-13  
**Status:** Concluído / Pronto para Integração  
**Documento Alvo:** `IMPLEMENTATION_PLAN_FLUXOMETRO.md` e Código-Fonte do Firmware Fluxômetro  
**Diretório de Trabalho:** `d:\OneDrive\PosDoc_Fapesp\Automacao_e_Controle\ProjetoTECNAL\.agents\explorer_remedy_1_it2`  

---

## 1. Observation (Observações Diretas do Repositório)

### 1.1 Feedback Adversarial de Challenger 2
Em `d:\OneDrive\PosDoc_Fapesp\Automacao_e_Controle\ProjetoTECNAL\.agents\challenger_2\handoff.md`, o auditor adversarial identificou quatro falhas críticas de viabilidade de compilação e segurança operacional no plano preliminar:
1. **F04:** O plano propunha zerar `integral_term = 0.0f;` em `Lifecycle.h`. A variável no código real chama-se `integralError`. Além disso, a proposta forçava `writeFlowSetpointToDAC(0.0f)` incondicionalmente, violando o contrato de `dacHold` especificado no manual (`COMANDOS_DISPOSITIVOS_EXTERNOS.md` §3.2 e §3.4) e carecia de validação de finitude (`!isnan(newTarget)`).
2. **F06:** O plano condicionava o intertravamento pneumático a `targetFlowSetpoint > MIN_FLOW_CUTOFF_THRESHOLD`. Um comando com setpoint zero e rotas fechadas (`v1=0, v2=0, valveFlow=0`) mantinha a válvula de corte geral aberta (`valveFlowState = 0`), criando uma **linha morta pressurizada**. Comandos parciais também podiam mascarar colisões de rotas.
3. **F12:** O tratamento de falha do ADS1115 e MCP4725 era pontual no boot (`setup()`), sem intertravamento contínuo (latch) capaz de impedir a reabertura subsequente de `valveFlowState = 0` ou a execução cega da malha PI.
4. **F14:** O Safe Stop executado no início do OTA em `OtaService.h` não adquiria `commandMutex`, criando condição de corrida com a `firmwareLoop()`. Além disso, ao expirar o watchdog de stall do OTA (`Lifecycle.h:136`), a flag `otaInProgress` era desmarcada, permitindo que a `httpTask` reassumisse a conexão com o Hub e reaplicasse o último comando de alta vazão sem supervisão.

### 1.2 Código-Fonte Ativo Auditado

#### A. Identificadores e Malha PI em `Lifecycle.h` e `FirmwareApp.cpp`
- Em `External-Devices/fluxometro/firmware/flowmeter/src/core/FirmwareApp.cpp:125-127`:
  ```cpp
  float Kp_flow = 0.1;
  float Ki_flow = 0.1;
  float integralError = 0.0;
  ```
- Em `External-Devices/fluxometro/firmware/flowmeter/src/core/Lifecycle.h:161-175`:
  ```cpp
  integralError += error * integralIntervalScale;
  ...
  float P_term = error * Kp_flow;
  float I_term = integralError * Ki_flow;
  ```
  *Constatação:* Não existe a variável `integral_term` no escopo global ou de classe; trata-se de uma variável local temporária (`I_term`). O acumulador de estado persistente chama-se `integralError`.

#### B. Semântica de `dacHold` no Firmware e na Documentação
- Em `COMANDOS_DISPOSITIVOS_EXTERNOS.md` §3.2 e §3.4:
  `dac_hold`: `"1 preserva DAC/PI ao zerar; 0 zera DAC/rampa"`.
- Em `External-Devices/fluxometro/firmware/flowmeter/src/core/FirmwareApp.cpp:121-124`:
  ```cpp
  // Omega FMA-5400 manual 5.5: keep the setpoint, toggle Valve Off. With dacHold a
  // zero setpoint asserts Valve Off and leaves the DAC and PI state untouched, so
  // the restart is the last operating point and not a step into a closed valve.
  bool dacHold = true; // dac_hold command, persisted.
  ```
- Em `External-Devices/fluxometro/firmware/flowmeter/src/protocol/CommandCodec.h:170-178`:
  ```cpp
  if (targetFlowSetpoint == 0.0f) {
    valveFlowState = 1;
    digitalWrite(VALVE_FLOW_PIN, HIGH);
    if (!dacHold) {
      flowSetpoint = 0.0f;
      rampedTarget = 0.0f;
      writeFlowSetpointToDAC(0.0f);
    }
  }
  ```
  *Constatação:* Quando `dacHold == true`, o corte mecânico (`valveFlowState = 1; digitalWrite(VALVE_FLOW_PIN, HIGH);`) fecha a linha física de gás, mas a tensão do DAC é retida para manter a pré-polarização do MFC. Forçar `writeFlowSetpointToDAC(0.0f)` para qualquer setpoint $\le 0.10$ destrói essa funcionalidade deliberada de engenharia.

#### C. Lógica de Atuação de Válvulas e Risco Pneumático
- Em `External-Devices/fluxometro/firmware/flowmeter/src/protocol/CommandCodec.h:139-153`:
  Os comandos de válvulas são aplicados sequencialmente conforme encontrados no JSON:
  ```cpp
  if (strcmp(keyBuf, "v_Flow") == 0 || strcmp(keyBuf, "valveFlow") == 0) {
    valveFlowState = (atoi(valBuf) != 0);
    digitalWrite(VALVE_FLOW_PIN, valveFlowState);
    recognizedCommand = true;
  }
  else if (strcmp(keyBuf, "v1") == 0 || strcmp(keyBuf, "valve_1") == 0) {
    valve1State = (atoi(valBuf) != 0);
    digitalWrite(VALVE1_PIN, valve1State);
    recognizedCommand = true;
  }
  else if (strcmp(keyBuf, "v2") == 0 || strcmp(keyBuf, "valve_2") == 0) {
    valve2State = (atoi(valBuf) != 0);
    digitalWrite(VALVE2_PIN, valve2State);
    recognizedCommand = true;
  }
  ```
  *Constatação:* O firmware aceita qualquer combinação arbitrária de válvulas, incluindo `v1=1, v2=1` (mistura e refluxo) ou `v1=0, v2=0, valveFlow=0` (bloqueio cego com corte aberto, criando linha morta pressurizada).

#### D. Manipulação Concorrente no OTA e Watchdog
- Em `External-Devices/fluxometro/firmware/flowmeter/src/api/OtaService.h:74-89`:
  O callback de chunk OTA (`AsyncWebServerRequest`) é assíncrono e não adquire `commandMutex` ao manipular o hardware no início do upload.
- Em `External-Devices/fluxometro/firmware/flowmeter/src/core/Lifecycle.h:132-137`:
  ```cpp
  if (otaInProgress && now - otaLastChunkMs > otaStallTimeoutMs) {
    Serial.printf("[OTA] No data for %lus after %u bytes. Hub link resumes; upload may still continue.\n",
                  otaStallTimeoutMs / 1000, (unsigned)Update.progress());
    otaStalled = true;
    otaInProgress = false;
  }
  ```
  *Constatação:* `otaInProgress = false` libera imediatamente a `httpTask` (`TaskRuntime.h:16`). Se o Hub possuir comandos em cache, o fluxo anterior é restabelecido sem nenhuma validação humana.

---

## 2. Logic Chain (Cadeia Lógica de Inferência)

1. **Correção de Identificador (F04):**
   - *Premissa:* A integridade de compilação C++ exige que qualquer diff referencie símbolos existentes na unidade de compilação.
   - *Passo dedutivo:* Como `integralError` é o símbolo declarado em `FirmwareApp.cpp:127` e manipulado em `Lifecycle.h:161`, a atribuição para reset do integrador deve ser `integralError = 0.0f;`.
2. **Preservação do Contrato `dacHold` (F04):**
   - *Premissa:* O manual do FMA-5400 (§5.5) e o protocolo TECNAL definem que `dacHold == true` mantém o ponto de operação elétrico do MFC e sua rampa durante paradas temporárias de fluxo via solenoide de corte.
   - *Passo dedutivo:* A condição para zerar o DAC e a rampa (`writeFlowSetpointToDAC(0.0f)`, `rampedTarget = 0.0f`) quando o setpoint for $\le 0.10\text{ L/min}$ deve ser estritamente guardada por `if (!dacHold)`. Se `dacHold == true`, o corte mecânico (`valveFlowState = 1`) atua imediatamente, mas o valor analógico do DAC e o `rampedTarget` permanecem protegidos.
3. **Finitude de Entrada (F04):**
   - *Premissa:* Entradas em ponto flutuante recebidas via JSON/Serial podem conter representações de `NaN` ou `±Inf`.
   - *Passo dedutivo:* Em C++, comparações ordinais com `NaN` avaliam como falso, permitindo que valores venenosos ultrapassem constrains e corrompam a malha PI. Uma validação explícita `if (isnan(parsed) || isinf(parsed))` antes de qualquer atribuição garante a imunidade da malha.
4. **Fechamento Incondicional em Rotas Nulas (F06):**
   - *Premissa:* É proibido manter a válvula de corte de montante (`valveFlowState = 0`) aberta quando ambas as válvulas de jusante estiverem fechadas (`v1 == 0 && v2 == 0`), pois o duto fica estanque e pressurizado sem alívio de fluxo, independentemente do setpoint demandado.
   - *Passo dedutivo:* O intertravamento não pode depender de `targetFlowSetpoint > 0.10f`. A regra deve ser **incondicional**: `if (stagedV1 == 0 && stagedV2 == 0) stagedVFlow = 1;`.
5. **Prevenção de Colisão por Comandos Parciais (F06):**
   - *Premissa:* O protocolo aceita comandos JSON parciais (ex.: `{"v1": 1}`).
   - *Passo dedutivo:* Se as variáveis de estágio forem inicializadas em 0, um comando que omita `v2` interpretará erroneamente `v2` como 0, mesmo que `valve2State` esteja em 1 no hardware, podendo causar colisão ou abertura indevida. Logo, os estágios devem ser inicializados com o estado vigente de hardware: `uint8_t stagedV1 = valve1State; uint8_t stagedV2 = valve2State; uint8_t stagedVFlow = valveFlowState;`.
6. **Latching Contínuo de Falha de Hardware (F12):**
   - *Premissa:* Falhas em dispositivos I²C (ADS1115 / MCP4725) podem ocorrer tanto no boot quanto a quente (desconexão ou ESD).
   - *Passo dedutivo:* Flags booleanas (`adsHealthy`, `dacHealthy`, `hardwareFaultLatched`) devem ser testadas em cada ciclo de `firmwareLoop()`. Havendo falha latente ou ativa, o código força `valveFlowState = 1` (corte fechado) e desativa a execução da malha PI.
7. **Sincronização por Mutex e Trava de Stall no OTA (F14):**
   - *Premissa:* O handler OTA roda na task `AsyncTCP` concorrentemente à `firmwareLoop()` e `httpTask` no Core 1.
   - *Passo dedutivo:* A manipulação dos atuadores e estados no Safe Stop do OTA deve adquirir `commandMutex` com timeout finito. Em caso de timeout do watchdog de stall (90 s), o nó deve entrar em Safe Stop travado (`otaSafeLatch = true`), impedindo que a reconexão automática do Hub retome vazões anteriores.

---

## 3. Caveats (Ressalvas Técnicas)

- **Escopo Analítico:** Em obediência estrita às instruções do usuário, nenhuma alteração direta foi aplicada aos arquivos de código-fonte de produção durante esta investigação; todas as soluções estão detalhadas neste relatório em formato de diff pronto para aplicação.
- **Valores Analógicos em Bancada:** Os testes com ruído elétrico e resposta transitória do atuador FMA-5400 sob transição de `dacHold` dependem de bancada física (previstos no Checklist §3.11).

---

## 4. Conclusion: Especificação Técnica e Diffs Recomendados

Abaixo estão as especificações exatas de código para inclusão no `IMPLEMENTATION_PLAN_FLUXOMETRO.md` e aplicação subsequente no firmware:

```
┌─────────────────────────────────────────────────────────────────────────────┐
│ RESUMO DAS REMEDIAÇÕES TÉCNICAS (EXPLORER 1 - ITERAÇÃO 2)                   │
├──────┬──────────────────────┬───────────────────────────────┬───────────────┤
│ Item │ Arquivo Principal    │ Elemento Técnico Corrigido    │ Classificação │
├──────┼──────────────────────┼───────────────────────────────┼───────────────┤
│ F04  │ Lifecycle.h / Codec  │ integralError, dacHold, isnan │ Safety & PI   │
│ F06  │ CommandCodec.h       │ Incondicional (v1=0 && v2=0)  │ Pneumática    │
│ F12  │ Lifecycle.h / Core   │ Latch contínuo de hardware    │ Interlock     │
│ F14  │ OtaService.h / Loop  │ commandMutex + otaSafeLatch   │ Concorrência  │
└──────┴──────────────────────┴───────────────────────────────┴───────────────┘
```

---

### 4.1 Remediação F04: Nomenclatura `integralError`, Respeito a `dacHold` e Finitude

#### A. Arquivo `FirmwareApp.cpp` (Constante de Limiar)
Adicionar a definição canônica de limiar operacional mínimo:
```cpp
// External-Devices/fluxometro/firmware/flowmeter/src/core/FirmwareApp.cpp
// Linha ~110:
#define MIN_FLOW_CUTOFF_THRESHOLD 0.10f
```

#### B. Arquivo `CommandCodec.h` (Finitude e Processamento de Setpoint)
Substituir o bloco de processamento de setpoint (`CommandCodec.h:164-183`):

```cpp
<<<<
    else if (strcmp(keyBuf, "flow_setpoint") == 0 || strcmp(keyBuf, "flowSetpoint") == 0) {
      float newTarget = constrain(strtof(valBuf, nullptr), 0.0f, maxFlowRate);
      recognizedCommand = true;

      if (fabs(newTarget - targetFlowSetpoint) > 0.001f || newTarget == 0.0f || targetFlowSetpoint == 0.0f) {
        targetFlowSetpoint = newTarget;
        if (targetFlowSetpoint == 0.0f) {
          valveFlowState = 1;
          digitalWrite(VALVE_FLOW_PIN, HIGH);
          if (!dacHold) {
            flowSetpoint = 0.0f;
            rampedTarget = 0.0f;
            writeFlowSetpointToDAC(0.0f);
          }
        }
        Serial.printf("New Target Accepted: %.3f\n", targetFlowSetpoint);
      } else {
        Serial.printf("Target Update Ignored (Delta < 0.05): %.3f\n", newTarget);
      }
    }
====
    else if (strcmp(keyBuf, "flow_setpoint") == 0 || strcmp(keyBuf, "flowSetpoint") == 0) {
      char *endPtr = nullptr;
      float parsedVal = strtof(valBuf, &endPtr);
      if (endPtr == valBuf || isnan(parsedVal) || isinf(parsedVal)) {
        Serial.printf("[CMD] Rejeitado setpoint não-finito ou inválido: '%s'\n", valBuf);
      } else {
        float newTarget = constrain(parsedVal, 0.0f, maxFlowRate);
        recognizedCommand = true;

        // Se hardware em falha ou OTA em safe latch, bloqueia alvos positivos
        if ((hardwareFaultLatched || !adsHealthy || !dacHealthy || otaSafeLatch) && newTarget > 0.0f) {
          Serial.println("[SAFETY] Setpoint rejeitado: falha de hardware ou OTA safe latch ativo!");
          newTarget = 0.0f;
        }

        if (newTarget <= MIN_FLOW_CUTOFF_THRESHOLD) {
          targetFlowSetpoint = 0.0f;
          stagedVFlow = 1; // Fecha corte geral mecanicamente
          valveCommandPresent = true;
          if (!dacHold) {
            flowSetpoint = 0.0f;
            rampedTarget = 0.0f;
            integralError = 0.0f;
            flowFeedforward = 0.0f;
            writeFlowSetpointToDAC(0.0f);
          }
          Serial.printf("Setpoint <= %.2f: Corte acionado (dacHold=%s)\n",
                        MIN_FLOW_CUTOFF_THRESHOLD, dacHold ? "ON" : "OFF");
        } else {
          targetFlowSetpoint = newTarget;
          Serial.printf("Novo Setpoint Aceito: %.3f L/min\n", targetFlowSetpoint);
        }
      }
    }
>>>>
```

#### C. Arquivo `Lifecycle.h` (Malha PI e Slew Rate)
Substituir o tratamento de setpoint na malha (`Lifecycle.h:145-199`):

```cpp
<<<<
    // Slew the reference. Zero is applied at once; with dacHold a zero target
    // leaves rampedTarget where it was, so the restart resumes from that point.
    if (targetFlowSetpoint <= 0.0f) {
      if (!dacHold) rampedTarget = 0.0f;
    } else if (rampRate > 0.0f) {
      float step = rampRate * controlInterval / 1000.0f;
      if (rampedTarget < targetFlowSetpoint) rampedTarget = min(rampedTarget + step, targetFlowSetpoint);
      else if (rampedTarget > targetFlowSetpoint) rampedTarget = max(rampedTarget - step, targetFlowSetpoint);
    } else {
      rampedTarget = targetFlowSetpoint;
    }

    if (targetFlowSetpoint > 0.1f && valveFlowState == 0) {
      float error = rampedTarget - readFlowRate;
      flowFeedforward = feedforwardSetpoint(rampedTarget);

      // Deadband: below one DAC LSB the integral would only chase sensor noise.
      if (abs(error) > 0.01) {
        integralError += error * integralIntervalScale;

        // Anti-windup in output units: the integral may take the output anywhere
        // in 0..maxFlowRate relative to the corrected base, and no further.
        if (Ki_flow > 0.0f) {
          float iMin = -flowFeedforward / Ki_flow;
          float iMax = (maxFlowRate - flowFeedforward) / Ki_flow;
          integralError = constrain(integralError, iMin, iMax);
        } else {
          integralError = 0.0f;
        }

        float P_term = error * Kp_flow;
        float I_term = integralError * Ki_flow;

        // Output = setpoint corrigido (of the ramped reference) + PI trim.
        float newFlowSetpoint = constrain(flowFeedforward + P_term + I_term, 0.0f, maxFlowRate);

        // Commit only once the DAC actually took the value: a timed-out I2C write
        // used to leave flowSetpoint updated and the DAC stale, never retried.
        if (abs(newFlowSetpoint - flowSetpoint) > dacUpdateThreshold) {
          if (writeFlowSetpointToDAC(newFlowSetpoint)) flowSetpoint = newFlowSetpoint;
        }
        if (debugPI) {
          Serial.printf("[PI] t=%lu Tgt:%.3f Ref:%.3f FF:%.3f Act:%.3f Err:%.3f P:%.3f I:%.3f Out:%.3f\n",
                        now, targetFlowSetpoint, rampedTarget, flowFeedforward, readFlowRate,
                        error, P_term, I_term, flowSetpoint);
        }
      }
    } else if (targetFlowSetpoint > 0.1f) {
      // Live target but Valve Off asserted: no flow can exist and the error is not
      // the output's fault. Integral and DAC stay frozen until the valve opens.
    } else if (!dacHold) {
      // Legacy behaviour: a zero target drops the DAC to zero as well.
      flowFeedforward = 0.0f;
      if (flowSetpoint > 0) {
        if (writeFlowSetpointToDAC(0)) flowSetpoint = 0;
      }
    }
====
    // Tratamento de rampa e slew rate (F04)
    if (targetFlowSetpoint <= MIN_FLOW_CUTOFF_THRESHOLD) {
      if (!dacHold) rampedTarget = 0.0f;
    } else if (rampRate > 0.0f) {
      float step = rampRate * controlInterval / 1000.0f;
      if (rampedTarget < targetFlowSetpoint) rampedTarget = min(rampedTarget + step, targetFlowSetpoint);
      else if (rampedTarget > targetFlowSetpoint) rampedTarget = max(rampedTarget - step, targetFlowSetpoint);
    } else {
      rampedTarget = targetFlowSetpoint;
    }

    if (targetFlowSetpoint > MIN_FLOW_CUTOFF_THRESHOLD && valveFlowState == 0) {
      float error = rampedTarget - readFlowRate;
      flowFeedforward = feedforwardSetpoint(rampedTarget);

      if (abs(error) > 0.01f) {
        integralError += error * integralIntervalScale;

        if (Ki_flow > 0.0f) {
          float iMin = -flowFeedforward / Ki_flow;
          float iMax = (maxFlowRate - flowFeedforward) / Ki_flow;
          integralError = constrain(integralError, iMin, iMax);
        } else {
          integralError = 0.0f;
        }

        float P_term = error * Kp_flow;
        float I_term = integralError * Ki_flow;

        float newFlowSetpoint = constrain(flowFeedforward + P_term + I_term, 0.0f, maxFlowRate);

        if (abs(newFlowSetpoint - flowSetpoint) > dacUpdateThreshold) {
          if (writeFlowSetpointToDAC(newFlowSetpoint)) flowSetpoint = newFlowSetpoint;
        }
        if (debugPI) {
          Serial.printf("[PI] t=%lu Tgt:%.3f Ref:%.3f FF:%.3f Act:%.3f Err:%.3f P:%.3f I:%.3f Out:%.3f\n",
                        now, targetFlowSetpoint, rampedTarget, flowFeedforward, readFlowRate,
                        error, P_term, I_term, flowSetpoint);
        }
      }
    } else if (targetFlowSetpoint > MIN_FLOW_CUTOFF_THRESHOLD) {
      // Alvo ativo com válvula fechada: congela integrador e DAC sem descarregar
    } else if (!dacHold) {
      // Setpoint nulo/sub-limiar com dacHold desligado: zera atuador e limpa integrador
      flowFeedforward = 0.0f;
      integralError = 0.0f; // <-- IDENTIFICADOR EXATO
      rampedTarget = 0.0f;
      if (flowSetpoint > 0.0f) {
        if (writeFlowSetpointToDAC(0.0f)) flowSetpoint = 0.0f;
      }
    } else {
      // Setpoint nulo/sub-limiar com dacHold ativo:
      // Corte geral mecânico assegurado, tensão do DAC e rampedTarget preservados.
    }
>>>>
```

---

### 4.2 Remediação F06: Fechamento Incondicional e Proteção de Linha Morta

#### Arquivo `CommandCodec.h` (Staging de Comandos e Intertravamento)
Modificar a função `processReceivedData` (`CommandCodec.h:93-153` e `214-237`):

```cpp
// Antes do loop while(cursor < lastBrace):
uint8_t stagedV1 = valve1State;       // Inicializado com o hardware real
uint8_t stagedV2 = valve2State;       // Inicializado com o hardware real
uint8_t stagedVFlow = valveFlowState; // Inicializado com o hardware real
bool valveCommandPresent = false;

// Dentro do loop while(cursor < lastBrace):
if (strcmp(keyBuf, "v_Flow") == 0 || strcmp(keyBuf, "valveFlow") == 0) {
  stagedVFlow = (atoi(valBuf) != 0) ? 1 : 0;
  valveCommandPresent = true;
  recognizedCommand = true;
}
else if (strcmp(keyBuf, "v1") == 0 || strcmp(keyBuf, "valve_1") == 0) {
  stagedV1 = (atoi(valBuf) != 0) ? 1 : 0;
  valveCommandPresent = true;
  recognizedCommand = true;
}
else if (strcmp(keyBuf, "v2") == 0 || strcmp(keyBuf, "valve_2") == 0) {
  stagedV2 = (atoi(valBuf) != 0) ? 1 : 0;
  valveCommandPresent = true;
  recognizedCommand = true;
}

// Logo após o término do loop while(cursor < lastBrace), antes de liberar o mutex:
// REGRA 1 (F06): Prevenção de Colisão de Rotas (V1 e V2 abertas simultaneamente)
if (stagedV1 == 1 && stagedV2 == 1) {
  Serial.println("[SAFETY] Tentativa de abrir V1 e V2 simultaneamente rejeitada: forçando rotas fechadas e corte!");
  stagedV1 = 0;
  stagedV2 = 0;
  stagedVFlow = 1; // Corta fluxo de entrada
  valveCommandPresent = true;
}

// REGRA 2 (F06): Prevenção de Linha Morta Pressurizada (INCONDICIONAL)
// Se nenhuma rota estiver aberta, o corte geral DEVE fechar, independente de setpoint
if (stagedV1 == 0 && stagedV2 == 0) {
  if (stagedVFlow == 0) {
    Serial.println("[SAFETY] Nenhuma rota aberta (V1=0 && V2=0): forçando corte geral fechado incondicionalmente!");
  }
  stagedVFlow = 1; // Força corte geral fechado (GPIO 5 HIGH)
  valveCommandPresent = true;
}

// Aplica as alterações atômicas no hardware se houve comando de válvula
if (valveCommandPresent) {
  valve1State = stagedV1;
  digitalWrite(VALVE1_PIN, valve1State ? HIGH : LOW);

  valve2State = stagedV2;
  digitalWrite(VALVE2_PIN, valve2State ? HIGH : LOW);

  valveFlowState = stagedVFlow;
  digitalWrite(VALVE_FLOW_PIN, valveFlowState ? HIGH : LOW);
}
```

#### Defesa em Profundidade em `Lifecycle.h`
Adicionar no início do ciclo de controle de `Lifecycle.h:142`:
```cpp
// Defesa em profundidade contínua: impede duto estanque pressurizado
if (valve1State == 0 && valve2State == 0 && valveFlowState == 0) {
  valveFlowState = 1;
  digitalWrite(VALVE_FLOW_PIN, HIGH);
  Serial.println("[SAFETY LATCH] Duto estanque sem rota detectado no loop: corte geral forçado fechado!");
}
```

---

### 4.3 Remediação F12: Latch Contínuo de Falha de Hardware (ADS1115 / MCP4725)

#### A. Arquivo `FirmwareApp.cpp`
Declarar as flags globais de supervisão de hardware:
```cpp
// External-Devices/fluxometro/firmware/flowmeter/src/core/FirmwareApp.cpp
// Linha ~74:
bool adsHealthy = false;
bool dacHealthy = false;
bool hardwareFaultLatched = false;
```

#### B. Arquivo `Lifecycle.h` (`firmwareSetup`)
Substituir a inicialização I²C (`Lifecycle.h:67-83`):
```cpp
<<<<
  Serial.print("6. Init ADS1115... ");
  if (ads.begin(0x48)) {
    ads.setGain(GAIN_TWOTHIRDS);
    ads.setDataRate(RATE_ADS1115_128SPS);
    Serial.println("OK");
  } else {
    Serial.println("FAILED (Check wiring!)");
    // Não travamos aqui com while(1) para permitir debug do resto
  }

  Serial.print("7. Init MCP4725... ");
  if (mcp.begin(0x60)) {
    Serial.println("OK");
  } else {
    Serial.println("FAILED (Check wiring!)");
  }
====
  Serial.print("6. Init ADS1115... ");
  adsHealthy = ads.begin(0x48);
  if (adsHealthy) {
    ads.setGain(GAIN_TWOTHIRDS);
    ads.setDataRate(RATE_ADS1115_128SPS);
    Serial.println("OK");
  } else {
    Serial.println("FAILED (Check wiring!)");
  }

  Serial.print("7. Init MCP4725... ");
  dacHealthy = mcp.begin(0x60);
  if (dacHealthy) {
    Serial.println("OK");
  } else {
    Serial.println("FAILED (Check wiring!)");
  }

  // Intertravamento Mandatório de Falha de Hardware no Boot
  if (!adsHealthy || !dacHealthy) {
    hardwareFaultLatched = true;
    valveFlowState = 1; // Garante corte fechado
    digitalWrite(VALVE_FLOW_PIN, HIGH);
    Serial.println("[HARDWARE FAULT] Latch ativado no boot: corte fechado e malha PI desativada!");
  }
>>>>
```

#### C. Arquivo `Lifecycle.h` (`firmwareLoop`)
Garantir o intertravamento contínuo em `Lifecycle.h:142`:
```cpp
// Latch contínuo de hardware: bloqueia PI e força corte
if (hardwareFaultLatched || !adsHealthy || !dacHealthy) {
  if (valveFlowState == 0) {
    valveFlowState = 1;
    digitalWrite(VALVE_FLOW_PIN, HIGH);
  }
  targetFlowSetpoint = 0.0f;
  rampedTarget = 0.0f;
  flowSetpoint = 0.0f;
  integralError = 0.0f;
  flowFeedforward = 0.0f;
  xSemaphoreGive(commandMutex);
  return; // Pula execução da malha PI
}
```

#### D. Arquivo `FlowIo.h` (Detecção em Tempo de Execução)
Atualizar `writeFlowSetpointToDAC`:
```cpp
bool writeFlowSetpointToDAC(float flowSetpointVal) {
  if (hardwareFaultLatched || !dacHealthy) return false;
  uint16_t dacValue = (flowSetpointVal / maxFlowRate) * 4095;
  dacValue = constrain(dacValue, 0, 4095);
  if (i2cMutex != NULL && xSemaphoreTake(i2cMutex, pdMS_TO_TICKS(250)) == pdTRUE) {
    Wire.beginTransmission(0x60);
    byte err = Wire.endTransmission();
    if (err != 0) {
      Serial.printf("[DAC] Falha de comunicação I2C (%d). Latched!\n", err);
      dacHealthy = false;
      hardwareFaultLatched = true;
      xSemaphoreGive(i2cMutex);
      return false;
    }
    mcp.setVoltage(dacValue, false);
    xSemaphoreGive(i2cMutex);
    return true;
  }
  return false;
}
```

---

### 4.4 Remediação F14: Sincronização por Mutex e Trava de Stall no OTA

#### A. Arquivo `FirmwareApp.cpp`
Declarar a trava de stall de OTA:
```cpp
// External-Devices/fluxometro/firmware/flowmeter/src/core/FirmwareApp.cpp
// Linha ~44:
volatile bool otaSafeLatch = false;
```

#### B. Arquivo `OtaService.h` (Proteção com `commandMutex` no Safe Stop)
Substituir o início do handler de upload em `OtaService.h:74-90`:

```cpp
<<<<
    [](AsyncWebServerRequest *request, String filename, size_t index, uint8_t *data, size_t len, bool final) {
      if (index == 0) {
        otaRejectReason = "";
        // Only the app image belongs in an OTA slot. The other exports start with the
        // same 0xE9 magic, so Update would accept them and the name is the only tell.
        if (filename.indexOf("merged") >= 0 || filename.indexOf("bootloader") >= 0 || filename.indexOf("partitions") >= 0) {
          otaRejectReason = "'" + filename + "' is not the app image, send the plain .ino.bin";
          Serial.println("[OTA] " + otaRejectReason);
          return;
        }
        Serial.printf("[OTA] Upload start: %s\n", filename.c_str());
        if (Update.isRunning()) Update.abort();   // safe here: same task that writes
        otaStalled = false;
        otaNextProgressLog = 0;
        if (!Update.begin(UPDATE_SIZE_UNKNOWN)) Update.printError(Serial);
      }
====
    [](AsyncWebServerRequest *request, String filename, size_t index, uint8_t *data, size_t len, bool final) {
      if (index == 0) {
        otaRejectReason = "";
        if (filename.indexOf("merged") >= 0 || filename.indexOf("bootloader") >= 0 || filename.indexOf("partitions") >= 0) {
          otaRejectReason = "'" + filename + "' is not the app image, send the plain .ino.bin";
          Serial.println("[OTA] " + otaRejectReason);
          return;
        }
        Serial.printf("[OTA] Upload start: %s\n", filename.c_str());

        // --- F14: Safe Stop Atômico Protegido por commandMutex ---
        if (commandMutex != NULL && xSemaphoreTake(commandMutex, pdMS_TO_TICKS(1000)) == pdTRUE) {
          valveFlowState = 1;
          digitalWrite(VALVE_FLOW_PIN, HIGH); // Corte fechado
          valve1State = 0;
          digitalWrite(VALVE1_PIN, LOW);       // V1 fechada
          valve2State = 0;
          digitalWrite(VALVE2_PIN, LOW);       // V2 fechada

          targetFlowSetpoint = 0.0f;
          rampedTarget = 0.0f;
          flowSetpoint = 0.0f;
          integralError = 0.0f;
          flowFeedforward = 0.0f;

          writeFlowSetpointToDAC(0.0f);        // Zero V no DAC

          xSemaphoreGive(commandMutex);
          Serial.println("[OTA SAFETY] Safe Stop executado com sucesso sob commandMutex!");
        } else {
          // Timeout de mutex: atuação direta de emergência nos pinos
          digitalWrite(VALVE_FLOW_PIN, HIGH);
          digitalWrite(VALVE1_PIN, LOW);
          digitalWrite(VALVE2_PIN, LOW);
          writeFlowSetpointToDAC(0.0f);
          Serial.println("[OTA SAFETY] Timeout de mutex: pinos de hardware forçados para estado seguro!");
        }

        if (Update.isRunning()) Update.abort();
        otaStalled = false;
        otaSafeLatch = false;
        otaNextProgressLog = 0;
        if (!Update.begin(UPDATE_SIZE_UNKNOWN)) Update.printError(Serial);
      }
>>>>
```

#### C. Arquivo `Lifecycle.h` (Watchdog de Stall com Safe Latch)
Substituir o tratamento de stall (`Lifecycle.h:132-137`):

```cpp
<<<<
  if (otaInProgress && now - otaLastChunkMs > otaStallTimeoutMs) {
    Serial.printf("[OTA] No data for %lus after %u bytes. Hub link resumes; upload may still continue.\n",
                  otaStallTimeoutMs / 1000, (unsigned)Update.progress());
    otaStalled = true;
    otaInProgress = false;
  }
====
  if (otaInProgress && now - otaLastChunkMs > otaStallTimeoutMs) {
    Serial.printf("[OTA] Sem dados por %lus após %u bytes. STALL DETECTADO: travando em SAFE LATCH!\n",
                  otaStallTimeoutMs / 1000, (unsigned)Update.progress());
    otaStalled = true;
    otaSafeLatch = true; // Trava persistente impedindo retomada de fluxo
    otaInProgress = false;

    // Garante parada segura mecânica e elétrica sob mutex
    if (commandMutex != NULL && xSemaphoreTake(commandMutex, pdMS_TO_TICKS(200)) == pdTRUE) {
      valveFlowState = 1;
      digitalWrite(VALVE_FLOW_PIN, HIGH);
      valve1State = 0;
      digitalWrite(VALVE1_PIN, LOW);
      valve2State = 0;
      digitalWrite(VALVE2_PIN, LOW);
      targetFlowSetpoint = 0.0f;
      rampedTarget = 0.0f;
      flowSetpoint = 0.0f;
      integralError = 0.0f;
      flowFeedforward = 0.0f;
      writeFlowSetpointToDAC(0.0f);
      xSemaphoreGive(commandMutex);
    }
  }
>>>>
```

---

## 5. Verification Method (Método de Verificação Independente)

Para auditar e verificar a integridade conceitual e técnica desta remediação:

1. **Validação de Sintaxe e Símbolos C++:**
   - Confirmar em `External-Devices/fluxometro/firmware/flowmeter/src/core/Lifecycle.h:161` que o identificador de acumulação integral é estritamente `integralError`.
   - Confirmar em `External-Devices/fluxometro/firmware/flowmeter/src/core/FirmwareApp.cpp:121-124` a semântica de `dacHold`.
2. **Matriz de Teste Teórico de Rotas (F06):**
   - **Caso 1:** Payload `{"v1": 0, "v2": 0, "valveFlow": 0}` $\rightarrow$ Regra 2 força `stagedVFlow = 1`. Aprovado (sem linha morta).
   - **Caso 2:** Payload `{"v1": 1, "v2": 1, "valveFlow": 0}` $\rightarrow$ Regra 1 força `stagedV1 = 0, stagedV2 = 0, stagedVFlow = 1`. Aprovado (sem colisão).
   - **Caso 3:** Payload parcial `{"v1": 1}` com `valve2State == 1` no hardware $\rightarrow$ Inicialização dos estágios detecta `stagedV1=1 && stagedV2=1` e impede colisão. Aprovado.
3. **Matriz de Teste Teórico de Setpoint (F04):**
   - **Caso 1:** Setpoint `0.05 L/min` com `dacHold = true` $\rightarrow$ Avalia `newTarget <= 0.10f`, fecha corte geral `valveFlowState = 1`, preserva DAC e `rampedTarget`.
   - **Caso 2:** Setpoint `0.05 L/min` com `dacHold = false` $\rightarrow$ Zera DAC (`writeFlowSetpointToDAC(0.0f)`), reseta `integralError = 0.0f`, fecha corte geral `valveFlowState = 1`.
   - **Caso 3:** Setpoint `"NaN"` ou `"inf"` $\rightarrow$ Rejeitado no teste `isnan/isinf` sem alterar `targetFlowSetpoint`.
4. **Matriz de Teste de Concorrência e Falha (F12 / F14):**
   - Se `adsHealthy == false` no boot ou em tempo de execução, `hardwareFaultLatched = true`, bloqueando comandos de `valveFlowState = 0` e impedindo a malha PI de operar.
   - Quando OTA inicia (`index == 0`), `commandMutex` é adquirido antes de forçar o Safe Stop. Se o upload estagnar por mais de 90 segundos, `otaSafeLatch` é armado, rejeitando reabertura de fluxo por comandos periódicos do Hub até intervenção do operador.
