using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace SpotifyIsland.Services;

public sealed record AudioOutputDevice(string Id, string Name, bool IsDefault);

/// <summary>
/// Manages system audio volume and exposes the active render device's friendly name.
/// Fires VolumeChanged on every volume/mute notification and DeviceChanged when the
/// default audio output is switched (e.g. headphones plugged in).
/// </summary>
public class SystemAudioService : IDisposable, IMMNotificationClient
{
    private MMDeviceEnumerator? _deviceEnumerator;
    private MMDevice? _defaultDevice;

    public event Action<int, bool>? VolumeChanged;
    /// <summary>Raised when the default render device changes (e.g. headphones plugged in).</summary>
    public event Action? DeviceChanged;

    public int CurrentVolumePercent { get; private set; } = 50;
    public bool IsMuted { get; private set; } = false;

    /// <summary>Friendly name of the current default render device (e.g. "Headphones (Realtek Audio)").</summary>
    public string ActiveDeviceName => _defaultDevice?.FriendlyName ?? "System Audio";

    public IReadOnlyList<AudioOutputDevice> GetOutputDevices()
    {
        var devices = new List<AudioOutputDevice>();
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            string? defaultId = _defaultDevice?.ID;
            foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            {
                devices.Add(new AudioOutputDevice(device.ID, device.FriendlyName, device.ID == defaultId));
                device.Dispose();
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Error listing audio devices: {ex.Message}");
        }
        return devices;
    }

    public bool SetDefaultOutputDevice(string deviceId)
    {
        if (string.IsNullOrWhiteSpace(deviceId)) return false;
        if (!AudioDevicePolicy.TrySetDefaultEndpoint(deviceId)) return false;

        RefreshDefaultDevice();
        DeviceChanged?.Invoke();
        return true;
    }

    public SystemAudioService()
    {
        try
        {
            _deviceEnumerator = new MMDeviceEnumerator();
            _deviceEnumerator.RegisterEndpointNotificationCallback(this);
            RefreshDefaultDevice();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Error initializing MMDevice: {ex.Message}");
        }
    }

    // ── Internal helpers ──────────────────────────────────────────────────

    private void RefreshDefaultDevice()
    {
        try
        {
            // Unsubscribe from the old device before replacing it
            if (_defaultDevice != null)
            {
                try { _defaultDevice.AudioEndpointVolume.OnVolumeNotification -= OnVolumeNotification; }
                catch { }
                _defaultDevice.Dispose();
                _defaultDevice = null;
            }

            _defaultDevice = _deviceEnumerator?.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            if (_defaultDevice != null)
            {
                CurrentVolumePercent = (int)Math.Round(_defaultDevice.AudioEndpointVolume.MasterVolumeLevelScalar * 100);
                IsMuted = _defaultDevice.AudioEndpointVolume.Mute;
                _defaultDevice.AudioEndpointVolume.OnVolumeNotification += OnVolumeNotification;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Error refreshing default audio device: {ex.Message}");
        }
    }

    private void OnVolumeNotification(AudioVolumeNotificationData data)
    {
        int pct = (int)Math.Round(data.MasterVolume * 100);
        CurrentVolumePercent = pct;
        IsMuted = data.Muted;
        VolumeChanged?.Invoke(pct, data.Muted);
    }

    // ── IMMNotificationClient — fires when the default device changes ─────

    void IMMNotificationClient.OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId)
    {
        if (flow == DataFlow.Render && role == Role.Multimedia)
        {
            RefreshDefaultDevice();
            DeviceChanged?.Invoke();
        }
    }

    void IMMNotificationClient.OnDeviceAdded(string deviceId) { }
    void IMMNotificationClient.OnDeviceRemoved(string deviceId) { }
    void IMMNotificationClient.OnDeviceStateChanged(string deviceId, DeviceState newState) { }
    void IMMNotificationClient.OnPropertyValueChanged(string deviceId, PropertyKey key) { }

    // ── Volume control ────────────────────────────────────────────────────

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
            _deviceEnumerator?.UnregisterEndpointNotificationCallback(this);
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

// Windows exposes the default-output switch through the PolicyConfig COM API.
// It is intentionally isolated here because changing outputs is an explicit user action.
internal static class AudioDevicePolicy
{
    public static bool TrySetDefaultEndpoint(string deviceId)
    {
        try
        {
            var policy = (IPolicyConfig)new PolicyConfigClient();
            try
            {
                return policy.SetDefaultEndpoint(deviceId, ERole.Console) == 0
                    && policy.SetDefaultEndpoint(deviceId, ERole.Multimedia) == 0
                    && policy.SetDefaultEndpoint(deviceId, ERole.Communications) == 0;
            }
            finally
            {
                Marshal.ReleaseComObject(policy);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Error selecting audio device: {ex.Message}");
            return false;
        }
    }
}

internal enum ERole
{
    Console,
    Multimedia,
    Communications,
}

[ComImport]
[Guid("F8679F50-850A-41CF-9C72-430F290290C8")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IPolicyConfig
{
    int GetMixFormat(string deviceId, IntPtr format);
    int GetDeviceFormat(string deviceId, bool defaultFormat, IntPtr format);
    int ResetDeviceFormat(string deviceId);
    int SetDeviceFormat(string deviceId, IntPtr endpointFormat, IntPtr mixFormat);
    int GetProcessingPeriod(string deviceId, bool defaultPeriod, IntPtr defaultPeriodValue, IntPtr minimumPeriod);
    int SetProcessingPeriod(string deviceId, IntPtr period);
    int GetShareMode(string deviceId, IntPtr mode);
    int SetShareMode(string deviceId, IntPtr mode);
    int GetPropertyValue(string deviceId, bool store, IntPtr key, IntPtr value);
    int SetPropertyValue(string deviceId, bool store, IntPtr key, IntPtr value);
    int SetDefaultEndpoint(string deviceId, ERole role);
    int SetEndpointVisibility(string deviceId, bool visible);
}

[ComImport]
[Guid("870AF99C-171D-4F9E-AF0D-E63DF40C2BC9")]
internal class PolicyConfigClient
{
}
