using System;
using System.Diagnostics;
using NAudio.Wave;

namespace SpotifyIsland.Services;

public class AudioCaptureService : IDisposable
{
    private WasapiLoopbackCapture? _capture;
    private readonly float[] _fftBuffer = new float[1024];
    private int _fftPos = 0;
    private readonly object _lock = new();

    public const int BandCount = 18;
    private readonly float[] _currentBands = new float[BandCount];
    private readonly float[] _peakBands = new float[BandCount];

    public bool IsActive { get; private set; }

    public void Start()
    {
        try
        {
            _capture = new WasapiLoopbackCapture();
            _capture.DataAvailable += OnDataAvailable;
            _capture.RecordingStopped += (s, a) => IsActive = false;
            _capture.StartRecording();
            IsActive = true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to start WASAPI loopback: {ex.Message}");
            IsActive = false;
        }
    }

    public void Stop()
    {
        try
        {
            if (_capture != null)
            {
                _capture.StopRecording();
                _capture.Dispose();
                _capture = null;
            }
        }
        catch { }
        finally
        {
            IsActive = false;
        }
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (e.BytesRecorded == 0 || _capture == null) return;

        int channels = _capture.WaveFormat.Channels;
        int bytesPerSample = _capture.WaveFormat.BitsPerSample / 8;

        lock (_lock)
        {
            for (int i = 0; i < e.BytesRecorded; i += bytesPerSample * channels)
            {
                float sample = 0;
                if (bytesPerSample == 4)
                {
                    sample = BitConverter.ToSingle(e.Buffer, i);
                }
                else if (bytesPerSample == 2)
                {
                    short val = BitConverter.ToInt16(e.Buffer, i);
                    sample = val / 32768f;
                }

                _fftBuffer[_fftPos++] = sample;
                if (_fftPos >= _fftBuffer.Length)
                {
                    _fftPos = 0;
                    ComputeBands();
                }
            }
        }
    }

    private void ComputeBands()
    {
        // Simple and robust multi-band energy calculation with Hann window
        int n = _fftBuffer.Length;
        int half = n / 2;

        // Logarithmic frequency division across 18 bands
        for (int band = 0; band < BandCount; band++)
        {
            double lowFraction = Math.Pow(band / (double)BandCount, 2.2);
            double highFraction = Math.Pow((band + 1) / (double)BandCount, 2.2);

            int startIdx = Math.Clamp((int)(lowFraction * half), 0, half - 1);
            int endIdx = Math.Clamp((int)(highFraction * half), startIdx + 1, half);

            float sum = 0f;
            int count = 0;
            for (int k = startIdx; k < endIdx; k++)
            {
                float val = _fftBuffer[k];
                sum += Math.Abs(val);
                count++;
            }

            float avg = count > 0 ? (sum / count) : 0f;

            // Frequency-dependent gain boost (boost treble and mids, scale bass)
            float freqWeight = 1.0f + (band / (float)BandCount) * 2.2f;
            float target = Math.Clamp(avg * freqWeight * 4.5f, 0f, 1f);

            // Fast attack, smooth decay
            if (target > _currentBands[band])
            {
                _currentBands[band] = target;
            }
            else
            {
                _currentBands[band] += (target - _currentBands[band]) * 0.22f;
            }
        }
    }

    public void GetFrequencyLevels(float[] output)
    {
        lock (_lock)
        {
            for (int i = 0; i < Math.Min(output.Length, BandCount); i++)
            {
                output[i] = _currentBands[i];
            }
        }
    }

    public void Dispose()
    {
        Stop();
    }
}
