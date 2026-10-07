using System.Security.Cryptography;
using System.Text.Json;

namespace OpenTECHub.Services.KlaTesting;

/// <summary>One journal can dispatch successive reserved scopes without resetting cultivation budgets.</summary>
public sealed class KlaRecipeExecutionRouter : IKlaAssayExecution
{
    private readonly KlaRecipeOperationalProfileRegistry? _profiles;
    private readonly object _gate = new();
    private readonly Dictionary<Guid, Scope> _scopes = new();
    private bool _running;
    public KlaAssayExecutionCapabilities Capabilities { get; }
    public bool IsValidated => Capabilities.IsIsolatedSimulation;
    public KlaRecipeExecutionRouter(KlaAssayExecutionCapabilities capabilities) => Capabilities = capabilities;
    public KlaRecipeExecutionRouter(KlaRecipeOperationalProfileRegistry profiles)
    {
        _profiles = profiles;
        Capabilities = new() { InstallationId = profiles.InstallationId, ProfileId = "operational-profile-registry",
            ProfileVersion = "1", Protocols = [KlaAssayProtocol.Abiotic, KlaAssayProtocol.Biotic],
            EvidenceId = "host-routing-only", IsIsolatedSimulation = profiles.IsIsolatedEnvironment };
    }
    public void EnsureAllows(KlaAssayApiRequest request)
    {
        var allowed = _profiles is null ? Capabilities : _profiles.ResolveCapabilities(
            request.RecipePulse?.Invocation ?? throw new InvalidOperationException("Roteador de perfis exige contexto de receita."));
        allowed.EnsureAllows(request);
    }
    private sealed record Scope(string Fingerprint, IKlaAssayExecution Execution)
    {
        public bool Started { get; set; }
    }

    public IDisposable Register(KlaAssayApiRequest request, IKlaAssayExecution execution)
    {
        request.Validate();
        if (!IsValidated || !execution.IsValidated || request.RecipePulse is null || execution.Capabilities is null)
            throw new InvalidOperationException("Escopo de receita sem capacidades isoladas validadas.");
        EnsureAllows(request); execution.Capabilities.EnsureAllows(request);
        if (_profiles is not null && JsonSerializer.Serialize(execution.Capabilities) !=
            JsonSerializer.Serialize(_profiles.ResolveCapabilities(request.RecipePulse.Invocation)))
            throw new InvalidOperationException("Escopo não corresponde à evidência do perfil registrado.");
        if (!execution.Capabilities.IsIsolatedSimulation)
            throw new InvalidOperationException("Escopo físico não pode ser registrado no roteador isolado.");
        lock (_gate)
        {
            if (_scopes.ContainsKey(request.RequestId)) throw new InvalidOperationException("Escopo da tentativa já registrado.");
            var scope = new Scope(Hash(request), execution);
            _scopes.Add(request.RequestId, scope);
            return new Registration(this, request.RequestId, scope);
        }
    }

    public async Task<KlaAssayApiResult> ExecuteWithRecoveryAsync(KlaAssayApiRequest request, CancellationToken acquisitionCancellation)
    {
        EnsureAllows(request);
        Scope scope;
        lock (_gate)
        {
            if (!_scopes.TryGetValue(request.RequestId, out scope!) || scope.Fingerprint != Hash(request))
                throw new InvalidOperationException("Tentativa sem escopo correspondente ao request congelado.");
            if (_running || scope.Started) throw new InvalidOperationException("Escopo em uso ou tentativa já despachada.");
            scope.Started = true; _running = true;
        }
        try { return await scope.Execution.ExecuteWithRecoveryAsync(request, acquisitionCancellation).ConfigureAwait(false); }
        finally { lock (_gate) { _scopes.Remove(request.RequestId); _running = false; } }
    }

    private static string Hash(KlaAssayApiRequest request) => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(request)));
    private sealed class Registration(KlaRecipeExecutionRouter router, Guid id, Scope scope) : IDisposable
    {
        public void Dispose()
        {
            lock (router._gate)
            {
                if (!router._scopes.TryGetValue(id, out var registered) || !ReferenceEquals(registered, scope)) return;
                if (scope.Started) throw new InvalidOperationException("Aguarde recuperação antes de remover o escopo.");
                router._scopes.Remove(id);
            }
        }
    }
}
