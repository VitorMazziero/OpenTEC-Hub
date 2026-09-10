using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using OpenTECHub.Protocol;
using OpenTECHub.Services.Communication;
using OpenTECHub.Services.Control;
using OpenTECHub.Services.KlaTesting;
using OpenTECHub.Services.Persistence;
using OpenTECHub.Services.PowerTesting;
using OpenTECHub.Services.Recipes;
using Xunit;

namespace OpenTECHub.Tests;

public sealed class PowerMotorRouteTests
{
    [Fact]
    public void Route_changes_are_refused_without_agitation_ownership()
    {
        var device = new TestDeviceService();
        using var arbiter = new CommandArbiter(device, TimeProvider.System);
        arbiter.Claim(CommandOwner.Recipe, [ActuatorId.Agitation], "receita");
        var coordinator = new MotorRouteCoordinator(arbiter, device, CommandOwner.PowerAssay);
        Assert.False(coordinator.EnsurePrimaryRoute(out _));
        Assert.False(coordinator.RouteRequestAccepted);
        Assert.False(coordinator.IsUartFallback);
        Assert.Empty(device.Sent);
    }

    [Fact]
    public void IsModbusCandidate_evaluates_servo_telemetry_presence()
    {
        Assert.True(PowerMotorRouteCoordinator.IsModbusCandidate(null));

        var onlineSnapshot = new SensorSnapshot
        {
            HasServoTelemetry = true,
            ServoOnline = true,
            ServoCommEnabled = true,
        };
        Assert.True(PowerMotorRouteCoordinator.IsModbusCandidate(onlineSnapshot));

        var offlineSnapshot = new SensorSnapshot
        {
            HasServoTelemetry = true,
            ServoOnline = false,
            ServoCommEnabled = true,
        };
        Assert.False(PowerMotorRouteCoordinator.IsModbusCandidate(offlineSnapshot));
    }

    [Fact]
    public void EnsurePrimaryRoute_dispatches_modbus_mode_when_online()
    {
        var device = new TestDeviceService();
        var arbiter = new CommandArbiter(device, TimeProvider.System);
        arbiter.Claim(CommandOwner.PowerAssay, [ActuatorId.Agitation], "teste");
        var coordinator = new PowerMotorRouteCoordinator(arbiter, device, CommandOwner.PowerAssay);

        device.Push(new SensorSnapshot
        {
            HasServoTelemetry = true,
            ServoOnline = true,
            ServoCommEnabled = true,
        });

        var success = coordinator.EnsurePrimaryRoute(out var msg);

        Assert.True(success);
        Assert.True(coordinator.IsModbusActive);
        Assert.False(coordinator.IsUartFallback);
        Assert.Equal(1000.0, coordinator.EffectiveMaxRpm);
        Assert.Contains("{\"motorControlMode\":1}", device.Sent);
        Assert.Contains("Modbus direto solicitada", msg);
    }

    [Fact]
    public void EnsurePrimaryRoute_activates_uart_fallback_when_servo_offline()
    {
        var device = new TestDeviceService();
        var arbiter = new CommandArbiter(device, TimeProvider.System);
        arbiter.Claim(CommandOwner.PowerAssay, [ActuatorId.Agitation], "teste");
        var coordinator = new PowerMotorRouteCoordinator(arbiter, device, CommandOwner.PowerAssay);

        device.Push(new SensorSnapshot
        {
            HasServoTelemetry = true,
            ServoOnline = false,
            ServoCommEnabled = true,
        });

        var success = coordinator.EnsurePrimaryRoute(out var msg);

        Assert.False(success);
        Assert.False(coordinator.IsModbusActive);
        Assert.True(coordinator.IsUartFallback);
        Assert.Equal(965.0, coordinator.EffectiveMaxRpm);
        Assert.Contains("{\"motorControlMode\":0}", device.Sent);
        Assert.Contains("fallback UART", msg);
    }

