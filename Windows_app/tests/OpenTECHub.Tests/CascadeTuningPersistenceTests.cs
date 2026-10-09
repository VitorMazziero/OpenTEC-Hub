using System.IO;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using OpenTECHub.Services.Communication;
using OpenTECHub.Services.Control;
using OpenTECHub.Services.Persistence;
using OpenTECHub.ViewModels;
using Xunit;

namespace OpenTECHub.Tests;

/// <summary>D-071: what the oxygen page applies is what a reopened workspace loads, for every mode.</summary>
public sealed class CascadeTuningPersistenceTests
{
    [Fact]
    public async Task Applied_gains_of_every_mode_survive_saving_and_reopening_the_workspace()
    {
        var path = NewSettingsFile(JsonSerializer.Serialize(new AppSettings()));
        await using (var settings = new SettingsService(NullLogger<SettingsService>.Instance, path))
        {
            using var vm = OpenPage(settings, out var service);
            vm.SelectedMode = vm.Modes.Single(m => m.Mode == CascadeMode.AgitationOnly);
            vm.KdText = "0.75";
            vm.IMinText = "-200";
            vm.IMaxText = "200";
            vm.SelectedMode = vm.Modes.Single(m => m.Mode == CascadeMode.DualCascade);
            vm.KdText = "3.5";
            vm.IMinText = "-2.5";
            vm.IMaxText = "2.5";
            vm.ApplyCommand.Execute(null);
            Assert.True(vm.DialogResult, vm.ValidationError);
            Assert.Equal(3.5, service.Tuning.Kd); // live at once, not only on disk
            service.Dispose();
            await settings.SaveNowAsync();
        }

        // Reopened twice: a value lost on load would also be written back lost.
        for (var reopen = 0; reopen < 2; reopen++)
        {
            await using var reloaded = new SettingsService(NullLogger<SettingsService>.Instance, path);
            var cascade = reloaded.Current.Cascade;
            Assert.Equal((3.5, -2.5, 2.5), (cascade.CascadePid.Kd, cascade.CascadePid.IMin, cascade.CascadePid.IMax));
            Assert.Equal((0.75, -200.0, 200.0), (cascade.AgitationPid.Kd, cascade.AgitationPid.IMin, cascade.AgitationPid.IMax));
            Assert.Equal(new CascadeSettings().AerationPid, cascade.AerationPid);
            Assert.Equal(new CascadeSettings().MapPid, cascade.MapPid);
            await reloaded.SaveNowAsync();
        }

        var json = await File.ReadAllTextAsync(path);
        using var document = JsonDocument.Parse(json);
        var written = document.RootElement.GetProperty("Cascade");
        foreach (var legacy in new[] { "Kp", "Ki", "Kd", "IntegralMin", "IntegralMax", "PredictionHorizonSeconds", "IntervalSeconds" })
        {
            Assert.False(written.TryGetProperty(legacy, out _), $"The legacy field {legacy} is still written.");
        }
    }

    [Fact]
    public void A_file_with_both_layouts_keeps_the_per_mode_pids()
    {
        // The layout every workspace saved before D-071 has: per-mode blocks, then the flat copy of the cascade.
        const string json = """
            { "Cascade": {
                "AgitationPid": { "Kp": 0.10, "Kd": 0.75, "IMin": -200, "IMax": 200, "TPred": 30 },
                "CascadePid":   { "Kp": 0.035, "Kd": 3.5, "IMin": -2.5, "IMax": 2.5, "TPred": 60 },
                "Kp": 0.035, "Ki": 0.001, "Kd": 3.5, "IntegralMin": -2.5, "IntegralMax": 2.5,
                "PredictionHorizonSeconds": 60, "RateWindowSeconds": 25, "IntervalSeconds": 3 } }
            """;

        var cascade = LoadFrom(json);

        Assert.Equal((0.10, 0.75, -200.0, 200.0, 30.0),
            (cascade.AgitationPid.Kp, cascade.AgitationPid.Kd, cascade.AgitationPid.IMin, cascade.AgitationPid.IMax, cascade.AgitationPid.TPred));
        Assert.Equal((0.035, 3.5, -2.5, 2.5, 60.0),
            (cascade.CascadePid.Kp, cascade.CascadePid.Kd, cascade.CascadePid.IMin, cascade.CascadePid.IMax, cascade.CascadePid.TPred));
        Assert.Equal(new CascadeSettings().AerationPid, cascade.AerationPid);
    }

    [Fact]
    public void A_file_from_before_the_per_mode_pids_still_migrates_into_the_cascade()
    {
        const string json = """
            { "Cascade": { "Kp": 0.05, "Ki": 0.002, "Kd": 0.8, "IntegralMin": -10, "IntegralMax": 10,
                           "PredictionHorizonSeconds": 45, "IntervalSeconds": 2 } }
            """;

        var cascade = LoadFrom(json);

        Assert.Equal((0.05, 0.002, 0.8, -10.0, 10.0, 45.0, 2.0),
            (cascade.CascadePid.Kp, cascade.CascadePid.Ki, cascade.CascadePid.Kd, cascade.CascadePid.IMin,
             cascade.CascadePid.IMax, cascade.CascadePid.TPred, cascade.CascadePid.IntervalSeconds));
        Assert.Equal(new CascadeSettings().AgitationPid, cascade.AgitationPid);
    }

    [Fact]
    public void Reopening_and_applying_the_page_does_not_round_a_saved_gain()
    {
        var settings = new MemorySettingsService(new AppSettings
        {
            Cascade = new CascadeSettings
            {
                CascadePid = new CascadeSettings().CascadePid with { Kp = 0.0355, Ki = 0.00005, Kd = 3.25, TauD = 32.5, IMax = 2.75 },
            },
        });
        var before = settings.Current.Cascade.CascadePid;

        using var vm = OpenPage(settings, out var service);
        using (service)
        {
            Assert.Equal(("0.0355", "0.00005", "3.250", "32.5", "2.75"), (vm.KpText, vm.KiText, vm.KdText, vm.TauDText, vm.IMaxText));
            vm.ApplyCommand.Execute(null);
        }

        Assert.Equal(before, settings.Current.Cascade.CascadePid);
    }

    private static OxygenConfigViewModel OpenPage(ISettingsService settings, out CascadeService service)
    {
        var clock = new TestClock(DateTimeOffset.UnixEpoch);
        var arbiter = new CommandArbiter(new RecordingDeviceService(), clock);
        var store = new FakeKlaProfileStore();
        service = new CascadeService(arbiter, arbiter, settings, store, clock);
        service.SelectMode(CascadeMode.DualCascade);
        return new OxygenConfigViewModel(service, settings, store);
    }

    private static CascadeSettings LoadFrom(string json)
    {
        var path = NewSettingsFile(json);
        var settings = new SettingsService(NullLogger<SettingsService>.Instance, path);
        return settings.Current.Cascade;
    }

    private static string NewSettingsFile(string json)
    {
        var directory = Path.Combine(Path.GetTempPath(), "OpenTEC-CascadeTuning-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "settings.json");
        File.WriteAllText(path, json);
        return path;
    }
}
