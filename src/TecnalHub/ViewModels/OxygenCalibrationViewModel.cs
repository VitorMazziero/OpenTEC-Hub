using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TecnalHub.Protocol;
using TecnalHub.Services.Calibration;
using TecnalHub.Services.Communication;
using TecnalHub.Services.Persistence;

namespace TecnalHub.ViewModels;

/// <summary>
/// Direct two-point assistant around the same linear oxygen coefficients exposed by
/// v.6. No coefficient command exists on the wire; applying updates the app parser.
/// </summary>
public sealed partial class OxygenCalibrationViewModel : ObservableObject, IDisposable
{
    private readonly IDeviceService _device;
    private readonly ISettingsService _settings;
    private SensorSnapshot? _latest;
    private double? _raw1;
    private double? _raw2;
    private LinearCalibration? _proposal;

    public OxygenCalibrationViewModel(IDeviceService device, ISettingsService settings)
    {
        _device = device;
        _settings = settings;
        RefreshCurrentEquation(settings.Current);
        _device.TelemetryReceived += OnTelemetryReceived;
        _device.StateChanged += OnStateChanged;
        _settings.Changed += OnSettingsChanged;
    }

    [ObservableProperty]
    public partial string Reference1Text { get; set; } = "0";

    [ObservableProperty]
    public partial string Reference2Text { get; set; } = "100";

    [ObservableProperty]
    public partial string CurrentRawText { get; set; } = "—";

    [ObservableProperty]
    public partial string CurrentCalibratedText { get; set; } = "—";

    [ObservableProperty]
    public partial string Point1Text { get; set; } = "Não capturado";

    [ObservableProperty]
    public partial string Point2Text { get; set; } = "Não capturado";

    [ObservableProperty]
    public partial string CurrentEquationText { get; set; } = "—";

    [ObservableProperty]
    public partial string ProposedEquationText { get; set; } = "Nenhuma curva proposta.";

    [ObservableProperty]
    public partial string StatusText { get; set; } =
        "Estabilize cada padrão e capture a leitura bruta aceita.";

    public bool CanCapture => _device.State == ConnectionState.Connected && HasValidRaw(_latest);

    public bool CanApplyProposal => _proposal is not null;

    partial void OnReference1TextChanged(string value) => Recalculate();

    partial void OnReference2TextChanged(string value) => Recalculate();

    [RelayCommand(CanExecute = nameof(CanCapture))]
    private void CapturePoint1()
    {
        _raw1 = _latest!.OxygenRaw;
        Point1Text = $"raw {_raw1.Value:F3} → referência {Reference1Text}%";
        StatusText = "Ponto 1 capturado. Estabilize o segundo padrão.";
        Recalculate();
    }

    [RelayCommand(CanExecute = nameof(CanCapture))]
    private void CapturePoint2()
    {
        _raw2 = _latest!.OxygenRaw;
        Point2Text = $"raw {_raw2.Value:F3} → referência {Reference2Text}%";
        StatusText = "Ponto 2 capturado.";
        Recalculate();
    }

