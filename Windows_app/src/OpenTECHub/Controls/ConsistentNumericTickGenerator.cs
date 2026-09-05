using System;
using System.Globalization;
using ScottPlot;
using ScottPlot.TickGenerators;

namespace OpenTECHub.Controls;

/// <summary>
/// ScottPlot tick generator that ensures consistent decimal representation across all non-zero tick labels on an axis
/// (e.g. 0, 0.5, 1.0, 1.5, 2.0 or 0, 100, 200, 300), formatting zero simply as "0".
/// </summary>
public sealed class ConsistentNumericTickGenerator : ITickGenerator
{
    private readonly NumericAutomatic _gen = new();

    public Tick[] Ticks { get; private set; } = [];

    public int MaxTickCount
    {
        get => _gen.MaxTickCount;
        set => _gen.MaxTickCount = value;
    }

    public void Regenerate(CoordinateRange range, Edge edge, PixelLength size, Paint paint, LabelStyle labelStyle)
    {
        _gen.Regenerate(range, edge, size, paint, labelStyle);
        var sourceTicks = _gen.Ticks;
        if (sourceTicks == null || sourceTicks.Length == 0)
        {
            Ticks = [];
            return;
        }

        // Find max decimal places among non-zero major ticks
        int maxDecimals = 0;
        foreach (var t in sourceTicks)
        {
            if (!t.IsMajor)
            {
                continue;
            }

            if (Math.Abs(t.Position) < 1e-9)
            {
                continue;
            }

            for (int d = 1; d <= 4; d++)
            {
                double rounded = Math.Round(t.Position, d);
                if (Math.Abs(t.Position - rounded) < 1e-6)
                {
                    double prev = Math.Round(t.Position, d - 1);
                    if (Math.Abs(t.Position - prev) >= 1e-6 && d > maxDecimals)
                    {
                        maxDecimals = d;
                    }
                    break;
                }
            }
        }

        var format = maxDecimals > 0 ? "0." + new string('0', maxDecimals) : "0";
        var result = new Tick[sourceTicks.Length];
        for (int i = 0; i < sourceTicks.Length; i++)
        {
            var t = sourceTicks[i];
            if (!t.IsMajor)
            {
                result[i] = t;
                continue;
            }

            string label;
            if (Math.Abs(t.Position) < 1e-9)
            {
                label = "0";
            }
            else
            {
                label = t.Position.ToString(format, CultureInfo.CurrentCulture);
            }

            result[i] = new Tick(t.Position, label, t.IsMajor);
        }

        Ticks = result;
    }
}
