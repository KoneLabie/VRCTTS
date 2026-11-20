using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using VRCTTS.Services;

var passed = 0;
var mp3 = new byte[] { 0xff, 0xfb, 0x90, 0x00, 0x00, 0x01, 0x02, 0x03 };
const string signedUrl = "https://polly.streamlabs.com/v1/speech?private=secret";

await Check("Streamlabs Justin form, required referrer, and MP3", async () =>
{
    var calls = 0;
    using var client = Client(async (request, _) =>
    {
        calls++;
        if (calls == 1)
        {
            Equal(HttpMethod.Post, request.Method);
            Equal("https://streamlabs.com/polly/speak", request.RequestUri!.AbsoluteUri);
            Equal("https://streamlabs.com/", request.Headers.Referrer!.AbsoluteUri);
            var form = await request.Content!.ReadAsStringAsync();
            Equal("voice=Justin&text=Hello+%26+world%21", form);
            return Json(new { success = true, speak_url = signedUrl });
        }
        Equal(signedUrl, request.RequestUri!.AbsoluteUri);
        return Audio(mp3);
    });
    using var service = new TtsService(client);
    var result = await service.SynthesizeAsync("Hello & world!", "streamlabs", null, null, default);
    True(result.SequenceEqual(mp3));
    Equal(2, calls);
});

await Check("SpeechGen explicitly selected with Justin and credentials", async () =>
{
    var calls = 0;
    using var client = Client(async (request, _) =>
    {
        if (++calls == 1)
        {
            Equal("https://speechgen.io/index.php?r=api/text", request.RequestUri!.AbsoluteUri);
            True(request.Headers.Referrer is null);
            var form = await request.Content!.ReadAsStringAsync();
            True(form.Contains("voice=Justin") && form.Contains("email=test%40example.com") && form.Contains("token=private"));
            True(form.Contains("format=mp3") && form.Contains("speed=1") && form.Contains("pitch=0"));
            return Json(new { status = 1, file = "https://speechgen.io/texttomp3/test.mp3" });
        }
        return Audio(mp3);
    });
    using var service = new TtsService(client);
    await service.SynthesizeAsync("Hello", "speechgen", "test@example.com", "private", default);
    Equal(2, calls);
});

await Check("SpeechGen string success status", async () =>
{
    using var client = Client((request, _) => Task.FromResult(request.Method == HttpMethod.Post
        ? Json(new { status = "1", file = "https://speechgen.io/clip.mp3" }) : Audio(mp3)));
    using var service = new TtsService(client);
    await service.SynthesizeAsync("Hello", "speechgen", "test@example.com", "private", default);
});

await Check("Streamlabs UTF-8 limit and missing SpeechGen settings before network", async () =>
{
    using var client = Client((_, _) => throw new Exception("Unexpected request"));
    using var service = new TtsService(client);
    await Throws<ArgumentException>(() => service.SynthesizeAsync(new string('é', 276), "streamlabs", null, null, default));
    await Throws<ArgumentException>(() => service.SynthesizeAsync(new string('a', 2001), "speechgen", "x", "x", default));
    await Throws<InvalidOperationException>(() => service.SynthesizeAsync("Hello", "speechgen", null, "x", default));
    await Throws<ArgumentException>(() => service.SynthesizeAsync("Hello", "other", null, null, default));
    await Throws<ArgumentException>(() => service.SynthesizeAsync("  ", "streamlabs", null, null, default));
});

await Check("Non-HTTPS, user info, foreign host, and port rejected", async () =>
{
    foreach (var address in new[] { "http://polly.streamlabs.com/clip", "https://streamlabs.com.evil.example/clip", "https://user@polly.streamlabs.com/clip", "https://polly.streamlabs.com:8443/clip", "file:///clip.mp3" })
    {
        var calls = 0;
        using var client = Client((_, _) => { calls++; return Task.FromResult(Json(new { success = true, speak_url = address })); });
        using var service = new TtsService(client);
        var error = await Throws<InvalidOperationException>(() => service.SynthesizeAsync("Hello", "streamlabs", null, null, default));
        True(!error.ToString().Contains(address));
        Equal(1, calls);
    }
});

