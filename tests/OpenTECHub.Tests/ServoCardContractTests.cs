using System.IO;
using Xunit;

namespace OpenTECHub.Tests;

/// <summary>
/// What the servo drawer must and must not put in front of an operator.
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

    /// <summary>
    /// The Agitação row and its drawer, where the servo drive now lives.
    /// </summary>
    /// <remarks>
    /// It was card 13 of Dispositivos Externos until the drive turned out to be the wrong
    /// shape for that list: it is inside the module, the operator does not switch it on, and
    /// it measures the very shaft the Agitação row commands. Listing it as a peripheral
    /// suggested a choice nobody has.
    /// </remarks>
    private static string Card()
    {
        var xaml = File.ReadAllText(ViewPath);
        var start = xaml.IndexOf("<!-- 1. Agitação", StringComparison.Ordinal);
        Assert.True(start >= 0, "A linha de Agitação não está no ControlView.");

        var end = xaml.IndexOf("<!-- 2. Temperatura", start, StringComparison.Ordinal);
        Assert.True(end > start, "A linha de Temperatura não foi encontrada após a Agitação.");
        return xaml[start..end];
    }

    /// <summary>
    /// The speed entry is the agitation subsystem own binding, and the servo section adds none.
    /// </summary>
    /// <remarks>
    /// The drawer does carry a rotation setpoint - deliberately, so it is the whole agitation
    /// surface - but it is <c>Subsystem.SetpointText</c>, the same binding the row above uses,
    /// going out through the same validation and the same arbiter. What must never appear is a
    /// second path: the servo view model has no speed command at all, and no stop control
    /// belongs here, because <c>motorSetpoint</c> zero disables the TECNAL module rather than
    /// stopping it and latches its own keypad.
    /// </remarks>
    [Fact]
    public void The_only_speed_entry_is_the_agitation_subsystem_binding()
    {
        var card = Card();

        Assert.Equal(2, Occurrences(card, "Binding Subsystem.SetpointText"));
        Assert.DoesNotContain("MotorSetpoint", card, StringComparison.Ordinal);
        Assert.DoesNotContain("motorSetpoint", card, StringComparison.Ordinal);
        Assert.DoesNotContain("Parada", card, StringComparison.Ordinal);
        Assert.DoesNotContain("Parar", card, StringComparison.Ordinal);
    }

    /// <summary>
    /// Torque sits beside the speed on the row, not only inside the drawer.
    /// </summary>
    /// <remarks>
    /// The two come off the same shaft and only mean something together: 600 rpm at 2 % is a
    /// free-running impeller, 600 rpm at 40 % is a vessel fighting back. Behind a drawer
    /// nobody puts them side by side.
    /// </remarks>
    [Fact]
    public void Torque_is_shown_next_to_the_speed_on_the_row()
    {
        var card = Card();
        var drawer = card.IndexOf("IsExpandedServoDrive, RelativeSource={RelativeSource AncestorType=UserControl}, Converter",
            StringComparison.Ordinal);
        Assert.True(drawer > 0, "A gaveta não foi encontrada.");

        var row = card[..drawer];
        Assert.Contains("{Binding TorquePercentText}", row, StringComparison.Ordinal);
        Assert.Contains("% torque", row, StringComparison.Ordinal);
    }

    private static int Occurrences(string text, string value)
    {
        var count = 0;
        var index = text.IndexOf(value, StringComparison.Ordinal);
        while (index >= 0)
        {
            count++;
            index = text.IndexOf(value, index + value.Length, StringComparison.Ordinal);
        }

        return count;
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
    public void Power_and_energy_are_named_as_estimated_mechanical()
    {
        var card = Card();

        Assert.Contains("Potência mecânica estimada", card, StringComparison.Ordinal);
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

    /// <summary>
    /// The drawer keeps the shared presence vocabulary, without pretending to be a device row.
    /// </summary>
    /// <remarks>
    /// The chips stay: presence and routing still have to be told apart, and that is what they
    /// say. The state dot does not - the Agitação row already has one, reporting whether
    /// agitation is running, and a second dot on the same row reporting a different thing would
    /// be two answers to one question.
    /// </remarks>
    [Fact]
    public void The_drawer_reuses_the_shared_presence_chips()
    {
        var card = Card();

        Assert.Contains("ctl:ExternalDeviceChips", card, StringComparison.Ordinal);
        Assert.Contains("Status.StatusText", card, StringComparison.Ordinal);
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
                     "{Binding RpmText,",
                     "{Binding TorquePercentText}",
                     "{Binding PowerWattText,",
                     "{Binding TorqueNewtonMetreText",
                     "{Binding LoadPercentText",
                     "{Binding EnergyWattHourText",
                     "{Binding StateText,",
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

        // The hint that used to sit beside the field is gone: it explained a convention the
        // whole page already follows, and repeating it on one field implied the others behaved
        // differently. The tooltip still says it, where someone unsure would look.
        Assert.DoesNotContain("Enter para enviar", card, StringComparison.Ordinal);
        Assert.Contains("Confirma ao sair do campo ou com Enter", card, StringComparison.Ordinal);

        var codeBehind = File.ReadAllText(Path.ChangeExtension(ViewPath, ".xaml.cs"));
        Assert.Contains("case ServoDriveViewModel servo", codeBehind, StringComparison.Ordinal);
    }
}
