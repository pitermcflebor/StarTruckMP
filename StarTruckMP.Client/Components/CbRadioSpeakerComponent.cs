using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using Concentus;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using StarTruckMP.Client.Audio;
using StarTruckMP.Client.Synchronization;
using StarTruckMP.Shared.Dto;
using UnityEngine;

namespace StarTruckMP.Client.Components;

public class CbRadioSpeakerComponent : MonoBehaviour
{
    private const int PlaybackClipLengthSeconds = 2;
    private const int StartPlaybackFrames = 3;
    private const int TargetLeadFrames = 4;
    private const int MaxWriteFramesPerUpdate = 6;

    /// <summary>World-space position of the CB radio speaker.</summary>
    public Vector3 SpeakerPosition
    {
        get => transform.position;
        set => transform.position = value;
    }

    /// <summary>Distance at which the volume starts attenuating.</summary>
    public float MinDistance
    {
        get => _minDistance;
        set
        {
            _minDistance = value;
            ApplyAudioSourceSettings();
        }
    }

    /// <summary>Distance at which the volume reaches zero.</summary>
    public float MaxDistance
    {
        get => _maxDistance;
        set
        {
            _maxDistance = value;
            ApplyAudioSourceSettings();
        }
    }


    private int _maxFrameSizePerChannel;
    private int _samplesPerFrame;
    private int _playbackClipSamples;
    private float _minDistance = 1f;
    private float _maxDistance = 30f;

    // Per-sender decoder state (keyed by NetId).
    private readonly Dictionary<int, SenderState> _senders = new();
    private readonly object _sendersLock = new();
    private readonly ConcurrentQueue<SenderState> _retiredSenders = new();

    private class SenderState : IDisposable
    {
        public readonly int SenderId;
        public readonly IOpusDecoder Decoder;
        public readonly float[] DecodeBuffer;
        public readonly RadioVoiceEffectProcessor RadioEffect;
        public readonly ConcurrentQueue<float[]> PendingFrames = new();
        public readonly Queue<float[]> BufferedFrames = new();
        public AudioSource AudioSource;
        public AudioClip PlaybackClip;
        public int BufferedFrameOffset;
        public int BufferedSampleCount;
        public int WriteSamplePosition;
        public bool IsPlaying;

        public SenderState(int senderId, int maxFrameSizePerChannel, bool createDecoder, RadioVoiceEffectProcessor radioEffect)
        {
            SenderId = senderId;
            RadioEffect = radioEffect;

            if (createDecoder)
            {
                Decoder = OpusCodecFactory.CreateDecoder(CbRadioPttComponent.SampleRate, CbRadioPttComponent.Channels);
                DecodeBuffer = new float[maxFrameSizePerChannel * CbRadioPttComponent.Channels];
            }
        }

        public void Dispose()
        {
        }
    }

    private void Awake()
    {
        DontDestroyOnLoad(gameObject);

        // Max Opus frame is 120 ms
        _maxFrameSizePerChannel = CbRadioPttComponent.SampleRate * 120 / 1000;
        _samplesPerFrame = CbRadioPttComponent.SampleRate * CbRadioPttComponent.FrameDurationMs / 1000 * CbRadioPttComponent.Channels;
        _playbackClipSamples = CbRadioPttComponent.SampleRate * PlaybackClipLengthSeconds;

        Network.OnVoiceReceived += HandleVoiceReceived;
        Network.OnPlayerDisconnected += HandlePlayerDisconnected;

        App.Log.LogInfo("[CB Radio] Speaker ready");
    }

    private void OnDestroy()
    {
        App.Log.LogWarning("[CB Radio] Speaker destroyed, cleaning up state");
        Network.OnVoiceReceived -= HandleVoiceReceived;
        Network.OnPlayerDisconnected -= HandlePlayerDisconnected;

        while (_retiredSenders.TryDequeue(out var retiredState))
            CleanupSenderState(retiredState);

        List<SenderState> activeStates;
        lock (_sendersLock)
        {
            activeStates = new List<SenderState>(_senders.Values);
            _senders.Clear();
        }

        foreach (var state in activeStates)
            CleanupSenderState(state);
    }

    // Called on the main thread every frame.
    // Keeps a small rolling PCM buffer ahead of the playhead for each sender.
    private void Update()
    {
        while (_retiredSenders.TryDequeue(out var retiredState))
            CleanupSenderState(retiredState);

        List<SenderState> senderStates;
        lock (_sendersLock)
        {
            senderStates = new List<SenderState>(_senders.Values);
        }

        foreach (var state in senderStates)
        {
            EnsurePlaybackObjects(state);
            DrainPendingFrames(state);
            PumpPlayback(state);
        }
    }

