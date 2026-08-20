using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TecnalHub.Protocol;
using TecnalHub.Services.Communication;

namespace TecnalHub.ViewModels;

/// <summary>
/// Everything that distinguishes one controllable subsystem from another.
/// </summary>
/// <param name="Minimum">Lowest value the device accepts, excluding the off value.</param>
/// <param name="Maximum">Highest value the device accepts.</param>
/// <param name="IsInteger">True when the wire carries an int, as agitation does.</param>
/// <param name="BuildApply">Command for an enabled subsystem at the given value.</param>
/// <param name="BuildDisable">Command that safely turns the subsystem off.</param>
/// <remarks>
/// Range and command construction live together on purpose: validation and the wire
/// must agree, and separating them is how a UI comes to accept a value the device
/// will reject.
/// </remarks>
/// <param name="HasOutput">
/// True where the app can show what the actuator is actually doing. Flow can: the wire
/// reports valve states and the vent flag. Temperature cannot - its controller lives
/// inside the device.
/// </param>
/// <param name="HasCascade">True once an app-side cascade drives this loop. Phase 2.</param>
/// <param name="HasPid">
/// True only where an app-side controller exists to have terms. <b>The firmware has no
/// PID keys at all</b> - it accepts setpoints - so a PID tab anywhere else would describe
/// a controller this application cannot observe. See docs/UI_DESIGN.md section 2, item 7.
/// </param>
/// <param name="HasCalibration">True where raw counts are decoded on the PC side.</param>
/// <param name="HasHealth">False where the wire reports nothing to be healthy about.</param>
/// <param name="OnCommitted">
/// Optional callback after a command is accepted for sending. Flow uses it to commit
/// the staged <c>maxFlow</c> value carried in every flow command.
/// </param>
public sealed record SubsystemSpec(
    double Minimum,
    double Maximum,
    bool IsInteger,
    Func<double, TecnalCommand> BuildApply,
    Func<TecnalCommand> BuildDisable,
    bool HasOutput = false,
    bool HasCascade = false,
    bool HasPid = false,
    bool HasCalibration = false,
    bool HasHealth = true,
    Action<double, bool>? OnCommitted = null);

/// <summary>
/// One controllable subsystem: its live reading, its setpoint entry, and the rules
/// that decide whether that entry may be sent.
/// </summary>
/// <remarks>
/// <para>
/// <b>Invalid input is never silently replaced.</b> v.6 wrapped every setpoint parse
/// in a bare <c>except</c> that substituted a plausible default - a typo in the pH
/// field sent setpoint 7 to the reactor and said nothing. Here a value that does not
/// parse, or falls outside the device's range, blocks the send and is shown as an
/// error. See <c>docs/UI_DESIGN.md</c> section 9.
/// </para>
/// <para>
/// A typed value that has not yet been acknowledged is also visually distinct from
/// one the device has confirmed, which v.6 gave no indication of at all.
/// </para>
/// </remarks>
public sealed partial class SubsystemViewModel : ObservableObject
{
    private readonly IDeviceService _device;
    private SubsystemSpec _spec;

    /// <summary>
    /// Suppresses the pending-change flag while the constructor seeds the field.
    /// </summary>
    /// <remarks>
    /// Restoring a persisted setpoint into the box is not an operator edit. Flagging
    /// it would light "não aplicado" on every subsystem at launch, and a warning that
    /// is always on is a warning nobody reads.
    /// </remarks>
    private readonly bool _initialised;

    public SubsystemViewModel(
        ProcessVariableViewModel variable,
        SubsystemSpec spec,
        IDeviceService device,
        double initialSetpoint)
    {
        Variable = variable;
        _spec = spec;
        _device = device;

        SetpointText = Format(initialSetpoint);
        Validate();

        // FormattedDeviation is computed from the variable's reading and setpoint, so it
        // has to be told when either moves. Without this the delta shows whatever it was
        // when the pane was built and then quietly stops - the sort of stale number this
        // application exists to avoid.
        Variable.PropertyChanged += OnVariableChanged;

        _initialised = true;
    }

