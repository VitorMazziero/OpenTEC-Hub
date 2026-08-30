using System.IO;
using System.Text.Json;

namespace OpenTECHub.Services.KlaMapping;

public interface IKlaProfileStore
{
    string RootDirectory { get; }

    string ExperimentsDirectory { get; }

    string GetExperimentFilePath(Guid experimentId);

    Task<IReadOnlyList<KlaExperimentDocument>> LoadExperimentsAsync(
        CancellationToken cancellationToken = default);

    Task SaveExperimentAsync(
        KlaExperimentDocument experiment,
        CancellationToken cancellationToken = default);

    Task DeleteExperimentAsync(Guid experimentId, CancellationToken cancellationToken = default);

    Task<KlaPublishedProfile> PublishAsync(
        KlaExperimentDocument experiment,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<KlaPublishedProfile>> LoadPublishedAsync(
        CancellationToken cancellationToken = default);

    Task ExportExperimentAsync(
        Guid experimentId,
        string destinationPath,
        CancellationToken cancellationToken = default);

    Task<KlaExperimentDocument> ImportExperimentAsync(
        string sourcePath,
        CancellationToken cancellationToken = default);

    /// <summary>Raised whenever a new kLa profile is successfully published for control.</summary>
    event Action<KlaPublishedProfile>? ProfilePublished;
}

/// <summary>
/// File-backed store saving whole kLa experiments in individual JSON files
/// under <c>experiments/&lt;guid&gt;.kla.json</c>.
/// </summary>
public sealed class KlaProfileStore : IKlaProfileStore
{
    private const string LegacyDraftFileName = "experiments.json";

    private readonly string _root;
    private readonly string _experiments;
    private readonly string _receipts;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _migrated;

    public string RootDirectory => _root;

    public string ExperimentsDirectory => _experiments;

    public event Action<KlaPublishedProfile>? ProfilePublished;

    public KlaProfileStore(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        _root = root;
        _experiments = Directory.Exists(Path.Combine(root, "Experimentos"))
            ? Path.Combine(root, "Experimentos")
            : (Directory.Exists(Path.Combine(root, "experiments"))
                ? Path.Combine(root, "experiments")
                : Path.Combine(root, "Experimentos"));
        _receipts = Directory.Exists(Path.Combine(root, "Recibos"))
            ? Path.Combine(root, "Recibos")
            : (Directory.Exists(Path.Combine(root, "receipts"))
                ? Path.Combine(root, "receipts")
                : Path.Combine(root, "Recibos"));
    }

    public string GetExperimentFilePath(Guid experimentId) =>
        Path.Combine(_experiments, $"{experimentId}.kla.json");

