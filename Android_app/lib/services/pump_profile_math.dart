import 'dart:math' as math;
import '../models/peristaltic_pump_state.dart';

/// Defines the operating profile parameters for the external peristaltic pump.
class PumpProfileSpec {
  final PeristalticPumpMode mode;
  final double initMinutes;
  final double finalMinutes;
  final double lambda;
  final double phi;
  final List<double> polynomialCoefficients;
  final List<double> piecewiseTimes;
  final List<double> piecewiseFlows;

  const PumpProfileSpec({
    required this.mode,
    required this.initMinutes,
    required this.finalMinutes,
    this.lambda = 1.0,
    this.phi = 0.0,
    this.polynomialCoefficients = const [],
    this.piecewiseTimes = const [],
    this.piecewiseFlows = const [],
  });

  /// Factory for constant flow profile: Q(t') = lambda
  factory PumpProfileSpec.constant({
    required double initMinutes,
    required double finalMinutes,
    required double lambda,
  }) {
    return PumpProfileSpec(
      mode: PeristalticPumpMode.constant,
      initMinutes: initMinutes,
      finalMinutes: finalMinutes,
      lambda: lambda,
    );
  }

  /// Factory for linear flow profile: Q(t') = lambda + phi * t'
  factory PumpProfileSpec.linear({
    required double initMinutes,
    required double finalMinutes,
    required double lambda,
    required double phi,
  }) {
    return PumpProfileSpec(
      mode: PeristalticPumpMode.linear,
      initMinutes: initMinutes,
      finalMinutes: finalMinutes,
      lambda: lambda,
      phi: phi,
    );
  }

  /// Factory for exponential flow profile: Q(t') = lambda * exp(phi * t')
  factory PumpProfileSpec.exponential({
    required double initMinutes,
    required double finalMinutes,
    required double lambda,
    required double phi,
  }) {
    return PumpProfileSpec(
      mode: PeristalticPumpMode.exponential,
      initMinutes: initMinutes,
      finalMinutes: finalMinutes,
      lambda: lambda,
      phi: phi,
    );
  }

  /// Factory for polynomial flow profile: Q(t') = p0 + p1*t' + p2*(t')^2 + ...
  factory PumpProfileSpec.polynomial({
    required double initMinutes,
    required double finalMinutes,
    required List<double> coefficients,
  }) {
    return PumpProfileSpec(
      mode: PeristalticPumpMode.polynomial,
      initMinutes: initMinutes,
      finalMinutes: finalMinutes,
      polynomialCoefficients: coefficients,
    );
  }

  /// Factory for piecewise linear flow profile
  factory PumpProfileSpec.piecewise({
    required double initMinutes,
    required double finalMinutes,
    required List<double> times,
    required List<double> flows,
  }) {
    return PumpProfileSpec(
      mode: PeristalticPumpMode.piecewise,
      initMinutes: initMinutes,
      finalMinutes: finalMinutes,
      piecewiseTimes: times,
      piecewiseFlows: flows,
    );
  }
}

/// Simulated flow and integrated volume preview calculated client-side.
class PumpPreview {
  final double peakFlow; // mL/min
  final double totalVolume; // mL
  final double averageFlow; // mL/min
  final List<double> minutes;
  final List<double> flows;

  const PumpPreview({
    required this.peakFlow,
    required this.totalVolume,
    required this.averageFlow,
    required this.minutes,
    required this.flows,
  });
}

/// Pure mathematical profile simulation and numerical integration engine.
class PumpProfileMath {
  /// Evaluates the flow in mL/min at absolute time [tMinutes].
  ///
  /// Flow is zero before [spec.initMinutes], and clamped to >= 0.0.
  static double flowAt(PumpProfileSpec spec, double tMinutes) {
    if (tMinutes < spec.initMinutes || tMinutes > spec.finalMinutes) {
      return 0.0;
    }

    final t = tMinutes - spec.initMinutes; // relative time t'

    double q = 0.0;
    switch (spec.mode) {
      case PeristalticPumpMode.constant:
        q = spec.lambda;
        break;
      case PeristalticPumpMode.linear:
        q = spec.lambda + (spec.phi * t);
        break;
      case PeristalticPumpMode.exponential:
        q = spec.lambda * math.exp(spec.phi * t);
        break;
      case PeristalticPumpMode.polynomial:
        q = _horner(spec.polynomialCoefficients, t);
        break;
      case PeristalticPumpMode.piecewise:
        q = _interpolate(spec.piecewiseTimes, spec.piecewiseFlows, t);
        break;
      case PeristalticPumpMode.stop:
        q = 0.0;
        break;
    }

    if (!q.isFinite || q < 0.0) return 0.0;
    return q;
  }

