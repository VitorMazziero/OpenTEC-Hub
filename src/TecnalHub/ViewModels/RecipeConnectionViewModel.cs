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

    /// <summary>The orthogonal route, as a polyline point list.</summary>
    [ObservableProperty]
    public partial PointCollection RoutePoints { get; set; } = new();

    /// <summary>The arrowhead triangle at the target port.</summary>
    [ObservableProperty]
    public partial PointCollection ArrowPoints { get; set; } = new();

    private void OnEndpointMoved(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(RecipeNodeViewModel.X) or nameof(RecipeNodeViewModel.Y))
        {
            Recompute();
        }
    }

    private void Recompute()
    {
        var (sx, sy) = Anchor(Source, Model.SourceConnector);
        var (tx, ty) = Anchor(Target, Model.TargetConnector);

        RoutePoints = Route(sx, sy, tx, ty);

        // The arrow arrives pointing right into the target's left-edge input port.
        ArrowPoints = new PointCollection { new(tx - 9, ty - 5), new(tx, ty), new(tx - 9, ty + 5) };
    }

    private PointCollection Route(double sx, double sy, double tx, double ty)
    {
        var points = new PointCollection();
        if (tx >= sx + 2 * Stub)
        {
            // Forward connection: right, down/up, right — a single S-bend at the midpoint.
            var midX = (sx + tx) / 2;
            points.Add(new Point(sx, sy));
            points.Add(new Point(midX, sy));
            points.Add(new Point(midX, ty));
            points.Add(new Point(tx, ty));
        }
        else
        {
            // Backward/loop-return connection: route around the underside of both blocks.
            var below = Math.Max(Source.Y + Source.Height, Target.Y + Target.Height) + 28;
            points.Add(new Point(sx, sy));
            points.Add(new Point(sx + Stub, sy));
            points.Add(new Point(sx + Stub, below));
            points.Add(new Point(tx - Stub, below));
            points.Add(new Point(tx - Stub, ty));
            points.Add(new Point(tx, ty));
        }

        return points;
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
