using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace TecnalHub.Services.Persistence;

/// <summary>Loads and saves <see cref="AppSettings"/>.</summary>
public interface ISettingsService
{
    /// <summary>The settings in force. Never null; falls back to defaults.</summary>
    AppSettings Current { get; }

    /// <summary>Raised after <see cref="Current"/> changes.</summary>
    event Action<AppSettings>? Changed;

    /// <summary>Replaces the settings and schedules a save.</summary>
    void Update(Func<AppSettings, AppSettings> mutate);

    /// <summary>Writes immediately, e.g. on shutdown.</summary>
    Task SaveNowAsync();
}

/// <summary>
/// JSON-backed settings, written to <c>%APPDATA%\TECNAL-Hub\settings.json</c>.
/// </summary>
/// <remarks>
/// <para>
/// Saves are <b>debounced</b>: typing in a setpoint field would otherwise rewrite the
/// file on every keystroke.
/// </para>
/// <para>
/// A corrupt or unreadable file falls back to defaults rather than failing to start.
/// The controller must always come up - an operator who cannot launch the app cannot
/// reach the equipment either. The bad file is renamed rather than deleted, so it can
/// still be inspected.
/// </para>
/// </remarks>
public sealed class SettingsService : ISettingsService, IAsyncDisposable
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        // Settings files get hand-edited in the field; a trailing comma or a stray
        // comment should not cost someone their configuration.
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private static readonly TimeSpan SaveDebounce = TimeSpan.FromMilliseconds(750);

    private readonly ILogger<SettingsService> _log;
    private readonly string _path;
    private readonly Lock _gate = new();

    private CancellationTokenSource? _pendingSave;

    public SettingsService(ILogger<SettingsService> log, string? path = null)
    {
        _log = log;
        _path = path ?? AppPaths.SettingsFile;
        Current = Load();
    }

    public AppSettings Current { get; private set; }

    public event Action<AppSettings>? Changed;

    public void Update(Func<AppSettings, AppSettings> mutate)
    {
        ArgumentNullException.ThrowIfNull(mutate);

        AppSettings updated;
        lock (_gate)
        {
            updated = mutate(Current);
            if (updated == Current)
            {
                return; // records compare structurally; nothing actually changed
            }

            Current = updated;
        }

        Changed?.Invoke(updated);
        ScheduleSave();
    }

    public async Task SaveNowAsync()
    {
        CancelPendingSave();

        AppSettings snapshot;
        lock (_gate)
        {
            snapshot = Current;
        }

        await WriteAsync(snapshot, CancellationToken.None).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        await SaveNowAsync().ConfigureAwait(false);
        CancelPendingSave();
    }

    private AppSettings Load()
    {
        if (!File.Exists(_path))
        {
            _log.LogInformation("No settings file at {Path}; starting from defaults", _path);
            return new AppSettings();
        }

        try
        {
            var json = File.ReadAllText(_path);
            var loaded = JsonSerializer.Deserialize<AppSettings>(json, SerializerOptions);

            if (loaded is null)
            {
                _log.LogWarning("Settings file {Path} deserialised to null; using defaults", _path);
                return new AppSettings();
            }

            _log.LogInformation("Loaded settings v{Version} from {Path}", loaded.Version, _path);
            return loaded;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // Keep the bad file for diagnosis instead of overwriting it on the next save.
            QuarantineCorruptFile(ex);
            return new AppSettings();
        }
    }

    private void QuarantineCorruptFile(Exception cause)
    {
        var quarantine = _path + ".corrupt-" +
                         DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture);
        try
        {
            File.Move(_path, quarantine);
            _log.LogError(cause,
                "Settings file was unreadable; moved to {Quarantine} and continuing with defaults",
                quarantine);
        }
        catch (Exception moveFailure)
        {
            _log.LogError(moveFailure,
                "Settings file was unreadable and could not be set aside; continuing with defaults");
        }
    }

    private void ScheduleSave()
    {
        CancelPendingSave();

        var cts = new CancellationTokenSource();
        _pendingSave = cts;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(SaveDebounce, cts.Token).ConfigureAwait(false);

                AppSettings snapshot;
                lock (_gate)
                {
                    snapshot = Current;
                }

                await WriteAsync(snapshot, cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Superseded by a newer change; the newer save will land.
            }
        }, CancellationToken.None);
    }

    private void CancelPendingSave()
    {
        var pending = Interlocked.Exchange(ref _pendingSave, null);
        if (pending is null)
        {
            return;
        }

        try
        {
            pending.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Already completed.
        }

        pending.Dispose();
    }

    /// <remarks>
    /// Writes to a temporary file and moves it into place, so a crash mid-write
    /// cannot leave a half-written settings file behind.
    /// </remarks>
    private async Task WriteAsync(AppSettings settings, CancellationToken token)
    {
        try
        {
            var json = JsonSerializer.Serialize(settings, SerializerOptions);
            var temporary = _path + ".tmp";

            await File.WriteAllTextAsync(temporary, json, token).ConfigureAwait(false);
            File.Move(temporary, _path, overwrite: true);

            _log.LogDebug("Settings saved to {Path}", _path);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Losing a preference is annoying; failing to run is not acceptable.
            _log.LogError(ex, "Could not save settings to {Path}", _path);
        }
    }
}
