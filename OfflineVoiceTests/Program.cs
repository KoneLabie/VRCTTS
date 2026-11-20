using System.Speech.Synthesis;
using System.Windows.Threading;
using VRCTTS.Services;

var checks = 0;
void Check(bool condition, string name)
{
    if (!condition) throw new Exception("FAILED: " + name);
    checks++;
    Console.WriteLine("PASS " + name);
}
async Task ThrowsAsync<T>(Func<Task> action, string name) where T : Exception
{
    try { await action(); }
    catch (T) { Check(true, name); return; }
    throw new Exception("FAILED (expected " + typeof(T).Name + "): " + name);
}

Check(OfflineJustinService.IsJustinIdentity("IVONA 2 Justin22", "IVONA 2 Justin", "en-US", "IVONA Software Sp. z o.o."), "Standard IVONA Justin token identity accepted");
Check(OfflineJustinService.IsJustinIdentity(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Speech\Voices\Tokens\IVONA 2 Justin22", "IVONA 2 Justin", "en-US", "IVONA Software"), "Registry-qualified Justin token identity accepted");
Check(!OfflineJustinService.IsJustinIdentity("TTS_MS_EN-US_DAVID_11.0", "Microsoft David Desktop", "en-US", "Microsoft"), "David cannot be mistaken for offline Justin");
Check(!OfflineJustinService.IsJustinIdentity("IVONA 2 Justine22", "IVONA 2 Justine", "en-US", "IVONA"), "Another similarly named voice is rejected");
Check(!OfflineJustinService.IsJustinIdentity("TTS_MS_EN-US_DAVID_11.0", "IVONA 2 Justin", "en-US", "IVONA"), "Display name alone cannot authorize a mismatched token");
Check(!OfflineJustinService.IsJustinIdentity("IVONA 2 Justin22", "IVONA 2 Justin", "en-US", "Other vendor"), "Conflicting vendor identity is rejected");
Check(!OfflineJustinService.IsJustinIdentity("IVONA 2 Justin22", "IVONA 2 Justin", "en-GB", "IVONA"), "Wrong voice locale is rejected");
Check(OfflineJustinService.IsJustinIdentity("Justin RSI Harpo 22kHz", "Justin RSI Harpo 22kHz", "en-US", "Harpo Sp. z o.o."), "Official signed Harpo Justin token accepted");
Check(OfflineJustinService.IsJustinIdentity(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Speech\Voices\Tokens\Justin RSI Harpo 22kHz", "Justin", "en-US", null), "Documented Harpo token permits short Justin display name with no vendor attribute");
Check(new[] { "IVONA Software Sp. z o.o.", "Harpo", "ReadSpeaker", "RSI Harpo" }.All(vendor =>
    OfflineJustinService.IsJustinIdentity("Justin RSI Harpo 22kHz", "IVONA 2 Justin", "en-US", vendor)), "Documented Harpo token permits the original engine vendor identities");
Check(!OfflineJustinService.IsJustinIdentity("Some other Justin", "Justin", "en-US", "Harpo"), "Short display name never authorizes an unrelated Justin token");
Check(!OfflineJustinService.IsJustinIdentity("Justin RSI Harpo 22kHz modified", "Justin", "en-US", "Harpo"), "Harpo token suffix variants cannot masquerade as the documented token");
Check(!OfflineJustinService.IsJustinIdentity("Justin RSI Harpo 22kHz", "Microsoft David Desktop", "en-US", "Harpo"), "Documented Harpo token still rejects a conflicting display name");
Check(!OfflineJustinService.IsJustinIdentity("Justin RSI Harpo 22kHz", "Justin", "en-US", "Other vendor"), "Documented Harpo token rejects a conflicting vendor");
Check(!OfflineJustinService.IsJustinIdentity("Justin RSI Harpo 22kHz", "Justin", "en-US", "Harpotron"), "Similar vendor prefixes do not authorize the Harpo token");
Check(!OfflineJustinService.IsJustinIdentity("Justin RSI Harpo 22kHz", "Justin", "en-GB", "Harpo"), "Documented Harpo token requires English US locale");

using (var service = new OfflineJustinService())
{
    var voices = service.GetVoices();
    Check(voices.All(v => v.Name.Contains("Justin", StringComparison.OrdinalIgnoreCase)
        && (v.Name.Contains("IVONA", StringComparison.OrdinalIgnoreCase)
            || v.Id.Replace('/', '\\').Split('\\').Last().Equals("Justin RSI Harpo 22kHz", StringComparison.OrdinalIgnoreCase))), "App selector exposes only the original IVONA or documented Harpo Justin");
    Console.WriteLine($"Installed IVONA Justin tokens: {voices.Count}; enabled: {voices.Count(v => v.Enabled)}");
    await ThrowsAsync<ArgumentNullException>(() => service.SynthesizeAsync(null!, CancellationToken.None), "Null message rejected");
    await ThrowsAsync<ArgumentException>(() => service.SynthesizeAsync("  ", CancellationToken.None), "Empty message rejected");
    await ThrowsAsync<ArgumentException>(() => service.SynthesizeAsync("hello\0world", CancellationToken.None), "Embedded NUL rejected");
    await ThrowsAsync<ArgumentOutOfRangeException>(() => service.SynthesizeAsync(new string('a', 5001), CancellationToken.None), "5001-character offline message rejected");
    using var canceled = new CancellationTokenSource();
    canceled.Cancel();
    await ThrowsAsync<OperationCanceledException>(() => service.SynthesizeAsync("Hello.", canceled.Token), "Pre-canceled operation never synthesizes");
    if (!voices.Any(v => v.Enabled))
    {
        await ThrowsAsync<InvalidOperationException>(() => service.SynthesizeAsync("Hello from offline Justin.", CancellationToken.None), "Missing or disabled Justin refuses speech without selecting David");
        await ThrowsAsync<InvalidOperationException>(() => service.SynthesizeAsync(new string('a', 5000), CancellationToken.None), "5000-character message passes length validation and reaches missing-Justin check");
        Check(service.GetVoices().Count == voices.Count, "Missing-voice failure leaves service refreshable");
    }
    else
    {
        var wav = await service.SynthesizeAsync("Hello from offline Justin.", CancellationToken.None);
        Check(ValidWav(wav), "Installed Justin returns a complete in-memory WAV");
    }
    service.Dispose();
    service.Dispose();
    await ThrowsAsync<ObjectDisposedException>(() => service.SynthesizeAsync("Hello.", CancellationToken.None), "Disposed service refuses synthesis");
    try { service.GetVoices(); throw new Exception("FAILED: disposed voice inventory"); }
    catch (ObjectDisposedException) { Check(true, "Disposed service refuses voice inventory"); }
}

// Separate SDK-plumbing smoke test: David is used only in this test and never by
// OfflineJustinService. Both syntheses write exclusively to MemoryStreams.
var smoke = await SapiBindingSmokeAsync().WaitAsync(TimeSpan.FromSeconds(15));
Check(smoke.ShortWaveBytes > 44, "Raw test-only SAPI voice produces WAV in memory on STA worker");
Check(smoke.Canceled, "Raw test-only SAPI async cancellation completes before output release");
Console.WriteLine($"SAPI smoke voice={smoke.Voice}, shortWavBytes={smoke.ShortWaveBytes}, cancellationConfirmed={smoke.Canceled}; no audio devices opened and no speech saved.");
Console.WriteLine($"All {checks} offline voice checks passed.");

static bool ValidWav(byte[] wav) => wav.Length > 44 && wav.AsSpan(0, 4).SequenceEqual("RIFF"u8) && wav.AsSpan(8, 4).SequenceEqual("WAVE"u8);

static Task<SmokeResult> SapiBindingSmokeAsync()
{
    var result = new TaskCompletionSource<SmokeResult>(TaskCreationOptions.RunContinuationsAsynchronously);
    var thread = new Thread(() =>
    {
        var dispatcher = Dispatcher.CurrentDispatcher;
        SpeechSynthesizer? synth = null;
        MemoryStream? output = null;
        System.Threading.Timer? timer = null;
        var phase = 0;
        var shortWaveBytes = 0;
        var voiceName = "";
        void Fail(Exception ex)
        {
            result.TrySetException(ex);
            dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
        }
        try
        {
            synth = new SpeechSynthesizer();
            var voice = synth.GetInstalledVoices().FirstOrDefault(v => v.Enabled && v.VoiceInfo.Name.Equals("Microsoft David Desktop", StringComparison.Ordinal));
            if (voice is null) throw new InvalidOperationException("The separate SAPI binding smoke test requires installed Microsoft David Desktop.");
            voiceName = voice.VoiceInfo.Name;
            synth.SelectVoice(voiceName);
            output = new MemoryStream();
            synth.SetOutputToWaveStream(output);
            synth.SpeakCompleted += (_, args) => dispatcher.BeginInvoke(DispatcherPriority.Send, new Action(() =>
            {
                try
                {
                    var canceled = args.Cancelled || args.Error is OperationCanceledException;
                    if (args.Error is not null && !canceled) throw args.Error;
                    synth.SetOutputToNull();
                    if (phase == 0)
                    {
                        var wav = output.ToArray();
                        if (!ValidWav(wav)) throw new InvalidOperationException("SAPI did not produce a valid WAV stream.");
                        shortWaveBytes = wav.Length;
                        output.Dispose();
                        output = new MemoryStream();
                        synth.SetOutputToWaveStream(output);
                        phase = 1;
                        synth.SpeakAsync(string.Concat(Enumerable.Repeat("This long test is canceled without playing audio. ", 4000)));
                        timer = new System.Threading.Timer(_ => dispatcher.BeginInvoke(DispatcherPriority.Send, new Action(synth.SpeakAsyncCancelAll)), null, 20, Timeout.Infinite);
                    }
                    else
                    {
                        if (!canceled) throw new InvalidOperationException("The long test completed without confirming cancellation.");
                        output.Dispose();
                        output = null;
                        result.TrySetResult(new(voiceName, shortWaveBytes, true));
                        dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
                    }
                }
                catch (Exception ex) { Fail(ex); }
            }));
            synth.SpeakAsync("This is an in-memory voice binding test.");
            Dispatcher.Run();
        }
        catch (Exception ex) { result.TrySetException(ex); }
        finally
        {
            timer?.Dispose();
            synth?.Dispose();
            output?.Dispose();
        }
    }) { IsBackground = true, Name = "Test-only SAPI binding (STA)" };
    thread.SetApartmentState(ApartmentState.STA);
    thread.Start();
    return result.Task;
}

internal sealed record SmokeResult(string Voice, int ShortWaveBytes, bool Canceled);
