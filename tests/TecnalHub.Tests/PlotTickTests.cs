using System;
using System.Globalization;
using System.Linq;
using ScottPlot;
using TecnalHub.Controls;
using Xunit;

namespace TecnalHub.Tests;

public class PlotTickTests
{
    [Fact]
    public void TestConsistentTickGenerator_HalfSteps()
    {
        var plot = new Plot();
        plot.Axes.Bottom.TickGenerator = new ConsistentNumericTickGenerator();
        plot.Add.Scatter(new double[] { 0, 0.5, 1.0, 1.5, 2.0 }, new double[] { 0, 1, 2, 3, 4 });
        plot.RenderInMemory();

        var ticks = plot.Axes.Bottom.TickGenerator.Ticks.Where(t => t.IsMajor).ToList();
        Assert.NotEmpty(ticks);

        // Zero is "0"
        var zeroTick = ticks.FirstOrDefault(t => Math.Abs(t.Position) < 1e-9);
        if (zeroTick.Label != null)
        {
            Assert.Equal("0", zeroTick.Label);
        }

        // 1.0 is "1,0" or "1.0" (consistent with 0.5)
        var oneTick = ticks.FirstOrDefault(t => Math.Abs(t.Position - 1.0) < 1e-9);
        if (oneTick.Label != null)
        {
            var sep = CultureInfo.CurrentCulture.NumberFormat.NumberDecimalSeparator;
            Assert.Equal($"1{sep}0", oneTick.Label);
        }
    }

    [Fact]
    public void TestConsistentTickGenerator_IntegerSteps()
    {
        var plot = new Plot();
        plot.Axes.Bottom.TickGenerator = new ConsistentNumericTickGenerator();
        plot.Add.Scatter(new double[] { 0, 100, 200, 300 }, new double[] { 0, 100, 200, 300 });
        plot.RenderInMemory();

        var ticks = plot.Axes.Bottom.TickGenerator.Ticks.Where(t => t.IsMajor).ToList();
        Assert.NotEmpty(ticks);

        var zeroTick = ticks.FirstOrDefault(t => Math.Abs(t.Position) < 1e-9);
        if (zeroTick.Label != null)
        {
            Assert.Equal("0", zeroTick.Label);
        }

        var hundredTick = ticks.FirstOrDefault(t => Math.Abs(t.Position - 100.0) < 1e-9);
        if (hundredTick.Label != null)
        {
            Assert.Equal("100", hundredTick.Label);
        }
    }
}
