class ApiConstants {
  static const String defaultIp = "192.168.4.1";
  static const int defaultPort = 80;

  // Endpoints
  static const String pingEndpoint = "/ping";
  static const String readDataEndpoint = "/readData";
  static const String commandEndpoint = "/command";

  // Timing
  static const Duration defaultPollInterval = Duration(milliseconds: 1000);
  static const Duration pingTimeout = Duration(milliseconds: 1500);
  static const Duration readTimeout = Duration(milliseconds: 2000);
  static const Duration commandTimeout = Duration(milliseconds: 2000);

  // Buffer and Graphing limits
  static const int maxTelemetryHistoryPoints = 300;

  // Servo limits
  static const int minMotorRpm = 0;
  static const int maxMotorRpm = 1000;
  static const int defaultServoPollMs = 1000;

  // Calibration Defaults (as specified in Protocol v9/v10 & OpenTEC-Hub)
  static const double defaultPhSlope = 0.0005012405704;
  static const double defaultPhIntercept = -0.600385955239;
  static const double defaultOxyA = 0.0305473419314;
  static const double defaultOxyB = -25.09136520919;
}