await Check("HTTP errors, negative status, and no silent fallback", async () =>
{
    foreach (var status in new[] { HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden, HttpStatusCode.TooManyRequests, HttpStatusCode.InternalServerError })
    {
        var calls = 0;
        using var client = Client((_, _) => { calls++; return Task.FromResult(new HttpResponseMessage(status)); });
        using var service = new TtsService(client);
        await Throws<InvalidOperationException>(() => service.SynthesizeAsync("Hello", "streamlabs", null, null, default));
        Equal(1, calls);
    }
    using var failedClient = Client((_, _) => Task.FromResult(Json(new { status = -1, error = "bad token private and text Hello https://secret.example" })));
    using var failedService = new TtsService(failedClient);
    var error = await Throws<InvalidOperationException>(() => failedService.SynthesizeAsync("Hello", "speechgen", "test@example.com", "private", default));
    True(!error.ToString().Contains("private") && !error.ToString().Contains("Hello") && !error.ToString().Contains("https://"));
});

await Check("HTML and JSON masquerading as audio are rejected", async () =>
{
    foreach (var body in new[] { "<!doctype html><html>Unavailable</html>", "{\"error\":\"bad\"}", "ID3not-a-real-audio-file" })
    {
        using var client = Client((request, _) => Task.FromResult(request.Method == HttpMethod.Post
            ? Json(new { success = true, speak_url = signedUrl }) : Audio(Encoding.UTF8.GetBytes(body))));
        using var service = new TtsService(client);
        await Throws<InvalidOperationException>(() => service.SynthesizeAsync("Hello", "streamlabs", null, null, default));
    }
    using var typeClient = Client((request, _) => Task.FromResult(request.Method == HttpMethod.Post
        ? Json(new { success = true, speak_url = signedUrl }) : Audio(mp3, "text/html")));
    using var typeService = new TtsService(typeClient);
    await Throws<InvalidOperationException>(() => typeService.SynthesizeAsync("Hello", "streamlabs", null, null, default));
});

await Check("Audio content-length bound", async () =>
{
    using var client = Client((request, _) =>
    {
        if (request.Method == HttpMethod.Post) return Task.FromResult(Json(new { success = true, speak_url = signedUrl }));
        var response = Audio(mp3);
        response.Content.Headers.ContentLength = TtsService.MaximumAudioBytes + 1L;
        return Task.FromResult(response);
    });
    using var service = new TtsService(client);
    await Throws<InvalidOperationException>(() => service.SynthesizeAsync("Hello", "streamlabs", null, null, default));
});

await Check("Chunked audio bound without content length", async () =>
{
    using var client = Client((request, _) =>
    {
        if (request.Method == HttpMethod.Post) return Task.FromResult(Json(new { success = true, speak_url = signedUrl }));
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new GeneratedStream(TtsService.MaximumAudioBytes + 1L)) };
        response.Content.Headers.ContentType = new MediaTypeHeaderValue("audio/mpeg");
        return Task.FromResult(response);
    });
    using var service = new TtsService(client);
    await Throws<InvalidOperationException>(() => service.SynthesizeAsync("Hello", "streamlabs", null, null, default));
});

await Check("JSON body bound", async () =>
{
    using var client = Client((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(new string(' ', 65537)) }));
    using var service = new TtsService(client);
    await Throws<InvalidOperationException>(() => service.SynthesizeAsync("Hello", "streamlabs", null, null, default));
});

await Check("Caller cancellation before request, during request, and during body", async () =>
{
    using var cancelled = new CancellationTokenSource();
    cancelled.Cancel();
    using var client = Client((_, _) => throw new Exception("Unexpected request"));
    using var service = new TtsService(client);
    await Throws<OperationCanceledException>(() => service.SynthesizeAsync("Hello", "streamlabs", null, null, cancelled.Token));
    using var waitingClient = Client(async (_, token) => { await Task.Delay(Timeout.Infinite, token); return Json(new { }); });
    using var waitingService = new TtsService(waitingClient);
    using var cancellation = new CancellationTokenSource(30);
    await Throws<OperationCanceledException>(() => waitingService.SynthesizeAsync("Hello", "streamlabs", null, null, cancellation.Token));
    using var bodyClient = Client((request, _) => Task.FromResult(request.Method == HttpMethod.Post
        ? Json(new { success = true, speak_url = signedUrl })
        : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new BlockingStream()) }));
    using var bodyService = new TtsService(bodyClient);
    using var bodyCancellation = new CancellationTokenSource(30);
    await Throws<OperationCanceledException>(() => bodyService.SynthesizeAsync("Hello", "streamlabs", null, null, bodyCancellation.Token));
});

await Check("Provider request timeout is sanitized", async () =>
{
    using var client = Client((_, _) => throw new TaskCanceledException("Timed out https://private.example/token"));
    using var service = new TtsService(client);
    var error = await Throws<InvalidOperationException>(() => service.SynthesizeAsync("Hello", "streamlabs", null, null, default));
    True(error.Message.Contains("took too long") && !error.ToString().Contains("private.example"));
});

