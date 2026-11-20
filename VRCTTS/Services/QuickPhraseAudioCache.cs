using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace VRCTTS.Services;

/// <summary>
/// Keeps completed Quick TTS clips in memory only. Every request must match the
/// saved phrase and voice settings exactly. Callers own the arrays they receive.
/// </summary>
public sealed class QuickPhraseAudioCache
{
    private const int MaximumEntries = 16;
    private const int MaximumBytes = 32 * 1024 * 1024;
    private const int MaximumClipBytes = 8 * 1024 * 1024;
    private readonly object gate = new();
    private readonly Dictionary<CacheKey, LinkedListNode<CacheEntry>> entries = [];
    private readonly LinkedList<CacheEntry> recent = new();
    private int totalBytes;

    public bool TryGet(Guid phraseId, string text, string mode, string email, string token, out byte[] audio)
    {
        var key = CreateKey(phraseId, text, mode, email, token);
        lock (gate)
        {
            if (!entries.TryGetValue(key, out var node))
            {
                audio = [];
                return false;
            }
            recent.Remove(node);
            recent.AddFirst(node);
            audio = (byte[])node.Value.Audio.Clone();
            return true;
        }
    }

    /// <summary>
    /// Stores an owned copy. Clips outside the cache size bound are skipped, so a
    /// successful synthesis can still play even when its audio cannot be cached.
    /// </summary>
    public void Store(Guid phraseId, string text, string mode, string email, string token, byte[] audio)
    {
        ArgumentNullException.ThrowIfNull(audio);
        var key = CreateKey(phraseId, text, mode, email, token);
        lock (gate)
        {
            if (entries.TryGetValue(key, out var previous)) RemoveEntry(previous);
            if (audio.Length == 0 || audio.Length > MaximumClipBytes) return;
            while (entries.Count >= MaximumEntries || totalBytes + audio.Length > MaximumBytes)
                RemoveEntry(recent.Last!);
            var entry = new CacheEntry(key, (byte[])audio.Clone());
            entries.Add(key, recent.AddFirst(entry));
            totalBytes += entry.Audio.Length;
        }
    }

    /// <summary>Forgets every text/provider variant belonging to a saved phrase.</summary>
    public void Remove(Guid phraseId)
    {
        lock (gate)
        {
            var node = recent.First;
            while (node is not null)
            {
                var next = node.Next;
                if (node.Value.Key.PhraseId == phraseId) RemoveEntry(node);
                node = next;
            }
        }
    }

    public void Clear()
    {
        lock (gate)
        {
            entries.Clear();
            recent.Clear();
            totalBytes = 0;
        }
    }

    private void RemoveEntry(LinkedListNode<CacheEntry> node)
    {
        entries.Remove(node.Value.Key);
        recent.Remove(node);
        totalBytes -= node.Value.Audio.Length;
    }

    private static CacheKey CreateKey(Guid phraseId, string text, string mode, string email, string token)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(mode);
        ArgumentNullException.ThrowIfNull(email);
        ArgumentNullException.ThrowIfNull(token);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        HashCredential(hash, email);
        HashCredential(hash, token);
        return new CacheKey(phraseId, text, mode, Convert.ToHexString(hash.GetHashAndReset()));
    }

    private static void HashCredential(IncrementalHash hash, string value)
    {
        Span<byte> length = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(length, value.Length);
        hash.AppendData(length);
        // Hash the exact UTF-16 code units, including any unpaired surrogate.
        // An encoding replacement fallback could otherwise merge distinct keys.
        hash.AppendData(MemoryMarshal.AsBytes(value.AsSpan()));
    }

    private sealed record CacheKey(Guid PhraseId, string Text, string Mode, string CredentialDigest);
    private sealed record CacheEntry(CacheKey Key, byte[] Audio);
}
