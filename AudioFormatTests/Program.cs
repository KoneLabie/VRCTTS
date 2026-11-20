using System.Buffers.Binary;
using VRCTTS.Services;
using NAudio.Wave;
using NAudio.Codecs;

var passed = 0;
void Check(bool condition, string name)
{
    if (!condition) throw new Exception("FAIL: " + name);
    passed++;
    Console.WriteLine("PASS " + name);
}
void Throws<T>(Action action, string name) where T : Exception
{
    try { action(); }
    catch (T) { Check(true, name); return; }
    throw new Exception("FAIL: expected " + typeof(T).Name + ": " + name);
}

// These tests call only the decoder: no render/capture devices or audio streams are opened.
foreach (var bits in new[] { 8, 16, 24, 32 })
foreach (var channels in new[] { 1, 2 })
{
    var format = new WaveFormat(22050, bits, channels);
    var samples = Enumerable.Range(0, format.BlockAlign * 7001).Select(i => (byte)(i * 37 + 17)).ToArray();
    var decoded = AudioRouter.DecodeAudio(MakeWave(format, samples), CancellationToken.None);
    Check(decoded.Data.SequenceEqual(samples) && decoded.Format.Encoding == WaveFormatEncoding.Pcm &&
          decoded.Format.BitsPerSample == bits && decoded.Format.Channels == channels && decoded.Format.SampleRate == 22050,
          $"PCM {bits}-bit/{channels}-channel WAV preserves exact samples and format");
    using var stream = new RawSourceWaveStream(new MemoryStream(decoded.Data, writable: false), decoded.Format);
    var provider = stream.ToSampleProvider();
    var rendered = new float[14];
    Check(provider.Read(rendered.AsSpan()) == rendered.Length, $"PCM {bits}-bit/{channels}-channel result supports playback adapter");
}

foreach (var bits in new[] { 32, 64 })
{
    var format = WaveFormat.CreateCustomFormat(WaveFormatEncoding.IeeeFloat, 48000, 2, 48000 * 2 * bits / 8, 2 * bits / 8, bits);
    var values = new[] { -1.0, -0.25, 0.0, 0.25, 1.0, 0.125 };
    var samples = values.SelectMany(v => bits == 32 ? BitConverter.GetBytes((float)v) : BitConverter.GetBytes(v)).ToArray();
    var decoded = AudioRouter.DecodeAudio(MakeWave(format, samples), CancellationToken.None);
    Check(decoded.Data.SequenceEqual(samples) && decoded.Format.Encoding == WaveFormatEncoding.IeeeFloat && decoded.Format.BitsPerSample == bits,
        $"IEEE float {bits}-bit WAV preserves exact samples without quantizing");
    using var stream = new RawSourceWaveStream(new MemoryStream(decoded.Data, writable: false), decoded.Format);
    var read = new float[values.Length];
    Check(stream.ToSampleProvider().Read(read.AsSpan()) == values.Length &&
        read.Select((value, index) => value == (float)values[index]).All(equal => equal), $"IEEE float {bits}-bit result reaches sample adapter intact");
}

var extensible = new WaveFormatExtensible(48000, 24, 2);
var extensibleBytes = Enumerable.Range(0, 42).Select(i => (byte)i).ToArray();
var extensibleDecoded = AudioRouter.DecodeAudio(MakeWave(extensible, extensibleBytes), CancellationToken.None);
Check(extensibleDecoded.Data.SequenceEqual(extensibleBytes) && extensibleDecoded.Format.Encoding == WaveFormatEncoding.Extensible,
    "Extensible PCM WAV retains exact samples and extensible format");

var muLawBytes = new byte[] { 0x00, 0x7f, 0xff, 0xd5, 0x80, 0xaa, 0x55, 0x10 };
var muLawDecoded = AudioRouter.DecodeAudio(MakeWave(WaveFormat.CreateMuLawFormat(8000, 1), muLawBytes), CancellationToken.None);
Check(muLawDecoded.Format.Encoding == WaveFormatEncoding.Pcm && muLawDecoded.Format.BitsPerSample == 16 &&
      muLawDecoded.Format.SampleRate == 8000 && muLawDecoded.Data.Length == muLawBytes.Length * 2 &&
      // G.711 negative zero can decode to 0 or -1 across implementations.
      muLawBytes.Select((value, index) => Math.Abs(BinaryPrimitives.ReadInt16LittleEndian(muLawDecoded.Data.AsSpan(index * 2)) -
          MuLawDecoder.MuLawToLinearSample(value)) <= 1).All(equal => equal), "Compressed mu-law WAV falls back to Media Foundation and decodes correct PCM samples");

