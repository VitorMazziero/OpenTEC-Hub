using System.Globalization;
using System.IO;
using System.Windows.Threading;
using Microsoft.Extensions.Logging;
using OpenTECHub.Protocol;
using OpenTECHub.Services.Communication;

namespace OpenTECHub.Services.KlaTesting;

public sealed record KlaPlaybackOptions(string FilePath, double Speed = 10.0)
{
    public string DisplayName => Path.GetFileName(FilePath);
}

internal sealed record KlaPlaybackSample(double TimeMinutes, double Temperature, double Oxygen, double SourceMotorRpm);

/// <summary>
/// Offline controller emulator for the kLa workflow. Experimental DO comes from a
/// tab-separated OpenTEC session; command echoes, valves and acknowledgements are
/// generated from the exact commands sent by the application.
/// </summary>
public sealed class KlaPlaybackDeviceService : IDeviceService, IDisposable
{
    private readonly KlaPlaybackOptions _options;
    private readonly ILogger<KlaPlaybackDeviceService> _log;
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _timer;
    private readonly List<KlaPlaybackSample> _samples;
    private int _index;
    private bool _playing;
    private int? _coastStopIndex;
    private double _flowSetpoint;
    private bool _valve1;
    private bool _valve2;
    private bool _vFlow = true;
    private int _flowCommandId;
    private int _flowCommandAck;
    private bool _flowCommandPending;
    private int _commandsSent;
    private int _framesReceived;
    private DateTimeOffset? _lastFrameAt;
    private bool _disposed;

