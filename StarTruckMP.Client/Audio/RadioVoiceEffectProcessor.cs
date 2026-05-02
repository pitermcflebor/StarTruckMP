using System;
using NWaves.Filters.Base;
using NWaves.Filters.Fda;
using NWaves.Operations;
using NWaves.Signals;
using BandPassFilter = NWaves.Filters.Butterworth.BandPassFilter;
using Random = System.Random;

namespace StarTruckMP.Client.Audio;

public sealed class RadioVoiceEffectProcessor
{
    private const float CompressorThresholdDb = -24f;
    private const float CompressorRatio = 3.5f;
    private const float CompressorAttackSeconds = 0.02f;
    private const float CompressorReleaseSeconds = 0.18f;
    private const float NoiseGateThresholdDb = -42f;
    private const float NoiseGateRatio = 8f;
    private const float NoiseGateAttackSeconds = 0.003f;
    private const float NoiseGateReleaseSeconds = 0.08f;
    private const float CrackleActivityThreshold = 0.018f;
    private const float CrackleBurstsPerSecond = 2.5f;
    private const float CrackleMinAmplitude = 0.008f;
    private const float CrackleMaxAmplitude = 0.02f;

    private readonly int _sampleRate;
    private readonly FilterChain _bandpass;
    private readonly DynamicsProcessor _compressor;
    private readonly DynamicsProcessor _noiseGate;
    private readonly Random _random = new();
    //private readonly DistortionEffect _distortion;

    public RadioVoiceEffectProcessor(int sampleRate = 48000, float outputGain = 1f)
    {
        _sampleRate = sampleRate;

        var lowNorm = 500.0 / sampleRate;
        var highNorm = 3400.0 / sampleRate;

        var tf = new BandPassFilter(lowNorm, highNorm, 4).Tf;
        var sos = DesignFilter.TfToSos(tf);
        _bandpass = new FilterChain(sos);
        _compressor = new DynamicsProcessor(
            DynamicsMode.Compressor,
            sampleRate,
            CompressorThresholdDb,
            CompressorRatio,
            outputGain,
            CompressorAttackSeconds,
            CompressorReleaseSeconds);
        _noiseGate = new DynamicsProcessor(
            DynamicsMode.NoiseGate,
            sampleRate,
            NoiseGateThresholdDb,
            NoiseGateRatio,
            0f,
            NoiseGateAttackSeconds,
            NoiseGateReleaseSeconds);

        //_distortion = new DistortionEffect(DistortionMode.SoftClipping, 20f, -12f);
        //_distortion.WetDryMix(.6f, MixingRule.Linear);
    }

    public float[] Process(float[] pcmSamples)
    {
        var signal = new DiscreteSignal(_sampleRate, pcmSamples);

        // 1. Bandpass
        var filtered = _bandpass.ApplyTo(signal);

        // 2. vintage-style compression
        var compressed = _compressor.ApplyTo(filtered);

        // 3. kill the constant hiss brought up by compression
        var gated = _noiseGate.ApplyTo(compressed);

        // 4. CB radio crackle should be intermittent, not a constant hiss
        var samples = gated.Samples;
        AddRfCrackle(samples);

        // 5. distortion
        //var distorted = _distortion.ApplyTo(filtered);

        return samples;
    }

    private void AddRfCrackle(float[] samples)
    {
        if (samples.Length == 0)
            return;

        var peak = 0f;
        for (var i = 0; i < samples.Length; i++)
        {
            var magnitude = MathF.Abs(samples[i]);
            if (magnitude > peak)
                peak = magnitude;
        }

        if (peak < CrackleActivityThreshold)
            return;

        var burstProbability = CrackleBurstsPerSecond * samples.Length / _sampleRate;
        if (_random.NextDouble() >= burstProbability)
            return;

        var burstStart = _random.Next(samples.Length);
        var burstLength = Math.Min(samples.Length - burstStart, _random.Next(6, 18));
        var burstAmplitude = CrackleMinAmplitude + (float)_random.NextDouble() * (CrackleMaxAmplitude - CrackleMinAmplitude);

        for (var i = 0; i < burstLength; i++)
        {
            var envelope = 1f - i / (float)burstLength;
            var polarity = _random.NextDouble() > 0.5 ? 1f : -1f;
            var crackle = polarity * burstAmplitude * envelope * (0.35f + (float)_random.NextDouble() * 0.65f);
            samples[burstStart + i] = Math.Clamp(samples[burstStart + i] + crackle, -1f, 1f);
        }
    }
}

