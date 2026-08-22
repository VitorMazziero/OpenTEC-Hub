using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using TecnalHub.ViewModels;

namespace TecnalHub.Views;

/// <summary>
/// Code-behind for the recipe canvas: node dragging and click-to-connect. The graph itself is
/// data-bound; this handles only the pointer gestures the canvas needs.
/// </summary>
public partial class ReceitasView : UserControl
{
    private RecipeNodeViewModel? _dragNode;
    private Point _dragStart;
    private double _originX;
    private double _originY;

    public ReceitasView() => InitializeComponent();

    private ReceitasViewModel? ViewModel => DataContext as ReceitasViewModel;

    private void OnNodeMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: RecipeNodeViewModel node })
        {
            ViewModel?.SelectNode(node);
            _dragNode = node;
            _dragStart = e.GetPosition(CanvasRoot);
            _originX = node.X;
            _originY = node.Y;
            CanvasRoot.CaptureMouse();
            e.Handled = true;
        }
    }

    private void OnCanvasMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragNode is null)
        {
            return;
        }

        var position = e.GetPosition(CanvasRoot);
        _dragNode.X = Math.Max(0, _originX + (position.X - _dragStart.X));
        _dragNode.Y = Math.Max(0, _originY + (position.Y - _dragStart.Y));
    }

    private void OnCanvasMouseUp(object sender, MouseButtonEventArgs e)
    {
        _dragNode = null;
        CanvasRoot.ReleaseMouseCapture();
    }

    private void OnPortMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: RecipePortViewModel port } element
            && FindNode(element) is { } node)
        {
            ViewModel?.PortClicked(node, port);
            e.Handled = true; // a port click must not also start a node drag
        }
    }

    /// <summary>Walks up the visual tree to the node card that owns a clicked port.</summary>
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
