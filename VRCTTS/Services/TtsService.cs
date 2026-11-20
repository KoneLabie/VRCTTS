using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace VRCTTS.Services;

/// <summary>Synthesizes the selected voice. Online text is sent only to its selected provider.</summary>
public sealed class TtsService : IDisposable
{
    public const int MaximumAudioBytes = 8 * 1024 * 1024;
    public const int StreamlabsMaximumUtf8Bytes = 550;
    public const int JustinMaximumUtf8Bytes = StreamlabsMaximumUtf8Bytes;
    public const int SpeechGenMaximumCharacters = 2000;
    public const int OfflineMaximumCharacters = 5000;
    public const int FishMaximumCharacters = 2000;
    public const int LazybirdMaximumCharacters = 2000;
    public const string FishReferenceId = "acc8237220d8470985ec9be6c4c480a9";
    public const string FishInferenceModel = "s2.1-pro-free";
    private const int MaximumJsonBytes = 64 * 1024;
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(35);
    private static readonly Uri StreamlabsEndpoint = new("https://streamlabs.com/polly/speak");
    private static readonly Uri SpeechGenEndpoint = new("https://speechgen.io/index.php?r=api/text");
    private static readonly Uri FishEndpoint = new("https://api.fish.audio/v1/tts");
    private static readonly Uri LazybirdEndpoint = new("https://api.lazybird.app/v2/generate-speech");
    private readonly HttpClient _client;
    private readonly bool _ownsClient;
    private readonly Lazy<OfflineJustinService> _offline = new(() => new OfflineJustinService());
    private readonly Func<string, CancellationToken, Task<byte[]>> _offlineSynthesize;
    private readonly TimeSpan _justinOnlineTimeout;
    private bool _disposed;

    public bool LastSynthesisWasOffline { get; private set; }

    public TtsService(HttpClient? client = null,
        Func<string, CancellationToken, Task<byte[]>>? offlineSynthesize = null,
        TimeSpan? justinOnlineTimeout = null)
    {
        _justinOnlineTimeout = justinOnlineTimeout ?? TimeSpan.FromSeconds(8);
        if (_justinOnlineTimeout < TimeSpan.FromMilliseconds(1) || _justinOnlineTimeout > RequestTimeout)
            throw new ArgumentOutOfRangeException(nameof(justinOnlineTimeout));
        _offlineSynthesize = offlineSynthesize ?? ((text, ct) => _offline.Value.SynthesizeAsync(text, ct));
        _ownsClient = client is null;
        _client = client ?? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
        {
            Timeout = RequestTimeout
        };
    }

    public async Task<byte[]> SynthesizeAsync(string text, string mode, string? email, string? token,
        CancellationToken cancellationToken, string? fishToken = null, string? lazybirdToken = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        LastSynthesisWasOffline = false;
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(text))
            throw new ArgumentException("Enter a message to speak.", nameof(text));
        if (text.Contains('\0'))
            throw new ArgumentException("Remove null characters from the message.", nameof(text));

