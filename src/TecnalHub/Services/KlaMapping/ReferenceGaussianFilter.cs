namespace TecnalHub.Services.KlaMapping;

/// <summary>Separable Gaussian filter using SciPy's default radius and reflect boundary.</summary>
internal static class ReferenceGaussianFilter
{
    public static double[] Apply(IReadOnlyList<double> values, int resolution, double sigma)
    {
        if (values.Count != resolution * resolution)
        {
            throw new ArgumentException("The grid dimensions do not match its resolution.", nameof(values));
        }

        if (!(sigma > 0))
        {
            return values.ToArray();
        }

        var radius = (int)((4.0 * sigma) + 0.5);
        var kernel = new double[(2 * radius) + 1];
        var total = 0.0;
        for (var offset = -radius; offset <= radius; offset++)
        {
            var weight = Math.Exp(-(offset * offset) / (2 * sigma * sigma));
            kernel[offset + radius] = weight;
            total += weight;
        }

        for (var index = 0; index < kernel.Length; index++)
        {
            kernel[index] /= total;
        }

        var horizontal = new double[values.Count];
        var output = new double[values.Count];
        for (var row = 0; row < resolution; row++)
        {
            for (var column = 0; column < resolution; column++)
            {
                var sum = 0.0;
                for (var kernelIndex = 0; kernelIndex < kernel.Length; kernelIndex++)
                {
                    var sourceColumn = Reflect(column + kernelIndex - radius, resolution);
                    sum += values[(row * resolution) + sourceColumn] * kernel[kernelIndex];
                }

                horizontal[(row * resolution) + column] = sum;
            }
        }

        for (var row = 0; row < resolution; row++)
        {
            for (var column = 0; column < resolution; column++)
            {
                var sum = 0.0;
                for (var kernelIndex = 0; kernelIndex < kernel.Length; kernelIndex++)
                {
                    var sourceRow = Reflect(row + kernelIndex - radius, resolution);
                    sum += horizontal[(sourceRow * resolution) + column] * kernel[kernelIndex];
                }

                output[(row * resolution) + column] = sum;
            }
        }

        return output;
    }

    private static int Reflect(int index, int length)
    {
        while (index < 0 || index >= length)
        {
            index = index < 0 ? -index - 1 : (2 * length) - index - 1;
        }

        return index;
    }
}
