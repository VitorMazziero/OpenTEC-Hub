using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using OpenTECHub.Services.Persistence;
using OpenTECHub.Services.PowerTesting;

namespace OpenTECHub.Services.PowerMapping;

/// <summary>
/// On-disk store for power maps and impeller comparisons under <c>Mapas-Potencia/</c>.
/// Atomic file operations with Windows file-lock transient retries.
/// </summary>
public sealed class PowerMapStore : IPowerMapStore
{
    private readonly string _rootDirectory;
    private readonly object _ioLock = new();

    public PowerMapStore(string? rootDirectory = null)
    {
        _rootDirectory = rootDirectory ?? AppPaths.PowerMapsDirectory;
        Directory.CreateDirectory(_rootDirectory);
    }

    public string RootDirectory => _rootDirectory;

    public bool ValidateMapName(string name, out string? error) =>
        PowerMapFileContracts.ValidateMapName(name, out error);

    public bool MapExists(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        var folderName = PowerMapFileContracts.SanitizeFolderName(name);
        return Directory.Exists(Path.Combine(_rootDirectory, folderName)) ||
               Directory.Exists(Path.Combine(_rootDirectory, name.Trim()));
    }

    public IReadOnlyList<PowerMapSummary> ListMaps()
    {
        lock (_ioLock)
        {
            if (!Directory.Exists(_rootDirectory))
            {
                return [];
            }

            var list = new List<PowerMapSummary>();
            foreach (var dir in Directory.GetDirectories(_rootDirectory))
            {
                var folderName = Path.GetFileName(dir);
                var manifestPath = Path.Combine(dir, PowerMapFileContracts.MapManifestFileName);

                if (File.Exists(manifestPath))
                {
                    try
                    {
                        var doc = PowerMapFileContracts.DeserializeMapDocument(File.ReadAllText(manifestPath));
                        if (doc is not null)
                        {
                            list.Add(new PowerMapSummary(
                                folderName,
                                doc.Name,
                                doc.MapId,
                                doc.CreatedAtUtc,
                                doc.UpdatedAtUtc,
                                doc.SourceTestIds.Count,
                                doc.SurfaceData is not null,
                                doc.KlaCorrelation is not null,
                                doc.KlaCorrelation?.R2));
                        }
                    }
                    catch
                    {
                        // Broken or incomplete manifest skipped safely
                    }
                }
            }

            return list.OrderByDescending(m => m.UpdatedAtUtc).ToList();
        }
    }

    public PowerMapDocument CreateMap(
        string name,
        IReadOnlyList<Guid> sourceTestIds,
        IReadOnlyList<string>? sourceTestNames = null,
        FluidProperties? fluid = null,
        PowerGeometry? geometry = null,
        string? notes = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        lock (_ioLock)
        {
            var folderName = PowerMapFileContracts.SanitizeFolderName(name);
            var mapDir = Path.Combine(_rootDirectory, folderName);

            // If folder exists with different case or prefix, append suffix
            if (Directory.Exists(mapDir))
            {
                folderName = $"{folderName}_{DateTimeOffset.UtcNow:yyyyMMdd_HHmmss}";
                mapDir = Path.Combine(_rootDirectory, folderName);
            }

            Directory.CreateDirectory(mapDir);

            var doc = new PowerMapDocument
            {
                MapId = Guid.NewGuid(),
                Name = name.Trim(),
                FolderName = folderName,
                CreatedAtUtc = DateTimeOffset.UtcNow,
                UpdatedAtUtc = DateTimeOffset.UtcNow,
                SourceTestIds = sourceTestIds ?? [],
                SourceTestNames = sourceTestNames ?? [],
                Fluid = fluid ?? new FluidProperties { DensityKgM3 = 1000.0, ViscosityPaS = 0.001 },
                Geometry = geometry ?? new PowerGeometry(),
                Notes = notes ?? "",
            };

            var manifestPath = Path.Combine(mapDir, PowerMapFileContracts.MapManifestFileName);
            var json = PowerMapFileContracts.SerializeMapDocument(doc);
            PowerMapFileContracts.WriteAllTextAtomic(manifestPath, json);

            return doc;
        }
    }

