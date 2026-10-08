using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using OpenTECHub.Services.Communication;
using OpenTECHub.Services.KlaTesting;
using OpenTECHub.Services.Persistence;
using OpenTECHub.Services.Recipes;
using OpenTECHub.Tests.Rendering;
using OpenTECHub.ViewModels;
using OpenTECHub.Views;
using Xunit;

namespace OpenTECHub.Tests;

[Collection(Rendering.WpfRenderingCollection.Name)]
public sealed class RecipeEditorRenderingTests
{
    [Theory]
    [InlineData(KlaAssayProtocol.Abiotic, RecipeKlaConditionMode.SingleAtCurrentCondition, false)]
    [InlineData(KlaAssayProtocol.Abiotic, RecipeKlaConditionMode.SingleExplicit, false)]
    [InlineData(KlaAssayProtocol.Abiotic, RecipeKlaConditionMode.Multiple, false)]
    [InlineData(KlaAssayProtocol.Biotic, RecipeKlaConditionMode.SingleAtCurrentCondition, false)]
    [InlineData(KlaAssayProtocol.Biotic, RecipeKlaConditionMode.SingleExplicit, false)]
    [InlineData(KlaAssayProtocol.Biotic, RecipeKlaConditionMode.Multiple, false)]
    [InlineData(KlaAssayProtocol.Abiotic, RecipeKlaConditionMode.SingleAtCurrentCondition, true)]
    [InlineData(KlaAssayProtocol.Abiotic, RecipeKlaConditionMode.SingleExplicit, true)]
    [InlineData(KlaAssayProtocol.Abiotic, RecipeKlaConditionMode.Multiple, true)]
    [InlineData(KlaAssayProtocol.Biotic, RecipeKlaConditionMode.SingleAtCurrentCondition, true)]
    [InlineData(KlaAssayProtocol.Biotic, RecipeKlaConditionMode.SingleExplicit, true)]
    [InlineData(KlaAssayProtocol.Biotic, RecipeKlaConditionMode.Multiple, true)]
    public void KlaModesRenderAndActualSelectorsUpdateTheirContext(KlaAssayProtocol protocol, RecipeKlaConditionMode mode, bool dark)
        => WithEditor(protocol, dark, vm =>
        {
            vm.SelectedTab!.AddBlock(NodeType.KlaAssay, 280, 140);
            var node = vm.SelectedTab.SelectedNode!;
            Choice(node, "protocol", protocol.ToString());
            Choice(node, "conditionsMode", mode.ToString());
            node.SelectedOperationalProfile = Assert.Single(node.AvailableOperationalProfiles);
            if (mode == RecipeKlaConditionMode.Multiple)
            {
                var rows = node.Fields.Single(field => field.Key == "conditions");
                rows.AddRowCommand.Execute(null); rows.AddRowCommand.Execute(null);
                rows.Rows[0].Fields.Single(field => field.Key == "replicates").NumberValue = 2;
                rows.Rows[1].Fields.Single(field => field.Key == "agitationRpm").NumberValue = 400;
                rows.Rows[1].Fields.Single(field => field.Key == "airflowLpm").NumberValue = 3;
            }
            foreach (var (key, value) in new[] { ("maximumBlockSeconds", 600d), ("maximumGasOffSeconds", 60d),
                ("maximumCultivationGasOffSeconds", 240d), ("maximumAttemptsPerCultivation", 4d) })
                node.Fields.Single(field => field.Key == key).NumberValue = value;
            var view = new ReceitasView { DataContext = vm };
            Save(view, $"kla-{protocol}-{mode}-{dark}-top");
            var protocolField = node.Fields.Single(field => field.Key == "protocol");
            var protocolControl = Assert.Single(Shown<ComboBox>(view), control => ReferenceEquals(control.DataContext, protocolField));
            protocolControl.SelectedItem = protocolField.Options.Single(option => option.Value != protocol.ToString());
            Assert.NotEqual(protocol.ToString(), node.Model.Text("protocol"));
            WpfRenderingHost.RenderElement(view, 1280, 800, 120);
            protocolControl = Assert.Single(Shown<ComboBox>(view), control => ReferenceEquals(control.DataContext, protocolField));
            protocolControl.SelectedItem = protocolField.Options.Single(option => option.Value == protocol.ToString());
            WpfRenderingHost.RenderElement(view, 1280, 800, 120);
            Assert.Equal(protocol.ToString(), node.Model.Text("protocol"));
            var modeField = node.Fields.Single(field => field.Key == "conditionsMode");
            var modeControl = Assert.Single(Shown<ComboBox>(view), control => ReferenceEquals(control.DataContext, modeField));
            modeControl.SelectedItem = modeField.Options.Single(option => option.Value == nameof(RecipeKlaConditionMode.SingleExplicit));
            Assert.Contains(node.KlaProcedureFields, field => field.Key == "airflowLpm");
            Assert.DoesNotContain(node.KlaProcedureFields, field => field.Key == "conditions");
            WpfRenderingHost.RenderElement(view, 1280, 800, 120);
            modeControl = Assert.Single(Shown<ComboBox>(view), control => ReferenceEquals(control.DataContext, modeField));
            modeControl.SelectedItem = modeField.Options.Single(option => option.Value == mode.ToString());
            WpfRenderingHost.RenderElement(view, 1280, 800, 120);
            modeControl = Assert.Single(Shown<ComboBox>(view), control => ReferenceEquals(control.DataContext, modeField));
            Assert.Equal(mode.ToString(), node.Model.Text("conditionsMode"));
            Assert.DoesNotContain(Shown<TextBox>(view), control => control.DataContext is RecipeParameterFieldViewModel field && field.Key is "profileId" or "profileVersion");
            var scroll = Ancestor<ScrollViewer>(modeControl)!;
            Assert.NotNull(scroll);
            scroll.ScrollToEnd();
            Save(view, $"kla-{protocol}-{mode}-{dark}-limits");
            Assert.True(scroll.ScrollableHeight > 0);
        });