    [Fact]
    public void ValidateRpm_accepts_1000_rpm_on_modbus_and_rejects_over_965_on_uart_fallback()
    {
        var device = new TestDeviceService();
        var arbiter = new CommandArbiter(device, TimeProvider.System);
        arbiter.Claim(CommandOwner.PowerAssay, [ActuatorId.Agitation], "teste");
        var coordinator = new PowerMotorRouteCoordinator(arbiter, device, CommandOwner.PowerAssay);

        // Under Modbus (default candidate)
        coordinator.EnsurePrimaryRoute(out _);
        Assert.True(coordinator.ValidateRpm(1000, out var err1));
        Assert.Null(err1);
        Assert.True(coordinator.ValidateRpm(300, out var err2));
        Assert.Null(err2);

        // Under UART fallback
        coordinator.ActivateUartFallback("teste", out _);
        Assert.True(coordinator.ValidateRpm(965, out var err3));
        Assert.Null(err3);
        Assert.True(coordinator.ValidateRpm(300, out var err4));
        Assert.Null(err4);

        Assert.False(coordinator.ValidateRpm(1000, out var err5));
        Assert.NotNull(err5);
        Assert.Contains("965", err5);
        Assert.Contains("fallback UART", err5);
    }

    [Fact]
    public void AdjustTareTargets_caps_at_965_only_on_uart_fallback()
    {
        var device = new TestDeviceService();
        var arbiter = new CommandArbiter(device, TimeProvider.System);
        arbiter.Claim(CommandOwner.PowerAssay, [ActuatorId.Agitation], "teste");
        var coordinator = new PowerMotorRouteCoordinator(arbiter, device, CommandOwner.PowerAssay);

        var originalTargets = new List<double> { 100, 300, 600, 900, 1000 };

        // Modbus: keeps original
        var modbusTargets = coordinator.AdjustTareTargets(originalTargets);
        Assert.Equal(originalTargets, modbusTargets);

        // UART fallback: caps 1000 to 965
        coordinator.ActivateUartFallback("teste", out _);
        var uartTargets = coordinator.AdjustTareTargets(originalTargets);
        Assert.Equal(new List<double> { 100, 300, 600, 900, 965 }, uartTargets);
    }

    [Fact]
    public void ValidateConditions_blocks_1000_rpm_only_on_uart_fallback()
    {
        var device = new TestDeviceService();
        var arbiter = new CommandArbiter(device, TimeProvider.System);
        arbiter.Claim(CommandOwner.PowerAssay, [ActuatorId.Agitation], "teste");
        var coordinator = new PowerMotorRouteCoordinator(arbiter, device, CommandOwner.PowerAssay);

        var doc = new PowerTestDocument
        {
            Conditions =
            {
                new PowerCondition { AgitationRpm = 300, RequestedReplicates = 1 },
                new PowerCondition { AgitationRpm = 1000, RequestedReplicates = 1 },
            },
        };

        // Modbus
        Assert.True(coordinator.ValidateConditions(doc, out var errModbus));
        Assert.Null(errModbus);

        // UART Fallback
        coordinator.ActivateUartFallback("teste", out _);
        Assert.False(coordinator.ValidateConditions(doc, out var errUart));
        Assert.NotNull(errUart);
        Assert.Contains("965", errUart);
        Assert.Contains("1000", errUart);
    }

    [Fact]
    public void CascadeService_Engage_prioritizes_modbus_and_clamps_on_uart_fallback()
    {
        var device = new TestDeviceService();
        var arbiter = new CommandArbiter(device, TimeProvider.System);
        var settings = new MemorySettingsService(new AppSettings
        {
            Cascade = new CascadeSettings { OxygenSetpointPercent = 30, AgitationMaxRpm = 1000 },
        });
        var store = new FakeKlaProfileStore();
        var cascade = new CascadeService(device, arbiter, settings, store, TimeProvider.System);

        device.Push(new SensorSnapshot
        {
            HasServoTelemetry = true,
            ServoOnline = true,
            ServoCommEnabled = true,
            OxygenCalibrated = 20.0,
        });

        cascade.Engage(currentAgitationRpm: 300, currentAerationLpm: 2.0);

        Assert.True(cascade.RouteCoordinator.IsModbusActive);
        Assert.Contains("{\"motorControlMode\":1}", device.Sent);

        // Under UART fallback, the coordinator clamps
        cascade.RouteCoordinator.ActivateUartFallback("falha", out _);
        Assert.Equal(965.0, cascade.RouteCoordinator.ClampRpm(1000.0));
        Assert.Equal(500.0, cascade.RouteCoordinator.ClampRpm(500.0));
    }