await Check("Signed URLs do not leak in network errors", async () =>
{
    using var client = Client((request, _) => request.Method == HttpMethod.Post
        ? Task.FromResult(Json(new { success = true, speak_url = signedUrl }))
        : throw new HttpRequestException("Failed " + signedUrl));
    using var service = new TtsService(client);
    var error = await Throws<InvalidOperationException>(() => service.SynthesizeAsync("Hello", "streamlabs", null, null, default));
    True(!error.ToString().Contains("secret") && !error.ToString().Contains("https://"));
});

await Check("Injected HttpClient remains caller-owned", async () =>
{
    using var client = Client((_, _) => Task.FromResult(Json(new { success = false })));
    var service = new TtsService(client);
    service.Dispose();
    service.Dispose();
    await client.GetAsync("https://example.com");
    await Throws<ObjectDisposedException>(() => service.SynthesizeAsync("Hello", "streamlabs", null, null, default));
});

await Check("Offline mode never makes a network request or requires credentials", async () =>
{
    var calls = 0;
    using var client = Client((_, _) => { calls++; throw new Exception("Offline speech attempted a network request"); });
    using var service = new TtsService(client);
    using var canceled = new CancellationTokenSource();
    canceled.Cancel();
    await Throws<OperationCanceledException>(() => service.SynthesizeAsync("Hello.", "offline", null, null, canceled.Token));
    if (service.GetOfflineVoices().Any(voice => voice.Enabled))
    {
        var wav = await service.SynthesizeAsync("Hello from offline Justin.", "offline", null, null, default);
        True(wav.AsSpan(0, 4).SequenceEqual("RIFF"u8));
    }
    else
        await Throws<InvalidOperationException>(() => service.SynthesizeAsync("Hello.", "offline", null, null, default));
    Equal(0, calls);
});

await Check("Fish uses exact linked voice, free model, Bearer key, and one direct MP3 response", async () =>
{
    var calls = 0;
    using var client = Client(async (request, _) =>
    {
        calls++;
        Equal(HttpMethod.Post, request.Method);
        Equal("https://api.fish.audio/v1/tts", request.RequestUri!.AbsoluteUri);
        Equal("Bearer", request.Headers.Authorization!.Scheme);
        Equal("fish-private", request.Headers.Authorization.Parameter);
        Equal("s2.1-pro-free", request.Headers.GetValues("model").Single());
        Equal("application/json", request.Content!.Headers.ContentType!.MediaType);
        True(request.Headers.Referrer is null);
        using var body = JsonDocument.Parse(await request.Content.ReadAsStringAsync());
        Equal(4, body.RootElement.EnumerateObject().Count());
        Equal("mamagüevo & ¡hola!", body.RootElement.GetProperty("text").GetString());
        Equal("acc8237220d8470985ec9be6c4c480a9", body.RootElement.GetProperty("reference_id").GetString());
        Equal("mp3", body.RootElement.GetProperty("format").GetString());
        Equal("low", body.RootElement.GetProperty("latency").GetString());
        return Audio(mp3);
    });
    using var service = new TtsService(client);
    var result = await service.SynthesizeAsync("mamagüevo & ¡hola!", "fish", "unused-email", "unused-speechgen-token", default, " fish-private ");
    True(result.SequenceEqual(mp3));
    Equal(1, calls);
});

await Check("Fish validates missing keys, malformed keys, and app text cap before network", async () =>
{
    var calls = 0;
    using var client = Client((_, _) => { calls++; return Task.FromResult(Audio(mp3)); });
    using var service = new TtsService(client);
    foreach (var key in new string?[] { null, "", " ", "private\r\nvalue", "private value", "private\0value" })
    {
        var error = await Throws<InvalidOperationException>(() => service.SynthesizeAsync("Hello", "fish", null, null, default, key));
        True(!error.ToString().Contains("private"));
    }
    await Throws<ArgumentException>(() => service.SynthesizeAsync(new string('a', 2001), "fish", null, null, default, "fish-private"));
    Equal(0, calls);
    await service.SynthesizeAsync(new string('a', 2000), "fish", null, null, default, "fish-private");
    Equal(1, calls);
});

