using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenTECHub.Services.KlaTesting;

namespace OpenTECHub.ViewModels;

public sealed partial class KlaDeterminationViewModel
{
    [ObservableProperty] private string _measurementMedium = "";
    [ObservableProperty] private string _measurementCultivation = "";
    [ObservableProperty] private string _measurementTimeWindow = "";
    [ObservableProperty] private KlaMeasurementSource _measurementSource = KlaMeasurementSource.Unknown;
    [ObservableProperty] private int _maximumAttempts = 3;
    [ObservableProperty] private int _maximumSessionRuns = 100;
    [ObservableProperty] private double _maximumRemovalExposure = 3600;
    [ObservableProperty] private double _minimumInterAssaySeconds;
    [ObservableProperty] private bool _autoAdvanceQueue;
    private CancellationTokenSource? _queueAdvanceCancellation;
    private Guid? _queueOwnerTestId;

    partial void OnAutoAdvanceQueueChanged(bool value)
    {
        if (!value) _queueAdvanceCancellation?.Cancel();
    }

    public static IReadOnlyList<KlaSourceOption> MeasurementSources { get; } =
        [new(KlaMeasurementSource.Unknown, "Não informado"), new(KlaMeasurementSource.Physical, "Equipamento"), new(KlaMeasurementSource.Simulation, "Simulação")];
    public bool HasQueuedRuns => CurrentTest is not null && CurrentTest.TestId == _queueOwnerTestId && _activeSequenceQueue.Count > 0;
    public string QueueStatus
    {
        get
        {
            if (CurrentTest is null || _activeSequenceQueue.Count == 0) return "Sem fila ativa";
            var pending = KlaSequence.Pending(CurrentTest, _activeSequenceQueue);
            if (pending.Count == 0) return "Fila concluída";
            var next = pending[0];
            var ready = KlaSequence.Check(CurrentTest, next, DateTimeOffset.UtcNow);
            return $"{pending.Count} réplicas pendentes · próxima: réplica {next.ReplicateNumber}, tentativa {next.AttemptNumber}. {ready.Reason}";
        }
    }

    private KlaMeasurementContext BuildMeasurementContext() => new()
    {
        Medium = MeasurementMedium.Trim(), CultivationId = MeasurementCultivation.Trim(),
        TimeWindow = MeasurementTimeWindow.Trim(), Source = MeasurementSource,
    };
    private KlaSequenceLimits BuildSequenceLimits() => new()
    {
        MaximumAttemptsPerReplicate = MaximumAttempts, MaximumRuns = MaximumSessionRuns,
        MaximumRemovalSeconds = MaximumRemovalExposure,
    };
    private void LoadSequenceSettings(KlaTestDocument doc)
    {
        _queueAdvanceCancellation?.Cancel();
        if (_queueOwnerTestId != doc.TestId) { _activeSequenceQueue.Clear(); _queueOwnerTestId = null; }
        MeasurementMedium = doc.Context?.Medium ?? "";
        MeasurementCultivation = doc.Context?.CultivationId ?? "";
        MeasurementTimeWindow = doc.Context?.TimeWindow ?? "";
        MeasurementSource = doc.Context?.Source ?? KlaMeasurementSource.Unknown;
        var limits = doc.SequenceLimits ?? new();
        MaximumAttempts = limits.MaximumAttemptsPerReplicate;
        MaximumSessionRuns = limits.MaximumRuns;
        MaximumRemovalExposure = limits.MaximumRemovalSeconds;
        MinimumInterAssaySeconds = doc.ProtocolSettings?.AerationReturn.MinimumInterAssaySeconds ?? 0;
    }

    [RelayCommand]
    public async Task ContinueQueueAsync()
    {
        if (CurrentTest is null || CurrentTest.TestId != _queueOwnerTestId || IsRunning || IsReviewOpen || _runner.IsInReview) return;
        _queueAdvanceCancellation?.Cancel();
        var next = KlaSequence.Pending(CurrentTest, _activeSequenceQueue).FirstOrDefault();
        if (next is null) { StopQueue(); StatusMessage = "Fila concluída; resultados preservados."; return; }
        var ready = KlaSequence.Check(CurrentTest, next, DateTimeOffset.UtcNow);
        if (!ready.CanStart && ready.WaitSeconds > 0 && AutoAdvanceQueue)
        {
            var testId = CurrentTest.TestId;
            using var cancellation = new CancellationTokenSource();
            _queueAdvanceCancellation = cancellation;
            StatusMessage = ready.Reason; NotifyQueue();
            try
            {
                while (!ready.CanStart && ready.WaitSeconds > 0 && AutoAdvanceQueue)
                {
                    await Task.Delay(TimeSpan.FromSeconds(Math.Max(0.01, ready.WaitSeconds)), cancellation.Token);
                    if (CurrentTest?.TestId != testId || !HasQueuedRuns || IsRunning || IsReviewOpen || _runner.IsInReview) return;
                    ready = KlaSequence.Check(CurrentTest, next, DateTimeOffset.UtcNow);
                }
            }
            catch (OperationCanceledException) { return; }
            finally { if (ReferenceEquals(_queueAdvanceCancellation, cancellation)) _queueAdvanceCancellation = null; }
            if (CurrentTest?.TestId != testId || !HasQueuedRuns || IsRunning || IsReviewOpen || _runner.IsInReview) return;
        }
        if (!ready.CanStart) { StatusMessage = ready.Reason; NotifyQueue(); return; }
        var condition = CurrentTest.Conditions.Single(c => c.ConditionId == next.ConditionId);
        if (!EnsureNitrogenSourceConfirmed()) return;
        LivePoints.Clear(); InstantaneousKlaSeries.Clear(); LogLinearSeries.Clear();
        await StartRunSafelyAsync(condition, next.ReplicateNumber);
        NotifyQueue();
    }

    [RelayCommand]
    public void StopQueue()
    {
        _queueAdvanceCancellation?.Cancel();
        _activeSequenceQueue.Clear();
        _queueOwnerTestId = null;
        if (CurrentTest is not null)
        {
            _store.AppendEventLog(CurrentTest.FolderName, new(DateTimeOffset.UtcNow, "QueueStopped",
                "Fila encerrada pelo operador. Dados e corrida atual preservados."));
        }
        StatusMessage = "Fila encerrada; histórico preservado.";
        NotifyQueue();
    }

    private void NotifyQueue()
    {
        OnPropertyChanged(nameof(HasQueuedRuns));
        OnPropertyChanged(nameof(QueueStatus));
    }
}

public sealed record KlaSourceOption(KlaMeasurementSource Value, string Label);
