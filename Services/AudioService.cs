using System.Diagnostics;
using NAudio.CoreAudioApi;
using NAudio.Dsp;
using NAudio.Wave;

namespace DynamicIsland.Services;

/// <summary>One app that is currently making sound.</summary>
internal sealed class AudioApp
{
    public string Name = "";
    public float Volume;
    public bool Muted;
}

/// <summary>
/// Three audio jobs: volume-change events, mic/speaker mute for call controls,
/// and a live spectrum of whatever the PC is playing for the visualizer.
/// </summary>
internal sealed class AudioService : IDisposable
{
    public const int BarCount = 6;
    private const int FftSize = 1024;
    private const int FftPower = 10; // 2^10 = 1024

    // Band edges in Hz for the six bars, low to high.
    private static readonly double[] BandEdges = { 60, 150, 400, 1000, 2500, 6000, 14000 };

    private readonly MMDeviceEnumerator _enumerator = new();
    private readonly object _lock = new();
    private readonly Complex[] _fft = new Complex[FftSize];
    private readonly float[] _levels = new float[BarCount];
    private MMDevice? _speaker;
    private WasapiLoopbackCapture? _capture;
    private int _fftPos;
    private DateTime _lastData = DateTime.MinValue;

    /// <summary>Raised on a background thread: volume 0..1 and whether output is muted.</summary>
    public event Action<float, bool>? VolumeChanged;

    public void Start()
    {
        RefreshDevice();
    }

    /// <summary>
    /// Follows the default output device (speakers to headphones and back).
    /// Returns true when the device changed.
    /// </summary>
    public bool RefreshDevice()
    {
        try
        {
            MMDevice current = _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            if (_speaker != null && current.ID == _speaker.ID)
            {
                current.Dispose();
                return false;
            }

            if (_speaker != null)
            {
                try { _speaker.AudioEndpointVolume.OnVolumeNotification -= OnVolume; } catch { }
            }
            _speaker = current;
            _speaker.AudioEndpointVolume.OnVolumeNotification += OnVolume;
            return true;
        }
        catch
        {
            return false; // no output device right now
        }
    }

    // ---- volume controls -----------------------------------------------

    /// <summary>System volume, 0..1.</summary>
    public float MasterVolume
    {
        get
        {
            try { return _speaker?.AudioEndpointVolume.MasterVolumeLevelScalar ?? 0; }
            catch { return 0; }
        }
        set
        {
            try
            {
                if (_speaker != null) _speaker.AudioEndpointVolume.MasterVolumeLevelScalar = Math.Clamp(value, 0f, 1f);
            }
            catch
            {
                // device went away
            }
        }
    }

    private List<(string Name, AudioSessionControl Session)> ActiveSessions()
    {
        var list = new List<(string Name, AudioSessionControl Session)>();
        if (_speaker == null) return list;
        try
        {
            AudioSessionManager manager = _speaker.AudioSessionManager;
            manager.RefreshSessions();
            var sessions = manager.Sessions;
            for (int i = 0; i < sessions.Count; i++)
            {
                AudioSessionControl session = sessions[i];
                if ((int)session.State != 1) continue; // 1 = actively playing
                list.Add((SessionName(session), session));
            }
        }
        catch
        {
            // sessions changed mid-read
        }
        return list;
    }

    private static string SessionName(AudioSessionControl session)
    {
        try
        {
            if (session.IsSystemSoundsSession) return "System sounds";
            using Process process = Process.GetProcessById((int)session.GetProcessID);
            string name = process.ProcessName;
            if (name.Equals("msedge", StringComparison.OrdinalIgnoreCase)) return "Edge";
            if (name.Length == 0) return "App";
            return char.ToUpperInvariant(name[0]) + name.Substring(1);
        }
        catch
        {
            return "App";
        }
    }

    /// <summary>Apps that are playing sound right now, one entry per app.</summary>
    public List<AudioApp> GetApps()
    {
        var apps = new List<AudioApp>();
        foreach (var (name, session) in ActiveSessions())
        {
            try
            {
                float volume = session.SimpleAudioVolume.Volume;
                bool muted = session.SimpleAudioVolume.Mute;
                AudioApp? existing = apps.FirstOrDefault(app => app.Name == name);
                if (existing == null)
                {
                    apps.Add(new AudioApp { Name = name, Volume = volume, Muted = muted });
                }
                else
                {
                    existing.Muted = existing.Muted && muted;
                }
            }
            catch
            {
                // session ended
            }
        }
        return apps;
    }

    public void SetAppVolume(string appName, float volume)
    {
        foreach (var (name, session) in ActiveSessions())
        {
            if (name != appName) continue;
            try { session.SimpleAudioVolume.Volume = Math.Clamp(volume, 0f, 1f); } catch { }
        }
    }

    public void SetAppMuted(string appName, bool muted)
    {
        foreach (var (name, session) in ActiveSessions())
        {
            if (name != appName) continue;
            try { session.SimpleAudioVolume.Mute = muted; } catch { }
        }
    }

