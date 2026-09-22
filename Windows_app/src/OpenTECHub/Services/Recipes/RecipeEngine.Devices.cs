using OpenTECHub.Protocol;

namespace OpenTECHub.Services.Recipes;

// External devices: holding a block until the device on the other end confirms what was sent it,
// and the two operator exits from that hold. Today this covers the flowmeter (air flow); the other
// external devices join the same mechanism as their actuation lands.
public sealed partial class RecipeEngine
{
    /// <summary>How close the echoed flow setpoint must be to count as applied, in L/min.</summary>
    /// <remarks>The same tolerance the arbiter confirms aeration with, deliberately.</remarks>
    private const double FlowEchoToleranceLpm = 0.1;

    /// <summary>How long a device may stay silent before the operator is told it is holding.</summary>
    /// <remarks>
    /// Four telemetry periods at the field 2 s cadence: a device that simply answered on the next
    /// frame never raises anything, and one that is not there is reported within a few seconds.
    /// </remarks>
    private static readonly TimeSpan DeviceWaitGrace = TimeSpan.FromSeconds(8);

    /// <summary>How often the hold re-checks when no telemetry frame arrives at all.</summary>
    private static readonly TimeSpan DeviceWaitPoll = TimeSpan.FromMilliseconds(500);

    /// <summary>Set to 1 by <see cref="SkipWait"/>; consumed by the block that is holding.</summary>
    private int _skipRequested;

    public RecipeDeviceWait? Waiting { get; private set; }

    public event Action? WaitingChanged;

    public void SkipWait() => Interlocked.Exchange(ref _skipRequested, 1);

    /// <summary>
    /// Holds until the flowmeter echoes <paramref name="target"/> back while reporting itself online.
    /// </summary>
    /// <remarks>
    /// A zero setpoint is a stop, and a stop never waits: an operator cutting the gas must not be
    /// held up by the very device that is failing to answer.
    /// </remarks>
    private Task AwaitFlowAppliedAsync(RecipeNode node, double target, CancellationToken ct)
    {
        var clamped = Math.Clamp(target, 0.0, MaxFlow);
        if (clamped <= 0.0)
        {
            return Task.CompletedTask;
        }

        return AwaitDeviceAsync(
            node,
            "Fluxômetro",
            $"a vazão de {clamped:0.##} L/min não foi confirmada pelo fluxômetro.",
            s => s.FlowmeterOnline && Math.Abs(s.FlowSetpoint - clamped) <= FlowEchoToleranceLpm,
            ct);
    }

    /// <summary>Holds until the Hub echoes the flow loop back as enabled.</summary>
    /// <remarks>
    /// The Hub publishes <c>FlowControlEnabled</c> from the very flag this block wrote, so the
    /// echo is real evidence the switch landed — unlike the flowmeter's own online state, which
    /// says nothing about whether the Hub accepted the command.
    /// </remarks>
    private Task AwaitFlowLoopEnabledAsync(RecipeNode node, CancellationToken ct)
        => AwaitDeviceAsync(
            node,
            "Fluxômetro",
            "o Hub não confirmou a malha de aeração como ativa.",
            s => s.FlowControlEnabled,
            ct);

    /// <summary>
    /// Holds a temperature actuation while the Hub-routed bath acknowledges and settles it.
    /// An old Hub (or the original UART route) deliberately satisfies immediately because it
    /// cannot publish bath evidence; absence of support must never deadlock a legacy recipe.
    /// </summary>
    private Task AwaitBathTemperatureAppliedAsync(
        RecipeNode node,
        double target,
        bool confirmationRequired,
        CancellationToken ct)
    {
        if (!double.IsFinite(target) || target <= 0)
        {
            return Task.CompletedTask;
        }

        // A recipe can legitimately start before the first telemetry frame arrives.  In that
        // state the Hub has not advertised the bath route (and therefore cannot provide a
        // meaningful confirmation); preserve the legacy fire-and-forget behaviour until a
        // bath-capable snapshot is actually present.
        if (!confirmationRequired)
        {
            return Task.CompletedTask;
        }

        return AwaitDeviceAsync(
            node,
            "Banho externo C404",
            $"a referência do reator ({target:0.##} °C) não foi confirmada pela cascata.",
            s => s.HasBathTelemetry && s.TempControlViaBath is true &&
                 s.BathOnline && s.BathCommEnabled is true &&
                 s.TempSetpoint is { } echoedTarget && double.IsFinite(echoedTarget) &&
                 Math.Abs(echoedTarget - target) <= 0.05 &&
                 !s.BathCommandCompletionPending &&
                 s.BathCascadeState.Contains("controlling", StringComparison.OrdinalIgnoreCase) &&
                 s.BathState.Equals("done", StringComparison.OrdinalIgnoreCase) &&
                 double.IsFinite(s.Temperature) && Math.Abs(s.Temperature - target) <= 0.5,
            ct);
    }

    private bool IsBathConfirmationRequired()
        => _bathConfirmationRequiredForRun;

    // ── External Wi-Fi nodes ─────────────────────────────────────────────────
    //
    // Every predicate below shares one escape clause: it is satisfied when the Hub has said
    // nothing at all about the device. Against a Hub built before the presence keys existed,
    // holding would be a hold on evidence that firmware cannot produce - every one of these
    // blocks would stall forever and the operator would have to skip each in turn. Absence of
    // evidence is not evidence of absence here either; the hold is only for a device the Hub
    // is actively reporting as absent, or one that has not yet echoed what it was told.