var empty = MakeWave(new WaveFormat(22050, 16, 1), []);
Throws<InvalidOperationException>(() => AudioRouter.DecodeAudio(empty, CancellationToken.None), "Empty WAV fails without playback");
var truncated = MakeWave(new WaveFormat(22050, 16, 1), [1, 2, 3, 4]);
Array.Resize(ref truncated, truncated.Length - 2);
Throws<InvalidDataException>(() => AudioRouter.DecodeAudio(truncated, CancellationToken.None), "Truncated WAV cannot become partial speech");
var malformed = MakeWave(new WaveFormat(22050, 16, 1), [1, 2]);
BinaryPrimitives.WriteInt16LittleEndian(malformed.AsSpan(32), 0); // fmt.blockAlign in this standard PCM fixture.
Throws<InvalidDataException>(() => AudioRouter.DecodeAudio(malformed, CancellationToken.None), "Malformed WAV frame size fails");
var oversized = new byte[64 * 1024 * 1024 + 2 + empty.Length];
empty.CopyTo(oversized, 0);
BinaryPrimitives.WriteUInt32LittleEndian(oversized.AsSpan(4), (uint)oversized.Length - 8);
BinaryPrimitives.WriteUInt32LittleEndian(oversized.AsSpan(FindDataChunk(oversized) + 4), 64 * 1024 * 1024 + 2);
Throws<InvalidOperationException>(() => AudioRouter.DecodeAudio(oversized, CancellationToken.None), "Declared decoded WAV beyond 64 MiB is rejected before allocation");
using (var canceled = new CancellationTokenSource())
{
    canceled.Cancel();
    Throws<OperationCanceledException>(() => AudioRouter.DecodeAudio(MakeWave(new WaveFormat(22050, 16, 1), [1, 2]), canceled.Token),
        "Pre-canceled decode reads no audio");
}

var mp3Path = args.Length > 0 ? args[0] : Path.GetFullPath("work/VRCTTS/VoiceTests/live-justin.mp3");
if (File.Exists(mp3Path))
{
    var mp3 = AudioRouter.DecodeAudio(await File.ReadAllBytesAsync(mp3Path), CancellationToken.None);
    Check(mp3.Data.Length > 1000 && mp3.Format.Encoding == WaveFormatEncoding.Pcm && mp3.Format.SampleRate > 0,
        "Existing Justin MP3 retains Media Foundation decode fallback");
}
else Console.WriteLine("SKIP Justin MP3 fixture not present");

Console.WriteLine($"All {passed} codec checks passed; no audio endpoints opened.");

static byte[] MakeWave(WaveFormat format, byte[] samples)
{
    using var memory = new MemoryStream();
    using (var writer = new WaveFileWriter(new IgnoreDisposeStream(memory), format)) writer.Write(samples, 0, samples.Length);
    return memory.ToArray();
}
static int FindDataChunk(byte[] data)
{
    for (var offset = 12; offset + 8 <= data.Length;)
    {
        if (data.AsSpan(offset, 4).SequenceEqual("data"u8)) return offset;
        var length = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(offset + 4));
        offset += 8 + length + (length & 1);
    }
    throw new Exception("Test WAV fixture missing data chunk.");
}

sealed class IgnoreDisposeStream(Stream inner) : Stream
{
    public override bool CanRead => inner.CanRead;
    public override bool CanSeek => inner.CanSeek;
    public override bool CanWrite => inner.CanWrite;
    public override long Length => inner.Length;
    public override long Position { get => inner.Position; set => inner.Position = value; }
    public override void Flush() => inner.Flush();
    public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
    public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
    public override void SetLength(long value) => inner.SetLength(value);
    public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);
}
