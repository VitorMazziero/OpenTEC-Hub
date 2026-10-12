using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenTECHub.Protocol;
using OpenTECHub.Services.Communication;
using OpenTECHub.Services.Control;
using OpenTECHub.Services.Persistence;

namespace OpenTECHub.ViewModels;

/// <summary>
/// The pulse panel of the external pump card (plan 2026-10-11 §4, §5.2, Phase 6): the target,
/// the granularity slider with its precision zones, start and stop with echo confirmation, and
/// the live delivered-volume overlay with its deviation (Phase 7).
/// </summary>
/// <remarks>
/// <para>
/// With the mean flow and the dose point fixed, the fraction of time the motor runs is fixed;
/// the slider only chooses how to split the same volume in time (<see cref="PumpDoseMath"/>).
/// Its position is kept as the period, so what is saved is what would be sent. A period that
/// falls out of range after the target changes is clipped and the operator is told.
/// </para>
/// <para>
/// Start sends the six parameters and <c>pump_dose:1</c> in one frame through the manual
/// dispatcher (the arbiter's <see cref="ActuatorId.ExternalPump"/>). It counts as confirmed only
/// when <c>PumpDoseActive</c> rises and the echoed parameters match within 0,0005 relative, in
/// 15 s (§7.5); a change of <c>PumpRejCmdId</c> means refused. The parent forwards every
/// snapshot and refusal (<see cref="OnSnapshot"/>, <see cref="OnRefused"/>).
/// </para>
/// </remarks>
public sealed partial class PumpDosingViewModel : ObservableObject
{
    private const double EchoTolerance = 0.0005;
    private static readonly TimeSpan EchoTimeout = TimeSpan.FromSeconds(15);

    private readonly ISettingsService _settings;
    private readonly IManualDispatcher _dispatcher;
    private readonly ExternalDeviceStatus _status;
    private readonly TimeProvider _time;
    private readonly Func<bool> _isOwnedByOther;
    private readonly Func<string?> _ownerLockReason;
    private readonly PumpDeviationTracker _deviation = new();
    private readonly PumpLiveSeries _live = new();

    private bool _loading;
    private bool _syncingSlider;
    private PumpDoseRange? _range;
    private SensorSnapshot? _last;

    private PumpDoseParameters? _awaitingStart;
    private DateTime? _startSentAt;
    private bool _awaitingStop;
    private DateTime? _stopSentAt;
    private bool? _wasActive;
    private double _lastDelivered;
    private long _lastPulses;
    private bool _stopRequested;

    public PumpDosingViewModel(
        ISettingsService settings,
        IManualDispatcher dispatcher,
        ExternalDeviceStatus status,
        TimeProvider? timeProvider = null,
        Func<bool>? isOwnedByOther = null,
        Func<string?>? ownerLockReason = null)
    {
        _settings = settings;
        _dispatcher = dispatcher;
        _status = status;
        _time = timeProvider ?? TimeProvider.System;
        _isOwnedByOther = isOwnedByOther ?? (() => false);
        _ownerLockReason = ownerLockReason ?? (() => null);

        var dose = settings.Current.PumpControl.Dose;
        _loading = true;
        IsTargetByVolume = dose.TargetByVolume;
        TargetVolumeText = Format(dose.TargetVolumeMl, "0.###");
        TargetHoursText = Format(dose.TargetHours, "0.###");
        MeanFlowText = Format(dose.MeanFlowMlMin, "0.#####");
        StopAtTotal = dose.StopAtTotal;
        MaxIntervalMinutesText = Format(dose.MaxIntervalS / 60.0, "0.##");
        PeriodS = dose.PeriodS;
        _loading = false;

        _settings.Changed += OnSettingsChanged;
        _status.PropertyChanged += (_, _) => NotifyCommands();
        Recompute(keepPeriod: true);
    }

    // ---- Target ------------------------------------------------------------------------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsTargetByFlow))]
    public partial bool IsTargetByVolume { get; set; } = true;