await Check("Fish errors are sanitized and never retry with a paid model or other voice", async () =>
{
    foreach (var status in new[] { HttpStatusCode.BadRequest, HttpStatusCode.Unauthorized, HttpStatusCode.PaymentRequired,
        HttpStatusCode.Forbidden, HttpStatusCode.NotFound, HttpStatusCode.UnprocessableEntity,
        HttpStatusCode.TooManyRequests, HttpStatusCode.ServiceUnavailable, HttpStatusCode.Redirect })
    {
        var calls = 0;
        using var client = Client((request, _) =>
        {
            calls++;
            Equal("s2.1-pro-free", request.Headers.GetValues("model").Single());
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent("private-key private-message https://private.example")
            });
        });
        using var service = new TtsService(client);
        var error = await Throws<InvalidOperationException>(() => service.SynthesizeAsync("private-message", "fish", null, null, default, "private-key"));
        True(!error.ToString().Contains("private") && !error.ToString().Contains("https://"));
        if (status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden) True(error.Message.Contains("API key"));
        if (status == HttpStatusCode.PaymentRequired) True(error.Message.Contains("free-tier"));
        Equal(1, calls);
    }
});

await Check("Fish rejects invalid direct audio and misleading response content types", async () =>
{
    foreach (var body in new[] { Array.Empty<byte>(), "<html>private error</html>"u8.ToArray(), "{\"error\":\"private\"}"u8.ToArray() })
    {
        using var client = Client((_, _) => Task.FromResult(Audio(body)));
        using var service = new TtsService(client);
        var error = await Throws<InvalidOperationException>(() => service.SynthesizeAsync("Hello", "fish", null, null, default, "fish-private"));
        True(!error.ToString().Contains("private"));
    }
    using var typeClient = Client((_, _) => Task.FromResult(Audio(mp3, "application/json")));
    using var typeService = new TtsService(typeClient);
    await Throws<InvalidOperationException>(() => typeService.SynthesizeAsync("Hello", "fish", null, null, default, "fish-private"));
});

await Check("Fish bounds content-length and chunked direct audio to 8 MiB", async () =>
{
    using var lengthClient = Client((_, _) =>
    {
        var response = Audio(mp3);
        response.Content.Headers.ContentLength = TtsService.MaximumAudioBytes + 1L;
        return Task.FromResult(response);
    });
    using var lengthService = new TtsService(lengthClient);
    await Throws<InvalidOperationException>(() => lengthService.SynthesizeAsync("Hello", "fish", null, null, default, "fish-private"));
    using var chunkClient = Client((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
    {
        Content = new StreamContent(new GeneratedStream(TtsService.MaximumAudioBytes + 1L))
    }));
    using var chunkService = new TtsService(chunkClient);
    await Throws<InvalidOperationException>(() => chunkService.SynthesizeAsync("Hello", "fish", null, null, default, "fish-private"));
});

await Check("Fish honors cancellation before request, during request, and during direct audio body", async () =>
{
    using var canceled = new CancellationTokenSource();
    canceled.Cancel();
    using var client = Client((_, _) => throw new Exception("Unexpected request"));
    using var service = new TtsService(client);
    await Throws<OperationCanceledException>(() => service.SynthesizeAsync("Hello", "fish", null, null, canceled.Token, "fish-private"));
    using var waitingClient = Client(async (_, token) => { await Task.Delay(Timeout.Infinite, token); return Audio(mp3); });
    using var waitingService = new TtsService(waitingClient);
    using var requestCancellation = new CancellationTokenSource(30);
    await Throws<OperationCanceledException>(() => waitingService.SynthesizeAsync("Hello", "fish", null, null, requestCancellation.Token, "fish-private"));
    using var bodyClient = Client((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
    {
        Content = new StreamContent(new BlockingStream())
    }));
    using var bodyService = new TtsService(bodyClient);
    using var bodyCancellation = new CancellationTokenSource(30);
    await Throws<OperationCanceledException>(() => bodyService.SynthesizeAsync("Hello", "fish", null, null, bodyCancellation.Token, "fish-private"));
});

await Check("Fish timeout, network, and interrupted-body errors do not expose secrets", async () =>
{
    foreach (var failure in new Exception[] { new TaskCanceledException("private-key https://private.example"),
        new HttpRequestException("private-key https://private.example"), new IOException("private-key https://private.example") })
    {
        using var client = Client((_, _) => throw failure);
        using var service = new TtsService(client);
        var error = await Throws<InvalidOperationException>(() => service.SynthesizeAsync("Hello", "fish", null, null, default, "fish-private"));
        True(!error.ToString().Contains("private") && !error.ToString().Contains("https://"));
    }
});