    public async Task<IReadOnlyList<KlaExperimentDocument>> LoadExperimentsAsync(
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureMigratedUnsafeAsync(cancellationToken).ConfigureAwait(false);
            return await LoadExperimentsUnsafeAsync(cancellationToken).ConfigureAwait(false);
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
            await SaveExperimentUnsafeAsync(experiment, cancellationToken).ConfigureAwait(false);
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
            var filePath = GetExperimentFilePath(experimentId);
            if (File.Exists(filePath))
            {
                File.Delete(filePath);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<KlaPublishedProfile> PublishAsync(
        KlaExperimentDocument document,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);

        if (document.PathData is null || document.PathData.Allocation.Length == 0)
        {
            throw new InvalidOperationException("Calcule uma trajetória válida antes de publicar o experimento para controle.");
        }

        var publishedDoc = document with
        {
            Stage = KlaWorkflowStage.Published,
            IsAvailableForControl = true,
            LastPublishedAtUtc = DateTimeOffset.UtcNow,
        };

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await SaveExperimentUnsafeAsync(publishedDoc, cancellationToken).ConfigureAwait(false);
            var profile = ToPublishedProfile(publishedDoc);
            ProfilePublished?.Invoke(profile);
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
            await EnsureMigratedUnsafeAsync(cancellationToken).ConfigureAwait(false);
            var experiments = await LoadExperimentsUnsafeAsync(cancellationToken).ConfigureAwait(false);
            var published = experiments
                .Where(doc => doc.IsAvailableForControl || doc.Stage == KlaWorkflowStage.Published)
                .Select(ToPublishedProfile)
                .OrderBy(profile => profile.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();

            return published;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task ExportExperimentAsync(
        Guid experimentId,
        string destinationPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var sourcePath = GetExperimentFilePath(experimentId);
            if (!File.Exists(sourcePath))
            {
                throw new FileNotFoundException($"Experimento não encontrado em {sourcePath}", sourcePath);
            }

            var bytes = await File.ReadAllBytesAsync(sourcePath, cancellationToken).ConfigureAwait(false);
            await File.WriteAllBytesAsync(destinationPath, bytes, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<KlaExperimentDocument> ImportExperimentAsync(
        string sourcePath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        var bytes = await File.ReadAllBytesAsync(sourcePath, cancellationToken).ConfigureAwait(false);

        KlaExperimentDocument importedDoc;
        try
        {
            importedDoc = JsonSerializer.Deserialize<KlaExperimentDocument>(bytes, KlaFingerprint.JsonOptions)
                ?? throw new InvalidDataException("Arquivo de experimento kLa inválido ou vazio.");
        }
        catch
        {
            // Tenta importar como recibo legado
            try
            {
                var legacyProfile = JsonSerializer.Deserialize<KlaPublishedProfile>(bytes, KlaFingerprint.JsonOptions)
                    ?? throw new InvalidDataException("Arquivo de recibo kLa inválido.");

                var payload = legacyProfile.Payload;
                importedDoc = new KlaExperimentDocument
                {
                    Snapshot = new KlaExperimentSnapshot
                    {
                        Id = Guid.NewGuid(),
                        Name = payload.Name,
                        Broth = payload.Broth,
                        RunCode = payload.RunCode,
                        Notes = payload.Notes,
                        Domain = payload.Domain,
                        Anchors = payload.Anchors,
                        Algorithm = payload.Algorithm,
                        UpdatedAtUtc = DateTimeOffset.UtcNow,
                    },
                    DraftRows = payload.Anchors.Select(a => new KlaAnchorDraft(
                        a.AirflowLpm.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        a.AgitationRpm.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        a.KlaPerHour.ToString(System.Globalization.CultureInfo.InvariantCulture))).ToArray(),
                    Stage = KlaWorkflowStage.Published,
                    IsAvailableForControl = true,
                    LastPublishedAtUtc = payload.PublishedAtUtc,
                    PathData = new KlaPathData(
                        [],
                        payload.Allocation,
                        [],
                        0,
                        payload.PathDiagnostics,
                        payload.SurfaceFingerprint,
                        payload.PathFingerprint),
                    SurfaceData = new KlaSurfaceData(payload.SurfaceDiagnostics, payload.SurfaceFingerprint),
                };
            }
            catch (Exception ex)
            {
                throw new InvalidDataException($"Não foi possível importar o arquivo kLa: {ex.Message}", ex);
            }
        }

        var newSnapshot = importedDoc.Snapshot with
        {
            Id = Guid.NewGuid(),
            Name = importedDoc.Snapshot.Name + " · importado",
            UpdatedAtUtc = DateTimeOffset.UtcNow,
        };

        var finalDoc = importedDoc with
        {
            Snapshot = newSnapshot,
        };

        await SaveExperimentAsync(finalDoc, cancellationToken).ConfigureAwait(false);
        return finalDoc;
    }

    private async Task<IReadOnlyList<KlaExperimentDocument>> LoadExperimentsUnsafeAsync(
        CancellationToken cancellationToken)
    {
        var dirsToSearch = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (Directory.Exists(_experiments))
        {
            dirsToSearch.Add(_experiments);
        }

        var legacyDir = Path.Combine(_root, "experiments");
        if (Directory.Exists(legacyDir))
        {
            dirsToSearch.Add(legacyDir);
        }

        if (dirsToSearch.Count == 0)
        {
            return [];
        }

        var seenIds = new HashSet<Guid>();
        var list = new List<KlaExperimentDocument>();
        foreach (var dir in dirsToSearch)
        {
            foreach (var file in Directory.EnumerateFiles(dir, "*.kla.json"))
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    await using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, useAsync: true);
                    var doc = await JsonSerializer.DeserializeAsync<KlaExperimentDocument>(stream, KlaFingerprint.JsonOptions, cancellationToken).ConfigureAwait(false);
                    if (doc is not null && seenIds.Add(doc.Snapshot.Id))
                    {
                        list.Add(doc);
                    }
                }
                catch
                {
                    // Ignora arquivo corrompido para não quebrar a listagem inteira
                }
            }
        }

        return list.OrderBy(d => d.Snapshot.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    private async Task SaveExperimentUnsafeAsync(
        KlaExperimentDocument experiment,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_experiments);
        var filePath = GetExperimentFilePath(experiment.Snapshot.Id);
        var temporaryPath = filePath + ".tmp";

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
                experiment,
                KlaFingerprint.JsonOptions,
                cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        File.Move(temporaryPath, filePath, overwrite: true);
    }

    private async Task EnsureMigratedUnsafeAsync(CancellationToken cancellationToken)
    {
        if (_migrated)
        {
            return;
        }

        _migrated = true;
        Directory.CreateDirectory(_experiments);

        var legacyDraftPath = Path.Combine(_root, LegacyDraftFileName);
        if (!File.Exists(legacyDraftPath))
        {
            return;
        }

        try
        {
            await using var stream = new FileStream(legacyDraftPath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, useAsync: true);
            var legacyDocs = await JsonSerializer.DeserializeAsync<KlaExperimentDocument[]>(stream, KlaFingerprint.JsonOptions, cancellationToken).ConfigureAwait(false);
            if (legacyDocs is null || legacyDocs.Length == 0)
            {
                return;
            }

            var receiptsMap = new Dictionary<string, KlaPublishedProfile>(StringComparer.OrdinalIgnoreCase);
            if (Directory.Exists(_receipts))
            {
                foreach (var rPath in Directory.EnumerateFiles(_receipts, "*.kla.json"))
                {
                    try
                    {
                        var bytes = await File.ReadAllBytesAsync(rPath, cancellationToken).ConfigureAwait(false);
                        var profile = JsonSerializer.Deserialize<KlaPublishedProfile>(bytes, KlaFingerprint.JsonOptions);
                        if (profile is not null)
                        {
                            receiptsMap[profile.ReceiptFingerprint] = profile;
                        }
                    }
                    catch
                    {
                        // Ignora recibo ilegível
                    }
                }
            }

            foreach (var doc in legacyDocs)
            {
                var targetPath = GetExperimentFilePath(doc.Snapshot.Id);
                if (File.Exists(targetPath))
                {
                    continue;
                }

                var migratedDoc = doc;
                if (!string.IsNullOrEmpty(doc.LatestReceiptFingerprint) && receiptsMap.TryGetValue(doc.LatestReceiptFingerprint, out var matchingProfile))
                {
                    migratedDoc = doc with
                    {
                        Stage = KlaWorkflowStage.Published,
                        IsAvailableForControl = true,
                        LastPublishedAtUtc = matchingProfile.Payload.PublishedAtUtc,
                        PathData = new KlaPathData(
                            [],
                            matchingProfile.Payload.Allocation,
                            [],
                            0,
                            matchingProfile.Payload.PathDiagnostics,
                            matchingProfile.Payload.SurfaceFingerprint,
                            matchingProfile.Payload.PathFingerprint),
                        SurfaceData = new KlaSurfaceData(matchingProfile.Payload.SurfaceDiagnostics, matchingProfile.Payload.SurfaceFingerprint),
                    };
                }

                await SaveExperimentUnsafeAsync(migratedDoc, cancellationToken).ConfigureAwait(false);
            }

            // Preserva backup renomeando
            var backupPath = legacyDraftPath + ".bak";
            if (!File.Exists(backupPath))
            {
                File.Move(legacyDraftPath, backupPath);
            }
        }
        catch
        {
            // Falha na migração não impede inicialização
        }
    }

    public static KlaPublishedProfile ToPublishedProfile(KlaExperimentDocument doc)
    {
        var snapshot = doc.Snapshot;
        var allocation = doc.PathData?.Allocation ?? [];
        var pathDiag = doc.PathData?.Diagnostics ?? new KlaPathDiagnostics(
            0, 0, 0, 0, 0, 0, 0, 0, 0, 0, allocation.Length, []);
        var surfaceDiag = doc.SurfaceData?.Diagnostics ?? new KlaSurfaceDiagnostics(
            0, 0, 0, 0, 0, 0, true, []);

        var payload = new KlaPublicationPayload
        {
            ProfileId = snapshot.Id,
            ExperimentId = snapshot.Id,
            Version = 1,
            PublishedAtUtc = doc.LastPublishedAtUtc ?? doc.Snapshot.UpdatedAtUtc,
            Name = snapshot.Name.Trim(),
            Broth = snapshot.Broth.Trim(),
            RunCode = snapshot.RunCode.Trim(),
            Notes = snapshot.Notes.Trim(),
            ReviewNote = doc.ReviewNote,
            Domain = snapshot.Domain,
            Anchors = snapshot.Anchors,
            Algorithm = snapshot.Algorithm,
            AlgorithmIdentity = KlaMappingEngine.AlgorithmIdentity(snapshot.Algorithm),
            SurfaceFingerprint = doc.SurfaceData?.Fingerprint ?? "",
            PathFingerprint = doc.PathData?.Fingerprint ?? "",
            SurfaceDiagnostics = surfaceDiag,
            PathDiagnostics = pathDiag,
            Allocation = allocation,
        };

        var payloadBytes = JsonSerializer.SerializeToUtf8Bytes(payload, KlaFingerprint.JsonOptions);
        return new KlaPublishedProfile
        {
            Payload = payload,
            ReceiptFingerprint = doc.LatestReceiptFingerprint ?? KlaFingerprint.ForBytes(payloadBytes),
        };
    }
}
