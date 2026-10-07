using System.IO;
using OpenTECHub.Services.Persistence;
using OpenTECHub.Services.Recipes;

namespace OpenTECHub.Services.KlaTesting;

/// <summary>Application-owned journal, profile catalog and context shared by every recipe invocation.</summary>
public sealed class KlaRecipeApplicationHost : IRecipeAutonomousWorkSource, IDisposable
{
    private readonly string _root;
    private readonly TimeProvider _time;
    private readonly BackgroundFileWriter _writer;
    private readonly KlaRecipeAssayExecutionFactory _factory;
    private readonly IKlaTestStore _store;
    private readonly ISettingsService _settings;
    private readonly KlaRecipeExecutionRouter _router;
    private KlaAssayApi? _api;
    private KlaRecipeAutonomousWorkSource? _source;
    public KlaRecipeApplicationContext? Context { get; }
    public KlaRecipeOperationalProfileRegistry Profiles { get; }
    public KlaRecipeOperationalProfileStore ProfileStore { get; }
    public string? AvailabilityError { get; private set; }
    public IReadOnlyList<KlaRecipeActiveInvocation> ActiveInvocations => _source?.ActiveInvocations ?? [];

    public KlaRecipeApplicationHost(string root, bool isIsolatedEnvironment, KlaRecipeAssayExecutionFactory factory,
        IKlaTestStore store, ISettingsService settings, TimeProvider time, BackgroundFileWriter writer)
    {
        _root = Path.GetFullPath(root); _time = time; _writer = writer; _factory = factory; _store = store; _settings = settings;
        try { Context = new(_root, writer); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidOperationException or AggregateException)
        { AvailabilityError = $"Contexto automático indisponível: {error.Message}"; }
        Profiles = new(Context?.InstallationId ?? "unavailable", time, isIsolatedEnvironment);
        ProfileStore = new(Path.Combine(_root, "profiles"), Profiles.InstallationId, isIsolatedEnvironment, time, writer);
        _router = new(Profiles);
        if (Context is not null) ReloadProfiles();
    }

    public void ReloadProfiles()
    {
        if (Context is null) return;
        try
        {
            ProfileStore.RegisterAvailable(Profiles);
            _api ??= new(Path.Combine(_root, "assay-journal.json"), _router, _time);
            _source ??= new(Profiles, _router, _api, _factory, _store, _settings, _time, _writer,
                Path.Combine(_root, "periodic"), () => Context.CultivationId);
            AvailabilityError = null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or ArgumentException or InvalidOperationException or AggregateException)
        { AvailabilityError = $"Catálogo/diário automático indisponível: {error.Message}"; }
    }

    public async Task ImportProfileAsync(string path, CancellationToken ct = default)
    {
        if (Context is null) throw new InvalidOperationException(AvailabilityError);
        await ProfileStore.ImportAsync(path, ct).ConfigureAwait(false);
        ReloadProfiles();
        if (AvailabilityError is not null) throw new InvalidOperationException(AvailabilityError);
    }

    public bool CanExecute(RecipeDocument recipe, out string? reason)
    {
        if (Context?.IsChangingCultivation == true)
        { reason = "Aguarde a gravação do cultivo antes de iniciar o ensaio."; return false; }
        if (AvailabilityError is not null || _source is null)
        { reason = AvailabilityError ?? "Serviço de ensaio automático indisponível."; return false; }
        return _source.CanExecute(recipe, out reason);
    }

    public RecipeAutonomousExecutionPlan CreateWork(RecipeDocument recipe, Guid executionId, RecipeResourceCoordinator resources)
    {
        if (!CanExecute(recipe, out var reason)) throw new InvalidOperationException(reason);
        return _source!.CreateWork(recipe, executionId, resources);
    }

    public void Dispose() => _api?.Dispose();
}
