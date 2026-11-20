using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using VRCTTS.Services;
using NAudio.CoreAudioApi;

namespace VRCTTS;

public partial class MainWindow : Window
{
    private const int MicrophoneSetupRevision = 4;
    private readonly AppSettings settings;
    private readonly AudioRouter audio = new();
    private readonly TtsService speech = new();
    private readonly QuickPhraseStore quickPhraseStore;
    private readonly QuickPhraseAudioCache quickPhraseAudio = new();
    private List<QuickPhrase> quickPhrases = [];
    private Guid? editingQuickPhrase;
    private bool quickPhrasesAvailable;
    private OscChatbox? osc;
    private int currentPort;
    private CancellationTokenSource? operationCancellation;
    private Task? operation;
    private bool ready, busy, closing, exitReady, driverBusy;
    private List<AudioDevice> devices = [];

    public MainWindow() : this(new QuickPhraseStore(), AppSettings.Load()) { }

    public MainWindow(QuickPhraseStore phraseStore, AppSettings preferences)
    {
        settings = preferences ?? throw new ArgumentNullException(nameof(preferences));
        quickPhraseStore = phraseStore ?? throw new ArgumentNullException(nameof(phraseStore));
        InitializeComponent();
        HeadphonesCheck.IsChecked = settings.HeadphonesEnabled;
        MicCheck.IsChecked = settings.MicrophoneEnabled;
        ChatboxCheck.IsChecked = settings.ChatboxEnabled;
        SoundCheck.IsChecked = settings.NotificationSound;
        PinCheck.IsChecked = settings.KeepOnTop;
        Topmost = settings.KeepOnTop;
        ClearAfterCheck.IsChecked = settings.ClearAfterSpeaking;
        HeadphoneVolume.Value = settings.HeadphoneVolume;
        MicVolume.Value = settings.MicrophoneVolume;
        VoiceMode.ItemsSource = VoiceCatalog.Options;
        VoiceMode.SelectedValue = VoiceCatalog.Normalize(settings.VoiceMode);
        FishToken.Password = settings.ReadFishToken();
        LazybirdToken.Password = settings.ReadLazybirdToken();
        OscPort.Text = settings.OscPort.ToString();
        ready = true;
        UpdateVoicePanel();
        UpdateMessageLimit();
        LoadQuickPhrases();
        RefreshDevices();
        if (settings.LoadWarning is not null) SetStatus(settings.LoadWarning, true);
        Loaded += async (_, _) =>
        {
            MessageInput.Focus();
            if (IsVisible && IsLoaded && !WindowsAudioEndpoints.HasAppMicrophone()
                && (WindowsAudioEndpoints.HasActiveBasicCablePair() || !settings.MicrophoneSetupAttempted || settings.MicrophoneSetupRevision < MicrophoneSetupRevision))
            {
                operation = PrepareMicrophoneAsync();
                await operation;
            }
        };
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape && busy) { operationCancellation?.Cancel(); e.Handled = true; } };
    }

    private void SetStatus(string message, bool error = false)
    {
        Status.Text = message;
        Status.Foreground = new SolidColorBrush(error ? Color.FromRgb(255, 178, 157) : Color.FromRgb(104, 221, 196));
    }

    private void RefreshDevices()
    {
        var headphoneId = (HeadphoneDevice.SelectedItem as AudioDevice)?.Id ?? settings.HeadphoneId;
        var cableId = (MicDevice.SelectedItem as AudioDevice)?.Id ?? settings.CableId;
        try
        {
            devices = AudioRouter.GetOutputDevices();
            HeadphoneDevice.ItemsSource = devices;
            var renderId = WindowsAudioEndpoints.FindBasicCableRenderId();
            var cables = devices.Where(d => d.Id == renderId).ToList();
            MicDevice.ItemsSource = cables;
            MicDevice.IsEnabled = cables.Count != 0;
            HeadphoneDevice.SelectedItem = devices.FirstOrDefault(d => d.Id == headphoneId) ?? PreferredHeadphones();
            MicDevice.SelectedItem = cables.FirstOrDefault(d => d.Id == cableId) ?? cables.FirstOrDefault(d => d.Name.StartsWith("CABLE Input", StringComparison.OrdinalIgnoreCase)) ?? cables.FirstOrDefault();
            MicHint.Text = cables.Count == 0
                ? "The app’s microphone is not ready. Use Settings → Repair microphone."
                : "In VRChat, select VRCTTS as your microphone.";
            SetStatus(cables.Count == 0
                ? "Microphone not ready · Settings → Repair microphone."
                : VoiceReadyStatus());
        }
        catch (Exception)
        {
            SetStatus("Could not read Windows audio devices. Connect your headphones and try Refresh.", true);
        }
    }

    private AudioDevice? PreferredHeadphones()
    {
        bool Physical(AudioDevice d) => !AudioRouter.IsVirtualCable(d) && !new[] { "Voicemeeter", "Steam Streaming", "Virtual Desktop", "Sonar", "Virtual Audio" }.Any(name => d.Name.Contains(name, StringComparison.OrdinalIgnoreCase));
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            using var endpoint = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            var preferred = devices.FirstOrDefault(d => d.Id == endpoint.ID);
            if (preferred is not null && Physical(preferred)) return preferred;
        }
        catch { /* No default endpoint is valid when all audio devices are disconnected. */ }
        return devices.FirstOrDefault(Physical) ?? devices.FirstOrDefault(d => !AudioRouter.IsVirtualCable(d));
    }

    private string ReadMessage()
    {
        return NormalizeMessage(MessageInput.Text);
    }

    private static string NormalizeMessage(string value)
    {
        var text = value.Replace("\r\n", "\n").Replace('\r', '\n').Trim();
        if (string.IsNullOrWhiteSpace(text)) throw new InvalidOperationException("Type a message first.");
        if (text.Contains('\0')) throw new InvalidOperationException("Remove null characters from the message.");
        return text;
    }

    private OscChatbox GetOsc()
    {
        if (!int.TryParse(OscPort.Text, out var port) || port is < 1 or > 65535)
        {
            SettingsPopup.IsOpen = true;
            throw new InvalidOperationException("Use a valid OSC port from 1 to 65535. VRChat normally uses 9000.");
        }
        if (osc is null || currentPort != port)
        {
            osc?.Dispose();
            osc = new OscChatbox(port: port);
            currentPort = port;
        }
        return osc;
    }

    private void SetBusy(bool value)
    {
        busy = value;
        SpeakButton.IsEnabled = !value;
        StopButton.IsEnabled = value;
        HeadphoneDevice.IsEnabled = !value;
        MicDevice.IsEnabled = !value && MicDevice.Items.Count != 0;
        DriverButton.IsEnabled = !value && !driverBusy;
        VoiceMode.IsEnabled = !value;
        HeadphoneVolume.IsEnabled = !value;
        MicVolume.IsEnabled = !value;
        QuickPhraseList.IsEnabled = !value;
        AddQuickPhraseButton.IsEnabled = !value && quickPhrasesAvailable && quickPhrases.Count < QuickPhraseStore.MaximumPhrases;
        QuickPhraseEditor.IsEnabled = !value;
    }

    private void OnSpeak(object sender, RoutedEventArgs e)
    {
        if (!busy && !closing) operation = SpeakAsync();
    }

    private async Task SpeakAsync(QuickPhrase? quickPhrase = null)
    {
        CancellationTokenSource? cancellation = null;
        try
        {
            var text = quickPhrase is null ? ReadMessage() : NormalizeMessage(quickPhrase.Text);
            var original = MessageInput.Text;
            var sendChat = ChatboxCheck.IsChecked == true;
            if (sendChat) OscChatbox.ValidateText(text);
            var targetOsc = sendChat ? GetOsc() : null;
            var notify = SoundCheck.IsChecked == true;
            var headphone = HeadphonesCheck.IsChecked == true ? HeadphoneDevice.SelectedItem as AudioDevice : null;
            var microphone = MicCheck.IsChecked == true ? MicDevice.SelectedItem as AudioDevice : null;
            if (HeadphonesCheck.IsChecked == true && headphone is null) throw new InvalidOperationException("Select your headphones in Audio routing.");
            if (MicCheck.IsChecked == true && microphone is null)
            {
                SettingsPopup.IsOpen = true;
                throw new InvalidOperationException("The app’s microphone is not ready. Use Settings → Repair microphone, or restart Windows if setup requested it.");
            }
            if (headphone is null && microphone is null) throw new InvalidOperationException("Enable headphones or the program microphone in Audio.");
            if (headphone?.Id == microphone?.Id && headphone is not null) throw new InvalidOperationException("Select your physical headphones in Audio. The program microphone is routed automatically.");
            var mode = SelectedVoiceMode;
            var limit = MessageLimits.For(sendChat, mode);
            if (limit.IsExceeded(text)) throw new InvalidOperationException(limit.UsesUtf8Bytes
                ? $"Shorten your message to {limit.Maximum} UTF-8 bytes. Emoji and accented letters can use more than one byte."
                : $"Shorten your message to {limit.Maximum} characters.");
            var isFish = VoiceCatalog.Find(mode)?.FishReferenceId is not null;
            var isLazybird = VoiceCatalog.Find(mode)?.LazybirdVoiceId is not null;
            var fishToken = FishToken.Password.Trim();
            var lazybirdToken = LazybirdToken.Password.Trim();
            if (isFish && string.IsNullOrWhiteSpace(fishToken))
            {
                SettingsPopup.IsOpen = true;
                throw new InvalidOperationException($"Add your Fish Audio API key in Settings to use {SelectedVoiceName}.");
            }
            if (isLazybird && string.IsNullOrWhiteSpace(lazybirdToken))
            {
                SettingsPopup.IsOpen = true;
                throw new InvalidOperationException("Add your Lazybird API key in Settings to use Hasan Piker.");
            }
            var cacheToken = isFish ? fishToken : isLazybird ? lazybirdToken : "";
            var headphoneVolume = (float)HeadphoneVolume.Value;
            var microphoneVolume = (float)MicVolume.Value;
            TrySaveSettings();
            cancellation = new CancellationTokenSource();
            operationCancellation = cancellation;
            SetBusy(true);
            byte[] bytes;
            var offlineJustin = false;
            var cacheNewClip = false;
            if (quickPhrase is not null && quickPhraseAudio.TryGet(quickPhrase.Id, text, mode, "", cacheToken, out var cached))
            {
                bytes = cached;
            }
            else
            {
                SetStatus($"Creating {SelectedVoiceName}’s speech…");
                bytes = await speech.SynthesizeAsync(text, mode, null, null, cancellation.Token, fishToken, lazybirdToken);
                cancellation.Token.ThrowIfCancellationRequested();
                offlineJustin = mode == "justin" && speech.LastSynthesisWasOffline;
                // A fallback clip must not prevent retrying the online Justin voice
                // when the connection recovers on the next play.
                cacheNewClip = quickPhrase is not null && !offlineJustin;
            }
            SetStatus("Preparing your audio routes…");
            await audio.PlayAsync(bytes, headphone?.Id, microphone?.Id, headphoneVolume, microphoneVolume, cancellation.Token,
                async ct =>
                {
                    if (targetOsc is not null) await targetOsc.SendAsync(text, notify, ct);
                    await Dispatcher.InvokeAsync(() => SetStatus(offlineJustin
                        ? targetOsc is not null ? "Speaking · Justin offline · chatbox sent locally" : "Speaking · Justin offline…"
                        : targetOsc is not null ? "Speaking · chatbox sent locally" : "Speaking…"));
                });
            // Commit a new clip only after it has decoded and played successfully.
            // A malformed provider response must not make every retry reuse bad audio.
            if (cacheNewClip) quickPhraseAudio.Store(quickPhrase!.Id, text, mode, "", cacheToken, bytes);
            SetStatus(sendChat ? "Finished · chatbox sent via OSC. VRChat must have OSC enabled." : "Finished · ready for your next message.");
            if (offlineJustin) SetStatus(sendChat ? "Finished · Justin offline · chatbox sent locally." : "Finished · Justin offline.");
            if (quickPhrase is null && ClearAfterCheck.IsChecked == true && MessageInput.Text == original) MessageInput.Clear();
        }
        catch (OperationCanceledException) { SetStatus("Stopped."); }
        catch (Exception ex) { SetStatus(ex.Message, true); }
        finally
        {
            operationCancellation = null;
            cancellation?.Dispose();
            SetBusy(false);
            if (!closing) MessageInput.Focus();
        }
    }

    private void LoadQuickPhrases()
    {
        try
        {
            quickPhrases = quickPhraseStore.Load().ToList();
            quickPhrasesAvailable = true;
        }
        catch
        {
            QuickPhraseHint.Text = "Could not load saved phrases. Your phrase file has not been changed.";
            QuickPhraseHint.Foreground = new SolidColorBrush(Color.FromRgb(255, 178, 157));
        }
        RefreshQuickPhrases();
    }

    private void RefreshQuickPhrases()
    {
        QuickPhraseList.ItemsSource = quickPhrases.ToArray();
        QuickPhraseEmpty.Visibility = quickPhrases.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        AddQuickPhraseButton.IsEnabled = quickPhrasesAvailable && !busy && quickPhrases.Count < QuickPhraseStore.MaximumPhrases;
    }

    private void OnPlayQuickPhrase(object sender, RoutedEventArgs e)
    {
        if (busy || closing || (sender as Button)?.Tag is not QuickPhrase phrase) return;
        QuickTtsPopup.IsOpen = false;
        operation = SpeakAsync(phrase);
    }

    private void OnAddQuickPhrase(object sender, RoutedEventArgs e)
    {
        if (!quickPhrasesAvailable || busy || quickPhrases.Count >= QuickPhraseStore.MaximumPhrases) return;
        ShowQuickPhraseEditor(null);
    }

    private void OnEditQuickPhrase(object sender, RoutedEventArgs e)
    {
        if (!busy && (sender as Button)?.Tag is QuickPhrase phrase) ShowQuickPhraseEditor(phrase);
    }

    private void ShowQuickPhraseEditor(QuickPhrase? phrase)
    {
        editingQuickPhrase = phrase?.Id;
        QuickPhraseEditorTitle.Text = phrase is null ? "ADD PHRASE" : "EDIT PHRASE";
        QuickPhraseInput.Text = phrase?.Text ?? "";
        DeleteQuickPhraseButton.Visibility = phrase is null ? Visibility.Collapsed : Visibility.Visible;
        QuickPhraseEditor.Visibility = Visibility.Visible;
        QuickPhraseInput.Focus();
        QuickPhraseInput.CaretIndex = QuickPhraseInput.Text.Length;
    }

    private void OnSaveQuickPhrase(object sender, RoutedEventArgs e)
    {
        if (busy || !quickPhrasesAvailable) return;
        try
        {
            var text = NormalizeMessage(QuickPhraseInput.Text);
            var id = editingQuickPhrase ?? Guid.NewGuid();
            var updated = quickPhrases.ToList();
            if (updated.Any(p => p.Id != id && p.Text == text))
                throw new InvalidOperationException("That phrase is already saved in Quick TTS.");
            var index = updated.FindIndex(p => p.Id == id);
            var phrase = new QuickPhrase(id, text);
            if (index >= 0) updated[index] = phrase;
            else updated.Add(phrase);
            quickPhraseStore.Save(updated);
            quickPhrases = updated;
            quickPhraseAudio.Remove(id);
            CloseQuickPhraseEditor();
            RefreshQuickPhrases();
            QuickPhraseHint.Text = "Phrase saved. Click it to speak; your draft stays untouched.";
            QuickPhraseHint.Foreground = (Brush)FindResource("Muted");
        }
        catch (QuickPhraseConflictException) { RefreshConflictingQuickPhrases(); }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            ShowQuickPhraseError(ex.Message);
        }
        catch { ShowQuickPhraseError("Could not save the phrase. Your saved list has not changed."); }
    }

    private void OnDeleteQuickPhrase(object sender, RoutedEventArgs e)
    {
        if (busy || !quickPhrasesAvailable || editingQuickPhrase is not Guid id) return;
        try
        {
            var updated = quickPhrases.Where(p => p.Id != id).ToList();
            quickPhraseStore.Save(updated);
            quickPhrases = updated;
            quickPhraseAudio.Remove(id);
            CloseQuickPhraseEditor();
            RefreshQuickPhrases();
            QuickPhraseHint.Text = "Phrase deleted.";
            QuickPhraseHint.Foreground = (Brush)FindResource("Muted");
        }
        catch (QuickPhraseConflictException) { RefreshConflictingQuickPhrases(); }
        catch { ShowQuickPhraseError("Could not delete the phrase. Your saved list has not changed."); }
    }

    private void RefreshConflictingQuickPhrases()
    {
        try
        {
            quickPhrases = quickPhraseStore.Load().ToList();
            RefreshQuickPhrases();
            ShowQuickPhraseError("Phrases changed in another window. List refreshed; review your edit, then try again.");
        }
        catch { ShowQuickPhraseError("Phrases changed in another window and could not be refreshed. Reopen VRCTTS before editing."); }
    }

    private void ShowQuickPhraseError(string message)
    {
        QuickPhraseHint.Text = message;
        QuickPhraseHint.Foreground = new SolidColorBrush(Color.FromRgb(255, 178, 157));
    }

    private void OnCancelQuickPhrase(object sender, RoutedEventArgs e) => CloseQuickPhraseEditor();

    private void CloseQuickPhraseEditor()
    {
        editingQuickPhrase = null;
        QuickPhraseInput.Clear();
        QuickPhraseEditor.Visibility = Visibility.Collapsed;
    }

    private void OnQuickPhraseKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Shift) == 0 && !e.IsRepeat)
        {
            e.Handled = true;
            OnSaveQuickPhrase(sender, new RoutedEventArgs());
        }
        else if (e.Key == Key.Escape && !busy)
        {
            e.Handled = true;
            CloseQuickPhraseEditor();
        }
    }

    private async void OnSendTextOnly(object sender, RoutedEventArgs e)
    {
        if (busy || closing) return;
        operation = SendTextOnlyAsync();
        await operation;
    }

    private async Task SendTextOnlyAsync()
    {
        using var cancellation = new CancellationTokenSource();
        try
        {
            var text = ReadMessage();
            OscChatbox.ValidateText(text);
            var target = GetOsc();
            var notify = SoundCheck.IsChecked == true;
            operationCancellation = cancellation;
            SetBusy(true);
            SetStatus("Sending chatbox text…");
            await target.SendAsync(text, notify, cancellation.Token);
            SetStatus("Chatbox sent locally. Enable OSC in VRChat to display it.");
            TrySaveSettings();
        }
        catch (OperationCanceledException) { SetStatus("Stopped."); }
        catch (Exception ex) { SetStatus(ex.Message, true); }
        finally { operationCancellation = null; SetBusy(false); }
    }

    private void OnStop(object sender, RoutedEventArgs e) => operationCancellation?.Cancel();
    private void OnRefresh(object sender, RoutedEventArgs e) { if (!busy) RefreshDevices(); }
    private void OnPinChanged(object sender, RoutedEventArgs e) { if (ready) Topmost = PinCheck.IsChecked == true; }
    private void OnMessageChanged(object sender, TextChangedEventArgs e)
    {
        UpdateMessageLimit();
    }
    private void OnChatboxChanged(object sender, RoutedEventArgs e) { if (ready) UpdateMessageLimit(); }
    private void UpdateMessageLimit()
    {
        if (Counter is null || MessageInput is null || ChatboxCheck is null || VoiceMode is null) return;
        var chatbox = ChatboxCheck.IsChecked == true;
        var limit = MessageLimits.For(chatbox, SelectedVoiceMode);
        // MaxLength affects future typing/pastes. Preserve existing text when switching modes.
        MessageInput.MaxLength = limit.Maximum;
        Counter.Text = limit.Counter(MessageInput.Text);
        Counter.Foreground = new SolidColorBrush(limit.IsExceeded(MessageInput.Text)
            ? Color.FromRgb(255, 178, 157) : Color.FromRgb(161, 175, 190));
        Counter.ToolTip = limit.UsesUtf8Bytes ? "550 UTF-8 bytes. Emoji and accented letters use more than one byte."
            : chatbox ? "VRChat allows up to 144 characters and 9 lines."
            : VoiceCatalog.Find(SelectedVoiceMode)?.LazybirdVoiceId is not null ? "VRCTTS allows up to 2,000 characters per Lazybird message."
            : "VRCTTS allows up to 2,000 characters per Fish Audio message.";
        if (SpeakButton is not null) SpeakButton.Content = chatbox ? "Speak + send" : "Speak";
        if (SoundCheck is not null) SoundCheck.IsEnabled = chatbox;
    }
    private void OnMessageKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Shift) == 0 && !e.IsRepeat)
        {
            e.Handled = true;
            if (!busy && !closing) operation = SpeakAsync();
        }
    }
    private void OnVoiceModeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ready) return;
        UpdateVoicePanel();
        UpdateMessageLimit();
        if (!busy) SetStatus(VoiceReadyStatus());
        settings.SelectVoice(SelectedVoiceMode);
        TrySaveSettings();
    }
    private void UpdateVoicePanel()
    {
        var mode = SelectedVoiceMode;
        var voice = VoiceCatalog.Find(mode);
        Title = $"VRCTTS — {SelectedVoiceName}";
        FishPanel.Visibility = voice?.FishReferenceId is not null ? Visibility.Visible : Visibility.Collapsed;
        LazybirdPanel.Visibility = voice?.LazybirdVoiceId is not null ? Visibility.Visible : Visibility.Collapsed;
        JustinPanel.Visibility = mode == "justin" ? Visibility.Visible : Visibility.Collapsed;
        if (mode == "justin") OfflineHint.Text = OfflineReadyStatus();
    }
    private void OnRefreshLocalVoice(object sender, RoutedEventArgs e)
    {
        if (busy) return;
        OfflineHint.Text = OfflineReadyStatus();
        if (SelectedVoiceMode == "justin") SetStatus(VoiceReadyStatus());
    }
    private string SelectedVoiceMode => (VoiceMode.SelectedItem as TtsVoiceOption)?.Id ?? VoiceCatalog.DefaultVoiceId;
    private string SelectedVoiceName => VoiceCatalog.Find(SelectedVoiceMode)?.Name ?? "Hatsune Miku";
    private string VoiceReadyStatus() => SelectedVoiceMode switch
    {
        "justin" => "Justin · automatic online / offline · Enter to speak.",
        _ when VoiceCatalog.Find(SelectedVoiceMode)?.LazybirdVoiceId is not null && string.IsNullOrWhiteSpace(LazybirdToken.Password) => $"{SelectedVoiceName} · add your Lazybird API key in Settings.",
        _ when VoiceCatalog.Find(SelectedVoiceMode)?.LazybirdVoiceId is not null => $"{SelectedVoiceName} is ready · Enter to speak.",
        _ when string.IsNullOrWhiteSpace(FishToken.Password) => $"{SelectedVoiceName} · add your Fish Audio API key in Settings.",
        _ => $"{SelectedVoiceName} is ready · Enter to speak."
    };

    private void OnFishTokenChanged(object sender, RoutedEventArgs e)
    {
        if (!ready) return;
        TrySaveSettings();
        if (!busy && VoiceCatalog.Find(SelectedVoiceMode)?.FishReferenceId is not null) SetStatus(VoiceReadyStatus());
    }

    private void OnLazybirdTokenChanged(object sender, RoutedEventArgs e)
    {
        if (!ready) return;
        TrySaveSettings();
        if (!busy && VoiceCatalog.Find(SelectedVoiceMode)?.LazybirdVoiceId is not null) SetStatus(VoiceReadyStatus());
    }
    private string OfflineReadyStatus()
    {
        try
        {
            var installed = speech.GetOfflineVoices().FirstOrDefault(voice => voice.Enabled);
            return installed is null ? "Offline fallback is not installed. It needs an activated original IVONA Justin voice."
                : "Original offline Justin is detected. It will be tried if online speech is unavailable.";
        }
        catch { return "Windows could not load offline Justin. Check its installation and voice activation."; }
    }
    private void OnSetupExpanded(object sender, RoutedEventArgs e) { }
    private void OnSetupCollapsed(object sender, RoutedEventArgs e) { }
    private void OnOpenFishKeys(object sender, RoutedEventArgs e) => OpenLink("https://fish.audio/app/api-keys");
    private void OnOpenLazybirdKeys(object sender, RoutedEventArgs e) => OpenLink("https://www.lazybird.app/docs/api-reference");
    private void OnOpenLazybirdVoice(object sender, RoutedEventArgs e) => OpenLink("https://www.lazybird.app/hasan-piker-ai-voice");
    private void OnOpenFishVoice(object sender, RoutedEventArgs e)
    {
        if (VoiceCatalog.Find(SelectedVoiceMode)?.FishReferenceId is string referenceId)
            OpenLink($"https://fish.audio/m/{referenceId}/");
    }
    private void OnOpenVbAudio(object sender, RoutedEventArgs e) => OpenLink("https://vb-audio.com/Cable/");
    private void OpenLink(string url)
    {
        try { DriverSetup.OpenUrl(url); }
        catch { SetStatus("Windows could not open your browser. The links are also in the included guide.", true); }
    }

    private async void OnSetupDriver(object sender, RoutedEventArgs e)
    {
        if (driverBusy || busy || closing) return;
        SettingsPopup.IsOpen = false;
        operation = PrepareMicrophoneAsync();
        await operation;
    }

    private async Task PrepareMicrophoneAsync()
    {
        if (driverBusy || closing) return;
        driverBusy = true;
        settings.MicrophoneSetupAttempted = true;
        settings.MicrophoneSetupRevision = MicrophoneSetupRevision;
        TrySaveSettings();
        SetBusy(true);
        StopButton.IsEnabled = false;
        try
        {
            SetStatus("Finishing the VRCTTS microphone. Windows may request administrator approval.");
            var result = await DriverSetup.LaunchAsync();
            if (result.NeedsRestart)
            {
                // A fresh launch after restart completes endpoint naming automatically.
                settings.MicrophoneSetupAttempted = false;
                TrySaveSettings();
            }
            if (!closing)
            {
                RefreshDevices();
                SetStatus(result.Success && !result.NeedsRestart ? VoiceReadyStatus() : result.Message, !result.Success);
            }
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223) { SetStatus("Microphone creation canceled. Use Settings → Repair microphone when ready."); }
        catch (Exception ex) { SetStatus(ex.Message, true); }
        finally { driverBusy = false; SetBusy(false); }
    }

    private void TrySaveSettings()
    {
        try
        {
            settings.HeadphoneId = (HeadphoneDevice.SelectedItem as AudioDevice)?.Id;
            settings.CableId = (MicDevice.SelectedItem as AudioDevice)?.Id;
            settings.HeadphonesEnabled = HeadphonesCheck.IsChecked == true;
            settings.MicrophoneEnabled = MicCheck.IsChecked == true;
            settings.HeadphoneVolume = (float)HeadphoneVolume.Value;
            settings.MicrophoneVolume = (float)MicVolume.Value;
            settings.ChatboxEnabled = ChatboxCheck.IsChecked == true;
            settings.NotificationSound = SoundCheck.IsChecked == true;
            settings.KeepOnTop = PinCheck.IsChecked == true;
            settings.ClearAfterSpeaking = ClearAfterCheck.IsChecked == true;
            if (int.TryParse(OscPort.Text, out var port) && port is > 0 and <= 65535) settings.OscPort = port;
            settings.SetFishToken(FishToken.Password.Trim());
            settings.SetLazybirdToken(LazybirdToken.Password.Trim());
            settings.Save();
        }
        catch { SetStatus("Could not save preferences. You can still use the app this session.", true); }
    }

    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (exitReady) return;
        if (closing) { e.Cancel = true; return; }
        closing = true;
        operationCancellation?.Cancel();
        if (operation is { IsCompleted: false })
        {
            e.Cancel = true;
            try { await operation; } catch { /* The operation already displays its own error. */ }
            TrySaveSettings();
            osc?.Dispose();
            speech.Dispose();
            exitReady = true;
            Close();
        }
        else
        {
            TrySaveSettings();
            osc?.Dispose();
            speech.Dispose();
            exitReady = true;
        }
    }
}
