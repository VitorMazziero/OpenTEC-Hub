using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace OpenTECHub.Tests;

/// <summary>
/// Every resource key the power pages reference must exist.
/// </summary>
/// <remarks>
/// A missing <c>StaticResource</c> throws when the element is realised, and WPF realises a tab's
/// content only when that tab is first selected. The map page's benchmarking and scale-up tabs are
/// therefore invisible to a start-up smoke run: a bad key there would reach the operator, not the
/// build. Checking the keys statically catches that without needing a WPF host.
/// </remarks>
public sealed class PowerViewResourceContractTests
{
    private static readonly string[] PowerViews =
    [
        "PowerView.xaml",
        "PowerMapView.xaml",
        "PowerImpellerComparisonView.xaml",
        "ScaleUpCalculatorView.xaml",
    ];

    [Theory]
    [InlineData("PowerView.xaml")]
    [InlineData("PowerMapView.xaml")]
    [InlineData("PowerImpellerComparisonView.xaml")]
    [InlineData("ScaleUpCalculatorView.xaml")]
    public void Every_referenced_resource_key_is_defined(string viewFileName)
    {
        var viewPath = Path.Combine(TestPaths.RepositoryRoot, "src", "OpenTECHub", "Views", viewFileName);
        var xaml = File.ReadAllText(viewPath);

        var defined = CollectDefinedKeys();

        // Keys the view declares for itself count as defined.
        foreach (var localKey in Regex.Matches(xaml, @"x:Key=""([^""]+)""").Select(m => m.Groups[1].Value))
        {
            defined.Add(localKey);
        }

        var referenced = Regex
            .Matches(xaml, @"\{(?:Static|Dynamic)Resource\s+([A-Za-z0-9_.]+)\}")
            .Select(m => m.Groups[1].Value)
            .Distinct()
            .ToList();

        Assert.NotEmpty(referenced);

        var missing = referenced.Where(key => !defined.Contains(key)).ToList();

        Assert.True(
            missing.Count == 0,
            $"{viewFileName} references resource keys that are not defined anywhere: {string.Join(", ", missing)}");
    }

    [Fact]
    public void The_map_page_hosts_the_benchmarking_and_scale_up_tabs()
    {
        var xaml = File.ReadAllText(Path.Combine(
            TestPaths.RepositoryRoot, "src", "OpenTECHub", "Views", "PowerMapView.xaml"));

        // §10 freezes the shell at two power destinations, so steps 6 and 7 live here as tabs.
        Assert.Contains("<views:PowerImpellerComparisonView DataContext=\"{Binding Comparison}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("<views:ScaleUpCalculatorView Grid.Row=\"1\" DataContext=\"{Binding ScaleUp}\"", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void No_power_view_reintroduces_an_apply_button()
    {
        // The workspace contract is that settings take effect on change (§10).
        foreach (var viewFileName in PowerViews)
        {
            var xaml = File.ReadAllText(Path.Combine(
                TestPaths.RepositoryRoot, "src", "OpenTECHub", "Views", viewFileName));

            Assert.DoesNotContain("Content=\"Aplicar", xaml, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static HashSet<string> CollectDefinedKeys()
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        var resourceRoots = new[]
        {
            Path.Combine(TestPaths.RepositoryRoot, "src", "OpenTECHub", "Themes"),
            Path.Combine(TestPaths.RepositoryRoot, "src", "OpenTECHub", "Resources"),
        };

        foreach (var root in resourceRoots.Where(Directory.Exists))
        {
            foreach (var file in Directory.EnumerateFiles(root, "*.xaml", SearchOption.AllDirectories))
            {
                foreach (Match match in Regex.Matches(File.ReadAllText(file), @"x:Key=""([^""]+)"""))
                {
                    keys.Add(match.Groups[1].Value);
                }
            }
        }

        return keys;
    }
}
