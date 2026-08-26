namespace TecnalHub.Simulator;

/// <summary>Fault-injection modes, switchable while running.</summary>
public enum Scenario
{
    /// <summary>Healthy process, every sensor reporting.</summary>
    Normal,

    /// <summary>Sensor module offline: probes at sentinel, <c>SensorCommOK:false</c>.</summary>
    NoModule,

    /// <summary>
    /// Telemetry frozen while the link stays up.
    /// </summary>
    /// <remarks>
    /// The failure the Wi-Fi silence timeout exists to catch: <c>/ping</c> keeps
    /// answering and <c>/readData</c> keeps returning 304, so the app would happily
    /// show stale readings behind a healthy indicator.
    /// </remarks>
    Stall,

    /// <summary>Link disappears entirely, to exercise reconnect.</summary>
    Dropout,

    /// <summary>Single-sample outliers, to prove the spike filter holds its value.</summary>
    Spikes,

    /// <summary>Heavy measurement noise on every channel.</summary>
    Noise,

    /// <summary>Slow calibration drift, for long-run chart behaviour.</summary>
    Drift,

    /// <summary>Malformed lines, to exercise the parse-failure path.</summary>
    Garbage,
}

/// <summary>
/// The simulated bioreactor and its controller state.
/// </summary>
/// <remarks>
/// Supports realistic kLa surfaces (published mapping receipts), biological cultivation
/// kinetics with phase transitions, configurable polarographic probe dead time and quantisation,
/// and accelerated headless simulation.
/// </remarks>
public sealed class DeviceModel
{
    private readonly ISimulatorClock _clock;
    private readonly Random _random;
    private readonly DateTimeOffset _bootedAt;

    /// <summary>Delayed DO samples, so the reported value lags the true one.</summary>
    private readonly Queue<(DateTimeOffset At, double Value)> _oxygenDelayLine = new();

    private DateTimeOffset _lastTick;
    private double _elapsedSimulationSeconds;

    // ---- true process state ------------------------------------------

    private double _temperature = 24.0;      // degC, starts at ambient
    private double _flow;                    // L/min
    private double _oxygenTrue = 95.0;       // % saturation, starts near-saturated
    private double _oxygenReported = 95.0;   // what the probe says, i.e. delayed
    private double _ph = 7.0;
    private double _biomass = 0.15;          // AU
    private double _pressure;                // kPa
    private double _calibrationDrift;        // Drift scenario only

    public DeviceModel(
        ISimulatorClock? clock = null,
        IKlaSource? klaSource = null,
        CultivationProfile? profile = null,
        TimeSpan? probeDeadTime = null,
        double oxygenQuantisation = 0.0,
        int randomSeed = 20260819)
    {
        _clock = clock ?? new WallClock();
        _bootedAt = _clock.Now;
        _lastTick = _clock.Now;
        _random = new Random(randomSeed);

        KlaSource = klaSource ?? new PowerLawKla();
        Profile = profile ?? CultivationProfile.Default;
        OxygenProbeDeadTime = probeDeadTime ?? TimeSpan.FromSeconds(25);
        OxygenQuantisation = Math.Max(0.0, oxygenQuantisation);
        CurrentPhase = Profile.GetPhase(0);
    }

    // ---- dynamic & scientific configuration ---------------------------

    public IKlaSource KlaSource { get; set; }

    public CultivationProfile Profile { get; set; }

    public TimeSpan OxygenProbeDeadTime { get; set; }

    public double OxygenQuantisation { get; set; }

    public double TrueOxygen => _oxygenTrue;

    public double ReportedOxygen => _oxygenReported;

    public double CurrentKLa { get; private set; }

    public double CurrentOur { get; private set; }

    public CultivationPhase CurrentPhase { get; private set; }

    public double ElapsedSimulationSeconds => _elapsedSimulationSeconds;

    // ---- commanded state ---------------------------------------------

    public Scenario Scenario { get; set; } = Scenario.Normal;

    public double TemperatureSetpoint { get; set; }

    public int MotorRpm { get; set; }

    public double OxygenSetpoint { get; set; }

    public double FlowSetpoint { get; set; }

