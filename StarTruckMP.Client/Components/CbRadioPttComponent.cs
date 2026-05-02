using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Concentus;
using Concentus.Enums;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using RNNoise.NET;
using StarTruckMP.Client.Synchronization;
using UnityEngine;

namespace StarTruckMP.Client.Components;

public class CbRadioPttComponent : MonoBehaviour
{
    private const int MicClipLengthSeconds = 1;
    private const int MicWarmupFrameCount = 3;
    private const int MicReadLagFrameCount = 1;

    public static bool IsCbPttPressed { get; set; }
    public static event Action<bool> CbPttStateChanged;
    
    // Mic & Audio
    public const int SampleRate = 48000;
    public const int Channels = 1;
    
    // Opus
    public const int FrameDurationMs = 20;
    public const int Bitrate = 24000;
    public const OpusApplication OpusApp = OpusApplication.OPUS_APPLICATION_VOIP;
    
    private CBRadioController _cbRadio;
    private string _device;
    private string _deviceLabel;
    private AudioClip _micClip;
    private CancellationTokenSource _cts;
    private bool _enabled;
    private IOpusEncoder _encoder;
    private Denoiser _denoiser;
    private int _samplesPerFrame;
    private byte[] _encodeBuffer;
    private int _captureSamplesPerFrame;
    private int _captureSampleRate;
    private int _micChannels;
    private bool _micReady;
    private float _micStartTime;
    private Il2CppStructArray<float> _micReadBuffer;

    // Mic read position — updated on the main thread only.
    private int _micReadPos;

    // Raw PCM frames queued by Update (main thread) → consumed by the encoder thread.
    private readonly ConcurrentQueue<float[]> _encodeQueue = new();
    private readonly List<MicOpenCandidate> _micCandidates = new();

    private sealed class MicOpenCandidate
    {
        public string Device { get; init; }
        public string DeviceLabel { get; init; }
        public int RequestedSampleRate { get; init; }
        public string DeviceCaps { get; init; }

        public override string ToString() => $"{DeviceLabel} @ {RequestedSampleRate} Hz ({DeviceCaps})";
    }

    private void Awake()
    {
        DontDestroyOnLoad(gameObject);
        _cts = new CancellationTokenSource();

        _samplesPerFrame = SampleRate * FrameDurationMs / 1000;
        _encodeBuffer = new byte[4000];
        _enabled = true;

        if (Microphone.devices.Length == 0)
        {
            App.Log.LogError("[CB Radio] No microphone devices found.");
            return;
        }

        App.Log.LogInfo($"[CB Radio] Available microphone devices: {string.Join(", ", Microphone.devices)}");

        BuildMicrophoneCandidates();
        if (!TryStartMicrophoneCandidate(0, "initial startup"))
        {
            App.Log.LogError("[CB Radio] Failed to start any microphone candidate.");
            return;
        }

        // Opus encoder
        _encoder = OpusCodecFactory.CreateEncoder(SampleRate, Channels, OpusApp);
        _encoder.Bitrate = Bitrate;
        _encoder.Complexity = 3; // 0-10; 3-5 best for real-time

        try
        {
            _denoiser = new Denoiser();
            App.Log.LogInfo("[CB Radio] RNNoise input denoiser ready");
        }
        catch (Exception ex)
        {
            App.Log.LogError($"[CB Radio] Failed to initialise RNNoise denoiser: {ex}");
            App.Log.LogWarning("[CB Radio] Voice input will be encoded without RNNoise noise reduction.");
            _denoiser = null;
        }

        // The encoder runs on a background thread; mic reading happens in Update().
        Plugin.StartAttachedThread(EncodeLoop);

        App.Log.LogInfo($"[CB Radio] Encoder started on device {_deviceLabel}");
    }

