using System.ComponentModel;
using System.Windows;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using OpenTECHub.Services.Recipes;

namespace OpenTECHub.ViewModels;

/// <summary>
/// A connector between two node ports, rendered as an orthogonal (90°) polyline with an arrowhead,
/// mirroring ReceitasOpenTEC. Its geometry recomputes live as either endpoint node moves.
/// </summary>
public sealed partial class RecipeConnectionViewModel : ObservableObject
{
    private const double Stub = 22;
    private const double CornerRadius = 12;
    private readonly Func<IReadOnlyList<RecipeNodeViewModel>>? _nodesProvider;

    public RecipeConnectionViewModel(
        RecipeConnection model,
        RecipeNodeViewModel source,
        RecipeNodeViewModel target,
        Func<IReadOnlyList<RecipeNodeViewModel>>? nodesProvider = null)
    {
        Model = model;
        Source = source;
        Target = target;
        _nodesProvider = nodesProvider;
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

    /// <summary>
    /// The arrowhead triangle at the target port. Always frozen: a <see cref="PointCollection"/> is a
    /// <see cref="System.Windows.Freezable"/>, and an unfrozen one can only be bound from the thread
    /// that created it — the canvas template throws "DependencySource must be created on the same
    /// thread" on every re-measure otherwise (crash storm of 2026-09-11 18:19).
    /// </summary>
    [ObservableProperty]
    public partial PointCollection ArrowPoints { get; set; } = EmptyPoints;

    private static readonly PointCollection EmptyPoints = CreateFrozen([]);

    private void OnEndpointMoved(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(RecipeNodeViewModel.X) or nameof(RecipeNodeViewModel.Y) or nameof(RecipeNodeViewModel.Height))
        {
            Recompute();
        }
    }

    public void Recompute(IReadOnlyList<RecipeNodeViewModel>? nodes = null)
    {
        var sourcePort = FindPort(Source, Model.SourceConnector);
        var targetPort = FindPort(Target, Model.TargetConnector);
        var (sx, sy) = Anchor(Source, sourcePort);
        var (tx, ty) = Anchor(Target, targetPort);

        var isSelfLoop = ConnectorNames.IsCascadeSelfLoop(Model);
        if (isSelfLoop)
        {
            RouteGeometry = BuildLoopGeometry(sx, sy, tx, ty);
            ArrowPoints = CreateFrozen([new(tx - 8, ty - 4), new(tx, ty), new(tx - 8, ty + 4)]);
            return;
        }

        var obstacles = (nodes ?? _nodesProvider?.Invoke() ?? [])
            .Select(n => new RecipeRouteObstacle(n.Id, new Rect(n.X, n.Y, RecipeNodeViewModel.Width, n.Height)))
            .ToArray();
        var route = RecipeConnectionRouter.Build(
            new Point(sx, sy), new Point(tx, ty), Model.SourceNodeId, Model.TargetNodeId, obstacles,
            sourcePort?.IsOnLeft ?? false, targetPort?.IsOnLeft ?? true, Stub);
        RouteGeometry = BuildRoundedPolyline(route.Points);
        ArrowPoints = BuildArrow(route.Points);
    }

    private void Recompute() => Recompute(_nodesProvider?.Invoke());

    private static PointCollection CreateFrozen(IEnumerable<Point> points)
    {
        var collection = new PointCollection(points);
        collection.Freeze();
        return collection;
    }

    private static PointCollection BuildArrow(IReadOnlyList<Point> points)
    {
        var tip = points[^1];
        var previous = points.Count > 1 ? points[^2] : new Point(tip.X - 1, tip.Y);
        var dx = tip.X - previous.X;
        var dy = tip.Y - previous.Y;
        var length = Math.Sqrt(dx * dx + dy * dy);
        if (length < 0.01)
        {
            dx = -1;
            dy = 0;
            length = 1;
        }

        dx /= length;
        dy /= length;
        var nx = -dy;
        var ny = dx;
        return CreateFrozen([
            new(tip.X - dx * 9 + nx * 4, tip.Y - dy * 9 + ny * 4),
            tip,
            new(tip.X - dx * 9 - nx * 4, tip.Y - dy * 9 - ny * 4),
        ]);
    }

    /// <summary>Builds a rounded geometry from an orthogonal route.</summary>
    private static Geometry BuildRoundedPolyline(IReadOnlyList<Point> points)
    {
        var geometry = new StreamGeometry();
        using var ctx = geometry.Open();
        if (points.Count == 0)
        {
            geometry.Freeze();
            return geometry;
        }

        ctx.BeginFigure(points[0], false, false);
        for (var i = 1; i < points.Count - 1; i++)
        {
            var previous = points[i - 1];
            var corner = points[i];
            var next = points[i + 1];
            var incoming = Math.Sqrt(Math.Pow(corner.X - previous.X, 2) + Math.Pow(corner.Y - previous.Y, 2));
            var outgoing = Math.Sqrt(Math.Pow(next.X - corner.X, 2) + Math.Pow(next.Y - corner.Y, 2));
            var radius = Math.Min(CornerRadius, Math.Min(incoming, outgoing) / 2);
            if (radius < 0.1)
            {
                ctx.LineTo(corner, true, false);
                continue;
            }

            var before = MoveToward(corner, previous, radius);
            var after = MoveToward(corner, next, radius);
            ctx.LineTo(before, true, false);
            var cross = (corner.X - previous.X) * (next.Y - corner.Y)
                        - (corner.Y - previous.Y) * (next.X - corner.X);
            ctx.ArcTo(after, new Size(radius, radius), 0, false,
                cross > 0 ? SweepDirection.Clockwise : SweepDirection.Counterclockwise, true, true);
        }

        ctx.LineTo(points[^1], true, false);
        geometry.Freeze();
        return geometry;
    }

    private static Point MoveToward(Point from, Point to, double distance)
    {
        var dx = to.X - from.X;
        var dy = to.Y - from.Y;
        var length = Math.Sqrt(dx * dx + dy * dy);
        return length < 0.01 ? from : new Point(from.X + dx / length * distance, from.Y + dy / length * distance);
    }

    /// <summary>
    /// Builds a smooth-arc geometry for loop connections (Saída Loop → Entrada Loop),
    /// mirroring ReceitasOpenTEC's <c>LeftLoopConnection</c>. The wire exits left from
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

    private static RecipePortViewModel? FindPort(RecipeNodeViewModel node, string connector)
    {
        return node.Ports.FirstOrDefault(p => p.Name == connector)
               ?? node.Ports.FirstOrDefault(p =>
                       (ConnectorNames.IsLoopIn(connector) && ConnectorNames.IsLoopIn(p.Name)) ||
                       (ConnectorNames.IsLoopOut(connector) && ConnectorNames.IsLoopOut(p.Name)));
    }

    private static (double X, double Y) Anchor(RecipeNodeViewModel node, RecipePortViewModel? port)
    {
        if (port is null)
        {
            return (node.X + RecipeNodeViewModel.Width / 2, node.Y + RecipeNodeViewModel.HeaderHeight / 2);
        }

        return (node.X + port.OffsetX, node.Y + port.OffsetY);
    }

    public void Detach()
    {
        Source.PropertyChanged -= OnEndpointMoved;
        Target.PropertyChanged -= OnEndpointMoved;
    }
}
