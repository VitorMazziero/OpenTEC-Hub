using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using OpenTECHub.Services.Recipes;
using OpenTECHub.ViewModels;

namespace OpenTECHub.Views;

/// <summary>
/// Code-behind for the recipe canvas: node dragging, click-to-connect, connection selection,
/// wheel-zoom to the cursor and drag-to-pan. The graph itself is data-bound.
/// </summary>
public partial class ReceitasView : UserControl
{
    private RecipeNodeViewModel? _dragNode;
    private bool _dragMoved;
    private Point _dragStart;
    private double _originX;
    private double _originY;

    private bool _panning;
    private Point _panStart;
    private double _panOriginX;
    private double _panOriginY;

    private bool _connectDragging;
    private RecipeNodeViewModel? _connectSourceNode;
    private RecipePortViewModel? _connectSourcePort;
    private Point _connectStart;

    public ReceitasView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private ReceitasViewModel? ViewModel => DataContext as ReceitasViewModel;

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is ReceitasViewModel old)
        {
            old.CenterOnNodeRequested -= CenterOnNode;
            old.RequestViewportCenter -= GetViewportCenter;
        }

        if (e.NewValue is ReceitasViewModel current)
        {
            current.CenterOnNodeRequested += CenterOnNode;
            current.RequestViewportCenter += GetViewportCenter;
        }
    }

    private (double X, double Y) GetViewportCenter()
    {
        var scale = ZoomTransform.ScaleX <= 0 ? 1 : ZoomTransform.ScaleX;
        var width = ViewportBorder.ActualWidth > 0 ? ViewportBorder.ActualWidth : 800;
        var height = ViewportBorder.ActualHeight > 0 ? ViewportBorder.ActualHeight : 500;
        var cx = (-PanTransform.X + width / 2) / scale - RecipeNodeViewModel.Width / 2;
        var cy = (-PanTransform.Y + height / 2) / scale - 40;
        return (cx, cy);
    }

    // ── Node drag ─────────────────────────────────────────────────────────────

    private void OnNodeMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left && sender is FrameworkElement { DataContext: RecipeNodeViewModel node })
        {
            Focus();
            ViewModel?.SelectedTab?.SelectNode(node);
            _dragNode = node;
            _dragMoved = false;
            _dragStart = e.GetPosition(CanvasRoot);
            _originX = node.X;
            _originY = node.Y;
            ViewportBorder.CaptureMouse();
            e.Handled = true;
        }
        else if (e.ChangedButton is MouseButton.Right or MouseButton.Middle)
        {
            OnViewportMouseDown(sender, e);
        }
    }

    private void OnPortMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: RecipePortViewModel port } element
            && FindNode(element) is { } node && !port.IsInput)
        {
            _connectDragging = true;
            _connectSourceNode = node;
            _connectSourcePort = port;

            var anchor = new Point(node.X + port.OffsetX, node.Y + port.OffsetY);
            _connectStart = anchor;

            DragPreviewLine.X1 = anchor.X;
            DragPreviewLine.Y1 = anchor.Y;
            DragPreviewLine.X2 = anchor.X;
            DragPreviewLine.Y2 = anchor.Y;
            DragPreviewLine.Visibility = Visibility.Visible;

            HighlightConnectablePorts(node, port);

            ViewportBorder.CaptureMouse();
            e.Handled = true;
        }
    }

    private void HighlightConnectablePorts(RecipeNodeViewModel sourceNode, RecipePortViewModel sourcePort)
    {
        var tab = ViewModel?.SelectedTab;
        if (tab is null)
        {
            return;
        }

        var existingConnections = tab.Document.Connections;

        foreach (var node in tab.Nodes)
        {
            foreach (var port in node.Ports)
            {
                if (!port.IsInput)
                {
                    port.IsConnectableTarget = false;
                    continue;
                }

                var isSameNode = node.Id == sourceNode.Id;
                var isLoopSelf = isSameNode && ConnectorNames.IsLoopOut(sourcePort.Name) && ConnectorNames.IsLoopIn(port.Name);

                if (isSameNode && !isLoopSelf)
                {
                    port.IsConnectableTarget = false;
                    continue;
                }

                // The Saída Loop already feeds an exit condition, so a self-loop would decide
                // nothing and the drop is refused — do not offer it as a target.
                if (isLoopSelf && existingConnections.Any(c => c.SourceNodeId == sourceNode.Id
                        && ConnectorNames.IsLoopOut(c.SourceConnector)
                        && c.TargetNodeId != sourceNode.Id))
                {
                    port.IsConnectableTarget = false;
                    continue;
                }

                // Check if this input port is already connected
                var alreadyConnected = !port.Port.Multiple && existingConnections.Any(c => c.TargetNodeId == node.Id && c.TargetConnector == port.Name);

                // Also check if this exact connection already exists
                var exactExists = existingConnections.Any(c => c.SourceNodeId == sourceNode.Id && c.SourceConnector == sourcePort.Name && c.TargetNodeId == node.Id && c.TargetConnector == port.Name);

                port.IsConnectableTarget = !alreadyConnected && !exactExists;
            }
        }
    }

    private void ClearConnectablePorts()
    {
        var tab = ViewModel?.SelectedTab;
        if (tab is null)
        {
            return;
        }

        foreach (var node in tab.Nodes)
        {
            foreach (var port in node.Ports)
            {
                port.IsConnectableTarget = false;
            }
        }
    }

    private void OnConnectionMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: RecipeConnectionViewModel connection })
        {
            Focus();
            ViewModel?.SelectedTab?.SelectConnection(connection);
            e.Handled = true;
        }
    }

    // ── Canvas: pan on empty drag, plus node move ──────────────────────────────

    private void OnViewportMouseDown(object sender, MouseButtonEventArgs e)
    {
        Focus();
        if (e.ChangedButton == MouseButton.Left)
        {
            ViewModel?.SelectedTab?.SelectNode(null);
        }

        _panning = true;
        _panStart = e.GetPosition(this);
        _panOriginX = PanTransform.X;
        _panOriginY = PanTransform.Y;
        ViewportBorder.CaptureMouse();
    }

    private void OnCanvasMouseMove(object sender, MouseEventArgs e)
    {
        if (_connectDragging)
        {
            var position = e.GetPosition(CanvasRoot);
            DragPreviewLine.X2 = position.X;
            DragPreviewLine.Y2 = position.Y;
        }
        else if (_dragNode is not null)
        {
            if (!_dragMoved)
            {
                ViewModel?.SelectedTab?.PushHistory(); // snapshot once, before the first move
                _dragMoved = true;
            }

            var position = e.GetPosition(CanvasRoot);
            var rawX = _originX + (position.X - _dragStart.X);
            var rawY = _originY + (position.Y - _dragStart.Y);
            var snapped = SnapNode(_dragNode, rawX, rawY);
            _dragNode.X = snapped.X;
            _dragNode.Y = snapped.Y;
            ViewModel?.SelectedTab?.SetAlignmentGuides(snapped.Guides);
        }
        else if (_panning)
        {
            var position = e.GetPosition(this);
            PanTransform.X = _panOriginX + (position.X - _panStart.X);
            PanTransform.Y = _panOriginY + (position.Y - _panStart.Y);
        }
    }

    private void OnViewportMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_connectDragging)
        {
            DragPreviewLine.Visibility = Visibility.Collapsed;
            _connectDragging = false;
            ClearConnectablePorts();

            var position = e.GetPosition(CanvasRoot);
            var targetPort = FindPortAtPosition(position);
            if (targetPort is { Port: var port, Node: var node }
                && port.IsInput
                && _connectSourceNode is not null
                && _connectSourcePort is not null)
            {
                var isLoopSelfConnection = node.Id == _connectSourceNode.Id &&
                                           ConnectorNames.IsLoopOut(_connectSourcePort.Name) &&
                                           ConnectorNames.IsLoopIn(port.Name);

                if (node.Id != _connectSourceNode.Id || isLoopSelfConnection)
                {
                    ViewModel?.PortClicked(_connectSourceNode, _connectSourcePort);
                    ViewModel?.PortClicked(node, port);
                }
            }

            _connectSourceNode = null;
            _connectSourcePort = null;
            ViewportBorder.ReleaseMouseCapture();
            e.Handled = true;
            return;
        }

        _dragNode = null;
        ViewModel?.SelectedTab?.ClearAlignmentGuides();
        _panning = false;
        ViewportBorder.ReleaseMouseCapture();
    }

    private (RecipeNodeViewModel Node, RecipePortViewModel Port)? FindPortAtPosition(Point position)
    {
        const double hitRadius = 12;
        foreach (var node in ViewModel?.SelectedTab?.Nodes ?? [])
        {
            foreach (var port in node.Ports)
            {
                var px = node.X + port.OffsetX;
                var py = node.Y + port.OffsetY;
                var dist = Math.Sqrt(Math.Pow(position.X - px, 2) + Math.Pow(position.Y - py, 2));
                if (dist <= hitRadius)
                {
                    return (node, port);
                }
            }
        }
        return null;
    }

    private SnapResult SnapNode(RecipeNodeViewModel moving, double rawX, double rawY)
    {
        var tab = ViewModel?.SelectedTab;
        if (tab is null)
        {
            return new SnapResult(rawX, rawY, []);
        }

        var others = tab.Nodes.Where(n => !ReferenceEquals(n, moving)).ToArray();
        var tolerance = 8 / Math.Max(0.05, ZoomTransform.ScaleX);
        var xCandidates = new List<(double Value, double Distance)>();
        var yCandidates = new List<(double Value, double Distance)>();

        foreach (var other in others)
        {
            var otherX = new[] { other.X, other.X + RecipeNodeViewModel.Width / 2, other.X + RecipeNodeViewModel.Width };
            var movingX = new[] { rawX, rawX + RecipeNodeViewModel.Width / 2, rawX + RecipeNodeViewModel.Width };
            for (var i = 0; i < otherX.Length; i++)
            {
                xCandidates.Add((otherX[i] - (movingX[i] - rawX), Math.Abs(otherX[i] - movingX[i])));
            }

            var otherY = new[] { other.Y, other.Y + other.Height / 2, other.Y + other.Height };
            var movingY = new[] { rawY, rawY + moving.Height / 2, rawY + moving.Height };
            for (var i = 0; i < otherY.Length; i++)
            {
                yCandidates.Add((otherY[i] - (movingY[i] - rawY), Math.Abs(otherY[i] - movingY[i])));
            }

            // Port ordinates are the most useful alignment target: once snapped, a
            // normal connector can become a single straight horizontal segment.
            foreach (var movingPort in moving.Ports)
            {
                foreach (var otherPort in other.Ports)
                {
                    var targetY = other.Y + otherPort.OffsetY;
                    var candidateY = targetY - movingPort.OffsetY;
                    yCandidates.Add((candidateY, Math.Abs(candidateY - rawY)));
                }
            }
        }

        var xHasMatch = xCandidates.Any(c => c.Distance <= tolerance);
        var yHasMatch = yCandidates.Any(c => c.Distance <= tolerance);
        var xMatch = xCandidates.Where(c => c.Distance <= tolerance).OrderBy(c => c.Distance).FirstOrDefault();
        var yMatch = yCandidates.Where(c => c.Distance <= tolerance).OrderBy(c => c.Distance).FirstOrDefault();
        var x = xHasMatch ? xMatch.Value : rawX;
        var y = yHasMatch ? yMatch.Value : rawY;

        var (width, height) = tab.ComputeCanvasBounds();
        var guides = new List<RecipeAlignmentGuide>();
        if (xHasMatch)
        {
            guides.Add(new RecipeAlignmentGuide(x, 0, x, height));
        }

        if (yHasMatch)
        {
            guides.Add(new RecipeAlignmentGuide(0, y, width, y));
        }

        return new SnapResult(x, y, guides);
    }

    private sealed record SnapResult(double X, double Y, IReadOnlyList<RecipeAlignmentGuide> Guides);

    // ── Wheel zoom, toward the cursor ──────────────────────────────────────────

    private void OnViewportWheel(object sender, MouseWheelEventArgs e)
    {
        var oldScale = ZoomTransform.ScaleX <= 0 ? 1 : ZoomTransform.ScaleX;
        var factor = e.Delta > 0 ? 1.1 : 1 / 1.1;
        var newScale = Math.Clamp(oldScale * factor, 0.05, 5.0);

        // The content-space point under the cursor stays fixed as the scale changes.
        var cursor = e.GetPosition(CanvasRoot);
        ZoomTransform.ScaleX = newScale;
        ZoomTransform.ScaleY = newScale;
        PanTransform.X -= cursor.X * (newScale - oldScale);
        PanTransform.Y -= cursor.Y * (newScale - oldScale);
        e.Handled = true;
    }

    private void CenterOnNode(RecipeNodeViewModel node)
    {
        var scale = ZoomTransform.ScaleX <= 0 ? 1 : ZoomTransform.ScaleX;
        PanTransform.X = ActualWidth / 2 - (node.X + RecipeNodeViewModel.Width / 2) * scale;
        PanTransform.Y = 320 - node.Y * scale;
    }

    private void OnFindingSelected(object sender, SelectionChangedEventArgs e)
    {
        if (sender is ListBox { SelectedItem: RecipeFinding finding } && ViewModel is { } vm)
        {
            vm.RevealFindingCommand.Execute(finding);
        }
    }

    // ── Tab rename ────────────────────────────────────────────────────────────

    private void OnTabTitleDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: RecipeTabViewModel tab })
        {
            tab.IsRenaming = true;
            e.Handled = true;
        }
    }

    private void OnTabRenameKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && sender is TextBox textBox)
        {
            CommitRename(textBox);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && sender is TextBox tb)
        {
            if (tb.DataContext is RecipeTabViewModel tab)
            {
                tab.IsRenaming = false;
            }

            e.Handled = true;
        }
    }

    private void OnTabRenameLostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is TextBox textBox)
        {
            CommitRename(textBox);
        }
    }

    private void OnTabRenameVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is TextBox textBox && textBox.IsVisible)
        {
            textBox.Focus();
            textBox.SelectAll();
        }
    }

    private static void CommitRename(TextBox textBox)
    {
        // Force the binding to update
        var binding = textBox.GetBindingExpression(TextBox.TextProperty);
        binding?.UpdateSource();

        if (textBox.DataContext is RecipeTabViewModel tab)
        {
            tab.IsRenaming = false;
        }
    }

    private void OnKlaDropDownOpened(object? sender, EventArgs e)
    {
        if (DataContext is ReceitasViewModel { SelectedTab.SelectedNode: { } node })
        {
            node.LoadAvailableKlaPaths();
        }
    }

    private static RecipeNodeViewModel? FindNode(DependencyObject start)
    {
        for (var current = start; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (current is FrameworkElement { DataContext: RecipeNodeViewModel node })
            {
                return node;
            }
        }

        return null;
    }
}