await Check("Known Fish voice selections use their own reference; unknown Fish ids are rejected", async () =>
{
    var calls = 0;
    using var client = Client(async (request, _) =>
    {
        calls++;
        Equal("https://api.fish.audio/v1/tts", request.RequestUri!.AbsoluteUri);
        Equal("s2.1-pro-free", request.Headers.GetValues("model").Single());
        using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
        Equal("ee5e93f259c84f77b15a18dccd6b031c", body.RootElement.GetProperty("reference_id").GetString());
        return Audio(mp3);
    });
    using var service = new TtsService(client);
    await service.SynthesizeAsync("Hello", VoiceCatalog.XqcVoiceId, null, null, default, "fish-private");
    Equal(1, calls);
    await Throws<ArgumentException>(() => service.SynthesizeAsync("Hello", "fish:unknown", null, null, default, "fish-private"));
    Equal(1, calls);
});

await Check("Every catalog Fish voice routes to its selected reference instead of the default", async () =>
{
    var fishVoices = VoiceCatalog.Options.Where(voice => voice.FishReferenceId is not null).ToArray();
    True(fishVoices.Length >= 2);
    Equal(fishVoices.Length, fishVoices.Select(voice => voice.Id).Distinct(StringComparer.Ordinal).Count());
    foreach (var voice in fishVoices)
    {
        var calls = 0;
        using var client = Client(async (request, _) =>
        {
            calls++;
            Equal("https://api.fish.audio/v1/tts", request.RequestUri!.AbsoluteUri);
            Equal("s2.1-pro-free", request.Headers.GetValues("model").Single());
            Equal("catalog-private", request.Headers.Authorization!.Parameter);
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            Equal(voice.FishReferenceId, body.RootElement.GetProperty("reference_id").GetString());
            return Audio(mp3);
        });
        using var service = new TtsService(client);
        await service.SynthesizeAsync("Hello", voice.Id, null, null, default, "catalog-private");
        Equal(1, calls);
    }
});

await Check("Lazybird sends selected Hasan voice and X-API-Key with one direct MP3 response", async () =>
{
    var calls = 0;
    var offlineCalls = 0;
    using var client = Client(async (request, _) =>
    {
        calls++;
        Equal(HttpMethod.Post, request.Method);
        Equal("https://api.lazybird.app/v2/generate-speech", request.RequestUri!.AbsoluteUri);
        Equal("lazybird-private", request.Headers.GetValues("X-API-Key").Single());
        True(request.Headers.Authorization is null && !request.Headers.Contains("model"));
        Equal("application/json", request.Content!.Headers.ContentType!.MediaType);
        using var body = JsonDocument.Parse(await request.Content.ReadAsStringAsync());
        Equal(2, body.RootElement.EnumerateObject().Count());
        Equal("396g2687JGhEZqvyoonWku", body.RootElement.GetProperty("voiceId").GetString());
        Equal("¡Hola, mamagüevo!", body.RootElement.GetProperty("text").GetString());
        return Audio(mp3);
    });
    using var service = new TtsService(client, (_, _) => { offlineCalls++; throw new Exception("Unexpected fallback"); });
    var result = await service.SynthesizeAsync("¡Hola, mamagüevo!", "lazybird:hasan-piker", "unused-email", "unused-token", default, "unused-fish-key", " lazybird-private ");
    True(result.SequenceEqual(mp3) && !service.LastSynthesisWasOffline);
    Equal(1, calls);
    Equal(0, offlineCalls);
});

await Check("Lazybird validates its own missing key and 2,000-character app cap before network", async () =>
{
    var calls = 0;
    using var client = Client((_, _) => { calls++; return Task.FromResult(Audio(mp3)); });
    using var service = new TtsService(client);
    foreach (var key in new string?[] { null, "", " ", "private key", "private\r\nkey", "private\0key" })
    {
        var error = await Throws<InvalidOperationException>(() => service.SynthesizeAsync("Hello", "lazybird:hasan-piker", null, null, default, "fish-private", key));
        True(!error.ToString().Contains("private"));
    }
    await Throws<ArgumentException>(() => service.SynthesizeAsync(new string('a', 2001), "lazybird:hasan-piker", null, null, default, null, "lazybird-private"));
    await Throws<ArgumentException>(() => service.SynthesizeAsync("Hello", "lazybird:unknown", null, null, default, null, "lazybird-private"));
    Equal(0, calls);
    await service.SynthesizeAsync(new string('a', 2000), "lazybird:hasan-piker", null, null, default, null, "lazybird-private");
    Equal(1, calls);
});