        var provider = mode?.Trim().ToLowerInvariant();
        var selectedVoice = VoiceCatalog.Find(provider);
        var fishReferenceId = selectedVoice?.FishReferenceId;
        var lazybirdVoiceId = selectedVoice?.LazybirdVoiceId;
        if (fishReferenceId is not null) provider = "fish";
        else if (lazybirdVoiceId is not null) provider = "lazybird";
        if (provider == "justin")
        {
            if (Encoding.UTF8.GetByteCount(text) > JustinMaximumUtf8Bytes)
                throw new ArgumentException("Shorten your Justin message to 550 UTF-8 bytes or fewer.", nameof(text));
            return await SynthesizeJustinAsync(text, cancellationToken).ConfigureAwait(false);
        }
        if (provider == "offline")
        {
            var audio = await _offlineSynthesize(text, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return audio;
        }
        switch (provider)
        {
            case "streamlabs" when Encoding.UTF8.GetByteCount(text) > StreamlabsMaximumUtf8Bytes:
                throw new ArgumentException("This message is too long for Justin. Shorten it to 550 UTF-8 bytes or fewer.", nameof(text));
            case "speechgen" when text.Length > SpeechGenMaximumCharacters:
                throw new ArgumentException("SpeechGen messages must be 2,000 characters or fewer.", nameof(text));
            case "speechgen" when string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(token):
                throw new InvalidOperationException("Enter your SpeechGen account email and API token in Settings.");
            case "fish" when text.Length > FishMaximumCharacters:
                throw new ArgumentException("Fish Audio messages in VRCTTS must be 2,000 characters or fewer.", nameof(text));
            case "fish" when string.IsNullOrEmpty(fishReferenceId):
                throw new ArgumentException("Choose a voice in Settings.", nameof(mode));
            case "fish" when string.IsNullOrWhiteSpace(fishToken):
                throw new InvalidOperationException("Enter your Fish Audio API key in Settings to use this voice.");
            case "fish" when fishToken!.Trim().Any(character => char.IsWhiteSpace(character) || char.IsControl(character)):
                throw new InvalidOperationException("Enter a valid Fish Audio API key in Settings.");
            case "lazybird" when text.Length > LazybirdMaximumCharacters:
                throw new ArgumentException("Lazybird messages in VRCTTS must be 2,000 characters or fewer.", nameof(text));
            case "lazybird" when string.IsNullOrEmpty(lazybirdVoiceId):
                throw new ArgumentException("Choose a voice in Settings.", nameof(mode));
            case "lazybird" when string.IsNullOrWhiteSpace(lazybirdToken):
                throw new InvalidOperationException("Enter your Lazybird API key in Settings to use this voice.");
            case "lazybird" when lazybirdToken!.Trim().Any(character => char.IsWhiteSpace(character) || char.IsControl(character)):
                throw new InvalidOperationException("Enter a valid Lazybird API key in Settings.");
            case "fish":
            case "lazybird":
            case "streamlabs":
            case "speechgen":
                break;
            default:
                throw new ArgumentException("Choose a voice in Settings.", nameof(mode));
        }

        // HttpClient.Timeout does not cover streaming a body after ResponseHeadersRead.
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(RequestTimeout);
        try
        {
            using var request = CreateRequest(text, provider, email, token, fishToken, fishReferenceId, lazybirdToken, lazybirdVoiceId);
            using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token)
                .ConfigureAwait(false);
            EnsureSuccess(response, provider);
            if (provider is "fish" or "lazybird")
                return await ReadAudioResponseAsync(response, deadline.Token).ConfigureAwait(false);
            var jsonBytes = await ReadBoundedAsync(response.Content, MaximumJsonBytes, "The voice service returned an oversized response.", deadline.Token)
                .ConfigureAwait(false);
            using var json = JsonDocument.Parse(jsonBytes);
            var audioUri = ReadAudioUri(json.RootElement, provider);

            using var audioRequest = new HttpRequestMessage(HttpMethod.Get, audioUri);
            audioRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("audio/mpeg"));
            using var audioResponse = await _client.SendAsync(audioRequest, HttpCompletionOption.ResponseHeadersRead, deadline.Token)
                .ConfigureAwait(false);
            EnsureSuccess(audioResponse, provider);
            if (audioResponse.RequestMessage?.RequestUri is { } finalUri)
                ValidateAudioUri(finalUri, provider);
            return await ReadAudioResponseAsync(audioResponse, deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new InvalidOperationException("The voice service took too long. Try again.");
        }
        catch (HttpRequestException)
        {
            // In particular, never expose a signed download URL, account token, or message in errors.
            throw new InvalidOperationException("Could not reach the voice service. Check your internet connection and try again.");
        }
        catch (IOException)
        {
            throw new InvalidOperationException("The voice download was interrupted. Try again.");
        }
        catch (JsonException)
        {
            throw new InvalidOperationException("The voice service returned an unexpected response. Try again.");
        }
    }

    public IReadOnlyList<OfflineVoiceInfo> GetOfflineVoices() => _offline.Value.GetVoices();

    private async Task<byte[]> SynthesizeJustinAsync(string text, CancellationToken cancellationToken)
    {
        // Attempt the actual speech request directly, without a connectivity ping.
        // This deadline covers both the online request and its audio download.
        using var onlineDeadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        onlineDeadline.CancelAfter(_justinOnlineTimeout);
        try
        {
            return await SynthesizeAsync(text, "streamlabs", null, null, onlineDeadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is InvalidOperationException or OperationCanceledException)
        {
            // Unavailable, rejected, malformed, or timed-out online audio can use
            // only the installed original Justin engine. Validation and Stop never
            // cause a fallback, and Fish voices never enter this path.
        }

        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var audio = await _offlineSynthesize(text, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            LastSynthesisWasOffline = true;
            return audio;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw new InvalidOperationException("Justin is unavailable online, and offline Justin could not speak. Check that the original IVONA Justin SAPI5 voice is installed and activated.");
        }
    }

    private static HttpRequestMessage CreateRequest(string text, string provider, string? email, string? token,
        string? fishToken, string? fishReferenceId, string? lazybirdToken, string? lazybirdVoiceId)
    {
        if (provider == "lazybird")
        {
            var request = new HttpRequestMessage(HttpMethod.Post, LazybirdEndpoint);
            request.Headers.Add("X-API-Key", lazybirdToken!.Trim());
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("audio/mpeg"));
            request.Content = new StringContent(JsonSerializer.Serialize(new
            {
                voiceId = lazybirdVoiceId!,
                text
            }), Encoding.UTF8, "application/json");
            return request;
        }
        if (provider == "fish")
        {
            var request = new HttpRequestMessage(HttpMethod.Post, FishEndpoint);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", fishToken!.Trim());
            // Send the exact free-model header on every request; the API's default
            // model is paid. A failed request is never retried with a different model.
            request.Headers.Add("model", FishInferenceModel);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("audio/mpeg"));
            request.Content = new StringContent(JsonSerializer.Serialize(new
            {
                text,
                reference_id = fishReferenceId!,
                format = "mp3",
                latency = "low"
            }), Encoding.UTF8, "application/json");
            return request;
        }
        if (provider == "streamlabs")
        {
            var request = new HttpRequestMessage(HttpMethod.Post, StreamlabsEndpoint);
            request.Headers.Referrer = new Uri("https://streamlabs.com");
            request.Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["voice"] = "Justin", ["text"] = text
            });
            return request;
        }

        return new HttpRequestMessage(HttpMethod.Post, SpeechGenEndpoint)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["email"] = email!.Trim(), ["token"] = token!.Trim(), ["voice"] = "Justin",
                ["text"] = text, ["format"] = "mp3", ["speed"] = "1", ["pitch"] = "0",
                ["sample_rate"] = "24000", ["bitrate"] = "128", ["channels"] = "1"
            })
        };
    }

    private static Uri ReadAudioUri(JsonElement root, string provider)
    {
        if (root.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException("The voice service returned an unexpected response. Try again.");

        string? address;
        if (provider == "streamlabs")
        {
            if (!root.TryGetProperty("success", out var success) || success.ValueKind != JsonValueKind.True)
                throw new InvalidOperationException("Streamlabs could not create the Justin voice clip. Try again later or select SpeechGen in Settings.");
            address = ReadString(root, "speak_url");
        }
        else
        {
            if (!root.TryGetProperty("status", out var status) || !IsReadyStatus(status))
                throw new InvalidOperationException(SpeechGenError(root));
            address = ReadString(root, "file");
        }

        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri))
            throw new InvalidOperationException("The voice service did not return an audio download. Try again.");
        ValidateAudioUri(uri, provider);
        return uri;
    }

    private static bool IsReadyStatus(JsonElement status) =>
        status.ValueKind == JsonValueKind.Number && status.TryGetInt32(out var number) && number == 1
        || status.ValueKind == JsonValueKind.String && status.GetString() == "1";

    private static string? ReadString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String ? property.GetString() : null;

    private static string SpeechGenError(JsonElement root)
    {
        // The provider may echo submitted data, so never display its raw error string.
        var error = ReadString(root, "error")?.ToLowerInvariant() ?? "";
        if (error.Contains("token") || error.Contains("email") || error.Contains("auth"))
            return "SpeechGen could not verify your account. Check your email and API token in Settings.";
        if (error.Contains("balance") || error.Contains("balans") || error.Contains("credit") || error.Contains("limit"))
            return "SpeechGen has insufficient credits or a usage limit. Check your SpeechGen account.";
        return "SpeechGen could not create the Justin voice clip. Check your account and try again.";
    }

    private static void ValidateAudioUri(Uri uri, string provider)
    {
        var domain = provider == "streamlabs" ? "streamlabs.com" : "speechgen.io";
        if (uri.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(uri.UserInfo) || !uri.IsDefaultPort
            || !(uri.IdnHost.Equals(domain, StringComparison.OrdinalIgnoreCase)
                 || uri.IdnHost.EndsWith("." + domain, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("The voice service returned an unsupported audio download address.");
    }

    private static void EnsureSuccess(HttpResponseMessage response, string provider)
    {
        if (response.IsSuccessStatusCode) return;
        if (response.StatusCode == HttpStatusCode.TooManyRequests)
            throw new InvalidOperationException("The voice service is busy or limiting requests. Wait a moment before trying again.");
        if (provider == "lazybird")
        {
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                throw new InvalidOperationException("Lazybird could not verify your API key. Check it in Settings.");
            if (response.StatusCode is HttpStatusCode.PaymentRequired or HttpStatusCode.UnprocessableEntity)
                throw new InvalidOperationException("Lazybird could not generate speech with your current plan or credits. Check your Lazybird account.");
            if (response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.NotFound)
                throw new InvalidOperationException("Lazybird could not use the selected voice. Check its availability and try again.");
        }
        if (provider == "fish")
        {
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                throw new InvalidOperationException("Fish Audio could not verify your API key. Check it in Settings.");
            if (response.StatusCode == HttpStatusCode.PaymentRequired)
                throw new InvalidOperationException("Fish Audio declined this free-tier request. Check your account's API access and free-tier availability.");
            if (response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.NotFound or HttpStatusCode.UnprocessableEntity)
                throw new InvalidOperationException("Fish Audio could not use the selected voice. Check its availability and try again.");
        }
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            throw new InvalidOperationException(provider == "speechgen"
                ? "SpeechGen could not verify your account. Check your email and API token in Settings."
                : "Streamlabs is unavailable for Justin right now. Try later or select SpeechGen in Settings.");
        throw new InvalidOperationException("The voice service is unavailable right now. Try again later.");
    }

    private static async Task<byte[]> ReadAudioResponseAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var mediaType = response.Content.Headers.ContentType?.MediaType;
        if (mediaType is not null && !mediaType.StartsWith("audio/", StringComparison.OrdinalIgnoreCase)
            && !mediaType.Equals("application/octet-stream", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The voice service returned a page or error instead of audio. Try again.");
        var audio = await ReadBoundedAsync(response.Content, MaximumAudioBytes,
            "The voice clip is too large. Shorten the message and try again.", cancellationToken).ConfigureAwait(false);
        if (!HasSupportedAudioSignature(audio))
            throw new InvalidOperationException("The voice service returned invalid audio. Try again.");
        return audio;
    }

    private static async Task<byte[]> ReadBoundedAsync(HttpContent content, int maximumBytes, string sizeError,
        CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength is > 0 && content.Headers.ContentLength > maximumBytes)
            throw new InvalidOperationException(sizeError);
        await using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var output = new MemoryStream();
        var buffer = new byte[16 * 1024];
        while (true)
        {
            var count = await stream.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (count == 0) break;
            if (output.Length + count > maximumBytes)
                throw new InvalidOperationException(sizeError);
            output.Write(buffer, 0, count);
        }
        return output.ToArray();
    }

    private static bool HasSupportedAudioSignature(byte[] data)
    {
        // Both formats are supported by the Windows audio decoder; reject HTML / JSON error bodies.
        if (data.Length >= 44 && data.AsSpan(0, 4).SequenceEqual("RIFF"u8)
            && data.AsSpan(8, 4).SequenceEqual("WAVE"u8)) return true;
        var start = 0;
        if (data.Length >= 10 && data.AsSpan(0, 3).SequenceEqual("ID3"u8))
        {
            if ((data[6] | data[7] | data[8] | data[9]) >= 128) return false;
            start = 10 + (data[6] << 21 | data[7] << 14 | data[8] << 7 | data[9]);
            if ((data[5] & 0x10) != 0) start += 10;
        }
        for (var i = start; i < data.Length - 3 && i < start + 1024; i++)
        {
            if (data[i] != 0xff || (data[i + 1] & 0xe0) != 0xe0) continue;
            var version = (data[i + 1] >> 3) & 3;
            var layer = (data[i + 1] >> 1) & 3;
            var bitRate = data[i + 2] >> 4;
            var sampleRate = (data[i + 2] >> 2) & 3;
            if (version != 1 && layer == 1 && bitRate is > 0 and < 15 && sampleRate != 3) return true;
        }
        return false;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_offline.IsValueCreated) _offline.Value.Dispose();
        if (_ownsClient) _client.Dispose();
    }
}
