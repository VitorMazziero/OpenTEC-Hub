using Xunit;

namespace OpenTECHub.Tests.Rendering;

/// <summary>
/// Every class that drives the shared <see cref="WpfRenderingHost"/> dispatcher. Rendering pumps nested
/// dispatcher frames, so a parallel theme switch or capture from another class could run inside a
/// capture and leave it blank. Running these classes outside the parallel phase keeps them isolated.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class WpfRenderingCollection
{
    public const string Name = "WpfRendering";
}
