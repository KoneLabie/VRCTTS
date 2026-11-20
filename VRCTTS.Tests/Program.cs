using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using VRCTTS.Services;
using NAudio.CoreAudioApi;

var passed = 0;
void Check(bool condition, string name)
{
    if (!condition) throw new Exception("FAILED: " + name);
    passed++;
    Console.WriteLine("PASS " + name);
}
void Throws<T>(Action action, string name) where T : Exception
{
    try { action(); }
    catch (T) { Check(true, name); return; }
    throw new Exception("FAILED (expected " + typeof(T).Name + "): " + name);
}
async Task ThrowsAsync<T>(Func<Task> action, string name) where T : Exception
{
    try { await action(); }
    catch (T) { Check(true, name); return; }
    throw new Exception("FAILED (expected " + typeof(T).Name + "): " + name);
}
string ReadOscString(byte[] packet, ref int offset)
{
    var end = Array.IndexOf(packet, (byte)0, offset);
    if (end < 0) throw new Exception("Missing OSC NUL terminator");
    var value = Encoding.UTF8.GetString(packet, offset, end - offset);
    offset = (end + 4) & ~3;
    return value;
}

var packet = OscChatbox.BuildPacket("/chatbox/input", "héllo 👋 世界", true, false);
var offset = 0;
Check(packet.Length % 4 == 0, "OSC packet 4-byte alignment");
Check(ReadOscString(packet, ref offset) == "/chatbox/input", "OSC input address");
Check(ReadOscString(packet, ref offset) == ",sTF", "OSC bools are tags without payload");
Check(ReadOscString(packet, ref offset) == "héllo 👋 世界" && offset == packet.Length, "OSC UTF-8 round trip and padding");
Check(Convert.ToHexString(OscChatbox.BuildPacket("/x", "a", true, false)) == "2F7800002C7354460000000061000000", "OSC golden byte sequence");
Throws<ArgumentException>(() => OscChatbox.BuildPacket("/x", "a\0b"), "OSC rejects embedded NUL");
Throws<ArgumentException>(() => OscChatbox.BuildPacket("/x", 123), "OSC rejects unsupported argument type");
Throws<ArgumentException>(() => new OscChatbox("192.168.1.1"), "OSC rejects remote destinations");
Throws<ArgumentException>(() => new OscChatbox("example.com"), "OSC rejects DNS destinations");
Throws<ArgumentOutOfRangeException>(() => new OscChatbox(port: 0), "OSC rejects invalid port");
OscChatbox.ValidateText(new string('a', 144));
OscChatbox.ValidateText(string.Concat(Enumerable.Repeat("👋", 72)));
Check(true, "Accepts 72 surrogate-pair emoji within UTF-16 limit");
Throws<ArgumentException>(() => OscChatbox.ValidateText(string.Concat(Enumerable.Repeat("👋", 73))), "Rejects 73 surrogate-pair emoji exceeding UTF-16 limit");
Check(OscChatbox.TextElementCount("e\u0301👨‍👩‍👧‍👦") == 2, "Grapheme count keeps accents and family emoji intact");
Throws<ArgumentException>(() => OscChatbox.ValidateText(new string('a', 145)), "Rejects 145-character chatbox message");
OscChatbox.ValidateText(string.Join("\r\n", Enumerable.Repeat("a", 9)));
Throws<ArgumentException>(() => OscChatbox.ValidateText(string.Join("\n", Enumerable.Repeat("a", 10))), "Rejects ten-line message");
Throws<ArgumentException>(() => OscChatbox.ValidateText("a\0b"), "Rejects chatbox NUL");
Throws<EncoderFallbackException>(() => OscChatbox.ValidateText("\ud800"), "Rejects malformed Unicode");

