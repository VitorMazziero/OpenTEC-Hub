using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using OpenTECHub.Services.Calibration;
using OpenTECHub.Services.Persistence;
using Xunit;

namespace OpenTECHub.Tests;

[Collection("AppPaths")]
public sealed class PumpCalibrationProfileStoreTests : IDisposable
{
    private readonly string _testRoot;
    private readonly IDisposable _overrideScope;

    private static PumpDualRangeCurve TestCurve(double slope, double intercept, double transitionSpeed = 500.0)
    {
        var line = new PolynomialCalibration(0.0, slope, intercept);
        return new PumpDualRangeCurve(line, line, transitionSpeed);
    }

    public PumpCalibrationProfileStoreTests()
    {
        _testRoot = Path.Combine(Path.GetTempPath(), $"opentechub-pumpprofiles-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testRoot);
        _overrideScope = AppPaths.OverrideForTests(_testRoot);
    }

    public void Dispose()
    {
        _overrideScope.Dispose();

        try
        {
            if (Directory.Exists(_testRoot))
            {
                Directory.Delete(_testRoot, recursive: true);
            }
        }
        catch
        {
            // Best effort temp cleanup
        }
    }

    [Fact]
    public void SaveProfile_And_LoadProfile_PersistsAllProperties()
    {
        var store = new PumpCalibrationProfileStore();
        var curve = TestCurve(0.025, 1.25, 450.0);

        var points = new[]
        {
            new PumpCalibrationPoint { SpeedUnits = 100, Seconds = 60, VolumeMl = 2.5, CapturedAtUtc = "2026-09-13T12:00:00Z" },
            new PumpCalibrationPoint { SpeedUnits = 600, Seconds = 60, VolumeMl = 18.0, CapturedAtUtc = "2026-09-13T12:05:00Z" },
        };

        var stats = new PumpFitStatistics { RSquared = 0.998, RMSE = 0.12, SSE = 0.05, TotalPoints = 2 };

        var profile = PumpCalibrationProfile.FromCurve(
            name: "Mangueira Silicone 2mm",
            curve: curve,
            points: points,
            fitStatistics: stats,
            notes: "Tubo novo recém-instalado") with
        {
            AlgorithmVersion = "1.0-test",
            LastAppliedPumpFirmware = "3.11"
        };

        store.SaveProfile(profile);

        Assert.True(store.ProfileExists("Mangueira Silicone 2mm"));

        var loaded = store.LoadProfile("Mangueira Silicone 2mm");
        Assert.NotNull(loaded);
        Assert.Equal("Mangueira Silicone 2mm", loaded.Name);
        Assert.Equal(PumpCalibrationProfile.CurrentSchemaVersion, loaded.SchemaVersion);
        Assert.Equal("Tubo novo recém-instalado", loaded.OptionalNotes);
        Assert.Equal("1.0-test", loaded.AlgorithmVersion);
        Assert.Equal("3.11", loaded.LastAppliedPumpFirmware);

        var loadedCurve = loaded.ToCurve();
        Assert.Equal(curve.LowSlope, loadedCurve.LowSlope);
        Assert.Equal(curve.HighSlope, loadedCurve.HighSlope);
        Assert.Equal(curve.TransitionSpeed, loadedCurve.TransitionSpeed);
        Assert.Equal(curve.TransitionFlow, loadedCurve.TransitionFlow);

        Assert.Equal(2, loaded.CalibrationPoints.Length);
        Assert.Equal(100, loaded.CalibrationPoints[0].SpeedUnits);
        Assert.Equal(2.5, loaded.CalibrationPoints[0].VolumeMl);

        Assert.NotNull(loaded.FitStatistics);
        Assert.Equal(0.998, loaded.FitStatistics.RSquared);
        Assert.Equal(0.12, loaded.FitStatistics.RMSE);
    }

    [Fact]
    public void SaveProfile_WithoutOverwrite_ThrowsIfAlreadyExists()
    {
        var store = new PumpCalibrationProfileStore();
        var curve = TestCurve(0.028, 1.5);
        var profile = PumpCalibrationProfile.FromCurve("Perfil Existente", curve);

        store.SaveProfile(profile, overwrite: false);

        Assert.Throws<InvalidOperationException>(() =>
            store.SaveProfile(profile, overwrite: false));

        // Overwrite true succeeds
        var updatedProfile = profile with { OptionalNotes = "Atualizado com sucesso" };
        store.SaveProfile(updatedProfile, overwrite: true);

        var loaded = store.LoadProfile("Perfil Existente");
        Assert.NotNull(loaded);
        Assert.Equal("Atualizado com sucesso", loaded.OptionalNotes);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("../perfil")]
    [InlineData("..\\perfil")]
    [InlineData("sub/perfil")]
    [InlineData("sub\\perfil")]
    [InlineData("perfil*")]
    [InlineData("perfil?")]
    [InlineData("perfil:")]
    [InlineData("perfil<")]
    [InlineData("perfil>")]
    [InlineData("perfil|")]
    [InlineData("perfil\"")]
    [InlineData("perfil.")]
    [InlineData("perfil ")]
    [InlineData("CON")]
    [InlineData("con")]
    [InlineData("PRN")]
    [InlineData("AUX")]
    [InlineData("NUL")]
    [InlineData("COM1")]
    [InlineData("COM9")]
    [InlineData("LPT1")]
    public void FilenameValidation_RejectsInvalidNames(string invalidName)
    {
        var store = new PumpCalibrationProfileStore();
        var curve = TestCurve(0.028, 1.5);
        var profile = new PumpCalibrationProfile
        {
            Name = invalidName,
            LowSlope = curve.LowSlope,
            HighSlope = curve.HighSlope,
            TransitionSpeedUnits = curve.TransitionSpeed,
            TransitionFlowMlMin = curve.TransitionFlow
        };

        Assert.False(PumpProfileFileContracts.ValidateProfileName(invalidName, out _));
        Assert.False(store.ProfileExists(invalidName));
        Assert.Null(store.LoadProfile(invalidName));
        Assert.Throws<ArgumentException>(() => store.SaveProfile(profile));
        Assert.False(store.DeleteProfile(invalidName));
    }

    [Fact]
    public void ListProfiles_ReturnsAllProfiles_AndSafelyFlagsCorruptFiles()
    {
        var store = new PumpCalibrationProfileStore();

        // 1. Save two valid profiles
        var curve1 = TestCurve(0.025, 1.0);
        store.SaveProfile(PumpCalibrationProfile.FromCurve("Perfil 1", curve1));

        var curve2 = TestCurve(0.030, 2.0);
        store.SaveProfile(PumpCalibrationProfile.FromCurve("Perfil 2", curve2));

        // 2. Introduce a corrupt JSON file directly in the directory
        var corruptFilePath = Path.Combine(store.ProfilesDirectory, "Corrompido.json");
        File.WriteAllText(corruptFilePath, "{ this is not valid json }");

        var summaries = store.ListProfiles();

        Assert.Equal(2, summaries.Count);

        var valid1 = summaries.FirstOrDefault(s => s.Name == "Perfil 1");
        Assert.NotNull(valid1);
        Assert.True(valid1.IsCompatible);
        Assert.Equal(0.025, valid1.LowSlope);

        var valid2 = summaries.FirstOrDefault(s => s.Name == "Perfil 2");
        Assert.NotNull(valid2);
        Assert.True(valid2.IsCompatible);
        Assert.Equal(0.030, valid2.LowSlope);

        // Loading corrupt file throws InvalidOperationException
        Assert.Throws<InvalidOperationException>(() => store.LoadProfile("Corrompido"));
    }

    [Fact]
    public void NonCurrentSchemaVersion_MarksIncompatible_AndBlocksOverwrite()
    {
        var store = new PumpCalibrationProfileStore();

        var futureDoc = new
        {
            schemaVersion = 99,
            profileId = Guid.NewGuid().ToString("D"),
            name = "Futuro",
            createdUtc = DateTimeOffset.UtcNow,
            modifiedUtc = DateTimeOffset.UtcNow,
            transitionFlowMlMin = 10.0,
            transitionSpeedUnits = 500.0,
            lowSlope = 0.02,
            highSlope = 0.02,
            calibrationPoints = Array.Empty<object>()
        };
        var futureJson = JsonSerializer.Serialize(futureDoc);
        var path = Path.Combine(store.ProfilesDirectory, "Futuro.json");
        File.WriteAllText(path, futureJson);

        var summaries = store.ListProfiles();
        var summary = Assert.Single(summaries);
        Assert.Equal("Futuro", summary.Name);
        Assert.False(summary.IsCompatible);

        // LoadProfile rejects future schema
        Assert.Throws<InvalidOperationException>(() => store.LoadProfile("Futuro"));

        // SaveProfile blocks overwriting incompatible future file even if overwrite = true
        var localProfile = PumpCalibrationProfile.FromCurve("Futuro", TestCurve(0.025, 1.0));
        Assert.Throws<InvalidOperationException>(() => store.SaveProfile(localProfile, overwrite: true));
    }

    [Fact]
    public void DeleteProfile_RemovesFile_AndReturnsStatus()
    {
        var store = new PumpCalibrationProfileStore();
        var curve = TestCurve(0.028, 1.5);
        store.SaveProfile(PumpCalibrationProfile.FromCurve("Para Deletar", curve));

        Assert.True(store.ProfileExists("Para Deletar"));
        Assert.True(store.DeleteProfile("Para Deletar"));
        Assert.False(store.ProfileExists("Para Deletar"));
        Assert.False(store.DeleteProfile("Para Deletar"));
    }

    [Fact]
    public void SaveProfile_ValidatesCurve_ThrowsArgumentExceptionWhenInvalid()
    {
        var store = new PumpCalibrationProfileStore();
        var invalidProfile = new PumpCalibrationProfile
        {
            Name = "Curva Invalida",
            LowSlope = -0.05, // Negative slope is physically invalid
            HighSlope = 0.02,
            TransitionSpeedUnits = 500.0,
            TransitionFlowMlMin = 10.0
        };

        Assert.Throws<ArgumentException>(() => store.SaveProfile(invalidProfile));
    }

}
