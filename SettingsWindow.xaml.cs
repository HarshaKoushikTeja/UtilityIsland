using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DynamicIsland.Core;
using DynamicIsland.Pets;

namespace DynamicIsland;

/// <summary>
/// The control panel: pet, feature switches, a page of buttons that trigger every
/// island state for testing, and help. Changes apply as soon as they are made.
/// </summary>
public partial class SettingsWindow : Window
{
    private static readonly Brush NavSelected = new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF));

    private readonly MainWindow _island;
    private bool _loading = true;
    private PetKind _selected;

    public SettingsWindow(MainWindow island)
    {
        InitializeComponent();
        _island = island;

        AppSettings settings = App.Settings;
        _selected = PetControl.Parse(settings.PetKind);

        foreach (PetKind kind in Enum.GetValues<PetKind>())
        {
            PetChoices.Children.Add(MakeChoice(kind));
        }

        LoadControls();

        Version? version = typeof(App).Assembly.GetName().Version;
        VersionText.Text = version == null ? "" : "Version " + version.ToString(3);

        ShowPage("Pet");
        _loading = false;
    }

    /// <summary>Puts every control in step with the saved settings.</summary>
    private void LoadControls()
    {
        bool wasLoading = _loading;
        _loading = true;

        AppSettings settings = App.Settings;
        PetNameBox.Text = settings.PetName;
        PetEnabledBox.IsChecked = settings.PetEnabled;
        VisualizerBox.IsChecked = settings.Visualizer;
        VolumeBox.IsChecked = settings.VolumeHud;
        CallBox.IsChecked = settings.CallDetection;
        ClipboardBox.IsChecked = settings.ClipboardEnabled;
        CaptureBox.IsChecked = settings.HideFromCapture;
        FullscreenBox.IsChecked = settings.HideOnFullscreen;
        StartupBox.IsChecked = AppSettings.StartsWithWindows;
        HoverBox.IsChecked = settings.OpenOnHover;
        ShelfTabBox.IsChecked = settings.ShowShelfTab;
        StatsTabBox.IsChecked = settings.ShowStatsTab;

        // Displays
        DisplayBox.Items.Clear();
        DisplayBox.Items.Add(new ComboBoxItem { Content = "Main display", Tag = "" });
        foreach (Native.Monitor monitor in Native.GetMonitors().OrderBy(m => m.Name))
        {
            DisplayBox.Items.Add(new ComboBoxItem { Content = monitor.Label, Tag = monitor.Name });
        }
        Select(DisplayBox, settings.DisplayName);
        if (DisplayBox.SelectedIndex < 0) DisplayBox.SelectedIndex = 0;

        PositionText.Text = settings.CustomPosition ? "Custom (dragged)" : "Top centre";
        SizeSlider.Value = Math.Round(settings.Scale * 100);
        SizeText.Text = (int)SizeSlider.Value + "%";
        WidthSlider.Value = settings.IdleWidth;
        WidthText.Text = (int)WidthSlider.Value + " px";
        Select(ShapeBox, settings.Shape);
        Select(BackgroundBox, settings.Background);
        Select(AccentBox, settings.Accent);
        LockBox.IsChecked = settings.LockPosition;

        _loading = wasLoading;
    }

    private static void Select(ComboBox box, string tag)
    {
        foreach (object item in box.Items)
        {
            if (item is ComboBoxItem entry && string.Equals(entry.Tag as string, tag, StringComparison.OrdinalIgnoreCase))
            {
                box.SelectedItem = entry;
                return;
            }
        }
        if (box.Items.Count > 0) box.SelectedIndex = 0;
    }

    private static string Selected(ComboBox box, string fallback)
    {
        return (box.SelectedItem as ComboBoxItem)?.Tag as string ?? fallback;
    }

    /// <summary>Called by the island after it was dragged, so the Appearance page shows the new spot.</summary>
    public void RefreshFromSettings() => LoadControls();

    /// <summary>Jumps to a page by name: Pet, Look, Island, Test or About.</summary>
    public void ShowPage(string page)
    {
        PagePet.Visibility = page == "Pet" ? Visibility.Visible : Visibility.Collapsed;
        PageLook.Visibility = page == "Look" ? Visibility.Visible : Visibility.Collapsed;
        PageIsland.Visibility = page == "Island" ? Visibility.Visible : Visibility.Collapsed;
        PageTest.Visibility = page == "Test" ? Visibility.Visible : Visibility.Collapsed;
        PageAbout.Visibility = page == "About" ? Visibility.Visible : Visibility.Collapsed;

        NavPet.Background = page == "Pet" ? NavSelected : Brushes.Transparent;
        NavLook.Background = page == "Look" ? NavSelected : Brushes.Transparent;
        NavIsland.Background = page == "Island" ? NavSelected : Brushes.Transparent;
        NavTest.Background = page == "Test" ? NavSelected : Brushes.Transparent;
        NavAbout.Background = page == "About" ? NavSelected : Brushes.Transparent;
    }

    private RadioButton MakeChoice(PetKind kind)
    {
        // A dark tile so every pet (including the white bunny) is visible.
        var preview = new Border
        {
            Width = 72,
            Height = 64,
            CornerRadius = new CornerRadius(14),
            Background = Brushes.Black,
            Child = new Viewbox
            {
                Width = 52,
                Height = 48,
                Child = new PetControl { Kind = kind, Roam = false }
            }
        };

        var label = new TextBlock
        {
            Text = kind.ToString(),
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 6, 0, 0)
        };

        var panel = new StackPanel();
        panel.Children.Add(preview);
        panel.Children.Add(label);

        var choice = new RadioButton
        {
            GroupName = "Pet",
            Content = panel,
            IsChecked = kind == _selected,
            Margin = new Thickness(0, 0, 14, 0),
            VerticalContentAlignment = VerticalAlignment.Center
        };
        choice.Checked += (_, _) =>
        {
            _selected = kind;
            Apply();
        };
        return choice;
    }

    /// <summary>Writes every control back to settings and tells the island to pick them up.</summary>
    private void Apply()
    {
        if (_loading) return;

        AppSettings settings = App.Settings;

        string name = PetNameBox.Text.Trim();
        settings.PetName = name.Length == 0 ? "Mochi" : name;
        settings.PetKind = _selected.ToString();
        settings.PetEnabled = PetEnabledBox.IsChecked == true;
        settings.Visualizer = VisualizerBox.IsChecked == true;
        settings.VolumeHud = VolumeBox.IsChecked == true;
        settings.CallDetection = CallBox.IsChecked == true;
        settings.ClipboardEnabled = ClipboardBox.IsChecked == true;
        settings.HideFromCapture = CaptureBox.IsChecked == true;
        settings.HideOnFullscreen = FullscreenBox.IsChecked == true;
        settings.OpenOnHover = HoverBox.IsChecked == true;
        settings.ShowShelfTab = ShelfTabBox.IsChecked == true;
        settings.ShowStatsTab = StatsTabBox.IsChecked == true;

        settings.DisplayName = Selected(DisplayBox, "");
        settings.Scale = SizeSlider.Value / 100;
        settings.IdleWidth = WidthSlider.Value;
        settings.Shape = Selected(ShapeBox, "Pill");
        settings.Background = Selected(BackgroundBox, "Black");
        settings.Accent = Selected(AccentBox, "Pink");
        settings.LockPosition = LockBox.IsChecked == true;
        SizeText.Text = (int)SizeSlider.Value + "%";
        WidthText.Text = (int)WidthSlider.Value + " px";

        settings.Save();

        bool startup = StartupBox.IsChecked == true;
        if (AppSettings.StartsWithWindows != startup) AppSettings.StartsWithWindows = startup;

        _island.ReloadSettings();
    }

    private void Toggle_Changed(object sender, RoutedEventArgs e) => Apply();

    private void Choice_Changed(object sender, SelectionChangedEventArgs e) => Apply();

    private void Slider_Changed(object sender, RoutedPropertyChangedEventArgs<double> e) => Apply();

    private void ResetPosition_Click(object sender, RoutedEventArgs e)
    {
        AppSettings settings = App.Settings;
        settings.CustomPosition = false;
        settings.AnchorX = 0.5;
        settings.AnchorY = 0;
        settings.Save();
        LoadControls();
        _island.ReloadSettings();
    }

    private void ResetLook_Click(object sender, RoutedEventArgs e)
    {
        App.Settings.ResetAppearance();
        App.Settings.Save();
        LoadControls();
        _island.ReloadSettings();
    }

    private void Name_Changed(object sender, TextChangedEventArgs e) => Apply();

    private void Nav_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button button && button.Tag is string page) ShowPage(page);
    }

    private void Test_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button button && button.Tag is string test) _island.RunTest(test);
    }

    private void DataFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(AppSettings.Folder);
            Process.Start(new ProcessStartInfo(AppSettings.Folder) { UseShellExecute = true });
        }
        catch
        {
            // nothing to show if Explorer won't open
        }
    }

    private void Quit_Click(object sender, RoutedEventArgs e)
    {
        Application.Current.Shutdown();
    }
}