using (var listener = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0)))
using (var client = new OscChatbox(port: ((IPEndPoint)listener.Client.LocalEndPoint!).Port))
{
    await client.SendAsync("First", false, CancellationToken.None);
    var received = await listener.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(2));
    Check(received.Buffer.SequenceEqual(OscChatbox.BuildPacket("/chatbox/input", "First", true, false)), "UDP loopback sends complete chatbox message");
    var timer = Stopwatch.StartNew();
    await client.SendTypingAsync(true);
    received = await listener.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(2));
    Check(received.Buffer.SequenceEqual(OscChatbox.BuildPacket("/chatbox/typing", true)) && timer.ElapsedMilliseconds < 500,
        "Typing indicator bypasses message throttle");
    using (var canceled = new CancellationTokenSource(100))
        await ThrowsAsync<OperationCanceledException>(() => client.SendAsync("Canceled", false, canceled.Token), "Cancel during throttle does not send");
    Check(listener.Available == 0, "Canceled message leaves no UDP datagram");
    await client.SendAsync("Second", true, CancellationToken.None);
    received = await listener.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(2));
    Check(timer.ElapsedMilliseconds >= 1400 && received.Buffer.SequenceEqual(OscChatbox.BuildPacket("/chatbox/input", "Second", true, true)),
        "Chat messages are throttled and canceled send does not consume slot");
    using var preCanceled = new CancellationTokenSource();
    preCanceled.Cancel();
    await ThrowsAsync<OperationCanceledException>(() => client.SendTypingAsync(false, preCanceled.Token), "Pre-canceled typing send");
}

using (var listener = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0)))
{
    var client = new OscChatbox(port: ((IPEndPoint)listener.Client.LocalEndPoint!).Port);
    await client.SendAsync("First", false, CancellationToken.None);
    var pending = client.SendAsync("Queued", false, CancellationToken.None);
    client.Dispose();
    await ThrowsAsync<OperationCanceledException>(() => pending, "Dispose cancels queued chatbox send");
    await ThrowsAsync<ObjectDisposedException>(() => client.SendTypingAsync(true), "Disposed OSC client fails clearly");
}

var router = new AudioRouter();
Throws<InvalidOperationException>(() => router.PlayAsync([1], null, null, 1, 1, CancellationToken.None), "Audio requires selected route");
Throws<ArgumentException>(() => router.PlayAsync([], "x", null, 1, 1, CancellationToken.None), "Audio rejects empty data");
Throws<ArgumentOutOfRangeException>(() => router.PlayAsync([1], "x", null, float.NaN, 1, CancellationToken.None), "Audio rejects NaN volume");
Check(AudioRouter.IsVirtualCable(new AudioDevice("x", "CABLE Input (VB-Audio Virtual Cable)")) &&
      !AudioRouter.IsVirtualCable(new AudioDevice("y", "Speakers (Focusrite USB Audio)")), "Detects virtual cable render endpoint");

// Setup verification is deliberately nonmutating: never launch/elevate/install or rename in tests.
var driverPackage = await DriverSetup.LoadVerifiedPackageAsync();
Check(driverPackage.Length > 1_000_000 && DriverSetup.VerifyPackage(driverPackage), "Embedded vendor driver matches pinned SHA-256");
driverPackage[0] ^= 1;
Check(!DriverSetup.VerifyPackage(driverPackage), "Tampered driver package fails integrity check");
var nonce = Guid.NewGuid().ToString("N");
Check(DriverSetup.IsValidNonce(nonce) && !DriverSetup.IsValidNonce("../escape") && !DriverSetup.IsValidNonce(Guid.NewGuid().ToString()), "Setup nonce accepts only Guid N format");
var expectedResultPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VRCTTS", "SetupResults", nonce + ".json");
Check(DriverSetup.GetResultPath(nonce) == expectedResultPath, "Setup result path is scoped to the current user and nonce");
Throws<ArgumentException>(() => DriverSetup.GetResultPath("..\\escape"), "Setup result path rejects traversal");
var setupResult = new MicrophoneSetupResult(false, "Restart required", true);
Check(JsonSerializer.Deserialize<MicrophoneSetupResult>(JsonSerializer.Serialize(setupResult)) == setupResult, "Setup JSON preserves restart result");
Check(DriverSetup.SanitizeMessage("Ready\r\n\0") == "Ready" && DriverSetup.SanitizeMessage(new string('a', 1000)).Length == 650, "Setup messages strip controls and bound length");

var baseSpeaker = new AudioEndpointDiagnostic("standard", "Speakers (VB-Audio Virtual Cable)", "VB-Audio Virtual Cable",
    DataFlow.Render, DeviceState.Active, 1, 2, 48000);