    public PowerMapDocument? LoadMap(string nameOrId)
    {
        if (string.IsNullOrWhiteSpace(nameOrId))
        {
            return null;
        }

        lock (_ioLock)
        {
            if (!Directory.Exists(_rootDirectory))
            {
                return null;
            }

            var trimmed = nameOrId.Trim();

            // 1. Match direct folder name
            var directDir = Path.Combine(_rootDirectory, trimmed);
            if (Directory.Exists(directDir))
            {
                var manifest = Path.Combine(directDir, PowerMapFileContracts.MapManifestFileName);
                if (File.Exists(manifest))
                {
                    return PowerMapFileContracts.DeserializeMapDocument(File.ReadAllText(manifest));
                }
            }

            // 2. Search through all subdirectories
            foreach (var dir in Directory.GetDirectories(_rootDirectory))
            {
                var folderName = Path.GetFileName(dir);
                var manifest = Path.Combine(dir, PowerMapFileContracts.MapManifestFileName);
                if (!File.Exists(manifest))
                {
                    continue;
                }

                try
                {
                    var doc = PowerMapFileContracts.DeserializeMapDocument(File.ReadAllText(manifest));
                    if (doc is null)
                    {
                        continue;
                    }

                    if (string.Equals(folderName, trimmed, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(doc.Name, trimmed, StringComparison.OrdinalIgnoreCase) ||
                        (Guid.TryParse(trimmed, out var id) && doc.MapId == id))
                    {
                        return doc;
                    }
                }
                catch
                {
                    // Ignore corrupted directory
                }
            }

            return null;
        }
    }

    public void SaveMap(PowerMapDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        lock (_ioLock)
        {
            var folderName = string.IsNullOrWhiteSpace(document.FolderName)
                ? PowerMapFileContracts.SanitizeFolderName(document.Name)
                : document.FolderName;

            var mapDir = Path.Combine(_rootDirectory, folderName);
            Directory.CreateDirectory(mapDir);

            var updated = document with
            {
                FolderName = folderName,
                UpdatedAtUtc = DateTimeOffset.UtcNow,
            };

            var manifestPath = Path.Combine(mapDir, PowerMapFileContracts.MapManifestFileName);
            var json = PowerMapFileContracts.SerializeMapDocument(updated);
            PowerMapFileContracts.WriteAllTextAtomic(manifestPath, json);

            // Also write accompanying CSVs if relevant
            if (updated.KlaCorrelation is not null && updated.KlaPairs.Count > 0)
            {
                var klaCsvPath = Path.Combine(mapDir, PowerMapFileContracts.KlaCorrelationSummaryFileName);
                var csv = PowerMapFileContracts.BuildKlaCorrelationCsv(updated.KlaCorrelation, updated.KlaPairs);
                PowerMapFileContracts.WriteAllTextAtomic(klaCsvPath, csv);
            }

            if (updated.SurfaceData is not null)
            {
                var surfaceCsvPath = Path.Combine(mapDir, PowerMapFileContracts.SurfaceGridCsvFileName);
                var csv = PowerMapFileContracts.BuildSurfaceGridCsv(updated.SurfaceData);
                PowerMapFileContracts.WriteAllTextAtomic(surfaceCsvPath, csv);
            }
        }
    }

    public bool DeleteMap(string nameOrId)
    {
        if (string.IsNullOrWhiteSpace(nameOrId))
        {
            return false;
        }

        lock (_ioLock)
        {
            var doc = LoadMap(nameOrId);
            if (doc is null)
            {
                return false;
            }

            var dir = Path.Combine(_rootDirectory, doc.FolderName);
            if (Directory.Exists(dir))
            {
                try
                {
                    Directory.Delete(dir, recursive: true);
                    return true;
                }
                catch
                {
                    return false;
                }
            }

            return false;
        }
    }

    public IReadOnlyList<ImpellerComparisonDocument> ListComparisons()
    {
        lock (_ioLock)
        {
            if (!Directory.Exists(_rootDirectory))
            {
                return [];
            }

            var list = new List<ImpellerComparisonDocument>();
            foreach (var dir in Directory.GetDirectories(_rootDirectory))
            {
                var compFile = Path.Combine(dir, PowerMapFileContracts.ImpellerComparisonFileName);
                if (File.Exists(compFile))
                {
                    try
                    {
                        var doc = PowerMapFileContracts.DeserializeComparisonDocument(File.ReadAllText(compFile));
                        if (doc is not null)
                        {
                            list.Add(doc);
                        }
                    }
                    catch
                    {
                        // Ignore unparseable
                    }
                }
            }

            return list.OrderByDescending(c => c.UpdatedAtUtc).ToList();
        }
    }

    public ImpellerComparisonDocument CreateComparison(
        string name,
        IReadOnlyList<Guid> selectedTestIds,
        IReadOnlyList<ImpellerComparisonItem> items,
        string? notes = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        lock (_ioLock)
        {
            var folderName = "Comp_" + PowerMapFileContracts.SanitizeFolderName(name);
            var compDir = Path.Combine(_rootDirectory, folderName);

            if (Directory.Exists(compDir))
            {
                folderName = $"{folderName}_{DateTimeOffset.UtcNow:yyyyMMdd_HHmmss}";
                compDir = Path.Combine(_rootDirectory, folderName);
            }

            Directory.CreateDirectory(compDir);

            var doc = new ImpellerComparisonDocument
            {
                ComparisonId = Guid.NewGuid(),
                Name = name.Trim(),
                FolderName = folderName,
                CreatedAtUtc = DateTimeOffset.UtcNow,
                UpdatedAtUtc = DateTimeOffset.UtcNow,
                SelectedTestIds = selectedTestIds ?? [],
                Items = items ?? [],
                CompatibilityNotes = notes is not null ? [notes] : [],
            };

            var filePath = Path.Combine(compDir, PowerMapFileContracts.ImpellerComparisonFileName);
            var json = PowerMapFileContracts.SerializeComparisonDocument(doc);
            PowerMapFileContracts.WriteAllTextAtomic(filePath, json);

            return doc;
        }
    }

    public ImpellerComparisonDocument? LoadComparison(string nameOrId)
    {
        if (string.IsNullOrWhiteSpace(nameOrId))
        {
            return null;
        }

        lock (_ioLock)
        {
            if (!Directory.Exists(_rootDirectory))
            {
                return null;
            }

            var trimmed = nameOrId.Trim();

            foreach (var dir in Directory.GetDirectories(_rootDirectory))
            {
                var compFile = Path.Combine(dir, PowerMapFileContracts.ImpellerComparisonFileName);
                if (!File.Exists(compFile))
                {
                    continue;
                }

                try
                {
                    var doc = PowerMapFileContracts.DeserializeComparisonDocument(File.ReadAllText(compFile));
                    if (doc is null)
                    {
                        continue;
                    }

                    if (string.Equals(Path.GetFileName(dir), trimmed, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(doc.Name, trimmed, StringComparison.OrdinalIgnoreCase) ||
                        (Guid.TryParse(trimmed, out var id) && doc.ComparisonId == id))
                    {
                        return doc;
                    }
                }
                catch
                {
                    // Ignore unparseable
                }
            }

            return null;
        }
    }

    public void SaveComparison(ImpellerComparisonDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        lock (_ioLock)
        {
            var folderName = string.IsNullOrWhiteSpace(document.FolderName)
                ? "Comp_" + PowerMapFileContracts.SanitizeFolderName(document.Name)
                : document.FolderName;

            var compDir = Path.Combine(_rootDirectory, folderName);
            Directory.CreateDirectory(compDir);

            var updated = document with
            {
                FolderName = folderName,
                UpdatedAtUtc = DateTimeOffset.UtcNow,
            };

            var filePath = Path.Combine(compDir, PowerMapFileContracts.ImpellerComparisonFileName);
            var json = PowerMapFileContracts.SerializeComparisonDocument(updated);
            PowerMapFileContracts.WriteAllTextAtomic(filePath, json);
        }
    }

    public bool DeleteComparison(string nameOrId)
    {
        if (string.IsNullOrWhiteSpace(nameOrId))
        {
            return false;
        }

        lock (_ioLock)
        {
            var doc = LoadComparison(nameOrId);
            if (doc is null)
            {
                return false;
            }

            var dir = Path.Combine(_rootDirectory, doc.FolderName);
            if (Directory.Exists(dir))
            {
                try
                {
                    Directory.Delete(dir, recursive: true);
                    return true;
                }
                catch
                {
                    return false;
                }
            }

            return false;
        }
    }
}
