using System.IO;
using OpenTECHub.Services.KlaTesting;
using OpenTECHub.Services.Persistence;
using Xunit;

namespace OpenTECHub.Tests;

public sealed class KlaRecipeOperationalProfileStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "recipe-profiles-" + Guid.NewGuid().ToString("N"));
    private readonly TestClock _clock = new(DateTimeOffset.UnixEpoch);
    private readonly BackgroundFileWriter _writer = new();
    private KlaRecipeOperationalProfileStore Store(string installation = "test-installation", bool isolated = true)
        => new(_root, installation, isolated, _clock, _writer);
    private KlaRecipeOperationalProfileRegistry Registry(string installation = "test-installation", bool isolated = true)
        => new(installation, _clock, isolated);

    [Fact]
    public async Task Reopens_both_protocols_with_identical_evidence_and_expiry_removes_availability_only()
    {
        var store = Store();
        foreach (var protocol in new[] { KlaAssayProtocol.Abiotic, KlaAssayProtocol.Biotic })
        {
            var profile = KlaRecipeOperationalProfileTests.Profile(_clock, protocol);
            await store.SaveAsync(profile); await store.SaveAsync(profile);
        }
        var reopened = Store(); var registry = Registry(); reopened.RegisterAvailable(registry);
        Assert.Equal(2, registry.AvailableProfiles.Count);
        Assert.All(registry.AvailableProfiles, p => Assert.Equal("isolated-profile", p.Capabilities.EvidenceId));
        _clock.Advance(TimeSpan.FromHours(25));
        var expiredRegistry = Registry(); reopened.RegisterAvailable(expiredRegistry);
        Assert.Empty(expiredRegistry.AvailableProfiles);
        Assert.Equal(2, reopened.ReadAll().Count);
    }

    [Fact]
    public async Task Same_version_cannot_be_rewritten_and_source_mutation_does_not_change_saved_profile()
    {
        var profile = KlaRecipeOperationalProfileTests.Profile(_clock); var store = Store();
        await store.SaveAsync(profile);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.SaveAsync(profile with { DispatchToleranceSeconds = 2 }));
        Assert.Equal(profile.DispatchToleranceSeconds, Store().ReadAll().Single().DispatchToleranceSeconds);
        var next = profile with { Capabilities = profile.Capabilities with { ProfileVersion = "2" },
            Quality = profile.Quality with { Version = "2" } };
        await store.SaveAsync(next);
        Assert.Equal(2, store.ReadAll().Count);
    }

    [Theory]
    [InlineData("hash")] [InlineData("duplicate")] [InlineData("unknown")] [InlineData("schema")]
    [InlineData("filename")] [InlineData("truncated")]
    public async Task Invalid_catalog_never_partially_registers_or_accepts_another_record(string corruption)
    {
        var store = Store();
        await store.SaveAsync(KlaRecipeOperationalProfileTests.Profile(_clock));
        await store.SaveAsync(KlaRecipeOperationalProfileTests.Profile(_clock, KlaAssayProtocol.Biotic));
        var path = Directory.GetFiles(_root, "profile-*.json").Order().Last();
        var json = File.ReadAllText(path);
        if (corruption == "filename") File.Move(path, Path.Combine(_root, "profile-renamed.json"));
        else File.WriteAllText(path, corruption switch
        {
            "hash" => json.Replace("isolated-profile", "changed-evidence"),
            "duplicate" => json.Insert(1, "\"SchemaVersion\":1,"),
            "unknown" => json.Insert(1, "\"UnknownSetting\":true,"),
            "schema" => json.Replace("\"SchemaVersion\":1", "\"SchemaVersion\":2"),
            _ => "{"
        });
        var registry = Registry();
        Assert.ThrowsAny<Exception>(() => store.RegisterAvailable(registry));
        Assert.Empty(registry.AvailableProfiles);
        var next = KlaRecipeOperationalProfileTests.Profile(_clock) with { DispatchToleranceSeconds = 2 };
        await Assert.ThrowsAnyAsync<Exception>(() => store.SaveAsync(next));
    }

    [Theory]
    [InlineData("other", true)] [InlineData("test-installation", false)]
    public async Task Saved_profile_cannot_enable_a_different_installation_or_physical_environment(string installation, bool isolated)
    {
        await Store().SaveAsync(KlaRecipeOperationalProfileTests.Profile(_clock));
        var registry = Registry(installation, isolated);
        Assert.Throws<InvalidOperationException>(() => Store().RegisterAvailable(registry));
        Assert.Throws<InvalidOperationException>(() => Store(installation, isolated).ReadAll());
        Assert.Empty(registry.AvailableProfiles);
    }

    [Fact]
    public async Task Failed_durable_write_and_competing_lease_do_not_publish_capabilities()
    {
        var profile = KlaRecipeOperationalProfileTests.Profile(_clock); Directory.CreateDirectory(_root);
        using (var lease = new FileStream(Path.Combine(_root, "profiles.lease"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
            await Assert.ThrowsAsync<IOException>(() => Store().SaveAsync(profile));
        _writer.Run(Path.Combine(_root, "failure"), () => throw new IOException("injected write failure"));
        var failure = await Assert.ThrowsAsync<AggregateException>(() => Store().SaveAsync(profile));
        Assert.IsType<IOException>(Assert.Single(failure.InnerExceptions));
        Assert.Empty(Directory.GetFiles(_root, "profile-*.json"));
    }

    [Fact]
    public void Registry_batch_conflict_preserves_previous_catalog_without_partial_additions()
    {
        var registry = Registry(); var original = KlaRecipeOperationalProfileTests.Profile(_clock);
        registry.Register(original);
        var another = KlaRecipeOperationalProfileTests.Profile(_clock, KlaAssayProtocol.Biotic);
        Assert.Throws<InvalidOperationException>(() => registry.RegisterMany([another, original with { DispatchToleranceSeconds = 2 }]));
        Assert.Single(registry.AvailableProfiles);
        Assert.Equal(KlaAssayProtocol.Abiotic, registry.AvailableProfiles.Single().Template.Protocol);
    }

    public void Dispose()
    {
        _writer.Dispose();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