    // Called from the network polling thread.
    private void HandleVoiceReceived(VoiceDto voice)
    {
        if (voice.OpusData.Length == 0)
            return;

        var state = GetOrCreateSenderState(voice.NetId, createDecoder: true);

        // Decode outside the lock: DecodeBuffer is exclusive to this sender.
        int decoded;
        try
        {
            decoded = state.Decoder.Decode(
                voice.OpusData.AsSpan(),
                state.DecodeBuffer.AsSpan(),
                _maxFrameSizePerChannel);
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[CB Radio] Decode error (sender {voice.NetId}): {e.Message}");
            return;
        }

        if (decoded <= 0)
            return;

        // Copy decoded samples into a fresh buffer — the network thread will
        // continue to the next frame while Update() consumes this one.
        var samples = new float[decoded * CbRadioPttComponent.Channels];
        Array.Copy(state.DecodeBuffer, samples, samples.Length);
        var processed = state.RadioEffect?.Process(samples);
        state.PendingFrames.Enqueue(processed ?? samples);
    }

    // Called from the network polling thread when a player leaves.
    private void HandlePlayerDisconnected(int netId)
    {
        lock (_sendersLock)
        {
            if (_senders.TryGetValue(netId, out var state))
            {
                _senders.Remove(netId);
                _retiredSenders.Enqueue(state);
            }
        }
    }

    private SenderState GetOrCreateSenderState(int senderId, bool createDecoder)
    {
        lock (_sendersLock)
        {
            if (_senders.TryGetValue(senderId, out var existingState))
                return existingState;

            var state = new SenderState(senderId, _maxFrameSizePerChannel, createDecoder, CreateRadioEffectProcessor());
            _senders[senderId] = state;
            return state;
        }
    }

    private static RadioVoiceEffectProcessor CreateRadioEffectProcessor()
    {
        return new RadioVoiceEffectProcessor(
            CbRadioPttComponent.SampleRate,
            /*App.RadioEffectOutputGain?.Value ??*/ 2.0f
            );
    }

    private void EnsurePlaybackObjects(SenderState state)
    {
        if (state.AudioSource != null && state.PlaybackClip != null)
            return;

        var source = gameObject.AddComponent<AudioSource>();
        ConfigureAudioSource(source);

        var clip = AudioClip.Create(
            $"voice_sender_{state.SenderId}_ring",
            _playbackClipSamples,
            CbRadioPttComponent.Channels,
            CbRadioPttComponent.SampleRate,
            false);
        clip.SetData(new Il2CppStructArray<float>(new float[_playbackClipSamples * CbRadioPttComponent.Channels]), 0);

        source.clip = clip;
        source.loop = true;
        state.AudioSource = source;
        state.PlaybackClip = clip;
        state.WriteSamplePosition = 0;
        state.IsPlaying = false;
    }

    private void DrainPendingFrames(SenderState state)
    {
        while (state.PendingFrames.TryDequeue(out var frame))
        {
            if (frame == null || frame.Length == 0)
                continue;

            state.BufferedFrames.Enqueue(frame);
            state.BufferedSampleCount += frame.Length;
        }
    }

    private void PumpPlayback(SenderState state)
    {
        if (state.AudioSource == null || state.PlaybackClip == null)
            return;

        var startPlaybackSamples = _samplesPerFrame * StartPlaybackFrames;
        var targetLeadSamples = _samplesPerFrame * TargetLeadFrames;

        if (!state.IsPlaying)
        {
            if (state.BufferedSampleCount < startPlaybackSamples)
                return;

            var initialWriteSamples = Math.Min(state.BufferedSampleCount, targetLeadSamples);
            initialWriteSamples -= initialWriteSamples % _samplesPerFrame;
            if (initialWriteSamples < _samplesPerFrame)
                return;

            WriteBufferedSamplesToClip(state, initialWriteSamples, allowSilencePadding: false);
            state.AudioSource.timeSamples = 0;
            state.AudioSource.Play();
            state.IsPlaying = true;
            return;
        }

        var playheadSamples = Mathf.Clamp(state.AudioSource.timeSamples, 0, state.PlaybackClip.samples - 1);
        var leadSamples = GetRingDistance(playheadSamples, state.WriteSamplePosition, state.PlaybackClip.samples);
        if (leadSamples >= targetLeadSamples)
            return;

        var samplesNeeded = targetLeadSamples - leadSamples;
        samplesNeeded = Mathf.Clamp(samplesNeeded, _samplesPerFrame, _samplesPerFrame * MaxWriteFramesPerUpdate);
        samplesNeeded = RoundUpToFrame(samplesNeeded);

        WriteBufferedSamplesToClip(state, samplesNeeded, allowSilencePadding: true);
    }

