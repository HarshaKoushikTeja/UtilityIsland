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

        PetNameBox.Text = settings.PetName;
        PetEnabledBox.IsChecked = settings.PetEnabled;
        VisualizerBox.IsChecked = settings.Visualizer;
        VolumeBox.IsChecked = settings.VolumeHud;
        CallBox.IsChecked = settings.CallDetection;
        ClipboardBox.IsChecked = settings.ClipboardEnabled;
        CaptureBox.IsChecked = settings.HideFromCapture;
        FullscreenBox.IsChecked = settings.HideOnFullscreen;
        StartupBox.IsChecked = AppSettings.StartsWithWindows;

        Version? version = typeof(App).Assembly.GetName().Version;
        VersionText.Text = version == null ? "" : "Version " + version.ToString(3);

        ShowPage("Pet");
        _loading = false;
    }

    /// <summary>Jumps to a page by name: Pet, Island, Test or About.</summary>
    public void ShowPage(string page)
    {
        PagePet.Visibility = page == "Pet" ? Visibility.Visible : Visibility.Collapsed;
        PageIsland.Visibility = page == "Island" ? Visibility.Visible : Visibility.Collapsed;
        PageTest.Visibility = page == "Test" ? Visibility.Visible : Visibility.Collapsed;
        PageAbout.Visibility = page == "About" ? Visibility.Visible : Visibility.Collapsed;

        NavPet.Background = page == "Pet" ? NavSelected : Brushes.Transparent;
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
        settings.Save();

        bool startup = StartupBox.IsChecked == true;
        if (AppSettings.StartsWithWindows != startup) AppSettings.StartsWithWindows = startup;

        _island.ReloadSettings();
    }

    private void Toggle_Changed(object sender, RoutedEventArgs e) => Apply();

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
