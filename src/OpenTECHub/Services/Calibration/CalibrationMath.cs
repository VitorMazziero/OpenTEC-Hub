namespace OpenTECHub.Services.Calibration;

/// <summary>One point whose raw instrument reading maps to an engineering reference.</summary>
public readonly record struct LinearCalibrationPoint(double Raw, double Reference);

/// <summary>Result of <c>engineering = Slope * raw + Intercept</c>.</summary>
public readonly record struct LinearCalibration(double Slope, double Intercept);

/// <summary>
/// Polynomial <c>y = A*x^4 + B*x^3 + K*x^2 + F*x + C</c>.
/// </summary>
/// <remarks>
/// <see cref="A"/> and <see cref="B"/> are zero for the quadratic high segment, which keeps the
/// positional <c>(K, F, C)</c> shape the high curve and the firmware's <c>k2/f2/c2</c> keys use.
/// The low segment is quartic (<c>a1/b1/k1/f1/c1</c>) so it can meet the high segment at the
/// split with both value and slope — see <c>flowmeter_OpenTECHUB_V05.ino</c>.
/// </remarks>
public readonly record struct PolynomialCalibration(double K, double F, double C)
{
    /// <summary>x⁴ coefficient (<c>a1</c>). Zero for a quadratic.</summary>
    public double A { get; init; }

    /// <summary>x³ coefficient (<c>b1</c>). Zero for a quadratic.</summary>
    public double B { get; init; }

    /// <summary>True when the quartic terms are in use.</summary>
    public bool IsQuartic => A != 0.0 || B != 0.0;

    /// <summary>Horner evaluation, matching the firmware's nested form exactly.</summary>
    public double Evaluate(double x) => ((((A * x) + B) * x + K) * x + F) * x + C;

    /// <summary>Analytic slope <c>dy/dx</c>, used to match the segments at the split.</summary>
    public double Derivative(double x) => (((4.0 * A * x) + (3.0 * B)) * x + (2.0 * K)) * x + F;
}

/// <summary>The two flowmeter curve segments, split at <see cref="SplitVoltage"/>.</summary>
public sealed record FlowCalibrationCurve(
    PolynomialCalibration? LowVoltage,
    PolynomialCalibration? HighVoltage)
{
    public const double SplitVoltage = 0.0545;

    public bool HasAny => LowVoltage is not null || HighVoltage is not null;

    public bool IsComplete => LowVoltage is not null && HighVoltage is not null;

    /// <summary>The flow the curve reports at a voltage, picking the segment the firmware would.</summary>
    public double? Evaluate(double voltage)
    {
        var segment = voltage <= SplitVoltage ? LowVoltage : HighVoltage;
        return segment?.Evaluate(voltage);
    }

    /// <summary>The jump at the split, or null when a segment is missing.</summary>
    public double? DiscontinuityAtSplit =>
        LowVoltage is { } low && HighVoltage is { } high
            ? high.Evaluate(SplitVoltage) - low.Evaluate(SplitVoltage)
            : null;
}

/// <summary>
/// Pure calibration calculations shared by the UI and tests.
/// </summary>
public static class CalibrationMath
{
    private const double Epsilon = 1e-12;

    /// <summary>
    /// The calibration shipped in <c>flowmeter_OpenTECHUB_V05.ino</c>: a quartic low segment
    /// anchored to the quadratic high segment at the split.
    /// </summary>
    /// <remarks>
    /// The low curve passes through the certified 0, 0.5 and 0.75 L/min points and meets the
    /// preserved high curve at 0.0545 V in both value and slope, so the ~0.17 L/min jump the
    /// older quadratic pair produced is gone. The firmware loads these on an EEPROM schema
    /// bump; the app keeps them as the reference to restore and to compare a new fit against.
    /// </remarks>
    public static FlowCalibrationCurve FirmwareDefault { get; } = new(
        new PolynomialCalibration(462.893536740, 43.294432104, -0.464367483)
        {
            A = 321791.345936369,
            B = -32589.073104291,
        },
        new PolynomialCalibration(-0.854551899, 11.814453070, 0.192231954));