var lineSixteen = baseSpeaker with { Id = "line16", Name = "CABLE In 16 Ch (VB-Audio Virtual Cable)", FormFactor = 2 };
var paidCable = baseSpeaker with { Id = "paid", Name = "CABLE-A Input", Adapter = "VB-Audio Cable A" };
Check(WindowsAudioEndpoints.SelectBasicCableRenderId([lineSixteen, paidCable, baseSpeaker]) == "standard", "Speakers name selects standard basic cable over paid or 16-channel line");
Check(WindowsAudioEndpoints.SelectBasicCableRenderId([baseSpeaker with { Name = "CABLE Input" }]) == "standard" &&
      WindowsAudioEndpoints.SelectBasicCableRenderId([baseSpeaker with { Name = "Localized or user-renamed speaker" }]) == "standard",
      "Standard cable selection is independent of endpoint display name");
Check(!WindowsAudioEndpoints.IsStandardBasicCableRender(lineSixteen), "Stereo mix does not misidentify the alternate 16-channel line pin");
Check(!WindowsAudioEndpoints.IsStandardBasicCableRender(paidCable) &&
      !WindowsAudioEndpoints.IsStandardBasicCableRender(paidCable with { Adapter = "VB-Audio Cable B" }) &&
      !WindowsAudioEndpoints.IsStandardBasicCableRender(baseSpeaker with { Adapter = "Focusrite USB Audio" }), "Cable A/B and physical speakers never masquerade as basic cable");
Check(!WindowsAudioEndpoints.IsStandardBasicCableRender(baseSpeaker with { State = DeviceState.Disabled }) &&
      !WindowsAudioEndpoints.IsStandardBasicCableRender(baseSpeaker with { Flow = DataFlow.Capture }) &&
      !WindowsAudioEndpoints.IsStandardBasicCableRender(baseSpeaker with { MixChannels = 16 }), "Disabled, capture, and 16-channel endpoints cannot be chosen as standard render");
Check(WindowsAudioEndpoints.MicrophoneName == "VRCTTS", "Microphone uses the exact requested name");
Check(WindowsAudioEndpoints.IsAppMicrophoneLabel("VB-Audio Virtual Cable", "VRCTTS") &&
      !WindowsAudioEndpoints.IsAppMicrophoneLabel("VB-Audio Virtual Cable", "VRCTTS (VB-Audio Virtual Cable)") &&
      !WindowsAudioEndpoints.IsAppMicrophoneLabel("VB-Audio Cable A", "VRCTTS"), "App microphone identity uses exact label and basic adapter rather than composite friendly name");
Check(!WindowsAudioEndpoints.IsAppMicrophoneLabel("VB-Audio Virtual Cable", "Justin Bridge"), "Previous app label requires the VRCTTS name update");
var renameCalls = 0;
var verifyCalls = 0;
Check(!WindowsAudioEndpoints.TryNameExistingMicrophone(() => false, () => renameCalls++, () => { verifyCalls++; return true; }) && renameCalls == 0 && verifyCalls == 0,
    "Existing-microphone rename never writes without an active standard cable pair");
Check(WindowsAudioEndpoints.TryNameExistingMicrophone(() => true, () => renameCalls++, () => { verifyCalls++; return true; }) && renameCalls == 1 && verifyCalls == 1,
    "Existing active cable is renamed once and verified before success");
Check(!WindowsAudioEndpoints.TryNameExistingMicrophone(() => true, () => { }, () => false),
    "Failed label readback cannot report the existing-microphone rename as successful");
Check(!WindowsAudioEndpoints.TryNameExistingMicrophone(() => true, () => throw new System.Runtime.InteropServices.COMException("Denied", unchecked((int)0x80070005)), () => true),
    "Denied per-user rename returns control to the normal repair path");
Check(DriverSetup.CanContinueAfterNoUpdate(259, true, true), "Already-current 259 is acceptable only with verified working device and pair");
Check(!DriverSetup.CanContinueAfterNoUpdate(259, false, true) && !DriverSetup.CanContinueAfterNoUpdate(259, true, false) &&
      !DriverSetup.CanContinueAfterNoUpdate(259, false, false) && !DriverSetup.CanContinueAfterNoUpdate(5, true, true),
      "259 cannot hide an unhealthy device or missing pair and other installation errors remain failures");

if (args.Contains("--diagnose"))
{
    var inventory = WindowsAudioEndpoints.GetEndpointInventory(includeInactive: true);
    Console.WriteLine(JsonSerializer.Serialize(new { SelectedRenderId = WindowsAudioEndpoints.FindBasicCableRenderId(),
        BoundDevice = DriverSetup.QueryBasicCableDevice(), Endpoints = inventory }, new JsonSerializerOptions { WriteIndented = true }));
}

