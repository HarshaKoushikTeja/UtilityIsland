using System.Windows;
using DynamicIsland.Core;

namespace DynamicIsland;

public partial class App : Application
{
    private static Mutex? _mutex;
    private System.Windows.Forms.NotifyIcon? _tray;

    public static AppSettings Settings { get; private set; } = new();

    protected override void OnStartup(StartupEventArgs e)
    {
        // Only one island at a time.
        _mutex = new Mutex(true, "DynamicIslandWin.SingleInstance", out bool isNew);
        if (!isNew)
        {
            Shutdown();
            return;
        }

        Settings = AppSettings.Load();

        // A background utility should survive a bad frame instead of vanishing.
        DispatcherUnhandledException += (_, args) =>
        {
            try
            {
                var log = System.IO.Path.Combine(AppSettings.Folder, "error.log");
                Directory.CreateDirectory(AppSettings.Folder);
                File.AppendAllText(log, DateTime.Now + "  " + args.Exception + Environment.NewLine);
            }
            catch
            {
                // nothing useful to do if logging fails
            }
            args.Handled = true;
        };

        base.OnStartup(e);

        var island = new MainWindow();
        island.Show();
        CreateTrayIcon(island);
    }

    /// <summary>An icon near the clock, so the app is easy to find, configure and quit.</summary>
    private void CreateTrayIcon(MainWindow island)
    {
        try
        {
            var menu = new System.Windows.Forms.ContextMenuStrip();
            menu.Items.Add("Open control panel", null, (_, _) => island.OpenControlPanel());
            menu.Items.Add("Try it out", null, (_, _) => island.OpenControlPanel("Test"));
            menu.Items.Add("Quit", null, (_, _) => Shutdown());

            _tray = new System.Windows.Forms.NotifyIcon
            {
                Icon = MakeTrayIcon(),
                Text = "Dynamic Island",
                ContextMenuStrip = menu,
                Visible = true
            };
            _tray.MouseClick += (_, args) =>
            {
                if (args.Button == System.Windows.Forms.MouseButtons.Left) island.OpenControlPanel();
            };
        }
        catch
        {
            _tray = null; // the gear inside the island still opens the control panel
        }
    }

    /// <summary>Draws a small pill so no icon file has to ship with the app.</summary>
    private static System.Drawing.Icon MakeTrayIcon()
    {
        try
        {
            using var bitmap = new System.Drawing.Bitmap(32, 32);
            using (var graphics = System.Drawing.Graphics.FromImage(bitmap))
            using (var fill = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(240, 240, 240)))
            {
                graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                graphics.Clear(System.Drawing.Color.Transparent);
                graphics.FillEllipse(fill, 1, 10, 12, 12);
                graphics.FillEllipse(fill, 19, 10, 12, 12);
                graphics.FillRectangle(fill, 7, 10, 18, 12);
            }
            return System.Drawing.Icon.FromHandle(bitmap.GetHicon());
        }
        catch
        {
            return System.Drawing.SystemIcons.Application;
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_tray != null)
        {
            _tray.Visible = false;
            _tray.Dispose();
        }
        base.OnExit(e);
    }
}