    [Theory]
    [InlineData(false, false)] [InlineData(true, false)]
    [InlineData(false, true)] [InlineData(true, true)]
    public void RampAssociationUsesARealSelectorOnlyForOxygen(bool oxygen, bool dark)
        => WithEditor(KlaAssayProtocol.Abiotic, dark, vm =>
        {
            var tab = vm.SelectedTab!;
            tab.AddBlock(NodeType.CascadeControl, 40, 120);
            var cascade = tab.SelectedNode!.Model;
            tab.AddBlock(NodeType.LinearSetpointRamp, 300, 160);
            var node = tab.SelectedNode!;
            var lines = node.Fields.Single(field => field.Key == "lines");
            lines.AddRowCommand.Execute(null);
            var variable = lines.Rows.Single().Fields.Single(field => field.Key == "variable");
            variable.SelectedOption = variable.Options.Single(option => option.Value == (oxygen ? "Oxygen" : "Temperature"));
            var view = new ReceitasView { DataContext = vm };
            Save(view, $"ramp-{oxygen}-{dark}");
            var selectors = Shown<ComboBox>(view).Where(control => ReferenceEquals(control.ItemsSource, node.AvailableRampCascades)).ToArray();
            if (oxygen)
            {
                var selector = Assert.Single(selectors);
                selector.SelectedItem = Assert.Single(node.AvailableRampCascades);
                Assert.Equal(cascade.Id, RecipeRampBlockConfiguration.Read(node.Model).CascadeNodeId);
                Save(view, $"ramp-{oxygen}-{dark}-selected");
            }
            else Assert.Empty(selectors);
        });

    private static void WithEditor(KlaAssayProtocol protocol, bool dark, Action<ReceitasViewModel> verify)
        => WpfRenderingHost.Run(() =>
        {
            var root = Path.Combine(Path.GetTempPath(), "recipe-ui-" + Guid.NewGuid().ToString("N"));
            var clock = new TestClock(DateTimeOffset.UnixEpoch);
            using var arbiter = new CommandArbiter(new RecordingDeviceService(), clock);
            using var engine = new RecipeEngine(arbiter, arbiter, new MemorySettingsService(), clock);
            var profiles = new KlaRecipeOperationalProfileRegistry("test-installation", clock, true);
            profiles.Register(KlaRecipeOperationalProfileTests.Profile(clock, protocol));
            try
            {
                WpfRenderingHost.SetTheme(dark);
                using var vm = new ReceitasViewModel(engine, new RecipeStore(root), operationalProfiles: profiles);
                verify(vm);
            }
            finally
            {
                WpfRenderingHost.SetTheme(false);
                if (Directory.Exists(root)) Directory.Delete(root, false);
            }
        });

    private static void Choice(RecipeNodeViewModel node, string key, string value)
    {
        var field = node.Fields.Single(field => field.Key == key);
        field.SelectedOption = field.Options.Single(option => option.Value == value);
    }
    private static void Save(ReceitasView view, string name)
    {
        var bitmap = WpfRenderingHost.RenderElement(view, 1280, 800, 120);
        Assert.True(VisualValidationHelper.ValidateBitmap(bitmap).IsNonTrivial);
        // Opaque corners catch the native-window crop that previously hid the property panel.
        var pixel = new byte[4];
        foreach (var corner in new[] { new Int32Rect(0, 0, 1, 1), new Int32Rect(bitmap.PixelWidth - 1, 0, 1, 1),
            new Int32Rect(0, bitmap.PixelHeight - 1, 1, 1), new Int32Rect(bitmap.PixelWidth - 1, bitmap.PixelHeight - 1, 1, 1) })
        {
            bitmap.CopyPixels(corner, pixel, 4, 0);
            Assert.Equal(byte.MaxValue, pixel[3]);
        }
        WpfRenderingHost.SavePng(bitmap, Path.Combine(TestPaths.RepositoryRoot, "docs", "plans", "receitas-r61", "evidence", "ui", name + ".png"));
    }
    private static IEnumerable<T> Shown<T>(DependencyObject root) where T : FrameworkElement
        => Descendants<T>(root).Where(element => IsShown(element));
    private static bool IsShown(DependencyObject element)
    {
        for (var current = element; current is not null; current = VisualTreeHelper.GetParent(current))
            if (current is FrameworkElement framework && framework.Visibility != Visibility.Visible) return false;
        return true;
    }
    private static T? Ancestor<T>(DependencyObject element) where T : DependencyObject
    {
        for (var current = VisualTreeHelper.GetParent(element); current is not null; current = VisualTreeHelper.GetParent(current))
            if (current is T match) return match;
        return null;
    }
    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T match) yield return match;
            foreach (var nested in Descendants<T>(child)) yield return nested;
        }
    }
}
