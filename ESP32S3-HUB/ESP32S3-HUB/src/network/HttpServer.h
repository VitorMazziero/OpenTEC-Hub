// [CORREÇÃO (Issue 1)] Helper para acúmulo de POST por requisição
struct CmdBuf { String buf; size_t total = 0; };

// ------------------------------------------------------------------
// startWiFi():
// ------------------------------------------------------------------
void startWiFi() {
  IPAddress apIP(192, 168, 4, 1);
  IPAddress subnet(255, 255, 255, 0);
  WiFi.mode(WIFI_MODE_AP);
  WiFi.softAPConfig(apIP, apIP, subnet);
  // PC + flowmeter + biomass + agitator + pump already exceeds the v6 limit.
  bool apStarted = WiFi.softAP(WIFI_SSID, WIFI_PASSWORD, 6, 0, 8);

  if (apStarted) {
    ESP32_INFO("Ponto de acesso Wi-Fi iniciado");
    ESP32_INFO(String("SSID: ") + WIFI_SSID);
    ESP32_INFO(String("IP do ponto de acesso: ") + WiFi.softAPIP().toString());
    vTaskDelay(pdMS_TO_TICKS(100)); // Permite a estabilização da stack de rede

    // Handler de POST /command (robusto, lida com 'chunks' de dados)
    server.on(
      "/command", HTTP_POST, [](AsyncWebServerRequest *request) {},
      NULL,
      [](AsyncWebServerRequest *request, uint8_t *data, size_t len, size_t index, size_t total) {
        
        // [CORREÇÃO (Issue 1)] Usa estado por requisição (request->_tempObject)
        auto *state = (CmdBuf*)request->_tempObject;

        if (index == 0) { // Primeiro chunk
          if (total > MAX_HTTP_PAYLOAD) {
            request->send(413, "text/plain", "Payload too large");
            return;
          }
          state = new CmdBuf();
          if (!state) {
            request->send(500, "text/plain", "Out of memory");
            return;
          }
          state->buf.reserve(total + 1);
          state->total = total;
          request->_tempObject = state;

          // [CORREÇÃO (Issue 1)] Anexa o handler de desconexão *ao request*
          // para limpar o buffer em caso de aborto.
          // [CORREÇÃO DE COMPILAÇÃO] Captura [request] e não recebe argumentos (void()).
          request->onDisconnect([request](){
            if (request->_tempObject != nullptr) {
                auto *state = (CmdBuf*)request->_tempObject;
                delete state;
                request->_tempObject = nullptr;
                // Serial.println("DEBUG: /command client disconnected, buffer cleared.");
            }
          });
        }

        if (state) {
          // [CORREÇÃO (Minor)] Evita cópia extra de string
          state->buf += String((const char*)data, len);
        }

        // Aguarda todos os 'chunks' chegarem
        if (index + len < total) {
          return;
        }

        // Todos os 'chunks' recebidos, processa o corpo (body) completo
        if (state) {
          int s = state->buf.indexOf('{');
          int e = state->buf.lastIndexOf('}');
          if (s != -1 && e != -1 && s < e) {
            String jsonContent = state->buf.substring(s, e + 1);
            if (httpCommandQueue.enqueue(jsonContent)) {
              request->send(200, "text/plain", "Queued");
            } else {
              request->send(503, "text/plain", "Command queue full");
            }
          } else {
            request->send(400, "text/plain", "Invalid JSON format");
          }
          // Limpa o estado da requisição (buffer)
          delete state;
          // Seta como nullptr para que o onDisconnect não faça double-delete
          request->_tempObject = nullptr; 
        }
    });

    // Handler de GET /readData (com ETag para cache)
    server.on("/readData", HTTP_GET, [](AsyncWebServerRequest *request) {
      String cachedJson;
      uint32_t cachedSampleId = 0;
      if (xSemaphoreTake(stateMutex, portMAX_DELAY) == pdTRUE) {
        cachedJson = lastSensorJson;
        cachedSampleId = sampleId;
        xSemaphoreGive(stateMutex);
      }
      String etag = "\"" + String(cachedSampleId) + "\"";
      if (request->hasHeader("If-None-Match")) {
        auto *h = request->getHeader("If-None-Match");
        if (h && h->value() == etag) {
          request->send(304); // Not Modified
          return;
        }
      }
      AsyncWebServerResponse *res =
          request->beginResponse(200, "application/json", cachedJson);
      res->addHeader("ETag", etag);
      res->addHeader("Cache-Control", "no-store");
      request->send(res);
    });

    // Handler de GET /ping (health check)
    server.on("/ping", HTTP_GET, [](AsyncWebServerRequest *request) {
      request->send(200, "text/plain", "pong");
    });

    // Handler de GET /distance (recebe dados do sensor de distância)
    server.on("/distance", HTTP_GET, [](AsyncWebServerRequest *request) {
      if (!request->hasParam("distance")) {
        request->send(400, "text/plain", "Missing 'distance' parameter");
        return;
      }
      String distStr = request->getParam("distance")->value();
      float   newDistance = distStr.toFloat();
      
      // Filtro de estagnação
      static float   buf[5]        = {0.0f};
      static uint8_t bufIndex      = 0;
      static uint8_t bufCount      = 0;
      static float   lastAccepted  = NAN;
      const float   ES            = 1e-3f;
      float acceptedDistance = -1.0f;
      if (xSemaphoreTake(stateMutex, portMAX_DELAY) == pdTRUE) {
        buf[bufIndex] = newDistance;
        bufIndex = (bufIndex + 1) % 5;
        if (bufCount < 5) ++bufCount;
        bool stagnated = false;
        if (bufCount == 5) {
          stagnated = true;
          for (uint8_t i = 1; i < 5; ++i) {
            if (fabsf(buf[i] - buf[0]) > ES) {
              stagnated = false;
              break;
            }
          }
        }
        if (stagnated) {
          distanceSensorValue = -1.0f;
        } else if (isnan(lastAccepted) || fabsf(newDistance - lastAccepted) > ES) {
          distanceSensorValue = newDistance;
          lastAccepted = newDistance;
        }
        if (request->hasParam("time")) {
          distanceSensorTime = request->getParam("time")->value().toFloat();
        }
        distanceSensorLastUpdate = millis();
        acceptedDistance = distanceSensorValue;
        xSemaphoreGive(stateMutex);
      }
      String json = "{\"Distance\":" + String(acceptedDistance, 2) + "}";
      request->send(200, "application/json", json);
    });

    // Handler de GET /flowData (recebe dados do fluxômetro)
    server.on("/flowData", HTTP_GET, [](AsyncWebServerRequest *request) {
      if (request->hasParam("seconds") && request->hasParam("flow_voltage") && request->hasParam("flow_rate") && request->hasParam("flow_setpoint") && request->hasParam("valve1State") && request->hasParam("valve2State")) {
        float newTime = request->getParam("seconds")->value().toFloat();
        float newVoltage = request->getParam("flow_voltage")->value().toFloat();
        float newRate = request->getParam("flow_rate")->value().toFloat();
        float newSetpoint = request->getParam("flow_setpoint")->value().toFloat();
        int newValve1 = request->getParam("valve1State")->value().toInt() != 0;
        int newValve2 = request->getParam("valve2State")->value().toInt() != 0;
        int newValveFlow = request->hasParam("valveFlowState")
                             ? (request->getParam("valveFlowState")->value().toInt() != 0)
                             : flowmeterValveFlow;
        bool hasAck = request->hasParam("ack_cmd_id");
        uint32_t reportedAck = hasAck
                                 ? (uint32_t)strtoul(request->getParam("ack_cmd_id")->value().c_str(), NULL, 10)
                                 : 0;
        String reportedSource = request->hasParam("command_source")
                                  ? request->getParam("command_source")->value()
                                  : "unknown";

        // v06 tags each frame with the id of the flowmeter power-on that produced it.
        // A v05 node sends none, and then this degrades to the old adopt-always behaviour.
        uint32_t reportedBootId = request->hasParam("boot_id")
                                    ? (uint32_t)strtoul(request->getParam("boot_id")->value().c_str(), NULL, 10)
                                    : 0;
        bool reportedReconnect = request->hasParam("reconnect_wifi")
                                   ? request->getParam("reconnect_wifi")->value().toInt() != 0
                                   : true;

        bool ackedNow = false;
        uint32_t ackedRevision = 0;
        bool rebootDetected = false;
        uint32_t rebootRevision = 0;
        if (xSemaphoreTake(cmdMutex, portMAX_DELAY) == pdTRUE) {
          flowmeterTime = newTime;
          flowmeterVoltage = newVoltage;
          flowmeterRate = newRate;
          flowmeterSetpoint = newSetpoint;
          flowmeterValve1 = newValve1;
          flowmeterValve2 = newValve2;
          flowmeterValveFlow = newValveFlow;
          flowmeterCommOn = true;
          flowmeterLastUpdate = millis();
          flowmeterLastCommandSource = reportedSource;
          flowmeterReconnectWifi = reportedReconnect;

          // Only a CHANGE between two known ids is a restart. A first observation just
          // records it: re-asserting on first contact would let a lone hub reboot stomp
          // an aeration the flowmeter was legitimately running.
          rebootDetected = reportedBootId != 0 && flowmeterBootId != 0 &&
                           reportedBootId != flowmeterBootId;
          if (reportedBootId != 0) flowmeterBootId = reportedBootId;

          if (hasAck) flowCommandAck = reportedAck;
          if (hasAck && flowCommandAwaitingAck && reportedAck == flowCommandRevision) {
            flowCommandAwaitingAck = false;
            flowCommandAckAt = millis();
            pendingFlowmeterCommand = "";
            pendingMaxFlow = false;
            pendingReconnectWifi = false;
            pendingA1 = pendingB1 = false;
            pendingK1 = pendingF1 = pendingC1 = false;
            pendingK2 = pendingF2 = pendingC2 = false;
            ackedNow = true;
            ackedRevision = reportedAck;
          }

          if (rebootDetected) {
            // The node came back with its own defaults - setpoint zero, valves shut.
            // Adopting that would silently discard the operator's last command and
            // report the zero as if it had been asked for. Re-assert instead.
            flowCommandRevision++;
            if (flowCommandRevision == 0) flowCommandRevision = 1;
            flowCommandAwaitingAck = true;
            flowCommandDeliveryCount = 0;
            flowCommandQueuedAt = millis();
            pendingFlowmeterCommand = buildFlowCommandLocked();
            rebootRevision = flowCommandRevision;
          } else if (!flowCommandAwaitingAck) {
            desiredFlowSetpoint = flowmeterSetpoint;
            desiredFlowValve1 = flowmeterValve1;
            desiredFlowValve2 = flowmeterValve2;
            desiredFlowValveFlow = flowmeterValveFlow;
          }
          xSemaphoreGive(cmdMutex);
        }

        if (ackedNow) {
          ESP32_EVT(String("Flow command acknowledged cmd_id=") + ackedRevision);
        }
        if (rebootDetected) {
          ESP32_AVISO(String("Fluxometro reiniciou (boot_id=") + reportedBootId +
                      "); reenviando estado desejado cmd_id=" + rebootRevision);
        }
        request->send(200, "text/plain", "Flowmeter data received");
      } else {
        request->send(400, "text/plain", "Missing one or more flowmeter parameters");
      }
    });

    // Handler de GET /biomassData (recebe dados do sensor de biomassa)
    server.on("/biomassData", HTTP_GET, [](AsyncWebServerRequest *request) {
        // Presence is recorded before the routing gate, and the acknowledgement is taken
        // before it too. A node that is up and knocking is present whether or not the
        // operator has routing switched on, and refusing its ack would strand a command
        // that it has already applied.
        if (xSemaphoreTake(stateMutex, portMAX_DELAY) == pdTRUE) {
          biomassLastUpdate = millis();
          xSemaphoreGive(stateMutex);
        }
        ackReliable(biomassBox, readAckParam(request), "Biomass");

        // A v05 node marks its idle heartbeat. A v04 node never sends the flag, so it
        // is treated as a real sample - which is exactly the old behaviour.
        const bool idleBeat = request->hasParam("idle") &&
                              request->getParam("idle")->value().toInt() != 0;

        if (!biomassCommOn) {
            request->send(403, "text/plain", "Biomass comm disabled on hub");
            return;
        }
        if (!request->hasParam("absorbance") || !request->hasParam("raw")) {
            request->send(400, "text/plain", "Missing biomass parameters");
            return;
        }
        if (xSemaphoreTake(stateMutex, portMAX_DELAY) == pdTRUE) {
          biomassAbsorbance = request->getParam("absorbance")->value().toFloat();
          biomassRaw        = request->getParam("raw")->value().toInt();
          if (request->hasParam("it"))  biomassIt  = request->getParam("it")->value().toInt();
          if (request->hasParam("pwm")) biomassPwm = request->getParam("pwm")->value().toFloat();
          if (!idleBeat) biomassSampleLastUpdate = millis();
          xSemaphoreGive(stateMutex);
        }

        request->send(200, "text/plain", "Biomass data received");
    });

    // Handler de GET /pumpData (recebe dados da bomba peristáltica)
    server.on("/pumpData", HTTP_GET, [](AsyncWebServerRequest *request) {
      // Check for the minimal required parameters
      if (xSemaphoreTake(stateMutex, portMAX_DELAY) == pdTRUE) {
        pumpLastUpdate = millis();
        xSemaphoreGive(stateMutex);
      }
      ackReliable(pumpBox, readAckParam(request), "Pump");

      if (request->hasParam("mode") && request->hasParam("flow") && request->hasParam("vol") && request->hasParam("v_tgt")) {
        if (xSemaphoreTake(stateMutex, portMAX_DELAY) == pdTRUE) {
          pumpMode = request->getParam("mode")->value().toInt();
          if (request->hasParam("pwm")) pumpPwm = request->getParam("pwm")->value().toInt();
          if (request->hasParam("speed")) pumpSpeed = request->getParam("speed")->value().toFloat();
          pumpFlowRate = request->getParam("flow")->value().toFloat();
          pumpVolume = request->getParam("vol")->value().toFloat();
          pumpTargetVolume = request->getParam("v_tgt")->value().toFloat();
          if (request->hasParam("active")) pumpActive = request->getParam("active")->value().toInt() == 1;
          if (request->hasParam("waiting")) pumpWaiting = request->getParam("waiting")->value().toInt() == 1;
          xSemaphoreGive(stateMutex);
        }

        request->send(200, "text/plain", "Pump data received");
      } else {
        request->send(400, "text/plain", "Missing one or more pump parameters");
      }
    });

    // Handler de GET /agitatorData (recebe telemetria do frasco agitador)
    //
    // O nó já montava esta telemetria a cada 500 ms para a própria serial e para o seu
    // /read local; só não a empurrava para cá. Sem ela o app comandava no escuro, e em
    // particular não tinha como saber que o potenciômetro de bancada havia reassumido o
    // motor - o que faz um "desligar" comum não desligar.
    server.on("/agitatorData", HTTP_GET, [](AsyncWebServerRequest *request) {
      if (!request->hasParam("pct")) {
        request->send(400, "text/plain", "Missing 'pct' parameter");
        return;
      }

      if (xSemaphoreTake(stateMutex, portMAX_DELAY) == pdTRUE) {
        agitatorActualPercent = request->getParam("pct")->value().toFloat();
        if (request->hasParam("dir")) agitatorActualDir = request->getParam("dir")->value().toInt() != 0 ? 1 : 0;
        if (request->hasParam("pot")) agitatorPotActive = request->getParam("pot")->value().toInt() != 0;
        if (request->hasParam("src")) agitatorSource    = request->getParam("src")->value();
        agitatorLastUpdate = millis();
        xSemaphoreGive(stateMutex);
      }
      ackReliable(agitatorBox, readAckParam(request), "Agitator");

      request->send(200, "text/plain", "Agitator data received");
    });

    // Handler de GET /servoData (recebe telemetria do no RS-485 do ASDA-B2)
    server.on("/servoData", HTTP_GET, [](AsyncWebServerRequest *request) {
      // Protocolo 10 exige telemetria e recibo de controle no mesmo push. Isso
      // torna um driver antigo visivelmente incompatível em vez de simular ACK.
      if (!request->hasParam("rpm") || !request->hasParam("torque_pct") ||
          !request->hasParam("power_w") || !request->hasParam("state") ||
          !request->hasParam("control_capable") || !request->hasParam("motor_ack") ||
          !request->hasParam("motor_route_ack") ||
          !request->hasParam("motor_applied_rpm") ||
          !request->hasParam("motor_control_active") ||
          !request->hasParam("motor_control_fault")) {
        request->send(400, "text/plain", "Missing one or more servo parameters");
        return;
      }

      ServoSample sample = servoDevice.snapshot(millis()).sample;
      auto parseFloatParam = [&](const char *name, float &destination, bool required) {
        if (!request->hasParam(name)) return !required;
        return JsonUtils::parseFiniteFloat(request->getParam(name)->value(), destination);
      };
      auto parseIntParam = [&](const char *name, int32_t &destination, bool required) {
        if (!request->hasParam(name)) return !required;
        return JsonUtils::parseInt(request->getParam(name)->value(), destination);
      };
      auto parseUIntParam = [&](const char *name, uint32_t &destination) {
        if (!request->hasParam(name)) return true;
        return JsonUtils::parseUInt(request->getParam(name)->value(), destination);
      };
      auto parseBoolParam = [&](const char *name, bool &destination) {
        if (!request->hasParam(name)) return false;
        return JsonUtils::parseBool(request->getParam(name)->value(), destination);
      };
      int32_t motorAppliedRpm = sample.motorAppliedRpm;
      int32_t motorRouteAck = sample.motorRouteAck;

      if (!parseFloatParam("rpm", sample.rpm, true) ||
          !parseFloatParam("torque_pct", sample.torquePct, true) ||
          !parseFloatParam("power_w", sample.powerW, true) ||
          !parseIntParam("state", sample.state, true) || sample.state < 0 || sample.state > 3 ||
          !parseFloatParam("torque_nm", sample.torqueNm, false) ||
          !parseFloatParam("load_pct", sample.loadPct, false) ||
          !parseFloatParam("energy_wh", sample.energyWh, false) ||
          !parseIntParam("alarm", sample.alarm, false) ||
          !parseUIntParam("ok", sample.commOk) || !parseUIntParam("err", sample.commErr) ||
          !parseBoolParam("control_capable", sample.controlCapable) ||
          !parseUIntParam("motor_ack", sample.motorCommandAck) ||
          !parseIntParam("motor_route_ack", motorRouteAck, true) ||
          motorRouteAck < -1 || motorRouteAck > 1 ||
          !parseIntParam("motor_applied_rpm", motorAppliedRpm, true) ||
          motorAppliedRpm < 0 || motorAppliedRpm > ServoDevice::kMaxMotorRpm ||
          !parseBoolParam("motor_control_active", sample.motorControlActive) ||
          !parseIntParam("motor_control_fault", sample.motorControlFault, true) ||
          sample.motorControlFault < 0) {
        request->send(400, "text/plain", "Invalid servo parameter");
        return;
      }
      sample.motorAppliedRpm = static_cast<uint16_t>(motorAppliedRpm);
      sample.motorRouteAck = motorRouteAck;

      // A syntactically valid push always proves presence. ServoDevice only replaces
      // the published sample while routing is enabled.
      servoDevice.acceptValidPush(sample, millis());
      request->send(200, "text/plain", "Servo data received");
    });

    // Handlers 'Pull' (thread-safe)
    
    server.on("/flowCommand", HTTP_GET, [](AsyncWebServerRequest *request) {
      request->send(200, "application/json", getReliableFlowCommand());
    });

    server.on("/biomassCommand", HTTP_GET, [](AsyncWebServerRequest *request) {
      request->send(200, "application/json", takeReliable(biomassBox));
    });

    server.on("/agitatorHello", HTTP_GET, [](AsyncWebServerRequest *request) {
      IPAddress rip = request->client()->remoteIP();
      String msg = String("{\"hello\":\"agitator\",\"ip\":\"") + rip.toString() + "\"}";
      request->send(200, "application/json", msg);
    });

    server.on("/agitatorCommand", HTTP_GET, [](AsyncWebServerRequest *request) {
      request->send(200, "application/json", takeReliable(agitatorBox));
    });

    server.on("/pumpCommand", HTTP_GET, [](AsyncWebServerRequest *request) {
      request->send(200, "application/json", takeReliable(pumpBox));
    });

    server.on("/servoCommand", HTTP_GET, [](AsyncWebServerRequest *request) {
      request->send(200, "application/json", servoDevice.takeCommand(millis()));
    });

    server.begin();
  } else {
      ESP32_ERRO("Falha ao iniciar o SoftAP; reiniciando ESP32");
      ESP.restart();
    }
}

// ------------------------------------------------------------------