await Check("Lazybird HTTP failures are sanitized, explain auth or credits, and never change provider", async () =>
{
    var offlineCalls = 0;
    foreach (var status in new[] { HttpStatusCode.BadRequest, HttpStatusCode.Unauthorized, HttpStatusCode.PaymentRequired,
        HttpStatusCode.Forbidden, HttpStatusCode.NotFound, HttpStatusCode.UnprocessableEntity,
        HttpStatusCode.TooManyRequests, HttpStatusCode.ServiceUnavailable, HttpStatusCode.Redirect })
    {
        var calls = 0;
        using var client = Client((request, _) =>
        {
            calls++;
            Equal("api.lazybird.app", request.RequestUri!.Host);
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent("private-key private-message https://private.example")
            });
        });
        using var service = new TtsService(client, (_, _) => { offlineCalls++; return Task.FromResult(mp3); });
        var error = await Throws<InvalidOperationException>(() => service.SynthesizeAsync("private-message", "lazybird:hasan-piker", null, null, default, null, "private-key"));
        True(!error.ToString().Contains("private") && !error.ToString().Contains("https://"));
        if (status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden) True(error.Message.Contains("API key"));
        if (status is HttpStatusCode.PaymentRequired or HttpStatusCode.UnprocessableEntity) True(error.Message.Contains("plan or credits"));
        Equal(1, calls);
    }
    Equal(0, offlineCalls);
});

await Check("Lazybird rejects invalid direct audio and bounds length or chunked bodies to 8 MiB", async () =>
{
    foreach (var body in new[] { Array.Empty<byte>(), "<html>private error</html>"u8.ToArray(), "{\"error\":\"private\"}"u8.ToArray() })
    {
        using var client = Client((_, _) => Task.FromResult(Audio(body)));
        using var service = new TtsService(client);
        var error = await Throws<InvalidOperationException>(() => service.SynthesizeAsync("Hello", "lazybird:hasan-piker", null, null, default, null, "lazybird-private"));
        True(!error.ToString().Contains("private"));
    }
    using var typeClient = Client((_, _) => Task.FromResult(Audio(mp3, "application/json")));
    using var typeService = new TtsService(typeClient);
    await Throws<InvalidOperationException>(() => typeService.SynthesizeAsync("Hello", "lazybird:hasan-piker", null, null, default, null, "lazybird-private"));
    using var lengthClient = Client((_, _) =>
    {
        var response = Audio(mp3);
        response.Content.Headers.ContentLength = TtsService.MaximumAudioBytes + 1L;
        return Task.FromResult(response);
    });
    using var lengthService = new TtsService(lengthClient);
    await Throws<InvalidOperationException>(() => lengthService.SynthesizeAsync("Hello", "lazybird:hasan-piker", null, null, default, null, "lazybird-private"));
    using var chunkClient = Client((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
    {
        Content = new StreamContent(new GeneratedStream(TtsService.MaximumAudioBytes + 1L))
    }));
    using var chunkService = new TtsService(chunkClient);
    await Throws<InvalidOperationException>(() => chunkService.SynthesizeAsync("Hello", "lazybird:hasan-piker", null, null, default, null, "lazybird-private"));
});

await Check("Lazybird honors cancellation before request, during request, and during streamed body", async () =>
{
    using var preCanceled = new CancellationTokenSource();
    preCanceled.Cancel();
    using var client = Client((_, _) => throw new Exception("Unexpected request"));
    using var service = new TtsService(client);
    await Throws<OperationCanceledException>(() => service.SynthesizeAsync("Hello", "lazybird:hasan-piker", null, null, preCanceled.Token, null, "lazybird-private"));
    using var waitingClient = Client(async (_, token) => { await Task.Delay(Timeout.Infinite, token); return Audio(mp3); });
    using var waitingService = new TtsService(waitingClient);
    using var requestCancellation = new CancellationTokenSource(25);
    await Throws<OperationCanceledException>(() => waitingService.SynthesizeAsync("Hello", "lazybird:hasan-piker", null, null, requestCancellation.Token, null, "lazybird-private"));
    using var bodyClient = Client((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
    {
        Content = new StreamContent(new BlockingStream())
    }));
    using var bodyService = new TtsService(bodyClient);
    using var bodyCancellation = new CancellationTokenSource(25);
    await Throws<OperationCanceledException>(() => bodyService.SynthesizeAsync("Hello", "lazybird:hasan-piker", null, null, bodyCancellation.Token, null, "lazybird-private"));
});

await Check("Lazybird timeout and network errors never expose API keys or message content", async () =>
{
    foreach (var failure in new Exception[] { new TaskCanceledException("private-key private-message https://private.example"),
        new HttpRequestException("private-key private-message https://private.example"), new IOException("private-key private-message https://private.example") })
    {
        using var client = Client((_, _) => throw failure);
        using var service = new TtsService(client);
        var error = await Throws<InvalidOperationException>(() => service.SynthesizeAsync("private-message", "lazybird:hasan-piker", null, null, default, null, "private-key"));
        True(!error.ToString().Contains("private") && !error.ToString().Contains("https://"));
    }
});

