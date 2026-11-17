namespace DynamicIsland.Core;

/// <summary>One entry in the clipboard history.</summary>
public sealed class ClipItem
{
    public ClipItem(string text)
    {
        Text = text;
        string oneLine = text.Replace("\r", " ").Replace("\n", " ").Trim();
        Preview = oneLine.Length > 120 ? oneLine.Substring(0, 120) + "…" : oneLine;
    }

    public string Text { get; }
    public string Preview { get; }
}

/// <summary>One file or folder parked on the shelf.</summary>
public sealed class ShelfItem
{
    public ShelfItem(string fullPath)
    {
        FullPath = fullPath;
        string name = System.IO.Path.GetFileName(fullPath.TrimEnd('\\', '/'));
        Name = string.IsNullOrEmpty(name) ? fullPath : name;
        Glyph = Directory.Exists(fullPath) ? Glyphs.Folder : Glyphs.Document;
    }

    public string FullPath { get; }
    public string Name { get; }
    public string Glyph { get; }
}

/// <summary>Icon codes from the Segoe Fluent Icons / Segoe MDL2 Assets fonts.</summary>
public static class Glyphs
{
    public const string Play = "";
    public const string Pause = "";
    public const string Next = "";
    public const string Previous = "";
    public const string Volume = "";
    public const string Mute = "";
    public const string Mic = "";
    public const string MicOff = "";
    public const string Phone = "";
    public const string Bluetooth = "";
    public const string Bolt = "";
    public const string Unlock = "";
    public const string Copy = "";
    public const string Stopwatch = "";
    public const string Music = "";
    public const string Folder = "";
    public const string Document = "";
    public const string Warning = "";
}
