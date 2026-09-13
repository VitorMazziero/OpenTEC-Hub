void onWsEvent(AsyncWebSocket *server, AsyncWebSocketClient *client, AwsEventType type, void *arg, uint8_t *data, size_t len) {
  if (type == WS_EVT_CONNECT) {
    Serial.printf("WS Client #%u connected\n", client->id());
  } else if (type == WS_EVT_DISCONNECT) {
    Serial.printf("WS Client #%u disconnected\n", client->id());
  } else if (type == WS_EVT_DATA) {
    AwsFrameInfo *info = (AwsFrameInfo*)arg;
    if (info->final && info->index == 0 && info->len == len) {
      if (info->opcode == WS_TEXT) {
        String message((char*)data, len);
        bool accepted = processReceivedData(message, COMMAND_DIRECT);
        float snapTarget;
        uint8_t snapValve1, snapValve2, snapValveFlow;
        uint32_t snapDirectAck;
        uint32_t snapDirectSession;
        unsigned long snapApplyMs;
        xSemaphoreTake(commandMutex, portMAX_DELAY);
        snapTarget = targetFlowSetpoint;
        snapValve1 = valve1State;
        snapValve2 = valve2State;
        snapValveFlow = valveFlowState;
        snapDirectAck = lastAppliedDirectCommandId;
        snapDirectSession = lastAppliedDirectSessionId;
        snapApplyMs = lastCommandApplyMs;
        xSemaphoreGive(commandMutex);

        char ackMessage[320];
        snprintf(ackMessage, sizeof(ackMessage),
                 "{\"command_ack\":%s,\"ack_direct_session_id\":%lu"
                 ",\"ack_direct_cmd_id\":%lu"
                 ",\"last_apply_ms\":%lu,\"command_source\":\"direct\""
                 ",\"flow_setpoint\":%.6f,\"valve1State\":%d"
                 ",\"valve2State\":%d,\"valveFlowState\":%d"
                 ",\"cal_crc\":\"%08X\"}",
                 accepted ? "true" : "false", (unsigned long)snapDirectSession,
                 (unsigned long)snapDirectAck,
                 snapApplyMs, snapTarget, snapValve1, snapValve2, snapValveFlow,
                 currentCalCrc);
        client->text(ackMessage);
      }
    }
  }
}


