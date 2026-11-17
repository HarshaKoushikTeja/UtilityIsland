using System.Text.Json;
using Microsoft.Win32;

namespace DynamicIsland.Core;

/// <summary>User settings, stored as JSON in %AppData%\DynamicIslandWin.</summary>
public sealed class AppSettings
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunName = "DynamicIslandWin";

    public string PetKind { get; set; } = "Cat";
    public string PetName { get; set; } = "Mochi";
    public bool PetEnabled { get; set; } = true;
    public bool Visualizer { get; set; } = true;
    public bool HideOnFullscreen { get; set; } = true;
    public bool ClipboardEnabled { get; set; } = true;
    public bool CallDetection { get; set; } = true;
    public bool VolumeHud { get; set; } = true;
    public bool HideFromCapture { get; set; }
    public bool FirstRunDone { get; set; }
    public List<string> Shelf { get; set; } = new();

    // Pet wellbeing, 0-100. These only change how the pet looks and what it says.
    public double PetHappiness { get; set; } = 70;
    public double PetFullness { get; set; } = 70;
    public double PetEnergy { get; set; } = 80;
    public DateTime PetStatsAt { get; set; } = DateTime.UtcNow;

    /// <summary>Lets time pass for the pet: it slowly gets hungry and bored, and rests up.</summary>
    public void AgePet()
    {
        DateTime now = DateTime.UtcNow;
        double hours = Math.Clamp((now - PetStatsAt).TotalHours, 0, 48);
        if (hours < 1.0 / 60) return;

        PetStatsAt = now;
        PetFullness = Math.Clamp(PetFullness - 18 * hours, 5, 100);
        PetHappiness = Math.Clamp(PetHappiness - 10 * hours, 5, 100);
        PetEnergy = Math.Clamp(PetEnergy + 25 * hours, 0, 100);
    }

    public string PetMood()
    {
        if (PetFullness < 25) return "is hungry";
        if (PetEnergy < 20) return "is worn out";
        if (PetHappiness < 30) return "wants some attention";
        if (PetHappiness > 75) return "is very happy";
        return "is content";
    }

    public static string Folder =>
        System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DynamicIslandWin");

    private static string FilePath => System.IO.Path.Combine(Folder, "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
            }
        }
        catch
        {
            // fall through to defaults on a corrupt file
        }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Folder);
            var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(FilePath, json);
        }
        catch
        {
            // settings are best-effort
        }
    }

    public static bool StartsWithWindows
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKey);
                return key?.GetValue(RunName) != null;
            }
            catch
            {
                return false;
            }
        }
        set
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
                if (key == null) return;
                if (value && Environment.ProcessPath != null)
                {
                    key.SetValue(RunName, "\"" + Environment.ProcessPath + "\"");
                }
                else if (!value)
                {
                    key.DeleteValue(RunName, throwOnMissingValue: false);
                }
            }
            catch
            {
                // ignore: the checkbox simply won't stick
            }
        }
    }
}
