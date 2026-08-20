namespace TecnalHub.Services.Calibration;

/// <summary>One point whose raw instrument reading maps to an engineering reference.</summary>
public readonly record struct LinearCalibrationPoint(double Raw, double Reference);

/// <summary>Result of <c>engineering = Slope * raw + Intercept</c>.</summary>
public readonly record struct LinearCalibration(double Slope, double Intercept);

/// <summary>Polynomial <c>y = K*x^2 + F*x + C</c>.</summary>
public readonly record struct PolynomialCalibration(double K, double F, double C)
{
    public double Evaluate(double x) => (K * x * x) + (F * x) + C;
}

/// <summary>The two v.6 flowmeter curve segments.</summary>
public sealed record FlowCalibrationCurve(
    PolynomialCalibration? LowVoltage,
    PolynomialCalibration? HighVoltage)
{
    public const double SplitVoltage = 0.0545;

    public bool HasAny => LowVoltage is not null || HighVoltage is not null;

    public bool IsComplete => LowVoltage is not null && HighVoltage is not null;
}

/// <summary>
/// Pure calibration calculations shared by the UI and tests.
/// </summary>
public static class CalibrationMath
{
    private const double Epsilon = 1e-12;

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
    /// Reproduces v.6's split and polynomial degrees: at least three low-voltage
    /// points for a quadratic; two high-voltage points for a line, three for a
    /// quadratic.
    /// </summary>
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

        PolynomialCalibration? lowCurve = low.Length >= 3
            ? FitPolynomial(low, degree: 2)
            : null;

        PolynomialCalibration? highCurve = high.Length switch
        {
            >= 3 => FitPolynomial(high, degree: 2),
            2 => FitPolynomial(high, degree: 1),
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

        // The solved basis is [C, F, K]. A linear high segment explicitly carries
        // K=0, just as v.6 does before sending k2/f2/c2.
        var c = augmented[0, n];
        var f = augmented[1, n];
        var k = degree == 2 ? augmented[2, n] : 0.0;

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
