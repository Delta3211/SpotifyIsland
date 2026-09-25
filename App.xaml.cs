using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Forms;
using Microsoft.Win32;

namespace SpotifyIsland;

/// <summary>
/// Tray-first application host.
/// SpotifyIsland runs silently in the background and activates automatically
/// when Spotify opens or is minimized, just like the Apple Dynamic Island.
/// </summary>
public partial class App : System.Windows.Application
{
    private NotifyIcon? _trayIcon;
    private MainWindow? _mainWindow;
    private ToolStripMenuItem? _startupMenuItem;
    private static System.Threading.Mutex? _mutex;
    private static System.Threading.EventWaitHandle? _showEvent;
    private static System.Threading.RegisteredWaitHandle? _showEventRegistration;

    protected override void OnStartup(StartupEventArgs e)
    {
        // This namespace change lets the repaired app start when an older,
        // invisible build is still holding the original single-instance lock.
        const string mutexName = @"Local\SpotifyIsland_SingleInstance_Mutex_v2";
        const string eventName = @"Local\SpotifyIsland_ShowIsland_Event_v2";

        _mutex = new System.Threading.Mutex(true, mutexName, out bool isNewInstance);
        if (!isNewInstance)
        {
            // Another instance is already running! Signal it to show and exit immediately.
            try
            {
                using var ev = System.Threading.EventWaitHandle.OpenExisting(eventName);
                ev.Set();
            }
            catch { }
            Shutdown();
            return;
        }

        try
        {
            _showEvent = new System.Threading.EventWaitHandle(false, System.Threading.EventResetMode.AutoReset, eventName);
            _showEventRegistration = System.Threading.ThreadPool.RegisterWaitForSingleObject(_showEvent, (state, timedOut) =>
            {
                Dispatcher.InvokeAsync(ShowIsland);
            }, null, -1, false);
        }
        catch { }

        base.OnStartup(e);

        // ── Global crash handlers ────────────────────────────────────────
        AppDomain.CurrentDomain.UnhandledException += (s, args) =>
        {
            try { AppendCrashLog($"Unhandled: {args.ExceptionObject}"); } catch { }
        };
        DispatcherUnhandledException += (s, args) =>
        {
            try { AppendCrashLog($"DispatcherUnhandled: {args.Exception}"); } catch { }
            args.Handled = true;
        };

        // ── Create MainWindow (hidden until Spotify is detected) ─────────
        _mainWindow = new MainWindow();
        MainWindow = _mainWindow;

        // ── System Tray Icon ─────────────────────────────────────────────
        _trayIcon = new NotifyIcon
        {
            Icon    = LoadTrayIcon(),
            Text    = "Spotify Island — Waiting for Spotify…",
            Visible = true
        };
        _trayIcon.DoubleClick += (s, _) => ShowIsland();

        var menu = new ContextMenuStrip();
        menu.Renderer = new DarkMenuRenderer();

        var titleItem = new ToolStripMenuItem("🏝  Spotify Island") { Enabled = false };
        titleItem.Font = new Font(titleItem.Font, System.Drawing.FontStyle.Bold);

        var showItem    = new ToolStripMenuItem("Show Island",          null, (_, __) => ShowIsland());
        var spotifyItem = new ToolStripMenuItem("Open Spotify",         null, (_, __) => OpenSpotify());
        var quitItem    = new ToolStripMenuItem("Quit",                 null, (_, __) => QuitApp());
        _startupMenuItem = new ToolStripMenuItem("Run on Windows Startup", null, (_, __) => ToggleStartup());
        _startupMenuItem.Checked = IsInStartup();

        menu.Items.Add(titleItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(showItem);
        menu.Items.Add(spotifyItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_startupMenuItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(quitItem);

        _trayIcon.ContextMenuStrip = menu;

        // Show the window so the island is immediately visible and interactive.
        _mainWindow.Show();
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    public void ShowIsland()
    {
        if (_mainWindow == null) return;

        // If the window was closed (e.g. after a previous crash), recreate it
        if (!_mainWindow.IsLoaded)
        {
            _mainWindow = new MainWindow();
            MainWindow  = _mainWindow;
        }

        _mainWindow.ShowForUserRequest();
        _mainWindow.Activate();
        _trayIcon!.Text = "Spotify Island — Active";
    }

    public void UpdateTrayText(string text)
    {
        if (_trayIcon != null)
            _trayIcon.Text = text.Length > 63 ? text[..63] : text; // Win32 limit = 63 chars
    }

    private static void OpenSpotify()
    {
        try { Process.Start(new ProcessStartInfo("spotify:") { UseShellExecute = true }); }
        catch { }
    }

    private void QuitApp()
    {
        // Run MainWindow cleanup before shutdown
        if (_mainWindow is SpotifyIsland.MainWindow mw)
        {
            try { mw.OnWindowDispose(); } catch { }
        }
        _trayIcon?.Dispose();
        Shutdown();
    }

    // ── Windows Startup Registry ──────────────────────────────────────────

    private const string StartupKey  = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
    private const string StartupName = "SpotifyIsland";

    private static bool IsInStartup()
    {
        using var key = Registry.CurrentUser.OpenSubKey(StartupKey, false);
        return key?.GetValue(StartupName) != null;
    }

    private void ToggleStartup()
    {
        using var key = Registry.CurrentUser.OpenSubKey(StartupKey, true);
        if (key == null) return;

        if (IsInStartup())
        {
            key.DeleteValue(StartupName, false);
            if (_startupMenuItem != null) _startupMenuItem.Checked = false;
        }
        else
        {
            string? exe = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName;
            if (exe != null)
            {
                key.SetValue(StartupName, $"\"{exe}\"");
                if (_startupMenuItem != null) _startupMenuItem.Checked = true;
            }
        }
    }

    // ── Icon loader (embedded ICO → tray icon) ────────────────────────────

    private static Icon LoadTrayIcon()
    {
        try
        {
            // Try to load app.ico sitting next to the exe (AppContext.BaseDirectory works in single-file apps)
            string iconPath = System.IO.Path.Combine(AppContext.BaseDirectory, "app.ico");
            if (File.Exists(iconPath))
                return new Icon(iconPath, 16, 16);
        }
        catch { }

        // Fallback: generate a tiny green circle icon in-memory
        var bmp = new Bitmap(16, 16);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.Transparent);
            using var brush = new SolidBrush(Color.FromArgb(30, 215, 96));
            g.FillEllipse(brush, 1, 1, 13, 13);
        }
        return Icon.FromHandle(bmp.GetHicon());
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _trayIcon?.Dispose();
        _showEventRegistration?.Unregister(null);
        _showEvent?.Dispose();
        _mutex?.Dispose();
        base.OnExit(e);
    }

    private static void AppendCrashLog(string message)
    {
        try
        {
            string logPath = System.IO.Path.Combine(AppContext.BaseDirectory, "crash.log");
            string line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}";
            File.AppendAllText(logPath, line);
        }
        catch { }
    }
}

// ── Dark context-menu renderer ────────────────────────────────────────────

internal class DarkMenuRenderer : ToolStripProfessionalRenderer
{
    public DarkMenuRenderer() : base(new DarkColorTable()) { }
}

internal class DarkColorTable : ProfessionalColorTable
{
    public override Color MenuItemSelected          => Color.FromArgb(50, 50, 58);
    public override Color MenuItemBorder            => Color.FromArgb(60, 60, 68);
    public override Color MenuBorder                => Color.FromArgb(40, 40, 48);
    public override Color ToolStripDropDownBackground => Color.FromArgb(22, 22, 28);
    public override Color ImageMarginGradientBegin  => Color.FromArgb(22, 22, 28);
    public override Color ImageMarginGradientMiddle => Color.FromArgb(22, 22, 28);
    public override Color ImageMarginGradientEnd    => Color.FromArgb(22, 22, 28);
    public override Color SeparatorDark             => Color.FromArgb(50, 50, 58);
    public override Color SeparatorLight            => Color.FromArgb(50, 50, 58);
}