    /// <summary>
    /// Reads microphone samples into complete 20 ms frames.
    /// Must run on the main thread — AudioClip.GetData requires it.
    /// PTT frames are queued for the encoder thread.
    /// </summary>
    private void ReadMicFrames()
    {
        if (_micClip == null) return;
        if (!_micReady && !TryPrimeMicrophone()) return;

        var writePos = Microphone.GetPosition(_device);
        if (writePos < 0)
            return;

        var available = GetRingDistance(_micReadPos, writePos, _micClip.samples);
        var readable = available - (_captureSamplesPerFrame * MicReadLagFrameCount);

        while (readable >= _captureSamplesPerFrame)
        {
            if (!_micClip.GetData(_micReadBuffer, _micReadPos))
            {
                App.Log.LogWarning($"[CB Radio] AudioClip.GetData failed at readPos={_micReadPos} (writePos={writePos}, available={available}, channels={_micChannels}, clipSamples={_micClip.samples})");
                _micReady = false;
                return;
            }

            var frame = ConvertCaptureBufferToOutputFrame();
            _micReadPos = (_micReadPos + _captureSamplesPerFrame) % _micClip.samples;
            readable -= _captureSamplesPerFrame;

            TryDenoiseFrame(frame, "input");

            if (IsCbPttPressed)
            {
                _encodeQueue.Enqueue(frame);
            }
        }
    }

    /// <summary>
    /// Background thread: dequeues raw PCM frames, encodes them with Opus and sends over the network.
    /// </summary>
    private void EncodeLoop()
    {
        while (_enabled && !_cts.IsCancellationRequested)
        {
            if (_encodeQueue.TryDequeue(out var frame))
            {
                if (_encoder == null) continue;

                var written = _encoder.Encode(frame, _samplesPerFrame, _encodeBuffer, _encodeBuffer.Length);
                if (written <= 0) continue;

                var packet = new byte[written];
                Buffer.BlockCopy(_encodeBuffer, 0, packet, 0, written);
                Network.SendOpusFrame(packet);
            }
            else
            {
                Thread.Sleep(5);
            }
        }
    }

    private void TryDenoiseFrame(float[] frame, string path)
    {
        if (_denoiser == null || frame == null || frame.Length == 0)
            return;

        try
        {
            var processed = _denoiser.Denoise(frame.AsSpan(), false);
            if (processed <= 0)
                App.Log.LogWarning($"[CB Radio] RNNoise returned no samples for {path}; using the raw frame.");
        }
        catch (Exception ex)
        {
            App.Log.LogError($"[CB Radio] RNNoise denoise failed on {path}: {ex}");
            App.Log.LogWarning("[CB Radio] Disabling RNNoise for the rest of this session.");
            _denoiser.Dispose();
            _denoiser = null;
        }
    }

    private void Update()
    {
        TryBindCbRadio();
        UpdateCbPttState();

        if (IsCbPttPressed)
            ReadMicFrames();
        else
            ResetMicReadState(); // discard stale data and re-prime before the next talk burst
    }

    private bool TryPrimeMicrophone()
    {
        if (_micClip == null || !Microphone.IsRecording(_device))
            return false;

        var writePos = Microphone.GetPosition(_device);
        if (writePos <= 0)
            return false;

        var warmupSamples = _captureSamplesPerFrame * MicWarmupFrameCount;
        var warmupSeconds = (float)warmupSamples / Math.Max(1, _captureSampleRate);
        if (Time.realtimeSinceStartup - _micStartTime < warmupSeconds)
            return false;

        _micReadPos = (writePos - warmupSamples + _micClip.samples) % _micClip.samples;
        _micReady = true;
        App.Log.LogInfo($"[CB Radio] Mic primed: writePos={writePos}, readPos={_micReadPos}, warmupSamples={warmupSamples}");
        return true;
    }

    private void ResetMicReadState()
    {
        if (_micClip == null)
            return;

        _micReady = false;
        _micReadPos = Math.Max(0, Microphone.GetPosition(_device));
    }

