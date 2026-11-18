using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using DynamicIsland.Core;
using DynamicIsland.Pets;
using DynamicIsland.Services;
using Microsoft.Win32;

namespace DynamicIsland;

/// <summary>
/// The island itself. One transparent, always-on-top window holds a black pill
/// whose width, height and corner radius are driven by springs. What the pill
/// shows is decided in <see cref="Refresh"/> from the current activities.
/// </summary>
public partial class MainWindow : Window
{
    private const double IdleWidth = 150;
    private const double PillHeight = 34;
    private const double BaseWindowWidth = 680;
    private const double BaseWindowHeight = 440;
    private const double EdgeGap = 6;
    private const double ExpandedWidth = 420;
    private const double ExpandedMaxHeight = 425;
    private const double StatBarWidth = 190;
    private const double MeterWidth = 62;

    private enum Mode { Compact, Expanded }
    private enum Tab { Home, Audio, Shelf, Clip, Stats }
    private enum Activity { Call, Timer, Media }

    private sealed record Transient(string Glyph, string Text, string Trail, Brush Color, bool KeepPet);

    private sealed class AudioRow
    {
        public Slider Slider = new();
        public TextBlock Percent = new();
        public Button Mute = new();
        public bool Muted;
    }

    private sealed class StatRow
    {
        public Grid Row = new();
        public Rectangle Bar = new();
        public TextBlock Value = new();
    }

    // Colors (iOS system palette)
    private static readonly Brush White = Brushes.White;
    private static readonly Brush Green = MakeBrush("#FF30D158");
    private static readonly Brush Orange = MakeBrush("#FFFF9F0A");
    private static readonly Brush Red = MakeBrush("#FFFF453A");
    private static readonly Brush Blue = MakeBrush("#FF0A84FF");
    private static readonly Brush Pink = MakeBrush("#FFFF6482");
    private static readonly Brush Faint = MakeBrush("#26FFFFFF");
    private static readonly Brush Selected = MakeBrush("#4DFFFFFF");
    private static readonly Brush Dim = MakeBrush("#99FFFFFF");

    // Pill geometry
    private readonly Spring _width = new(IdleWidth);
    private readonly Spring _height = new(PillHeight);
    private readonly Spring _radius = new(PillHeight / 2);
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private double _lastFrame;
    private bool _rendering;

    // State
    private Mode _mode = Mode.Compact;
    private Tab _tab = Tab.Home;
    private Transient? _transient;
    private bool _bubbleShown;
    private bool _dragging;
    private IntPtr _hwnd;

    // Services
    private readonly MediaService _media = new();
    private readonly AudioService _audio = new();
    private readonly SystemStats _stats = new();
    private readonly BluetoothService _bluetooth = new();

    // Timers
    private readonly DispatcherTimer _tick;
    private readonly DispatcherTimer _vizTimer;
    private readonly DispatcherTimer _collapseTimer;
    private readonly DispatcherTimer _transientTimer;

    // Activities
    private CallState _call = new();
    private DateTime? _callStart;
    private bool _micMuted;
    private DateTime? _timerEnd;
    private byte[]? _artBytes;
    private bool? _lastPlugged;
    private int _lastBattery = 100;

    // Visualizer
    private readonly Rectangle[] _bars;
    private readonly Rectangle[] _bigBars;
    private readonly float[] _levels = new float[AudioService.BarCount];
    private readonly double[] _shown = new double[AudioService.BarCount];
    private double _vizTime;

    // Shelf + clipboard
    private readonly ObservableCollection<ShelfItem> _shelf = new();
    private readonly ObservableCollection<ClipItem> _clips = new();
    private Point _shelfDragStart;
    private bool _shelfPressed;

    // Stats rows
    private StatRow _cpuRow = new();
    private StatRow _ramRow = new();
    private StatRow _gpuRow = new();
    private StatRow _batteryRow = new();

    // Pet
    private Rectangle _happyBar = new();
    private Rectangle _fullBar = new();
    private Rectangle _energyBar = new();
    private string _petNote = "";
    private DateTime _petNoteUntil = DateTime.MinValue;

    // Volume tab
    private readonly Dictionary<string, AudioRow> _audioRows = new();
    private string _audioAppsKey = "";
    private bool _audioUpdating;

    // Demo states started from the control panel
    private DateTime _demoCallUntil = DateTime.MinValue;
    private bool _mediaDemoWasOn;
    private int _ticks;

    // Which display the island is on, and what (if anything) is fullscreen there
    private readonly List<IntPtr> _fullscreenWindows = new();
    private bool _paused;
    private bool _growUp;
    private bool _locked;

    /// <summary>Where the island is and why, shown in the control panel.</summary>
    public string PlacementStatus { get; private set; } = "";

    // Dragging the island around
    private bool _pressed;
    private Point _pressPoint;
    private bool _movingWindow;
    private DateTime _dragEndedAt = DateTime.MinValue;
    private readonly DispatcherTimer _hoverTimer;

    // Appearance
    private Brush _accent = Pink;
    private Brush? _artAccent;
    private double _compactRadius = PillHeight / 2;
    private double _expandedRadius = 30;

    private SettingsWindow? _settingsWindow;

    public MainWindow()
    {
        InitializeComponent();

        Left = (SystemParameters.PrimaryScreenWidth - Width) / 2;
        Top = 0;

        _bars = MakeBars(BarsHost, 3, 2);
        _bigBars = MakeBars(BigBars, 5, 3);
        BuildStatRows();
        BuildPetMeters();

        Pet.Clicked += () =>
        {
            Pet.ShowLove();
            OnPetted();
        };
        Playground.Petted += OnPetted;
        Playground.Fed += OnFed;
        Playground.Played += OnPlayed;

        foreach (string path in App.Settings.Shelf)
        {
            if (File.Exists(path) || Directory.Exists(path)) _shelf.Add(new ShelfItem(path));
        }
        ShelfList.ItemsSource = _shelf;
        ClipList.ItemsSource = _clips;

        _tick = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _tick.Tick += (_, _) => OnTick();

        _vizTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
        _vizTimer.Tick += (_, _) => OnVisualizerFrame();

        _collapseTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(450) };
        _collapseTimer.Tick += (_, _) =>
        {
            _collapseTimer.Stop();
            // Don't close mid-drag (a file from the shelf, or a volume slider).
            if (!_dragging && !Pill.IsMouseOver && Mouse.Captured == null) SetMode(Mode.Compact);
        };

        _hoverTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
        _hoverTimer.Tick += (_, _) =>
        {
            _hoverTimer.Stop();
            if (_mode == Mode.Compact && Pill.IsMouseOver && !_pressed && !_movingWindow)
            {
                ShowTab(Tab.Home);
                SetMode(Mode.Expanded);
            }
        };

        _transientTimer = new DispatcherTimer();
        _transientTimer.Tick += (_, _) =>
        {
            _transientTimer.Stop();
            _transient = null;
            Refresh();
        };

