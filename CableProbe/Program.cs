using System.Runtime.InteropServices;
using System.Diagnostics;
using NAudio.CoreAudioApi;
using NAudio.Wave;

const string AdapterName = "VB-Audio Virtual Cable";
const int SampleRate = 24000;
const double Frequency = 440;
const double PeakAmplitude = 0.01;

try
{
    using var enumerator = new MMDeviceEnumerator();
    using var endpoints = enumerator.EnumerateAudioEndPoints(DataFlow.All, DeviceState.Active);
    var known = new List<MMDevice>();
    foreach (var endpoint in endpoints)
    {
        if (endpoint.DeviceFriendlyName.Equals(AdapterName, StringComparison.OrdinalIgnoreCase))
        {
            known.Add(endpoint);
            Console.WriteLine($"BASE-CABLE flow={endpoint.DataFlow} name={endpoint.FriendlyName} adapter={endpoint.DeviceFriendlyName} formFactor={FormFactor(endpoint)} id={endpoint.ID}");
        }
        else endpoint.Dispose();
    }
    try
    {
        var renders = known.Where(d => d.DataFlow == DataFlow.Render && FormFactor(d) == 1).ToArray();
        var captures = known.Where(d => d.DataFlow == DataFlow.Capture).ToArray();
        if (renders.Length != 1 || captures.Length != 1)
            throw new InvalidOperationException($"Refusing audio: expected exactly one known base-cable Speakers/formFactor1 render and one base-cable capture; found {renders.Length} renders and {captures.Length} captures.");
        var render = renders[0];
        var capture = captures[0];
        Console.WriteLine("SELECTED RENDER " + render.ID);
        Console.WriteLine("SELECTED CAPTURE " + capture.ID);
        if (!args.Contains("--run", StringComparer.Ordinal))
        {
            Console.WriteLine("Inventory only. Use --run for a bounded low-level 440Hz virtual-cable signal test. No physical microphone or headphones are opened.");
            return 0;
        }

        var vrchatProcesses = Process.GetProcessesByName("VRChat");
        try
        {
            if (vrchatProcesses.Length != 0)
            {
                Console.WriteLine("SKIP live signal: VRChat is running. Close VRChat before using --run to avoid sending a test tone into a room.");
                return 0;
            }
        }
        finally { foreach (var process in vrchatProcesses) process.Dispose(); }

        // Both endpoints are explicitly verified as the basic virtual cable. Never use defaults,
        // loopback capture, physical microphones, headphones, OSC, files, or endpoint volume APIs.
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var recorder = new WasapiRecorderBuilder().WithDevice(capture).WithSharedMode()
            .WithEventSync().WithBufferLength(40).WithFormat(WaveFormat.CreateIeeeFloatWaveFormat(SampleRate, 1)).Build();
        await using var player = new WasapiPlayerBuilder().WithDevice(render).WithSharedMode()
            .WithEventSync().WithLatency(60).Build();
        using var captured = new MemoryStream();
        var captureLock = new object();
        var captureStopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var playbackStopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Exception? captureError = null;
        Exception? playbackError = null;
        recorder.DataAvailable += (buffer, flags, _, _) =>
        {
            lock (captureLock)
            {
                if (captured.Length + buffer.Length > 2 * 1024 * 1024) return;
                if ((flags & AudioClientBufferFlags.Silent) != 0) captured.Write(new byte[buffer.Length]);
                else captured.Write(buffer);
            }
        };
        recorder.RecordingStopped += (_, eventArgs) => { captureError = eventArgs.Exception; captureStopped.TrySetResult(); };
        player.PlaybackStopped += (_, eventArgs) => { playbackError = eventArgs.Exception; playbackStopped.TrySetResult(); };

        var tone = BuildTone(SampleRate, Frequency, PeakAmplitude, .8);
        using var source = new RawSourceWaveStream(new MemoryStream(tone, writable: false), new WaveFormat(SampleRate, 16, 1));
        player.Init(source);
        Console.WriteLine($"TEST tone={Frequency}Hz peak={PeakAmplitude:F3} (-40dBFS), source={SampleRate}Hz PCM16 mono, duration=0.8s");
        Console.WriteLine("CAPTURE FORMAT " + recorder.WaveFormat);
        recorder.StartRecording();
        try
        {
            await Task.Delay(350, deadline.Token);
            player.Play();
            await playbackStopped.Task.WaitAsync(deadline.Token);
            if (playbackError is not null) throw new InvalidOperationException("Virtual-cable playback failed.", playbackError);
            await Task.Delay(350, deadline.Token);
        }
        finally
        {
            player.Stop();
            recorder.StopRecording();
            await captureStopped.Task.WaitAsync(TimeSpan.FromSeconds(1));
        }
        if (captureError is not null) throw new InvalidOperationException("Virtual-cable capture failed.", captureError);
        byte[] bytes;
        lock (captureLock) bytes = captured.ToArray();
        if (recorder.WaveFormat.SampleRate != SampleRate || recorder.WaveFormat.Channels != 1 || recorder.WaveFormat.BitsPerSample != 32)
            throw new InvalidOperationException("The recorder did not honor the requested float32 mono capture format.");
        var samples = MemoryMarshal.Cast<byte, float>(bytes).ToArray();
        if (samples.Length < SampleRate) throw new InvalidOperationException("Too few virtual-cable samples were captured to verify the signal.");
        if (samples.Any(s => !float.IsFinite(s))) throw new InvalidOperationException("Non-finite samples were captured.");

        var baselineSize = Math.Min(samples.Length, SampleRate / 5);
        var baseline = Analyze(samples.AsSpan(0, baselineSize), SampleRate, Frequency);
        var window = SampleRate / 2;
        var best = new Spectrum(0, 0, 0, 0);
        var bestOffset = 0;
        for (var offset = SampleRate / 4; offset + window <= samples.Length; offset += SampleRate / 20)
        {
            var spectrum = Analyze(samples.AsSpan(offset, window), SampleRate, Frequency);
            if (spectrum.TargetAmplitude > best.TargetAmplitude) { best = spectrum; bestOffset = offset; }
        }
        var signalFraction = best.Rms <= 0 ? 0 : best.TargetAmplitude * best.TargetAmplitude / 2 / (best.Rms * best.Rms);
        var neighborRatio = best.TargetAmplitude / Math.Max(1e-12, Math.Max(best.LowerAmplitude, best.UpperAmplitude));
        Console.WriteLine($"MEASURE capturedSamples={samples.Length} capturedBytes={bytes.Length} bestOffsetSeconds={(double)bestOffset / SampleRate:F3} rms={best.Rms:G7} amplitude440={best.TargetAmplitude:G7} baseline440={baseline.TargetAmplitude:G7} signalEnergyFraction={signalFraction:F6} targetToNeighborRatio={neighborRatio:F2}");
        if (best.TargetAmplitude < 0.0001 || best.TargetAmplitude < baseline.TargetAmplitude * 8 || signalFraction < .8 || neighborRatio < 10)
            throw new InvalidOperationException("The expected 440Hz test signal did not traverse the virtual cable strongly enough to verify audio routing.");
        Console.WriteLine("PASS: low-level 440Hz audio sent only to the known base-cable render was recovered from its capture endpoint. No drivers, names, defaults, or volumes were changed; no captured audio was saved.");
        return 0;
    }
    finally { foreach (var endpoint in known) endpoint.Dispose(); }
}
catch (Exception ex)
{
    Console.Error.WriteLine("FAIL " + ex);
    return 1;
}

