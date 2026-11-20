using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace VRCTTS.Services;

/// <summary>VRChat chatbox OSC output, restricted to the local computer.</summary>
public sealed class OscChatbox : IDisposable
{
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private static readonly TimeSpan SendInterval = TimeSpan.FromMilliseconds(1500);
    private readonly UdpClient udp;
    private readonly SemaphoreSlim chatGate = new(1, 1);
    private readonly SemaphoreSlim socketGate = new(1, 1);
    private readonly CancellationTokenSource lifetime = new();
    private long? lastSent;
    private bool disposed;

    public OscChatbox(string host = "127.0.0.1", int port = 9000)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        if (port < 1 || port > ushort.MaxValue) throw new ArgumentOutOfRangeException(nameof(port));
        var address = host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ? IPAddress.Loopback :
            IPAddress.TryParse(host, out var parsed) ? parsed : null;
        if (address is null || !IPAddress.IsLoopback(address))
            throw new ArgumentException("VRChat OSC must use a local loopback address.", nameof(host));
        udp = new UdpClient(address.AddressFamily);
        udp.Connect(new IPEndPoint(address, port));
    }

    public static int TextElementCount(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return StringInfo.ParseCombiningCharacters(text).Length;
    }

    public static void ValidateText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Contains('\0')) throw new ArgumentException("Text cannot contain a null character.", nameof(text));
        // Unity strings use UTF-16 code units; this conservative bound also fits clients
        // that count Unicode scalar values or graphemes.
        if (text.Length > 144)
            throw new ArgumentException("VRChat chatbox messages can contain up to 144 characters.", nameof(text));
        var lines = 1;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\r')
            {
                lines++;
                if (i + 1 < text.Length && text[i + 1] == '\n') i++;
            }
            else if (text[i] == '\n') lines++;
        }
        if (lines > 9) throw new ArgumentException("VRChat chatbox messages can contain up to 9 lines.", nameof(text));
        // Reject malformed UTF-16 rather than silently transmitting replacement characters.
        _ = Utf8.GetByteCount(text);
    }

    public async Task SendAsync(string text, bool notify, CancellationToken token)
    {
        ValidateText(text);
        ObjectDisposedException.ThrowIf(disposed, this);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token);
        await chatGate.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            if (lastSent is long timestamp)
            {
                var remaining = SendInterval - Stopwatch.GetElapsedTime(timestamp);
                if (remaining > TimeSpan.Zero) await Task.Delay(remaining, linked.Token).ConfigureAwait(false);
            }
            await SendPacketAsync(BuildPacket("/chatbox/input", text, true, notify), linked.Token).ConfigureAwait(false);
            lastSent = Stopwatch.GetTimestamp();
        }
        finally { chatGate.Release(); }
    }

    public Task SendTypingAsync(bool typing, CancellationToken token = default) =>
        SendPacketAsync(BuildPacket("/chatbox/typing", typing), token);

    private async Task SendPacketAsync(byte[] packet, CancellationToken token)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token);
        await socketGate.WaitAsync(linked.Token).ConfigureAwait(false);
        try { await udp.SendAsync(packet.AsMemory(), linked.Token).ConfigureAwait(false); }
        finally { socketGate.Release(); }
    }

    public static byte[] BuildPacket(string address, params object[] values)
    {
        ArgumentNullException.ThrowIfNull(address);
        ArgumentNullException.ThrowIfNull(values);
        if (!address.StartsWith('/') || address.Contains('\0'))
            throw new ArgumentException("An OSC address must start with '/' and cannot contain NUL.", nameof(address));
        using var packet = new MemoryStream();
        WriteString(packet, address);
        var tags = new StringBuilder(",");
        foreach (var value in values)
            tags.Append(value switch
            {
                string => 's',
                bool state => state ? 'T' : 'F',
                _ => throw new ArgumentException("Only OSC strings and booleans are supported.", nameof(values))
            });
        WriteString(packet, tags.ToString());
        foreach (var value in values)
            if (value is string text) WriteString(packet, text);
        return packet.ToArray();
    }

    private static void WriteString(Stream stream, string text)
    {
        if (text.Contains('\0')) throw new ArgumentException("OSC strings cannot contain NUL.", nameof(text));
        var bytes = Utf8.GetBytes(text);
        stream.Write(bytes);
        stream.WriteByte(0);
        for (var padding = (4 - ((bytes.Length + 1) % 4)) % 4; padding > 0; padding--)
            stream.WriteByte(0);
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        lifetime.Cancel();
        udp.Dispose();
        lifetime.Dispose();
        // Gates are intentionally not disposed while canceled waiters unwind and release them.
    }
}
