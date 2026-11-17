using Microsoft.Win32;

namespace DynamicIsland.Services;

/// <summary>What is using the microphone and camera right now.</summary>
internal sealed class CallState
{
    public bool MicInUse;
    public bool CameraInUse;
    public string AppName = "";
    public string? ProcessName; // null for Store apps, where we can't map back to a process
}

/// <summary>
/// Detects calls by asking Windows which app currently has the microphone or
/// camera open. This is the same data behind the mic icon in the taskbar, so it
/// covers Teams, Zoom, Discord, and Meet running in a browser alike.
/// </summary>
internal static class CallService
{
    private const string StorePath =
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\";

    public static CallState Read()
    {
        var state = new CallState();
        var mic = FindActive("microphone");
        var cam = FindActive("webcam");

        state.MicInUse = mic != null;
        state.CameraInUse = cam != null;

        var source = mic ?? cam;
        if (source != null)
        {
            state.AppName = source.Value.Display;
            state.ProcessName = source.Value.Process;
        }
        return state;
    }

    private static (string Display, string? Process)? FindActive(string capability)
    {
        try
        {
            using var root = Registry.CurrentUser.OpenSubKey(StorePath + capability);
            if (root == null) return null;

            foreach (string name in root.GetSubKeyNames())
            {
                if (name == "NonPackaged")
                {
                    // Desktop apps: key names are exe paths with '#' in place of '\'.
                    using var desktop = root.OpenSubKey(name);
                    if (desktop == null) continue;
                    foreach (string app in desktop.GetSubKeyNames())
                    {
                        using var key = desktop.OpenSubKey(app);
                        if (!InUse(key)) continue;
                        string exe = app.Split('#').Last();
                        if (exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                        {
                            exe = exe.Substring(0, exe.Length - 4);
                        }
                        return (Pretty(exe), exe);
                    }
                }
                else
                {
                    // Store apps: key names are package family names.
                    using var key = root.OpenSubKey(name);
                    if (!InUse(key)) continue;
                    string family = name.Split('_')[0];
                    return (Pretty(family.Split('.').Last()), null);
                }
            }
        }
        catch
        {
            // registry layout differs or access denied: report nothing in use
        }
        return null;
    }

    private static bool InUse(RegistryKey? key)
    {
        if (key == null) return false;
        object? start = key.GetValue("LastUsedTimeStart");
        object? stop = key.GetValue("LastUsedTimeStop");
        if (start == null || stop == null) return false;
        // Windows writes a stop time of 0 while the device is still open.
        return Convert.ToInt64(stop) == 0 && Convert.ToInt64(start) != 0;
    }

    private static string Pretty(string raw)
    {
        string lower = raw.ToLowerInvariant();
        if (lower.Contains("teams")) return "Teams";
        if (lower.Contains("zoom")) return "Zoom";
        if (lower.Contains("discord")) return "Discord";
        if (lower.Contains("slack")) return "Slack";
        if (lower.Contains("skype")) return "Skype";
        if (lower.Contains("webex")) return "Webex";
        if (lower == "chrome") return "Chrome";
        if (lower == "msedge") return "Edge";
        if (lower == "firefox") return "Firefox";
        if (lower == "brave") return "Brave";
        if (raw.Length == 0) return "Call";
        return char.ToUpperInvariant(raw[0]) + raw.Substring(1);
    }
}