    /// <summary>Holds until the Hub echoes the pump routing flag in the requested state.</summary>
    private Task AwaitPumpRoutingAsync(RecipeNode node, bool enabled, CancellationToken ct)
        => AwaitDeviceAsync(
            node,
            "Bomba externa",
            enabled
                ? "o Hub não confirmou a bomba como habilitada."
                : "o Hub não confirmou a bomba como desabilitada.",
            s => s.PumpCommEnabled is not { } routed || routed == enabled,
            ct);

    /// <summary>
    /// Holds until the pump node reports it is running the mode that was just sent.
    /// </summary>
    /// <remarks>
    /// <c>PumpMode</c> is the node's own report of what it loaded, not an echo of the frame, so
    /// this is real evidence the profile arrived and was parsed - the thing a recipe needs before
    /// it starts timing a feed.
    /// </remarks>
    private Task AwaitPumpProfileAsync(RecipeNode node, PumpProfileMode mode, CancellationToken ct)
        => AwaitDeviceAsync(
            node,
            "Bomba externa",
            $"o perfil enviado não foi confirmado pela bomba (modo {(int)mode}).",
            s => !s.HasPumpTelemetry || (s.PumpOnline && s.PumpMode == (int)mode),
            ct);

    /// <summary>Holds until the Hub echoes the biomass routing flag in the requested state.</summary>
    private Task AwaitBiomassRoutingAsync(RecipeNode node, bool enabled, CancellationToken ct)
        => AwaitDeviceAsync(
            node,
            "Sensor de biomassa",
            enabled
                ? "o Hub não confirmou o sensor de biomassa como habilitado."
                : "o Hub não confirmou o sensor de biomassa como desabilitado.",
            s => s.BiomassCommEnabled is not { } routed || routed == enabled,
            ct);

    /// <summary>
    /// Holds until a fresh absorbance sample arrives.
    /// </summary>
    /// <remarks>
    /// The only honest confirmation that <c>start</c> took effect. The node publishes nothing at
    /// all while idle except a liveness beat, and the Hub tracks presence and sample freshness on
    /// separate clocks - so an arriving sample means the acquisition loop really is running,
    /// which an online flag alone would not.
    /// </remarks>
    /// <summary>
    /// A genuine absorbance sample: online and neither absent nor one of the node's
    /// sentinels (-99 invalid blank, 9.9 dark). A recipe must not start timing a feed on a
    /// sentinel that merely proves the loop is running.
    /// </summary>
    public static bool IsBiomassMeasuring(SensorSnapshot s)
        => s.BiomassOnline && SensorReadings.IsBiomassAbsorbanceMeasured(s.BiomassAbsorbance);

    private Task AwaitBiomassMeasuringAsync(RecipeNode node, CancellationToken ct)
        => AwaitDeviceAsync(
            node,
            "Sensor de biomassa",
            "a aquisição não começou: nenhuma leitura de absorbância chegou.",
            s => !s.HasBiomassTelemetry || IsBiomassMeasuring(s),
            ct);

    /// <summary>Holds until the agitator node reports the magnitude it was told to hold.</summary>
    /// <remarks>
    /// For a stop the target is zero, and reaching it is exactly what proves the bench
    /// potentiometer did not take the motor back - which is why the recipe's stop locks the
    /// potentiometer out rather than leaving the operator's preference in place.
    /// </remarks>
    private Task AwaitAgitatorAppliedAsync(RecipeNode node, double targetPercent, CancellationToken ct)
        => AwaitDeviceAsync(
            node,
            "Agitador de frasco",
            $"o agitador não confirmou {targetPercent:0.#}%.",
            s => !s.HasAgitatorTelemetry ||
                 (s.AgitatorOnline &&
                  Math.Abs(s.AgitatorPercent - targetPercent) <= AgitatorEchoTolerancePercent),
            ct);

    /// <summary>
    /// Holds the strand until <paramref name="confirmed"/> is satisfied by a telemetry frame.
    /// </summary>
    /// <returns>True when the device confirmed; false when the operator skipped the block.</returns>
    private async Task<bool> AwaitDeviceAsync(
        RecipeNode node,
        string device,
        string detail,
        Func<SensorSnapshot, bool> confirmed,
        CancellationToken ct)
    {
        var since = _time.GetUtcNow();
        Interlocked.Exchange(ref _skipRequested, 0);
        var held = false;

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            _pauseGate.Wait(ct);

            if (_latest is { } snapshot && confirmed(snapshot))
            {
                if (held)
                {
                    Log(RecipeLogSeverity.Info, $"{device}: comando confirmado; a receita seguiu.", node.Id);
                    SetWaiting(null);
                }

                return true;
            }

            if (Interlocked.Exchange(ref _skipRequested, 0) == 1)
            {
                Log(RecipeLogSeverity.Warning,
                    $"{device}: bloco pulado pelo operador sem confirmação do dispositivo.", node.Id);
                SetWaiting(null);
                return false;
            }

            if (!held && _time.GetUtcNow() - since >= DeviceWaitGrace)
            {
                held = true;
                Log(RecipeLogSeverity.Warning,
                    $"{device}: {detail} A receita está aguardando — pule o bloco ou pare a receita.", node.Id);
                SetWaiting(new RecipeDeviceWait(node.Id, device, detail, since));
            }

            await Task.WhenAny(WaitNextFrameAsync(ct), _delay(DeviceWaitPoll, ct)).ConfigureAwait(false);
        }
    }

    private void SetWaiting(RecipeDeviceWait? wait)
    {
        if (Waiting is null && wait is null)
        {
            return;
        }

        Waiting = wait;
        WaitingChanged?.Invoke();
    }
}
