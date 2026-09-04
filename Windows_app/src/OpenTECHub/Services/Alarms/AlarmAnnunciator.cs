using System.Media;
using System.Windows.Threading;

namespace OpenTECHub.Services.Alarms;

/// <summary>
/// The real audible annunciator: a repeating system sound while an alarm is annunciating.
/// </summary>
/// <remarks>
/// Deliberately trivial and behind <see cref="IAlarmAnnunciator"/> so the whole timed-silence
/// policy lives in <see cref="AlarmService"/> and is tested without ever making a sound. The
/// repeat exists because a single beep at the moment of failure is easy to miss; the operator
/// silences it for a window rather than muting it forever.
/// </remarks>
public sealed class AlarmAnnunciator : IAlarmAnnunciator, IDisposable
{
    private readonly DispatcherTimer _timer;

    public AlarmAnnunciator()
    {
        _timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(3),
        };
        _timer.Tick += (_, _) => SystemSounds.Exclamation.Play();
    }

    public void SetSounding(bool sounding)
    {
        if (sounding)
        {
            if (!_timer.IsEnabled)
            {
                SystemSounds.Exclamation.Play();
                _timer.Start();
            }
        }
        else
        {
            _timer.Stop();
        }
    }

    public void Dispose() => _timer.Stop();
}
