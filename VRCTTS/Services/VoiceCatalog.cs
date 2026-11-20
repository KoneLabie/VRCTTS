namespace VRCTTS.Services;

public sealed record TtsVoiceOption(string Id, string Name, string? FishReferenceId = null, string? LazybirdVoiceId = null)
{
    public override string ToString() => Name;
}

public static class VoiceCatalog
{
    public const string DefaultVoiceId = "fish";
    public const string XqcVoiceId = "fish:ee5e93f259c84f77b15a18dccd6b031c";
    public const string HasanVoiceId = "lazybird:hasan-piker";

    public static IReadOnlyList<TtsVoiceOption> Options { get; } = Array.AsReadOnly<TtsVoiceOption>(
    [
        new("fish", "Hatsune Miku", "acc8237220d8470985ec9be6c4c480a9"),
        new("justin", "Justin"),
        new(XqcVoiceId, "xQc", "ee5e93f259c84f77b15a18dccd6b031c"),
        new("fish:04827d1eecc4460aa0ca7f73aedbd511", "Alex Jones", "04827d1eecc4460aa0ca7f73aedbd511"),
        new("fish:df3cfdc9b9dd42e9a0f589569f598263", "Nick Fuentes", "df3cfdc9b9dd42e9a0f589569f598263"),
        new("fish:caec241606a642b78cd9f7b321bb14b3", "clav", "caec241606a642b78cd9f7b321bb14b3"),
        new(HasanVoiceId, "Hasan Piker", LazybirdVoiceId: "396g2687JGhEZqvyoonWku"),
        new("fish:652d734759a14fbab52177325e835be4", "Yameii Online", "652d734759a14fbab52177325e835be4"),
        new("fish:e33adaeadc644b1d9de55bbc84a2341c", "Kai Cenat", "e33adaeadc644b1d9de55bbc84a2341c"),
        new("fish:a9261e0625ce4253914f5349fa0d7852", "mrbeast", "a9261e0625ce4253914f5349fa0d7852"),
        new("fish:98655a12fa944e26b274c535e5e03842", "E-girl", "98655a12fa944e26b274c535e5e03842"),
        new("fish:a2346273eb314b6cb82ba83dbc9d9fee", "Markiplier", "a2346273eb314b6cb82ba83dbc9d9fee"),
        new("fish:a3b3f0a9c49340bd8fa722d83c81cb08", "teto", "a3b3f0a9c49340bd8fa722d83c81cb08"),
        new("fish:c85fb11f91f84312a4bd16756f298ae2", "GOJO", "c85fb11f91f84312a4bd16756f298ae2"),
        new("fish:265ee53e0b6d4a2ab028ca1ff1dd94d0", "Subaru", "265ee53e0b6d4a2ab028ca1ff1dd94d0"),
        new("fish:d34183dcf4a041f5a82aa3dd19644329", "Naruto", "d34183dcf4a041f5a82aa3dd19644329"),
        new("fish:03397b4c4be74759b72533b663fbd001", "Elon Musk (Noise reduction)", "03397b4c4be74759b72533b663fbd001"),
        new("fish:305611ec428640ee824979aea5c7e19c", "Chris", "305611ec428640ee824979aea5c7e19c"),
        new("fish:ffeb710f6d8b4bb6a2099428ad51cf95", "Casual Young Male", "ffeb710f6d8b4bb6a2099428ad51cf95")
    ]);

    public static TtsVoiceOption? Find(string? id) => Options.FirstOrDefault(voice => voice.Id == id);

    public static string Normalize(string? id) => id is "streamlabs" or "speechgen" or "offline"
        ? "justin" : Find(id)?.Id ?? DefaultVoiceId;
}
