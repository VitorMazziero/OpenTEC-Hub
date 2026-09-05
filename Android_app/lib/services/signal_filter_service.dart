import '../constants/api_constants.dart';

/// Handles ADC-to-engineering-unit conversions and signal conditioning
/// for pH and Dissolved Oxygen sensors.
class SignalFilterService {
  double phSlope = ApiConstants.defaultPhSlope;
  double phIntercept = ApiConstants.defaultPhIntercept;
  double oxyA = ApiConstants.defaultOxyA;
  double oxyB = ApiConstants.defaultOxyB;

  // Last calibrated pH sent to hub as {"pHCal": "X.XX"}
  String _lastSentPhCal = "";

  /// Calibrates raw ADC count to pH units (0.00 to 14.00)
  double rawToPh(double rawAdc) {
    if (rawAdc <= 0.1) return -1.0;
    double calculated = (phSlope * rawAdc) + phIntercept;
    return calculated.clamp(0.0, 14.0);
  }

  /// Calibrates raw ADC count to Dissolved Oxygen (mg/L)
  double rawToOxygen(double rawAdc) {
    if (rawAdc <= 0.1) return -1.0;
    double calculated = (oxyA * rawAdc) + oxyB;
    return calculated < 0.0 ? 0.0 : calculated;
  }

  /// Formats the calibrated pH with 2 decimal places using invariant dot notation
  /// for the Hub command {"pHCal": "X.XX"}.
  /// Returns null if unchanged from last dispatched value.
  String? getPhCalEchoIfChanged(double calibratedPh) {
    if (calibratedPh < 0.0 || calibratedPh > 14.0) return null;
    String formatted = calibratedPh.toStringAsFixed(2);
    if (formatted != _lastSentPhCal) {
      _lastSentPhCal = formatted;
      return formatted;
    }
    return null;
  }

  void resetEchoTracking() {
    _lastSentPhCal = "";
  }
}