    [RelayCommand(CanExecute = nameof(CanApplyProposal))]
    private void ApplyProposal()
    {
        if (_proposal is not { } proposal)
        {
            return;
        }

        _settings.Update(settings => settings with
        {
            Calibration = settings.Calibration with
            {
                OxygenA = proposal.Slope,
                OxygenB = proposal.Intercept,
            },
        });
        StatusText = "Coeficientes de oxigênio aplicados ao parser e persistidos.";
        _proposal = null;
        ProposedEquationText = "Curva aplicada. Capture novos pontos para recalibrar.";
        OnPropertyChanged(nameof(CanApplyProposal));
        ApplyProposalCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private void Reset()
    {
        _raw1 = null;
        _raw2 = null;
        _proposal = null;
        Point1Text = "Não capturado";
        Point2Text = "Não capturado";
        ProposedEquationText = "Nenhuma curva proposta.";
        StatusText = "Capturas descartadas; os coeficientes vigentes não mudaram.";
        OnPropertyChanged(nameof(CanApplyProposal));
        ApplyProposalCommand.NotifyCanExecuteChanged();
    }

    private void Recalculate()
    {
        _proposal = null;
        if (_raw1 is not { } raw1 || _raw2 is not { } raw2)
        {
            NotifyProposalChanged();
            return;
        }

        if (!TryParseReference(Reference1Text, out var reference1) ||
            !TryParseReference(Reference2Text, out var reference2))
        {
            ProposedEquationText = "Referências devem ser valores finitos entre 0 e 150%.";
            StatusText = "Revise as referências de oxigênio.";
            NotifyProposalChanged();
            return;
        }

        if (Math.Abs(reference1 - reference2) <= 1e-9)
        {
            ProposedEquationText = "Use duas referências diferentes.";
            StatusText = "Curva recusada: referências iguais.";
            NotifyProposalChanged();
            return;
        }

        try
        {
            _proposal = CalibrationMath.FitLinear(
                new LinearCalibrationPoint(raw1, reference1),
                new LinearCalibrationPoint(raw2, reference2));
            ProposedEquationText = FormatEquation(
                _proposal.Value.Slope, _proposal.Value.Intercept);
            StatusText = "Curva proposta. Revise antes de aplicar no app.";
        }
        catch (InvalidOperationException exception)
        {
            ProposedEquationText = exception.Message;
            StatusText = "Curva recusada: leituras brutas indistinguíveis.";
        }

        NotifyProposalChanged();
    }

    private void NotifyProposalChanged()
    {
        OnPropertyChanged(nameof(CanApplyProposal));
        ApplyProposalCommand.NotifyCanExecuteChanged();
    }

    private void OnTelemetryReceived(SensorSnapshot snapshot)
    {
        _latest = snapshot;
        CurrentRawText = HasValidRaw(snapshot)
            ? snapshot.OxygenRaw.ToString("F2", CultureInfo.CurrentCulture)
            : "—";
        CurrentCalibratedText = snapshot.OxygenCalibrated > SensorReadings.NotReceived
            ? snapshot.OxygenCalibrated.ToString("F2", CultureInfo.CurrentCulture)
            : "—";
        NotifyCaptureChanged();
    }

    private void OnStateChanged(ConnectionStateChange change) => NotifyCaptureChanged();

    private void NotifyCaptureChanged()
    {
        OnPropertyChanged(nameof(CanCapture));
        CapturePoint1Command.NotifyCanExecuteChanged();
        CapturePoint2Command.NotifyCanExecuteChanged();
    }

    private void OnSettingsChanged(AppSettings settings) => RefreshCurrentEquation(settings);

    private void RefreshCurrentEquation(AppSettings settings)
        => CurrentEquationText = FormatEquation(
            settings.Calibration.OxygenA, settings.Calibration.OxygenB);

    private static string FormatEquation(double slope, double intercept)
    {
        var sign = intercept < 0 ? "−" : "+";
        return string.Create(CultureInfo.CurrentCulture,
            $"O₂ = {slope:G13} × raw {sign} {Math.Abs(intercept):G13}");
    }

    private static bool HasValidRaw(SensorSnapshot? snapshot)
        => snapshot is { SensorCommOk: true } &&
           double.IsFinite(snapshot.OxygenRaw) && snapshot.OxygenRaw > 0.1;

    private static bool TryParseReference(string? text, out double value)
        => double.TryParse((text ?? "").Trim().Replace(',', '.'), NumberStyles.Float,
               CultureInfo.InvariantCulture, out value) &&
           double.IsFinite(value) && value is >= 0.0 and <= 150.0;

    public void Dispose()
    {
        _device.TelemetryReceived -= OnTelemetryReceived;
        _device.StateChanged -= OnStateChanged;
        _settings.Changed -= OnSettingsChanged;
    }
}
