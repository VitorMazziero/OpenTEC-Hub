namespace OpenTECHub.Services.KlaMapping;

/// <summary>
/// Tensor-product not-a-knot cubic spline over a uniform square grid. This is the
/// continuous stage corresponding to SciPy's zero-smoothing RectBivariateSpline.
/// </summary>
internal sealed class BicubicSurface
{
    private const int CoefficientsPerCell = 16;

    private readonly int _resolution;
    private readonly double _step;
    private readonly double[] _coefficients;

    private BicubicSurface(int resolution, double[] coefficients)
    {
        _resolution = resolution;
        _step = 1.0 / (resolution - 1);
        _coefficients = coefficients;
    }

    public static BicubicSurface Create(IReadOnlyList<double> values, int resolution)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(resolution, 4);
        if (values.Count != resolution * resolution)
        {
            throw new ArgumentException("The grid dimensions do not match its resolution.", nameof(values));
        }

        var intervals = resolution - 1;
        var step = 1.0 / intervals;

        // First interpolate each physical row in q. Four polynomial coefficients are
        // produced for each q interval and each n grid line.
        var qCoefficients = new double[resolution * intervals * 4];
        var line = new double[resolution];
        for (var row = 0; row < resolution; row++)
        {
            for (var column = 0; column < resolution; column++)
            {
                line[column] = values[(row * resolution) + column];
            }

            var rowSpline = UniformNotAKnot(line, step);
            Array.Copy(rowSpline, 0, qCoefficients, row * intervals * 4, rowSpline.Length);
        }

        // Spline each q-polynomial coefficient down n. Linearity makes this exactly the
        // tensor product: every cell receives four powers of q by four powers of n.
        var coefficients = new double[intervals * intervals * CoefficientsPerCell];
        var columnValues = new double[resolution];
        for (var qCell = 0; qCell < intervals; qCell++)
        {
            for (var qPower = 0; qPower < 4; qPower++)
            {
                for (var row = 0; row < resolution; row++)
                {
                    columnValues[row] = qCoefficients[
                        (row * intervals * 4) + (qCell * 4) + qPower];
                }

                var nSpline = UniformNotAKnot(columnValues, step);
                for (var nCell = 0; nCell < intervals; nCell++)
                {
                    var cellOffset = ((nCell * intervals) + qCell) * CoefficientsPerCell;
                    for (var nPower = 0; nPower < 4; nPower++)
                    {
                        coefficients[cellOffset + (nPower * 4) + qPower] =
                            nSpline[(nCell * 4) + nPower];
                    }
                }
            }
        }

        return new BicubicSurface(resolution, coefficients);
    }

    public KlaSurfaceValue Evaluate(double q, double n)
    {
        q = Math.Clamp(q, 0, 1);
        n = Math.Clamp(n, 0, 1);

        var qScaled = q * (_resolution - 1);
        var nScaled = n * (_resolution - 1);
        var qCell = Math.Min(_resolution - 2, (int)Math.Floor(qScaled));
        var nCell = Math.Min(_resolution - 2, (int)Math.Floor(nScaled));
        var dq = q - (qCell * _step);
        var dn = n - (nCell * _step);
        var offset = ((nCell * (_resolution - 1)) + qCell) * CoefficientsPerCell;

        var value = 0.0;
        var derivativeQ = 0.0;
        var derivativeN = 0.0;
        var nFactor = 1.0;
        for (var nPower = 0; nPower < 4; nPower++)
        {
            var qPolynomial = 0.0;
            var qDerivative = 0.0;
            var qFactor = 1.0;
            for (var qPower = 0; qPower < 4; qPower++)
            {
                var coefficient = _coefficients[offset + (nPower * 4) + qPower];
                qPolynomial += coefficient * qFactor;
                if (qPower > 0)
                {
                    qDerivative += qPower * coefficient * Math.Pow(dq, qPower - 1);
                }

                qFactor *= dq;
            }

            value += qPolynomial * nFactor;
            derivativeQ += qDerivative * nFactor;
            if (nPower > 0)
            {
                derivativeN += nPower * qPolynomial * Math.Pow(dn, nPower - 1);
            }

            nFactor *= dn;
        }

        return new KlaSurfaceValue(value, derivativeQ, derivativeN);
    }

    /// <summary>Returns [a,b,c,d] for each interval, evaluated as a+b·dx+c·dx²+d·dx³.</summary>
    private static double[] UniformNotAKnot(IReadOnlyList<double> values, double step)
    {
        var count = values.Count;
        var intervals = count - 1;
        var second = new double[count];

        // Uniform not-a-knot endpoints reduce the first and last interior equations to
        // direct values. The remaining system is tridiagonal with diagonal 4.
        var rhs = new double[count];
        var scale = 6.0 / (step * step);
        for (var index = 1; index < count - 1; index++)
        {
            rhs[index] = scale * (values[index + 1] - (2 * values[index]) + values[index - 1]);
        }

        second[1] = rhs[1] / 6.0;
        second[count - 2] = rhs[count - 2] / 6.0;

        var interiorCount = count - 4;
        if (interiorCount > 0)
        {
            var lower = new double[interiorCount];
            var diagonal = new double[interiorCount];
            var upper = new double[interiorCount];
            var reduced = new double[interiorCount];
            for (var item = 0; item < interiorCount; item++)
            {
                lower[item] = item == 0 ? 0 : 1;
                diagonal[item] = 4;
                upper[item] = item == interiorCount - 1 ? 0 : 1;
                var originalIndex = item + 2;
                reduced[item] = rhs[originalIndex];
            }

            reduced[0] -= second[1];
            reduced[^1] -= second[count - 2];
            SolveTridiagonal(lower, diagonal, upper, reduced);
            for (var item = 0; item < interiorCount; item++)
            {
                second[item + 2] = reduced[item];
            }
        }

        second[0] = (2 * second[1]) - second[2];
        second[^1] = (2 * second[^2]) - second[^3];

        var coefficients = new double[intervals * 4];
        for (var index = 0; index < intervals; index++)
        {
            coefficients[(index * 4) + 0] = values[index];
            coefficients[(index * 4) + 1] =
                ((values[index + 1] - values[index]) / step) -
                (step * ((2 * second[index]) + second[index + 1]) / 6.0);
            coefficients[(index * 4) + 2] = second[index] / 2.0;
            coefficients[(index * 4) + 3] =
                (second[index + 1] - second[index]) / (6.0 * step);
        }

        return coefficients;
    }

    private static void SolveTridiagonal(
        double[] lower,
        double[] diagonal,
        double[] upper,
        double[] rightHandSide)
    {
        for (var index = 1; index < diagonal.Length; index++)
        {
            var factor = lower[index] / diagonal[index - 1];
            diagonal[index] -= factor * upper[index - 1];
            rightHandSide[index] -= factor * rightHandSide[index - 1];
        }

        rightHandSide[^1] /= diagonal[^1];
        for (var index = diagonal.Length - 2; index >= 0; index--)
        {
            rightHandSide[index] =
                (rightHandSide[index] - (upper[index] * rightHandSide[index + 1])) /
                diagonal[index];
        }
    }
}