    public KlaPlaybackDeviceService(
        KlaPlaybackOptions options,
        ILogger<KlaPlaybackDeviceService> log,
        Dispatcher? dispatcher = null)
    {
        _options = options;
        _log = log;
        _dispatcher = dispatcher ?? Dispatcher.CurrentDispatcher;
        _samples = LoadSamples(options.FilePath);
        if (_samples.Count < 5)
        {
            throw new InvalidDataException("O arquivo de simulação de kLa não contém amostras suficientes.");
        }

        var sourceStepSeconds = MedianPositiveStepSeconds(_samples);
        var intervalMs = Math.Clamp(sourceStepSeconds * 1000.0 / Math.Clamp(options.Speed, 0.1, 100), 20, 10_000);
        _timer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(intervalMs),
        };
        _timer.Tick += OnTimerTick;
    }

    public ConnectionState State { get; private set; } = ConnectionState.Disconnected;
    public TransportMedium? Medium => TransportMedium.Simulation;
    public string Endpoint => $"Simulação kLa · {_options.DisplayName} · {_options.Speed:G}×";
    public SensorSnapshot? Latest { get; private set; }
    public LinkDiagnostics Diagnostics => new()
    {
        FramesReceived = _framesReceived,
        CommandsSent = _commandsSent,
        CommandAcks = _flowCommandAck,
        LastFrameAt = _lastFrameAt,
    };

    public event Action<ConnectionStateChange>? StateChanged;
    public event Action<SensorSnapshot>? TelemetryReceived;
    public event Action<string>? RawTelemetryReceived;
    public event Action<string>? DeviceLogReceived;
    public event Action<string>? CommandSent;
    public event Action<double>? SessionTimeZeroed;

    public void Connect()
    {
        if (State == ConnectionState.Connected)
        {
            return;
        }

        State = ConnectionState.Connected;
        StateChanged?.Invoke(new ConnectionStateChange(State, Medium, Endpoint, "Modo de teste kLa com arquivo"));
        DeviceLogReceived?.Invoke($"SIMULAÇÃO kLa ativa: {_options.FilePath}");
        PublishCurrentSample();
        _timer.Start();
        _log.LogInformation("kLa playback connected with {Count} samples at {Speed}x from {File}", _samples.Count, _options.Speed, _options.FilePath);
    }

    public void ConnectUsb(string portName) => Connect();
    public void ConnectWiFi(string ipAddress) => Connect();

    public void Disconnect()
    {
        _timer.Stop();
        _playing = false;
        State = ConnectionState.Disconnected;
        StateChanged?.Invoke(new ConnectionStateChange(State, Medium, Endpoint, "Simulação encerrada"));
    }

    public void Send(OpenTECCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (State != ConnectionState.Connected)
        {
            return;
        }

        _commandsSent++;
        CommandSent?.Invoke(command.ToJson());

        var wasNitrogenOpen = _valve1 || _valve2;
        var touchesFlow = command.Contains(CommandKeys.FlowSetpoint) || command.Contains(CommandKeys.Valve1) ||
                          command.Contains(CommandKeys.Valve2) || command.Contains(CommandKeys.V_Flow);
        if (TryDouble(command, CommandKeys.FlowSetpoint, out var flow))
        {
            _flowSetpoint = flow;
        }

        if (TryBool(command, CommandKeys.Valve1, out var valve1))
        {
            _valve1 = valve1;
        }

        if (TryBool(command, CommandKeys.Valve2, out var valve2))
        {
            _valve2 = valve2;
        }

        if (TryBool(command, CommandKeys.V_Flow, out var vFlow))
        {
            _vFlow = vFlow;
        }

        if (touchesFlow)
        {
            _flowCommandId++;
            _flowCommandPending = true;
            PublishCurrentSample();

            if ((_valve1 || _valve2) && _flowSetpoint <= 0.001)
            {
                _coastStopIndex = null;
                SeekNextDescendingSegment();
                _playing = true;
            }
            else if ((_valve1 || _valve2) && _flowSetpoint > 0.001)
            {
                // Alívio aberto: o gás sai antes do reator. A curva experimental fica
                // parada enquanto a vazão assenta, como acontece na bancada.
                _coastStopIndex = null;
                _playing = false;
            }
            else if (_flowSetpoint > 0.001)
            {
                _coastStopIndex = null;
                SeekNextAscendingSegment();
                _playing = true;
            }
            else if (wasNitrogenOpen)
            {
                // Reproduz a inércia observada depois de fechar o N₂ até o mínimo
                // local; em seguida mantém esse ponto para a sonda estabilizar.
                _coastStopIndex = FindNextLocalMinimumIndex();
                _playing = _coastStopIndex > _index;
            }
            else
            {
                // A parada segura do ensaio também congela a fonte experimental.
                // Assim, a seleção/revisão não consome amostras em segundo plano.
                _playing = false;
            }

            _dispatcher.InvokeAsync(() =>
            {
                _flowCommandAck = _flowCommandId;
                _flowCommandPending = false;
                PublishCurrentSample();
            }, DispatcherPriority.Background);
        }
    }

    public void ZeroSessionTime() => SessionTimeZeroed?.Invoke(_samples[_index].TimeMinutes);

    public Task<string?> DiscoverUsbPortAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<string?>("SIM-KLA");

    private void OnTimerTick(object? sender, EventArgs e)
    {
        if (State != ConnectionState.Connected)
        {
            return;
        }

        if (_playing)
        {
            if (_index < _samples.Count - 1)
            {
                _index++;
                if (_coastStopIndex.HasValue && _index >= _coastStopIndex.Value)
                {
                    _playing = false;
                    _coastStopIndex = null;
                }
            }
            else
            {
                _playing = false;
            }
        }

        PublishCurrentSample();
    }

    private void PublishCurrentSample()
    {
        var sample = _samples[_index];
        Latest = new SensorSnapshot
        {
            Temperature = sample.Temperature,
            OxygenRaw = sample.Oxygen,
            OxygenCalibrated = sample.Oxygen,
            FlowRate = _flowSetpoint,
            FlowSetpoint = _flowSetpoint,
            FlowValve1 = _valve1 ? 1 : 0,
            FlowValve2 = _valve2 ? 1 : 0,
            FlowValveMain = _vFlow ? 1 : 0,
            FlowmeterOnline = true,
            FlowControlEnabled = !_vFlow,
            FlowCommandPending = _flowCommandPending,
            FlowCommandSource = "kla-file-simulation",
            FlowCommandId = _flowCommandId,
            FlowCommandAck = _flowCommandAck,
            FlowCommandDeliveries = _flowCommandAck,
            HubStations = 1,
            SensorCommOk = true,
            TimeRawSeconds = sample.TimeMinutes * 60.0,
            TimeMinutes = sample.TimeMinutes,
        };
        _framesReceived++;
        _lastFrameAt = DateTimeOffset.Now;
        TelemetryReceived?.Invoke(Latest);
        RawTelemetryReceived?.Invoke("{ \"simulated\": true }");
    }

    private void SeekNextDescendingSegment()
    {
        if (IsDescendingAt(_index))
        {
            return;
        }

        for (var i = _index + 1; i < _samples.Count - 2; i++)
        {
            if (_samples[i].Oxygen > 15 && IsDescendingAt(i))
            {
                _index = i;
                return;
            }
        }

        for (var i = 0; i < _samples.Count - 2; i++)
        {
            if (_samples[i].Oxygen > 15 && IsDescendingAt(i))
            {
                _index = i;
                return;
            }
        }
    }

    private void SeekNextAscendingSegment()
    {
        if (IsAscendingAt(_index))
        {
            return;
        }

        for (var i = _index + 1; i < _samples.Count - 2; i++)
        {
            if (IsAscendingAt(i))
            {
                _index = i;
                return;
            }
        }

        for (var i = 0; i < _samples.Count - 2; i++)
        {
            if (IsAscendingAt(i))
            {
                _index = i;
                return;
            }
        }
    }

    private int FindNextLocalMinimumIndex()
    {
        for (var i = _index + 1; i < _samples.Count - 2; i++)
        {
            if (IsAscendingAt(i))
            {
                return i;
            }
        }
        return Math.Min(_samples.Count - 1, _index + 1);
    }

    private bool IsDescendingAt(int index)
    {
        var end = Math.Min(_samples.Count - 1, index + 3);
        return end > index && _samples[end].Oxygen < _samples[index].Oxygen - 0.03;
    }

    private bool IsAscendingAt(int index)
    {
        var end = Math.Min(_samples.Count - 1, index + 3);
        return end > index && _samples[end].Oxygen > _samples[index].Oxygen + 0.03;
    }

    private static List<KlaPlaybackSample> LoadSamples(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("Arquivo de simulação de kLa não encontrado.", path);
        }

        var result = new List<KlaPlaybackSample>();
        using var reader = new StreamReader(path);
        var header = reader.ReadLine()?.Split('\t') ?? [];
        var timeIndex = Array.FindIndex(header, h => h.StartsWith("Time", StringComparison.OrdinalIgnoreCase));
        var temperatureIndex = Array.FindIndex(header, h => h.StartsWith("Temperature", StringComparison.OrdinalIgnoreCase));
        var motorIndex = Array.FindIndex(header, h => h.StartsWith("Motor", StringComparison.OrdinalIgnoreCase));
        var oxygenIndex = Array.FindIndex(header, h => h.Equals("Oxygen", StringComparison.OrdinalIgnoreCase));
        if (timeIndex < 0 || oxygenIndex < 0)
        {
            throw new InvalidDataException("O arquivo precisa das colunas Time e Oxygen.");
        }

        while (reader.ReadLine() is { } line)
        {
            var cells = line.Split('\t');
            if (!TryCell(cells, timeIndex, out var time) || !TryCell(cells, oxygenIndex, out var oxygen) ||
                !double.IsFinite(time) || !double.IsFinite(oxygen))
            {
                continue;
            }

            TryCell(cells, temperatureIndex, out var temperature);
            TryCell(cells, motorIndex, out var motor);
            result.Add(new KlaPlaybackSample(time, temperature, oxygen, motor));
        }
        return result.OrderBy(s => s.TimeMinutes).ToList();
    }

    private static bool TryCell(string[] cells, int index, out double value)
    {
        value = 0;
        return index >= 0 && index < cells.Length &&
               (double.TryParse(cells[index], NumberStyles.Float, CultureInfo.InvariantCulture, out value) ||
                double.TryParse(cells[index], NumberStyles.Float, CultureInfo.CurrentCulture, out value));
    }

    private static double MedianPositiveStepSeconds(IReadOnlyList<KlaPlaybackSample> samples)
    {
        var steps = new List<double>();
        for (var i = 1; i < samples.Count; i++)
        {
            var step = (samples[i].TimeMinutes - samples[i - 1].TimeMinutes) * 60;
            if (step > 0)
            {
                steps.Add(step);
            }
        }
        if (steps.Count == 0)
        {
            return 2;
        }

        steps.Sort();
        return steps[steps.Count / 2];
    }

    private static bool TryDouble(OpenTECCommand command, string key, out double value) =>
        double.TryParse(command.GetRawValue(key), NumberStyles.Float, CultureInfo.InvariantCulture, out value);

    private static bool TryBool(OpenTECCommand command, string key, out bool value)
    {
        value = false;
        var raw = command.GetRawValue(key);
        if (raw == "1") { value = true; return true; }
        if (raw == "0")
        {
            return true;
        }

        return false;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _timer.Stop();
        _timer.Tick -= OnTimerTick;
    }
}
