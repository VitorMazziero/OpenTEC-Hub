namespace OpenTECHub.Services.KlaTesting;

/// <summary>E7 release gate: physical biotic acquisition has no bench approval yet. Offline analysis remains available.</summary>
public sealed class KlaActuationRelease(bool isIsolatedSimulation = false)
{
    public bool AllowsBioticActuation => isIsolatedSimulation;

    public void EnsureCanRun(KlaAssayProtocol protocol)
    {
        if (protocol == KlaAssayProtocol.Biotic && !AllowsBioticActuation)
            throw new InvalidOperationException("Ensaio biótico no equipamento aguarda validação em bancada. A revisão de curvas salvas continua disponível.");
    }
}
