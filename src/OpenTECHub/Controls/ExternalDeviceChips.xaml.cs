using System.Windows;
using System.Windows.Controls;
using OpenTECHub.ViewModels;

namespace OpenTECHub.Controls;

/// <summary>
/// The link state of one external Wi-Fi node, as chips beside its name.
/// </summary>
/// <remarks>
/// <para>
/// Three conditions, and only these three: a command left and is unconfirmed
/// (<c>aguardando</c>), the Hub says the node stopped answering (<c>desconectado</c>),
/// and the Hub's routing flag disagrees with the operator's switch (<c>roteamento</c>).
/// </para>
/// <para>
/// <b>Silence is deliberately not a chip.</b> A device the Hub has never mentioned shows
/// nothing here — the row's status text says <i>aguardando telemetria</i> instead. Absence
/// of evidence is not a fault, and marking it as one would put a red chip on every device
/// whenever the Hub firmware is older than these keys.
/// </para>
/// </remarks>
public partial class ExternalDeviceChips : UserControl
{
    public static readonly DependencyProperty StatusProperty =
        DependencyProperty.Register(
            nameof(Status), typeof(ExternalDeviceStatus), typeof(ExternalDeviceChips),
            new PropertyMetadata(null));

    public static readonly DependencyProperty ChipFontSizeProperty =
        DependencyProperty.Register(
            nameof(ChipFontSize), typeof(double), typeof(ExternalDeviceChips),
            new PropertyMetadata(10.0));

    public ExternalDeviceChips() => InitializeComponent();

    /// <summary>The device's live status. Nothing renders while this is null.</summary>
    public ExternalDeviceStatus? Status
    {
        get => (ExternalDeviceStatus?)GetValue(StatusProperty);
        set => SetValue(StatusProperty, value);
    }

    /// <summary>Chip text size: 10 on the Controle rows, 8 on the synoptic tiles.</summary>
    public double ChipFontSize
    {
        get => (double)GetValue(ChipFontSizeProperty);
        set => SetValue(ChipFontSizeProperty, value);
    }
}