    private void OnVolume(AudioVolumeNotificationData data)
    {
        VolumeChanged?.Invoke(data.MasterVolume, data.Muted);
    }

    // ---- call controls -------------------------------------------------

    private MMDevice? GetMic()
    {
        try
        {
            return _enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications);
        }
        catch
        {
            return null; // no microphone
        }
    }

    public bool MicMuted
    {
        get
        {
            try { return GetMic()?.AudioEndpointVolume.Mute ?? false; }
            catch { return false; }
        }
    }

    /// <summary>Mutes or unmutes the default microphone system-wide.</summary>
    public void ToggleMic()
    {
        try
        {
            var mic = GetMic();
            if (mic != null) mic.AudioEndpointVolume.Mute = !mic.AudioEndpointVolume.Mute;
        }
        catch
        {
            // device went away
        }
    }

    public bool SpeakerMuted
    {
        get
        {
            try { return _speaker?.AudioEndpointVolume.Mute ?? false; }
            catch { return false; }
        }
    }

    public void ToggleSpeaker()
    {
        try
        {
            if (_speaker != null) _speaker.AudioEndpointVolume.Mute = !_speaker.AudioEndpointVolume.Mute;
        }
        catch
        {
            // device went away
        }
    }

    // ---- visualizer ----------------------------------------------------

    public void StartCapture()
    {
        if (_capture != null) return;
        try
        {
            _fftPos = 0;
            _capture = new WasapiLoopbackCapture();
            _capture.DataAvailable += OnData;
            _capture.StartRecording();
        }
        catch
        {
            _capture = null; // visualizer falls back to a gentle idle wave
        }
    }

    public void StopCapture()
    {
        var capture = _capture;
        _capture = null;
        if (capture == null) return;
        try
        {
            capture.DataAvailable -= OnData;
            capture.RecordingStopped += (_, _) =>
            {
                try { capture.Dispose(); } catch { }
            };
            capture.StopRecording();
        }
        catch
        {
            // already stopped
        }
    }

    private void OnData(object? sender, WaveInEventArgs e)
    {
        var capture = sender as WasapiLoopbackCapture;
        if (capture == null) return;

        WaveFormat format = capture.WaveFormat;
        int channels = Math.Max(1, format.Channels);
        int bytesPerSample = format.BitsPerSample / 8;
        if (bytesPerSample != 4 && bytesPerSample != 2) return;
        int frameSize = bytesPerSample * channels;

        for (int i = 0; i + frameSize <= e.BytesRecorded; i += frameSize)
        {
            float sample = 0;
            for (int c = 0; c < channels; c++)
            {
                int offset = i + c * bytesPerSample;
                sample += bytesPerSample == 4
                    ? BitConverter.ToSingle(e.Buffer, offset)
                    : BitConverter.ToInt16(e.Buffer, offset) / 32768f;
            }
            sample /= channels;

            _fft[_fftPos].X = (float)(sample * FastFourierTransform.HannWindow(_fftPos, FftSize));
            _fft[_fftPos].Y = 0;
            _fftPos++;

            if (_fftPos == FftSize)
            {
                ComputeBands(format.SampleRate);
                _fftPos = 0;
            }
        }
    }

    private void ComputeBands(int sampleRate)
    {
        FastFourierTransform.FFT(true, FftPower, _fft);

        lock (_lock)
        {
            for (int band = 0; band < BarCount; band++)
            {
                int from = Math.Max(1, (int)(BandEdges[band] * FftSize / sampleRate));
                int to = Math.Min(FftSize / 2 - 1, (int)(BandEdges[band + 1] * FftSize / sampleRate));
                if (to < from) to = from;

                double peak = 0;
                for (int bin = from; bin <= to; bin++)
                {
                    double magnitude = Math.Sqrt(_fft[bin].X * _fft[bin].X + _fft[bin].Y * _fft[bin].Y);
                    if (magnitude > peak) peak = magnitude;
                }

                // Map roughly -70 dB .. -15 dB onto 0 .. 1.
                double db = 20 * Math.Log10(peak + 1e-9);
                float level = (float)Math.Clamp((db + 70) / 55, 0, 1);
                _levels[band] = Math.Max(level, _levels[band] * 0.75f);
            }
            _lastData = DateTime.UtcNow;
        }
    }

    /// <summary>Copies the current bar levels (0..1). Returns false when there is no live audio.</summary>
    public bool GetLevels(float[] destination)
    {
        lock (_lock)
        {
            bool live = (DateTime.UtcNow - _lastData).TotalMilliseconds < 250;
            for (int i = 0; i < BarCount && i < destination.Length; i++)
            {
                destination[i] = live ? _levels[i] : 0;
            }
            return live;
        }
    }

    public void Dispose()
    {
        StopCapture();
        try
        {
            if (_speaker != null) _speaker.AudioEndpointVolume.OnVolumeNotification -= OnVolume;
        }
        catch
        {
            // shutting down anyway
        }
    }
}
