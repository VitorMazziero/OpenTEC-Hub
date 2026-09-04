using System;
using System.Globalization;
using System.Linq;
using ScottPlot;
using OpenTECHub.Controls;
using Xunit;

namespace OpenTECHub.Tests;

public class PlotTickTests
{
    [Fact]
    public void TestAnnotationAndMarker()
    {
        var plot = new Plot();
        plot.Add.Scatter(new double[] { 0, 1, 2 }, new double[] { 10, 20, 30 });

        var anno = plot.Add.Annotation("t = 1.0 min\n20.00 °C", Alignment.UpperRight);
        Assert.NotNull(anno);

        var marker = plot.Add.Marker(1.0, 20.0);
        Assert.NotNull(marker);

        plot.RenderInMemory();
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
