using System;
using System.Diagnostics;
using System.Management;
using System.Threading;

namespace SpotifyIsland.Services;

/// <summary>
/// Zero-poll process watcher using WMI Win32_ProcessStartTrace and
/// Win32_ProcessStopTrace event subscriptions. Fires <see cref="ProcessStarted"/>
/// and <see cref="ProcessStopped"/> on a ThreadPool thread whenever a process
/// whose name matches <see cref="ProcessName"/> starts or stops.
/// </summary>
public sealed class ProcessWatcherService : IDisposable
{
    public string ProcessName { get; }

    public event Action? ProcessStarted;
    public event Action? ProcessStopped;

    private ManagementEventWatcher? _startWatcher;
    private ManagementEventWatcher? _stopWatcher;
    private bool _disposed;

    public ProcessWatcherService(string processName)
    {
        ProcessName = processName;
    }

    public void Start()
    {
        if (_disposed) return;

        try
        {
            // WMI event queries – pollingInterval is a within-interval guarantee
            // (1 second is fine for process start/stop detection).
            string startQuery = $"SELECT * FROM Win32_ProcessStartTrace WHERE ProcessName = '{ProcessName}.exe'";
            string stopQuery  = $"SELECT * FROM Win32_ProcessStopTrace  WHERE ProcessName = '{ProcessName}.exe'";

            _startWatcher = new ManagementEventWatcher(new WqlEventQuery(startQuery));
            _stopWatcher  = new ManagementEventWatcher(new WqlEventQuery(stopQuery));

            _startWatcher.EventArrived += (s, e) =>
            {
                Debug.WriteLine($"[ProcessWatcher] {ProcessName} started.");
                ProcessStarted?.Invoke();
            };

            _stopWatcher.EventArrived += (s, e) =>
            {
                Debug.WriteLine($"[ProcessWatcher] {ProcessName} stopped.");
                ProcessStopped?.Invoke();
            };

            _startWatcher.Start();
            _stopWatcher.Start();

            Debug.WriteLine($"[ProcessWatcher] Watching {ProcessName} via WMI.");
        }
        catch (Exception ex)
        {
            // WMI is available on all supported Windows versions, but can fail in
            // restricted environments (e.g. some sandboxed containers). Fall back
            // gracefully — the existing timer-based monitor in MainWindow remains
            // as the fallback if this service fails to start.
            Debug.WriteLine($"[ProcessWatcher] WMI watcher failed to start: {ex.Message}");
            DisposeWatchers();
        }
    }

    /// <summary>Returns true if the watched process is currently running.</summary>
    public bool IsRunning()
    {
        try { return Process.GetProcessesByName(ProcessName).Length > 0; }
        catch { return false; }
    }

    private void DisposeWatchers()
    {
        try { _startWatcher?.Stop(); _startWatcher?.Dispose(); } catch { }
        try { _stopWatcher?.Stop();  _stopWatcher?.Dispose();  } catch { }
        _startWatcher = null;
        _stopWatcher  = null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        DisposeWatchers();
    }
}
