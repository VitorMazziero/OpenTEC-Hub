using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Extensions.DependencyInjection;
using OpenTECHub.Tests.Rendering;
using OpenTECHub.ViewModels;
using OpenTECHub.Views;
using Xunit;

namespace OpenTECHub.Tests;

[Collection(WpfRenderingCollection.Name)]
public sealed class RawEventsInteractionTests
{
    [Fact]
    public void A_long_raw_frame_can_be_selected_and_scrolled_inside_the_message()
    {
        WpfRenderingHost.Run(() =>
        {
            var vm = WpfRenderingHost.Services.GetRequiredService<ShellViewModel>().Events;
            vm.RawEvents.Clear();
            var raw = "{\"Time\":14241.5," + string.Join(',', Enumerable.Range(0, 180)
                .Select(i => $"\"Diagnostic{i}\":\"conteúdo completo da telemetria\"")) + "}";
            vm.RawEvents.Add(raw);
            vm.RawEvents.Add("{\"Time\":14243.5}");
            Window? window = null;
            try
            {
                var view = new EventsView { DataContext = vm };
                window = new Window { Content = view, Width = 1280, Height = 800,
                    ShowInTaskbar = false, ShowActivated = false, Left = -32000, Top = -32000 };
                window.Show();
                WpfRenderingHost.PumpDispatcher();
                var list = Descendants<ListBox>(view).Single(item => ReferenceEquals(item.ItemsSource, vm.RawEvents));
                var text = Descendants<TextBox>(list).First();
                Assert.True(text.IsReadOnly);
                Assert.Equal(raw, text.Text);
                text.SelectAll();
                Assert.Equal(raw, text.SelectedText);
                WpfRenderingHost.PumpDispatcher();
                var scroll = Descendants<ScrollViewer>(list).First();
                Assert.True(scroll.ScrollableHeight > scroll.ViewportHeight);
                scroll.ScrollToVerticalOffset(80);
                WpfRenderingHost.PumpDispatcher();
                view.UpdateLayout();
                WpfRenderingHost.PumpDispatcher();
                Assert.InRange(scroll.VerticalOffset, 79, 81);
                Assert.True(text.FontSize <= 10);
                window.Content = null;
                window.Close();
                var bitmap = WpfRenderingHost.RenderElement(view, 1280, 800, 96);
                WpfRenderingHost.SavePng(bitmap, Path.Combine(TestPaths.EvidenceRoot,
                    "docs", "evidence", "screenshots", "raw-events-long-frame.png"));
            }
            finally { window?.Close(); vm.RawEvents.Clear(); }
        });
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) yield return match;
            foreach (var descendant in Descendants<T>(child)) yield return descendant;
        }
    }
}
