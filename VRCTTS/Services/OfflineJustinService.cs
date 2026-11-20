using System.Speech.Synthesis;
using System.Text.RegularExpressions;
using System.Windows.Threading;

namespace VRCTTS.Services;

public sealed record OfflineVoiceInfo(string Id, string Name, string Culture, bool Enabled);

/// <summary>
/// Synthesizes only an explicitly installed IVONA Justin SAPI5 token into memory.
/// The synthesizer and all of its output resources belong to one reusable STA worker.
/// This class never chooses a default voice or opens an audio output device.
/// </summary>
public sealed class OfflineJustinService : IDisposable
{
    public const int MaximumCharacters = 5000;
    // The signed official Harpo Justin package registers this exact SAPI token
    // for both architectures (statically inspected install_script.iss, 227/229).
    private const string HarpoJustinToken = "Justin RSI Harpo 22kHz";
    private static readonly Regex JustinIdentity = new(@"^IVONA(?:\s*2)?\s+Justin(?:\b|\d)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private readonly TaskCompletionSource<Dispatcher> dispatcherReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly SemaphoreSlim serial = new(1, 1);
    private readonly Thread worker;
    private SpeechSynthesizer? engine;
    private Request? active;
    private bool shutdownRequested;
    private int disposed;

    public OfflineJustinService()
    {
        worker = new Thread(WorkerMain) { IsBackground = true, Name = "Justin offline voice (STA)" };
        worker.SetApartmentState(ApartmentState.STA);
        worker.Start();
    }

    /// <summary>Returns only IVONA Justin voices. An empty list means Justin is not installed for this process architecture.</summary>
    public IReadOnlyList<OfflineVoiceInfo> GetVoices()
    {
        ThrowIfDisposed();
        var dispatcher = dispatcherReady.Task.GetAwaiter().GetResult();
        return dispatcher.Invoke(() =>
        {
            ThrowIfDisposed();
            var synth = GetEngineCore();
            var voices = synth.GetInstalledVoices().Where(v => IsJustin(v.VoiceInfo))
                .Select(v => new OfflineVoiceInfo(v.VoiceInfo.Id, v.VoiceInfo.Name, v.VoiceInfo.Culture.Name, v.Enabled))
                .ToArray();
            // SAPI caches its token inventory. Discard an engine without a usable Justin so a
            // later refresh can detect a newly installed voice without restarting this app.
            if (active is null && !voices.Any(v => v.Enabled)) DisposeEngineCore();
            return (IReadOnlyList<OfflineVoiceInfo>)Array.AsReadOnly(voices);
        });
    }

    public async Task<byte[]> SynthesizeAsync(string text, CancellationToken token)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(text);
        if (string.IsNullOrWhiteSpace(text)) throw new ArgumentException("Type a message to speak.", nameof(text));
        if (text.Contains('\0')) throw new ArgumentException("The message cannot contain null characters.", nameof(text));
        if (text.Length > MaximumCharacters) throw new ArgumentOutOfRangeException(nameof(text), $"Offline messages can contain up to {MaximumCharacters} characters.");
        token.ThrowIfCancellationRequested();
        await serial.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            var dispatcher = await dispatcherReady.Task.ConfigureAwait(false);
            // Do not abandon a queued/started native operation with WaitAsync(token). The
            // worker owns cancellation and completes only after SAPI releases its output.
            var result = await dispatcher.InvokeAsync(() => StartCore(text, token)).Task.ConfigureAwait(false);
            return await result.ConfigureAwait(false);
        }
        finally { serial.Release(); }
    }

    private static bool IsJustin(VoiceInfo voice)
    {
        var vendor = voice.AdditionalInfo.FirstOrDefault(p => p.Key.Equals("Vendor", StringComparison.OrdinalIgnoreCase)).Value;
        return IsJustinIdentity(voice.Id, voice.Name, voice.Culture.Name, vendor);
    }

    internal static bool IsJustinIdentity(string id, string name, string culture, string? vendor)
    {
        var tokenLeaf = id.Replace('/', '\\').Split('\\', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? "";
        if (!culture.Equals("en-US", StringComparison.OrdinalIgnoreCase)) return false;
        if (tokenLeaf.Equals(HarpoJustinToken, StringComparison.OrdinalIgnoreCase))
        {
            // A short display name is accepted only behind the documented exact
            // token ID, never as authorization for an arbitrary voice named Justin.
            if (!(name.Equals("Justin", StringComparison.OrdinalIgnoreCase)
                || name.Equals(HarpoJustinToken, StringComparison.OrdinalIgnoreCase)
                || JustinIdentity.IsMatch(name))) return false;
            return string.IsNullOrWhiteSpace(vendor) || new[] { "IVONA", "Harpo", "ReadSpeaker", "RSI" }
                .Any(prefix => VendorStartsWith(vendor.Trim(), prefix));
        }
        if (!JustinIdentity.IsMatch(name) || !JustinIdentity.IsMatch(tokenLeaf)) return false;
        return string.IsNullOrWhiteSpace(vendor) || vendor.Contains("IVONA", StringComparison.OrdinalIgnoreCase);
    }

    private static bool VendorStartsWith(string vendor, string prefix) =>
        vendor.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
        && (vendor.Length == prefix.Length || char.IsWhiteSpace(vendor[prefix.Length]) || char.IsPunctuation(vendor[prefix.Length]));

    private SpeechSynthesizer GetEngineCore() => engine ??= new SpeechSynthesizer();

    private Task<byte[]> StartCore(string text, CancellationToken token)
    {
        ThrowIfDisposed();
        token.ThrowIfCancellationRequested();
        if (active is not null) throw new InvalidOperationException("The offline voice is still finishing the previous message.");
        var synth = GetEngineCore();
        var justins = synth.GetInstalledVoices().Where(v => IsJustin(v.VoiceInfo)).ToArray();
        var voice = justins.Where(v => v.Enabled).OrderBy(v => v.VoiceInfo.Id, StringComparer.Ordinal).FirstOrDefault();
        if (voice is null)
        {
            DisposeEngineCore();
            throw new InvalidOperationException(justins.Length == 0
                ? $"IVONA 2 Justin is not installed for {(Environment.Is64BitProcess ? "64" : "32")}-bit apps. Install the matching SAPI5 Justin voice, then refresh voice settings. No other voice will be used."
                : "The installed IVONA Justin voice is disabled or unavailable. Check the voice installation and its license or trial, then refresh voice settings.");
        }

        // Select by the engine's actual installed name, then verify the exact token ID.
        // Never use SelectVoiceByHints, the SAPI default, or any alternative named voice.
        try
        {
            synth.SelectVoice(voice.VoiceInfo.Name);
            VerifySelected(synth, voice.VoiceInfo.Id);
        }
        catch (Exception ex)
        {
            DisposeEngineCore();
            throw new InvalidOperationException("Windows could not load the installed IVONA Justin voice. Check that its installation and license or trial are active. " + ex.Message, ex);
        }
        var request = new Request(voice.VoiceInfo.Id, token);
        active = request;
        var dispatcher = Dispatcher.CurrentDispatcher;
        request.Handler = (_, args) =>
        {
            try { dispatcher.BeginInvoke(DispatcherPriority.Send, new Action(() => CompleteCore(request, args, null))); }
            catch (InvalidOperationException) { /* Worker teardown will release resources. */ }
        };
        synth.SpeakCompleted += request.Handler;
        try
        {
            synth.SetOutputToWaveStream(request.Output);
            request.Registration = token.Register(() =>
            {
                try { dispatcher.BeginInvoke(DispatcherPriority.Send, new Action(() => CancelCore(request))); }
                catch (InvalidOperationException) { }
            });
            token.ThrowIfCancellationRequested();
            request.Prompt = synth.SpeakAsync(text);
        }
        catch (Exception ex)
        {
            // If enqueueing itself failed, dispose the engine before its output stream.
            // This also prevents a partially initialized engine from being reused.
            CompleteCore(request, null, ex);
        }
        return request.Completion.Task;
    }

    private static void VerifySelected(SpeechSynthesizer synth, string expectedId)
    {
        if (!synth.Voice.Id.Equals(expectedId, StringComparison.OrdinalIgnoreCase) || !IsJustin(synth.Voice))
            throw new InvalidOperationException("Windows did not select the installed IVONA Justin voice. Speech was stopped instead of using another voice.");
    }

    private void CancelCore(Request request)
    {
        if (!ReferenceEquals(active, request) || engine is null) return;
        request.CancelRequested = true;
        try { engine.SpeakAsyncCancelAll(); }
        catch (Exception ex) { CompleteCore(request, null, ex); }
        // SpeakCompleted is also raised for a canceled prompt. Keep the WAV stream alive
        // until that callback; a cancellation token alone does not stop native synthesis.
    }

    private void CompleteCore(Request request, SpeakCompletedEventArgs? args, Exception? startFailure)
    {
        if (!ReferenceEquals(active, request)) return;
        if (args is not null && request.Prompt is not null && !ReferenceEquals(args.Prompt, request.Prompt)) return;
        byte[]? bytes = null;
        Exception? failure = startFailure ?? args?.Error;
        var canceled = request.Token.IsCancellationRequested || request.CancelRequested || args?.Cancelled == true || shutdownRequested;
        try
        {
            if (engine is not null)
            {
                engine.SpeakCompleted -= request.Handler;
                if (startFailure is not null)
                    DisposeEngineCore(); // Guarantees SAPI is stopped before the stream is disposed.
                else
                {
                    if (!canceled && failure is null) VerifySelected(engine, request.VoiceId);
                    engine.SetOutputToNull(); // Releases/finalizes the RIFF stream after completion.
                }
            }
            if (!canceled && failure is null)
            {
                bytes = request.Output.ToArray();
                if (bytes.Length <= 44 || !bytes.AsSpan(0, 4).SequenceEqual("RIFF"u8)
                    || !bytes.AsSpan(8, 4).SequenceEqual("WAVE"u8))
                    failure = new InvalidOperationException("The installed Justin voice returned no playable WAV audio.");
            }
        }
        catch (Exception ex)
        {
            failure ??= ex;
            try { DisposeEngineCore(); } catch { /* Preserve the original synthesis error. */ }
        }
        finally
        {
            request.Registration.Dispose();
            request.Output.Dispose();
            active = null;
        }
        if (canceled) request.Completion.TrySetCanceled(request.Token.IsCancellationRequested ? request.Token : new CancellationToken(true));
        else if (failure is not null)
            request.Completion.TrySetException(new InvalidOperationException("The offline IVONA Justin voice could not create speech. Check that its installation and license or trial are active. " + failure.Message, failure));
        else request.Completion.TrySetResult(bytes!);
        if (shutdownRequested) ShutdownCore();
    }

    private void WorkerMain()
    {
        try
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            dispatcherReady.TrySetResult(dispatcher);
            Dispatcher.Run();
        }
        catch (Exception ex)
        {
            dispatcherReady.TrySetException(ex);
            active?.Completion.TrySetException(new InvalidOperationException("The offline voice worker stopped unexpectedly.", ex));
        }
        finally
        {
            try { DisposeEngineCore(); }
            finally
            {
                active?.Registration.Dispose();
                active?.Output.Dispose();
                active?.Completion.TrySetCanceled();
                active = null;
            }
        }
    }

    private void DisposeEngineCore()
    {
        var synth = engine;
        engine = null;
        synth?.Dispose();
    }

    private void ShutdownCore()
    {
        shutdownRequested = true;
        if (active is not null) { CancelCore(active); return; }
        DisposeEngineCore();
        Dispatcher.CurrentDispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        if (dispatcherReady.Task.IsCompletedSuccessfully)
        {
            var dispatcher = dispatcherReady.Task.Result;
            try { dispatcher.BeginInvoke(DispatcherPriority.Send, new Action(ShutdownCore)); }
            catch (InvalidOperationException) { }
        }
        else
        {
            // Covers disposal immediately after construction without losing the shutdown request.
            _ = dispatcherReady.Task.ContinueWith(task =>
            {
                if (task.IsCompletedSuccessfully)
                    task.Result.BeginInvoke(DispatcherPriority.Send, new Action(ShutdownCore));
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
        if (Thread.CurrentThread != worker)
            worker.Join(TimeSpan.FromSeconds(10)); // An unresponsive third-party engine retains ownership until it exits.
    }

    private sealed class Request(string voiceId, CancellationToken token)
    {
        public string VoiceId { get; } = voiceId;
        public CancellationToken Token { get; } = token;
        public MemoryStream Output { get; } = new();
        public TaskCompletionSource<byte[]> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public EventHandler<SpeakCompletedEventArgs>? Handler { get; set; }
        public CancellationTokenRegistration Registration { get; set; }
        public Prompt? Prompt { get; set; }
        public bool CancelRequested { get; set; }
    }
}
