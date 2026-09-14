using OpenTECHub.Protocol;
using OpenTECHub.Services.Communication;
using OpenTECHub.ViewModels;
using Xunit;

namespace OpenTECHub.Tests;

/// <summary>
/// Network identity on <see cref="ExternalDeviceStatus"/>: text, reachability, advisory.
/// </summary>
public class ExternalDeviceStatusIdentityTests
{
    private static ExternalDeviceStatus Pump()
        => new(DeviceNames.ExternalPump, "da bomba externa") { NodeKind = NodeFirmwareCatalog.Pump };

    [Fact]
    public void Unknown_identity_says_so_and_is_never_a_fault()
    {
        var status = Pump();
        status.Update(hasTelemetry: true, online: true, pending: false, commEnabled: true, node: ExternalNodeIdentity.Empty);

        Assert.False(status.HasNodeIdentity);
        Assert.False(status.IsNodeReachable);
        Assert.Null(status.NodeDiagnosticsUri);
        Assert.Contains("desconhecida", status.NetworkSummaryText);
        Assert.Equal("—", status.NodeIpText);
        Assert.False(status.HasFirmwareAdvisory);
        Assert.False(status.HasStatusAlert);
    }

    [Fact]
    public void A_registered_node_reads_ip_and_firmware_and_points_at_its_diag()
    {
        var status = Pump();
        status.Update(true, true, false, true, new ExternalNodeIdentity("192.168.4.3", "AA:BB:CC:DD:EE:03", "3.9"));

        Assert.True(status.HasNodeIdentity);
        Assert.True(status.IsNodeReachable);
        Assert.Equal("192.168.4.3 · fw 3.9", status.NetworkSummaryText);
        Assert.Equal("AA:BB:CC:DD:EE:03", status.NodeMacText);
        Assert.Equal(new Uri("http://192.168.4.3/diag"), status.NodeDiagnosticsUri);
        Assert.Null(status.FirmwareAdvisoryText);
    }

    [Fact]
    public void A_version_outside_the_validated_set_is_an_advisory_not_an_alarm()
    {
        var status = Pump();
        status.Update(true, true, false, true, new ExternalNodeIdentity("192.168.4.3", null, "4.0"));

        Assert.True(status.HasFirmwareAdvisory);
        Assert.Contains("4.0", status.FirmwareAdvisoryText);
        Assert.Contains("3.9", status.FirmwareAdvisoryText);
        Assert.False(status.HasStatusAlert);
        Assert.True(status.CanSend);
    }

    [Fact]
    public void A_device_outside_the_registry_never_gets_an_advisory()
    {
        var servo = new ExternalDeviceStatus("Servo drive", "do servo drive");
        servo.Update(true, true, false, true, new ExternalNodeIdentity("192.168.4.9", null, "anything"));

        Assert.Null(servo.FirmwareAdvisoryText);
    }

    [Fact]
    public void Hub_loss_clears_the_identity_view()
    {
        var status = Pump();
        status.Update(true, true, false, true, new ExternalNodeIdentity("192.168.4.3", "m", "3.8"));
        status.MarkHubUnavailable();

        Assert.Equal(ExternalNodeIdentity.Empty, status.Node);
        Assert.False(status.HasNodeIdentity);
    }

    [Fact]
    public void Update_without_identity_keeps_what_was_known()
    {
        var status = Pump();
        var id = new ExternalNodeIdentity("192.168.4.3", "m", "3.8");
        status.Update(true, true, false, true, id);
        status.Update(true, true, false, true);

        Assert.Same(id, status.Node);
    }

    [Fact]
    public void Identity_change_raises_the_derived_properties()
    {
        var status = Pump();
        var raised = new List<string>();
        status.PropertyChanged += (_, e) => raised.Add(e.PropertyName!);

        status.Update(true, true, false, true, new ExternalNodeIdentity("192.168.4.3", null, "3.8"));

        Assert.Contains(nameof(ExternalDeviceStatus.Node), raised);
        Assert.Contains(nameof(ExternalDeviceStatus.NetworkSummaryText), raised);
        Assert.Contains(nameof(ExternalDeviceStatus.NodeDiagnosticsUri), raised);
        Assert.Contains(nameof(ExternalDeviceStatus.FirmwareAdvisoryText), raised);
    }
}

/// <summary>Validated-version sets: equality to a set, never ordering.</summary>
public class NodeFirmwareCatalogTests
{
    [Theory]
    [InlineData(NodeFirmwareCatalog.Distance, "v11", true)]
    [InlineData(NodeFirmwareCatalog.Agitator, "v10", true)]
    [InlineData(NodeFirmwareCatalog.Pump, "3.9", true)]
    [InlineData(NodeFirmwareCatalog.Pump, "3.12", true)]
    [InlineData(NodeFirmwareCatalog.Flowmeter, "v11", true)]
    [InlineData(NodeFirmwareCatalog.Flowmeter, "v12.0", true)]
    [InlineData(NodeFirmwareCatalog.Biomass, "v11", true)]
    [InlineData(NodeFirmwareCatalog.Pump, "V10", false)]
    [InlineData(NodeFirmwareCatalog.Pump, "3.8", false)]
    [InlineData(NodeFirmwareCatalog.Distance, "v10", false)]
    [InlineData(NodeFirmwareCatalog.Flowmeter, "v10", false)]
    [InlineData(NodeFirmwareCatalog.Biomass, "v10", false)]
    public void Membership_is_what_the_five_firmwares_send_today(string device, string version, bool validated)
        => Assert.Equal(validated, NodeFirmwareCatalog.IsValidated(device, version));

