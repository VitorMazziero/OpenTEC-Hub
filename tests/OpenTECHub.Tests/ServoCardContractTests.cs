using System.IO;
using Xunit;

namespace OpenTECHub.Tests;

/// <summary>
/// What the servo card must and must not put in front of an operator.
/// </summary>
/// <remarks>
/// The view takes the whole application graph and cannot be exercised in isolation, so
/// these pin the markup the way <c>ControlWorkspaceContractTests</c> and
/// <c>DetailPaneContractTests</c> already do. Everything asserted here is a decision that
/// would be silently reversible by an ordinary-looking edit.
/// </remarks>
public sealed class ServoCardContractTests
{
    private static readonly string ViewPath = Path.Combine(
        TestPaths.RepositoryRoot, "src", "OpenTECHub", "Views", "ControlView.xaml");

    private static string Card()
    {
        var xaml = File.ReadAllText(ViewPath);
        var start = xaml.IndexOf("<!-- 13. Servo Drive", StringComparison.Ordinal);
        Assert.True(start >= 0, "O card do servo drive não está no ControlView.");

        // Bounded at the safety bar. Slicing to the end of the file would pull in the
        // "Parada Segura" block, and the assertion that this card carries no stop control
        // would fail against a control that is not on it.
        var end = xaml.IndexOf("<!-- Bottom Action & Safety Bar -->", start, StringComparison.Ordinal);
        Assert.True(end > start, "A barra de ação não foi encontrada após o card.");
        return xaml[start..end];
    }

    /// <summary>
    /// Nothing on this card can move the motor.
    /// </summary>
    /// <remarks>
    /// Agitation stays exclusively on CN1 through <c>motorSetpoint</c>. A rotation entry
    /// here would be a second command path to the same physical quantity over a link with
    /// no acknowledgement - and <c>motorSetpoint</c> zero disables the TECNAL module rather
    /// than stopping it, latching its own keypad, so a stop button here would be worse than
    /// useless.
    /// </remarks>
    [Fact]
    public void The_card_offers_no_way_to_command_the_motor()
    {
        var card = Card();

        Assert.DoesNotContain("MotorSetpoint", card, StringComparison.Ordinal);
        Assert.DoesNotContain("motorSetpoint", card, StringComparison.Ordinal);
        Assert.DoesNotContain("Parada", card, StringComparison.Ordinal);
        Assert.DoesNotContain("Parar", card, StringComparison.Ordinal);
    }

    /// <summary>
    /// Power is labelled as estimated mechanical, in the row and not only in a tooltip.
    /// </summary>
    /// <remarks>
    /// It is <c>T·ω</c> at the shaft, derived from a torque percentage and the motor's
    /// nameplate rating - not electrical draw. An operator who reads it as consumption is
    /// out by orders of magnitude, and a tooltip is not read before that happens.
    /// </remarks>
    [Fact]
    public void Power_carries_its_qualifier_in_the_row_itself()
    {
        var card = Card();

        Assert.Contains("W mec. est.", card, StringComparison.Ordinal);
        Assert.Contains("Energia mecânica acumulada", card, StringComparison.Ordinal);
    }

    /// <summary>
    /// The energy reset is fenced off from anything that touches the process.
    /// </summary>
    /// <remarks>
    /// It clears a data accumulator and does nothing to the reactor. Placed beside a control
    /// that does act, it invites the wrong click on a running vessel - so it sits below a
    /// rule, at the bottom of the drawer, with the caption saying what it does not do.
    /// </remarks>
    [Fact]
    public void The_energy_reset_is_separated_and_says_what_it_does_not_do()
    {
        var card = Card();

        Assert.Contains("ResetEnergyCommand", card, StringComparison.Ordinal);
        Assert.Contains("Não afeta o processo", card, StringComparison.Ordinal);
        Assert.Contains("BorderThickness=\"0,1,0,0\"", card, StringComparison.Ordinal);

        // Below the sampling controls, not above them: the only button in the drawer is
        // the last thing in it, where a stray click is least likely.
        var poll = card.IndexOf("PollIntervalText", StringComparison.Ordinal);
        var reset = card.IndexOf("ResetEnergyCommand", StringComparison.Ordinal);
        Assert.True(poll >= 0 && reset > poll, "A zeragem deve ficar abaixo dos controles de amostragem.");
    }