    public double MaxFlow { get; set; } = 50.0;

    public double PressureReference { get; set; }

    public double PHSetpoint { get; set; }

    public double PHInactiveBand { get; set; } = 0.17;

    public int PHOperationSeconds { get; set; } = 5;

    public int PHMixSeconds { get; set; } = 20;

    /// <summary>Firmware-scale intensity, 0-990 (operator percent times ten).</summary>
    public double PHIntensity { get; set; }

    /// <summary>Last app-calibrated pH value echoed for the module display.</summary>
    public double PHDisplayValue { get; set; }

    public double? FlowK1 { get; set; }
    public double? FlowF1 { get; set; }
    public double? FlowC1 { get; set; }
    public double? FlowK2 { get; set; }
    public double? FlowF2 { get; set; }
    public double? FlowC2 { get; set; }

    public bool FlowmeterEnabled { get; set; }

    public bool VentValveOpen { get; set; } = true;

    public int Valve1 { get; set; }

    public int Valve2 { get; set; }

    public int DataDelayMs { get; set; } = 2000;

    /// <summary>Command correlation counters the app surfaces in diagnostics.</summary>
    public int FlowCommandId { get; private set; }

    public int FlowCommandAck { get; private set; }

    public int FlowCommandDeliveries { get; private set; }

    /// <summary>True while the ESP32 can reach the sensor module over its internal UART.</summary>
    public bool SensorModuleOnline => Scenario != Scenario.NoModule;

    /// <summary>Seconds since simulated boot, as the device reports them.</summary>
    public double UptimeSeconds => (_clock.Now - _bootedAt).TotalSeconds;

    /// <summary>Records that a flow command was accepted, for ack correlation.</summary>
    public void NoteFlowCommand()
    {
        FlowCommandId++;
        FlowCommandAck = FlowCommandId;
        FlowCommandDeliveries++;
    }

    /// <summary>Advances the process by the given simulated time step or the elapsed clock time.</summary>
    public void Tick(double? explicitDt = null)
    {
        double dt;
        if (explicitDt is { } step && step > 0)
        {
            dt = step;
            _clock.Advance(TimeSpan.FromSeconds(dt));
            _lastTick = _clock.Now;
        }
        else
        {
            var now = _clock.Now;
            dt = (now - _lastTick).TotalSeconds;
            _lastTick = now;
            dt = Math.Clamp(dt, 0.0, 5.0);
        }

        if (dt <= 0)
        {
            return;
        }

        _elapsedSimulationSeconds += dt;

        if (Scenario == Scenario.Stall)
        {
            return; // deliberately frozen: the link lives, the process does not
        }

        StepTemperature(dt);
        StepFlowAndPressure(dt);
        StepOxygen(dt);
        StepPH(dt);
        StepBiomass(dt);

        if (Scenario == Scenario.Drift)
        {
            _calibrationDrift += dt * 0.0008;
        }
    }

    // ------------------------------------------------------------------
    // Dynamics
    // ------------------------------------------------------------------

    private void StepTemperature(double dt)
    {
        const double ambient = 24.0;
        const double heatingTau = 90.0;
        const double lossTau = 600.0;

        if (TemperatureSetpoint > 0)
        {
            _temperature += (TemperatureSetpoint - _temperature) * (dt / heatingTau);
        }

        _temperature += (ambient - _temperature) * (dt / lossTau);
    }

    private void StepFlowAndPressure(double dt)
    {
        const double flowTau = 6.0;

        var target = FlowmeterEnabled ? Math.Clamp(FlowSetpoint, 0.0, MaxFlow) : 0.0;
        _flow += (target - _flow) * (dt / flowTau);

        // Back-pressure builds against the vessel restriction, relieved by the vent.
        var restriction = VentValveOpen ? 0.35 : 1.6;
        var targetPressure = _flow * restriction * 2.0;
        _pressure += (targetPressure - _pressure) * (dt / 8.0);
    }

