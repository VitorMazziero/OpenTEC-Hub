using TecnalHub.Protocol;

namespace TecnalHub.Services.Recipes;

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

    /// <summary>Holds until the flowmeter reports itself online, for the aeration-loop enable.</summary>
    private Task AwaitFlowmeterOnlineAsync(RecipeNode node, CancellationToken ct)
        => AwaitDeviceAsync(
            node,
            "Fluxômetro",
            "o fluxômetro não está online.",
            s => s.FlowmeterOnline,
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
