using System;
using System.Diagnostics;
using NAudio.CoreAudioApi;

namespace SpotifyIsland.Services;

public class SystemAudioService : IDisposable
{
    private MMDeviceEnumerator? _deviceEnumerator;
    private MMDevice? _defaultDevice;

    public event Action<int, bool>? VolumeChanged;

    public int CurrentVolumePercent { get; private set; } = 50;
    public bool IsMuted { get; private set; } = false;
    public string ActiveDeviceName => _defaultDevice?.FriendlyName ?? "System Audio";

    public SystemAudioService()
    {
        try
        {
            _deviceEnumerator = new MMDeviceEnumerator();
            _defaultDevice = _deviceEnumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            if (_defaultDevice != null)
            {
                CurrentVolumePercent = (int)Math.Round(_defaultDevice.AudioEndpointVolume.MasterVolumeLevelScalar * 100);
                IsMuted = _defaultDevice.AudioEndpointVolume.Mute;

                _defaultDevice.AudioEndpointVolume.OnVolumeNotification += OnVolumeNotification;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Error initializing MMDevice: {ex.Message}");
        }
    }

    private void OnVolumeNotification(AudioVolumeNotificationData data)
    {
        int pct = (int)Math.Round(data.MasterVolume * 100);
        CurrentVolumePercent = pct;
        IsMuted = data.Muted;
        VolumeChanged?.Invoke(pct, data.Muted);
    }

    public void StepVolumeUp(float step = 0.02f)
    {
        try
        {
            if (_defaultDevice != null)
            {
                float cur = _defaultDevice.AudioEndpointVolume.MasterVolumeLevelScalar;
                _defaultDevice.AudioEndpointVolume.MasterVolumeLevelScalar = Math.Clamp(cur + step, 0f, 1f);
            }
        }
        catch { }
    }

    public void StepVolumeDown(float step = 0.02f)
    {
        try
        {
            if (_defaultDevice != null)
            {
                float cur = _defaultDevice.AudioEndpointVolume.MasterVolumeLevelScalar;
                _defaultDevice.AudioEndpointVolume.MasterVolumeLevelScalar = Math.Clamp(cur - step, 0f, 1f);
            }
        }
        catch { }
    }

    public void Dispose()
    {
        try
        {
            if (_defaultDevice != null)
            {
                _defaultDevice.AudioEndpointVolume.OnVolumeNotification -= OnVolumeNotification;
                _defaultDevice.Dispose();
                _defaultDevice = null;
            }
            _deviceEnumerator?.Dispose();
        }
        catch { }
    }
}