    public bool IsTargetByFlow
    {
        get => !IsTargetByVolume;
        set
        {
            if (value)
            {
                IsTargetByVolume = false;
            }
        }
    }

    [ObservableProperty] public partial string TargetVolumeText { get; set; } = "50";
    [ObservableProperty] public partial string TargetHoursText { get; set; } = "12";
    [ObservableProperty] public partial string MeanFlowText { get; set; } = "0.0694";
    [ObservableProperty] public partial bool StopAtTotal { get; set; } = true;
    [ObservableProperty] public partial string MaxIntervalMinutesText { get; set; } = "30";

    partial void OnIsTargetByVolumeChanged(bool value) => Recompute(keepPeriod: true);
    partial void OnTargetVolumeTextChanged(string value) => Recompute(keepPeriod: true);
    partial void OnTargetHoursTextChanged(string value) => Recompute(keepPeriod: true);
    partial void OnMeanFlowTextChanged(string value) => Recompute(keepPeriod: true);
    partial void OnStopAtTotalChanged(bool value) => Recompute(keepPeriod: true);
    partial void OnMaxIntervalMinutesTextChanged(string value) => Recompute(keepPeriod: true);

    // ---- Dose point --------------------------------------------------------------------

    public bool HasDosePoint => _settings.Current.PumpControl.Dose.Point is not null;

    [ObservableProperty] public partial string DosePointText { get; set; } = "";

    // ---- Slider ------------------------------------------------------------------------

    /// <summary>0 = many small pulses, 1 = few large ones (log scale between the range ends).</summary>
    [ObservableProperty] public partial double SliderPosition { get; set; }

    /// <summary>The chosen period between pulse starts, s; null until a range exists.</summary>
    public double? PeriodS { get; private set; }

    /// <summary>Slider fraction where the amber (low precision) zone ends and green starts.</summary>
    [ObservableProperty] public partial double AmberFraction { get; set; }

    public System.Windows.GridLength AmberWidth => new(Math.Max(AmberFraction, 0.0001), System.Windows.GridUnitType.Star);
    public System.Windows.GridLength GreenWidth => new(Math.Max(1.0 - AmberFraction, 0.0001), System.Windows.GridUnitType.Star);

    partial void OnAmberFractionChanged(double value)
    {
        OnPropertyChanged(nameof(AmberWidth));
        OnPropertyChanged(nameof(GreenWidth));
    }

    partial void OnSliderPositionChanged(double value)
    {
        if (_syncingSlider || _range is not { Feasible: true } range)
        {
            return;
        }

        PeriodS = PumpDoseMath.SliderToPeriod(value, range);
        ClampNotice = null;
        Recompute(keepPeriod: true);
    }

    /// <summary>Exact period entry ("Editar valores exatos"), s.</summary>
    [ObservableProperty] public partial string ExactPeriodText { get; set; } = "";

    /// <summary>Exact volume per pulse entry, mL; sets the period from the mean flow.</summary>
    [ObservableProperty] public partial string ExactVolumeText { get; set; } = "";

    [RelayCommand]
    private void ApplyExactPeriod()
    {
        if (DosingInput.TryParseDouble(ExactPeriodText, out var period) && period > 0)
        {
            PeriodS = period;
            Recompute(keepPeriod: true);
        }
        else
        {
            ClampNotice = "Período: informe segundos > 0.";
        }
    }

    [RelayCommand]
    private void ApplyExactVolume()
    {
        if (DosingInput.TryParseDouble(ExactVolumeText, out var volume) && volume > 0 && TryMeanFlow(out var q, out _))
        {
            PeriodS = 60.0 * volume / q;
            Recompute(keepPeriod: true);
        }
        else
        {
            ClampNotice = "Volume por pulso: informe mL > 0 com uma meta válida.";
        }
    }

    // ---- Plan readouts -------------------------------------------------------------------