    private void OnVariableChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ProcessVariableViewModel.Value)
                           or nameof(ProcessVariableViewModel.Setpoint))
        {
            OnPropertyChanged(nameof(FormattedDeviation));
        }
    }

    /// <summary>The live reading this subsystem drives.</summary>
    public ProcessVariableViewModel Variable { get; }

    // ── Which detail-pane sections exist for this subsystem ──────────────────
    // Not a uniform set. A tab that is always present regardless of what the device
    // can do teaches the operator to ignore tabs.

    public bool HasOutput => _spec.HasOutput;

    public bool HasCascade => _spec.HasCascade;

    public bool HasPid => _spec.HasPid;

    public bool HasCalibration => _spec.HasCalibration;

    public bool HasHealth => _spec.HasHealth;

    /// <summary>
    /// True when there is more than one section, so the segmented strip earns its space.
    /// </summary>
    /// <remarks>
    /// A one-segment segmented control is a heading pretending to be a choice. With only
    /// "SP &amp; Limites" - which is most subsystems in Phase 1 - the strip is hidden and
    /// the content shown directly.
    /// </remarks>
    public bool ShowTabStrip => HasOutput || HasCascade || HasPid;

    public string DisplayName => Variable.DisplayName;

    public string Unit => Variable.Unit;

    /// <summary>Human-readable range, shown beside the field.</summary>
    public string RangeHint => _spec.IsInteger
        ? string.Create(CultureInfo.CurrentCulture,
            $"{Variable.ToDisplay(_spec.Minimum):F0}–{Variable.ToDisplay(_spec.Maximum):F0} {Unit} (0 = desligado)")
        : string.Create(CultureInfo.CurrentCulture,
            $"{Variable.ToDisplay(_spec.Minimum):F1}–{Variable.ToDisplay(_spec.Maximum):F1} {Unit}");

    /// <summary>Raw text from the entry field.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsValid))]
    [NotifyPropertyChangedFor(nameof(CanApply))]
    public partial string SetpointText { get; set; }

    /// <summary>pt-BR message when the entry cannot be sent, else null.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsValid))]
    [NotifyPropertyChangedFor(nameof(CanApply))]
    public partial string? ValidationError { get; set; }

    /// <summary>Whether the operator wants this subsystem running.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanApply))]
    public partial bool IsEnabled { get; set; }

    /// <summary>
    /// True when the entry differs from what the device last acknowledged.
    /// </summary>
    /// <remarks>
    /// Drives the "não aplicado" marker. Without it the operator cannot tell a typed
    /// number from a commanded one, which is exactly the ambiguity that makes a
    /// half-entered setpoint dangerous.
    /// </remarks>
    [ObservableProperty]
    public partial bool HasPendingChange { get; set; }

    /// <summary>Last value the device confirmed, or null before any send.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FormattedAppliedSetpoint))]
    public partial double? AppliedSetpoint { get; set; }

    /// <summary>Whether the last command sent for this subsystem left it enabled.</summary>
    [ObservableProperty]
    public partial bool AppliedIsEnabled { get; set; }

    /// <summary>Acknowledged setpoint formatted for the all-parameters table.</summary>
    public string FormattedAppliedSetpoint => AppliedSetpoint is { } value
        ? Variable.ToDisplay(value).ToString(
            "F" + (_spec.IsInteger ? 0 : Variable.Decimals).ToString(CultureInfo.InvariantCulture),
            CultureInfo.CurrentCulture)
        : "—";

    /// <summary>
    /// Signed distance from the acknowledged setpoint, or an em dash.
    /// </summary>
    /// <remarks>
    /// Deliberately not coloured by sign. A positive deviation is not "good" and a
    /// negative one is not "bad" - whether the process is healthy is what the state
    /// chip says, and giving the number its own colour would put a sixth vocabulary
    /// on screen competing with the five that carry meaning.
    /// </remarks>
    public string FormattedDeviation
    {
        get
        {
            if (Variable.Value is not { } value || Variable.Setpoint is not { } setpoint)
            {
                return "—";
            }

            var delta = Variable.ToDisplay(value) - Variable.ToDisplay(setpoint);
            var format = "+0." + new string('0', Variable.Decimals) + ";-0." +
                         new string('0', Variable.Decimals) + ";0";

            return delta.ToString(format, CultureInfo.CurrentCulture);
        }
    }

    public bool IsValid => ValidationError is null;

    public bool CanApply => IsValid;

    partial void OnSetpointTextChanged(string value)
    {
        Validate();

        if (!_initialised)
        {
            return;
        }

        RefreshPendingState();
    }

    partial void OnIsEnabledChanged(bool value)
    {
        if (_initialised)
        {
            RefreshPendingState();
        }
    }

    /// <summary>
    /// Sends the setpoint, or the safe-off command when the subsystem is disabled.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanApply))]
    private void Apply()
    {
        if (!TryBuildPendingCommand(out var command))
        {
            return;
        }

        _device.Send(command);
        CommitPendingCommand();
    }

    /// <summary>
    /// Builds the currently staged command without sending it, for one-frame bulk apply.
    /// </summary>
    public bool TryBuildPendingCommand(out TecnalCommand command)
    {
        if (!IsEnabled)
        {
            // Disabling must not depend on the entry parsing: an operator turning
            // something off should never be blocked by a typo in its value field.
            command = _spec.BuildDisable();
            return true;
        }

        // Re-validate here, not only via CanExecute. CanExecute merely greys out the
        // button; every route to the wire must enforce the range and integer rules.
        Validate();
        if (!IsValid || !TryParse(SetpointText, out var value))
        {
            command = TecnalCommand.Create();
            return false;
        }

        command = _spec.BuildApply(value);
        return true;
    }

    /// <summary>
    /// Updates the acknowledged UI state after a caller has queued the staged command.
    /// </summary>
    public void CommitPendingCommand()
    {
        var value = 0.0;
        if (IsEnabled && !TryParse(SetpointText, out value))
        {
            return;
        }

        AppliedSetpoint = IsEnabled ? value : 0;
        AppliedIsEnabled = IsEnabled;
        Variable.IsEnabled = IsEnabled;
        Variable.Setpoint = IsEnabled ? value : 0;

        if (Variable.IsCommandedOnly)
        {
            Variable.PushCommanded(IsEnabled ? value : 0);
        }

        HasPendingChange = false;
        _spec.OnCommitted?.Invoke(value, IsEnabled);
    }

    /// <summary>Parses the staged value after applying the subsystem's validation rules.</summary>
    public bool TryGetStagedValue(out double value)
    {
        Validate();
        value = default;
        return IsValid && TryParse(SetpointText, out value);
    }

    /// <summary>Stages a preset value and enabled state without sending anything.</summary>
    public void Stage(double value, bool isEnabled)
    {
        SetpointText = Format(value);
        IsEnabled = isEnabled;
        RefreshPendingState();
    }

    /// <summary>
    /// Changes the upper validation limit when the staged flowmeter ceiling changes.
    /// </summary>
    public void UpdateMaximum(double maximum)
    {
        if (!double.IsFinite(maximum) || maximum <= _spec.Minimum ||
            Math.Abs(maximum - _spec.Maximum) <= 1e-9)
        {
            return;
        }

        _spec = _spec with { Maximum = maximum };
        OnPropertyChanged(nameof(RangeHint));
        Validate();
        RefreshPendingState();
    }

    /// <summary>
    /// Re-expresses the staged field in another display unit without changing its
    /// canonical value or sending anything to the equipment.
    /// </summary>
    public void SetPresentation(string unit, int decimals, double scale, double offset = 0.0)
    {
        var wasPending = HasPendingChange;
        var canonical = TryParse(SetpointText, out var staged)
            ? staged
            : AppliedSetpoint ?? 0.0;

        Variable.SetPresentation(unit, decimals, scale, offset);
        SetpointText = Format(canonical);
        OnPropertyChanged(nameof(Unit));
        OnPropertyChanged(nameof(RangeHint));
        OnPropertyChanged(nameof(FormattedAppliedSetpoint));
        OnPropertyChanged(nameof(FormattedDeviation));
        Validate();
        HasPendingChange = wasPending;
    }

    /// <summary>Discards an unapplied edit, restoring the acknowledged value.</summary>
    [RelayCommand]
    private void Revert()
    {
        SetpointText = AppliedSetpoint is { } applied ? Format(applied) : SetpointText;
        IsEnabled = AppliedIsEnabled;
        HasPendingChange = false;
    }

    private void RefreshPendingState()
    {
        if (!_initialised)
        {
            return;
        }

        if (IsEnabled != AppliedIsEnabled)
        {
            HasPendingChange = true;
            return;
        }

        HasPendingChange = IsValid && TryParse(SetpointText, out var parsed) &&
                           (AppliedSetpoint is not { } applied ||
                            Math.Abs(parsed - applied) > 1e-9);
    }

    /// <summary>Re-runs validation and publishes the message.</summary>
    private void Validate()
    {
        var text = SetpointText?.Trim() ?? "";

        if (text.Length == 0)
        {
            ValidationError = "Informe um valor.";
            return;
        }

        if (!TryParse(text, out var value))
        {
            ValidationError = "Valor inválido.";
            return;
        }

        // Zero is the device's "off" encoding for every subsystem in this scope, so
        // it is always in range even when below the operating minimum.
        if (value != 0 && (value < _spec.Minimum || value > _spec.Maximum))
        {
            ValidationError = string.Create(CultureInfo.CurrentCulture,
                $"Fora da faixa ({Variable.ToDisplay(_spec.Minimum):G}–{Variable.ToDisplay(_spec.Maximum):G} {Unit}).");
            return;
        }

        if (_spec.IsInteger && Math.Abs(value - Math.Round(value)) > 1e-9)
        {
            ValidationError = "Use um número inteiro.";
            return;
        }

        ValidationError = null;
    }

    /// <summary>
    /// Parses operator input.
    /// </summary>
    /// <remarks>
    /// A decimal comma is accepted because that is what a pt-BR keyboard produces.
    /// The <b>wire</b> is a separate matter entirely - <c>TecnalCommand</c> formats
    /// invariantly by construction, so what is typed here can never reach the device
    /// as <c>6,98</c>. See <c>docs/PROTOCOL.md</c> section 2.2.
    /// </remarks>
    private bool TryParse(string? text, out double value)
    {
        if (!double.TryParse(
                (text ?? "").Trim().Replace(',', '.'),
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var display))
        {
            value = default;
            return false;
        }

        value = Variable.ToCanonical(display);
        return double.IsFinite(value);
    }

    private string Format(double value)
        => Variable.ToDisplay(value).ToString(
            "F" + (_spec.IsInteger ? 0 : Variable.Decimals).ToString(CultureInfo.InvariantCulture),
            CultureInfo.CurrentCulture);
}
