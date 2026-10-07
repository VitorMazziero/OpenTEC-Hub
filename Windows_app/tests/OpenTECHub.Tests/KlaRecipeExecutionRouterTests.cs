using System.IO;
using OpenTECHub.Services.KlaTesting;
using Xunit;

namespace OpenTECHub.Tests;

public sealed class KlaRecipeExecutionRouterTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "recipe-router-" + Guid.NewGuid().ToString("N"));
    private static KlaAssayExecutionCapabilities Capabilities => new()
    {
        InstallationId = "test", ProfileId = "qualified-test-profile", ProfileVersion = "1",
        Protocols = [KlaAssayProtocol.Abiotic], EvidenceId = "isolated-test", IsIsolatedSimulation = true
    };
    private sealed class Scope : IKlaAssayExecution
    {
        public bool IsValidated => true;
        public KlaAssayExecutionCapabilities Capabilities => KlaRecipeExecutionRouterTests.Capabilities;
        public int Calls;
        public TaskCompletionSource<KlaAssayApiResult>? Completion;
        public Task<KlaAssayApiResult> ExecuteWithRecoveryAsync(KlaAssayApiRequest request, CancellationToken ct)
        {
            Calls++;
            return Completion?.Task ?? Task.FromResult(Result(request));
        }
    }
    private static KlaAssayApiResult Result(KlaAssayApiRequest request) => new(new()
    { Restoration = KlaRestorationState.Confirmed, KlaQuality = KlaScientificQuality.Valid }, 40)
    { ReturnSnapshotId = request.RecipePulse!.Invocation.Restoration.BeforeAssay.SnapshotId, PersistenceReceiptId = "scope-test-receipt" };
    private static KlaAssayApiRequest Request()
    {
        var invocation = RecipeExecutionContractTests.Request();
        invocation = invocation with { Retry = invocation.Retry with { MinimumInterAssaySeconds = 0 },
            Definition = invocation.Definition with { Settings = invocation.Definition.Settings with
                { MaxDegassingTimeMinutes = 0.5, MaxPrestageSeconds = 5 }, ProtocolSettings = invocation.Definition.ProtocolSettings with
                { AerationReturn = invocation.Definition.ProtocolSettings.AerationReturn with { MinimumInterAssaySeconds = 0 } } } };
        return KlaRecipePulseMapper.Create(invocation, "test", invocation.Definition.Conditions[0].ConditionId,
            1, 1, invocation.Restoration.BeforeAssay.CapturedUtc.AddMinutes(1));
    }

    [Fact]
    public async Task Successive_scopes_share_one_journal_and_budget_without_redispatch()
    {
        var router = new KlaRecipeExecutionRouter(Capabilities);
        var requests = new[] { Request(), Request() };
        var executors = new[] { new Scope(), new Scope() };
        using (var api = new KlaAssayApi(Path.Combine(_root, "api.json"), router))
        {
            for (var index = 0; index < 2; index++)
            {
                using var registration = router.Register(requests[index], executors[index]);
                api.Create(requests[index]); await api.StartAsync(requests[index].RequestId);
                var completed = await api.WaitForCompletionAsync(requests[index].RequestId).WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Equal(KlaAssayApiState.Completed, completed.State);
                await api.StartAsync(requests[index].RequestId);
                Assert.Equal(1, executors[index].Calls);
                Assert.Equal(9 - index, api.ReadCultivationBudget(requests[index]).RemainingAttempts);
            }
        }
        using var reopened = new KlaAssayApi(Path.Combine(_root, "api.json"), router);
        Assert.Equal(8, reopened.ReadCultivationBudget(requests[1]).RemainingAttempts);
        await reopened.StartAsync(requests[0].RequestId);
        Assert.Equal(1, executors[0].Calls);
    }

    [Fact]
    public async Task Scope_cannot_change_payload_overlap_or_be_removed_before_recovery()
    {
        var router = new KlaRecipeExecutionRouter(Capabilities);
        var request = Request();
        var executor = new Scope { Completion = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        using var registration = router.Register(request, executor);
        Assert.Throws<InvalidOperationException>(() => router.Register(request, executor));
        await Assert.ThrowsAsync<InvalidOperationException>(() => router.ExecuteWithRecoveryAsync(request with
            { DeadlineUtc = request.DeadlineUtc.AddSeconds(1) }, CancellationToken.None));
        var running = router.ExecuteWithRecoveryAsync(request, CancellationToken.None);
        Assert.Throws<InvalidOperationException>(registration.Dispose);
        var second = Request(); using var secondRegistration = router.Register(second, new Scope());
        await Assert.ThrowsAsync<InvalidOperationException>(() => router.ExecuteWithRecoveryAsync(second, CancellationToken.None));
        executor.Completion.SetResult(Result(request)); await running;
        await Assert.ThrowsAsync<InvalidOperationException>(() => router.ExecuteWithRecoveryAsync(request, CancellationToken.None));
        registration.Dispose();
        Assert.Equal(1, executor.Calls);
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