    /// <summary>
    /// Fits the low segment so it meets <paramref name="highCurve"/> at the split with the same
    /// value and the same slope — the two mathematical anchors the V05 firmware curve uses.
    /// </summary>
    /// <remarks>
    /// Writing the low curve as
    /// <c>P(x) = H(xs) + H'(xs)(x - xs) + (x - xs)² R(x)</c> satisfies both anchors by
    /// construction for any <c>R</c>, so only <c>R</c> has to be fitted. With three low points
    /// <c>R</c> is a quadratic and the fit is exact — reproducing the derivation that produced
    /// the shipped coefficients; with fewer points <c>R</c> drops degree and the curve stays
    /// continuous rather than refusing to fit at all.
    /// </remarks>
    public static PolynomialCalibration FitLowSegmentContinuous(
        IReadOnlyList<(double Voltage, double Flow)> lowPoints,
        PolynomialCalibration highCurve)
    {
        ArgumentNullException.ThrowIfNull(lowPoints);

        var split = FlowCalibrationCurve.SplitVoltage;
        var valueAtSplit = highCurve.Evaluate(split);
        var slopeAtSplit = highCurve.Derivative(split);

        // Transform each point into the residual space of R; points sitting on the split carry
        // no information about R (the (x - xs)² factor annihilates it) and are dropped.
        var residuals = new List<(double Voltage, double Flow)>();
        foreach (var (voltage, flow) in lowPoints)
        {
            var offset = voltage - split;
            if (!double.IsFinite(voltage) || !double.IsFinite(flow) || Math.Abs(offset) <= 1e-9)
            {
                continue;
            }

            var anchored = valueAtSplit + (slopeAtSplit * offset);
            residuals.Add((voltage, (flow - anchored) / (offset * offset)));
        }

        if (residuals.Count == 0)
        {
            throw new InvalidOperationException(
                "Não há pontos abaixo do limiar para ajustar o segmento inferior.");
        }

        // R's degree follows the evidence: three or more points determine a quadratic exactly.
        var degree = residuals.Count >= 3 ? 2 : residuals.Count - 1;
        var r = FitPolynomial(residuals, degree);

        // Expand P(x) = (x - xs)²(Rk x² + Rf x + Rc) + H'(xs)(x - xs) + H(xs).
        var (rk, rf, rc) = (r.K, r.F, r.C);
        return new PolynomialCalibration(
            K: rc - (2.0 * split * rf) + (split * split * rk),
            F: (-2.0 * split * rc) + (split * split * rf) + slopeAtSplit,
            C: (split * split * rc) - (slopeAtSplit * split) + valueAtSplit)
        {
            A = rk,
            B = rf - (2.0 * split * rk),
        };
    }

    /// <summary>Fits the exact two-point linear equation used by pH and oxygen.</summary>
    public static LinearCalibration FitLinear(
        LinearCalibrationPoint first,
        LinearCalibrationPoint second)
    {
        RequireFinite(first.Raw, nameof(first));
        RequireFinite(first.Reference, nameof(first));
        RequireFinite(second.Raw, nameof(second));
        RequireFinite(second.Reference, nameof(second));

        var rawDelta = second.Raw - first.Raw;
        if (Math.Abs(rawDelta) <= Epsilon)
        {
            throw new InvalidOperationException(
                "Os dois pontos têm a mesma leitura bruta; a inclinação não pode ser calculada.");
        }

        var slope = (second.Reference - first.Reference) / rawDelta;
        var intercept = first.Reference - (slope * first.Raw);
        RequireFinite(slope, nameof(slope));
        RequireFinite(intercept, nameof(intercept));
        return new LinearCalibration(slope, intercept);
    }

    /// <summary>Sample standard deviation, matching Python's statistics.stdev.</summary>
    public static double SampleStandardDeviation(IReadOnlyList<double> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (values.Count < 2)
        {
            throw new ArgumentException("São necessárias ao menos duas amostras.", nameof(values));
        }

        var mean = values.Average();
        var sum = 0.0;
        foreach (var value in values)
        {
            RequireFinite(value, nameof(values));
            var delta = value - mean;
            sum += delta * delta;
        }

        return Math.Sqrt(sum / (values.Count - 1));
    }