await Check("Justin automatically uses successful online speech without a ping or offline call", async () =>
{
    var calls = 0;
    var offlineCalls = 0;
    using var client = Client((request, _) =>
    {
        calls++;
        return Task.FromResult(request.Method == HttpMethod.Post
            ? Json(new { success = true, speak_url = signedUrl }) : Audio(mp3));
    });
    using var service = new TtsService(client, (_, _) => { offlineCalls++; throw new Exception("Unexpected offline call"); });
    var result = await service.SynthesizeAsync("Hello", "justin", null, null, default);
    True(result.SequenceEqual(mp3));
    Equal(2, calls);
    Equal(0, offlineCalls);
    True(!service.LastSynthesisWasOffline);
});

await Check("Justin falls back to original offline engine after network or HTTP failure", async () =>
{
    foreach (var failure in new object[] { new HttpRequestException("private network"), HttpStatusCode.ServiceUnavailable,
        HttpStatusCode.Unauthorized, HttpStatusCode.TooManyRequests, "invalid-json", "invalid-audio" })
    {
        var calls = 0;
        var offlineCalls = 0;
        using var client = Client((request, _) =>
        {
            calls++;
            if (failure is Exception error) throw error;
            if (failure is HttpStatusCode status) return Task.FromResult(new HttpResponseMessage(status));
            if ((string)failure == "invalid-json") return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{broken")
            });
            return Task.FromResult(request.Method == HttpMethod.Post
                ? Json(new { success = true, speak_url = signedUrl }) : Audio("{\"error\":\"bad\"}"u8.ToArray()));
        });
        using var service = new TtsService(client, (text, token) =>
        {
            offlineCalls++;
            Equal("Hello", text);
            token.ThrowIfCancellationRequested();
            return Task.FromResult(mp3);
        });
        var result = await service.SynthesizeAsync("Hello", "justin", null, null, default);
        True(result.SequenceEqual(mp3));
        True(service.LastSynthesisWasOffline);
        Equal(1, offlineCalls);
        Equal(failure is string name && name == "invalid-audio" ? 2 : 1, calls);
    }
});

await Check("Justin online attempt timeout covers request and streamed audio before fallback", async () =>
{
    foreach (var blockBody in new[] { false, true })
    {
        var offlineCalls = 0;
        using var client = Client(async (request, token) =>
        {
            if (!blockBody) { await Task.Delay(Timeout.Infinite, token); return Audio(mp3); }
            return request.Method == HttpMethod.Post ? Json(new { success = true, speak_url = signedUrl })
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new BlockingStream()) };
        });
        using var service = new TtsService(client, (_, token) =>
        {
            token.ThrowIfCancellationRequested();
            offlineCalls++;
            return Task.FromResult(mp3);
        }, TimeSpan.FromMilliseconds(25));
        var result = await service.SynthesizeAsync("Hello", "justin", null, null, default);
        True(result.SequenceEqual(mp3) && service.LastSynthesisWasOffline);
        Equal(1, offlineCalls);
    }
});

await Check("Justin validation, pre-cancellation, and Stop never start offline fallback", async () =>
{
    var calls = 0;
    var offlineCalls = 0;
    using var client = Client(async (_, token) =>
    {
        calls++;
        await Task.Delay(Timeout.Infinite, token);
        return Audio(mp3);
    });
    using var service = new TtsService(client, (_, _) => { offlineCalls++; return Task.FromResult(mp3); });
    await Throws<ArgumentException>(() => service.SynthesizeAsync(new string('é', 276), "justin", null, null, default));
    await Throws<ArgumentException>(() => service.SynthesizeAsync(" ", "justin", null, null, default));
    await Throws<ArgumentException>(() => service.SynthesizeAsync("Hello\0", "justin", null, null, default));
    using var preCanceled = new CancellationTokenSource();
    preCanceled.Cancel();
    await Throws<OperationCanceledException>(() => service.SynthesizeAsync("Hello", "justin", null, null, preCanceled.Token));
    Equal(0, calls);
    using var stopped = new CancellationTokenSource(25);
    await Throws<OperationCanceledException>(() => service.SynthesizeAsync("Hello", "justin", null, null, stopped.Token));
    Equal(1, calls);
    Equal(0, offlineCalls);
    True(!service.LastSynthesisWasOffline);
});

