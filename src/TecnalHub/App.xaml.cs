using System.Windows;

namespace TecnalHub;

/// <summary>
/// Application entry point and composition root.
/// </summary>
/// <remarks>
/// <para>
/// This is where the DI container is built and where the theme is resolved before
/// the first window appears. It is deliberately the ONLY place that news up
/// services - see docs/ARCHITECTURE.md.
/// </para>
/// <para>
/// Startup budget: cold start to first frame must stay under 2 s (docs/ROADMAP.md,
/// "Non-functional targets"). Nothing here may block on I/O: probing for the ESP32
/// is kicked off asynchronously AFTER the shell is visible, never before.
/// </para>
/// </remarks>
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // TODO(phase-1): build the ServiceCollection, register the protocol stack,
        // apply the persisted theme, then resolve and show the shell window.
        new MainWindow().Show();
    }
}
