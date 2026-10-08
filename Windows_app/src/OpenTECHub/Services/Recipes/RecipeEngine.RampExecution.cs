using OpenTECHub.Services.Communication;

namespace OpenTECHub.Services.Recipes;

public sealed partial class RecipeEngine
{
    private async Task<bool> ExecuteRampAsync(RecipeNode node, CancellationToken cancellation)
    {
        var runtime = _rampExecution ?? throw new InvalidOperationException("Rampa sem destinos e armazenamento configurados.");
        if (_arbiter is not ICommandAuthorityArbiter authority)
            throw new InvalidOperationException("Rampa exige árbitro com reservas.");
        var configuration = RecipeRampBlockConfiguration.Read(node);
        if (runtime.PrepareConfiguration is { } prepare)
        {
            var legacy = configuration.CompletionCriteria is null;
            configuration = prepare(configuration);
            if (legacy && configuration.CompletionCriteria is not null)
                Log(RecipeLogSeverity.Info, "Rampa antiga: critérios operacionais do editor serão registrados nesta execução.", node.Id);
        }
        Log(RecipeLogSeverity.Info, "Rampa iniciada; captura e resultado serão gravados automaticamente.", node.Id);
        var terminal = await new RecipeRampBlockRunner(this, authority, runtime.Store,
            frozen => runtime.CreateDestination(this, frozen), runtime.CreateCapturedDestination is { } captured
                ? start => captured(this, start) : null).ExecuteAsync(node.Id, configuration,
                runtime.PreparationTimeout, runtime.ConfirmationTimeout, runtime.RecoveryTimeout,
                runtime.MinimumDispatchInterval, cancellation).ConfigureAwait(false);
        if (terminal.Status != RecipeRampTerminalStatus.Completed)
        {
            if (terminal.Status == RecipeRampTerminalStatus.EmergencyStopped)
                throw new OperationCanceledException(terminal.Reason ?? "Rampa interrompida por parada de emergência.", cancellation);
            cancellation.ThrowIfCancellationRequested();
            if (terminal.Status == RecipeRampTerminalStatus.Cancelled && IsRampCascadeClosing(configuration.CascadeNodeId) &&
                (terminal.HasVerifiedRecovery || terminal.ReturnOutcome == RecipeRampReturnOutcome.HeldLastReferences))
            {
                Log(RecipeLogSeverity.Info, "Rampa interrompida pela saída do Controle de O₂; encerramento gravado. O ramo termina aqui.", node.Id);
                return false;
            }
            throw new InvalidOperationException($"Rampa encerrada: {terminal.Status}; retorno: {terminal.ReturnOutcome}.");
        }
        Log(RecipeLogSeverity.Info, "Rampa concluída com referências confirmadas e resultado gravado.", node.Id);
        return true;
    }
}
