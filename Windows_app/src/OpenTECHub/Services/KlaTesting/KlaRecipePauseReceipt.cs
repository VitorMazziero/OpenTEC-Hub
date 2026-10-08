namespace OpenTECHub.Services.KlaTesting;

/// <summary>Operational interruption evidence; never evidence of scientific acceptance or physical return.</summary>
public sealed record KlaRecipePauseReceipt(Guid PauseId, Guid InvocationId, Guid RequestId, DateTimeOffset RequestedUtc)
{
    public void ValidateAgainst(KlaAssayApiObservation observation)
    {
        var invocation = observation.Request.RecipePulse?.Invocation
            ?? throw new ArgumentException("Pausa sem invocação de receita.");
        if (PauseId == Guid.Empty || InvocationId != invocation.Context.InvocationId ||
            RequestId != observation.Request.RequestId || observation.State != KlaAssayApiState.Cancelled ||
            observation.StartedUtc is not { } started || observation.CompletedUtc is not { } completed ||
            RequestedUtc < started || RequestedUtc > completed ||
            observation.Result is not { } result || !result.ReasonCodes.Contains("acquisition_cancelled"))
            throw new ArgumentException("Recibo de pausa não corresponde ao cancelamento observado.");
    }
}
