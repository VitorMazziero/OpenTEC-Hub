using System;
using System.Collections.Generic;
using System.Linq;

namespace OpenTECHub.Services.KlaTesting;

internal static class KlaNumerics
{
    internal static KlaRegression? Ols(IReadOnlyList<double> t, IReadOnlyList<double> y)
    {
        if (t.Count != y.Count || t.Count < 3 || t.Any(x => !double.IsFinite(x)) || y.Any(x => !double.IsFinite(x)))
        {
            return null;
        }
        var mt = t.Average();
        var my = y.Average();
        double xx = 0, xy = 0, yy = 0;
        for (var i = 0; i < t.Count; i++)
        {
            var dx = t[i] - mt;
            var dy = y[i] - my;
            xx += dx * dx; xy += dx * dy; yy += dy * dy;
        }
        if (xx <= 0 || yy <= 0)
        {
            return null;
        }
        var slope = xy / xx;
        // Evaluate in centered coordinates to avoid cancellation when the origin is shifted.
        var residuals = t.Select((x, i) => y[i] - my - slope * (x - mt)).ToArray();
        var sse = residuals.Sum(x => x * x);
        var variance = sse / (t.Count - 2);
        return new(slope, my - slope * mt, 1 - sse / yy,
            Math.Sqrt(variance / xx), Math.Sqrt(variance), Lag1(residuals));
    }

    internal static double Lag1(double[] residuals)
    {
        // Exact/near-exact analytic curves have numerical residuals, not physical correlation.
        if (residuals.Length < 4 || residuals.Sum(x => x * x) < 1e-18)
        {
            return 0;
        }
        var a = residuals.Take(residuals.Length - 1).ToArray();
        var b = residuals.Skip(1).ToArray();
        var ma = a.Average(); var mb = b.Average();
        double aa = 0, bb = 0, ab = 0;
        for (var i = 0; i < a.Length; i++)
        {
            aa += (a[i] - ma) * (a[i] - ma);
            bb += (b[i] - mb) * (b[i] - mb);
            ab += (a[i] - ma) * (b[i] - mb);
        }
        return aa * bb <= 0 ? 0 : Math.Clamp(ab / Math.Sqrt(aa * bb), -0.99, 0.99);
    }

    internal static double Student95(int degrees) => degrees switch
    {
        <= 1 => 12.706204736, 2 => 4.30265273, 3 => 3.182446305,
        4 => 2.776445105, 5 => 2.570581836, 6 => 2.446911851,
        7 => 2.364624252, 8 => 2.306004135, 9 => 2.262157163, 10 => 2.228138852,
        _ => StudentExpansion(degrees),
    };

    private static double StudentExpansion(int n)
    {
        const double z = 1.959963984540054;
        return z + (Math.Pow(z, 3) + z) / (4 * n)
            + (5 * Math.Pow(z, 5) + 16 * Math.Pow(z, 3) + 3 * z) / (96 * n * n)
            + (3 * Math.Pow(z, 7) + 19 * Math.Pow(z, 5) + 17 * Math.Pow(z, 3) - 15 * z) / (384 * n * n * n);
    }

    internal static double? Inverse00(double[,] matrix)
    {
        var a = new double[3, 6];
        for (var i = 0; i < 3; i++)
        {
            for (var j = 0; j < 3; j++)
            {
                a[i, j] = matrix[i, j];
            }
            a[i, i + 3] = 1;
        }
        for (var col = 0; col < 3; col++)
        {
            var pivot = col;
            for (var row = col + 1; row < 3; row++)
            {
                if (Math.Abs(a[row, col]) > Math.Abs(a[pivot, col]))
                {
                    pivot = row;
                }
            }
            if (Math.Abs(a[pivot, col]) < 1e-12)
            {
                return null;
            }
            for (var j = 0; j < 6; j++)
            {
                (a[col, j], a[pivot, j]) = (a[pivot, j], a[col, j]);
            }
            var scale = a[col, col];
            for (var j = 0; j < 6; j++)
            {
                a[col, j] /= scale;
            }
            for (var row = 0; row < 3; row++)
            {
                if (row == col)
                {
                    continue;
                }
                var factor = a[row, col];
                for (var j = 0; j < 6; j++)
                {
                    a[row, j] -= factor * a[col, j];
                }
            }
        }
        return a[0, 3] >= 0 && double.IsFinite(a[0, 3]) ? a[0, 3] : null;
    }
}
