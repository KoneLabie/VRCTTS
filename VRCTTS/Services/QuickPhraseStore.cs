using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace VRCTTS.Services;

public sealed record QuickPhrase(Guid Id, string Text);

public sealed class QuickPhraseConflictException : InvalidOperationException
{
    public QuickPhraseConflictException() : base("Quick TTS phrases changed in another VRCTTS window. Reload the list, review your change, and save again.") { }
}

/// <summary>Stores only phrases explicitly saved in Quick TTS, independently of app preferences.</summary>
public sealed class QuickPhraseStore
{
    public const int MaximumPhrases = 50;
    public const int MaximumCharacters = 5000;
    private const int MaximumFileBytes = 2 * 1024 * 1024;
    private readonly string path;
    private readonly string mutexName;
    private readonly bool importLegacy;
    private string? revision;

    public QuickPhraseStore(string? path = null)
    {
        importLegacy = path is null;
        this.path = Path.GetFullPath(path ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VRCTTS", "quick-phrases.json"));
        mutexName = "VRCTTS.QuickPhrases." + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(this.path.ToUpperInvariant())));
    }

    public IReadOnlyList<QuickPhrase> Load() => WithLock(() =>
    {
        var snapshot = ReadSnapshot();
        var phrases = Parse(snapshot);
        revision = snapshot.Revision;
        return phrases;
    });

    private static IReadOnlyList<QuickPhrase> Parse(Snapshot snapshot)
    {
        if (snapshot.Bytes is null)
            return [new QuickPhrase(Guid.Parse("c16b7bf7-de6e-486c-9df5-18a8800148b7"), "mamagüevo")];
        try
        {
            var phrases = JsonSerializer.Deserialize<List<QuickPhrase?>>(snapshot.Bytes)
                ?? throw new InvalidDataException("The saved Quick TTS phrase list is empty or invalid. It has not been changed.");
            return Validate(phrases);
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException)
        {
            throw new InvalidDataException("The saved Quick TTS phrases are invalid. The original file has not been changed.", ex);
        }
    }

    public void Save(IEnumerable<QuickPhrase> phrases)
    {
        ArgumentNullException.ThrowIfNull(phrases);
        var validated = Validate(phrases);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(validated, new JsonSerializerOptions { WriteIndented = true });
        WithLock(() =>
        {
            var current = ReadSnapshot();
            if (revision is null)
            {
                // Initializers may save without loading first, but an existing
                // malformed file must still never be silently replaced.
                Parse(current);
                revision = current.Revision;
            }
            if (current.Revision != revision) throw new QuickPhraseConflictException();

            var directory = Path.GetDirectoryName(path)!;
            Directory.CreateDirectory(directory);
            var temporary = Path.Combine(directory, $"quick-phrases-{Guid.NewGuid():N}.tmp");
            try
            {
                using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    output.Write(bytes);
                    output.Flush(flushToDisk: true);
                }
                File.Move(temporary, path, overwrite: true);
                revision = Hash(bytes);
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
            return true;
        });
    }

    private Snapshot ReadSnapshot()
    {
        if (importLegacy)
            CopyLegacyIfMissing(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "JustinBridge", "quick-phrases.json"));
        try
        {
            // Deny concurrent writes or replacement by non-cooperating writers
            // while taking the bounded snapshot used for parsing and hashing.
            using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (input.Length > MaximumFileBytes)
                throw new InvalidDataException("The saved Quick TTS phrase file is too large. It has not been changed.");
            var bytes = new byte[(int)input.Length];
            input.ReadExactly(bytes);
            return new Snapshot(bytes, Hash(bytes));
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return new Snapshot(null, "missing");
        }
    }

    // Kept separate from preferences so an unreadable phrase file cannot reset
    // otherwise valid audio/voice settings. Explicit paths keep tests isolated.
    internal static void MigrateLegacyPhrases(string dataDirectory, string legacyDirectory)
    {
        var store = new QuickPhraseStore(Path.Combine(Path.GetFullPath(dataDirectory), "quick-phrases.json"));
        store.WithLock(() =>
        {
            store.CopyLegacyIfMissing(Path.Combine(Path.GetFullPath(legacyDirectory), "quick-phrases.json"));
            return true;
        });
    }

    private void CopyLegacyIfMissing(string legacyPath)
    {
        if (File.Exists(path) || !File.Exists(legacyPath)) return;
        byte[] bytes;
        using (var input = new FileStream(legacyPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            if (input.Length > MaximumFileBytes)
                throw new InvalidDataException("The original Quick TTS phrase file is too large. It has not been changed.");
            bytes = new byte[(int)input.Length];
            input.ReadExactly(bytes);
        }
        var directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, $"quick-phrases-import-{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllBytes(temporary, bytes);
            File.Move(temporary, path, overwrite: false);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    private sealed record Snapshot(byte[]? Bytes, string Revision);

    private T WithLock<T>(Func<T> action)
    {
        using var mutex = new Mutex(initiallyOwned: false, mutexName);
        var acquired = false;
        try
        {
            try { acquired = mutex.WaitOne(TimeSpan.FromSeconds(3)); }
            catch (AbandonedMutexException) { acquired = true; }
            if (!acquired) throw new IOException("Quick TTS phrases are being saved by another VRCTTS window. Try again.");
            return action();
        }
        finally { if (acquired) mutex.ReleaseMutex(); }
    }

    private static List<QuickPhrase> Validate(IEnumerable<QuickPhrase?> phrases)
    {
        List<QuickPhrase> result = [];
        HashSet<Guid> ids = [];
        HashSet<string> texts = new(StringComparer.Ordinal);
        foreach (var phrase in phrases)
        {
            if (result.Count >= MaximumPhrases)
                throw new ArgumentException($"You can save up to {MaximumPhrases} Quick TTS phrases.", nameof(phrases));
            if (phrase is null || phrase.Id == Guid.Empty || !ids.Add(phrase.Id))
                throw new ArgumentException("Every Quick TTS phrase must have its own valid ID.", nameof(phrases));
            if (phrase.Text is null || phrase.Text.Contains('\0'))
                throw new ArgumentException("Quick TTS phrases cannot contain null characters.", nameof(phrases));
            var text = phrase.Text.Replace("\r\n", "\n").Replace('\r', '\n').Trim();
            if (text.Length == 0)
                throw new ArgumentException("Type a phrase before saving it.", nameof(phrases));
            if (text.Length > MaximumCharacters)
                throw new ArgumentException($"Quick TTS phrases can contain up to {MaximumCharacters} characters.", nameof(phrases));
            if (!texts.Add(text))
                throw new ArgumentException("That phrase is already saved in Quick TTS.", nameof(phrases));
            result.Add(phrase with { Text = text });
        }
        return result;
    }
}
