namespace OpenTECHub.ViewModels;

public sealed partial class ReceitasViewModel
{
    /// <summary>
    /// Why autonomous kLa is unavailable, or null. The preparation panel is gone (D-068): the operator profile comes
    /// from the Determinar kLa settings and the recipe name identifies the assay, so only a failure needs to show.
    /// </summary>
    public string? KlaAvailabilityError => _klaHost?.AvailabilityError;

    public bool HasKlaAvailabilityError => !string.IsNullOrWhiteSpace(KlaAvailabilityError);

    /// <summary>The strip above the tabs is shown only while it has something to say.</summary>
    public bool ShowKlaStrip => HasKlaObservations || HasKlaAvailabilityError;
}