    [ObservableProperty] public partial PumpDosePlan? Plan { get; set; }
    [ObservableProperty] public partial PumpDosePreview? Preview { get; set; }
    [ObservableProperty] public partial double MeanFlowMlMin { get; set; }
    [ObservableProperty] public partial string? PlanError { get; set; }
    [ObservableProperty] public partial string? ClampNotice { get; set; }
    [ObservableProperty] public partial string PulseText { get; set; } = "—";
    [ObservableProperty] public partial string ScheduleText { get; set; } = "—";
    [ObservableProperty] public partial string StartErrorText { get; set; } = "";
    [ObservableProperty] public partial string ZoneText { get; set; } = "";
    [ObservableProperty] public partial PumpDoseZone? Zone { get; set; }

    // ---- Node state ----------------------------------------------------------------------

    /// <summary>Online pump without any 3.13 field: pulse dosing needs pump 3.13.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanStart))]
    public partial bool NodeLacksDosing { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanStart))]
    [NotifyPropertyChangedFor(nameof(CanStop))]
    [NotifyPropertyChangedFor(nameof(CanEditPlan))]
    public partial bool IsDosing { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanStart))]
    [NotifyPropertyChangedFor(nameof(CanStop))]
    [NotifyPropertyChangedFor(nameof(CanEditPlan))]
    public partial bool IsAwaitingEcho { get; set; }

    /// <summary>
    /// The plan is frozen while the node doses (or a command is in flight): the panel then
    /// shows what runs, not an edit the node never received. Stop, edit, start again.
    /// </summary>
    public bool CanEditPlan => !IsDosing && !IsAwaitingEcho;

    [ObservableProperty] public partial string RunText { get; set; } = "";
    [ObservableProperty] public partial string StatusText { get; set; } = "";

    /// <summary>Delivered − planned at the node's current time (Phase 7).</summary>
    [ObservableProperty] public partial string DeviationText { get; set; } = "";
    [ObservableProperty] public partial bool IsDeviationAlert { get; set; }

    [ObservableProperty] public partial IReadOnlyList<double>? LiveMinutes { get; set; }
    [ObservableProperty] public partial IReadOnlyList<double>? LiveFlow { get; set; }
    [ObservableProperty] public partial IReadOnlyList<double>? LiveVolume { get; set; }

    public bool CanStart => Plan is { Zone: not PumpDoseZone.Unavailable } && !IsDosing && !IsAwaitingEcho &&
                            !NodeLacksDosing && _status.IsOnline && _status.CanSend && !_isOwnedByOther();

    public bool CanStop => (IsDosing || IsAwaitingEcho) && _status.IsOnline && !_isOwnedByOther();

    [RelayCommand(CanExecute = nameof(CanStart))]
    private void Start()
    {
        if (_isOwnedByOther())
        {
            StatusText = _ownerLockReason() ?? "Operação bloqueada pelo controlador atual.";
            return;
        }

        if (Plan is not { } plan || PumpDoseRules.Validate(plan.Parameters) is { } code)
        {
            StatusText = "Revise a meta e a granularidade antes de iniciar.";
            return;
        }

        var result = _dispatcher.Dispatch(CommandBuilders.PumpDoseStart(plan.Parameters));
        if (!result.Accepted)
        {
            StatusText = DispatchRefusal.Describe(result);
            return;
        }

        _awaitingStart = plan.Parameters;
        _startSentAt = _time.GetUtcNow().UtcDateTime;
        _stopRequested = false;
        IsAwaitingEcho = true;
        _status.MarkCommandDispatched();
        Save(plan.PeriodS, runStartedUtc: null);
        StatusText = "Dosagem enviada; aguardando confirmação do nó (eco)…";
        NotifyCommands();
    }

    [RelayCommand(CanExecute = nameof(CanStop))]
    private void Stop()
    {
        var result = _dispatcher.Dispatch(CommandBuilders.PumpDoseStop());
        if (!result.Accepted)
        {
            StatusText = DispatchRefusal.Describe(result);
            return;
        }

        _awaitingStart = null;
        _startSentAt = null;
        _awaitingStop = true;
        _stopSentAt = _time.GetUtcNow().UtcDateTime;
        _stopRequested = true;
        IsAwaitingEcho = true;
        _status.MarkCommandDispatched();
        StatusText = "Parada da dosagem enviada; aguardando o nó…";
        NotifyCommands();
    }

    /// <summary>Called by the parent when <c>PumpRejCmdId</c> changes: the awaited echo will not come.</summary>
    public void OnRefused(string? code)
    {
        if (_awaitingStart is null && !_awaitingStop)
        {
            return;
        }

        _awaitingStart = null;
        _startSentAt = null;
        _awaitingStop = false;
        _stopSentAt = null;
        IsAwaitingEcho = false;
        StatusText = $"A bomba recusou a dosagem: {PumpDoseRules.DescribeRefusal(code)}.";
        NotifyCommands();
    }

    /// <summary>The parent's telemetry tick.</summary>
    public void OnSnapshot(SensorSnapshot s)
    {
        _last = s;
        var online = s.PumpOnline;
        NodeLacksDosing = online && s.PumpDrive is null && s.PumpDoseActive is null;
        var active = online && s.PumpDoseActive == true;
        var now = _time.GetUtcNow().UtcDateTime;

        // Start confirmation: active, and the echoed parameters are the ones sent.
        if (_awaitingStart is { } sent)
        {
            if (active && EchoMatches(s, sent))
            {
                _awaitingStart = null;
                _startSentAt = null;
                IsAwaitingEcho = false;
                _live.Clear();
                _deviation.Reset();
                Save(sent.PeriodS, runStartedUtc: now.ToString("O", CultureInfo.InvariantCulture));
                StatusText = "Dosagem confirmada pelo nó (eco).";
            }
            else if (now - _startSentAt > EchoTimeout)
            {
                _awaitingStart = null;
                _startSentAt = null;
                IsAwaitingEcho = false;
                StatusText = active
                    ? "O nó dosa com parâmetros diferentes dos enviados; confira a leitura e reenvie."
                    : "O nó não confirmou a dosagem em 15 s; confira a conexão e tente de novo.";
            }
        }

        if (_awaitingStop)
        {
            if (online && s.PumpDoseActive == false)
            {
                _awaitingStop = false;
                _stopSentAt = null;
                IsAwaitingEcho = false;
            }
            else if (now - _stopSentAt > EchoTimeout)
            {
                _awaitingStop = false;
                _stopSentAt = null;
                IsAwaitingEcho = false;
                StatusText = "O nó não confirmou a parada em 15 s; use o interruptor da linha para a parada segura.";
            }
        }

        // End of a run: completed or interrupted.
        if (_wasActive == true && online && s.PumpDoseActive == false)
        {
            var total = s.PumpDoseTotal ?? 0.0;
            var done = s.PumpDoseDelivered ?? _lastDelivered;
            var pulses = s.PumpDosePulses ?? _lastPulses;
            var c = CultureInfo.CurrentCulture;
            StatusText = total > 0 && done >= total - Math.Max(1e-6, (s.PumpDoseMl ?? 0) * 0.5)
                ? $"Concluída: {done.ToString("F2", c)} mL em {pulses} pulsos."
                : _stopRequested
                    ? $"Parada pelo operador em {done.ToString("F2", c)} mL."
                    : $"Interrompida em {done.ToString("F2", c)} mL" +
                      (s.PumpLiquidOk == false ? " (sensor de líquido seco)." : s.PumpDrive == (int)PumpDriveState.ProfileRunning ? " (perfil contínuo iniciado)." : " (modo 0 ou roteamento desligado).");
        }

        if (online && s.PumpDoseActive is { } a)
        {
            _wasActive = a;
        }

        IsDosing = active;
        _lastDelivered = s.PumpDoseDelivered ?? _lastDelivered;
        _lastPulses = s.PumpDosePulses ?? _lastPulses;
        UpdateRunText(s);
        UpdateLive(s);
        NotifyCommands();
    }

    /// <summary>Re-reads the dose point after Calibrações saves or chooses one.</summary>
    private void OnSettingsChanged(AppSettings settings)
    {
        OnPropertyChanged(nameof(HasDosePoint));
        Recompute(keepPeriod: true);
    }

    private void Recompute(bool keepPeriod)
    {
        if (_loading)
        {
            return;
        }

        var point = _settings.Current.PumpControl.Dose.Point;
        var c = CultureInfo.CurrentCulture;
        DosePointText = point is null
            ? "Sem ponto de dosagem: calibre em Calibrações › Bomba Externa › Ponto de dosagem."
            : $"S {point.SpeedUnits.ToString("F0", c)} → {point.RateMlMin.ToString("F2", c)} mL/min" +
              (point.DeadTimeMs is { } t0 ? $" · t₀ {t0.ToString("F0", c)} ms" : " · t₀ não medido") +
              (point.ProfileName is { Length: > 0 } name ? $" · {name}" : "");

        if (point is null)
        {
            SetNoPlan("Sem ponto de dosagem.");
            return;
        }

        if (!TryMeanFlow(out var q, out var total))
        {
            SetNoPlan(IsTargetByVolume ? "Meta: volume > 0 mL em duração > 0 h." : "Meta: vazão média > 0 mL/min.");
            return;
        }

        if (!DosingInput.TryParseDouble(MaxIntervalMinutesText, out var maxMin) || maxMin <= 0)
        {
            SetNoPlan("Intervalo máximo: minutos > 0.");
            return;
        }

        MeanFlowMlMin = q;
        var source = new PumpDoseSource(point.SpeedUnits, point.RateMlMin, point.DeadTimeMs, point.RecommendedMinOnMs);
        var limits = new PumpDoseLimits(maxMin * 60.0);
        var range = PumpDoseMath.Range(q, source, limits);
        _range = range;
        if (!range.Feasible)
        {
            SetNoPlan(range.Reason);
            return;
        }

        var wanted = keepPeriod && PeriodS is { } p ? p : range.RecommendedMinPeriodS;
        var plan = PumpDoseMath.Plan(q, total, source, wanted, limits)!;
        if (PeriodS is { } before && Math.Abs(plan.PeriodS - PumpDoseMath.RoundPeriod(before)) > 1e-6)
        {
            ClampNotice = $"O período salvo ({FormatInterval(before)}) saiu da faixa da nova meta e foi ajustado para {FormatInterval(plan.PeriodS)}.";
        }

        PeriodS = plan.PeriodS;
        Plan = plan;
        PlanError = null;
        AmberFraction = PumpDoseMath.PeriodToSlider(range.RecommendedMinPeriodS, range);
        _syncingSlider = true;
        SliderPosition = PumpDoseMath.PeriodToSlider(plan.PeriodS, range);
        _syncingSlider = false;
        ExactPeriodText = plan.PeriodS.ToString("0.#", c);
        ExactVolumeText = plan.VolumeMl.ToString("0.####", c);

        PulseText = $"{plan.VolumeMl.ToString("0.###", c)} mL · {(plan.OnMs / 1000.0).ToString("0.0#", c)} s · a cada {FormatInterval(plan.PeriodS)} · {plan.PulsesPerHour.ToString("0.#", c)}/h";
        ScheduleText = plan.TotalPulses is { } n
            ? $"{n.ToString("0", c)} pulsos · total {total.ToString("0.##", c)} mL em ≈ {FormatDuration(n * plan.PeriodS)}"
            : $"sem limite de volume · média {q.ToString("0.####", c)} mL/min";
        StartErrorText = plan.StartErrorFraction is { } e
            ? $"erro de partida ≈ {(e * 100).ToString("0.#", c)} % (compensado com t₀)"
            : "erro de partida: não medido (sem verificação em pulsos)";
        Zone = plan.Zone;
        ZoneText = plan.Zone switch
        {
            PumpDoseZone.Unavailable => "Pulso curto demais: o nó recusaria.",
            PumpDoseZone.LowPrecision => "Precisão baixa: o pulso é menor que o mínimo recomendado.",
            _ => "Precisão boa.",
        };
        Preview = PumpDoseMath.Preview(plan, runMinutes: IsTargetByVolume && DosingInput.TryParseDouble(TargetHoursText, out var h) ? h * 60.0 : 720.0);
        _deviation.HoldMinutes = Math.Max(2.0, 2.0 * plan.PeriodS / 60.0);
        NotifyCommands();
    }

    private void SetNoPlan(string? reason)
    {
        Plan = null;
        Preview = null;
        Zone = null;
        PlanError = reason;
        PulseText = "—";
        ScheduleText = "—";
        StartErrorText = "";
        ZoneText = "";
        NotifyCommands();
    }

    private bool TryMeanFlow(out double meanFlow, out double total)
    {
        meanFlow = 0;
        total = 0;
        if (IsTargetByVolume)
        {
            if (!DosingInput.TryParseDouble(TargetVolumeText, out var v) || v <= 0 ||
                !DosingInput.TryParseDouble(TargetHoursText, out var h) || h <= 0)
            {
                return false;
            }

            meanFlow = v / (h * 60.0);
            total = StopAtTotal ? v : 0.0;
            return true;
        }

        if (!DosingInput.TryParseDouble(MeanFlowText, out var q) || q <= 0)
        {
            return false;
        }

        meanFlow = q;
        return true;
    }

    private static bool EchoMatches(SensorSnapshot s, PumpDoseParameters p)
    {
        static bool Near(double? echoed, double sent)
            => echoed is { } e && Math.Abs(e - sent) <= EchoTolerance * Math.Max(1.0, Math.Abs(sent));

        return Near(s.PumpDoseMl, p.VolumeMl) && Near(s.PumpDosePeriodS, p.PeriodS) &&
               Near(s.PumpDoseSpeed, p.Speed) && Near(s.PumpDoseRate, p.RateMlMin) &&
               Near(s.PumpDoseTotal, p.TotalMl) && (s.PumpDoseDeadMs is null || Near(s.PumpDoseDeadMs, p.DeadMs));
    }

    private void UpdateRunText(SensorSnapshot s)
    {
        var c = CultureInfo.CurrentCulture;
        if (!s.PumpOnline || s.PumpDoseActive != true)
        {
            RunText = "";
            return;
        }

        var done = s.PumpDoseDelivered ?? 0.0;
        var total = s.PumpDoseTotal ?? 0.0;
        var pulses = s.PumpDosePulses ?? 0;
        RunText = (s.PumpDosePulse == true ? "● pulso " : "Dosando · pulso ") + pulses.ToString(c) +
                  (Plan?.TotalPulses is { } n ? $" de {n.ToString("0", c)}" : "") +
                  $" · {done.ToString("F2", c)}" + (total > 0 ? $" / {total.ToString("F1", c)} mL ({done / total * 100.0:F0} %)" : " mL") +
                  (s.PumpDosePulse != true && s.PumpDoseNextS is { } next ? $" · próximo em {next / 60}:{next % 60:00}" : "") +
                  (s.PumpDoseShortfall is { } sh && sh > 0 ? $" · {sh.ToString("F2", c)} mL não entregues (sensor)" : "");
    }

    /// <summary>
    /// Live delivered volume on the plan's clock: elapsed = n·T − next (§5.4, with n the finished pulses), which the node's
    /// own counters give even after the app restarts.
    /// </summary>
    private void UpdateLive(SensorSnapshot s)
    {
        if (!s.PumpOnline || s.PumpDoseActive != true || s.PumpDosePeriodS is not { } period || period <= 0 ||
            s.PumpDosePulses is not { } n || s.PumpDoseDelivered is not { } done)
        {
            if (s.PumpOnline && s.PumpDoseActive == false)
            {
                DeviationText = "";
                IsDeviationAlert = false;
            }

            return;
        }

        // d_n counts finished pulses and pulse k starts at k·T: between pulses the next one
        // (index n) starts in d_next seconds; during a pulse it started at n·T.
        var next = s.PumpDoseNextS ?? 0;
        var elapsedS = s.PumpDosePulse == true ? n * period : Math.Max(0.0, (n * period) - next);

        var minutes = elapsedS / 60.0;
        var flow = s.PumpFlow > SensorReadings.NotReceived ? s.PumpFlow : 0.0;
        _live.Add(minutes, flow, done);
        (LiveMinutes, LiveFlow, LiveVolume) = _live.Snapshot();

        // Planned: pulses that should have finished by now times the volume per pulse.
        var v = s.PumpDoseMl ?? Plan?.VolumeMl ?? 0.0;
        var onS = PumpDoseRules.PulseMs(v, s.PumpDoseRate ?? Plan?.Parameters.RateMlMin ?? 1.0, s.PumpDoseDeadMs ?? 0.0) / 1000.0;
        var plannedPulses = elapsedS + 1e-9 >= onS ? Math.Floor((elapsedS - onS) / period) + 1 : 0;
        var planned = plannedPulses * v;
        if (s.PumpDoseTotal is { } tot && tot > 0)
        {
            planned = Math.Min(planned, tot);
        }

        _deviation.Update(minutes, planned, done);
        var c = CultureInfo.CurrentCulture;
        var dev = _deviation.DeviationMl ?? 0.0;
        DeviationText = $"Desvio {(dev >= 0 ? "+" : "−")}{Math.Abs(dev).ToString("F3", c)} mL" +
                        (_deviation.Relative is { } r ? $" ({(r * 100).ToString("+0.#;−0.#;0", c)} %)" : "");
        IsDeviationAlert = _deviation.IsAlert;
        if (IsDeviationAlert)
        {
            DeviationText += " — acima de 5 % há mais de dois períodos";
        }
    }

    private void Save(double periodS, string? runStartedUtc)
    {
        DosingInput.TryParseDouble(TargetVolumeText, out var v);
        DosingInput.TryParseDouble(TargetHoursText, out var h);
        DosingInput.TryParseDouble(MeanFlowText, out var q);
        DosingInput.TryParseDouble(MaxIntervalMinutesText, out var maxMin);
        _settings.Changed -= OnSettingsChanged;
        try
        {
            _settings.Update(s => s with
            {
                PumpControl = s.PumpControl with
                {
                    Dose = s.PumpControl.Dose with
                    {
                        TargetByVolume = IsTargetByVolume,
                        TargetVolumeMl = v > 0 ? v : s.PumpControl.Dose.TargetVolumeMl,
                        TargetHours = h > 0 ? h : s.PumpControl.Dose.TargetHours,
                        MeanFlowMlMin = q > 0 ? q : s.PumpControl.Dose.MeanFlowMlMin,
                        StopAtTotal = StopAtTotal,
                        PeriodS = periodS,
                        MaxIntervalS = maxMin > 0 ? maxMin * 60.0 : s.PumpControl.Dose.MaxIntervalS,
                        RunStartedUtc = runStartedUtc ?? s.PumpControl.Dose.RunStartedUtc,
                    },
                },
            });
        }
        finally
        {
            _settings.Changed += OnSettingsChanged;
        }
    }

    /// <summary>Re-evaluates Start/Stop after the owner or the node status changed.</summary>
    public void NotifyCommands()
    {
        OnPropertyChanged(nameof(CanStart));
        OnPropertyChanged(nameof(CanStop));
        StartCommand.NotifyCanExecuteChanged();
        StopCommand.NotifyCanExecuteChanged();
    }

    public void Detach() => _settings.Changed -= OnSettingsChanged;

    private static string Format(double value, string format) => value.ToString(format, CultureInfo.CurrentCulture);

    public static string FormatInterval(double seconds)
    {
        var s = (long)Math.Round(seconds);
        return s < 3600 ? $"{s / 60}:{s % 60:00}" : $"{s / 3600}:{s % 3600 / 60:00}:{s % 60:00}";
    }

    private static string FormatDuration(double seconds)
        => seconds < 3600 ? $"{seconds / 60:0} min" : $"{seconds / 3600:0.#} h";
}