if (args.Contains("--cable-smoke"))
{
    var index = Array.IndexOf(args, "--cable-smoke");
    var mp3Path = index + 1 < args.Length ? args[index + 1] : Path.GetFullPath("work/VRCTTS/VoiceTests/live-justin.mp3");
    var selected = WindowsAudioEndpoints.FindBasicCableRenderId();
    Check(selected is not null, "Finds the real basic cable speaker endpoint");
    var metadata = WindowsAudioEndpoints.GetEndpointInventory().Single(d => d.Id == selected);
    Console.WriteLine("Selected virtual render: " + JsonSerializer.Serialize(metadata));
    Check(metadata.FormFactor == 1 && metadata.MixChannels == 2 && metadata.SampleRate == 48000, "Reads actual speaker stereo/48kHz mix format without configuration changes");
    Check(WindowsAudioEndpoints.HasAppMicrophone() && WindowsAudioEndpoints.FindMicrophoneCaptureName() == "VRCTTS (VB-Audio Virtual Cable)",
        "Real renamed microphone is detected by label while preserving actual composite name");
    await router.PlayAsync(await File.ReadAllBytesAsync(mp3Path), null, selected, 0, 0, CancellationToken.None);
    Check(true, "Actual Justin MP3 decodes and plays to the selected virtual speaker at zero gain");
}

if (args.Contains("--audio-smoke"))
{
    var devices = AudioRouter.GetOutputDevices();
    Console.WriteLine("Active outputs: " + string.Join("; ", devices.Select(d => d.Name)));
    Check(devices.Count > 0, "Enumerates real audio endpoints");
    var first = devices.FirstOrDefault(d => !d.Name.Contains("Voicemeeter", StringComparison.OrdinalIgnoreCase)) ?? devices[0];
    var second = devices.FirstOrDefault(d => d.Id != first.Id);
    var wav = MakeSilentWave(300);
    await router.PlayAsync(wav, first.Id, second?.Id, 0, 0, CancellationToken.None);
    Check(true, second is null ? "Real WASAPI silence playback" : "Real dual-WASAPI silence playback");
    await router.PlayAsync(wav, first.Id, first.Id, 0, 0, CancellationToken.None);
    Check(true, "Duplicate endpoint deduplicates and plays cleanly");
    using (var cancellation = new CancellationTokenSource(120))
        await ThrowsAsync<OperationCanceledException>(() => router.PlayAsync(MakeSilentWave(5000), first.Id, second?.Id, 0, 0, cancellation.Token), "Audio cancellation stops all open outputs");
    var callbackInvoked = false;
    await ThrowsAsync<InvalidOperationException>(() => router.PlayAsync(wav, first.Id, "{missing-device}", 0, 0, CancellationToken.None,
        _ => { callbackInvoked = true; return Task.CompletedTask; }), "Missing second endpoint fails before playback");
    Check(!callbackInvoked, "Route initialization failure never invokes before-playback callback");
    await ThrowsAsync<Exception>(() => router.PlayAsync([1, 2, 3, 4], first.Id, null, 0, 0, CancellationToken.None), "Malformed audio fails clearly");
    var callbackCount = 0;
    await router.PlayAsync(wav, first.Id, null, 0, 0, CancellationToken.None,
        token => { token.ThrowIfCancellationRequested(); callbackCount++; return Task.CompletedTask; });
    Check(true, "Playback recovers after cancellation and device failure");
    Check(callbackCount == 1, "Initialized playback invokes callback once");
}

Console.WriteLine($"All {passed} checks passed.");

static byte[] MakeSilentWave(int milliseconds)
{
    const int sampleRate = 24000;
    var bytes = sampleRate * milliseconds / 1000 * 2;
    using var memory = new MemoryStream();
    using var writer = new BinaryWriter(memory, Encoding.ASCII, leaveOpen: true);
    writer.Write(Encoding.ASCII.GetBytes("RIFF")); writer.Write(36 + bytes);
    writer.Write(Encoding.ASCII.GetBytes("WAVEfmt ")); writer.Write(16);
    writer.Write((short)1); writer.Write((short)1); writer.Write(sampleRate);
    writer.Write(sampleRate * 2); writer.Write((short)2); writer.Write((short)16);
    writer.Write(Encoding.ASCII.GetBytes("data")); writer.Write(bytes); writer.Write(new byte[bytes]);
    writer.Flush();
    return memory.ToArray();
}
