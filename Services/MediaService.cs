using Windows.Media.Control;

namespace DynamicIsland.Services;

/// <summary>
/// Now Playing, read from Windows' system media sessions. Works with anything
/// that shows up in the Windows media flyout (Spotify, browsers, most players).
/// </summary>
internal sealed class MediaService
{
    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private GlobalSystemMediaTransportControlsSession? _session;

    private bool _hasSession;
    private bool _isPlaying;
    private string _title = "";
    private string _artist = "";
    private DateTime _demoUntil = DateTime.MinValue;

    /// <summary>True while the built-in demo track is "playing" (started from the control panel).</summary>
    public bool Demo => DateTime.Now < _demoUntil;

    public bool HasSession => Demo || _hasSession;
    public bool IsPlaying => Demo || _isPlaying;
    public string Title => Demo ? "Demo Song" : _title;
    public string Artist => Demo ? "Dynamic Island" : _artist;
    public byte[]? Thumbnail { get; private set; }

    public void StartDemo(int seconds)
    {
        _demoUntil = DateTime.Now.AddSeconds(seconds);
        Changed?.Invoke();
    }

    /// <summary>Raised on a background thread whenever anything above changes.</summary>
    public event Action? Changed;

    public async Task StartAsync()
    {
        try
        {
            _manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            _manager.CurrentSessionChanged += OnSessionChanged;
            Attach();
        }
        catch
        {
            // media sessions unavailable: the island simply never shows Now Playing
        }
    }

    private void OnSessionChanged(GlobalSystemMediaTransportControlsSessionManager sender, CurrentSessionChangedEventArgs args)
    {
        Attach();
    }

    private void Attach()
    {
        try
        {
            if (_session != null)
            {
                _session.MediaPropertiesChanged -= OnPropertiesChanged;
                _session.PlaybackInfoChanged -= OnPlaybackChanged;
            }

            _session = _manager?.GetCurrentSession();

            if (_session != null)
            {
                _session.MediaPropertiesChanged += OnPropertiesChanged;
                _session.PlaybackInfoChanged += OnPlaybackChanged;
            }
        }
        catch
        {
            _session = null;
        }

        _ = UpdateAsync();
    }

    private void OnPropertiesChanged(GlobalSystemMediaTransportControlsSession sender, MediaPropertiesChangedEventArgs args)
    {
        _ = UpdateAsync();
    }

    private void OnPlaybackChanged(GlobalSystemMediaTransportControlsSession sender, PlaybackInfoChangedEventArgs args)
    {
        _ = UpdateAsync();
    }

    private async Task UpdateAsync()
    {
        try
        {
            var session = _session;
            if (session == null)
            {
                _hasSession = false;
                _isPlaying = false;
                _title = "";
                _artist = "";
                Thumbnail = null;
                Changed?.Invoke();
                return;
            }

            var playback = session.GetPlaybackInfo();
            _isPlaying = playback != null &&
                        playback.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;

            var props = await session.TryGetMediaPropertiesAsync();
            _title = props?.Title ?? "";
            _artist = props?.Artist ?? "";
            _hasSession = !string.IsNullOrWhiteSpace(_title);

            byte[]? art = null;
            if (props?.Thumbnail != null)
            {
                try
                {
                    using var stream = await props.Thumbnail.OpenReadAsync();
                    using var net = stream.AsStreamForRead();
                    using var memory = new MemoryStream();
                    await net.CopyToAsync(memory);
                    art = memory.ToArray();
                }
                catch
                {
                    art = null;
                }
            }
            Thumbnail = art;
        }
        catch
        {
            // a session can disappear mid-read; keep the last good state
        }

        Changed?.Invoke();
    }

    public async void TogglePlay()
    {
        try { if (_session != null) await _session.TryTogglePlayPauseAsync(); } catch { }
    }

    public async void Next()
    {
        try { if (_session != null) await _session.TrySkipNextAsync(); } catch { }
    }

    public async void Previous()
    {
        try { if (_session != null) await _session.TrySkipPreviousAsync(); } catch { }
    }
}
