using System.Text;
using System.Text.Json;
using VRCTTS.Services;

var temporaryBase = Path.GetFullPath(Path.GetTempPath());
var root = Path.Combine(temporaryBase, "VRCTTS-SettingsTests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
var checks = 0;
try
{
    var fresh = Path.Combine(root, "fresh");
    var freshSettings = AppSettings.Load(fresh);
    Check(freshSettings.VoiceMode == "fish", "fresh install defaults to Fish Audio");
    Check(!File.Exists(Path.Combine(fresh, "settings.json")), "isolated fresh load does not read legacy user settings or write defaults");
    const string addedFishVoice = "fish:ee5e93f259c84f77b15a18dccd6b031c";
    foreach (var mode in new[] { "justin", "fish", addedFishVoice, "lazybird:hasan-piker" })
    {
        freshSettings.SelectVoice(mode);
        freshSettings.Save();
        freshSettings = AppSettings.Load(fresh);
        Check(freshSettings.VoiceMode == mode, "saved voice selection survives restart: " + mode);
    }
    freshSettings.SetFishToken("test-fish-token-do-not-store-plain");
    freshSettings.SetToken("test-speechgen-token-do-not-store-plain");
    freshSettings.Save();
    var secretJson = File.ReadAllText(Path.Combine(fresh, "settings.json"));
    var secrets = AppSettings.Load(fresh);
    Check(secrets.ReadFishToken() == "test-fish-token-do-not-store-plain" && secrets.ReadToken() == "test-speechgen-token-do-not-store-plain", "separate Fish/SpeechGen credentials round trip with Windows DPAPI");
    Check(!secretJson.Contains("test-fish-token-do-not-store-plain", StringComparison.Ordinal) && !secretJson.Contains("test-speechgen-token-do-not-store-plain", StringComparison.Ordinal), "saved JSON does not contain plaintext API tokens");
    secrets.SetFishToken("");
    Check(secrets.ReadFishToken() == "" && secrets.ReadToken() == "test-speechgen-token-do-not-store-plain", "clearing Fish token leaves SpeechGen token intact");
    secrets.ProtectedFishToken = "broken-base64";
    secrets.ProtectedToken = "AAAA";
    Check(secrets.ReadFishToken() == "" && secrets.ReadToken() == "", "unreadable credentials fail empty without exposing encrypted data");

    var legacy = Path.Combine(root, "legacy");
    var renamed = Path.Combine(root, "renamed");
    var legacySettings = new AppSettings
    {
        VoiceMode = "streamlabs", HeadphoneId = "headphones-test-id", CableId = "cable-test-id",
        HeadphoneVolume = .23f, MicrophoneVolume = .64f, ChatboxEnabled = false,
        NotificationSound = true, KeepOnTop = true, MicrophoneSetupAttempted = true, MicrophoneSetupRevision = 3,
        Email = "test@example.invalid"
    };
    legacySettings.SetToken("legacy-protected-token");
    legacySettings.Save(legacy);
    var legacyBytes = File.ReadAllBytes(Path.Combine(legacy, "settings.json"));
    var imported = AppSettings.Load(renamed, legacy);
    Check(imported.VoiceMode == "fish", "first renamed launch uses requested Fish default even when legacy selected Justin");
    Check(imported.HeadphoneId == "headphones-test-id" && imported.CableId == "cable-test-id" && imported.HeadphoneVolume == .23f && imported.MicrophoneVolume == .64f, "migration preserves selected audio routes and volumes");
    Check(!imported.ChatboxEnabled && imported.NotificationSound && imported.KeepOnTop && imported.MicrophoneSetupAttempted && imported.MicrophoneSetupRevision == 3, "migration preserves chatbox, chime, pin, and microphone setup preferences");
    Check(imported.Email == "test@example.invalid" && imported.ReadToken() == "legacy-protected-token", "existing protected SpeechGen account survives the rename");
    Check(File.ReadAllBytes(Path.Combine(legacy, "settings.json")).SequenceEqual(legacyBytes), "legacy settings remain byte-for-byte unchanged");
    Check(File.Exists(Path.Combine(renamed, "settings.json")), "first settings migration is saved once to renamed directory");
    imported.SelectVoice("justin");
    imported.Save();
    legacySettings.HeadphoneId = "later-legacy-route";
    legacySettings.Save();
    var reopened = AppSettings.Load(renamed, legacy);
    Check(reopened.VoiceMode == "justin" && reopened.HeadphoneId == "headphones-test-id", "later renamed launch remembers nondefault voice and ignores changed legacy file");
    var olderWindow = AppSettings.Load(renamed);
    var newerWindow = AppSettings.Load(renamed);
    newerWindow.SelectVoice(addedFishVoice);
    newerWindow.Save();
    olderWindow.KeepOnTop = false;
    olderWindow.Save();
    Check(AppSettings.Load(renamed).VoiceMode == addedFishVoice && olderWindow.VoiceMode == "justin", "closing an older window preserves the newer explicit voice choice without changing its own displayed voice");
    olderWindow.SelectVoice("fish");
    olderWindow.Save();
    Check(AppSettings.Load(renamed).VoiceMode == "fish", "a later explicit selection in the older window becomes the remembered voice");
    newerWindow.Save();
    Check(AppSettings.Load(renamed).VoiceMode == "fish", "repeated non-selection saves cannot undo a newer choice");
    Throws<ArgumentException>(() => newerWindow.SelectVoice("unknown"), "explicit voice selection rejects unsupported modes");
    foreach (var legacyMode in new[] { "streamlabs", "speechgen", "offline" })
    {
        var legacyModeDirectory = Path.Combine(root, "legacy-mode-" + legacyMode);
        Directory.CreateDirectory(legacyModeDirectory);
        File.WriteAllText(Path.Combine(legacyModeDirectory, "settings.json"), JsonSerializer.Serialize(new { VoiceMode = legacyMode }));
        var oldSelection = AppSettings.Load(legacyModeDirectory);
        Check(oldSelection.VoiceMode == "justin", "existing legacy voice mode maps to original Justin: " + legacyMode);
        oldSelection.Save();
        Check(AppSettings.Load(legacyModeDirectory).VoiceMode == "justin", "normalized legacy voice remains Justin after restart: " + legacyMode);
        Throws<ArgumentException>(() => oldSelection.SelectVoice(legacyMode), "provider-only legacy mode cannot be selected as a new voice: " + legacyMode);
    }
    var credentialOlderWindow = AppSettings.Load(renamed);
    var credentialNewerWindow = AppSettings.Load(renamed);
    credentialNewerWindow.SetFishToken("newer-fish-token");
    credentialNewerWindow.SetToken("newer-speechgen-token");
    credentialNewerWindow.SetEmail(" newer@example.invalid ");
    credentialNewerWindow.Save();
    credentialOlderWindow.SetFishToken(credentialOlderWindow.ReadFishToken());
    credentialOlderWindow.SetToken(credentialOlderWindow.ReadToken());
    credentialOlderWindow.SetEmail(credentialOlderWindow.Email);
    credentialOlderWindow.Save();
    var afterStaleClose = AppSettings.Load(renamed);
    Check(afterStaleClose.ReadFishToken() == "newer-fish-token" && afterStaleClose.ReadToken() == "newer-speechgen-token" && afterStaleClose.Email == "newer@example.invalid",
        "closing a stale window with unchanged credential fields preserves newer Fish/SpeechGen tokens and email");
    var savedProtectedFish = afterStaleClose.ProtectedFishToken;
    var savedProtectedSpeechGen = afterStaleClose.ProtectedToken;
    afterStaleClose.SetFishToken("newer-fish-token");
    afterStaleClose.SetToken("newer-speechgen-token");
    afterStaleClose.SetEmail("newer@example.invalid");
    afterStaleClose.Save();
    var unchangedCredentials = AppSettings.Load(renamed);
    Check(unchangedCredentials.ProtectedFishToken == savedProtectedFish && unchangedCredentials.ProtectedToken == savedProtectedSpeechGen,
        "unchanged logical tokens do not generate fresh DPAPI blobs or mark credentials dirty");
    unchangedCredentials.SetFishToken("");
    unchangedCredentials.SetToken("");
    unchangedCredentials.SetEmail("");
    unchangedCredentials.Save();
    var clearedCredentials = AppSettings.Load(renamed);
    Check(clearedCredentials.ReadFishToken() == "" && clearedCredentials.ReadToken() == "" && clearedCredentials.Email == "",
        "explicit credential and email clearing persists");
    afterStaleClose.Save();
    var afterRepeatedStaleSave = AppSettings.Load(renamed);
    Check(afterRepeatedStaleSave.ReadFishToken() == "" && afterRepeatedStaleSave.ReadToken() == "" && afterRepeatedStaleSave.Email == "",
        "ordinary saves cannot resurrect credentials cleared by another window");

    var lazybirdDirectory = Path.Combine(root, "lazybird-credentials");
    var lazybirdOlderWindow = AppSettings.Load(lazybirdDirectory);
    var lazybirdNewerWindow = AppSettings.Load(lazybirdDirectory);
    Check(lazybirdNewerWindow.ReadLazybirdToken() == "", "old settings without a Lazybird key load safely");
    lazybirdNewerWindow.SetLazybirdToken("lazybird-dummy-new-key");
    lazybirdNewerWindow.SetFishToken("fish-independent-key");
    lazybirdNewerWindow.SetToken("speechgen-independent-key");
    lazybirdNewerWindow.Save();
    var lazybirdSaved = AppSettings.Load(lazybirdDirectory);
    Check(lazybirdSaved.ReadLazybirdToken() == "lazybird-dummy-new-key", "Lazybird key round trips using Windows DPAPI");
    Check(!File.ReadAllText(Path.Combine(lazybirdDirectory, "settings.json")).Contains("lazybird-dummy-new-key", StringComparison.Ordinal), "Lazybird key is never saved as plaintext");
    lazybirdOlderWindow.SetLazybirdToken("");
    lazybirdOlderWindow.Save();
    Check(AppSettings.Load(lazybirdDirectory).ReadLazybirdToken() == "lazybird-dummy-new-key", "closing a stale blank-key window preserves a newer Lazybird key");
    var protectedLazybird = lazybirdSaved.ProtectedLazybirdToken;
    lazybirdSaved.SetLazybirdToken("lazybird-dummy-new-key");
    lazybirdSaved.Save();
    Check(AppSettings.Load(lazybirdDirectory).ProtectedLazybirdToken == protectedLazybird, "unchanged Lazybird key keeps its DPAPI blob and is not marked dirty");
    var lazybirdOldKeyWindow = AppSettings.Load(lazybirdDirectory);
    lazybirdSaved.SetLazybirdToken("lazybird-dummy-replacement-key");
    lazybirdSaved.Save();
    lazybirdOldKeyWindow.SetLazybirdToken(lazybirdOldKeyWindow.ReadLazybirdToken());
    lazybirdOldKeyWindow.Save();
    Check(AppSettings.Load(lazybirdDirectory).ReadLazybirdToken() == "lazybird-dummy-replacement-key", "closing a stale old-key window preserves a changed Lazybird key");
    var lazybirdClearWindow = AppSettings.Load(lazybirdDirectory);
    lazybirdClearWindow.SetLazybirdToken("");
    lazybirdClearWindow.Save();
    lazybirdSaved.Save();
    var lazybirdCleared = AppSettings.Load(lazybirdDirectory);
    Check(lazybirdCleared.ReadLazybirdToken() == "" && lazybirdCleared.ProtectedLazybirdToken == "", "explicit Lazybird key clearing persists and stale saves cannot resurrect it");
    Check(lazybirdCleared.ReadFishToken() == "fish-independent-key" && lazybirdCleared.ReadToken() == "speechgen-independent-key", "clearing Lazybird credentials preserves the other provider keys");
    lazybirdCleared.ProtectedLazybirdToken = "bad-base64";
    Check(lazybirdCleared.ReadLazybirdToken() == "", "malformed Lazybird encrypted credentials fail empty without exposing data");

    var invalid = Path.Combine(root, "invalid-choice");
    Directory.CreateDirectory(invalid);
    File.WriteAllText(Path.Combine(invalid, "settings.json"), "{\"VoiceMode\":\"unknown\",\"HeadphoneVolume\":-1,\"MicrophoneVolume\":2,\"OscPort\":0,\"Email\":null}");
    var normalized = AppSettings.Load(invalid);
    Check(normalized.VoiceMode == "fish" && normalized.HeadphoneVolume == 0 && normalized.MicrophoneVolume == 1 && normalized.OscPort == 9000 && normalized.Email == "", "invalid settings normalize to safe bounds and Fish default");

    var corrupt = Path.Combine(root, "corrupt");
    Directory.CreateDirectory(corrupt);
    File.WriteAllText(Path.Combine(corrupt, "settings.json"), "{malformed");
    var broken = AppSettings.Load(corrupt, legacy);
    Check(broken.LoadWarning is not null, "malformed renamed settings surface a load warning instead of importing over them");
    Throws<InvalidOperationException>(() => broken.Save(), "malformed renamed preferences cannot be silently overwritten on exit");
    Check(File.ReadAllText(Path.Combine(corrupt, "settings.json")) == "{malformed", "malformed renamed settings remain unchanged");
    var corruptLegacy = Path.Combine(root, "corrupt-legacy");
    Directory.CreateDirectory(corruptLegacy);
    File.WriteAllText(Path.Combine(corruptLegacy, "settings.json"), "null");
    var failedDestination = Path.Combine(root, "failed-import");
    var failedImport = AppSettings.Load(failedDestination, corruptLegacy);
    Check(failedImport.LoadWarning is not null && !File.Exists(Path.Combine(failedDestination, "settings.json")), "malformed legacy settings do not create a replacement preferences file");
    Throws<InvalidOperationException>(() => failedImport.Save(), "failed legacy import cannot be silently replaced by defaults");

    var phraseLegacy = Path.Combine(root, "phrase-legacy");
    var phraseNew = Path.Combine(root, "phrase-new");
    Directory.CreateDirectory(phraseLegacy);
    var phraseJson = JsonSerializer.Serialize(new[] { new QuickPhrase(Guid.NewGuid(), "mamagüevo"), new QuickPhrase(Guid.NewGuid(), "¡Hola!") });
    File.WriteAllText(Path.Combine(phraseLegacy, "quick-phrases.json"), phraseJson, new UTF8Encoding(false));
    QuickPhraseStore.MigrateLegacyPhrases(phraseNew, phraseLegacy);
    Check(new QuickPhraseStore(Path.Combine(phraseNew, "quick-phrases.json")).Load().Select(p => p.Text).SequenceEqual(new[] { "mamagüevo", "¡Hola!" }), "saved accented phrases import into renamed app");
    Check(File.ReadAllText(Path.Combine(phraseLegacy, "quick-phrases.json")) == phraseJson, "original phrase file remains unchanged");
    new QuickPhraseStore(Path.Combine(phraseNew, "quick-phrases.json")).Save([]);
    QuickPhraseStore.MigrateLegacyPhrases(phraseNew, phraseLegacy);
    Check(new QuickPhraseStore(Path.Combine(phraseNew, "quick-phrases.json")).Load().Count == 0, "existing renamed empty list wins over legacy phrases");
    File.WriteAllText(Path.Combine(phraseLegacy, "quick-phrases.json"), "[]");
    var emptyNew = Path.Combine(root, "empty-phrase-new");
    QuickPhraseStore.MigrateLegacyPhrases(emptyNew, phraseLegacy);
    Check(new QuickPhraseStore(Path.Combine(emptyNew, "quick-phrases.json")).Load().Count == 0, "legacy empty phrase list imports without re-seeding mamagüevo");
    File.WriteAllText(Path.Combine(phraseLegacy, "quick-phrases.json"), "{broken-phrases");
    var malformedNew = Path.Combine(root, "malformed-phrase-new");
    QuickPhraseStore.MigrateLegacyPhrases(malformedNew, phraseLegacy);
    Throws<InvalidDataException>(() => new QuickPhraseStore(Path.Combine(malformedNew, "quick-phrases.json")).Load(), "malformed imported phrases fail clearly rather than seeding over user data");
    Check(File.ReadAllText(Path.Combine(malformedNew, "quick-phrases.json")) == "{broken-phrases", "malformed imported phrase bytes stay available for recovery");
    File.WriteAllBytes(Path.Combine(phraseLegacy, "quick-phrases.json"), new byte[2 * 1024 * 1024 + 1]);
    var oversizedNew = Path.Combine(root, "oversized-phrase-new");
    Throws<InvalidDataException>(() => QuickPhraseStore.MigrateLegacyPhrases(oversizedNew, phraseLegacy), "oversized legacy phrase import is bounded");
    Check(!File.Exists(Path.Combine(oversizedNew, "quick-phrases.json")), "failed oversized import does not leave a partial destination");
    Console.WriteLine($"All {checks} isolated settings/migration checks passed.");
    return 0;
}
finally
{
    var resolvedRoot = Path.GetFullPath(root);
    if (!resolvedRoot.StartsWith(temporaryBase, StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(resolvedRoot).StartsWith("VRCTTS-SettingsTests-", StringComparison.Ordinal))
        throw new InvalidOperationException("Refusing cleanup outside task-owned temporary directory.");
    Directory.Delete(resolvedRoot, recursive: true);
}

void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException("FAILED: " + message);
    checks++;
    Console.WriteLine("PASS " + message);
}

void Throws<T>(Action action, string message) where T : Exception
{
    try { action(); }
    catch (T) { Check(true, message); return; }
    throw new InvalidOperationException("FAILED: " + message);
}
