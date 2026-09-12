using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using OpenTECHub.ViewModels;

namespace OpenTECHub.Controls;

/// <summary>
/// The network identity of one external node - IP, firmware, MAC - and the two things an
/// operator can do with it: open the node's own diagnostic page, or copy the address.
/// </summary>
/// <remarks>
/// <para>
/// <b>Reachability is two conditions, and both are shown.</b> The Hub must have an address
/// for the node (<see cref="ExternalDeviceStatus.IsNodeReachable"/>) and the PC must be on
/// the Hub's Wi-Fi (<see cref="CanReachNodes"/>). Over USB the app still knows the address
/// from the aggregate frame but cannot get there, so the buttons stay visible and disabled
/// with that reason in the tooltip - hiding them would make the feature look absent.
/// </para>
/// <para>
/// The panel owns no behaviour: the commands come from the page's ViewModel, which holds
/// the browser and clipboard boundaries and can therefore be tested.
/// </para>
/// </remarks>
public partial class NodeNetworkPanel : UserControl
{
    public static readonly DependencyProperty StatusProperty =
        DependencyProperty.Register(nameof(Status), typeof(ExternalDeviceStatus), typeof(NodeNetworkPanel), new PropertyMetadata(null));

    public static readonly DependencyProperty CanReachNodesProperty =
        DependencyProperty.Register(nameof(CanReachNodes), typeof(bool), typeof(NodeNetworkPanel), new PropertyMetadata(false));

    public static readonly DependencyProperty OpenCommandProperty =
        DependencyProperty.Register(nameof(OpenCommand), typeof(ICommand), typeof(NodeNetworkPanel), new PropertyMetadata(null));

    public static readonly DependencyProperty CopyCommandProperty =
        DependencyProperty.Register(nameof(CopyCommand), typeof(ICommand), typeof(NodeNetworkPanel), new PropertyMetadata(null));

    public NodeNetworkPanel() => InitializeComponent();

    /// <summary>The device's live status; the panel renders its <see cref="ExternalDeviceStatus.Node"/>.</summary>
    public ExternalDeviceStatus? Status
    {
        get => (ExternalDeviceStatus?)GetValue(StatusProperty);
        set => SetValue(StatusProperty, value);
    }

    /// <summary>True when the telemetry link is Wi-Fi, i.e. the PC is on the Hub's network.</summary>
    public bool CanReachNodes
    {
        get => (bool)GetValue(CanReachNodesProperty);
        set => SetValue(CanReachNodesProperty, value);
    }

    /// <summary>Opens the node's <c>/diag</c>; receives the <see cref="ExternalDeviceStatus"/> as parameter.</summary>
    public ICommand? OpenCommand
    {
        get => (ICommand?)GetValue(OpenCommandProperty);
        set => SetValue(OpenCommandProperty, value);
    }

    /// <summary>Copies the node's IP; receives the <see cref="ExternalDeviceStatus"/> as parameter.</summary>
    public ICommand? CopyCommand
    {
        get => (ICommand?)GetValue(CopyCommandProperty);
        set => SetValue(CopyCommandProperty, value);
    }

}

/// <summary>The tooltip that explains a node-action button: what it does on Wi-Fi, why it is off otherwise.</summary>
public sealed class NodeReachabilityToolTipConverter : IValueConverter
{
    public const string OffWiFi = "Disponível apenas com o PC conectado à rede Wi-Fi do Hub.";

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is true
            ? "Abre a página de diagnóstico do nó no navegador; requer que o Hub tenha registrado o IP."
            : OffWiFi;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