  /// Samples the profile over [0, finalMinutes] and computes trapezoidal volume.
  static PumpPreview sample(PumpProfileSpec spec, {int count = 100}) {
    count = math.max(count, 2);
    final horizon = spec.finalMinutes > 0 ? spec.finalMinutes : math.max(spec.initMinutes, 1.0);

    final List<double> minutes = [];
    final List<double> flows = [];
    double peak = 0.0;
    double volume = 0.0;

    for (int i = 0; i < count; i++) {
      final t = horizon * i / (count - 1);
      final q = flowAt(spec, t);

      minutes.add(t);
      flows.add(q);
      if (q > peak) peak = q;

      if (i > 0) {
        final dt = minutes[i] - minutes[i - 1];
        volume += 0.5 * (flows[i] + flows[i - 1]) * dt;
      }
    }

    final duration = math.max(0.001, spec.finalMinutes - spec.initMinutes);
    final avgFlow = volume / duration;

    return PumpPreview(
      peakFlow: peak,
      totalVolume: volume,
      averageFlow: avgFlow,
      minutes: minutes,
      flows: flows,
    );
  }

  /// Validates profile parameters and returns an error message if invalid.
  static String? validateSpec(PumpProfileSpec spec) {
    if (spec.initMinutes < 0) {
      return "O início não pode ser negativo.";
    }
    if (spec.finalMinutes <= spec.initMinutes) {
      return "O fim deve ser maior que o início.";
    }

    switch (spec.mode) {
      case PeristalticPumpMode.constant:
        if (spec.lambda < 0) return "A vazão não pode ser negativa.";
        break;
      case PeristalticPumpMode.linear:
        if (spec.lambda < 0) return "A vazão inicial não pode ser negativa.";
        break;
      case PeristalticPumpMode.exponential:
        if (spec.lambda < 0) return "A vazão inicial não pode ser negativa.";
        break;
      case PeristalticPumpMode.polynomial:
        if (spec.polynomialCoefficients.isEmpty) {
          return "Informe ao menos um coeficiente.";
        }
        if (spec.polynomialCoefficients.length > 21) {
          return "No máximo 21 coeficientes (p0 a p20).";
        }
        break;
      case PeristalticPumpMode.piecewise:
        if (spec.piecewiseTimes.length < 2) {
          return "Informe ao menos 2 pontos (t, Q).";
        }
        if (spec.piecewiseTimes.length != spec.piecewiseFlows.length) {
          return "Tempos e vazões precisam ter o mesmo número de pontos.";
        }
        if (spec.piecewiseTimes[0] != 0.0) {
          return "O primeiro tempo (t0) deve ser 0.";
        }
        for (int i = 1; i < spec.piecewiseTimes.length; i++) {
          if (spec.piecewiseTimes[i] <= spec.piecewiseTimes[i - 1]) {
            return "Os tempos devem ser crescentes.";
          }
        }
        for (final flow in spec.piecewiseFlows) {
          if (flow < 0) return "As vazões não podem ser negativas.";
        }
        break;
      case PeristalticPumpMode.stop:
        break;
    }

    return null;
  }

  /// Horner's method for efficient polynomial evaluation
  static double _horner(List<double> coefficients, double t) {
    if (coefficients.isEmpty) return 0.0;
    double acc = coefficients.last;
    for (int i = coefficients.length - 2; i >= 0; i--) {
      acc = (acc * t) + coefficients[i];
    }
    return acc;
  }

  /// Linear interpolation between segments
  static double _interpolate(List<double> times, List<double> flows, double t) {
    if (times.isEmpty || flows.isEmpty || times.length != flows.length) {
      return 0.0;
    }

    // Clamp to endpoints outside sampled range
    if (t <= times.first) return flows.first;
    if (t >= times.last) return flows.last;

    for (int i = 1; i < times.length; i++) {
      if (t <= times[i]) {
        final span = times[i] - times[i - 1];
        if (span <= 0) return flows[i];
        final fraction = (t - times[i - 1]) / span;
        return flows[i - 1] + (fraction * (flows[i] - flows[i - 1]));
      }
    }

    return flows.last;
  }
}