    [Fact]
    public async Task RecipeEngine_StartAsync_prioritizes_modbus_and_clamps_agitation_on_uart_fallback()
    {
        var device = new TestDeviceService();
        var arbiter = new CommandArbiter(device, TimeProvider.System);
        var settings = new MemorySettingsService(new AppSettings());
        var engine = new RecipeEngine(arbiter, device, settings, TimeProvider.System,
            delay: (ts, ct) => Task.CompletedTask);

        device.Push(new SensorSnapshot
        {
            HasServoTelemetry = true,
            ServoOnline = true,
            ServoCommEnabled = true,
        });

        var recipe = new RecipeDocument { Name = "Teste Motor" };
        var start = RecipeNode.Create(NodeType.Start, id: "start");
        var end = RecipeNode.Create(NodeType.End, id: "end");
        recipe.Nodes.AddRange([start, end]);
        recipe.Connections.Add(new RecipeConnection("start", ConnectorNames.Out, "end", ConnectorNames.In));

        await engine.StartAsync(recipe);

        Assert.True(engine.RouteCoordinator.IsModbusActive);
        Assert.Contains("{\"motorControlMode\":1}", device.Sent);

        await engine.StopAsync("teste");
    }

    [Fact]
    public async Task KlaTestRunner_StartRunAsync_prioritizes_modbus_and_aborts_condition_over_965_on_uart_fallback()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"kla-route-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var device = new TestDeviceService();
            var arbiter = new CommandArbiter(device, TimeProvider.System);
            var store = new KlaTestStore(tempDir);
            var analysis = new KlaAnalysisEngine();
            var settings = new MemorySettingsService();
            var runner = new KlaTestRunner(device, arbiter, store, analysis, settings, TimeProvider.System);

            device.Push(new SensorSnapshot
            {
                HasServoTelemetry = true,
                ServoOnline = false, // Servo offline -> should fallback to UART
                ServoCommEnabled = true,
                FlowmeterOnline = true,
                OxygenCalibrated = 50.0,
            });

            var doc = new KlaTestDocument
            {
                Name = "Kla Test",
                FolderName = "Kla_Test",
                Conditions =
                {
                    new KlaTestCondition { ConditionId = Guid.NewGuid(), AgitationRpm = 1000, AirflowLpm = 2.0, RequestedReplicates = 1 },
                },
            };
            store.SaveTestManifest(doc);
            runner.PrepareTest(doc);

            await runner.StartRunAsync(doc.Conditions[0], 1);

            // Because condition was 1000 RPM and servo was offline (UART fallback), it aborts!
            Assert.True(runner.RouteCoordinator.IsUartFallback);
            Assert.Contains("{\"motorControlMode\":0}", device.Sent);
            Assert.Equal(RunPhase.Aborting, runner.Phase);
            Assert.Contains("965", runner.StatusMessage);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    [Fact]
    public void PowerTareCaptureController_allows_1000_rpm_when_targetRpm_is_1000()
    {
        var settings = new PowerTestSettings { MinRpm = 100, MaxRpm = 900 };
        // Even when settings.MaxRpm is 900, targetRpm = 1000 should NOT throw ArgumentOutOfRangeException
        var controller = new PowerTareCaptureController(settings, 1000.0);
        Assert.Equal(1000.0, controller.TargetRpm);
        Assert.Equal(1005.0, controller.MaxAllowedRpm); // 1000 + 5
    }


    private sealed class TestDeviceService : IDeviceService
    {
        public List<string> Sent { get; } = [];
        public ConnectionState State => ConnectionState.Connected;
        public TransportMedium? Medium => TransportMedium.Usb;
        public string Endpoint => "TEST";
        public SensorSnapshot? Latest { get; private set; }
        public LinkDiagnostics Diagnostics => new();

        public event Action<ConnectionStateChange>? StateChanged;
        public event Action<SensorSnapshot>? TelemetryReceived;
        public event Action<string>? RawTelemetryReceived;
        public event Action<string>? DeviceLogReceived;
        public event Action<string>? CommandSent;
        public event Action<double>? SessionTimeZeroed;

        public void Send(OpenTECCommand command)
        {
            var json = command.ToJson();
            Sent.Add(json);
            CommandSent?.Invoke(json);
        }

        public void Push(SensorSnapshot snapshot)
        {
            Latest = snapshot;
            TelemetryReceived?.Invoke(snapshot);
        }

        public void Connect() { }
        public void ConnectUsb(string portName) { }
        public void ConnectWiFi(string ipAddress) { }
        public void Disconnect() { }
        public void ZeroSessionTime() { }
        public Task<string?> DiscoverUsbPortAsync(CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);
    }
}
