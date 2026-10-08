using OpenTECHub.Services.Communication;

namespace OpenTECHub.Services.Recipes;

public sealed partial class RecipeEngine
{
    private async Task ExecuteRampAsync(RecipeNode node, CancellationToken cancellation)
    {
        var runtime = _rampExecution ?? throw new InvalidOperationException("Rampa sem destinos e armazenamento configurados.");
        if (_arbiter is not ICommandAuthorityArbiter authority)
            throw new InvalidOperationException("Rampa exige árbitro com reservas.");
        var configuration = RecipeRampBlockConfiguration.Read(node);
        Log(RecipeLogSeverity.Info, "Rampa iniciada; captura e resultado serão gravados automaticamente.", node.Id);
        var terminal = await new RecipeRampBlockRunner(this, authority, runtime.Store,
            frozen => runtime.CreateDestination(this, frozen), runtime.CreateCapturedDestination is { } captured
                ? start => captured(this, start) : null).ExecuteAsync(node.Id, configuration,
                runtime.PreparationTimeout, runtime.ConfirmationTimeout, runtime.RecoveryTimeout,
                runtime.MinimumDispatchInterval, cancellation).ConfigureAwait(false);
        if (terminal.Status != RecipeRampTerminalStatus.Completed)
        {
            cancellation.ThrowIfCancellationRequested();
            throw new InvalidOperationException($"Rampa encerrada: {terminal.Status}; retorno: {terminal.ReturnOutcome}.");
        }
        Log(RecipeLogSeverity.Info, "Rampa concluída com referências confirmadas e resultado gravado.", node.Id);
    }
}
