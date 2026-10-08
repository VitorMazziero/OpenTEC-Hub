using System.Media;
using System.Windows.Threading;
using OpenTECHub.Services.Persistence;

namespace OpenTECHub.Services.Alarms;

/// <summary>
/// The real audible annunciator: a repeating system sound while an alarm is annunciating.
/// </summary>
/// <remarks>
/// Deliberately trivial and behind <see cref="IAlarmAnnunciator"/> so the whole timed-silence
/// policy lives in <see cref="AlarmService"/> and is tested without ever making a sound. The
/// repeat exists because a single beep at the moment of failure is easy to miss; the operator
/// silences it for a window. The speaker button (<see cref="UiSettings.SoundMuted"/>, D-069) is a layer
/// above that: while it is off nothing plays, however the alarm service sees the alarms, and turning it
/// back on resumes the sound if an alarm is still sounding.
/// </remarks>
public sealed class AlarmAnnunciator : IAlarmAnnunciator, IDisposable
{
    private readonly DispatcherTimer _timer;
    private readonly ISettingsService _settings;
    private bool _sounding;

    public AlarmAnnunciator(ISettingsService settings)
    {
        _settings = settings;
        _timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(3),
        };
        _timer.Tick += (_, _) => Play();
        _settings.Changed += OnSettingsChanged;
    }

    private bool Muted => _settings.Current.Ui.SoundMuted;

    public void SetSounding(bool sounding)
    {
        _sounding = sounding;
        Apply();
    }

    private void OnSettingsChanged(AppSettings settings)
    {
        if (_timer.Dispatcher.CheckAccess())
        {
            Apply();
        }
        else
        {
            _timer.Dispatcher.BeginInvoke(Apply);
        }
    }

    private void Apply()
    {
        if (_sounding && !Muted)
        {
            if (!_timer.IsEnabled)
            {
                Play();
                _timer.Start();
            }
        }
        else
        {
            _timer.Stop();
        }
    }

    private void Play()
    {
        if (!Muted)
        {
            SystemSounds.Exclamation.Play();
        }
    }

    public void Dispose()
    {
        _settings.Changed -= OnSettingsChanged;
        _timer.Stop();
    }
}
