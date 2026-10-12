using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenTECHub.Protocol;
using OpenTECHub.Services.Calibration;
using OpenTECHub.Services.Communication;
using OpenTECHub.Services.Persistence;

namespace OpenTECHub.ViewModels;

/// <summary>
/// Calibrações › Bomba Externa › Ponto de dosagem (plan 2026-10-11 §6): the single point pulse
/// dosing runs on — one fixed speed and the flow measured there — plus the optional pulse check
/// that measures the start-up dead time t₀.
/// </summary>
/// <remarks>
/// <para>
/// The continuous run is timed by the node: <c>pump_speed_ms</c> is exactly the duration, so the
/// network latency of the start and of the stop does not enter the measurement. The app only
/// counts down and sends a backup <c>speed 0</c> three seconds after the deadline.
/// </para>
/// <para>
/// The pulse check runs N pulses with the node's own dosing machinery (the code that will
/// dose for real) and needs a 3.13 pump behind a 10.9 Hub, which report when the run ends.
/// </para>
/// <para>
/// Measuring, saving into the hose profile and sending to the node stay three acts. The point
/// is independent of the polynomial curve and does not expire (D7).
/// </para>
/// </remarks>
public sealed partial class PumpDosePointViewModel : ObservableObject, IDisposable
{
    public const double BackupStopMarginSeconds = 3.0;
    private static readonly TimeSpan TickInterval = TimeSpan.FromMilliseconds(250);
    private const double EchoTolerance = 0.0005;

    private readonly IDeviceService _device;
    private readonly IManualDispatcher _dispatcher;
    private readonly ISettingsService _settings;
    private readonly TimeProvider _timeProvider;
    private readonly Func<bool> _isHostBusy;
    private DispatcherTimer? _timer;

    private DateTimeOffset _runStartedAt;
    private double _pointSpeed;
    private double _runSeconds;
    private bool _backupStopSent;
    private double? _pendingRunSeconds;

    private PumpDoseParameters? _pulseCheckParameters;
    private bool _pulseCheckSawActive;

    private (double Speed, double Rate, double Dead)? _requestedEcho;
    private DateTimeOffset? _echoRequestedAt;

    private string? _profileName;
    private string? _pointId;

    private readonly Func<IReadOnlyList<PumpCalibrationPoint>> _curveRuns;

    /// <summary>Guards row events while the table is rebuilt or activations are normalised.</summary>
    private bool _syncing;

    public PumpDosePointViewModel(
        IDeviceService device,
        ISettingsService settings,
        IManualDispatcher dispatcher,
        TimeProvider timeProvider,
        Func<bool> isHostBusy,
        Func<IReadOnlyList<PumpCalibrationPoint>>? curveRuns = null)
    {
        _device = device;
        _settings = settings;
        _dispatcher = dispatcher;
        _timeProvider = timeProvider;
        _isHostBusy = isHostBusy;
        _curveRuns = curveRuns ?? (() => []);
        _device.TelemetryReceived += OnTelemetryReceived;
    }

    /// <summary>Raised by "Salvar no perfil": the host writes the hose profile with <see cref="Points"/>.</summary>
    public event Action? SaveRequested;

    /// <summary>Raised when a run starts or ends, so the host re-evaluates its own commands.</summary>
    public event Action? BusyChanged;

    /// <summary>Dose points of the active hose profile.</summary>
    public ObservableCollection<PumpDosePoint> Points { get; } = [];

    /// <summary>
    /// The active rows of <see cref="Table"/> as repetitions (derived; the charts and the point
    /// read it). Edit the table, not this.
    /// </summary>
    public ObservableCollection<PumpDoseRepetition> Repetitions { get; } = [];

    /// <summary>
    /// The dose point table: every run of this section, rows copied from the curve and rows
    /// typed by hand, each with an optional t₀. The active rows (one speed) make the point.
    /// </summary>
    public ObservableCollection<PumpDoseRunRow> Table { get; } = [];

