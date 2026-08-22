using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using TecnalHub.Services.Recipes;
using TecnalHub.ViewModels;

namespace TecnalHub.Views;

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
        if (tab is null) return;

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
        if (tab is null) return;

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
            _dragNode.X = _originX + (position.X - _dragStart.X);
            _dragNode.Y = _originY + (position.Y - _dragStart.Y);
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
