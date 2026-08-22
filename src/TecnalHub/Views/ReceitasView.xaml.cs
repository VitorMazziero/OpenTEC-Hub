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
        }

        if (e.NewValue is ReceitasViewModel current)
        {
            current.CenterOnNodeRequested += CenterOnNode;
        }
    }

    // ── Node drag ─────────────────────────────────────────────────────────────

    private void OnNodeMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: RecipeNodeViewModel node })
        {
            Focus();
            ViewModel?.SelectedTab?.SelectNode(node);
            _dragNode = node;
            _dragMoved = false;
            _dragStart = e.GetPosition(CanvasRoot);
            _originX = node.X;
            _originY = node.Y;
            CanvasRoot.CaptureMouse();
            e.Handled = true;
        }
    }

    private void OnPortMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: RecipePortViewModel port } element && FindNode(element) is { } node)
        {
            ViewModel?.PortClicked(node, port);
            e.Handled = true;
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

    private void OnCanvasMouseDown(object sender, MouseButtonEventArgs e)
    {
        Focus();
        ViewModel?.SelectedTab?.SelectNode(null);
        _panning = true;
        _panStart = e.GetPosition(this);
        _panOriginX = PanTransform.X;
        _panOriginY = PanTransform.Y;
        CanvasRoot.CaptureMouse();
    }

    private void OnCanvasMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragNode is not null)
        {
            if (!_dragMoved)
            {
                ViewModel?.SelectedTab?.PushHistory(); // snapshot once, before the first move
                _dragMoved = true;
            }

            var position = e.GetPosition(CanvasRoot);
            _dragNode.X = Math.Max(0, _originX + (position.X - _dragStart.X));
            _dragNode.Y = Math.Max(0, _originY + (position.Y - _dragStart.Y));
        }
        else if (_panning)
        {
            var position = e.GetPosition(this);
            PanTransform.X = _panOriginX + (position.X - _panStart.X);
            PanTransform.Y = _panOriginY + (position.Y - _panStart.Y);
        }
    }

    private void OnCanvasMouseUp(object sender, MouseButtonEventArgs e)
    {
        _dragNode = null;
        _panning = false;
        CanvasRoot.ReleaseMouseCapture();
    }

    // ── Wheel zoom, toward the cursor ──────────────────────────────────────────

    private void OnViewportWheel(object sender, MouseWheelEventArgs e)
    {
        var oldScale = ZoomTransform.ScaleX <= 0 ? 1 : ZoomTransform.ScaleX;
        var factor = e.Delta > 0 ? 1.1 : 1 / 1.1;
        var newScale = Math.Clamp(oldScale * factor, 0.25, 4.0);

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
