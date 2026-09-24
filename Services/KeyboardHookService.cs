using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace SpotifyIsland.Services;

/// <summary>
/// Low-level global keyboard hook (WH_KEYBOARD_LL).
/// Unlike RegisterHotKey, this cannot be blocked by other applications
/// and works even when the app window is hidden.
/// </summary>
public sealed class KeyboardHookService : IDisposable
{
    // WH_KEYBOARD_LL = 13
    private const int WH_KEYBOARD_LL   = 13;
    private const int WM_KEYDOWN       = 0x0100;
    private const int WM_SYSKEYDOWN    = 0x0104;

    private const int VK_CONTROL = 0x11;
    private const int VK_MENU    = 0x12; // Alt
    private const int VK_SPACE   = 0x20;
    private const int VK_LEFT    = 0x25;
    private const int VK_UP      = 0x26;
    private const int VK_RIGHT   = 0x27;
    private const int VK_DOWN    = 0x28;

    // ── Public events ───────────────────────────────────────────────────
    public event Action? PlayPause;
    public event Action? Next;
    public event Action? Previous;
    public event Action? VolumeUp;
    public event Action? VolumeDown;

    // ── Win32 plumbing ──────────────────────────────────────────────────
    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    [DllImport("user32.dll")]
    private static extern short GetKeyState(int vKey);

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    private IntPtr _hookHandle = IntPtr.Zero;
    private readonly LowLevelKeyboardProc _proc; // keep alive to prevent GC

    public KeyboardHookService()
    {
        _proc = HookCallback;
        Install();
    }

    private void Install()
    {
        try
        {
            IntPtr hMod = IntPtr.Zero;
            try
            {
                using var cur = Process.GetCurrentProcess();
                using var mod = cur.MainModule;
                if (mod?.ModuleName != null)
                {
                    hMod = GetModuleHandle(mod.ModuleName);
                }
            }
            catch { }

            if (hMod == IntPtr.Zero)
            {
                hMod = GetModuleHandle(null);
            }

            _hookHandle = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, hMod, 0);

            if (_hookHandle == IntPtr.Zero)
            {
                int err = Marshal.GetLastWin32Error();
                Debug.WriteLine($"[KeyboardHook] SetWindowsHookEx failed: {err}");
            }
            else
            {
                Debug.WriteLine("[KeyboardHook] Installed successfully.");
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[KeyboardHook] Exception: {ex.Message}");
        }
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            int msg = wParam.ToInt32();
            if (msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN)
            {
                var kbd = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
                uint vk = kbd.vkCode;

                bool ctrl = (GetAsyncKeyState(VK_CONTROL) & 0x8000) != 0
                         || (GetKeyState(VK_CONTROL) & 0x8000) != 0
                         || (GetAsyncKeyState(0xA2) & 0x8000) != 0   // VK_LCONTROL
                         || (GetAsyncKeyState(0xA3) & 0x8000) != 0;  // VK_RCONTROL

                bool alt  = (kbd.flags & 0x20) != 0                 // LLKHF_ALTDOWN
                         || (GetAsyncKeyState(VK_MENU) & 0x8000) != 0
                         || (GetKeyState(VK_MENU) & 0x8000) != 0
                         || (GetAsyncKeyState(0xA4) & 0x8000) != 0   // VK_LMENU
                         || (GetAsyncKeyState(0xA5) & 0x8000) != 0;  // VK_RMENU

                if (ctrl && alt)
                {
                    switch (vk)
                    {
                        case VK_SPACE: PlayPause?.Invoke(); return (IntPtr)1;  // consume
                        case VK_RIGHT: Next?.Invoke();      return (IntPtr)1;
                        case VK_LEFT:  Previous?.Invoke();  return (IntPtr)1;
                        case VK_UP:    VolumeUp?.Invoke();  return (IntPtr)1;
                        case VK_DOWN:  VolumeDown?.Invoke();return (IntPtr)1;
                    }
                }
            }
        }
        return CallNextHookEx(_hookHandle, nCode, wParam, lParam);
    }

    public void Dispose()
    {
        if (_hookHandle != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_hookHandle);
            _hookHandle = IntPtr.Zero;
        }
    }
}
