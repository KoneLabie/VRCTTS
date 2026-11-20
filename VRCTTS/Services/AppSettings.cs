using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace VRCTTS.Services;

public sealed class AppSettings
{
    public string? HeadphoneId { get; set; }
    public string? CableId { get; set; }
    public bool HeadphonesEnabled { get; set; } = true;
    public bool MicrophoneEnabled { get; set; } = true;
    public float HeadphoneVolume { get; set; } = .7f;
    public float MicrophoneVolume { get; set; } = 1;
    public bool ChatboxEnabled { get; set; } = true;
    public bool NotificationSound { get; set; }
    public bool ClearAfterSpeaking { get; set; } = true;
    public bool KeepOnTop { get; set; }
    public bool MicrophoneSetupAttempted { get; set; }
    public int MicrophoneSetupRevision { get; set; }
    public int OscPort { get; set; } = 9000;
    public string VoiceMode { get; set; } = VoiceCatalog.DefaultVoiceId;
    public string Email { get; set; } = "";
    public string ProtectedToken { get; set; } = "";
    public string ProtectedFishToken { get; set; } = "";
    public string ProtectedLazybirdToken { get; set; } = "";

    private string? storageDirectory;
    private bool canSave = true;
    private bool voiceSelectionDirty;
    private bool speechGenTokenDirty, fishTokenDirty, lazybirdTokenDirty, emailDirty;
    [JsonIgnore] public string? LoadWarning { get; private set; }

    public static string DataDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VRCTTS");
    public static string LegacyDataDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "JustinBridge");

    public static AppSettings Load(string? dataDirectory = null, string? legacyDirectory = null)
    {
        var directory = Path.GetFullPath(dataDirectory ?? DataDirectory);
        var settingsPath = Path.Combine(directory, "settings.json");
        try
        {
            WithStorageLock(directory, () =>
            {
                if (File.Exists(settingsPath)) return;
                // Explicit test directories never consult the user's real legacy preferences.
                var legacy = legacyDirectory ?? (dataDirectory is null ? LegacyDataDirectory : null);
                if (legacy is null) return;
                var legacyPath = Path.Combine(Path.GetFullPath(legacy), "settings.json");
                if (!File.Exists(legacyPath)) return;
                var imported = ReadSettings(legacyPath);
                imported.VoiceMode = VoiceCatalog.DefaultVoiceId;
                WriteAtomic(directory, imported, overwrite: false);
            });
            var value = File.Exists(settingsPath) ? ReadSettings(settingsPath) : new AppSettings();
            value.storageDirectory = directory;
            return value;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            // A damaged/unreadable file is never silently replaced with defaults on exit.
            return new AppSettings
            {
                storageDirectory = directory,
                canSave = false,
                LoadWarning = "Could not read or import saved preferences. The original files have not been changed."
            };
        }
    }

    private static AppSettings ReadSettings(string path)
    {
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length > 1024 * 1024) throw new InvalidDataException("The saved preferences file is too large.");
        var value = JsonSerializer.Deserialize<AppSettings>(input) ?? throw new InvalidDataException("The saved preferences are invalid.");
        value.HeadphoneVolume = float.IsFinite(value.HeadphoneVolume) ? Math.Clamp(value.HeadphoneVolume, 0, 1) : .7f;
        value.MicrophoneVolume = float.IsFinite(value.MicrophoneVolume) ? Math.Clamp(value.MicrophoneVolume, 0, 1) : 1;
        if (value.OscPort < 1 || value.OscPort > 65535) value.OscPort = 9000;
        value.VoiceMode = VoiceCatalog.Normalize(value.VoiceMode);
        value.Email ??= "";
        value.ProtectedToken ??= "";
        value.ProtectedFishToken ??= "";
        value.ProtectedLazybirdToken ??= "";
        return value;
    }

    public string ReadToken()
    {
        return ReadProtectedToken(ProtectedToken);
    }

    public string ReadFishToken() => ReadProtectedToken(ProtectedFishToken);
    public string ReadLazybirdToken() => ReadProtectedToken(ProtectedLazybirdToken);

    private static string ReadProtectedToken(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        try
        {
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(value), null, DataProtectionScope.CurrentUser));
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException) { return ""; }
    }

    public void SetToken(string token)
    {
        if (string.Equals(ReadToken(), token, StringComparison.Ordinal)) return;
        ProtectedToken = ProtectToken(token);
        speechGenTokenDirty = true;
    }

    public void SetFishToken(string token)
    {
        if (string.Equals(ReadFishToken(), token, StringComparison.Ordinal)) return;
        ProtectedFishToken = ProtectToken(token);
        fishTokenDirty = true;
    }

    public void SetEmail(string email)
    {
        var normalized = email.Trim();
        if (string.Equals(Email, normalized, StringComparison.Ordinal)) return;
        Email = normalized;
        emailDirty = true;
    }

    public void SetLazybirdToken(string token)
    {
        if (string.Equals(ReadLazybirdToken(), token, StringComparison.Ordinal)) return;
        ProtectedLazybirdToken = ProtectToken(token);
        lazybirdTokenDirty = true;
    }

    private static string ProtectToken(string? token) => string.IsNullOrEmpty(token) ? ""
        : Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(token), null, DataProtectionScope.CurrentUser));

    public void SelectVoice(string mode)
    {
        if (!VoiceCatalog.Options.Any(option => option.Id == mode))
            throw new ArgumentException("Choose a voice from the voice list.", nameof(mode));
        VoiceMode = mode;
        voiceSelectionDirty = true;
    }

    public void Save(string? dataDirectory = null)
    {
        if (!canSave) throw new InvalidOperationException("Saved preferences could not be read. Reopen VRCTTS after repairing the original settings file.");
        var directory = Path.GetFullPath(dataDirectory ?? storageDirectory ?? DataDirectory);
        WithStorageLock(directory, () =>
        {
            var path = Path.Combine(directory, "settings.json");
            var current = File.Exists(path) ? ReadSettings(path) : null;
            // Closing an older window must not undo a newer explicit choice made
            // in another window. Keep this instance's displayed voice untouched.
            var toWrite = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(this))!;
            if (current is not null)
            {
                if (!voiceSelectionDirty) toWrite.VoiceMode = current.VoiceMode;
                if (!fishTokenDirty) toWrite.ProtectedFishToken = current.ProtectedFishToken;
                if (!lazybirdTokenDirty) toWrite.ProtectedLazybirdToken = current.ProtectedLazybirdToken;
                if (!speechGenTokenDirty) toWrite.ProtectedToken = current.ProtectedToken;
                if (!emailDirty) toWrite.Email = current.Email;
            }
            WriteAtomic(directory, toWrite, overwrite: true);
            voiceSelectionDirty = false;
            fishTokenDirty = lazybirdTokenDirty = speechGenTokenDirty = emailDirty = false;
        });
        storageDirectory = directory;
    }

    private static void WriteAtomic(string directory, AppSettings settings, bool overwrite)
    {
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, $"settings-{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporary, Path.Combine(directory, "settings.json"), overwrite);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static void WithStorageLock(string directory, Action action)
    {
        var name = "VRCTTS.Settings." + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(directory.ToUpperInvariant())));
        using var mutex = new Mutex(initiallyOwned: false, name);
        var acquired = false;
        try
        {
            try { acquired = mutex.WaitOne(TimeSpan.FromSeconds(3)); }
            catch (AbandonedMutexException) { acquired = true; }
            if (!acquired) throw new IOException("Preferences are being saved in another VRCTTS window. Try again.");
            action();
        }
        finally { if (acquired) mutex.ReleaseMutex(); }
    }
}
