using System.IO;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using OpenTECHub.Services.Persistence;
using Xunit;

namespace OpenTECHub.Tests;

public sealed class SettingsSaveConcurrencyTests
{
    [Fact]
    public async Task RapidUpdatesAndImmediateSaveKeepLatestSettingsWithoutDeferredFaults()
    {
        var directory = Path.Combine(Path.GetTempPath(), "settings-race-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "settings.json");
        var logger = new ErrorLogger();
        try
        {
            await using var service = new SettingsService(logger, path);
            for (var index = 0; index < 200; index++)
                service.Update(settings => settings with { Version = settings.Version + 1 });
            await service.SaveNowAsync();
            await Task.Delay(1000);
            var saved = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path),
                new JsonSerializerOptions { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } });
            Assert.Equal(service.Current.Version, saved!.Version);
            Assert.Empty(logger.Errors);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task ConcurrentUpdatesAndShutdownSaveDoNotRaceOnTemporaryFile()
    {
        var directory = Path.Combine(Path.GetTempPath(), "settings-shutdown-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "settings.json");
        var logger = new ErrorLogger();
        try
        {
            var service = new SettingsService(logger, path);
            await Task.WhenAll(Enumerable.Range(0, 100).Select(_ => Task.Run(async () =>
            {
                service.Update(settings => settings with { Version = settings.Version + 1 });
                await service.SaveNowAsync();
            })));
            await service.DisposeAsync();
            await Task.Delay(1000);
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            Assert.Equal(service.Current.Version, document.RootElement.GetProperty("Version").GetInt32());
            Assert.Empty(logger.Errors);
        }
        finally { Directory.Delete(directory, true); }
    }

    private sealed class ErrorLogger : ILogger<SettingsService>
    {
        public System.Collections.Concurrent.ConcurrentQueue<Exception> Errors { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (level >= LogLevel.Error) Errors.Enqueue(exception ?? new Exception(formatter(state, exception)));
        }
    }
}
