using VRCTTS.Services;

var checks = 0;
void Check(bool result, string name)
{
    if (!result) throw new InvalidOperationException("FAILED: " + name);
    checks++;
    Console.WriteLine("PASS " + name);
}

const string phrase = "mamagüevo";
var id = Guid.NewGuid();
var cache = new QuickPhraseAudioCache();
bool Get(Guid phraseId, string text = phrase, string mode = "streamlabs", string email = "", string token = "") =>
    cache.TryGet(phraseId, text, mode, email, token, out _);
void Put(Guid phraseId, byte[] clip, string text = phrase, string mode = "streamlabs", string email = "", string token = "") =>
    cache.Store(phraseId, text, mode, email, token, clip);

Check(!cache.TryGet(id, phrase, "streamlabs", "", "", out var missing) && missing.Length == 0,
    "new cache misses without returning audio");
var source = new byte[] { 1, 2, 3 };
Put(id, source);
source[0] = 99;
Check(cache.TryGet(id, phrase, "streamlabs", "", "", out var first) && first.SequenceEqual(new byte[] { 1, 2, 3 }),
    "stored clip has an independent owned copy");
first[1] = 99;
Check(cache.TryGet(id, phrase, "streamlabs", "", "", out var second) && second.SequenceEqual(new byte[] { 1, 2, 3 }),
    "retrieved clip cannot mutate later playback");
Check(!Get(Guid.NewGuid()) && !Get(id, text: "mamagüevos") && !Get(id, text: "MAMAGÜEVO")
    && !Get(id, text: "mamagüevo ") && !Get(id, text: "mamaguevo"),
    "different phrase identity or exact accented text cannot reuse audio");
Check(!Get(id, mode: "speechgen") && !Get(id, mode: "Streamlabs") && !Get(id, email: "account") && !Get(id, token: "secret"),
    "provider or credential changes invalidate the request");
Put(id, [4], email: "a", token: "bc");
Check(Get(id, email: "a", token: "bc") && !Get(id, email: "ab", token: "c"),
    "credential digest distinguishes email and token boundaries");
Put(id, [7], token: "\ud800");
Check(Get(id, token: "\ud800") && !Get(id, token: "\ud801"),
    "credential digest preserves exact ordinal UTF-16 identity");
Put(id, [5], text: "edited", mode: "speechgen", email: "a", token: "new-secret");
var survivor = Guid.NewGuid();
Put(survivor, [6]);
cache.Remove(id);
Check(!Get(id) && !Get(id, email: "a", token: "bc")
    && !Get(id, text: "edited", mode: "speechgen", email: "a", token: "new-secret") && Get(survivor),
    "removing a phrase forgets every variant while preserving others");
cache.Remove(Guid.NewGuid());
Check(Get(survivor), "removing an unknown phrase is safe");

cache.Clear();
var ids = Enumerable.Range(0, 17).Select(_ => Guid.NewGuid()).ToArray();
for (var i = 0; i < 16; i++) Put(ids[i], [(byte)i]);
Check(ids.Take(16).All(x => Get(x)), "sixteen entries fit within the entry bound");
Get(ids[0]);
Put(ids[16], [16]);
Check(Get(ids[0]) && !Get(ids[1]) && Get(ids[16]), "entry-count eviction preserves the most recently used clip");

cache.Clear();
var large = new byte[8 * 1024 * 1024];
large[0] = 17;
for (var i = 0; i < 4; i++) Put(ids[i], large);
Check(ids.Take(4).All(x => Get(x)), "four maximum-size clips fit the 32 MiB bound exactly");
Get(ids[0]);
Put(ids[4], [18]);
Check(Get(ids[0]) && !Get(ids[1]) && Get(ids[2]) && Get(ids[3]) && Get(ids[4]),
    "byte-budget eviction discards the least recently used clip");
Put(ids[0], [19]);
Put(ids[5], large);
Check(Get(ids[0]) && Get(ids[2]) && Get(ids[3]) && Get(ids[4]) && Get(ids[5]),
    "replacing a large clip releases its former byte budget");

Put(survivor, [20]);
Put(id, [21]);
Put(id, new byte[8 * 1024 * 1024 + 1]);
Check(!Get(id) && Get(survivor), "oversized clips are not retained and stale exact-key clips are removed");
Put(id, [22]);
Put(id, []);
Check(!Get(id), "empty clips cannot be retained or revive stale audio");
cache.Clear();
Check(!Get(survivor) && !Get(ids[0]) && !Get(ids[5]), "clear removes all retained audio");

Parallel.For(0, 256, i =>
{
    var key = ids[i % ids.Length];
    Put(key, [(byte)i]);
    _ = Get(key);
    if (i % 7 == 0) cache.Remove(key);
    if (i % 31 == 0) cache.Clear();
});
Put(id, [23]);
Check(Get(id), "concurrent stores, reads, removals, and clears remain usable");
Console.WriteLine($"All {checks} Quick TTS cache checks passed.");