    [Fact]
    public void Unknown_version_is_not_judged()
    {
        Assert.Null(NodeFirmwareCatalog.IsValidated(NodeFirmwareCatalog.Pump, null));
        Assert.Null(NodeFirmwareCatalog.Advisory(NodeFirmwareCatalog.Pump, null));
    }

    [Fact]
    public void Unknown_device_is_never_validated_and_lists_nothing()
    {
        Assert.False(NodeFirmwareCatalog.IsValidated("servo", "x"));
        Assert.Empty(NodeFirmwareCatalog.ValidatedVersions("servo"));
    }

    [Fact]
    public void Every_registry_device_has_a_display_name_the_app_already_uses()
    {
        Assert.Equal(DeviceNames.Distance, NodeFirmwareCatalog.DisplayName(NodeFirmwareCatalog.Distance));
        Assert.Equal(DeviceNames.FlaskAgitator, NodeFirmwareCatalog.DisplayName(NodeFirmwareCatalog.Agitator));
        Assert.Equal(DeviceNames.ExternalPump, NodeFirmwareCatalog.DisplayName(NodeFirmwareCatalog.Pump));
        Assert.Equal(DeviceNames.Airflow, NodeFirmwareCatalog.DisplayName(NodeFirmwareCatalog.Flowmeter));
        Assert.Equal(DeviceNames.Absorbance, NodeFirmwareCatalog.DisplayName(NodeFirmwareCatalog.Biomass));
    }

    [Fact]
    public void IdentityOf_reads_the_matching_snapshot_member()
    {
        var pump = new ExternalNodeIdentity("192.168.4.3", null, "3.8");
        var snapshot = new SensorSnapshot { PumpNode = pump };

        Assert.Same(pump, NodeFirmwareCatalog.IdentityOf(snapshot, NodeFirmwareCatalog.Pump));
        Assert.Equal(ExternalNodeIdentity.Empty, NodeFirmwareCatalog.IdentityOf(snapshot, NodeFirmwareCatalog.Distance));
    }
}

/// <summary>Snapshots in, the few identity events worth journalling out.</summary>
public class NodeIdentityTrackerTests
{
    private static SensorSnapshot With(ExternalNodeIdentity pump) => new() { PumpNode = pump };

    [Fact]
    public void First_address_is_a_registration()
    {
        var tracker = new NodeIdentityTracker();

        Assert.Empty(tracker.Observe(new SensorSnapshot()));
        var change = Assert.Single(tracker.Observe(With(new("192.168.4.3", "m", "3.8"))));

        Assert.Equal(NodeIdentityChangeKind.Registered, change.Kind);
        Assert.Equal(NodeFirmwareCatalog.Pump, change.Device);
        Assert.Equal(DeviceNames.ExternalPump, change.DisplayName);
        Assert.Equal("192.168.4.3", change.After.Ip);
    }

    [Fact]
    public void Same_identity_across_many_frames_is_silent()
    {
        var tracker = new NodeIdentityTracker();
        var id = new ExternalNodeIdentity("192.168.4.3", "m", "3.8");
        tracker.Observe(With(id));

        for (var i = 0; i < 100; i++)
        {
            Assert.Empty(tracker.Observe(With(new ExternalNodeIdentity("192.168.4.3", "m", "3.8"))));
        }
    }

    [Fact]
    public void Dhcp_renumbering_is_an_ip_change()
    {
        var tracker = new NodeIdentityTracker();
        tracker.Observe(With(new("192.168.4.3", "m", "3.8")));

        var change = Assert.Single(tracker.Observe(With(new("192.168.4.5", "m", "3.8"))));

        Assert.Equal(NodeIdentityChangeKind.IpChanged, change.Kind);
        Assert.Equal("192.168.4.3", change.Before.Ip);
        Assert.Equal("192.168.4.5", change.After.Ip);
    }

    [Fact]
    public void Firmware_and_mac_changes_are_reported_separately_in_one_frame()
    {
        var tracker = new NodeIdentityTracker();
        tracker.Observe(With(new("192.168.4.3", "AA", "3.8")));

        var changes = tracker.Observe(With(new("192.168.4.3", "BB", "3.9")));

        Assert.Equal(2, changes.Count);
        Assert.Contains(changes, c => c.Kind == NodeIdentityChangeKind.FirmwareChanged);
        Assert.Contains(changes, c => c.Kind == NodeIdentityChangeKind.MacChanged);
    }

    [Fact]
    public void Losing_the_address_is_not_an_event_but_regaining_it_is_a_registration()
    {
        var tracker = new NodeIdentityTracker();
        tracker.Observe(With(new("192.168.4.3", "m", "3.8")));

        // Hub rebooted: 0.0.0.0 again, version/MAC still sticky in the parser.
        Assert.Empty(tracker.Observe(With(new(null, "m", "3.8"))));

        var change = Assert.Single(tracker.Observe(With(new("192.168.4.3", "m", "3.8"))));
        Assert.Equal(NodeIdentityChangeKind.Registered, change.Kind);
    }

    [Fact]
    public void Learning_version_after_the_address_is_not_a_change()
    {
        var tracker = new NodeIdentityTracker();
        tracker.Observe(With(new("192.168.4.3", null, null)));   // data push before hello

        Assert.Empty(tracker.Observe(With(new("192.168.4.3", "m", "3.8"))));
        Assert.Equal("3.8", tracker.Current(NodeFirmwareCatalog.Pump).FirmwareVersion);
    }

    [Fact]
    public void Reset_makes_the_next_address_a_registration_again()
    {
        var tracker = new NodeIdentityTracker();
        tracker.Observe(With(new("192.168.4.3", "m", "3.8")));
        tracker.Reset();

        var change = Assert.Single(tracker.Observe(With(new("192.168.4.3", "m", "3.8"))));
        Assert.Equal(NodeIdentityChangeKind.Registered, change.Kind);
    }
}
