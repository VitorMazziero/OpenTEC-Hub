using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using OpenTECHub.Protocol;
using OpenTECHub.Services.Communication;
using OpenTECHub.Services.PowerTesting;
using Xunit;

namespace OpenTECHub.Tests;

public sealed class PowerMotorRouteTests
{
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
        Assert.Contains("Modbus direto ativada", msg);
    }

    [Fact]
    public void EnsurePrimaryRoute_activates_uart_fallback_when_servo_offline()
    {
        var device = new TestDeviceService();
        var arbiter = new CommandArbiter(device, TimeProvider.System);
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
