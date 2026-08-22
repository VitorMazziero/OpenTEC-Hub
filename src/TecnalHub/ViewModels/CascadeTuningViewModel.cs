using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TecnalHub.Services.Control;
using TecnalHub.Services.KlaMapping;
using TecnalHub.Services.Persistence;

namespace TecnalHub.ViewModels;

/// <summary>One selectable actuator-allocation mode for the cascade.</summary>
public sealed record CascadeModeOption(CascadeMode Mode, string Label)
{
    public override string ToString() => Label;
}

/// <summary>
/// The <c>Controle → Cascata e sintonia</c> workspace: edit the oxygen cascade's tuning
/// and watch its live terms.
/// </summary>
/// <remarks>
/// <para>
/// The cascade runs in an advisory role here (<see cref="ICascadeService"/>): arming it
/// computes what it would command against live dissolved-oxygen telemetry, without sending
/// anything. That lets the operator tune against a real process before any actuation is
/// permitted. See <c>docs/UI_DESIGN.md</c> section 5.2.
/// </para>
/// <para>
/// Editable fields are staged; only <see cref="ApplyCommand"/> reaches the running
/// controller and persists. Saving and loading a named tuning never actuates.
/// </para>
/// </remarks>
public sealed partial class CascadeTuningViewModel : ObservableObject, IDisposable
{
    private static readonly HashSet<string> EditableFields =
    [
        nameof(Kp), nameof(Ki), nameof(Kd), nameof(IntegralMin), nameof(IntegralMax),
        nameof(PredictionHorizonSeconds), nameof(RateWindowSeconds), nameof(IntervalSeconds),
        nameof(OxygenSetpoint),
        nameof(AgitationMinRpm), nameof(AgitationMaxRpm), nameof(AgitationEffortStart), nameof(AgitationEffortEnd),
        nameof(AerationMinLpm), nameof(AerationMaxLpm), nameof(AerationEffortStart), nameof(AerationEffortEnd),
    ];

    private readonly ICascadeService _cascade;
    private readonly ISettingsService _settings;
    private bool _loading;

    public CascadeTuningViewModel(ICascadeService cascade, ISettingsService settings)
    {
        _cascade = cascade;
        _settings = settings;

        LoadFrom(settings.Current.Cascade);
        LoadSchedule(settings.Current.GainSchedule);

        foreach (var preset in settings.Current.CascadeTuningPresets
                     .Where(p => !string.IsNullOrWhiteSpace(p.Name))
                     .OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            Presets.Add(preset);
        }

        SelectedPreset = Presets.FirstOrDefault();
        SelectedModeOption = Modes.FirstOrDefault(m => m.Mode == cascade.Mode) ?? Modes[0];

        _cascade.Updated += OnCascadeUpdated;
        RefreshWindows();
        RefreshLive();
        Validate();
        _loading = false;

        LoadPathsSafe();
    }

    // ── Editable tuning (staged) ─────────────────────────────────────────────

    [ObservableProperty] public partial double Kp { get; set; }
    [ObservableProperty] public partial double Ki { get; set; }
    [ObservableProperty] public partial double Kd { get; set; }
    [ObservableProperty] public partial double IntegralMin { get; set; }
    [ObservableProperty] public partial double IntegralMax { get; set; }
    [ObservableProperty] public partial double PredictionHorizonSeconds { get; set; }
    [ObservableProperty] public partial double RateWindowSeconds { get; set; }
    [ObservableProperty] public partial double IntervalSeconds { get; set; }
    [ObservableProperty] public partial double OxygenSetpoint { get; set; }

    [ObservableProperty] public partial double AgitationMinRpm { get; set; }
    [ObservableProperty] public partial double AgitationMaxRpm { get; set; }
    [ObservableProperty] public partial double AgitationEffortStart { get; set; }
    [ObservableProperty] public partial double AgitationEffortEnd { get; set; }

    [ObservableProperty] public partial double AerationMinLpm { get; set; }
    [ObservableProperty] public partial double AerationMaxLpm { get; set; }
    [ObservableProperty] public partial double AerationEffortStart { get; set; }
    [ObservableProperty] public partial double AerationEffortEnd { get; set; }

    // ── Staged-state flags ───────────────────────────────────────────────────