    /// <summary>
    /// Fits both segments: a quadratic above the split (two points for a line, three for a
    /// quadratic) and, below it, a quartic anchored to that curve so the pair is continuous
    /// and smooth at 0.0545 V.
    /// </summary>
    /// <remarks>
    /// The low segment can only be anchored once the high segment exists, because the anchors
    /// are the high curve's value and slope at the split. Without a high curve the low points
    /// fall back to a free quadratic, which still needs three points.
    /// </remarks>
    public static FlowCalibrationCurve FitFlowCurve(
        IEnumerable<(double Voltage, double Flow)> points)
    {
        ArgumentNullException.ThrowIfNull(points);

        var finite = points
            .Where(point => double.IsFinite(point.Voltage) && double.IsFinite(point.Flow))
            .ToArray();

        var low = finite.Where(point => point.Voltage <= FlowCalibrationCurve.SplitVoltage)
                        .ToArray();
        var high = finite.Where(point => point.Voltage > FlowCalibrationCurve.SplitVoltage)
                         .ToArray();

        PolynomialCalibration? highCurve = high.Length switch
        {
            >= 3 => FitPolynomial(high, degree: 2),
            2 => FitPolynomial(high, degree: 1),
            _ => null,
        };

        PolynomialCalibration? lowCurve = (low.Length, highCurve) switch
        {
            ( > 0, { } anchor) => FitLowSegmentContinuous(low, anchor),
            ( >= 3, null) => FitPolynomial(low, degree: 2),
            _ => null,
        };

        return new FlowCalibrationCurve(lowCurve, highCurve);
    }

    private static PolynomialCalibration FitPolynomial(
        IReadOnlyList<(double Voltage, double Flow)> points,
        int degree)
    {
        // Normal equations for the least-squares fit. The system is only 2x2 or 3x3,
        // so a pivoted Gaussian elimination is both transparent and sufficient here.
        var n = degree + 1;
        var augmented = new double[n, n + 1];

        for (var row = 0; row < n; row++)
        {
            for (var column = 0; column < n; column++)
            {
                augmented[row, column] = points.Sum(point =>
                    Math.Pow(point.Voltage, row + column));
            }

            augmented[row, n] = points.Sum(point =>
                point.Flow * Math.Pow(point.Voltage, row));
        }

        for (var pivot = 0; pivot < n; pivot++)
        {
            var best = pivot;
            for (var row = pivot + 1; row < n; row++)
            {
                if (Math.Abs(augmented[row, pivot]) > Math.Abs(augmented[best, pivot]))
                {
                    best = row;
                }
            }

            if (Math.Abs(augmented[best, pivot]) <= Epsilon)
            {
                throw new InvalidOperationException(
                    "Os pontos de vazão não têm tensões distintas suficientes para ajustar a curva.");
            }

            if (best != pivot)
            {
                for (var column = pivot; column <= n; column++)
                {
                    (augmented[pivot, column], augmented[best, column]) =
                        (augmented[best, column], augmented[pivot, column]);
                }
            }

            var divisor = augmented[pivot, pivot];
            for (var column = pivot; column <= n; column++)
            {
                augmented[pivot, column] /= divisor;
            }

            for (var row = 0; row < n; row++)
            {
                if (row == pivot)
                {
                    continue;
                }

                var factor = augmented[row, pivot];
                for (var column = pivot; column <= n; column++)
                {
                    augmented[row, column] -= factor * augmented[pivot, column];
                }
            }
        }

        // The solved basis is [C, F, K]. Lower degrees explicitly carry zero in the
        // terms they do not use, so the caller always gets a well-formed polynomial.
        var c = augmented[0, n];
        var f = degree >= 1 ? augmented[1, n] : 0.0;
        var k = degree >= 2 ? augmented[2, n] : 0.0;

        RequireFinite(k, nameof(k));
        RequireFinite(f, nameof(f));
        RequireFinite(c, nameof(c));
        return new PolynomialCalibration(k, f, c);
    }

    private static void RequireFinite(double value, string name)
    {
        if (!double.IsFinite(value))
        {
            throw new ArgumentOutOfRangeException(name, "O valor deve ser finito.");
        }
    }
}