await Check("Justin Stop while offline fallback runs cancels without reporting a voice failure", async () =>
{
    var enteredOffline = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    using var client = Client((_, _) => throw new HttpRequestException("Offline"));
    using var service = new TtsService(client, async (_, token) =>
    {
        enteredOffline.TrySetResult();
        await Task.Delay(Timeout.Infinite, token);
        return mp3;
    });
    using var stopped = new CancellationTokenSource();
    var operation = service.SynthesizeAsync("Hello", "justin", null, null, stopped.Token);
    await enteredOffline.Task;
    stopped.Cancel();
    await Throws<OperationCanceledException>(() => operation);
    True(!service.LastSynthesisWasOffline);
});

await Check("Justin explains original voice installation and activation if both sources fail", async () =>
{
    using var client = Client((_, _) => throw new HttpRequestException("private-online"));
    using var service = new TtsService(client, (_, _) => throw new InvalidOperationException("private-offline"));
    var error = await Throws<InvalidOperationException>(() => service.SynthesizeAsync("Hello", "justin", null, null, default));
    True(error.Message.Contains("unavailable online") && error.Message.Contains("IVONA Justin")
        && error.Message.Contains("installed and activated") && !error.ToString().Contains("private"));
    True(!service.LastSynthesisWasOffline);
});

await Check("Fish failures never use Justin fallback and synthesis source resets on recovery", async () =>
{
    var fail = true;
    var offlineCalls = 0;
    using var client = Client((request, _) =>
    {
        if (fail) throw new HttpRequestException("Unavailable");
        if (request.RequestUri!.Host == "api.fish.audio") return Task.FromResult(Audio(mp3));
        return Task.FromResult(request.Method == HttpMethod.Post
            ? Json(new { success = true, speak_url = signedUrl }) : Audio(mp3));
    });
    using var service = new TtsService(client, (_, _) => { offlineCalls++; return Task.FromResult(mp3); });
    await Throws<InvalidOperationException>(() => service.SynthesizeAsync("Hello", "fish", null, null, default, "fish-private"));
    Equal(0, offlineCalls);
    await service.SynthesizeAsync("Hello", "justin", null, null, default);
    True(service.LastSynthesisWasOffline);
    fail = false;
    await service.SynthesizeAsync("Hello", "justin", null, null, default);
    True(!service.LastSynthesisWasOffline);
    await service.SynthesizeAsync("Hello", "fish", null, null, default, "fish-private");
    True(!service.LastSynthesisWasOffline);
    Equal(1, offlineCalls);
});

Console.WriteLine($"Passed {passed} voice service checks.");
if (args.Contains("--live"))
{
    using var service = new TtsService();
    var audio = await service.SynthesizeAsync("Hello. This is VRCTTS.", "streamlabs", null, null, default);
    var destination = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "live-justin.mp3"));
    await File.WriteAllBytesAsync(destination, audio);
    Console.WriteLine($"Live Justin synthesis passed: {audio.Length} MP3 bytes. Saved {destination}");
}

async Task Check(string label, Func<Task> test)
{
    await test();
    passed++;
    Console.WriteLine("PASS " + label);
}
static void True(bool value) { if (!value) throw new Exception("Assertion failed"); }
static void Equal<T>(T expected, T actual) { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"Expected {expected}; got {actual}"); }
static async Task<T> Throws<T>(Func<Task> action) where T : Exception
{
    try { await action(); }
    catch (T error) { return error; }
    throw new Exception("Expected " + typeof(T).Name);
}
static HttpClient Client(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> response) => new(new FakeHandler(response));
static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };
static HttpResponseMessage Audio(byte[] value, string mediaType = "audio/mpeg")
{
    var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(value) };
    response.Content.Headers.ContentType = new MediaTypeHeaderValue(mediaType);
    return response;
}
sealed class FakeHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => send(request, token);
}
sealed class GeneratedStream(long length) : Stream
{
    private long _position;
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => _position; set => throw new NotSupportedException(); }
    public override int Read(byte[] buffer, int offset, int count)
    {
        var result = (int)Math.Min(count, length - _position);
        buffer.AsSpan(offset, result).Fill(0xff);
        _position += result;
        return result;
    }
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var result = (int)Math.Min(buffer.Length, length - _position);
        buffer.Span[..result].Fill(0xff);
        _position += result;
        return ValueTask.FromResult(result);
    }
    public override void Flush() => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
sealed class BlockingStream : Stream
{
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => 0; set => throw new NotSupportedException(); }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        await Task.Delay(Timeout.Infinite, cancellationToken);
        return 0;
    }
    public override void Flush() => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
