using System.Runtime.ExceptionServices;
using System.Buffers.Binary;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace VRCTTS.Services;

public sealed record AudioDevice(string Id, string Name)
{
    public override string ToString() => Name;
}

/// <summary>Decodes once in memory and plays independent copies to selected render endpoints.</summary>
public sealed class AudioRouter
{
    public static List<AudioDevice> GetOutputDevices()
    {
        using var enumerator = new MMDeviceEnumerator();
        var result = new List<AudioDevice>();
        using var endpoints = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
        foreach (var endpoint in endpoints)
        {
            using (endpoint)
                result.Add(new AudioDevice(endpoint.ID, endpoint.FriendlyName));
        }
        return result.OrderBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    public static bool IsVirtualCable(AudioDevice device) =>
        device.Name.Contains("VB-Audio Virtual Cable", StringComparison.OrdinalIgnoreCase) ||
        device.Name.StartsWith("CABLE Input", StringComparison.OrdinalIgnoreCase) ||
        device.Name.StartsWith("CABLE-A Input", StringComparison.OrdinalIgnoreCase) ||
        device.Name.StartsWith("CABLE-B Input", StringComparison.OrdinalIgnoreCase) ||
        device.Name.StartsWith("CABLE-C Input", StringComparison.OrdinalIgnoreCase) ||
        device.Name.StartsWith("CABLE-D Input", StringComparison.OrdinalIgnoreCase);

    public Task PlayAsync(byte[] mp3, string? headphoneId, string? cableId,
        float headphoneVolume, float cableVolume, CancellationToken token,
        Func<CancellationToken, Task>? beforePlayback = null)
    {
        ArgumentNullException.ThrowIfNull(mp3);
        token.ThrowIfCancellationRequested();
        if (mp3.Length == 0) throw new ArgumentException("The speech audio is empty.", nameof(mp3));
        ValidateVolume(headphoneVolume, nameof(headphoneVolume));
        ValidateVolume(cableVolume, nameof(cableVolume));

        var targets = new List<(string Id, float Volume)>();
        if (!string.IsNullOrWhiteSpace(headphoneId)) targets.Add((headphoneId, headphoneVolume));
        // Playing the same endpoint twice doubles the signal. Keep a single copy at the higher volume.
        if (!string.IsNullOrWhiteSpace(cableId))
        {
            if (targets.Count > 0 && string.Equals(headphoneId, cableId, StringComparison.Ordinal))
                targets[0] = (cableId, Math.Max(headphoneVolume, cableVolume));
            else targets.Add((cableId, cableVolume));
        }
        if (targets.Count == 0)
            throw new InvalidOperationException("Choose headphones or a virtual microphone output first.");

        // Media Foundation/COM and player callbacks stay off the WPF synchronization context.
        return Task.Run(() => PlayCoreAsync(mp3, targets, token, beforePlayback), token);
    }

    private static void ValidateVolume(float value, string name)
    {
        if (!float.IsFinite(value) || value < 0 || value > 1)
            throw new ArgumentOutOfRangeException(name, "Volume must be between 0 and 1.");
    }

    private static async Task PlayCoreAsync(byte[] encoded, List<(string Id, float Volume)> targets,
        CancellationToken token, Func<CancellationToken, Task>? beforePlayback)
    {
        var (pcm, format) = DecodeAudio(encoded, token);

        var routes = new List<Route>();
        var failure = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        Exception? primaryFailure = null;
        using var enumerator = new MMDeviceEnumerator();
        try
        {
            foreach (var target in targets)
            {
                token.ThrowIfCancellationRequested();
                // Every requested endpoint must initialize before either starts making sound.
                routes.Add(Route.Create(enumerator, target.Id, target.Volume, pcm, format, failure));
            }
            token.ThrowIfCancellationRequested();
            if (beforePlayback is not null)
                await beforePlayback(token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            foreach (var route in routes) route.Player.Play();

            var completed = Task.WhenAll(routes.Select(r => r.Completed.Task));
            await Task.WhenAny(completed, failure.Task).WaitAsync(token).ConfigureAwait(false);
            if (failure.Task.IsCompletedSuccessfully)
                ExceptionDispatchInfo.Capture(failure.Task.Result).Throw();
            await completed.WaitAsync(token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
        }
        catch (Exception ex)
        {
            primaryFailure = ex;
            throw;
        }
        finally
        {
            // Stop the other endpoint too on cancellation, unplugging, or a playback failure.
            Exception? cleanupFailure = null;
            foreach (var route in routes)
            {
                try { route.Dispose(); }
                catch (Exception ex) { cleanupFailure ??= ex; }
            }
            if (primaryFailure is null && cleanupFailure is not null)
                ExceptionDispatchInfo.Capture(cleanupFailure).Throw();
        }
    }

    internal static (byte[] Data, WaveFormat Format) DecodeAudio(byte[] encoded, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(encoded);
        token.ThrowIfCancellationRequested();
        if (encoded.Length >= 12 && encoded.AsSpan(0, 4).SequenceEqual("RIFF"u8) &&
            encoded.AsSpan(8, 4).SequenceEqual("WAVE"u8))
        {
            using var input = new MemoryStream(encoded, writable: false);
            using var wave = new WaveFileReader(input);
            if (CanReadWaveDirectly(wave))
            {
                if ((long)BinaryPrimitives.ReadUInt32LittleEndian(encoded.AsSpan(4, 4)) + 8 > encoded.LongLength)
                    throw new InvalidDataException("The WAV container is truncated.");
                var decoded = ReadDecodedAudio(wave, token, wave.Length);
                if (decoded.Data.LongLength != wave.Length)
                    throw new InvalidDataException("The WAV audio data is truncated.");
                return decoded;
            }
        }

        // Compressed WAV and online MP3 retain the existing Windows decoder path.
        using var compressedInput = new MemoryStream(encoded, writable: false);
        using var decoder = new StreamMediaFoundationReader(compressedInput);
        return ReadDecodedAudio(decoder, token);
    }

    private static bool CanReadWaveDirectly(WaveFileReader wave)
    {
        if (wave.WaveFormat.Encoding is not (WaveFormatEncoding.Pcm or WaveFormatEncoding.IeeeFloat or WaveFormatEncoding.Extensible))
            return false;
        try
        {
            // This creates an adapter without reading/converting any samples. It also recognizes
            // PCM/float extensible WAV formats that the playback volume adapter supports.
            _ = wave.ToSampleProvider();
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return false;
        }
    }

    private static (byte[] Data, WaveFormat Format) ReadDecodedAudio(WaveStream decoder, CancellationToken token, long? knownLength = null)
    {
        const long maximumBytes = 64 * 1024 * 1024;
        if (knownLength > maximumBytes)
            throw new InvalidOperationException("The speech audio is unexpectedly long.");
        var format = decoder.WaveFormat;
        if (format.BlockAlign <= 0)
            throw new InvalidDataException("The speech audio format has an invalid frame size.");
        using var decoded = new MemoryStream();
        // PCM24, for example, has a three-byte frame. WaveFileReader requires aligned reads.
        var bufferSize = Math.Max(format.BlockAlign, 16384 / format.BlockAlign * format.BlockAlign);
        var buffer = new byte[bufferSize];
        int read;
        while ((read = decoder.Read(buffer, 0, buffer.Length)) != 0)
        {
            token.ThrowIfCancellationRequested();
            if (decoded.Length + read > maximumBytes)
                throw new InvalidOperationException("The speech audio is unexpectedly long.");
            decoded.Write(buffer, 0, read);
        }
        token.ThrowIfCancellationRequested();
        if (decoded.Length == 0)
            throw new InvalidOperationException("The speech service returned no playable audio.");
        if (decoded.Length % format.BlockAlign != 0)
            throw new InvalidDataException("The speech audio ends with an incomplete frame.");
        return (decoded.ToArray(), format);
    }

    private sealed class Route : IDisposable
    {
        private readonly MMDevice device;
        private readonly RawSourceWaveStream source;
        public WasapiPlayer Player { get; }
        public TaskCompletionSource Completed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        private Route(MMDevice device, RawSourceWaveStream source, WasapiPlayer player,
            TaskCompletionSource<Exception> failure)
        {
            this.device = device;
            this.source = source;
            Player = player;
            var deviceName = device.FriendlyName;
            Player.PlaybackStopped += (_, args) =>
            {
                if (args.Exception is not null)
                    failure.TrySetResult(new InvalidOperationException(
                        $"Audio stopped on {deviceName}. Reconnect it and refresh devices.", args.Exception));
                Completed.TrySetResult();
            };
        }

        public static Route Create(MMDeviceEnumerator enumerator, string id, float volume,
            byte[] pcm, WaveFormat format, TaskCompletionSource<Exception> failure)
        {
            MMDevice? device = null;
            RawSourceWaveStream? source = null;
            WasapiPlayer? player = null;
            try
            {
                device = enumerator.GetDevice(id);
                if (device.State != DeviceState.Active)
                    throw new InvalidOperationException("The selected audio device is disconnected or disabled.");
                source = new RawSourceWaveStream(new MemoryStream(pcm, writable: false), format);
                var scaled = new VolumeSampleProvider(source.ToSampleProvider()) { Volume = volume };
                player = new WasapiPlayerBuilder().WithDevice(device).WithSharedMode()
                    .WithEventSync().WithLatency(80).WithMmcssThreadPriority("Audio").Build();
                player.Init(scaled.ToWaveProvider());
                return new Route(device, source, player, failure);
            }
            catch (Exception ex)
            {
                var name = device?.FriendlyName ?? "the selected audio output";
                player?.Dispose();
                source?.Dispose();
                device?.Dispose();
                throw new InvalidOperationException($"Could not open {name}. Refresh devices and select an available output.", ex);
            }
        }

        public void Dispose()
        {
            try { Player.Dispose(); }
            finally
            {
                source.Dispose();
                device.Dispose();
            }
        }
    }
}