    private void StepOxygen(double dt)
    {
        const double saturation = 100.0;

        CurrentPhase = Profile.GetPhase(_elapsedSimulationSeconds);
        var kLa = KlaSource.Evaluate(MotorRpm, _flow);
        CurrentKLa = kLa;

        var uptake = _biomass * CurrentPhase.SpecificOurPerAu;
        CurrentOur = uptake;

        _oxygenTrue += ((kLa * (saturation - _oxygenTrue)) - uptake) * dt;
        _oxygenTrue = Math.Clamp(_oxygenTrue, 0.0, saturation);

        // Feed the delay line and read out whatever is old enough to have arrived.
        var now = _clock.Now;
        _oxygenDelayLine.Enqueue((now, _oxygenTrue));

        while (_oxygenDelayLine.Count > 0 &&
               now - _oxygenDelayLine.Peek().At >= OxygenProbeDeadTime)
        {
            _oxygenReported = _oxygenDelayLine.Dequeue().Value;
        }

        // Cap the queue in case the dead time is ever set absurdly high.
        while (_oxygenDelayLine.Count > 20_000)
        {
            _oxygenDelayLine.Dequeue();
        }
    }

    private void StepPH(double dt)
    {
        // Metabolism acidifies according to active cultivation phase; dosing corrects.
        var phase = CurrentPhase ?? Profile.GetPhase(_elapsedSimulationSeconds);
        _ph -= _biomass * phase.AcidificationRatePerAu * dt;

        if (PHSetpoint > 0 && PHIntensity > 0)
        {
            var error = PHSetpoint - _ph;
            if (Math.Abs(error) > PHInactiveBand)
            {
                var speedFraction = Math.Clamp(PHIntensity / 990.0, 0.0, 1.0);
                var dutyFraction = PHOperationSeconds /
                                   (double)Math.Max(PHOperationSeconds + PHMixSeconds, 1);
                var dosingGain = Math.Max(speedFraction * dutyFraction, 0.01);
                _ph += error * (dt / (240.0 / dosingGain));
            }
        }

        _ph = Math.Clamp(_ph, 3.0, 11.0);
    }

    private void StepBiomass(double dt)
    {
        var phase = CurrentPhase ?? Profile.GetPhase(_elapsedSimulationSeconds);

        // Growth stalls when oxygen runs out, which is the whole point of controlling it.
        var oxygenLimitation = Math.Clamp(_oxygenTrue / 25.0, 0.0, 1.0);

        if (phase.SpecificGrowthRatePerSecond > 0 && phase.CarryingCapacityAu > 0)
        {
            _biomass += phase.SpecificGrowthRatePerSecond * _biomass * (1.0 - (_biomass / phase.CarryingCapacityAu))
                        * oxygenLimitation * dt;
        }
    }

    // ------------------------------------------------------------------
    // Readings, with scenario effects applied
    // ------------------------------------------------------------------

    public double ReadTemperature() => Perturb(_temperature, 0.05);

    public double ReadOxygenPercent()
    {
        var value = _oxygenReported + _calibrationDrift;
        if (OxygenQuantisation > 0.0)
        {
            value = Math.Round(value / OxygenQuantisation) * OxygenQuantisation;
        }

        return Math.Clamp(Perturb(value, 0.4), 0.0, 100.0);
    }

    public double ReadPH() => Perturb(_ph + (_calibrationDrift * 0.05), 0.01);

    public double ReadFlow() => Math.Max(0.0, Perturb(_flow, 0.02));

    public double ReadPressure() => Math.Max(0.0, Perturb(_pressure, 0.1));

    public double ReadBiomass() => Math.Max(0.0, Perturb(_biomass, 0.005));

    /// <summary>Applies scenario noise and the occasional injected outlier.</summary>
    private double Perturb(double value, double noiseScale)
    {
        var noise = Scenario switch
        {
            Scenario.Noise => noiseScale * 8.0,
            Scenario.Spikes => noiseScale,
            _ => noiseScale,
        };

        var result = value + ((_random.NextDouble() - 0.5) * 2.0 * noise);

        // A lone outlier the app's spike filter must hold against rather than follow.
        if (Scenario == Scenario.Spikes && _random.NextDouble() < 0.06)
        {
            result += (_random.NextDouble() - 0.5) * Math.Max(Math.Abs(value), 1.0) * 1.4;
        }

        return result;
    }
}