static int FormFactor(MMDevice endpoint) => endpoint.Properties.TryGetValue<uint>(PropertyKeys.PKEY_AudioEndpoint_FormFactor, out var value) ? checked((int)value) : -1;

static byte[] BuildTone(int sampleRate, double frequency, double amplitude, double seconds)
{
    var frames = (int)(sampleRate * seconds);
    var bytes = new byte[frames * 2];
    for (var frame = 0; frame < frames; frame++)
    {
        // A short fade avoids abrupt edges even though this output is a virtual cable.
        var fade = Math.Min(1, Math.Min(frame / (sampleRate * .01), (frames - 1 - frame) / (sampleRate * .01)));
        var value = (short)Math.Round(short.MaxValue * amplitude * fade * Math.Sin(2 * Math.PI * frequency * frame / sampleRate));
        bytes[frame * 2] = (byte)(value & 255);
        bytes[frame * 2 + 1] = (byte)((value >> 8) & 255);
    }
    return bytes;
}

static Spectrum Analyze(ReadOnlySpan<float> samples, int sampleRate, double frequency)
{
    double energy = 0;
    foreach (var sample in samples) energy += (double)sample * sample;
    return new(Math.Sqrt(energy / samples.Length), Amplitude(samples, sampleRate, frequency),
        Amplitude(samples, sampleRate, frequency - 20), Amplitude(samples, sampleRate, frequency + 20));
}

static double Amplitude(ReadOnlySpan<float> samples, int sampleRate, double frequency)
{
    double sine = 0, cosine = 0;
    for (var index = 0; index < samples.Length; index++)
    {
        var phase = 2 * Math.PI * frequency * index / sampleRate;
        sine += samples[index] * Math.Sin(phase);
        cosine += samples[index] * Math.Cos(phase);
    }
    return 2 * Math.Sqrt(sine * sine + cosine * cosine) / samples.Length;
}

internal readonly record struct Spectrum(double Rms, double TargetAmplitude, double LowerAmplitude, double UpperAmplitude);
