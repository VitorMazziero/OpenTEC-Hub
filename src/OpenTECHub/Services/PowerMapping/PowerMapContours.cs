using System;
using System.Collections.Generic;

namespace OpenTECHub.Services.PowerMapping;

/// <summary>A single straight segment of an isoline in physical (Qg, N) coordinates.</summary>
public readonly record struct IsolineSegment(double X1, double Y1, double X2, double Y2);

/// <summary>All the segments extracted for one constant level.</summary>
public sealed record IsolineLevel(double Value, IReadOnlyList<IsolineSegment> Segments);

/// <summary>
/// Marching-squares extraction of constant-value contours from a scalar field that may be
/// undefined (NaN) over part of its domain.
/// </summary>
/// <remarks>
/// <para>
/// The power map leaves everything outside the convex hull of the measured anchors undefined, and
/// §19 forbids silent extrapolation. ScottPlot's own contour plottable paints across the whole
/// data area regardless of NaN, so isolines are extracted here instead: a cell is contoured only
/// when all four of its corners are finite, which keeps every drawn line inside measured ground.
/// </para>
/// <para>
/// The field is indexed <c>[row = N index, column = Qg index]</c>, matching
/// <see cref="PowerMapSurfaceData"/>; the returned coordinates are physical, with X the gas flow
/// and Y the agitation, which is the orientation the plots use.
/// </para>
/// </remarks>
public static class PowerMapContours
{
    /// <summary>
    /// Extracts <paramref name="levelCount"/> evenly spaced isolines between the finite extremes
    /// of <paramref name="field"/>. Cells touching an undefined corner produce no segments.
    /// </summary>
    public static IReadOnlyList<IsolineLevel> Extract(
        double[,] field,
        double[] xGrid,
        double[] yGrid,
        int levelCount = 10,
        double? minValue = null,
        double? maxValue = null)
    {
        ArgumentNullException.ThrowIfNull(field);
        ArgumentNullException.ThrowIfNull(xGrid);
        ArgumentNullException.ThrowIfNull(yGrid);

        var rows = field.GetLength(0);
        var columns = field.GetLength(1);

        if (rows < 2 || columns < 2 || yGrid.Length < rows || xGrid.Length < columns || levelCount < 1)
        {
            return [];
        }

        var min = minValue ?? double.PositiveInfinity;
        var max = maxValue ?? double.NegativeInfinity;

        if (minValue is null || maxValue is null)
        {
            for (var row = 0; row < rows; row++)
            {
                for (var column = 0; column < columns; column++)
                {
                    var value = field[row, column];
                    if (!double.IsFinite(value))
                    {
                        continue;
                    }

                    if (value < min) min = value;
                    if (value > max) max = value;
                }
            }
        }

        if (!double.IsFinite(min) || !double.IsFinite(max) || max - min <= 1e-12)
        {
            return [];
        }

        var levels = new List<IsolineLevel>(levelCount);
        var step = (max - min) / (levelCount + 1);

        for (var index = 1; index <= levelCount; index++)
        {
            var level = min + (index * step);
            var segments = new List<IsolineSegment>();

            for (var row = 0; row < rows - 1; row++)
            {
                for (var column = 0; column < columns - 1; column++)
                {
                    var bottomLeft = field[row, column];
                    var bottomRight = field[row, column + 1];
                    var topRight = field[row + 1, column + 1];
                    var topLeft = field[row + 1, column];

                    // A cell with any undefined corner is not measured ground: skip it whole.
                    if (!double.IsFinite(bottomLeft) || !double.IsFinite(bottomRight) ||
                        !double.IsFinite(topRight) || !double.IsFinite(topLeft))
                    {
                        continue;
                    }

                    var x0 = xGrid[column];
                    var x1 = xGrid[column + 1];
                    var y0 = yGrid[row];
                    var y1 = yGrid[row + 1];

                    AppendCellSegments(
                        segments, level,
                        bottomLeft, bottomRight, topRight, topLeft,
                        x0, x1, y0, y1);
                }
            }

            if (segments.Count > 0)
            {
                levels.Add(new IsolineLevel(level, segments));
            }
        }

        return levels;
    }

    private static void AppendCellSegments(
        List<IsolineSegment> segments,
        double level,
        double bottomLeft,
        double bottomRight,
        double topRight,
        double topLeft,
        double x0,
        double x1,
        double y0,
        double y1)
    {
        var caseIndex = 0;
        if (bottomLeft >= level) caseIndex |= 1;
        if (bottomRight >= level) caseIndex |= 2;
        if (topRight >= level) caseIndex |= 4;
        if (topLeft >= level) caseIndex |= 8;

        if (caseIndex is 0 or 15)
        {
            return;
        }

        // Crossing points on each edge, by linear interpolation along the edge.
        var bottom = new { X = Interpolate(x0, x1, bottomLeft, bottomRight, level), Y = y0 };
        var right = new { X = x1, Y = Interpolate(y0, y1, bottomRight, topRight, level) };
        var top = new { X = Interpolate(x0, x1, topLeft, topRight, level), Y = y1 };
        var left = new { X = x0, Y = Interpolate(y0, y1, bottomLeft, topLeft, level) };

        switch (caseIndex)
        {
            case 1:
            case 14:
                segments.Add(new IsolineSegment(left.X, left.Y, bottom.X, bottom.Y));
                break;
            case 2:
            case 13:
                segments.Add(new IsolineSegment(bottom.X, bottom.Y, right.X, right.Y));
                break;
            case 3:
            case 12:
                segments.Add(new IsolineSegment(left.X, left.Y, right.X, right.Y));
                break;
            case 4:
            case 11:
                segments.Add(new IsolineSegment(top.X, top.Y, right.X, right.Y));
                break;
            case 6:
            case 9:
                segments.Add(new IsolineSegment(bottom.X, bottom.Y, top.X, top.Y));
                break;
            case 7:
            case 8:
                segments.Add(new IsolineSegment(left.X, left.Y, top.X, top.Y));
                break;

            // Saddles: the cell centre decides which pair of corners the contour separates.
            case 5:
            {
                var centre = (bottomLeft + bottomRight + topRight + topLeft) / 4.0;
                if (centre >= level)
                {
                    segments.Add(new IsolineSegment(left.X, left.Y, top.X, top.Y));
                    segments.Add(new IsolineSegment(bottom.X, bottom.Y, right.X, right.Y));
                }
                else
                {
                    segments.Add(new IsolineSegment(left.X, left.Y, bottom.X, bottom.Y));
                    segments.Add(new IsolineSegment(top.X, top.Y, right.X, right.Y));
                }

                break;
            }

            case 10:
            {
                var centre = (bottomLeft + bottomRight + topRight + topLeft) / 4.0;
                if (centre >= level)
                {
                    segments.Add(new IsolineSegment(left.X, left.Y, bottom.X, bottom.Y));
                    segments.Add(new IsolineSegment(top.X, top.Y, right.X, right.Y));
                }
                else
                {
                    segments.Add(new IsolineSegment(left.X, left.Y, top.X, top.Y));
                    segments.Add(new IsolineSegment(bottom.X, bottom.Y, right.X, right.Y));
                }

                break;
            }
        }
    }

    private static double Interpolate(double a, double b, double valueA, double valueB, double level)
    {
        var span = valueB - valueA;
        if (Math.Abs(span) < 1e-15)
        {
            return (a + b) / 2.0;
        }

        var t = Math.Clamp((level - valueA) / span, 0.0, 1.0);
        return a + (t * (b - a));
    }
}
