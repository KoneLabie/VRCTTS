using System.Text;

namespace VRCTTS.Services;

public readonly record struct MessageLimit(int Maximum, bool UsesUtf8Bytes)
{
    public int Count(string text) => UsesUtf8Bytes ? Encoding.UTF8.GetByteCount(text) : text.Length;
    public string Counter(string text) => $"{Count(text)} / {Maximum}" + (UsesUtf8Bytes ? " bytes" : "");
    public bool IsExceeded(string text) => Count(text) > Maximum;
}

public static class MessageLimits
{
    public static MessageLimit For(bool chatboxEnabled, string voiceMode) => chatboxEnabled
        ? new(144, false)
        : VoiceCatalog.Normalize(voiceMode) == "justin"
            ? new(TtsService.StreamlabsMaximumUtf8Bytes, true)
            : VoiceCatalog.Find(voiceMode)?.LazybirdVoiceId is not null
                ? new(TtsService.LazybirdMaximumCharacters, false)
            : new(TtsService.FishMaximumCharacters, false);
}
