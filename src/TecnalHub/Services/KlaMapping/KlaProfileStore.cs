using System.IO;
using System.Text.Json;

namespace TecnalHub.Services.KlaMapping;

public interface IKlaProfileStore
{
    Task<IReadOnlyList<KlaExperimentDocument>> LoadExperimentsAsync(
        CancellationToken cancellationToken = default);

    Task SaveExperimentAsync(
        KlaExperimentDocument experiment,
        CancellationToken cancellationToken = default);

    Task DeleteExperimentAsync(Guid experimentId, CancellationToken cancellationToken = default);

    Task<KlaPublishedProfile> PublishAsync(
        KlaExperimentSnapshot experiment,
        KlaSurface surface,
        KlaPathResult path,
        string reviewNote,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<KlaPublishedProfile>> LoadPublishedAsync(
        CancellationToken cancellationToken = default);

    Task<byte[]> ReadReceiptBytesAsync(
        string receiptFingerprint,
        CancellationToken cancellationToken = default);

    Task ExportReceiptAsync(
        string receiptFingerprint,
        string destinationPath,
        CancellationToken cancellationToken = default);

    Task<KlaExperimentSnapshot> ImportReceiptAsDraftAsync(
        string sourcePath,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// File-backed experiment and receipt store. Drafts are mutable operator work; published
/// JSON receipts are create-only and named by their SHA-256 fingerprint.
/// </summary>
public sealed class KlaProfileStore : IKlaProfileStore
{
    private const string DraftFileName = "experiments.json";

    private readonly string _root;
    private readonly string _receipts;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public KlaProfileStore(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        _root = root;
        _receipts = Path.Combine(root, "receipts");
    }

    public async Task<IReadOnlyList<KlaExperimentDocument>> LoadExperimentsAsync(
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await LoadDraftsUnsafeAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveExperimentAsync(
        KlaExperimentDocument experiment,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(experiment);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var drafts = (await LoadDraftsUnsafeAsync(cancellationToken).ConfigureAwait(false)).ToList();
            var index = drafts.FindIndex(item => item.Snapshot.Id == experiment.Snapshot.Id);
            if (index >= 0)
            {
                drafts[index] = experiment;
            }
            else
            {
                drafts.Add(experiment);
            }

            await SaveDraftsUnsafeAsync(drafts, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task DeleteExperimentAsync(
        Guid experimentId,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var drafts = (await LoadDraftsUnsafeAsync(cancellationToken).ConfigureAwait(false))
                .Where(item => item.Snapshot.Id != experimentId)
                .ToList();
            await SaveDraftsUnsafeAsync(drafts, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<KlaPublishedProfile> PublishAsync(
        KlaExperimentSnapshot experiment,
        KlaSurface surface,
        KlaPathResult path,
        string reviewNote,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(experiment);
        ArgumentNullException.ThrowIfNull(surface);
        ArgumentNullException.ThrowIfNull(path);
        if (string.IsNullOrWhiteSpace(reviewNote))
        {
            throw new InvalidOperationException(
                "Informe uma nota de revisão antes de publicar o perfil kLa.");
        }

        if (surface.Input.ScientificFingerprint() != experiment.ScientificFingerprint())
        {
            throw new InvalidOperationException("A superfície não pertence à revisão atual do experimento.");
        }

        if (!string.Equals(path.SourceSurfaceFingerprint, surface.Fingerprint, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("A trajetória não pertence à superfície atual.");
        }

        if (!experiment.Algorithm.IsPaperReference)
        {
            throw new InvalidOperationException(
                "Somente o conjunto numérico de referência pode ser publicado como método do artigo.");
        }

        if (path.Diagnostics.Warnings.Any(warning => warning.Contains("recusada", StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException(string.Join(" ", path.Diagnostics.Warnings));
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var existing = await LoadPublishedUnsafeAsync(cancellationToken).ConfigureAwait(false);
            var prior = existing
                .Where(profile => profile.Payload.ExperimentId == experiment.Id)
                .OrderByDescending(profile => profile.Payload.Version)
                .FirstOrDefault();
            var payload = new KlaPublicationPayload
            {
                ProfileId = prior?.Payload.ProfileId ?? Guid.NewGuid(),
                ExperimentId = experiment.Id,
                Version = (prior?.Payload.Version ?? 0) + 1,
                PublishedAtUtc = DateTimeOffset.UtcNow,
                Name = experiment.Name.Trim(),
                Broth = experiment.Broth.Trim(),
                RunCode = experiment.RunCode.Trim(),
                Notes = experiment.Notes.Trim(),
                ReviewNote = reviewNote.Trim(),
                Domain = experiment.Domain,
                Anchors = experiment.Anchors
                    .OrderByDescending(anchor => anchor.AgitationRpm)
                    .ThenBy(anchor => anchor.AirflowLpm)
                    .ToArray(),
                Algorithm = experiment.Algorithm,
                AlgorithmIdentity = KlaMappingEngine.AlgorithmIdentity(experiment.Algorithm),
                SurfaceFingerprint = surface.Fingerprint,
                PathFingerprint = path.Fingerprint,
                SurfaceDiagnostics = surface.Diagnostics,
                PathDiagnostics = path.Diagnostics,
                Allocation = path.Allocation.ToArray(),
            };
            var payloadBytes = JsonSerializer.SerializeToUtf8Bytes(payload, KlaFingerprint.JsonOptions);
            var profile = new KlaPublishedProfile
            {
                Payload = payload,
                ReceiptFingerprint = KlaFingerprint.ForBytes(payloadBytes),
            };
            var receiptBytes = JsonSerializer.SerializeToUtf8Bytes(profile, KlaFingerprint.JsonOptions);

            Directory.CreateDirectory(_receipts);
            var receiptPath = ReceiptPath(profile.ReceiptFingerprint);
            await using (var stream = new FileStream(
                receiptPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.Read,
                bufferSize: 64 * 1024,
                useAsync: true))
            {
                await stream.WriteAsync(receiptBytes, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            return profile;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<KlaPublishedProfile>> LoadPublishedAsync(
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await LoadPublishedUnsafeAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<byte[]> ReadReceiptBytesAsync(
        string receiptFingerprint,
        CancellationToken cancellationToken = default)
    {
        ValidateFingerprint(receiptFingerprint);
        var bytes = await File.ReadAllBytesAsync(ReceiptPath(receiptFingerprint), cancellationToken)
            .ConfigureAwait(false);
        _ = ReadAndVerifyReceipt(bytes, receiptFingerprint, "recibo solicitado");
        return bytes;
    }

    public async Task ExportReceiptAsync(
        string receiptFingerprint,
        string destinationPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        var bytes = await ReadReceiptBytesAsync(receiptFingerprint, cancellationToken).ConfigureAwait(false);
        await File.WriteAllBytesAsync(destinationPath, bytes, cancellationToken).ConfigureAwait(false);
    }

    public async Task<KlaExperimentSnapshot> ImportReceiptAsDraftAsync(
        string sourcePath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        var bytes = await File.ReadAllBytesAsync(sourcePath, cancellationToken).ConfigureAwait(false);
        var profile = ReadAndVerifyReceipt(bytes, expectedFingerprint: null, "arquivo importado");

        // Import is intentionally a new local draft. The foreign review/publication is
        // retained as a note but never becomes an active operational profile silently.
        return new KlaExperimentSnapshot
        {
            Id = Guid.NewGuid(),
            Name = profile.Payload.Name + " · importado",
            Broth = profile.Payload.Broth,
            RunCode = profile.Payload.RunCode,
            Notes = $"Importado de {profile.ReceiptFingerprint}. Revisão local obrigatória. " +
                    profile.Payload.Notes,
            Domain = profile.Payload.Domain,
            Anchors = profile.Payload.Anchors,
            Algorithm = profile.Payload.Algorithm,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
        };
    }

    private async Task<IReadOnlyList<KlaExperimentDocument>> LoadDraftsUnsafeAsync(
        CancellationToken cancellationToken)
    {
        var path = Path.Combine(_root, DraftFileName);
        if (!File.Exists(path))
        {
            return [];
        }

        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 64 * 1024,
            useAsync: true);
        return await JsonSerializer.DeserializeAsync<KlaExperimentDocument[]>(
                   stream,
                   KlaFingerprint.JsonOptions,
                   cancellationToken).ConfigureAwait(false)
               ?? [];
    }

    private async Task SaveDraftsUnsafeAsync(
        IReadOnlyList<KlaExperimentDocument> drafts,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, DraftFileName);
        var temporaryPath = path + ".tmp";
        await using (var stream = new FileStream(
            temporaryPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 64 * 1024,
            useAsync: true))
        {
            await JsonSerializer.SerializeAsync(
                stream,
                drafts.OrderBy(item => item.Snapshot.Name, StringComparer.CurrentCultureIgnoreCase).ToArray(),
                KlaFingerprint.JsonOptions,
                cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        File.Move(temporaryPath, path, overwrite: true);
    }

    private async Task<IReadOnlyList<KlaPublishedProfile>> LoadPublishedUnsafeAsync(
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(_receipts))
        {
            return [];
        }

        var profiles = new List<KlaPublishedProfile>();
        foreach (var path in Directory.EnumerateFiles(_receipts, "*.kla.json").Order())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
            var fileName = Path.GetFileNameWithoutExtension(Path.GetFileNameWithoutExtension(path));
            var profile = ReadAndVerifyReceipt(bytes, fileName, Path.GetFileName(path));

            profiles.Add(profile);
        }

        return profiles
            .OrderBy(profile => profile.Payload.Name, StringComparer.CurrentCultureIgnoreCase)
            .ThenByDescending(profile => profile.Payload.Version)
            .ToArray();
    }

    private string ReceiptPath(string fingerprint) =>
        Path.Combine(_receipts, fingerprint + ".kla.json");

    private static KlaPublishedProfile ReadAndVerifyReceipt(
        ReadOnlySpan<byte> bytes,
        string? expectedFingerprint,
        string description)
    {
        KlaPublishedProfile profile;
        try
        {
            profile = JsonSerializer.Deserialize<KlaPublishedProfile>(bytes, KlaFingerprint.JsonOptions)
                      ?? throw new InvalidDataException($"Recibo kLa ilegível: {description}.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException($"Recibo kLa ilegível: {description}.", exception);
        }

        var payloadBytes = JsonSerializer.SerializeToUtf8Bytes(profile.Payload, KlaFingerprint.JsonOptions);
        var computed = KlaFingerprint.ForBytes(payloadBytes);
        if (!string.Equals(computed, profile.ReceiptFingerprint, StringComparison.Ordinal) ||
            (expectedFingerprint is not null &&
             !string.Equals(expectedFingerprint, profile.ReceiptFingerprint, StringComparison.Ordinal)))
        {
            throw new InvalidDataException($"Recibo kLa alterado: {description}.");
        }

        return profile;
    }

    private static void ValidateFingerprint(string fingerprint)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fingerprint);
        if (fingerprint.Length != 64 || fingerprint.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new ArgumentException("Invalid receipt fingerprint.", nameof(fingerprint));
        }
    }
}
