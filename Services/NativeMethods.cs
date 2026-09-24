using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace SpotifyIsland.Services;

public static class NativeMethods
{
    public const int WM_HOTKEY = 0x0312;
    public const int WM_NCLBUTTONDOWN = 0xA1;
    public const int HTCAPTION = 0x2;

    public const uint MOD_NONE = 0x0000;
    public const uint MOD_ALT = 0x0001;
    public const uint MOD_CONTROL = 0x0002;
    public const uint MOD_SHIFT = 0x0004;
    public const uint MOD_WIN = 0x0008;

    public const uint VK_SPACE = 0x20;
    public const uint VK_LEFT = 0x25;
    public const uint VK_UP = 0x26;
    public const uint VK_RIGHT = 0x27;
    public const uint VK_DOWN = 0x28;
    public const uint VK_VOLUME_MUTE = 0xAD;
    public const uint VK_VOLUME_DOWN = 0xAE;
    public const uint VK_VOLUME_UP = 0xAF;
    public const uint VK_MEDIA_NEXT_TRACK = 0xB0;
    public const uint VK_MEDIA_PREV_TRACK = 0xB1;
    public const uint VK_MEDIA_STOP = 0xB2;
    public const uint VK_MEDIA_PLAY_PAUSE = 0xB3;

    public const uint KEYEVENTF_EXTENDEDKEY = 0x0001;
    public const uint KEYEVENTF_KEYUP = 0x0002;

    // Send a single media key press + release with EXTENDEDKEY flag so Windows & Spotify recognize it
    public static void SendMediaKey(uint vk)
    {
        keybd_event((byte)vk, 0, KEYEVENTF_EXTENDEDKEY, UIntPtr.Zero);
        keybd_event((byte)vk, 0, KEYEVENTF_EXTENDEDKEY | KEYEVENTF_KEYUP, UIntPtr.Zero);
    }

    public const int SW_RESTORE = 9;
    public const int SW_SHOW = 5;

    [DllImport("user32.dll")]
    public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll")]
    public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("user32.dll")]
    public static extern int SendMessage(IntPtr hWnd, int msg, int wParam, int lParam);

    [DllImport("user32.dll")]
    public static extern bool ReleaseCapture();

    [DllImport("user32.dll")]
    public static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    public static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

    public static void StepVolumeUp()
    {
        keybd_event((byte)VK_VOLUME_UP, 0, KEYEVENTF_EXTENDEDKEY, UIntPtr.Zero);
        keybd_event((byte)VK_VOLUME_UP, 0, KEYEVENTF_EXTENDEDKEY | KEYEVENTF_KEYUP, UIntPtr.Zero);
    }

    public static void StepVolumeDown()
    {
        keybd_event((byte)VK_VOLUME_DOWN, 0, KEYEVENTF_EXTENDEDKEY, UIntPtr.Zero);
        keybd_event((byte)VK_VOLUME_DOWN, 0, KEYEVENTF_EXTENDEDKEY | KEYEVENTF_KEYUP, UIntPtr.Zero);
    }

    [DllImport("user32.dll")]
    public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    public static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    public static extern int GetClassName(IntPtr hWnd, System.Text.StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    public static extern int GetWindowText(IntPtr hWnd, System.Text.StringBuilder lpString, int nMaxCount);

    public static IntPtr FindSpotifyMainWindow()
    {
        IntPtr found = IntPtr.Zero;
        try
        {
            var pids = Process.GetProcessesByName("Spotify");
            if (pids.Length == 0) return IntPtr.Zero;

            var pidSet = new System.Collections.Generic.HashSet<uint>();
            foreach (var p in pids)
            {
                pidSet.Add((uint)p.Id);
                if (p.MainWindowHandle != IntPtr.Zero && (IsWindowVisible(p.MainWindowHandle) || IsIconic(p.MainWindowHandle)))
                {
                    return p.MainWindowHandle;
                }
            }

            EnumWindows((hwnd, lParam) =>
            {
                GetWindowThreadProcessId(hwnd, out uint pid);
                if (pidSet.Contains(pid))
                {
                    var sbClass = new System.Text.StringBuilder(256);
                    GetClassName(hwnd, sbClass, 256);
                    string className = sbClass.ToString();

                    if (className == "Chrome_WidgetWin_0")
                    {
                        var sbTitle = new System.Text.StringBuilder(256);
                        GetWindowText(hwnd, sbTitle, 256);
                        string title = sbTitle.ToString();

                        if (!string.IsNullOrEmpty(title) || IsWindowVisible(hwnd) || IsIconic(hwnd))
                        {
                            found = hwnd;
                            return false;
                        }
                    }
                }
                return true;
            }, IntPtr.Zero);
        }
        catch { }

        return found;
    }

    public static void FocusSpotifyWindow()
    {
        try
        {
            var hwnd = FindSpotifyMainWindow();
            if (hwnd != IntPtr.Zero)
            {
                if (IsIconic(hwnd))
                {
                    ShowWindow(hwnd, SW_RESTORE);
                }
                else
                {
                    ShowWindow(hwnd, SW_SHOW);
                }
                SetForegroundWindow(hwnd);
                return;
            }

            Process.Start(new ProcessStartInfo("spotify:") { UseShellExecute = true });
        }
        catch
        {
            try
            {
                Process.Start(new ProcessStartInfo("spotify:") { UseShellExecute = true });
            }
            catch { }
        }
    }
}