        Loaded += OnLoaded;
        Closed += OnClosed;
    }

    // ------------------------------------------------------------------
    // Startup / shutdown
    // ------------------------------------------------------------------

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        _hwnd = new WindowInteropHelper(this).Handle;
        Native.MakeOverlay(_hwnd);

        // Clipboard history: Windows tells us whenever the clipboard changes.
        HwndSource.FromHwnd(_hwnd)?.AddHook(WndProc);
        Native.AddClipboardFormatListener(_hwnd);
        Native.WatchSession(_hwnd);

        // Another app coming to the front can knock the island out of the always-on-top band; put it back.
        Native.WatchForeground(() =>
        {
            if (!_paused) Native.KeepOnTop(_hwnd);
            UpdatePlacement();
        });
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        ApplySettings();
        ApplyPill();
        UpdatePlacement();

        _media.Changed += () => Dispatcher.InvokeAsync(OnMediaChanged);
        _audio.VolumeChanged += (volume, muted) => Dispatcher.InvokeAsync(() => OnVolumeChanged(volume, muted));
        _bluetooth.Connected += name => Dispatcher.InvokeAsync(() =>
            ShowTransient(Glyphs.Bluetooth, name, "Connected", Blue, 3));


        _audio.Start();
        _bluetooth.Start();
        _stats.Sample();
        _tick.Start();

        Refresh();

        // First launch: open the control panel so a new tester knows where everything is.
        if (!App.Settings.FirstRunDone)
        {
            App.Settings.FirstRunDone = true;
            App.Settings.Save();
            OpenControlPanel("Test");
        }

        await _media.StartAsync();
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        Native.StopWatchingSession(_hwnd);
        Native.StopWatchingForeground();
        Native.RemoveClipboardFormatListener(_hwnd);
        _bluetooth.Stop();
        _audio.Dispose();
        SaveShelf();
        Application.Current.Shutdown();
    }

    private void ApplySettings()
    {
        AppSettings settings = App.Settings;

        PetKind kind = PetControl.Parse(settings.PetKind);
        if (Pet.Kind != kind) Pet.Kind = kind;
        if (Playground.Pet.Kind != kind) Playground.Pet.Kind = kind;
        Pet.ToolTip = "Pet " + settings.PetName;
        Playground.Pet.ToolTip = "Pet " + settings.PetName;
        PetSection.Visibility = settings.PetEnabled ? Visibility.Visible : Visibility.Collapsed;

        Native.SetHiddenFromCapture(_hwnd, settings.HideFromCapture);
        if (!settings.ClipboardEnabled) _clips.Clear();

        ApplyAppearance();

        UpdatePlacement();
        SyncVisualizer();
        Refresh();
    }

    /// <summary>Called by the control panel after a setting changes.</summary>
    public void ReloadSettings() => ApplySettings();

    /// <summary>Opens the control panel (from the gear, the tray icon, or first launch).</summary>
    public void OpenControlPanel(string? page = null)
    {
        SetMode(Mode.Compact);
        if (_settingsWindow == null)
        {
            _settingsWindow = new SettingsWindow(this);
            _settingsWindow.Closed += (_, _) => _settingsWindow = null;
            _settingsWindow.Show();
        }
        else
        {
            if (_settingsWindow.WindowState == WindowState.Minimized) _settingsWindow.WindowState = WindowState.Normal;
            _settingsWindow.Activate();
        }
        if (page != null) _settingsWindow.ShowPage(page);
    }

    /// <summary>Shows one island state on demand, for the control panel's "Try it out" page.</summary>
    public void RunTest(string name)
    {
        SetMode(Mode.Compact);
        switch (name)
        {
            case "charging":
                ShowTransient(Glyphs.Bolt, "Charging", "80%", Green, 3);
                break;
            case "lowbattery":
                ShowTransient(Glyphs.Warning, "Low battery", "20%", Red, 4);
                break;
            case "bluetooth":
                ShowTransient(Glyphs.Bluetooth, "Demo Headphones", "Connected", Blue, 3);
                break;
            case "volume":
                ShowTransient(Glyphs.Volume, "Volume", "60%", White, 2);
                break;
            case "unlock":
                // Pretend to lock: the pet falls asleep; then play the real unlock sequence.
                OnLocked();
                After(2.5, PlayUnlock);
                break;
            case "timer":
                _timerEnd = DateTime.Now.AddSeconds(10);
                break;
            case "call":
                _demoCallUntil = DateTime.Now.AddSeconds(12);
                PollCall();
                break;
            case "music":
                _media.StartDemo(15);
                break;
            case "open":
                ShowTab(Tab.Home);
                SetMode(Mode.Expanded);
                break;
        }
        Refresh();
    }

    // ------------------------------------------------------------------
    // Deciding what the island shows
    // ------------------------------------------------------------------

    private List<Activity> ActiveActivities()
    {
        // Highest priority first, like iOS: calls beat timers beat music.
        var list = new List<Activity>();
        if (_callStart != null) list.Add(Activity.Call);
        if (_timerEnd != null) list.Add(Activity.Timer);
        if (_media.HasSession && _media.IsPlaying) list.Add(Activity.Media);
        return list;
    }

    private void Refresh()
    {
        if (!IsLoaded) return;

        List<Activity> activities = ActiveActivities();

        if (_mode == Mode.Expanded)
        {
            UpdateExpanded();
            ShowBubble(false);

            ExpandedView.Measure(new Size(ExpandedWidth, double.PositiveInfinity));
            double height = Math.Min(ExpandedView.DesiredSize.Height, ExpandedMaxHeight);
            SetTargets(ExpandedWidth, Math.Max(height, 90), _expandedRadius);
            return;
        }

        // The pet lives in the middle of the pill. Notifications need the room, so it steps out for those.
        bool petVisible = App.Settings.PetEnabled && (_transient == null || _transient.KeepPet);
        PetHost.Visibility = petVisible ? Visibility.Visible : Visibility.Collapsed;

        double width;
        if (_transient != null)
        {
            SetCompact(_transient.Glyph, _transient.Text, _transient.Trail, _transient.Color, false, false);
            width = petVisible ? 300 : 260;
        }
        else if (activities.Count > 0)
        {
            width = ShowCompactActivity(activities[0]) + (petVisible ? 54 : 0);
        }
        else
        {
            SetCompact(null, "", "", White, false, false);
            width = Math.Clamp(App.Settings.IdleWidth, 80, 260);
        }

        bool bubble = _transient == null && activities.Count > 1;
        if (bubble)
        {
            switch (activities[1])
            {
                case Activity.Timer:
                    BubbleGlyph.Text = Glyphs.Stopwatch;
                    BubbleGlyph.Foreground = Orange;
                    break;
                case Activity.Media:
                    BubbleGlyph.Text = Glyphs.Music;
                    BubbleGlyph.Foreground = _accent;
                    break;
                default:
                    BubbleGlyph.Text = Glyphs.Phone;
                    BubbleGlyph.Foreground = Green;
                    break;
            }
        }
        ShowBubble(bubble);

        SetTargets(width, PillHeight, _compactRadius);
    }

    private double ShowCompactActivity(Activity activity)
    {
        switch (activity)
        {
            case Activity.Call:
                SetCompact(
                    _micMuted ? Glyphs.MicOff : Glyphs.Phone,
                    _call.AppName,
                    Elapsed(DateTime.Now - _callStart!.Value),
                    _micMuted ? Orange : Green,
                    false, false);
                return 240;

            case Activity.Timer:
                SetCompact(Glyphs.Stopwatch, "Timer", Elapsed(_timerEnd!.Value - DateTime.Now), Orange, false, false);
                return 200;

            default:
                SetCompact(null, "", "", White, true, true);
                return 190;
        }
    }

    private void SetCompact(string? glyph, string text, string trail, Brush color, bool art, bool bars)
    {
        ArtSmall.Visibility = art ? Visibility.Visible : Visibility.Collapsed;

        LeadGlyph.Visibility = glyph != null ? Visibility.Visible : Visibility.Collapsed;
        LeadGlyph.Text = glyph ?? "";
        LeadGlyph.Foreground = color;

        LeadText.Text = text;
        LeadText.Visibility = text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

        TrailText.Text = trail;
        TrailText.Foreground = color;
        TrailText.Visibility = trail.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

        BarsHost.Visibility = bars ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateExpanded()
    {
        AppSettings settings = App.Settings;

        // Call controls
        bool inCall = _callStart != null;
        CallSection.Visibility = inCall ? Visibility.Visible : Visibility.Collapsed;
        if (inCall)
        {
            CallApp.Text = _call.AppName;
            string info = (_call.MicInUse ? "Mic in use" : "Camera in use") + " · " + Elapsed(DateTime.Now - _callStart!.Value);
            if (_call.MicInUse && _call.CameraInUse) info += " · Camera on";
            CallInfo.Text = info;

            MicBtn.Content = _micMuted ? Glyphs.MicOff : Glyphs.Mic;
            MicBtn.Background = _micMuted ? Red : Faint;

            bool speakerMuted = _audio.SpeakerMuted;
            SpeakerBtn.Content = speakerMuted ? Glyphs.Mute : Glyphs.Volume;
            SpeakerBtn.Background = speakerMuted ? Red : Faint;

            ShowAppBtn.Visibility = _call.ProcessName != null ? Visibility.Visible : Visibility.Collapsed;
        }

        // Now playing
        MediaSection.Visibility = _media.HasSession ? Visibility.Visible : Visibility.Collapsed;
        MediaTitle.Text = _media.Title;
        MediaArtist.Text = _media.Artist;
        PlayBtn.Content = _media.IsPlaying ? Glyphs.Pause : Glyphs.Play;

        // Timer
        bool timing = _timerEnd != null;
        TimerBig.Text = timing ? Elapsed(_timerEnd!.Value - DateTime.Now) : "Timer";
        TimerCancel.Visibility = timing ? Visibility.Visible : Visibility.Collapsed;

        // Pet
        if (settings.PetEnabled)
        {
            PetLine.Text = DateTime.Now < _petNoteUntil ? _petNote : settings.PetName + " " + settings.PetMood();
            SetMeter(_happyBar, settings.PetHappiness);
            SetMeter(_fullBar, settings.PetFullness);
            SetMeter(_energyBar, settings.PetEnergy);
        }

        TabShelf.Visibility = settings.ShowShelfTab ? Visibility.Visible : Visibility.Collapsed;
        TabClip.Visibility = settings.ClipboardEnabled ? Visibility.Visible : Visibility.Collapsed;
        TabStats.Visibility = settings.ShowStatsTab ? Visibility.Visible : Visibility.Collapsed;
        if (!TabAvailable(_tab)) ShowTab(Tab.Home);

        ShelfEmpty.Visibility = _shelf.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ClipEmpty.Visibility = _clips.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private static string Elapsed(TimeSpan span)
    {
        if (span < TimeSpan.Zero) span = TimeSpan.Zero;
        if (span.TotalHours >= 1)
        {
            return ((int)span.TotalHours) + ":" + span.Minutes.ToString("00") + ":" + span.Seconds.ToString("00");
        }
        return ((int)span.TotalMinutes) + ":" + span.Seconds.ToString("00");
    }

    // ------------------------------------------------------------------
    // Modes, tabs and animation
    // ------------------------------------------------------------------

    private void SetMode(Mode mode)
    {
        if (_mode == mode) return;
        _mode = mode;
        bool expanded = mode == Mode.Expanded;

        if (expanded) ExpandedView.Visibility = Visibility.Visible;
        ExpandedView.IsHitTestVisible = expanded;
        CompactView.IsHitTestVisible = !expanded;

        Fade(ExpandedView, expanded ? 1 : 0, expanded ? 240 : 110, () =>
        {
            if (_mode == Mode.Compact) ExpandedView.Visibility = Visibility.Collapsed;
        });
        Fade(CompactView, expanded ? 0 : 1, 150, null);

        AnimateScale(1);
        if (expanded && _tab == Tab.Stats) UpdateStats();
        Refresh();
    }

    private void ShowTab(Tab tab)
    {
        if (!TabAvailable(tab)) tab = Tab.Home;
        _tab = tab;
        HomePanel.Visibility = tab == Tab.Home ? Visibility.Visible : Visibility.Collapsed;
        AudioPanel.Visibility = tab == Tab.Audio ? Visibility.Visible : Visibility.Collapsed;
        ShelfPanel.Visibility = tab == Tab.Shelf ? Visibility.Visible : Visibility.Collapsed;
        ClipPanel.Visibility = tab == Tab.Clip ? Visibility.Visible : Visibility.Collapsed;
        StatsPanel.Visibility = tab == Tab.Stats ? Visibility.Visible : Visibility.Collapsed;

        TabHome.Background = tab == Tab.Home ? Selected : Brushes.Transparent;
        TabAudio.Background = tab == Tab.Audio ? Selected : Brushes.Transparent;
        TabShelf.Background = tab == Tab.Shelf ? Selected : Brushes.Transparent;
        TabClip.Background = tab == Tab.Clip ? Selected : Brushes.Transparent;
        TabStats.Background = tab == Tab.Stats ? Selected : Brushes.Transparent;

        if (tab == Tab.Stats) UpdateStats();
        if (tab == Tab.Audio) UpdateAudioTab();
        Refresh();
    }

    private static bool TabAvailable(Tab tab)
    {
        AppSettings settings = App.Settings;
        return tab switch
        {
            Tab.Shelf => settings.ShowShelfTab,
            Tab.Clip => settings.ClipboardEnabled,
            Tab.Stats => settings.ShowStatsTab,
            _ => true
        };
    }

    private void SetTargets(double width, double height, double radius)
    {
        if (_width.Target == width && _height.Target == height && _radius.Target == radius) return;
        _width.Target = width;
        _height.Target = height;
        _radius.Target = radius;

        if (_rendering) return;
        _rendering = true;
        _lastFrame = _clock.Elapsed.TotalSeconds;
        CompositionTarget.Rendering += OnFrame;
    }

    private void OnFrame(object? sender, EventArgs e)
    {
        double now = _clock.Elapsed.TotalSeconds;
        double dt = Math.Min(now - _lastFrame, 1.0 / 30);
        _lastFrame = now;

        bool moving = false;
        if (dt > 0)
        {
            moving |= _width.Step(dt);
            moving |= _height.Step(dt);
            moving |= _radius.Step(dt);
            ApplyPill();
        }
        else
        {
            moving = true; // same frame delivered twice; wait for the next one
        }

        if (!moving)
        {
            CompositionTarget.Rendering -= OnFrame;
            _rendering = false;
        }
    }

    private void ApplyPill()
    {
        double width = Math.Max(40, _width.Value);
        double height = Math.Max(20, _height.Value);
        double radius = Math.Max(0, Math.Min(_radius.Value, height / 2));

        Pill.Width = width;
        Pill.Height = height;
        PillBg.RadiusX = radius;
        PillBg.RadiusY = radius;
        PillContent.Clip = new RectangleGeometry(new Rect(0, 0, width, height), radius, radius);

        BubbleShift.X = width / 2 + 6 + PillHeight / 2;
    }

    private void ShowBubble(bool show)
    {
        if (_bubbleShown == show) return;
        _bubbleShown = show;
        Fade(Bubble, show ? 1 : 0, 200, null);
    }

    private static void Fade(UIElement element, double to, int milliseconds, Action? done)
    {
        var animation = new DoubleAnimation(to, TimeSpan.FromMilliseconds(milliseconds));
        if (done != null) animation.Completed += (_, _) => done();
        element.BeginAnimation(UIElement.OpacityProperty, animation);
    }

    private void AnimateScale(double to)
    {
        var ease = new QuadraticEase { EasingMode = EasingMode.EaseOut };
        var x = new DoubleAnimation(to, TimeSpan.FromMilliseconds(160)) { EasingFunction = ease };
        var y = new DoubleAnimation(to, TimeSpan.FromMilliseconds(160)) { EasingFunction = ease };
        PillScale.BeginAnimation(ScaleTransform.ScaleXProperty, x);
        PillScale.BeginAnimation(ScaleTransform.ScaleYProperty, y);
    }

    private void ShowTransient(string glyph, string text, string trail, Brush color, double seconds, bool keepPet = false)
    {
        _transient = new Transient(glyph, text, trail, color, keepPet);
        _transientTimer.Stop();
        _transientTimer.Interval = TimeSpan.FromSeconds(seconds);
        _transientTimer.Start();
        Refresh();
    }

    // ------------------------------------------------------------------
    // Mouse on the pill
    // ------------------------------------------------------------------

    private void Pill_MouseEnter(object sender, MouseEventArgs e)
    {
        _collapseTimer.Stop();
        if (_mode == Mode.Compact) AnimateScale(1.05);
        if (_mode == Mode.Compact && App.Settings.OpenOnHover)
        {
            _hoverTimer.Stop();
            _hoverTimer.Start();
        }
    }

    private void Pill_MouseLeave(object sender, MouseEventArgs e)
    {
        _hoverTimer.Stop();
        AnimateScale(1);
        if (_mode == Mode.Expanded && !_dragging)
        {
            _collapseTimer.Stop();
            _collapseTimer.Start();
        }
    }

    private void Pill_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_mode != Mode.Compact || App.Settings.LockPosition) return;
        _pressed = true;
        _pressPoint = e.GetPosition(this);
    }

    /// <summary>Press and drag the closed island to move it. A short click still opens it.</summary>
    private void Pill_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_pressed) return;
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            _pressed = false;
            return;
        }

        Point now = e.GetPosition(this);
        if (Math.Abs(now.X - _pressPoint.X) < 6 && Math.Abs(now.Y - _pressPoint.Y) < 6) return;

        _pressed = false;
        _hoverTimer.Stop();
        _movingWindow = true;
        try
        {
            DragMove(); // returns when the mouse button is released
        }
        catch (InvalidOperationException)
        {
            // button was already released
        }
        _movingWindow = false;
        _dragEndedAt = DateTime.Now;
        FinishDrag();
    }

    private void Pill_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        _pressed = false;
        if (_mode != Mode.Compact) return;
        if ((DateTime.Now - _dragEndedAt).TotalMilliseconds < 400) return; // end of a drag, not a click
        ShowTab(Tab.Home);
        SetMode(Mode.Expanded);
    }

    /// <summary>Remembers where the island was dropped, snapping back to the top centre when it's close.</summary>
    private void FinishDrag()
    {
        AppSettings settings = App.Settings;

        Point centre;
        try
        {
            centre = Pill.PointToScreen(new Point(Pill.ActualWidth / 2, Pill.ActualHeight / 2));
        }
        catch (InvalidOperationException)
        {
            return; // not on screen
        }

        Native.Monitor? monitor = Native.MonitorAt(centre.X, centre.Y);
        if (monitor == null || monitor.Width <= 0 || monitor.Height <= 0) return;

        double px = monitor.Dpi / 96.0;
        double fromTop = centre.Y - monitor.Top;
        double fromMiddle = Math.Abs(centre.X - (monitor.Left + monitor.Width / 2.0));

        settings.DisplayName = monitor.Name;
        if (fromTop < 60 * px && fromMiddle < 90 * px)
        {
            settings.CustomPosition = false; // snap home
            settings.AnchorX = 0.5;
            settings.AnchorY = 0;
        }
        else
        {
            settings.CustomPosition = true;
            settings.AnchorX = Math.Clamp((centre.X - monitor.Left) / monitor.Width, 0, 1);
            settings.AnchorY = fromTop < 60 * px ? 0 : Math.Clamp(fromTop / monitor.Height, 0, 1);
        }
        settings.Save();
        UpdatePlacement();
        _settingsWindow?.RefreshFromSettings();
    }

    private void Tab_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button button && button.Tag is string name && Enum.TryParse(name, out Tab tab))
        {
            ShowTab(tab);
        }
    }

    private void Settings_Click(object sender, RoutedEventArgs e) => OpenControlPanel();

    /// <summary>Scrolling over the closed island changes the system volume.</summary>
    private void Pill_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (_mode != Mode.Compact) return;
        _audio.MasterVolume = _audio.MasterVolume + (e.Delta > 0 ? 0.02f : -0.02f);
        e.Handled = true;
    }

    // ------------------------------------------------------------------
    // Once-a-second housekeeping
    // ------------------------------------------------------------------

    private void OnTick()
    {
        AppSettings settings = App.Settings;

        UpdatePlacement();
        if (!_paused) Native.KeepOnTop(_hwnd);

        bool asleep = _locked || Native.IdleSeconds() > 120;
        Pet.Sleeping = asleep;
        Playground.Pet.Sleeping = asleep;

        settings.AgePet();
        double pace = settings.PetFullness < 25 ? 0.6 : 1;
        Pet.SpeedFactor = pace;
        Playground.Pet.SpeedFactor = pace;

        PollCall();
        PollPower();

        if (_timerEnd != null && DateTime.Now >= _timerEnd.Value)
        {
            _timerEnd = null;
            System.Media.SystemSounds.Exclamation.Play();
            ShowTransient(Glyphs.Stopwatch, "Timer", "Done", Orange, 5);
        }

        if (_mode == Mode.Expanded && _tab == Tab.Stats) UpdateStats();
        if (_mode == Mode.Expanded && _tab == Tab.Audio) UpdateAudioTab();

        // Follow the default output device (plugging in headphones, etc.).
        _ticks++;
        if (_ticks % 5 == 0 && _audio.RefreshDevice())
        {
            _audio.StopCapture();
            SyncVisualizer();
        }

        // The demo track from the control panel has just finished.
        bool demo = _media.Demo;
        if (_mediaDemoWasOn && !demo) OnMediaChanged();
        _mediaDemoWasOn = demo;

        Refresh();
    }

    private void PollCall()
    {
        if (DateTime.Now < _demoCallUntil)
        {
            _call = new CallState { MicInUse = true, AppName = "Demo call" };
        }
        else if (App.Settings.CallDetection)
        {
            _call = CallService.Read();
        }
        else
        {
            _call = new CallState();
        }
        bool active = _call.MicInUse || _call.CameraInUse;

        if (active && _callStart == null) _callStart = DateTime.Now;
        if (!active) _callStart = null;

        _micMuted = active && _audio.MicMuted;
    }

    private void PollPower()
    {
        var power = SystemStats.Power();
        if (!power.HasBattery) return;

        if (_lastPlugged != null && _lastPlugged != power.Plugged)
        {
            if (power.Plugged)
            {
                ShowTransient(Glyphs.Bolt, "Charging", power.Percent + "%", Green, 3);
            }
            else
            {
                ShowTransient(Glyphs.Bolt, "On battery", power.Percent + "%", White, 3);
            }
        }
        else if (!power.Plugged && power.Percent <= 20 && _lastBattery > 20)
        {
            ShowTransient(Glyphs.Warning, "Low battery", power.Percent + "%", Red, 4);
        }

        _lastPlugged = power.Plugged;
        _lastBattery = power.Percent;
    }

    // ------------------------------------------------------------------
    // Lock and unlock
    // ------------------------------------------------------------------

    private void OnLocked()
    {
        _locked = true;
        SetMode(Mode.Compact);
        Pet.Sleeping = true;
        Playground.Pet.Sleeping = true;
    }

    private void OnUnlocked()
    {
        _locked = false;
        // The desktop takes a moment to reappear after unlocking; play the animation once it's visible.
        After(0.7, PlayUnlock);
    }

    /// <summary>Lock icon, the pet wakes up and stretches, then the lock pops open: "Welcome back".</summary>
    private void PlayUnlock()
    {
        _locked = false;
        bool pet = App.Settings.PetEnabled;

        if (pet)
        {
            Pet.WakeUp();
            Playground.Pet.Sleeping = false;
        }

        ShowTransient(Glyphs.Lock, "", "", White, 3, keepPet: pet);
        PopLeadGlyph();

        After(pet ? 1.3 : 0.6, () =>
        {
            ShowTransient(Glyphs.Unlock, pet ? "Welcome back" : "Unlocked", "", Green, 2.8, keepPet: pet);
            PopLeadGlyph();
        });
    }

    /// <summary>A quick springy pop on the compact icon.</summary>
    private void PopLeadGlyph()
    {
        var scale = new ScaleTransform(1, 1);
        LeadGlyph.RenderTransformOrigin = new Point(0.5, 0.5);
        LeadGlyph.RenderTransform = scale;
        var ease = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.8 };
        var pop = new DoubleAnimation(0.4, 1, TimeSpan.FromMilliseconds(380)) { EasingFunction = ease };
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, pop);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, pop);
    }

    /// <summary>Runs an action once, after a delay, on the UI thread.</summary>
    private void After(double seconds, Action action)
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(seconds) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            action();
        };
        timer.Start();
    }

    private void OnVolumeChanged(float volume, bool muted)
    {
        if (_mode == Mode.Expanded)
        {
            // The open island already shows volume on its own tab; no pop-up needed.
            if (_tab == Tab.Audio) UpdateAudioTab();
            return;
        }
        if (!App.Settings.VolumeHud) return;

        string trail = muted ? "Muted" : (int)Math.Round(volume * 100) + "%";
        ShowTransient(muted ? Glyphs.Mute : Glyphs.Volume, "Volume", trail, muted ? Red : White, 1.6);
    }

    // ------------------------------------------------------------------
    // Displays and fullscreen apps
    // ------------------------------------------------------------------

    /// <summary>
    /// Puts the island on its chosen display at its chosen spot, and keeps it off any display
    /// that a fullscreen app has taken over: it moves to a free display, or pauses if there is none,
    /// and goes back as soon as its own display is free again. Safe to call any time; it only moves
    /// the window when it is out of place.
    /// </summary>
    private void UpdatePlacement()
    {
        if (_hwnd == IntPtr.Zero || _movingWindow) return;
        AppSettings settings = App.Settings;

        // Which displays are taken by fullscreen apps? Track every one we have seen, not only the
        // focused one, so a game left running on one display still counts after you click away.
        var busy = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var busyBy = new Dictionary<string, IntPtr>(StringComparer.OrdinalIgnoreCase);
        if (settings.HideOnFullscreen)
        {
            IntPtr foreground = Native.ForegroundWindow();
            if (foreground != _hwnd && !_fullscreenWindows.Contains(foreground) && Native.IsFullscreen(foreground, out _))
            {
                _fullscreenWindows.Add(foreground);
            }

            for (int i = _fullscreenWindows.Count - 1; i >= 0; i--)
            {
                if (Native.IsFullscreen(_fullscreenWindows[i], out string name))
                {
                    busy.Add(name);
                    busyBy[name] = _fullscreenWindows[i];
                }
                else
                {
                    _fullscreenWindows.RemoveAt(i);
                }
            }
        }
        else
        {
            _fullscreenWindows.Clear();
        }

        List<Native.Monitor> monitors = Native.GetMonitors();
        if (monitors.Count == 0) return;

        Native.Monitor preferred =
            monitors.FirstOrDefault(m => m.Name.Equals(settings.DisplayName, StringComparison.OrdinalIgnoreCase))
            ?? monitors.FirstOrDefault(m => m.Primary)
            ?? monitors[0];

        Native.Monitor? target = preferred;
        if (busy.Contains(preferred.Name))
        {
            target = monitors.Where(m => !busy.Contains(m.Name))
                             .OrderByDescending(m => m.Primary)
                             .FirstOrDefault();
        }

        string chosen = settings.DisplayName.Length == 0 ? "the main display" : "your chosen display";
        if (target == null)
        {
            PlacementStatus = "Paused: every display has a fullscreen app (" +
                              string.Join(", ", busyBy.Values.Select(Native.DescribeWindow)) + ").";
            SetPaused(true);
            return;
        }

        if (target == preferred)
        {
            PlacementStatus = "Showing on " + target.Label + " (" + chosen + ").";
        }
        else
        {
            PlacementStatus = "Showing on " + target.Label + " because " + Native.DescribeWindow(busyBy[preferred.Name]) +
                              " is fullscreen on " + chosen + ".";
        }

        SetPaused(false);
        PlaceOn(target);
    }

    /// <summary>Moves the window so the pill sits at the configured spot on this display.</summary>
    private void PlaceOn(Native.Monitor monitor)
    {
        AppSettings settings = App.Settings;
        double px = monitor.Dpi / 96.0;          // pixels per WPF unit on this display
        double scale = RootScale.ScaleX;          // the user's size setting

        double windowWidth = Width * px;
        double windowHeight = Height * px;
        double gap = EdgeGap * scale * px;
        double halfPill = PillHeight / 2 * scale * px;
        double halfExpanded = (ExpandedWidth / 2 + EdgeGap) * scale * px;

        double centreX;
        double y;
        bool growUp = false;

        if (!settings.CustomPosition)
        {
            centreX = monitor.Left + monitor.Width / 2.0;
            y = monitor.Top;
        }
        else
        {
            centreX = monitor.Left + settings.AnchorX * monitor.Width;
            double centreY = monitor.Top + settings.AnchorY * monitor.Height;

            // In the lower half of the screen the island opens upwards so it never runs off the bottom.
            growUp = settings.AnchorY > 0.5;
            y = growUp ? centreY + halfPill + gap - windowHeight : centreY - halfPill - gap;
        }

        // Keep enough room on both sides for the open island, and the window on the display vertically.
        double minX = monitor.Left + halfExpanded;
        double maxX = monitor.Right - halfExpanded;
        centreX = minX <= maxX ? Math.Clamp(centreX, minX, maxX) : monitor.Left + monitor.Width / 2.0;
        y = Math.Clamp(y, monitor.Top, Math.Max(monitor.Top, monitor.Bottom - windowHeight));

        SetGrowDirection(growUp);

        int x = (int)Math.Round(centreX - windowWidth / 2);
        int top = (int)Math.Round(y);
        var (currentX, currentY) = Native.WindowPosition(_hwnd);
        if (Math.Abs(currentX - x) > 1 || Math.Abs(currentY - top) > 1) Native.MoveWindow(_hwnd, x, top);
    }

    /// <summary>Anchors the pill to the top of the window (opens downwards) or the bottom (opens upwards).</summary>
    private void SetGrowDirection(bool growUp)
    {
        if (_growUp == growUp && IsLoaded) return;
        _growUp = growUp;

        VerticalAlignment align = growUp ? VerticalAlignment.Bottom : VerticalAlignment.Top;
        Thickness margin = growUp ? new Thickness(0, 0, 0, EdgeGap) : new Thickness(0, EdgeGap, 0, 0);

        Pill.VerticalAlignment = align;
        Pill.Margin = margin;
        Pill.RenderTransformOrigin = new Point(0.5, growUp ? 1 : 0);
        Bubble.VerticalAlignment = align;
        Bubble.Margin = margin;
    }

    // ------------------------------------------------------------------
    // Appearance
    // ------------------------------------------------------------------

    private void ApplyAppearance()
    {
        AppSettings settings = App.Settings;

        // Size: scale everything, and grow the (transparent) window to match.
        double scale = Math.Clamp(settings.Scale, 0.7, 1.5);
        RootScale.ScaleX = scale;
        RootScale.ScaleY = scale;
        Width = BaseWindowWidth * scale;
        Height = BaseWindowHeight * scale;

        // Shape
        switch (settings.Shape)
        {
            case "Rounded":
                _compactRadius = 12;
                _expandedRadius = 22;
                break;
            case "Square":
                _compactRadius = 6;
                _expandedRadius = 10;
                break;
            default:
                _compactRadius = PillHeight / 2;
                _expandedRadius = 30;
                break;
        }
        Bubble.CornerRadius = new CornerRadius(Math.Min(_compactRadius, PillHeight / 2));

        // Colour
        Brush background = settings.Background switch
        {
            "Graphite" => MakeBrush("#FF1C1C1E"),
            "Midnight" => MakeBrush("#FF0B1A33"),
            "Glass" => MakeBrush("#D9101012"),
            _ => Brushes.Black
        };
        PillBg.Fill = background;
        Bubble.Background = background;

        ApplyAccent();

        // Re-pick the spot after the window has resized.
        Dispatcher.BeginInvoke(new Action(UpdatePlacement), DispatcherPriority.Background);
    }

    private void ApplyAccent()
    {
        _accent = App.Settings.Accent switch
        {
            "Blue" => Blue,
            "Green" => Green,
            "Orange" => Orange,
            "Purple" => MakeBrush("#FFBF5AF2"),
            "Album" => _artAccent ?? Pink,
            _ => Pink
        };

        foreach (Rectangle bar in _bars) bar.Fill = _accent;
        foreach (Rectangle bar in _bigBars) bar.Fill = _accent;
        Refresh();
    }

    /// <summary>A bright version of the album art's average colour, for the "match album art" accent.</summary>
    private static Brush? AccentFromArt(byte[]? bytes)
    {
        if (bytes == null || bytes.Length == 0) return null;
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.DecodePixelWidth = 16;
            image.StreamSource = new MemoryStream(bytes);
            image.EndInit();

            var pixels32 = new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
            int w = pixels32.PixelWidth, h = pixels32.PixelHeight;
            var data = new byte[w * h * 4];
            pixels32.CopyPixels(data, w * 4, 0);

            double r = 0, g = 0, b = 0, count = 0;
            for (int i = 0; i < data.Length; i += 4)
            {
                b += data[i];
                g += data[i + 1];
                r += data[i + 2];
                count++;
            }
            if (count == 0) return null;
            r /= count;
            g /= count;
            b /= count;

            // Stretch the colour so the brightest channel is near full: dull covers still give a visible accent.
            double max = Math.Max(r, Math.Max(g, b));
            if (max < 1) return null;
            double boost = 235 / max;
            var color = Color.FromRgb((byte)Math.Min(255, r * boost), (byte)Math.Min(255, g * boost), (byte)Math.Min(255, b * boost));
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            return brush;
        }
        catch
        {
            return null;
        }
    }

    private void SetPaused(bool paused)
    {
        if (_paused == paused) return;
        _paused = paused;

        Root.Visibility = paused ? Visibility.Hidden : Visibility.Visible;
        Pet.Paused = paused;
        Playground.Pet.Paused = paused;
        Playground.Paused = paused;
        if (paused && _mode == Mode.Expanded) SetMode(Mode.Compact);
        SyncVisualizer();
    }

    // ------------------------------------------------------------------
    // Pet
    // ------------------------------------------------------------------

    private void BuildPetMeters()
    {
        _happyBar = AddMeter("Happy", Pink);
        _fullBar = AddMeter("Food", Orange);
        _energyBar = AddMeter("Energy", Green);
    }

    private Rectangle AddMeter(string label, Brush color)
    {
        var name = new TextBlock
        {
            Text = label,
            FontSize = 11,
            Foreground = Dim,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 6, 0)
        };

        var track = new Border
        {
            Width = MeterWidth,
            Height = 5,
            CornerRadius = new CornerRadius(2.5),
            Background = Faint
        };
        var bar = new Rectangle
        {
            Width = 0,
            Height = 5,
            RadiusX = 2.5,
            RadiusY = 2.5,
            Fill = color,
            HorizontalAlignment = HorizontalAlignment.Left
        };
        var holder = new Grid
        {
            Width = MeterWidth,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 16, 0)
        };
        holder.Children.Add(track);
        holder.Children.Add(bar);

        PetMeters.Children.Add(name);
        PetMeters.Children.Add(holder);
        return bar;
    }

    private static void SetMeter(Rectangle bar, double value)
    {
        bar.Width = MeterWidth * Math.Clamp(value, 0, 100) / 100;
    }

    private void PetPet_Click(object sender, RoutedEventArgs e) => Playground.PetIt();

    private void PetFeed_Click(object sender, RoutedEventArgs e)
    {
        AppSettings settings = App.Settings;
        if (settings.PetFullness >= 97)
        {
            SayPet(settings.PetName + " is full");
            return;
        }
        if (Playground.Feed())
        {
            Playground.Pet.Dancing = false;
            SayPet("Here you go, " + settings.PetName);
        }
    }

    private void PetPlay_Click(object sender, RoutedEventArgs e)
    {
        AppSettings settings = App.Settings;
        if (settings.PetEnergy < 15)
        {
            SayPet(settings.PetName + " is too tired to play");
            return;
        }
        if (Playground.Play())
        {
            Playground.Pet.Dancing = false;
            SayPet(settings.PetName + " is chasing the ball");
        }
    }

    private void OnPetted()
    {
        AppSettings settings = App.Settings;
        settings.PetHappiness = Math.Min(100, settings.PetHappiness + 6);
        settings.Save();
        Refresh();
    }

    private void OnFed()
    {
        AppSettings settings = App.Settings;
        settings.PetFullness = Math.Min(100, settings.PetFullness + 25);
        settings.PetHappiness = Math.Min(100, settings.PetHappiness + 4);
        settings.Save();
        Playground.Pet.Dancing = _media.IsPlaying;
        Refresh();
    }

    private void OnPlayed()
    {
        AppSettings settings = App.Settings;
        settings.PetHappiness = Math.Min(100, settings.PetHappiness + 18);
        settings.PetEnergy = Math.Max(0, settings.PetEnergy - 15);
        settings.PetFullness = Math.Max(5, settings.PetFullness - 5);
        settings.Save();
        Playground.Pet.Dancing = _media.IsPlaying;
        Refresh();
    }

    /// <summary>Shows a short message in place of the pet's mood line.</summary>
    private void SayPet(string text)
    {
        _petNote = text;
        _petNoteUntil = DateTime.Now.AddSeconds(3);
        Refresh();
    }

    // ------------------------------------------------------------------
    // Now playing + visualizer
    // ------------------------------------------------------------------

    private void OnMediaChanged()
    {
        byte[]? bytes = _media.Thumbnail;
        if (!ReferenceEquals(bytes, _artBytes))
        {
            _artBytes = bytes;
            Brush art = MakeArtBrush(bytes);
            ArtSmall.Background = art;
            ArtBig.Background = art;
            _artAccent = AccentFromArt(bytes);
            if (App.Settings.Accent == "Album") ApplyAccent();
        }

        Pet.Dancing = _media.IsPlaying;
        Playground.Pet.Dancing = _media.IsPlaying && !Playground.Busy;
        SyncVisualizer();
        Refresh();
    }

    private static Brush MakeArtBrush(byte[]? bytes)
    {
        if (bytes == null || bytes.Length == 0) return MakeBrush("#33FFFFFF");
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.DecodePixelWidth = 128;
            image.StreamSource = new MemoryStream(bytes);
            image.EndInit();
            image.Freeze();
            return new ImageBrush(image) { Stretch = Stretch.UniformToFill };
        }
        catch
        {
            return MakeBrush("#33FFFFFF");
        }
    }

    /// <summary>Only listen to system audio while something is actually playing.</summary>
    private void SyncVisualizer()
    {
        bool playing = _media.IsPlaying && !_paused;

        if (playing && App.Settings.Visualizer) _audio.StartCapture();
        else _audio.StopCapture();

        if (playing) _vizTimer.Start();
        else
        {
            _vizTimer.Stop();
            for (int i = 0; i < _shown.Length; i++) _shown[i] = 0;
            DrawBars();
        }
    }

    private void OnVisualizerFrame()
    {
        _vizTime += 0.033;
        bool live = _audio.GetLevels(_levels);

        for (int i = 0; i < _shown.Length; i++)
        {
            // With no live capture (visualizer off, or silence) fall back to a gentle wave.
            double target = live ? _levels[i] : 0.3 + 0.22 * Math.Sin(_vizTime * 5 + i * 1.3);
            _shown[i] += (target - _shown[i]) * 0.45;
        }
        DrawBars();
    }

    private void DrawBars()
    {
        for (int i = 0; i < _bars.Length; i++)
        {
            _bars[i].Height = 3 + _shown[i] * 15;
            _bigBars[i].Height = 4 + _shown[i] * 28;
        }
    }

    private static Rectangle[] MakeBars(Panel host, double barWidth, double gap)
    {
        var bars = new Rectangle[AudioService.BarCount];
        for (int i = 0; i < bars.Length; i++)
        {
            bars[i] = new Rectangle
            {
                Width = barWidth,
                Height = 3,
                RadiusX = barWidth / 2,
                RadiusY = barWidth / 2,
                Fill = Pink,
                Margin = new Thickness(gap / 2, 0, gap / 2, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            host.Children.Add(bars[i]);
        }
        return bars;
    }

    private void Play_Click(object sender, RoutedEventArgs e) => _media.TogglePlay();

    private void Next_Click(object sender, RoutedEventArgs e) => _media.Next();

    private void Prev_Click(object sender, RoutedEventArgs e) => _media.Previous();

    // ------------------------------------------------------------------
    // Volume tab: one slider for everything, plus one per app that is playing
    // ------------------------------------------------------------------

    private void UpdateAudioTab()
    {
        if (Mouse.Captured != null) return; // a slider is being dragged; don't fight it

        _audioUpdating = true;
        try
        {
            double master = _audio.MasterVolume * 100;
            MasterSlider.Value = master;
            MasterText.Text = (int)Math.Round(master) + "%";

            bool masterMuted = _audio.SpeakerMuted;
            MasterMuteBtn.Content = masterMuted ? Glyphs.Mute : Glyphs.Volume;
            MasterMuteBtn.Background = masterMuted ? Red : Faint;

            List<AudioApp> apps = _audio.GetApps();

            // Rebuild the rows only when the set of apps changes.
            string key = string.Join("|", apps.Select(app => app.Name));
            if (key != _audioAppsKey)
            {
                _audioAppsKey = key;
                _audioRows.Clear();
                AudioApps.Children.Clear();
                foreach (AudioApp app in apps) AddAudioRow(app.Name);
            }

            foreach (AudioApp app in apps)
            {
                if (!_audioRows.TryGetValue(app.Name, out AudioRow? row)) continue;
                double percent = app.Volume * 100;
                row.Slider.Value = percent;
                row.Percent.Text = (int)Math.Round(percent) + "%";
                row.Muted = app.Muted;
                row.Mute.Content = app.Muted ? Glyphs.Mute : Glyphs.Volume;
                row.Mute.Background = app.Muted ? Red : Faint;
            }

            AudioEmpty.Visibility = apps.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        finally
        {
            _audioUpdating = false;
        }
    }

    private void AddAudioRow(string appName)
    {
        var row = new AudioRow();

        var grid = new Grid { Margin = new Thickness(0, 0, 0, 8) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(104) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(42) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var name = new TextBlock
        {
            Text = appName,
            FontSize = 12.5,
            Foreground = White,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(0, 0, 8, 0)
        };
        grid.Children.Add(name);

        row.Slider.Style = (Style)FindResource("IslandSlider");
        row.Slider.ValueChanged += (_, e) =>
        {
            if (_audioUpdating) return;
            _audio.SetAppVolume(appName, (float)(e.NewValue / 100));
            row.Percent.Text = (int)Math.Round(e.NewValue) + "%";
        };
        Grid.SetColumn(row.Slider, 1);
        grid.Children.Add(row.Slider);

        row.Percent.FontSize = 12;
        row.Percent.Foreground = Dim;
        row.Percent.TextAlignment = TextAlignment.Right;
        row.Percent.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(row.Percent, 2);
        grid.Children.Add(row.Percent);

        row.Mute.Style = (Style)FindResource("IconBtn");
        row.Mute.Width = 28;
        row.Mute.Height = 28;
        row.Mute.FontSize = 12;
        row.Mute.Margin = new Thickness(8, 0, 0, 0);
        row.Mute.Content = Glyphs.Volume;
        row.Mute.ToolTip = "Mute or unmute " + appName;
        row.Mute.Click += (_, _) =>
        {
            _audio.SetAppMuted(appName, !row.Muted);
            UpdateAudioTab();
        };
        Grid.SetColumn(row.Mute, 3);
        grid.Children.Add(row.Mute);

        _audioRows[appName] = row;
        AudioApps.Children.Add(grid);
    }

    private void MasterSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_audioUpdating || !IsLoaded) return;
        _audio.MasterVolume = (float)(e.NewValue / 100);
        MasterText.Text = (int)Math.Round(e.NewValue) + "%";
    }

    private void MasterMute_Click(object sender, RoutedEventArgs e)
    {
        _audio.ToggleSpeaker();
        UpdateAudioTab();
    }

    // ------------------------------------------------------------------
    // Call / meeting controls
    // ------------------------------------------------------------------

    private void MicBtn_Click(object sender, RoutedEventArgs e)
    {
        _audio.ToggleMic();
        _micMuted = _audio.MicMuted;
        Refresh();
    }

    private void SpeakerBtn_Click(object sender, RoutedEventArgs e)
    {
        _audio.ToggleSpeaker();
        Refresh();
    }

    private void ShowAppBtn_Click(object sender, RoutedEventArgs e)
    {
        string? name = _call.ProcessName;
        if (name == null) return;
        try
        {
            foreach (Process process in Process.GetProcessesByName(name))
            {
                if (process.MainWindowHandle != IntPtr.Zero)
                {
                    Native.BringToFront(process.MainWindowHandle);
                    break;
                }
            }
        }
        catch
        {
            // the app closed between the poll and the click
        }
        SetMode(Mode.Compact);
    }

    // ------------------------------------------------------------------
    // Timer
    // ------------------------------------------------------------------

    private void TimerStart_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button button && button.Tag is string text && int.TryParse(text, out int minutes))
        {
            _timerEnd = DateTime.Now.AddMinutes(minutes);
            Refresh();
        }
    }

    private void TimerCancel_Click(object sender, RoutedEventArgs e)
    {
        _timerEnd = null;
        Refresh();
    }

    // ------------------------------------------------------------------
    // File shelf
    // ------------------------------------------------------------------

    private void Pill_DragEnter(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        _collapseTimer.Stop();
        if (_mode == Mode.Compact || _tab != Tab.Shelf)
        {
            ShowTab(Tab.Shelf);
            SetMode(Mode.Expanded);
        }
        e.Effects = DragDropEffects.Copy;
        e.Handled = true;
    }

    private void Pill_DragOver(object sender, DragEventArgs e)
    {
        _collapseTimer.Stop();
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void Pill_DragLeave(object sender, DragEventArgs e)
    {
        if (_dragging) return;
        _collapseTimer.Stop();
        _collapseTimer.Start();
    }

    private void Pill_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] paths)
        {
            foreach (string path in paths)
            {
                if (_shelf.Any(item => string.Equals(item.FullPath, path, StringComparison.OrdinalIgnoreCase))) continue;
                _shelf.Add(new ShelfItem(path));
            }
            SaveShelf();
        }
        e.Handled = true;
        Refresh();
    }

    private void ShelfItem_Down(object sender, MouseButtonEventArgs e)
    {
        _shelfPressed = true;
        _shelfDragStart = e.GetPosition(this);
    }

    private void ShelfItem_Move(object sender, MouseEventArgs e)
    {
        if (!_shelfPressed || e.LeftButton != MouseButtonState.Pressed) return;
        if (sender is not FrameworkElement element || element.Tag is not ShelfItem item) return;

        Point now = e.GetPosition(this);
        if (Math.Abs(now.X - _shelfDragStart.X) < 6 && Math.Abs(now.Y - _shelfDragStart.Y) < 6) return;

        // Hand the file to Windows as a normal file drag, so any app or folder accepts it.
        _shelfPressed = false;
        _dragging = true;
        try
        {
            var data = new DataObject(DataFormats.FileDrop, new[] { item.FullPath });
            DragDrop.DoDragDrop(element, data, DragDropEffects.Copy | DragDropEffects.Link);
        }
        catch
        {
            // drag cancelled or the file vanished
        }
        finally
        {
            _dragging = false;
            if (!Pill.IsMouseOver)
            {
                _collapseTimer.Stop();
                _collapseTimer.Start();
            }
        }
    }

    private void ShelfItem_Up(object sender, MouseButtonEventArgs e)
    {
        bool click = _shelfPressed;
        _shelfPressed = false;
        if (!click) return;
        if (sender is not FrameworkElement element || element.Tag is not ShelfItem item) return;

        try
        {
            Process.Start(new ProcessStartInfo(item.FullPath) { UseShellExecute = true });
        }
        catch
        {
            // file was moved or deleted since it was shelved
        }
    }

    private void ShelfRemove_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement element && element.Tag is ShelfItem item)
        {
            _shelf.Remove(item);
            SaveShelf();
            Refresh();
        }
    }

    private void ShelfClear_Click(object sender, RoutedEventArgs e)
    {
        _shelf.Clear();
        SaveShelf();
        Refresh();
    }

    private void SaveShelf()
    {
        App.Settings.Shelf = _shelf.Select(item => item.FullPath).ToList();
        App.Settings.Save();
    }

    // ------------------------------------------------------------------
    // Clipboard history
    // ------------------------------------------------------------------

    private IntPtr WndProc(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == Native.WM_CLIPBOARDUPDATE) OnClipboardChanged();
        if (message == Native.WM_WTSSESSION_CHANGE)
        {
            int reason = wParam.ToInt32();
            if (reason == Native.WTS_SESSION_LOCK) OnLocked();
            else if (reason == Native.WTS_SESSION_UNLOCK) OnUnlocked();
        }
        return IntPtr.Zero;
    }

    private void OnClipboardChanged()
    {
        try
        {
            if (!App.Settings.ClipboardEnabled) return;

            // Password managers set this flag to ask clipboard tools not to record the copy.
            if (Clipboard.ContainsData("ExcludeClipboardContentFromMonitorProcessing")) return;
            if (!Clipboard.ContainsText()) return;

            string text = Clipboard.GetText();
            if (string.IsNullOrWhiteSpace(text)) return;

            ClipItem? existing = _clips.FirstOrDefault(clip => clip.Text == text);
            if (existing != null) _clips.Remove(existing);

            _clips.Insert(0, new ClipItem(text));
            while (_clips.Count > 25) _clips.RemoveAt(_clips.Count - 1);

            if (_mode == Mode.Expanded) Refresh();
        }
        catch
        {
            // another app is holding the clipboard open; skip this change
        }
    }

    private void ClipItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement element || element.Tag is not ClipItem item) return;
        try
        {
            Clipboard.SetText(item.Text);
        }
        catch
        {
            return; // clipboard busy
        }
        SetMode(Mode.Compact);
        ShowTransient(Glyphs.Copy, "Copied", "", White, 1.5);
    }

    private void ClipClear_Click(object sender, RoutedEventArgs e)
    {
        _clips.Clear();
        Refresh();
    }

    // ------------------------------------------------------------------
    // System stats
    // ------------------------------------------------------------------

    private void BuildStatRows()
    {
        _cpuRow = AddStatRow("CPU");
        _ramRow = AddStatRow("Memory");
        _gpuRow = AddStatRow("GPU");
        _batteryRow = AddStatRow("Battery");
        _gpuRow.Row.Visibility = Visibility.Collapsed;
    }

    private StatRow AddStatRow(string label)
    {
        var row = new StatRow();
        row.Row.Margin = new Thickness(0, 4, 0, 4);
        row.Row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(64) });
        row.Row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(StatBarWidth) });
        row.Row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var name = new TextBlock
        {
            Text = label,
            FontSize = 12,
            Foreground = Dim,
            VerticalAlignment = VerticalAlignment.Center
        };
        row.Row.Children.Add(name);

        var track = new Border
        {
            Height = 6,
            CornerRadius = new CornerRadius(3),
            Background = Faint,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(track, 1);
        row.Row.Children.Add(track);

        row.Bar.Height = 6;
        row.Bar.Width = 0;
        row.Bar.RadiusX = 3;
        row.Bar.RadiusY = 3;
        row.Bar.Fill = Green;
        row.Bar.HorizontalAlignment = HorizontalAlignment.Left;
        row.Bar.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(row.Bar, 1);
        row.Row.Children.Add(row.Bar);

        row.Value.FontSize = 12;
        row.Value.Foreground = White;
        row.Value.HorizontalAlignment = HorizontalAlignment.Right;
        row.Value.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(row.Value, 2);
        row.Row.Children.Add(row.Value);

        StatsRows.Children.Add(row.Row);
        return row;
    }

    private static void SetStat(StatRow row, double percent, string text, bool lowIsBad)
    {
        percent = Math.Clamp(percent, 0, 100);
        row.Bar.Width = StatBarWidth * percent / 100;
        row.Value.Text = text;

        double load = lowIsBad ? 100 - percent : percent;
        row.Bar.Fill = load < 60 ? Green : load < 85 ? Orange : Red;
    }

    private void UpdateStats()
    {
        _stats.Sample();

        SetStat(_cpuRow, _stats.Cpu, _stats.Cpu.ToString("0") + "%", false);
        SetStat(_ramRow, _stats.RamPercent,
            _stats.RamUsedGb.ToString("0.0") + " / " + _stats.RamTotalGb.ToString("0") + " GB", false);

        _gpuRow.Row.Visibility = _stats.HasGpu ? Visibility.Visible : Visibility.Collapsed;
        if (_stats.HasGpu)
        {
            SetStat(_gpuRow, _stats.GpuUsage,
                _stats.GpuUsage.ToString("0") + "% · " + _stats.GpuTemp.ToString("0") + "°C", false);
        }

        var power = SystemStats.Power();
        _batteryRow.Row.Visibility = power.HasBattery ? Visibility.Visible : Visibility.Collapsed;
        if (power.HasBattery)
        {
            SetStat(_batteryRow, power.Percent, power.Percent + "%" + (power.Plugged ? " · charging" : ""), true);
        }

        NetText.Text = "Network   ↓ " + SystemStats.FormatSpeed(_stats.DownBytesPerSec) +
                       "    ↑ " + SystemStats.FormatSpeed(_stats.UpBytesPerSec);

        _ = _stats.UpdateGpuAsync();
    }

    private static Brush MakeBrush(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }
}