    [ObservableProperty]
    public partial string? ValidationError { get; set; }

    [ObservableProperty]
    public partial bool HasPendingChange { get; set; }

    [ObservableProperty]
    public partial string StatusText { get; set; } = "";

    public bool CanApply => ValidationError is null;

    // ── Named tunings ────────────────────────────────────────────────────────

    public ObservableCollection<CascadeTuningPreset> Presets { get; } = [];

    [ObservableProperty]
    public partial CascadeTuningPreset? SelectedPreset { get; set; }

    [ObservableProperty]
    public partial string PresetName { get; set; } = "";

    // ── Live terms (advisory) ────────────────────────────────────────────────

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ArmLabel))]
    public partial bool IsArmed { get; set; }

    public string ArmLabel => IsArmed ? "Parar cascata consultiva" : "Simular cascata (não envia)";

    // ── Automatic actuation (WP6) ────────────────────────────────────────────

    /// <summary>The three operator modes offered by the mode selector.</summary>
    public IReadOnlyList<CascadeModeOption> Modes { get; } =
    [
        new(CascadeMode.KlaPath, "Trajetória kLa (agitação + aeração)"),
        new(CascadeMode.AgitationOnly, "Somente agitação"),
        new(CascadeMode.AerationOnly, "Somente aeração"),
    ];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RequiresPath))]
    public partial CascadeModeOption? SelectedModeOption { get; set; }

    /// <summary>The kLa mode needs a published path; the fallback modes do not.</summary>
    public bool RequiresPath => SelectedModeOption?.Mode == CascadeMode.KlaPath;

    /// <summary>Published receipts available for the trajectory mode.</summary>
    public ObservableCollection<KlaPublishedProfile> AvailablePaths { get; } = [];

    [ObservableProperty]
    public partial KlaPublishedProfile? SelectedPath { get; set; }

    /// <summary>True while the cascade is actuating under Automatic ownership.</summary>
    public bool IsEngaged => _cascade.IsEngaged;

    /// <summary>Mode and path may only change while not engaged.</summary>
    public bool CanEditMode => !_cascade.IsEngaged;

    public string EngageLabel => IsEngaged ? "Assumir manual" : "Ativar Automático";

    /// <summary>Why the operator cannot engage right now, or null when they can.</summary>
    public string? EngageBlockedReason
    {
        get
        {
            if (IsEngaged)
            {
                return null;
            }

            _cascade.CanEngage(out var reason);
            return reason;
        }
    }

    public bool CanToggleEngage => IsEngaged || _cascade.CanEngage(out _);

    /// <summary>The live kLa demand while engaged on the trajectory, else an em dash.</summary>
    public string ActiveKlaText => _cascade.ActiveKlaDemand is { } kla
        ? kla.ToString("F1", CultureInfo.CurrentCulture) + " /h"
        : "—";

    /// <summary>The live PV/SP/kLa/output ring for the tuning chart.</summary>
    public CascadeTrend Trend => _cascade.Trend;

    [ObservableProperty] public partial string LiveOxygen { get; set; } = "—";
    [ObservableProperty] public partial string LivePredicted { get; set; } = "—";
    [ObservableProperty] public partial string LiveRate { get; set; } = "—";
    [ObservableProperty] public partial string LiveError { get; set; } = "—";
    [ObservableProperty] public partial string LiveProportional { get; set; } = "—";
    [ObservableProperty] public partial string LiveIntegral { get; set; } = "—";
    [ObservableProperty] public partial string LiveDerivative { get; set; } = "—";
    [ObservableProperty] public partial string LiveDeltaOutput { get; set; } = "—";
    [ObservableProperty] public partial string LiveOutput { get; set; } = "—";
    [ObservableProperty] public partial string LiveAgitation { get; set; } = "—";
    [ObservableProperty] public partial string LiveAeration { get; set; } = "—";

    [ObservableProperty]
    public partial bool IsSaturated { get; set; }

    // ── Gain scheduling (WP8) ────────────────────────────────────────────────
    // Off by default: the manuscript's single robust gain set works without it. When on, the
    // gains follow the control effort along a versioned schedule, with a bounded transition rate.

    [ObservableProperty]
    public partial bool GainScheduleEnabled { get; set; }

    /// <summary>Largest change in any gain per second — the bound on a transition.</summary>
    [ObservableProperty]
    public partial double MaxGainSlew { get; set; }

    [ObservableProperty]
    public partial bool HasScheduleChange { get; set; }

    /// <summary>The versioned schedule breakpoints, shown read-only (their gains come from tuning).</summary>
    public ObservableCollection<GainScheduleBreakpointSettings> ScheduleBreakpoints { get; } = [];

    /// <summary>True while a schedule is actively driving the gains this run.</summary>
    public bool IsGainSchedulingActive => _cascade.IsGainSchedulingEnabled;

    public string GainScheduleSummary =>
        $"Perfil v{_cascade.GainScheduleVersion} · {ScheduleBreakpoints.Count} pontos por esforço";

    /// <summary>The gains the schedule is currently applying, or an em dash when off.</summary>
    public string ScheduledGainsText => _cascade.ScheduledGains is { } g
        ? string.Create(CultureInfo.CurrentCulture, $"Kp {g.Kp:F3} · Ki {g.Ki:F4} · Kd {g.Kd:F3}")
        : "—";

    public string GainScheduleSegmentText => _cascade.IsGainSchedulingEnabled
        ? $"Segmento {_cascade.GainScheduleSegment}/{_cascade.GainScheduleSegmentCount}"
        : "—";

    partial void OnGainScheduleEnabledChanged(bool value) => RefreshScheduleChange();

    partial void OnMaxGainSlewChanged(double value) => RefreshScheduleChange();

    [RelayCommand]
    private void ApplyGainSchedule()
    {
        if (!double.IsFinite(MaxGainSlew) || MaxGainSlew <= 0)
        {
            StatusText = "O limite de variação do ganho deve ser um valor positivo.";
            return;
        }

        var current = _settings.Current.GainSchedule;
        var changed = current.Enabled != GainScheduleEnabled ||
                      Math.Abs(current.MaxGainSlewPerSecond - MaxGainSlew) > 1e-12;

        var schedule = current with
        {
            Enabled = GainScheduleEnabled,
            MaxGainSlewPerSecond = MaxGainSlew,
            // Each applied edit is a new version, so a schedule change is auditable.
            Version = changed ? current.Version + 1 : current.Version,
        };

        _cascade.ConfigureGainSchedule(schedule);
        _settings.Update(s => s with { GainSchedule = schedule });
        HasScheduleChange = false;
        RefreshScheduleLive();
        StatusText = GainScheduleEnabled
            ? $"Escalonamento de ganho ativado (perfil v{schedule.Version})."
            : "Escalonamento de ganho desativado; a cascata usa a sintonia única.";
    }

    [RelayCommand]
    private void RevertGainSchedule()
    {
        LoadSchedule(_settings.Current.GainSchedule);
        StatusText = "Alterações do escalonamento de ganho revertidas.";
    }

    // ── Allocation bar (0-100 % effort track, in star units) ─────────────────

    [ObservableProperty] public partial double AgitationBarStart { get; set; }
    [ObservableProperty] public partial double AgitationBarSpan { get; set; }
    [ObservableProperty] public partial double AgitationBarRest { get; set; }
    [ObservableProperty] public partial double AerationBarStart { get; set; }
    [ObservableProperty] public partial double AerationBarSpan { get; set; }
    [ObservableProperty] public partial double AerationBarRest { get; set; }

    /// <summary>Current effort as a star fraction of the 0-100 track, for the marker.</summary>
    [ObservableProperty] public partial double EffortMarker { get; set; }
    [ObservableProperty] public partial double EffortRest { get; set; } = 100;

    // ── Commands ─────────────────────────────────────────────────────────────

    [RelayCommand]
    private void ToggleArm()
    {
        if (_cascade.IsArmed)
        {
            _cascade.Disarm();
            StatusText = "Cascata consultiva parada.";
        }
        else
        {
            _cascade.Arm();
            StatusText = "Cascata consultiva iniciada. Ela calcula, mas não envia comandos.";
        }
    }

    partial void OnSelectedModeOptionChanged(CascadeModeOption? value)
    {
        if (_loading || value is null)
        {
            return;
        }

        _cascade.SelectMode(value.Mode);
        RefreshEngage();
    }

    partial void OnSelectedPathChanged(KlaPublishedProfile? value)
    {
        if (_loading)
        {
            return;
        }

        _cascade.SelectPath(value);
        RefreshEngage();
    }

    /// <summary>
    /// Engages or disengages live automatic actuation. Engaging claims the oxygen actuators
    /// through the arbiter and starts sending; the initial state is taken from the last
    /// applied agitation and flow so the transfer is bumpless.
    /// </summary>
    [RelayCommand]
    private void ToggleEngage()
    {
        if (_cascade.IsEngaged)
        {
            _cascade.Disengage("operador assumiu o controle manual");
            StatusText = "Automático desativado; o operador retomou o comando.";
        }
        else if (_cascade.CanEngage(out var reason))
        {
            var setpoints = _settings.Current.Setpoints;
            _cascade.Engage(setpoints.MotorRpm, setpoints.FlowLitresPerMinute);
            StatusText = "Automático ativado. A cascata assumiu agitação, aeração e o monitor de O₂.";
        }
        else
        {
            StatusText = reason ?? "Não é possível ativar o Automático agora.";
        }

        RefreshEngage();
    }

    [RelayCommand]
    private void ResetIntegral()
    {
        _cascade.ResetIntegral();
        StatusText = "Contribuição integral zerada.";
    }

    private async void LoadPathsSafe()
    {
        try
        {
            await _cascade.LoadAvailablePathsAsync().ConfigureAwait(true);
        }
        catch (Exception)
        {
            // A missing or unreadable receipt store must not take the workspace down; the
            // path list simply stays empty and the trajectory mode reports it cannot engage.
        }

        RebuildPaths();
    }

    private void RebuildPaths()
    {
        var previous = SelectedPath?.ReceiptFingerprint;
        AvailablePaths.Clear();
        foreach (var profile in _cascade.AvailablePaths)
        {
            AvailablePaths.Add(profile);
        }

        SelectedPath = AvailablePaths.FirstOrDefault(p => p.ReceiptFingerprint == previous)
            ?? (_cascade.ActivePath is { } active
                ? AvailablePaths.FirstOrDefault(p => p.ReceiptFingerprint == active.ReceiptFingerprint)
                : null);
        RefreshEngage();
    }

    private void RefreshEngage()
    {
        OnPropertyChanged(nameof(IsEngaged));
        OnPropertyChanged(nameof(CanEditMode));
        OnPropertyChanged(nameof(EngageLabel));
        OnPropertyChanged(nameof(EngageBlockedReason));
        OnPropertyChanged(nameof(CanToggleEngage));
        OnPropertyChanged(nameof(ActiveKlaText));
    }

    [RelayCommand(CanExecute = nameof(CanApply))]
    private void Apply()
    {
        Validate();
        if (!CanApply)
        {
            StatusText = "Revise os campos destacados antes de aplicar.";
            return;
        }

        var settings = BuildSettings();
        _cascade.Configure(settings);
        _settings.Update(s => s with { Cascade = settings });

        HasPendingChange = false;
        StatusText = "Sintonia aplicada à cascata consultiva. Nada foi enviado ao equipamento.";
        RefreshWindows();
    }

    [RelayCommand]
    private void Revert()
    {
        LoadFrom(_settings.Current.Cascade);
        StatusText = "Alterações de sintonia revertidas.";
    }

    [RelayCommand]
    private void SaveTuning()
    {
        var name = PresetName.Trim();
        if (name.Length == 0)
        {
            StatusText = "Informe um nome para a sintonia.";
            return;
        }

        Validate();
        if (!CanApply)
        {
            StatusText = "Corrija os campos antes de salvar a sintonia.";
            return;
        }

        var preset = new CascadeTuningPreset { Name = name, Settings = BuildSettings() };
        var existing = Presets.FirstOrDefault(p =>
            string.Equals(p.Name, name, StringComparison.CurrentCultureIgnoreCase));
        if (existing is null)
        {
            Presets.Add(preset);
        }
        else
        {
            Presets[Presets.IndexOf(existing)] = preset;
        }

        SortPresets();
        SelectedPreset = Presets.First(p =>
            string.Equals(p.Name, name, StringComparison.CurrentCultureIgnoreCase));
        PresetName = "";
        _settings.Update(s => s with { CascadeTuningPresets = [.. Presets] });
        StatusText = $"Sintonia “{name}” salva. Nenhum comando foi enviado.";
    }

    [RelayCommand]
    private void LoadTuning()
    {
        if (SelectedPreset is not { } preset)
        {
            StatusText = "Selecione uma sintonia para carregar.";
            return;
        }

        LoadFrom(preset.Settings, markPending: true);
        StatusText = $"Sintonia “{preset.Name}” carregada nos campos; nada foi aplicado nem enviado.";
    }

    // ── Internals ────────────────────────────────────────────────────────────

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);

        if (_loading || e.PropertyName is null || !EditableFields.Contains(e.PropertyName))
        {
            return;
        }

        Validate();
        HasPendingChange = !BuildSettings().Equals(_settings.Current.Cascade);
    }

    private void Validate()
    {
        var settings = BuildSettings();
        var issues = new List<string>(CascadeService.ToTuning(settings).Validate());
        issues.AddRange(Agitation(settings).Validate());
        issues.AddRange(Aeration(settings).Validate());

        if (settings.OxygenSetpointPercent is < 0 or > 100)
        {
            issues.Add("O setpoint de O₂ deve ficar entre 0 e 100 %.");
        }

        ValidationError = issues.Count > 0 ? issues[0] : null;
        OnPropertyChanged(nameof(CanApply));
        ApplyCommand.NotifyCanExecuteChanged();
    }

    private CascadeSettings BuildSettings() => new()
    {
        Kp = Kp,
        Ki = Ki,
        Kd = Kd,
        IntegralMin = IntegralMin,
        IntegralMax = IntegralMax,
        PredictionHorizonSeconds = PredictionHorizonSeconds,
        RateWindowSeconds = RateWindowSeconds,
        IntervalSeconds = IntervalSeconds,
        OxygenSetpointPercent = OxygenSetpoint,
        AgitationMinRpm = AgitationMinRpm,
        AgitationMaxRpm = AgitationMaxRpm,
        AgitationEffortStart = AgitationEffortStart,
        AgitationEffortEnd = AgitationEffortEnd,
        AerationMinLpm = AerationMinLpm,
        AerationMaxLpm = AerationMaxLpm,
        AerationEffortStart = AerationEffortStart,
        AerationEffortEnd = AerationEffortEnd,
    };

    private static ActuatorWindow Agitation(CascadeSettings c) => new(
        CascadeController.AgitationActuator,
        c.AgitationMinRpm, c.AgitationMaxRpm, c.AgitationEffortStart, c.AgitationEffortEnd);

    private static ActuatorWindow Aeration(CascadeSettings c) => new(
        CascadeController.AerationActuator,
        c.AerationMinLpm, c.AerationMaxLpm, c.AerationEffortStart, c.AerationEffortEnd);

    private void LoadFrom(CascadeSettings c, bool markPending = false)
    {
        _loading = true;

        Kp = c.Kp;
        Ki = c.Ki;
        Kd = c.Kd;
        IntegralMin = c.IntegralMin;
        IntegralMax = c.IntegralMax;
        PredictionHorizonSeconds = c.PredictionHorizonSeconds;
        RateWindowSeconds = c.RateWindowSeconds;
        IntervalSeconds = c.IntervalSeconds;
        OxygenSetpoint = c.OxygenSetpointPercent;
        AgitationMinRpm = c.AgitationMinRpm;
        AgitationMaxRpm = c.AgitationMaxRpm;
        AgitationEffortStart = c.AgitationEffortStart;
        AgitationEffortEnd = c.AgitationEffortEnd;
        AerationMinLpm = c.AerationMinLpm;
        AerationMaxLpm = c.AerationMaxLpm;
        AerationEffortStart = c.AerationEffortStart;
        AerationEffortEnd = c.AerationEffortEnd;

        _loading = false;

        Validate();
        HasPendingChange = markPending;
    }

    private void OnCascadeUpdated()
    {
        RefreshLive();
        RefreshScheduleLive();
        if (IsArmed != _cascade.IsArmed)
        {
            IsArmed = _cascade.IsArmed;
        }

        // A safe abort disengages the cascade from under the operator; keep the button and
        // the kLa readout in step with the service.
        RefreshEngage();
    }

    private void LoadSchedule(GainScheduleSettings s)
    {
        _loading = true;
        GainScheduleEnabled = s.Enabled;
        MaxGainSlew = s.MaxGainSlewPerSecond;
        ScheduleBreakpoints.Clear();
        foreach (var breakpoint in s.Breakpoints.OrderBy(b => b.EffortPercent))
        {
            ScheduleBreakpoints.Add(breakpoint);
        }

        _loading = false;
        HasScheduleChange = false;
        RefreshScheduleLive();
    }

    private void RefreshScheduleChange()
    {
        if (_loading)
        {
            return;
        }

        var current = _settings.Current.GainSchedule;
        HasScheduleChange = current.Enabled != GainScheduleEnabled ||
                            Math.Abs(current.MaxGainSlewPerSecond - MaxGainSlew) > 1e-12;
    }

    private void RefreshScheduleLive()
    {
        OnPropertyChanged(nameof(IsGainSchedulingActive));
        OnPropertyChanged(nameof(ScheduledGainsText));
        OnPropertyChanged(nameof(GainScheduleSegmentText));
        OnPropertyChanged(nameof(GainScheduleSummary));
    }

    private void RefreshLive()
    {
        IsArmed = _cascade.IsArmed;

        var c = CultureInfo.CurrentCulture;
        LiveOxygen = _cascade.LatestOxygen is { } o ? o.ToString("F1", c) + " %" : "—";

        var terms = _cascade.Terms;
        if (!_cascade.IsArmed)
        {
            LivePredicted = LiveRate = LiveError = LiveProportional = LiveIntegral =
                LiveDerivative = LiveDeltaOutput = LiveOutput = LiveAgitation = LiveAeration = "—";
            IsSaturated = false;
            EffortMarker = 0;
            EffortRest = 100;
            return;
        }

        LivePredicted = terms.PredictedMeasurement.ToString("F1", c) + " %";
        LiveRate = terms.MeasurementRate.ToString("F3", c) + " %/s";
        LiveError = terms.Error.ToString("+0.0;-0.0;0.0", c) + " %";
        LiveProportional = terms.Proportional.ToString("F2", c);
        LiveIntegral = terms.Integral.ToString("F2", c);
        LiveDerivative = terms.Derivative.ToString("F2", c);
        LiveDeltaOutput = terms.DeltaOutput.ToString("+0.00;-0.00;0.00", c);
        LiveOutput = terms.Output.ToString("F1", c) + " %";
        IsSaturated = terms.Saturated;

        if (_cascade.LastActuation is { } actuation)
        {
            LiveAgitation = actuation.AgitationRpm.ToString(c) + " rpm";
            LiveAeration = actuation.AerationLpm.ToString("F2", c) + " L/min";
        }

        EffortMarker = Math.Clamp(terms.Output, 0, 100);
        EffortRest = 100 - EffortMarker;
    }

    private void RefreshWindows()
    {
        var agitation = _cascade.Windows.FirstOrDefault(w => w.Name == CascadeController.AgitationActuator);
        var aeration = _cascade.Windows.FirstOrDefault(w => w.Name == CascadeController.AerationActuator);

        if (agitation is not null)
        {
            AgitationBarStart = Math.Clamp(agitation.EffortStart, 0, 100);
            AgitationBarSpan = Math.Clamp(agitation.EffortEnd - agitation.EffortStart, 0, 100);
            AgitationBarRest = Math.Max(0, 100 - AgitationBarStart - AgitationBarSpan);
        }

        if (aeration is not null)
        {
            AerationBarStart = Math.Clamp(aeration.EffortStart, 0, 100);
            AerationBarSpan = Math.Clamp(aeration.EffortEnd - aeration.EffortStart, 0, 100);
            AerationBarRest = Math.Max(0, 100 - AerationBarStart - AerationBarSpan);
        }
    }

    private void SortPresets()
    {
        var ordered = Presets.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
        Presets.Clear();
        foreach (var preset in ordered)
        {
            Presets.Add(preset);
        }
    }

    public void Dispose() => _cascade.Updated -= OnCascadeUpdated;
}
