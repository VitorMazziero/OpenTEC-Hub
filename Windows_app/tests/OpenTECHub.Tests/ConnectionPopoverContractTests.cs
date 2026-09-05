using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Windows.Input;
using OpenTECHub.ViewModels;
using Xunit;

namespace OpenTECHub.Tests;

/// <summary>
/// Ties the connection popover's markup to the view model that has to satisfy it.
/// </summary>
/// <remarks>
/// A WPF <c>Command</c> binding that resolves to nothing fails <b>silently</b>: the button
/// simply never fires, with no exception and no log line. That is exactly what a rename
/// produces, and it is why the port refresh moving from <c>RefreshPorts</c> to
/// <c>RefreshPortsAsync</c> needed a guard - the toolkit strips the <c>Async</c> suffix, so
/// the generated name happens to stay <c>RefreshPortsCommand</c>, but nothing in the build
/// enforces that.
/// </remarks>
public sealed class ConnectionPopoverContractTests
{
    private static readonly string WindowPath = Path.Combine(
        TestPaths.RepositoryRoot, "src", "OpenTECHub", "MainWindow.xaml");

    [Fact]
    public void Every_command_the_connection_popover_binds_exists_on_the_view_model()
    {
        var xaml = File.ReadAllText(WindowPath);

        var bound = Regex.Matches(xaml, @"\{Binding\s+(?<name>\w+Command)\b")
            .Select(m => m.Groups["name"].Value)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        Assert.NotEmpty(bound);

        var exposed = typeof(ConnectionViewModel)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => typeof(ICommand).IsAssignableFrom(p.PropertyType))
            .Select(p => p.Name)
            .ToHashSet(StringComparer.Ordinal);

        // MainWindow hosts more than the popover, so only the commands this view model is
        // responsible for are asserted; the rest belong to the shell.
        var connectionCommands = new[]
        {
            "RefreshPortsCommand", "DiscoverCommand", "ConnectCommand", "DisconnectCommand",
        };

        var missing = connectionCommands
            .Where(bound.Contains)
            .Where(name => !exposed.Contains(name))
            .ToList();

        Assert.True(
            missing.Count == 0,
            $"MainWindow.xaml binds commands that ConnectionViewModel does not expose: {string.Join(", ", missing)}. " +
            "Uma ligação de comando que não resolve falha em silêncio: o botão simplesmente não responde.");
    }

    [Fact]
    public void Refreshing_the_port_list_is_asynchronous()
    {
        // The ranked enumeration runs a WMI query - ~1.1 s cold on a machine with no COM
        // device attached. It used to be a synchronous command called from the constructor,
        // which put that cost on the first-frame path and froze the popover on every click.
        var command = typeof(ConnectionViewModel).GetProperty("RefreshPortsCommand");

        Assert.NotNull(command);
        Assert.True(
            typeof(CommunityToolkit.Mvvm.Input.IAsyncRelayCommand).IsAssignableFrom(command!.PropertyType),
            "RefreshPortsCommand voltou a ser síncrono; a consulta WMI bloquearia a thread de UI.");
    }

    [Fact]
    public void The_ui_thread_enumeration_does_not_touch_wmi()
    {
        // ListPortNames is the enumeration the constructor is allowed to call. It must stay
        // a device-map read: if it ever grows a WMI query the first-frame budget goes with it.
        var source = File.ReadAllText(Path.Combine(
            TestPaths.RepositoryRoot, "src", "OpenTECHub.Protocol", "SerialTransport.cs"));

        var start = source.IndexOf("public static IReadOnlyList<string> ListPortNames()", StringComparison.Ordinal);
        Assert.True(start >= 0, "ListPortNames desapareceu de SerialTransport.");

        var end = source.IndexOf("public static IReadOnlyList<string> ListCandidatePorts()", start, StringComparison.Ordinal);
        Assert.True(end > start, "ListCandidatePorts deveria vir logo após ListPortNames.");

        var body = source[start..end];
        Assert.DoesNotContain("ManagementObjectSearcher", body, StringComparison.Ordinal);
        Assert.DoesNotContain("QueryPortDescriptors", body, StringComparison.Ordinal);
    }
}