    private void WriteBufferedSamplesToClip(SenderState state, int requestedSamples, bool allowSilencePadding)
    {
        var sampleCount = requestedSamples;
        if (!allowSilencePadding)
        {
            sampleCount = Math.Min(sampleCount, state.BufferedSampleCount);
            sampleCount -= sampleCount % _samplesPerFrame;
            if (sampleCount < _samplesPerFrame)
                return;
        }

        var samples = new float[sampleCount];
        var copied = CopyBufferedSamples(state, samples, sampleCount);

        if (copied == 0 && !allowSilencePadding)
            return;

        if (copied > 0 && copied < _samplesPerFrame)
            return;

        if (!allowSilencePadding && copied < sampleCount)
            sampleCount = copied;

        if (sampleCount != samples.Length)
        {
            var trimmed = new float[sampleCount];
            Array.Copy(samples, trimmed, sampleCount);
            samples = trimmed;
        }

        WriteSamplesIntoRingClip(state, samples);
    }

    private int CopyBufferedSamples(SenderState state, float[] destination, int requestedSamples)
    {
        var copied = 0;

        while (copied < requestedSamples && state.BufferedFrames.Count > 0)
        {
            var frame = state.BufferedFrames.Peek();
            var availableInFrame = frame.Length - state.BufferedFrameOffset;
            var copyCount = Math.Min(requestedSamples - copied, availableInFrame);
            Array.Copy(frame, state.BufferedFrameOffset, destination, copied, copyCount);

            copied += copyCount;
            state.BufferedFrameOffset += copyCount;
            state.BufferedSampleCount -= copyCount;

            if (state.BufferedFrameOffset >= frame.Length)
            {
                state.BufferedFrames.Dequeue();
                state.BufferedFrameOffset = 0;
            }
        }

        return copied;
    }

    private void WriteSamplesIntoRingClip(SenderState state, float[] samples)
    {
        if (samples.Length == 0)
            return;

        var clipSamples = state.PlaybackClip.samples;
        var writePosition = state.WriteSamplePosition;
        var firstSegmentLength = Math.Min(samples.Length, clipSamples - writePosition);
        if (firstSegmentLength > 0)
        {
            var firstSegment = new float[firstSegmentLength];
            Array.Copy(samples, 0, firstSegment, 0, firstSegmentLength);
            state.PlaybackClip.SetData(new Il2CppStructArray<float>(firstSegment), writePosition);
        }

        var secondSegmentLength = samples.Length - firstSegmentLength;
        if (secondSegmentLength > 0)
        {
            var secondSegment = new float[secondSegmentLength];
            Array.Copy(samples, firstSegmentLength, secondSegment, 0, secondSegmentLength);
            state.PlaybackClip.SetData(new Il2CppStructArray<float>(secondSegment), 0);
        }

        state.WriteSamplePosition = (writePosition + samples.Length) % clipSamples;
    }

    private void ApplyAudioSourceSettings()
    {
        lock (_sendersLock)
        {
            foreach (var state in _senders.Values)
            {
                if (state.AudioSource == null)
                    continue;

                ConfigureAudioSource(state.AudioSource);
            }
        }
    }

    private void ConfigureAudioSource(AudioSource source)
    {
        source.playOnAwake = false;
        source.loop = false;
        source.spatialBlend = 1f;
        source.rolloffMode = AudioRolloffMode.Linear;
        source.minDistance = _minDistance;
        source.maxDistance = _maxDistance;
        source.dopplerLevel = 0f;
    }

    private void CleanupSenderState(SenderState state)
    {
        state.Dispose();

        if (state.AudioSource != null)
        {
            state.AudioSource.Stop();
            Destroy(state.AudioSource);
            state.AudioSource = null;
        }

        if (state.PlaybackClip != null)
        {
            Destroy(state.PlaybackClip);
            state.PlaybackClip = null;
        }

        state.BufferedFrames.Clear();
        state.BufferedFrameOffset = 0;
        state.BufferedSampleCount = 0;
        state.WriteSamplePosition = 0;
        state.IsPlaying = false;
    }

    private int RoundUpToFrame(int sampleCount)
    {
        var remainder = sampleCount % _samplesPerFrame;
        return remainder == 0 ? sampleCount : sampleCount + (_samplesPerFrame - remainder);
    }

    private static int GetRingDistance(int from, int to, int length)
    {
        var distance = to - from;
        return distance < 0 ? distance + length : distance;
    }
}