    /// <summary>Speeds that have curve runs in the active hose profile.</summary>
    public ObservableCollection<double> ImportSpeeds { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ImportFromCurveCommand))]
    public partial double? SelectedImportSpeed { get; set; }

    [ObservableProperty] public partial string ImportHintText { get; set; } = "A curva desta mangueira não tem ensaios para importar.";

    /// <summary>Mean of the t₀ typed on the active rows, ms; null when none was typed.</summary>
    public double? TypedDeadMs { get; private set; }

    public bool CanImportFromCurve => SelectedImportSpeed is not null && !IsBusy;

    public ObservableCollection<PumpPulseCheck> PulseChecks { get; } = [];

    // ---- Inputs ------------------------------------------------------------------

    [ObservableProperty] public partial string SpeedText { get; set; } = "120";
    [ObservableProperty] public partial string DurationText { get; set; } = "120";
    /// <summary>Volume read on the graduated cylinder, mL.</summary>
    [ObservableProperty] public partial string VolumeText { get; set; } = "";
    [ObservableProperty] public partial string PulseOnText { get; set; } = "1.0";
    [ObservableProperty] public partial string PulseCountText { get; set; } = "20";
    [ObservableProperty] public partial string PulseIntervalText { get; set; } = "5";
    [ObservableProperty] public partial string PulseVolumeText { get; set; } = "";

    // ---- State -------------------------------------------------------------------

    [ObservableProperty] public partial bool IsRunning { get; set; }
    [ObservableProperty] public partial bool HasPendingRun { get; set; }
    [ObservableProperty] public partial bool IsPulseCheckRunning { get; set; }
    [ObservableProperty] public partial bool HasPendingPulseCheck { get; set; }
    [ObservableProperty] public partial double ProgressPercent { get; set; }
    [ObservableProperty] public partial string CountdownText { get; set; } = "";
    [ObservableProperty] public partial string StatusText { get; set; } = "";
    [ObservableProperty] public partial string RepetitionSummaryText { get; set; } = "Nenhuma repetição registrada.";
    [ObservableProperty] public partial string PulseCheckSummaryText { get; set; } = "";
    [ObservableProperty] public partial string NodeEchoText { get; set; } = "Nó: —";
    [ObservableProperty] public partial string ProfileText { get; set; } = "Mangueira: nenhum perfil ativo";
    [ObservableProperty] public partial string InUseText { get; set; } = "Em uso na dosagem: —";
    [ObservableProperty] public partial string? InterlockText { get; set; }
    [ObservableProperty] public partial bool IsPumpOnline { get; set; }

    /// <summary>True when a 3.13 pump behind a 10.9 Hub reports dosing (needed by the pulse check).</summary>
    [ObservableProperty] public partial bool HasDoseTelemetry { get; set; }

    /// <summary>A run, a pulse check or an echo wait is in progress.</summary>
    public bool IsBusy => IsRunning || IsPulseCheckRunning;

    /// <summary>The node drives the motor from a profile or a dosing run: calibration would cancel it.</summary>
    private bool _nodeOperating;

    /// <summary>The hose has a dose point (saved, or measured and not yet saved).</summary>
    public bool HasPoint => Points.Count > 0 || Repetitions.Count > 0;

    public double MeanRateMlMin { get; private set; } = double.NaN;
    public double CvPercent { get; private set; } = double.NaN;

    public bool CanStartRun => IsPumpOnline && !IsBusy && !_nodeOperating && !_isHostBusy() && TryParseRun(out _, out _, out _);
    public bool CanAbort => IsRunning;
    public bool CanRegisterRepetition => HasPendingRun && TryParsePositive(VolumeText, out _);
    public bool CanStartPulseCheck => IsPumpOnline && HasDoseTelemetry && !IsBusy && !_nodeOperating && !_isHostBusy() &&
                                      double.IsFinite(MeanRateMlMin) && TryBuildPulseCheck(out _, out _);
    public bool CanAbortPulseCheck => IsPulseCheckRunning;
    public bool CanRegisterPulseCheck => HasPendingPulseCheck && TryParsePositive(PulseVolumeText, out _);
    public bool CanSave => Repetitions.Count > 0 && double.IsFinite(MeanRateMlMin) && !IsBusy;
    public bool CanUseForDosing => CanSave;
    public bool CanSendToNode => CanSave && IsPumpOnline && _requestedEcho is null;

    // ---- Profile hand-off -------------------------------------------------------

    /// <summary>
    /// Loads a hose profile's dose points and table. A profile saved before the table existed
    /// gets one from its newest point's repetitions, all active.
    /// </summary>
    public void LoadPoints(IReadOnlyList<PumpDosePoint> points, string? profileName, IReadOnlyList<PumpDoseRun>? runs = null)
    {
        _profileName = profileName;
        ProfileText = string.IsNullOrWhiteSpace(profileName) ? "Mangueira: perfil novo (não salvo)" : $"Mangueira: {profileName}";
        Points.Clear();
        foreach (var p in points)
        {
            Points.Add(p);
        }

        PulseChecks.Clear();
        _pointId = null;
        var latest = points.OrderByDescending(p => p.CapturedAtUtc, StringComparer.Ordinal).FirstOrDefault();
        var tableRuns = runs is { Count: > 0 }
            ? runs
            : latest is null
                ? []
                : latest.Repetitions.Select(r => new PumpDoseRun
                {
                    SpeedUnits = latest.SpeedUnits, Seconds = r.Seconds, VolumeMl = r.VolumeMl,
                    IsActive = true, Source = latest.Source, CapturedAtUtc = latest.CapturedAtUtc,
                }).ToArray();
        ReplaceTable(tableRuns);

        var activeSpeed = ActiveSpeed();
        if (latest is not null && activeSpeed is { } sp && Math.Abs(latest.SpeedUnits - sp) < 1e-9)
        {
            _pointId = latest.Id;
            foreach (var c in latest.PulseChecks) PulseChecks.Add(c);
        }

        if (activeSpeed is { } speed)
        {
            SpeedText = DosingInput.Format(speed, 0);
        }

        SyncActive();
        RefreshInUse();
        OnPropertyChanged(nameof(HasPoint));
    }

    /// <summary>The working point folded into <see cref="Points"/>, replacing a point at the same speed.</summary>
    public IReadOnlyList<PumpDosePoint> PointsForSave()
    {
        if (CurrentPoint() is not { } current)
        {
            return [.. Points];
        }

        var others = Points.Where(p => Math.Abs(p.SpeedUnits - current.SpeedUnits) > 1e-9 && p.Id != current.Id);
        return [.. others, current];
    }

    /// <summary>The table as saved in the hose profile (rows that parse).</summary>
    public IReadOnlyList<PumpDoseRun> RunsForSave() => [.. Table.Select(r => r.ToRun()).OfType<PumpDoseRun>()];

    /// <summary>After the host saved the profile: the saved points become the list.</summary>
    public void MarkSaved(string profileName, IReadOnlyList<PumpDosePoint> saved)
    {
        _profileName = profileName;
        ProfileText = $"Mangueira: {profileName}";
        Points.Clear();
        foreach (var p in saved) Points.Add(p);
        _pointId = CurrentPoint()?.Id ?? _pointId;
    }

    private PumpDosePoint? CurrentPoint()
    {
        if (Repetitions.Count == 0 || ActiveSpeed() is not { } speed)
        {
            return null;
        }

        return PumpDosePointMath.Build(speed, [.. Repetitions], [.. PulseChecks], _timeProvider.GetUtcNow(), _pointId,
            ActiveSource(), TypedDeadMs);
    }

    // ---- Continuous run, timed by the node ----------------------------------------

    [RelayCommand(CanExecute = nameof(CanStartRun))]
    public void StartRun()
    {
        if (!TryParseRun(out var speed, out var seconds, out var error))
        {
            StatusText = error ?? "Parâmetros do ensaio inválidos.";
            return;
        }

        // The deadline IS the duration: the node stops the motor itself, so the delivered
        // volume corresponds to exactly this motor time.
        var result = _dispatcher.Dispatch(CommandBuilders.PumpManualSpeed(speed, (int)Math.Round(seconds * 1000.0)));
        if (!result.Accepted)
        {
            StatusText = DispatchRefusal.Describe(result);
            return;
        }

        _pointSpeed = speed;
        _runSeconds = seconds;
        _runStartedAt = _timeProvider.GetUtcNow();
        _backupStopSent = false;
        _pendingRunSeconds = null;
        HasPendingRun = false;
        ProgressPercent = 0;
        IsRunning = true;
        StatusText = $"Bomba a S = {speed} por {seconds.ToString("F0", CultureInfo.CurrentCulture)} s, cronometrados pelo nó. Recolha o volume na proveta.";
        EnsureTimer().Start();
        NotifyAll();
    }

    [RelayCommand(CanExecute = nameof(CanAbort))]
    public void AbortRun()
    {
        var result = _dispatcher.Dispatch(CommandBuilders.PumpManualSpeed(0));
        if (!result.Accepted)
        {
            StatusText = "PARADA RECUSADA - a bomba para sozinha no prazo do ensaio: " + DispatchRefusal.Describe(result);
        }
        else
        {
            StatusText = "Ensaio abortado; nenhuma repetição foi criada.";
        }

        IsRunning = false;
        HasPendingRun = false;
        _timer?.Stop();
        NotifyAll();
    }

    /// <summary>Advances the countdown; driven by a UI timer and by telemetry.</summary>
    public void Tick()
    {
        if (!IsRunning)
        {
            return;
        }

        var elapsed = (_timeProvider.GetUtcNow() - _runStartedAt).TotalSeconds;
        ProgressPercent = Math.Clamp(elapsed / _runSeconds * 100.0, 0.0, 100.0);
        if (elapsed < _runSeconds)
        {
            CountdownText = $"{Math.Max(0.0, _runSeconds - elapsed).ToString("F0", CultureInfo.CurrentCulture)} s restantes";
            return;
        }

        if (_pendingRunSeconds is null)
        {
            _pendingRunSeconds = _runSeconds;
            HasPendingRun = true;
            CountdownText = "Parada pelo nó no prazo.";
            StatusText = $"Ensaio de {_runSeconds.ToString("F0", CultureInfo.CurrentCulture)} s concluído. Informe o volume lido na proveta.";
        }

        if (!_backupStopSent && elapsed >= _runSeconds + BackupStopMarginSeconds)
        {
            // Backup only: the node already stopped at its deadline.
            _dispatcher.Dispatch(CommandBuilders.PumpManualSpeed(0));
            _backupStopSent = true;
            IsRunning = false;
            _timer?.Stop();
        }

        NotifyAll();
    }

    [RelayCommand(CanExecute = nameof(CanRegisterRepetition))]
    public void RegisterRepetition()
    {
        if (_pendingRunSeconds is not { } seconds || !TryParsePositive(VolumeText, out var volume))
        {
            StatusText = "Informe o volume lido na proveta (mL).";
            return;
        }

        // A run at the active speed joins the point; at another speed it waits, inactive, until
        // the operator marks it active.
        var active = ActiveSpeed();
        var joins = active is null || Math.Abs(active.Value - _pointSpeed) < 1e-9;
        AddRow(new PumpDoseRun
        {
            SpeedUnits = _pointSpeed, Seconds = seconds, VolumeMl = volume, IsActive = joins,
            CapturedAtUtc = _timeProvider.GetUtcNow().ToString("o"),
        });
        _pendingRunSeconds = null;
        HasPendingRun = false;
        VolumeText = "";
        if (IsRunning && !_backupStopSent)
        {
            _dispatcher.Dispatch(CommandBuilders.PumpManualSpeed(0));
            _backupStopSent = true;
            IsRunning = false;
            _timer?.Stop();
        }

        SyncActive();
        if (!joins)
        {
            StatusText = $"Ensaio em S {_pointSpeed.ToString("F0", CultureInfo.CurrentCulture)} adicionado à tabela, inativo: o ponto ativo está em outra velocidade. Marque \"Ativa\" para usá-lo.";
            NotifyAll();
            return;
        }

        StatusText = CvPercent > PumpDosePointMath.MaxCvPercent
            ? $"Repetição registrada. CV de {CvPercent.ToString("F1", CultureInfo.CurrentCulture)} % acima de {PumpDosePointMath.MaxCvPercent:F0} %: faça outra repetição antes de salvar."
            : $"Repetição registrada: {Repetitions[^1].RateMlMin.ToString("F3", CultureInfo.CurrentCulture)} mL/min.";
        NotifyAll();
    }

    // ---- Table ------------------------------------------------------------------------

    /// <summary>Adds a row to type a run by hand, at the speed in the run field.</summary>
    [RelayCommand]
    public void AddTableRow()
    {
        var speed = TryParseSpeed(SpeedText, out var sp) ? sp : (int?)null;
        var active = ActiveSpeed();
        var row = new PumpDoseRunRow
        {
            SpeedText = speed is { } v ? DosingInput.FormatInt(v) : "",
            Source = PumpDosePointMath.SourceManual,
            IsActive = speed is { } x && (active is null || Math.Abs(active.Value - x) < 1e-9),
            CapturedAtUtc = _timeProvider.GetUtcNow().ToString("o"),
        };
        Attach(row);
        Table.Add(row);
        StatusText = "Linha adicionada: informe tempo (s) e volume (mL); t₀ é opcional.";
    }

    [RelayCommand]
    public void RemoveTableRow(PumpDoseRunRow? row)
    {
        if (row is null || !Table.Remove(row))
        {
            return;
        }

        row.PropertyChanged -= OnRowChanged;
        SyncActive();
        NotifyAll();
    }

    /// <summary>
    /// Copies every curve run at <see cref="SelectedImportSpeed"/> into the table as active rows
    /// (other speeds are deactivated). A copy: editing the curve later does not change these rows.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanImportFromCurve))]
    public void ImportFromCurve()
    {
        if (SelectedImportSpeed is not { } speed)
        {
            return;
        }

        var imported = PumpDosePointMath.RepetitionsAt(_curveRuns(), speed);
        if (imported.Count == 0)
        {
            StatusText = "A curva não tem ensaios válidos nessa velocidade.";
            return;
        }

        _syncing = true;
        try
        {
            foreach (var row in Table.Where(r => r.Speed is { } s && Math.Abs(s - speed) > 1e-9))
            {
                row.IsActive = false;
            }
        }
        finally
        {
            _syncing = false;
        }

        var now = _timeProvider.GetUtcNow().ToString("o");
        foreach (var r in imported)
        {
            AddRow(new PumpDoseRun
            {
                SpeedUnits = speed, Seconds = r.Seconds, VolumeMl = r.VolumeMl, IsActive = true,
                Source = PumpDosePointMath.SourceCurve, CapturedAtUtc = now,
            });
        }

        SpeedText = DosingInput.Format(speed, 0);
        SyncActive();
        var c = CultureInfo.CurrentCulture;
        StatusText = $"{imported.Count} ensaio(s) da curva em S {speed.ToString("F0", c)} copiados para a tabela e ativados: " +
                     $"média {MeanRateMlMin.ToString("F3", c)} mL/min. Informe t₀ na tabela se o conhecer." +
                     (imported.Count < 2 ? " Com um ensaio só não há CV." : "");
        NotifyAll();
    }

    /// <summary>Refreshes the import list from the curve's runs; the host calls it when they change.</summary>
    public void RefreshImportOptions()
    {
        var speeds = PumpDosePointMath.ImportableSpeeds(_curveRuns());
        var keep = SelectedImportSpeed;
        ImportSpeeds.Clear();
        foreach (var sp in speeds)
        {
            ImportSpeeds.Add(sp);
        }

        SelectedImportSpeed = keep is { } k && speeds.Contains(k) ? k : null;
        var c = CultureInfo.CurrentCulture;
        ImportHintText = speeds.Count == 0
            ? "A curva desta mangueira não tem ensaios para importar."
            : $"Velocidades com ensaios na curva: {string.Join(", ", speeds.Select(x => x.ToString("F0", c)))}.";
    }

    private void AddRow(PumpDoseRun run)
    {
        var row = new PumpDoseRunRow(run);
        Attach(row);
        Table.Add(row);
    }

    private void Attach(PumpDoseRunRow row) => row.PropertyChanged += OnRowChanged;

    private void ReplaceTable(IReadOnlyList<PumpDoseRun> runs)
    {
        foreach (var row in Table)
        {
            row.PropertyChanged -= OnRowChanged;
        }

        Table.Clear();
        foreach (var run in runs)
        {
            AddRow(run);
        }
    }

    private void OnRowChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (_syncing || sender is not PumpDoseRunRow row)
        {
            return;
        }

        if (e.PropertyName == nameof(PumpDoseRunRow.IsActive) && row.IsActive && row.Speed is { } speed)
        {
            // One speed per point: activating a row at another speed deactivates the rest.
            var others = Table.Where(r => r != row && r.IsActive && r.Speed is { } s && Math.Abs(s - speed) > 1e-9).ToList();
            if (others.Count > 0)
            {
                _syncing = true;
                try
                {
                    foreach (var r in others) r.IsActive = false;
                }
                finally
                {
                    _syncing = false;
                }

                PulseChecks.Clear();
                _pointId = null;
                StatusText = $"Ponto passou para S {speed.ToString("F0", CultureInfo.CurrentCulture)}: linhas de outra velocidade foram desativadas e as verificações em pulsos descartadas.";
            }

            SpeedText = DosingInput.Format(speed, 0);
        }

        if (e.PropertyName is nameof(PumpDoseRunRow.IsActive) or nameof(PumpDoseRunRow.SpeedText) or
            nameof(PumpDoseRunRow.SecondsText) or nameof(PumpDoseRunRow.VolumeText) or nameof(PumpDoseRunRow.DeadMsText))
        {
            if (e.PropertyName is not (nameof(PumpDoseRunRow.IsActive) or nameof(PumpDoseRunRow.DeadMsText)) && row.Source is null)
            {
                row.Source = PumpDosePointMath.SourceManual; // a measured value edited by hand
            }

            SyncActive();
            NotifyAll();
        }
    }

    /// <summary>Speed of the active rows (they share one); null without an active valid row.</summary>
    private double? ActiveSpeed() => Table.FirstOrDefault(r => r.IsActive && r.IsValid)?.Speed;

    private string? ActiveSource()
    {
        var sources = Table.Where(r => r.IsActive && r.IsValid).Select(r => r.Source).Distinct().ToList();
        return sources.Count switch
        {
            0 => null,
            1 => sources[0],
            _ => PumpDosePointMath.SourceMixed,
        };
    }

    /// <summary>Rebuilds <see cref="Repetitions"/> and the typed t₀ from the active rows.</summary>
    private void SyncActive()
    {
        var active = Table.Where(r => r.IsActive && r.IsValid).ToList();
        _pointSpeed = active.FirstOrDefault()?.Speed ?? _pointSpeed;
        Repetitions.Clear();
        foreach (var r in active)
        {
            Repetitions.Add(r.Repetition!);
        }

        var typed = active.Select(r => r.DeadMs).OfType<double>().ToList();
        TypedDeadMs = typed.Count > 0 ? typed.Average() : null;
        OnPropertyChanged(nameof(TypedDeadMs));
        Recompute();
    }

    // ---- Pulse check ----------------------------------------------------------------

    [RelayCommand(CanExecute = nameof(CanStartPulseCheck))]
    public void StartPulseCheck()
    {
        if (!TryBuildPulseCheck(out var parameters, out var error))
        {
            StatusText = error ?? "Parâmetros da verificação inválidos.";
            return;
        }

        var result = _dispatcher.Dispatch(CommandBuilders.PumpDoseStart(parameters));
        if (!result.Accepted)
        {
            StatusText = DispatchRefusal.Describe(result);
            return;
        }

        _pulseCheckParameters = parameters;
        _pulseCheckSawActive = false;
        HasPendingPulseCheck = false;
        IsPulseCheckRunning = true;
        PulseCheckSummaryText = "";
        StatusText = $"Verificação: {PulseCountText} pulsos de {PulseOnText} s pela dosagem do nó. Recolha o volume.";
        NotifyAll();
    }

    [RelayCommand(CanExecute = nameof(CanAbortPulseCheck))]
    public void AbortPulseCheck()
    {
        _dispatcher.Dispatch(CommandBuilders.PumpDoseStop());
        IsPulseCheckRunning = false;
        HasPendingPulseCheck = false;
        _pulseCheckParameters = null;
        StatusText = "Verificação em pulsos abortada; nada foi registrado.";
        NotifyAll();
    }

    [RelayCommand(CanExecute = nameof(CanRegisterPulseCheck))]
    public void RegisterPulseCheck()
    {
        if (!TryParsePositive(PulseVolumeText, out var volume) || !TryParsePositive(PulseOnText, out var onS) ||
            !int.TryParse(PulseCountText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var count))
        {
            StatusText = "Informe o volume lido na proveta após a verificação (mL).";
            return;
        }

        var check = new PumpPulseCheck(onS * 1000.0, count, volume);
        var t0 = PumpDosePointMath.DeadTimeMs(check, MeanRateMlMin);
        if (t0 is null)
        {
            StatusText = "Atraso de partida fora de 0..2000 ms: confira o volume medido e a vazão do ponto. Nada foi registrado.";
            return;
        }

        PulseChecks.Add(check);
        HasPendingPulseCheck = false;
        PulseVolumeText = "";
        Recompute();
        StatusText = $"Verificação registrada: {check.PerPulseMl.ToString("F4", CultureInfo.CurrentCulture)} mL/pulso, t₀ ≈ {t0.Value.ToString("F0", CultureInfo.CurrentCulture)} ms.";
        NotifyAll();
    }

    // ---- Save, use, send -------------------------------------------------------------

    [RelayCommand(CanExecute = nameof(CanSave))]
    public void SaveToProfile()
    {
        if (CvPercent > PumpDosePointMath.MaxCvPercent && Repetitions.Count < 3)
        {
            StatusText = $"CV de {CvPercent.ToString("F1", CultureInfo.CurrentCulture)} %: faça outra repetição antes de salvar.";
            return;
        }

        SaveRequested?.Invoke();
    }

    [RelayCommand(CanExecute = nameof(CanUseForDosing))]
    public void UseForDosing()
    {
        if (CurrentPoint() is not { } point)
        {
            return;
        }

        _settings.Update(s => s with
        {
            PumpControl = s.PumpControl with
            {
                Dose = s.PumpControl.Dose with
                {
                    Point = new PumpDosePointSettings
                    {
                        SpeedUnits = point.SpeedUnits,
                        RateMlMin = point.RateMlMin,
                        DeadTimeMs = point.DeadTimeMs,
                        RecommendedMinOnMs = point.RecommendedMinOnMs,
                        CvPercent = point.CvPercent,
                        ProfileName = _profileName,
                        CapturedAtUtc = point.CapturedAtUtc,
                    },
                },
            },
        });
        RefreshInUse();
        StatusText = "Ponto de dosagem definido para o cartão da bomba (não enviado ao nó).";
    }

    [RelayCommand(CanExecute = nameof(CanSendToNode))]
    public void SendToNode()
    {
        if (CurrentPoint() is not { } point)
        {
            return;
        }

        var dead = Math.Round(point.DeadTimeMs ?? 0.0);
        var result = _dispatcher.Dispatch(CommandBuilders.PumpDosePoint(point.SpeedUnits, point.RateMlMin, dead));
        if (!result.Accepted)
        {
            StatusText = DispatchRefusal.Describe(result);
            return;
        }

        _requestedEcho = (point.SpeedUnits, point.RateMlMin, dead);
        _echoRequestedAt = _timeProvider.GetUtcNow();
        StatusText = "Ponto enviado ao nó; aguardando eco.";
        NotifyAll();
    }

    // ---- Telemetry -------------------------------------------------------------------

    private void OnTelemetryReceived(SensorSnapshot snapshot)
    {
        IsPumpOnline = snapshot.PumpOnline;
        HasDoseTelemetry = snapshot.PumpDoseActive is not null;
        var drive = snapshot.PumpDrive;
        var operating = drive is (int)PumpDriveState.ProfileWaiting or (int)PumpDriveState.ProfileRunning ||
                        (!IsPulseCheckRunning && drive is (int)PumpDriveState.DoseIdle or (int)PumpDriveState.DosePulse);
        _nodeOperating = operating;
        InterlockText = operating
            ? "Pare a dosagem ou o perfil no Controle antes de calibrar: o ensaio os cancelaria."
            : null;

        NodeEchoText = snapshot.PumpDoseSpeed is { } s && snapshot.PumpDoseRate is { } q
            ? $"Nó: S {s.ToString("F0", CultureInfo.CurrentCulture)} → {q.ToString("F3", CultureInfo.CurrentCulture)} mL/min · t₀ {(snapshot.PumpDoseDeadMs ?? 0).ToString("F0", CultureInfo.CurrentCulture)} ms (eco)"
            : "Nó: — (requer bomba 3.13 e Hub 10.9)";

        TrackPulseCheck(snapshot);
        TrackEcho(snapshot);
        Tick();
        NotifyAll();
    }

    private void TrackPulseCheck(SensorSnapshot snapshot)
    {
        if (!IsPulseCheckRunning || _pulseCheckParameters is null)
        {
            return;
        }

        if (snapshot.PumpDoseActive == true)
        {
            _pulseCheckSawActive = true;
            return;
        }

        if (_pulseCheckSawActive && snapshot.PumpDoseActive == false)
        {
            IsPulseCheckRunning = false;
            HasPendingPulseCheck = true;
            PulseCheckSummaryText = $"{snapshot.PumpDosePulses ?? 0} pulsos concluídos pelo nó.";
            StatusText = "Verificação concluída. Informe o volume coletado (mL).";
        }
    }

    private void TrackEcho(SensorSnapshot snapshot)
    {
        if (_requestedEcho is not { } want)
        {
            return;
        }

        if (snapshot.PumpDoseSpeed is { } s && snapshot.PumpDoseRate is { } q && snapshot.PumpDoseDeadMs is { } d &&
            Close(s, want.Speed) && Close(q, want.Rate) && Math.Abs(d - want.Dead) < 0.5)
        {
            _requestedEcho = null;
            _echoRequestedAt = null;
            StatusText = "Ponto de dosagem confirmado pelo nó.";
        }
        else if (_echoRequestedAt is { } at && (_timeProvider.GetUtcNow() - at).TotalSeconds > 15.0)
        {
            _requestedEcho = null;
            _echoRequestedAt = null;
            StatusText = "Aviso: o nó não ecoou o ponto de dosagem em 15 s (bomba 3.13 e Hub 10.9 necessários).";
        }
    }

    private static bool Close(double a, double b) => Math.Abs(a - b) <= EchoTolerance * Math.Max(1.0, Math.Abs(b));

    // ---- Helpers ---------------------------------------------------------------------

    private void Recompute()
    {
        var (mean, cv) = PumpDosePointMath.MeanAndCv(Repetitions.Select(r => r.RateMlMin).ToArray());
        MeanRateMlMin = mean;
        CvPercent = cv;
        var c = CultureInfo.CurrentCulture;
        var t0 = TypedDeadMs is { } typed
            ? $" · t₀ {typed.ToString("F0", c)} ms (digitado)"
            : PulseChecks.Count > 0 ? "" : " · t₀ não informado";
        RepetitionSummaryText = Repetitions.Count == 0
            ? (Table.Count == 0 ? "Tabela vazia: faça um ensaio, importe da curva ou adicione uma linha." : "Nenhuma linha ativa: marque \"Ativa\" nas linhas que formam o ponto.")
            : $"Ponto: S {_pointSpeed.ToString("F0", c)} · {Repetitions.Count} linha(s) ativa(s) → média {mean.ToString("F3", c)} mL/min · CV {cv.ToString("F1", c)} %{t0} · origem: {PumpDosePointMath.DescribeSource(ActiveSource())}";

        if (PulseChecks.Count > 0 && double.IsFinite(mean))
        {
            var dead = PumpDosePointMath.DeadTimeMs([.. PulseChecks], mean);
            var rec = PumpDosePointMath.RecommendedMinOnMs([.. PulseChecks], dead ?? 0.0);
            PulseCheckSummaryText = $"{PulseChecks.Count} verificação(ões) · t₀ ≈ {(dead ?? 0).ToString("F0", CultureInfo.CurrentCulture)} ms" +
                (rec is { } r ? $" · pulso mínimo recomendado {(r / 1000.0).ToString("F1", CultureInfo.CurrentCulture)} s" : " · repita a mesma duração para obter o pulso mínimo recomendado");
        }
        else if (!HasPendingPulseCheck)
        {
            PulseCheckSummaryText = "";
        }

        OnPropertyChanged(nameof(MeanRateMlMin));
        OnPropertyChanged(nameof(CvPercent));
        OnPropertyChanged(nameof(HasPoint));
    }

    private void RefreshInUse()
    {
        var p = _settings.Current.PumpControl.Dose.Point;
        InUseText = p is null
            ? "Em uso na dosagem: —"
            : $"Em uso na dosagem: S {p.SpeedUnits.ToString("F0", CultureInfo.CurrentCulture)} → {p.RateMlMin.ToString("F3", CultureInfo.CurrentCulture)} mL/min" +
              (p.ProfileName is { } n ? $" ({n}" + (DateTimeOffset.TryParse(p.CapturedAtUtc, CultureInfo.InvariantCulture, DateTimeStyles.None, out var at) ? $", {at.LocalDateTime:dd/MM/yyyy})" : ")") : "");
    }

    private bool TryParseRun(out int speed, out double seconds, out string? error)
    {
        seconds = 0;
        if (!TryParseSpeed(SpeedText, out speed))
        {
            error = "Velocidade S: inteiro entre 1 e 1000.";
            return false;
        }

        if (!DosingInput.TryParseDouble(DurationText, out seconds) || seconds < 10.0 || seconds > 1800.0)
        {
            error = "Duração: de 10 a 1800 s.";
            return false;
        }

        error = null;
        return true;
    }

    private bool TryBuildPulseCheck(out PumpDoseParameters parameters, out string? error)
    {
        parameters = null!;
        if (ActiveSpeed() is not { } speed || !double.IsFinite(MeanRateMlMin))
        {
            error = "Ative pelo menos uma linha da tabela antes da verificação em pulsos.";
            return false;
        }

        if (!TryParsePositive(PulseOnText, out var onS) ||
            !int.TryParse(PulseCountText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var count) || count is < 2 or > 200 ||
            !TryParsePositive(PulseIntervalText, out var interval))
        {
            error = "Verificação: duração > 0, de 2 a 200 pulsos e intervalo > 0.";
            return false;
        }

        var volume = MeanRateMlMin * onS / 60.0;
        parameters = new PumpDoseParameters(Math.Round(volume, 5), interval, speed, MeanRateMlMin, Math.Round(volume * count, 5), 0.0);
        if (PumpDoseRules.Validate(parameters) is { } code)
        {
            error = $"Verificação inválida: {PumpDoseRules.DescribeRefusal(code)}.";
            return false;
        }

        error = null;
        return true;
    }

    private static bool TryParseSpeed(string text, out int speed)
        => int.TryParse(text?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out speed) && speed is >= 1 and <= 1000;

    private static bool TryParsePositive(string? text, out double value)
        => DosingInput.TryParseDouble(text, out value) && value > 0.0 && double.IsFinite(value);

    private DispatcherTimer EnsureTimer()
    {
        if (_timer is null)
        {
            _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TickInterval };
            _timer.Tick += (_, _) => Tick();
        }

        return _timer;
    }

    /// <summary>Re-evaluates every command; the host calls it when its own state changes.</summary>
    public void NotifyAll()
    {
        OnPropertyChanged(nameof(IsBusy));
        OnPropertyChanged(nameof(CanStartRun));
        OnPropertyChanged(nameof(CanStartPulseCheck));
        StartRunCommand.NotifyCanExecuteChanged();
        AbortRunCommand.NotifyCanExecuteChanged();
        RegisterRepetitionCommand.NotifyCanExecuteChanged();
        StartPulseCheckCommand.NotifyCanExecuteChanged();
        AbortPulseCheckCommand.NotifyCanExecuteChanged();
        RegisterPulseCheckCommand.NotifyCanExecuteChanged();
        ImportFromCurveCommand.NotifyCanExecuteChanged();
        SaveToProfileCommand.NotifyCanExecuteChanged();
        UseForDosingCommand.NotifyCanExecuteChanged();
        SendToNodeCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsRunningChanged(bool value) => BusyChanged?.Invoke();
    partial void OnIsPulseCheckRunningChanged(bool value) => BusyChanged?.Invoke();
    partial void OnSpeedTextChanged(string value) => NotifyAll();
    partial void OnSelectedImportSpeedChanged(double? value) => OnPropertyChanged(nameof(CanImportFromCurve));
    partial void OnDurationTextChanged(string value) => NotifyAll();
    partial void OnVolumeTextChanged(string value) => NotifyAll();
    partial void OnPulseOnTextChanged(string value) => NotifyAll();
    partial void OnPulseCountTextChanged(string value) => NotifyAll();
    partial void OnPulseIntervalTextChanged(string value) => NotifyAll();
    partial void OnPulseVolumeTextChanged(string value) => NotifyAll();

    public void Dispose()
    {
        if (IsRunning)
        {
            _dispatcher.Dispatch(CommandBuilders.PumpManualSpeed(0));
        }

        if (IsPulseCheckRunning)
        {
            _dispatcher.Dispatch(CommandBuilders.PumpDoseStop());
        }

        _timer?.Stop();
        _device.TelemetryReceived -= OnTelemetryReceived;
    }
}
