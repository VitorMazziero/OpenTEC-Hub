using System.ComponentModel;
using System.Windows;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using TecnalHub.Services.Recipes;

namespace TecnalHub.ViewModels;

/// <summary>
/// A connector between two node ports, rendered as an orthogonal (90°) polyline with an arrowhead,
/// mirroring ReceitasTECNAL. Its geometry recomputes live as either endpoint node moves.
/// </summary>
public sealed partial class RecipeConnectionViewModel : ObservableObject
{
    private const double Stub = 22;
    private const double CornerRadius = 12;

    public RecipeConnectionViewModel(RecipeConnection model, RecipeNodeViewModel source, RecipeNodeViewModel target)
    {
        Model = model;
        Source = source;
        Target = target;
        source.PropertyChanged += OnEndpointMoved;
        target.PropertyChanged += OnEndpointMoved;
        Recompute();
    }

    public RecipeConnection Model { get; }

    public RecipeNodeViewModel Source { get; }

    public RecipeNodeViewModel Target { get; }

    /// <summary>Whether execution has traversed this connection (drives the green executed path).</summary>
    [ObservableProperty]
    public partial bool IsExecuted { get; set; }

    /// <summary>Whether the operator has selected this connection (for deletion).</summary>
    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    /// <summary>The route geometry, rendered by a <c>Path</c> in the view.</summary>
    [ObservableProperty]
    public partial Geometry RouteGeometry { get; set; } = Geometry.Empty;

    /// <summary>The arrowhead triangle at the target port.</summary>
    [ObservableProperty]
    public partial PointCollection ArrowPoints { get; set; } = new();

    private void OnEndpointMoved(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(RecipeNodeViewModel.X) or nameof(RecipeNodeViewModel.Y) or nameof(RecipeNodeViewModel.Height))
        {
            Recompute();
        }
    }

    private void Recompute()
    {
        var (sx, sy) = Anchor(Source, Model.SourceConnector);
        var (tx, ty) = Anchor(Target, Model.TargetConnector);

        var isLoop = ConnectorNames.IsLoopOut(Model.SourceConnector);
        RouteGeometry = isLoop ? BuildLoopGeometry(sx, sy, tx, ty) : BuildRouteGeometry(sx, sy, tx, ty);

        // Arrow points rightward (→) into the port on the left of the node:
        // Tip is at (tx, ty), wings are at (tx - 8, ty ± 4) so the arrow sits outside the node.
        ArrowPoints = new PointCollection
        {
            new(tx - 8, ty - 4),
            new(tx, ty),
            new(tx - 8, ty + 4)
        };
    }

    /// <summary>Builds a geometry for normal (non-loop) connections using orthogonal segments.</summary>
    private Geometry BuildRouteGeometry(double sx, double sy, double tx, double ty)
    {
        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(new Point(sx, sy), false, false);

            if (tx >= sx + 2 * Stub)
            {
                // Forward connection: right, down/up, right — a single S-bend at the midpoint.
                var midX = (sx + tx) / 2;
                ctx.LineTo(new Point(midX, sy), true, false);
                ctx.LineTo(new Point(midX, ty), true, false);
                ctx.LineTo(new Point(tx, ty), true, false);
            }
            else
            {
                // Backward connection: route around the underside of both blocks.
                var below = Math.Max(Source.Y + Source.Height, Target.Y + Target.Height) + 28;
                ctx.LineTo(new Point(sx + Stub, sy), true, false);
                ctx.LineTo(new Point(sx + Stub, below), true, false);
                ctx.LineTo(new Point(tx - Stub, below), true, false);
                ctx.LineTo(new Point(tx - Stub, ty), true, false);
                ctx.LineTo(new Point(tx, ty), true, false);
            }
        }

        geometry.Freeze();
        return geometry;
    }

    /// <summary>
    /// Builds a smooth-arc geometry for loop connections (Saída Loop → Entrada Loop),
    /// mirroring ReceitasTECNAL's <c>LeftLoopConnection</c>. The wire exits left from
    /// the source port, curves vertically with rounded corners, and enters the target
    /// from the left.
    /// </summary>
    private Geometry BuildLoopGeometry(double sx, double sy, double tx, double ty)
    {
        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            var leftX = Math.Min(sx, tx) - Stub;
            var yDir = Math.Sign(ty - sy);
            var r = Math.Min(CornerRadius, Math.Abs(ty - sy) / 2);

            ctx.BeginFigure(new Point(sx, sy), false, false);

            // Line: horizontal out to the left
            ctx.LineTo(new Point(leftX + r, sy), true, false);

            // Arc: turn downward (or upward)
            if (r > 0)
            {
                ctx.ArcTo(
                    new Point(leftX, sy + r * yDir),
                    new Size(r, r),
                    rotationAngle: 0,
                    isLargeArc: false,
                    sweepDirection: yDir > 0 ? SweepDirection.Counterclockwise : SweepDirection.Clockwise,
                    isStroked: true,
                    isSmoothJoin: true);
            }

            // Line: vertical
            ctx.LineTo(new Point(leftX, ty - r * yDir), true, false);

            // Arc: turn right toward the target
            if (r > 0)
            {
                ctx.ArcTo(
                    new Point(leftX + r, ty),
                    new Size(r, r),
                    rotationAngle: 0,
                    isLargeArc: false,
                    sweepDirection: yDir > 0 ? SweepDirection.Counterclockwise : SweepDirection.Clockwise,
                    isStroked: true,
                    isSmoothJoin: true);
            }

            // Line: horizontal into the target
            ctx.LineTo(new Point(tx, ty), true, false);
        }

        geometry.Freeze();
        return geometry;
    }

    private static (double X, double Y) Anchor(RecipeNodeViewModel node, string connector)
    {
        var port = node.Ports.FirstOrDefault(p => p.Name == connector)
                   ?? node.Ports.FirstOrDefault(p =>
                       (ConnectorNames.IsLoopIn(connector) && ConnectorNames.IsLoopIn(p.Name)) ||
                       (ConnectorNames.IsLoopOut(connector) && ConnectorNames.IsLoopOut(p.Name)));

        return port is null
            ? (node.X + RecipeNodeViewModel.Width / 2, node.Y + RecipeNodeViewModel.HeaderHeight / 2)
            : (node.X + port.OffsetX, node.Y + port.OffsetY);
    }

    public void Detach()
    {
        Source.PropertyChanged -= OnEndpointMoved;
        Target.PropertyChanged -= OnEndpointMoved;
    }
}
