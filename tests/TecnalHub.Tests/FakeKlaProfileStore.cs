using TecnalHub.Services.KlaMapping;

namespace TecnalHub.Tests;

/// <summary>
/// A minimal <see cref="IKlaProfileStore"/> for the cascade actuation tests: it serves a
/// settable list of published profiles and no-ops the experiment/receipt file operations,
/// which those tests never exercise.
/// </summary>
internal sealed class FakeKlaProfileStore : IKlaProfileStore
{
    public string RootDirectory { get; set; } = "C:\\fake\\kla";
    public string ExperimentsDirectory { get; set; } = "C:\\fake\\kla\\experiments";

    public string GetExperimentFilePath(Guid experimentId) =>
        $"{ExperimentsDirectory}\\{experimentId}.kla.json";

    public List<KlaPublishedProfile> Published { get; } = [];

    public event Action<KlaPublishedProfile>? ProfilePublished;

    public void NotifyPublished(KlaPublishedProfile profile) => ProfilePublished?.Invoke(profile);

    public Task<IReadOnlyList<KlaPublishedProfile>> LoadPublishedAsync(
        CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<KlaPublishedProfile>>([.. Published]);

    public Task<IReadOnlyList<KlaExperimentDocument>> LoadExperimentsAsync(
        CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<KlaExperimentDocument>>([]);

    public Task SaveExperimentAsync(KlaExperimentDocument experiment, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    public Task DeleteExperimentAsync(Guid experimentId, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    public Task<KlaPublishedProfile> PublishAsync(
        KlaExperimentDocument experiment, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task ExportExperimentAsync(Guid experimentId, string destinationPath, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    public Task<KlaExperimentDocument> ImportExperimentAsync(string sourcePath, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();
}

/// <summary>Builds a monotonic published kLa path for the actuation tests.</summary>
internal static class KlaTestProfiles
{
    /// <summary>
    /// A small strictly-increasing allocation: kLa rises with both airflow and agitation, as a
    /// real gradient path does. Airflow 2→8 L/min, agitation 200→700 rpm, kLa 20→120 /h.
    /// </summary>
    public static KlaPublishedProfile Linear(string name = "Ensaio A")
    {
        KlaAllocationSample[] allocation =
        [
            new(20, 2.0, 200),
            new(45, 3.5, 320),
            new(70, 5.0, 440),
            new(95, 6.5, 570),
            new(120, 8.0, 700),
        ];

        var payload = new KlaPublicationPayload
        {
            ProfileId = Guid.NewGuid(),
            ExperimentId = Guid.NewGuid(),
            Version = 1,
            PublishedAtUtc = DateTimeOffset.UnixEpoch,
            Name = name,
            Domain = new KlaDomain(2, 8, 200, 700),
            Anchors = [],
            Algorithm = new KlaAlgorithmSettings(),
            AlgorithmIdentity = "test",
            SurfaceFingerprint = "surface",
            PathFingerprint = "path",
            SurfaceDiagnostics = new KlaSurfaceDiagnostics(20, 120, 0, 0, 100, 0, true, []),
            PathDiagnostics = new KlaPathDiagnostics(0.5, 0.5, 5, 450, 0.5, 0.5, 1, 20, 120, 5, 5, []),
            Allocation = allocation,
        };

        return new KlaPublishedProfile { Payload = payload, ReceiptFingerprint = "receipt-" + name };
    }
}