    /// <summary>
    /// The routing switch says what turning it off actually does.
    /// </summary>
    /// <remarks>
    /// Turning routing off does not stop the node: it keeps running and keeps pushing, and
    /// only the values leave the aggregate frame. A tooltip reading "desligar o servo" would
    /// describe something the control does not do.
    /// </remarks>
    [Fact]
    public void The_routing_toggle_explains_that_the_node_stays_present()
    {
        var card = Card();

        Assert.Contains("IsRoutingEnabled", card, StringComparison.Ordinal);
        Assert.Contains("ele continua presente", card, StringComparison.Ordinal);
    }

    [Fact]
    public void The_card_reuses_the_shared_external_device_vocabulary()
    {
        var card = Card();

        Assert.Contains("ctl:ExternalDeviceChips", card, StringComparison.Ordinal);
        Assert.Contains("ExternalDeviceState", card, StringComparison.Ordinal);
        Assert.Contains("Status.IsOffline", card, StringComparison.Ordinal);
        Assert.Contains("Status.ShowRoutingChipOnly", card, StringComparison.Ordinal);
    }

    /// <summary>The alarm block appears only when the drive raised one.</summary>
    [Fact]
    public void The_alarm_block_is_bound_to_the_alarm_flag()
    {
        var card = Card();

        Assert.Contains("Visibility=\"{Binding IsInAlarm, Converter={StaticResource BoolToVis}}\"", card, StringComparison.Ordinal);
        Assert.Contains("Alarme do drive: {0}", card, StringComparison.Ordinal);
    }

    /// <summary>Every reading the card shows is bound to a formatted, dash-capable text.</summary>
    /// <remarks>
    /// Binding a raw double would render 0 for missing data. The view model formats these
    /// and substitutes an em dash, which is the distinction the whole contract turns on:
    /// zero rpm is a stopped motor, no reading is no reading.
    /// </remarks>
    [Fact]
    public void Readings_bind_to_the_formatted_texts_and_not_to_raw_values()
    {
        var card = Card();

        foreach (var binding in new[]
                 {
                     "{Binding RpmText}",
                     "{Binding TorquePercentText}",
                     "{Binding PowerWattText, Mode=OneWay}",
                     "{Binding TorqueNewtonMetreText",
                     "{Binding LoadPercentText",
                     "{Binding EnergyWattHourText",
                     "{Binding StateText}",
                     "{Binding ErrorRateText",
                     "{Binding QueueDepthText",
                 })
        {
            Assert.Contains(binding, card, StringComparison.Ordinal);
        }

        Assert.DoesNotContain("{Binding ServoRpm}", card, StringComparison.Ordinal);
        Assert.DoesNotContain("{Binding ServoPowerW}", card, StringComparison.Ordinal);
    }

    /// <summary>
    /// The sampling entry commits the way every other entry on this page does.
    /// </summary>
    /// <remarks>
    /// The workspace is automatic-apply by design and carries no "Aplicar" buttons - a
    /// contract <c>ControlWorkspaceContractTests</c> enforces across the whole view. The
    /// first version of this card shipped one and was caught by it.
    /// </remarks>
    [Fact]
    public void The_sampling_entry_has_no_apply_button_of_its_own()
    {
        var card = Card();

        Assert.DoesNotContain("Content=\"Aplicar", card, StringComparison.Ordinal);
        Assert.Contains("Enter para enviar", card, StringComparison.Ordinal);

        var codeBehind = File.ReadAllText(Path.ChangeExtension(ViewPath, ".xaml.cs"));
        Assert.Contains("case ServoDriveViewModel servo", codeBehind, StringComparison.Ordinal);
    }
}