    private void BuildMicrophoneCandidates()
    {
        _micCandidates.Clear();

        foreach (var device in BuildPreferredDeviceOrder())
        {
            Microphone.GetDeviceCaps(device, out var minFreq, out var maxFreq);
            var caps = DescribeDeviceCaps(minFreq, maxFreq);
            var label = string.IsNullOrWhiteSpace(device) ? "<system default>" : device;

            foreach (var sampleRate in BuildCaptureSampleRateCandidates(minFreq, maxFreq))
            {
                if (_micCandidates.Any(candidate => string.Equals(candidate.Device, device, StringComparison.OrdinalIgnoreCase) && candidate.RequestedSampleRate == sampleRate))
                    continue;

                _micCandidates.Add(new MicOpenCandidate
                {
                    Device = device,
                    DeviceLabel = label,
                    RequestedSampleRate = sampleRate,
                    DeviceCaps = caps
                });
            }
        }

        App.Log.LogInfo($"[CB Radio] Microphone candidate order: {string.Join(" | ", _micCandidates.Select((candidate, index) => $"#{index}: {candidate}"))}");
    }

    private IEnumerable<string> BuildPreferredDeviceOrder()
    {
        var availableDevices = Microphone.devices ?? Array.Empty<string>();
        var configuredDeviceName = App.MicrophoneDeviceName?.Value?.Trim();
        var preferSystemDefault = App.PreferSystemDefaultMicrophone?.Value ?? true;
        var orderedDevices = new List<string>();

        if (!string.IsNullOrWhiteSpace(configuredDeviceName))
        {
            var configuredDevice = availableDevices.FirstOrDefault(device => string.Equals(device, configuredDeviceName, StringComparison.OrdinalIgnoreCase));
            if (configuredDevice != null)
                AddDeviceIfMissing(orderedDevices, configuredDevice);
            else
                App.Log.LogWarning($"[CB Radio] Configured microphone '{configuredDeviceName}' was not found. Falling back to auto-selection.");
        }

        if (preferSystemDefault)
            AddDeviceIfMissing(orderedDevices, null);

        var scoredDevices = new List<DeviceScore>(availableDevices.Length);
        for (var index = 0; index < availableDevices.Length; index++)
            scoredDevices.Add(new DeviceScore(availableDevices[index], index, ScoreMicrophoneDeviceName(availableDevices[index])));

        scoredDevices.Sort((left, right) =>
        {
            var scoreComparison = right.Score.CompareTo(left.Score);
            return scoreComparison != 0 ? scoreComparison : left.Index.CompareTo(right.Index);
        });

        foreach (var scoredDevice in scoredDevices)
            AddDeviceIfMissing(orderedDevices, scoredDevice.Name);

        AddDeviceIfMissing(orderedDevices, null);
        return orderedDevices;
    }

    private static IEnumerable<int> BuildCaptureSampleRateCandidates(int minFreq, int maxFreq)
    {
        var rates = new List<int>();

        void AddRate(int rate)
        {
            if (rate > 0 && !rates.Contains(rate))
                rates.Add(rate);
        }

        if (minFreq > 0 && minFreq == maxFreq)
        {
            AddRate(minFreq);
            AddRate(44100);
            AddRate(SampleRate);
        }
        else
        {
            if (IsSampleRateSupported(SampleRate, minFreq, maxFreq))
                AddRate(SampleRate);
            if (IsSampleRateSupported(44100, minFreq, maxFreq))
                AddRate(44100);
            AddRate(maxFreq);
            AddRate(minFreq);
        }

        if (rates.Count == 0)
        {
            AddRate(44100);
            AddRate(SampleRate);
        }

        return rates;
    }

    private bool TryStartMicrophoneCandidate(int startIndex, string reason)
    {
        for (var index = Math.Max(0, startIndex); index < _micCandidates.Count; index++)
        {
            if (TryStartMicrophone(_micCandidates[index], reason))
                return true;
        }

        return false;
    }

    private bool TryStartMicrophone(MicOpenCandidate candidate, string reason)
    {
        StopMicrophoneCapture();

        _device = candidate.Device;
        _deviceLabel = candidate.DeviceLabel;

        App.Log.LogInfo($"[CB Radio] Opening microphone {candidate.DeviceLabel} (caps: {candidate.DeviceCaps}, requestedRate={candidate.RequestedSampleRate}) [{reason}]");
        if (!IsSampleRateSupported(candidate.RequestedSampleRate, candidate.Device))
        {
            App.Log.LogWarning($"[CB Radio] Requested {candidate.RequestedSampleRate} Hz is outside the device caps {candidate.DeviceCaps}. Skipping candidate {candidate.DeviceLabel}.");
            return false;
        }

        _micClip = Microphone.Start(_device, true, MicClipLengthSeconds, candidate.RequestedSampleRate);
        if (_micClip == null)
        {
            App.Log.LogWarning($"[CB Radio] Failed to start microphone {candidate.DeviceLabel} @ {candidate.RequestedSampleRate} Hz.");
            return false;
        }

        _micChannels = Math.Max(1, _micClip.channels);
        _captureSampleRate = _micClip.frequency > 0 ? _micClip.frequency : candidate.RequestedSampleRate;
        _captureSamplesPerFrame = Math.Max(1, _captureSampleRate * FrameDurationMs / 1000);
        _micReadBuffer = new Il2CppStructArray<float>(_captureSamplesPerFrame * _micChannels);
        _micReadPos = 0;
        _micReady = false;
        _micStartTime = Time.realtimeSinceStartup;

        App.Log.LogInfo(
            $"[CB Radio] Microphone started on {candidate.DeviceLabel}: requestedRate={candidate.RequestedSampleRate}, clipFrequency={_captureSampleRate}, clipChannels={_micChannels}, clipSamples={_micClip.samples}, outputFrameSamples={_samplesPerFrame}, captureFrameSamples={_captureSamplesPerFrame}");
        return true;
    }

    private void StopMicrophoneCapture()
    {
        _micReady = false;
        _micReadPos = 0;
        _micReadBuffer = null;
        _captureSamplesPerFrame = 0;
        _captureSampleRate = 0;

        try
        {
            if (_micClip != null)
                Microphone.End(_device);
        }
        catch (Exception ex)
        {
            App.Log.LogWarning($"[CB Radio] Failed to stop microphone {_deviceLabel}: {ex.Message}");
        }

        _micClip = null;
    }

    private float[] ConvertCaptureBufferToOutputFrame()
    {
        var monoFrame = new float[_captureSamplesPerFrame];
        if (_micChannels == 1)
        {
            for (int sampleIndex = 0; sampleIndex < _captureSamplesPerFrame; sampleIndex++)
                monoFrame[sampleIndex] = _micReadBuffer[sampleIndex];
        }
        else
        {
            for (int sampleIndex = 0; sampleIndex < _captureSamplesPerFrame; sampleIndex++)
            {
                float sample = 0f;
                var baseIndex = sampleIndex * _micChannels;
                for (int channel = 0; channel < _micChannels; channel++)
                    sample += _micReadBuffer[baseIndex + channel];
                monoFrame[sampleIndex] = sample / _micChannels;
            }
        }

        if (monoFrame.Length == _samplesPerFrame)
            return monoFrame;

        return ResampleFrame(monoFrame, _samplesPerFrame);
    }

    private static int GetRingDistance(int from, int to, int length)
    {
        var distance = to - from;
        return distance < 0 ? distance + length : distance;
    }

    private static int ScoreMicrophoneDeviceName(string deviceName)
    {
        if (string.IsNullOrWhiteSpace(deviceName))
            return int.MinValue;

        var name = deviceName.ToLowerInvariant();
        var score = 0;

        if (name.Contains("micrófono") || name.Contains("microphone") || name.Contains(" headset") || name.StartsWith("mic") || name.Contains(" mic ") || name.Contains("headset"))
            score += 100;
        if (name.Contains("line") || name.Contains("analogue") || name.Contains("stereo mix") || name.Contains("loopback") || name.Contains("monitor"))
            score -= 100;

        return score;
    }

    private static void AddDeviceIfMissing(ICollection<string> devices, string device)
    {
        foreach (var existingDevice in devices)
        {
            if (string.Equals(existingDevice, device, StringComparison.OrdinalIgnoreCase))
                return;
        }

        devices.Add(device);
    }

    private sealed class DeviceScore
    {
        public DeviceScore(string name, int index, int score)
        {
            Name = name;
            Index = index;
            Score = score;
        }

        public string Name { get; }
        public int Index { get; }
        public int Score { get; }
    }

    private static bool IsSampleRateSupported(int sampleRate, string device)
    {
        Microphone.GetDeviceCaps(device, out var minFreq, out var maxFreq);
        return IsSampleRateSupported(sampleRate, minFreq, maxFreq);
    }

    private static bool IsSampleRateSupported(int sampleRate, int minFreq, int maxFreq)
    {
        return minFreq == 0 && maxFreq == 0 || sampleRate >= minFreq && sampleRate <= maxFreq;
    }

    private static float[] ResampleFrame(float[] source, int targetLength)
    {
        var output = new float[targetLength];
        if (source.Length == 0 || targetLength == 0)
            return output;

        if (source.Length == 1)
        {
            for (int i = 0; i < targetLength; i++)
                output[i] = source[0];
            return output;
        }

        if (targetLength == 1)
        {
            output[0] = source[0];
            return output;
        }

        var step = (source.Length - 1f) / (targetLength - 1f);
        for (int i = 0; i < targetLength; i++)
        {
            var sourcePos = i * step;
            var left = Mathf.Clamp((int)sourcePos, 0, source.Length - 1);
            var right = Mathf.Min(left + 1, source.Length - 1);
            var fraction = sourcePos - left;
            output[i] = Mathf.Lerp(source[left], source[right], fraction);
        }

        return output;
    }

    private static string DescribeDeviceCaps(int minFreq, int maxFreq)
    {
        return minFreq == 0 && maxFreq == 0 ? "any/unknown" : $"{minFreq}-{maxFreq} Hz";
    }
    
    private void TryBindCbRadio()
    {
        if (_cbRadio != null)
            return;

        _cbRadio = PlayerState.Truck.GetComponentInChildren<CBRadioController>();
        if (_cbRadio == null) return;

        App.Log.LogInfo("[CB Radio] controller found");
        var speaker = _cbRadio.gameObject.AddComponent<CbRadioSpeakerComponent>();
        speaker.SpeakerPosition = _cbRadio.transform.position;
    }

    private void UpdateCbPttState()
    {
        if (_cbRadio == null)
        {
            SetCbPttState(false);
            return;
        }

        var isMicHeld = _cbRadio.cbHeldBinding?.Get() ?? false;
        var isTalkHeld = ControlBindings.cbTalk != null && ControlBindings.cbTalk.Held();
        SetCbPttState(isMicHeld && isTalkHeld);
    }

    private void SetCbPttState(bool isPressed)
    {
        if (IsCbPttPressed == isPressed)
            return;

        IsCbPttPressed = isPressed;
        App.Log.LogInfo($"[CB Radio] PTT => {(isPressed ? "DOWN" : "UP")}");
        CbPttStateChanged?.Invoke(isPressed);
    }

    private void OnDisable()
    {
        App.Log.LogWarning("[CB Radio] PTT component disabled, stopping mic thread and cleaning up state");
        _cts.Cancel();
    }

    private void OnDestroy()
    {
        _enabled = false;
        if (!_cts.IsCancellationRequested)
            _cts.Cancel();

        try
        {
            StopMicrophoneCapture();
        }
        catch (Exception ex)
        {
            App.Log.LogError($"[CB Radio] Failed to end microphone: {ex}");
            App.Log.LogError(ex);
        }
        
        if (_encoder is IDisposable d)
        {
            d.Dispose();
            _encoder = null;
        }

        if (_denoiser != null)
        {
            _denoiser.Dispose();
            _denoiser = null;
        }
    }
}