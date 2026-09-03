using System.IO;
using OpenTECHub.ViewModels;
using Xunit;

namespace OpenTECHub.Tests;

/// <summary>
/// One name per device, read from one place by everything that shows or matches it.
/// </summary>
/// <remarks>
/// The display strings are the easy half. What these tests protect is the half that is
/// <i>compared</i>: the routing-mismatch keys, spelled in two files and silently useless if
/// they ever differ, and the synoptic drag tags, which route a dropped card.
/// </remarks>
public sealed class DeviceNamingTests
{
    private static string Source(params string[] parts) =>
        File.ReadAllText(Path.Combine(new[] { TestPaths.RepositoryRoot, "src", "OpenTECHub" }.Concat(parts).ToArray()));

    /// <summary>
    /// Both halves of the routing comparison come from the constant, not from a literal.
    /// </summary>
    /// <remarks>
    /// <c>ControlViewModel</c> reports the operator's switch under a name and
    /// <c>AlarmService</c> compares the Hub's echo under the same one. Spelled differently,
    /// the comparison never matches and the alarm goes quiet - failing exactly where it was
    /// supposed to speak up. Literals on either side are how that happens.
    /// </remarks>
    [Fact]
    public void The_routing_keys_are_paired_through_the_constant()
    {
        var control = Source("ViewModels", "ControlViewModel.cs");
        var alarms = Source("Services", "Alarms", "AlarmService.cs");

        foreach (var key in new[]
                 {
                     "DeviceNames.Routing.Airflow",
                     "DeviceNames.Routing.Absorbance",
                     "DeviceNames.Routing.ExternalPump",
                     "DeviceNames.Routing.Distance",
                 })
        {
            Assert.Contains(key, control, StringComparison.Ordinal);
            Assert.Contains(key, alarms, StringComparison.Ordinal);
        }

        Assert.Contains("DeviceNames.Routing.ServoDrive", alarms, StringComparison.Ordinal);

        // No literal may sneak back in beside the constant.
        Assert.DoesNotContain("SetRoutingRequested(\"", control, StringComparison.Ordinal);
    }

    /// <summary>
    /// The old names are gone from both surfaces the operator reads.
    /// </summary>
    [Theory]
    [InlineData("Dosagem de Nutrientes")]
    [InlineData("Dosagem de Antiespumante")]
    [InlineData("Bomba Dosadora Externa")]
    [InlineData("Sensor de Distância")]
    [InlineData("Sensor de Biomassa")]
    public void The_old_names_are_gone_from_the_panel_and_the_control_page(string old)
    {
        Assert.DoesNotContain(old, Source("Views", "ControlView.xaml"), StringComparison.Ordinal);
        Assert.DoesNotContain(old, Source("Views", "SynopticView.xaml"), StringComparison.Ordinal);
    }

    [Fact]
    public void The_new_names_are_the_short_ones()
    {
        Assert.Equal("Nutrientes", DeviceNames.Nutrient);
        Assert.Equal("Antiespumante", DeviceNames.Antifoam);
        Assert.Equal("Bomba Externa", DeviceNames.ExternalPump);
        Assert.Equal("Distância", DeviceNames.Distance);
    }

    /// <summary>
    /// The biomass card is named for the quantity it reports, which is absorbance.
    /// </summary>
    /// <remarks>
    /// The sensor publishes <c>BiomassAbs</c> and the app charts it in <c>Abs</c>.
    /// Transmittance is a different number - <c>A = −log₁₀(T)</c>, so 0.5 Abs is 31.6 %
    /// transmittance - and putting that word on this value would misname the measurement
    /// rather than shorten the label.
    /// </remarks>
    [Fact]
    public void The_biomass_card_is_named_for_absorbance_because_that_is_what_it_reads()
    {
        Assert.Equal("Absorbância", DeviceNames.Absorbance);
        Assert.DoesNotContain("Transmit", Source("Views", "ControlView.xaml"), StringComparison.Ordinal);
        Assert.DoesNotContain("Transmit", Source("Views", "SynopticView.xaml"), StringComparison.Ordinal);
    }

    /// <summary>
    /// Nutriente is commanded-only, so dragging it to a plot charts the commanded duty cycle,
    /// never another device's series.
    /// </summary>
    /// <remarks>
    /// It once returned the antifoam series: a different device, under the nutrient's name, on
    /// a chart that looked entirely normal. The equipment reports no nutrient feedback, so the
    /// honest series is the duty cycle the app asked for — recorded per frame, and a flat zero
    /// while the pump is off. It must still never resolve to the antifoam series.
    /// </remarks>
    [Fact]
    public void Dragging_nutrients_to_a_plot_charts_the_commanded_duty_not_another_device()
    {
        var code = Source("Views", "SynopticView.xaml.cs");
        var branch = code.IndexOf("tag.Contains(\"Nutrientes\"", StringComparison.Ordinal);
        Assert.True(branch > 0, "O ramo de Nutrientes não foi encontrado.");

        // Bounded at the next branch. A fixed window would run into the antifoam case that
        // follows, and assert against a line this test is not about.
        var next = code.IndexOf("tag.Contains(", branch + 20, StringComparison.Ordinal);
        Assert.True(next > branch, "O ramo seguinte não foi encontrado.");

        var body = code[branch..next];
        Assert.Contains("TelemetryChannel.Nutrient", body, StringComparison.Ordinal);
        Assert.DoesNotContain("TelemetryChannel.Antifoam", body, StringComparison.Ordinal);
    }
}

/// <summary>
/// Lists paint the themed surface they sit on, not the WPF default white.
/// </summary>
public sealed class ListThemingTests
{
    /// <summary>
    /// <c>ListBox</c> has an implicit style, as <c>ListView</c> already did.
    /// </summary>
    /// <remarks>
    /// Without one, every ListBox painted the default white chrome over whatever surface was
    /// behind it. The views that noticed set Background and BorderThickness by hand, one at a
    /// time; the raw-data pane on Eventos is where nobody did, and it stayed white in the
    /// dark theme.
    /// </remarks>
    [Fact]
    public void ListBox_is_themed_like_its_sibling_ListView()
    {
        var controls = File.ReadAllText(Path.Combine(
            TestPaths.RepositoryRoot, "src", "OpenTECHub", "Themes", "Controls.xaml"));

        var style = controls.IndexOf("<Style TargetType=\"ListBox\">", StringComparison.Ordinal);
        Assert.True(style > 0, "ListBox não tem estilo implícito.");

        var body = controls[style..(style + 400)];
        Assert.Contains("<Setter Property=\"Background\" Value=\"Transparent\" />", body, StringComparison.Ordinal);
        Assert.Contains("{DynamicResource TextPrimaryBrush}", body, StringComparison.Ordinal);
    }

    /// <summary>The raw-data pane relies on that style rather than hard-coding a colour.</summary>
    [Fact]
    public void The_raw_data_pane_sets_no_colour_of_its_own()
    {
        var xaml = File.ReadAllText(Path.Combine(
            TestPaths.RepositoryRoot, "src", "OpenTECHub", "Views", "EventsView.xaml"));

        Assert.DoesNotContain("Background=\"White\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Background=\"#FFFFFF\"", xaml, StringComparison.Ordinal);
        Assert.Contains("ItemsSource=\"{Binding RawEvents}\"", xaml, StringComparison.Ordinal);
    }
}
