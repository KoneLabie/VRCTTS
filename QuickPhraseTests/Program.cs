using VRCTTS.Services;

var directory = Path.Combine(Path.GetTempPath(), "VRCTTS-QuickPhraseTests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(directory);
var path = Path.Combine(directory, "phrases.json");
var store = new QuickPhraseStore(path);
var checks = 0;

void Check(bool condition, string message)
{
    if (!condition) throw new Exception("FAILED: " + message);
    checks++;
    Console.WriteLine("PASS " + message);
}

void RejectSave(IEnumerable<QuickPhrase> phrases, string message)
{
    var original = File.ReadAllBytes(path);
    try { store.Save(phrases); throw new Exception("FAILED: " + message); }
    catch (ArgumentException) { }
    Check(File.ReadAllBytes(path).SequenceEqual(original), message + " preserves existing file");
}

void RejectLoad(string json, string message)
{
    File.WriteAllText(path, json);
    try { store.Load(); throw new Exception("FAILED: " + message); }
    catch (InvalidDataException) { }
    Check(File.ReadAllText(path) == json, message + " is rejected without replacing file");
}

void RejectConflict(QuickPhraseStore stale, IEnumerable<QuickPhrase> phrases, string racePath, string message)
{
    var original = File.ReadAllBytes(racePath);
    try { stale.Save(phrases); throw new Exception("FAILED: " + message); }
    catch (QuickPhraseConflictException) { }
    Check(File.ReadAllBytes(racePath).SequenceEqual(original), message + " preserves newer changes");
}

try
{
    var initial = store.Load();
    Check(initial.Count == 1 && initial[0].Text == "mamagüevo", "missing file seeds exact accented phrase");
    Check(!File.Exists(path), "loading defaults does not create files");
    var phrase = new QuickPhrase(Guid.NewGuid(), "  uno\r\ndos\rtres  ");
    store.Save([initial[0], phrase]);
    var restored = new QuickPhraseStore(path).Load();
    Check(restored[0] == initial[0], "accents and phrase ID survive restart");
    Check(restored[1].Text == "uno\ndos\ntres", "saved phrases normalize whitespace and newline forms");
    store.Save([restored[0] with { Text = "otra frase" }]);
    Check(store.Load().Single().Text == "otra frase", "editing and deletion persist");
    store.Save([]);
    Check(new QuickPhraseStore(path).Load().Count == 0, "deleted seed stays deleted after restart");
    var valid = new QuickPhrase(Guid.NewGuid(), new string('ü', QuickPhraseStore.MaximumCharacters));
    store.Save([valid]);
    Check(store.Load().Single().Text == valid.Text, "maximum-length Unicode phrase round-trips");
    RejectSave([valid with { Text = new string('a', QuickPhraseStore.MaximumCharacters + 1) }], "overlong phrase");
    RejectSave([valid with { Text = "\0bad" }], "null character");
    RejectSave([valid with { Text = "  \r\n " }], "empty normalized phrase");
    RejectSave([valid with { Text = null! }], "null text");
    RejectSave([null!], "null phrase");
    RejectSave([valid with { Id = Guid.Empty }], "empty ID");
    RejectSave([valid, valid with { Text = "other" }], "duplicate ID");
    RejectSave([new(Guid.NewGuid(), "  mamagüevo "), new(Guid.NewGuid(), "mamagüevo")], "duplicate normalized phrase");
    var fifty = Enumerable.Range(0, QuickPhraseStore.MaximumPhrases).Select(i => new QuickPhrase(Guid.NewGuid(), $"phrase {i}")).ToList();
    store.Save(fifty);
    Check(store.Load().Count == 50, "fifty phrases are supported");
    RejectSave(fifty.Append(new QuickPhrase(Guid.NewGuid(), "extra")), "phrase count limit");
    store.Save([new(Guid.NewGuid(), "Hello"), new(Guid.NewGuid(), "hello")]);
    Check(store.Load().Count == 2, "phrase comparisons preserve case using ordinal equality");
    RejectLoad("not json", "malformed JSON");
    RejectLoad("null", "null list");
    RejectLoad("[null]", "null saved entry");
    RejectLoad("[{\"Id\":\"invalid-guid\",\"Text\":\"hello\"}]", "malformed ID");
    RejectLoad("[{\"Id\":\"" + Guid.NewGuid() + "\",\"Text\":null}]", "null saved text");
    RejectLoad("[" + new string(' ', 2 * 1024 * 1024) + "]", "oversized file");

    var racePath = Path.Combine(directory, "multiple-windows.json");
    var first = new QuickPhraseStore(racePath);
    var second = new QuickPhraseStore(racePath);
    var firstList = first.Load();
    var secondList = second.Load();
    var addedFirst = new QuickPhrase(Guid.NewGuid(), "first window phrase");
    var addedSecond = new QuickPhrase(Guid.NewGuid(), "second window phrase");
    first.Save(firstList.Append(addedFirst));
    RejectConflict(second, secondList.Append(addedSecond), racePath, "stale addition from missing-file baseline");
    second.Save(second.Load().Append(addedSecond));
    Check(second.Load().Count == 3, "reload and retry retains additions from both windows");
    RejectConflict(first, firstList.Select(item => item with { Text = "edited seed" }), racePath, "stale edit");
    first.Save(first.Load().Select(item => item.Id == firstList[0].Id ? item with { Text = "edited seed" } : item));
    RejectConflict(second, secondList.Where(item => item.Id != secondList[0].Id), racePath, "stale deletion");
    second.Save(second.Load().Where(item => item.Id != secondList[0].Id));
    Check(second.Load().Select(item => item.Text).ToHashSet().SetEquals([addedFirst.Text, addedSecond.Text]),
        "reload and retry deletion retains unrelated additions");

    var initializePath = Path.Combine(directory, "initializer.json");
    var initializer = new QuickPhraseStore(initializePath);
    initializer.Save([addedFirst]);
    initializer.Save([addedFirst, addedSecond]);
    Check(initializer.Load().Count == 2, "initial Save without Load and repeated saves work");
    File.WriteAllText(initializePath, "invalid existing data");
    try { new QuickPhraseStore(initializePath).Save([addedFirst]); throw new Exception("FAILED: malformed initialization"); }
    catch (InvalidDataException) { }
    Check(File.ReadAllText(initializePath) == "invalid existing data", "initial Save cannot replace malformed file");
    Check(!Directory.EnumerateFiles(directory, "*.tmp").Any(), "atomic saves leave no temporary files");
    Console.WriteLine($"All {checks} Quick TTS persistence checks passed.");
    return 0;
}
finally
{
    var resolved = Path.GetFullPath(directory);
    var tempRoot = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
    if (!resolved.StartsWith(tempRoot, StringComparison.OrdinalIgnoreCase)
        || !Path.GetFileName(resolved).StartsWith("VRCTTS-QuickPhraseTests-", StringComparison.Ordinal))
        throw new InvalidOperationException("Refusing cleanup outside the test's temporary directory.");
    Directory.Delete(resolved, recursive: true);
}
