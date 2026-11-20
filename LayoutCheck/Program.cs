using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using VRCTTS.Services;

namespace VRCTTS.LayoutCheck;

// An owned, in-process WPF object-tree integration test. No windows are shown,
// external apps are controlled, or speech is requested. Preferences use isolated temp files.
internal static class Program
{
    private static int checks;

    [STAThread]
    public static int Main(string[] args)
    {
        var phraseDirectory = Path.Combine(Path.GetTempPath(), "VRCTTS-Layout-" + Guid.NewGuid().ToString("N"));
        try
        {
            var app = new App();
            app.InitializeComponent();
            app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var phraseStore = new QuickPhraseStore(Path.Combine(phraseDirectory, "phrases.json"));
            var preferenceDirectory = Path.Combine(phraseDirectory, "preferences");
            var legacyDirectory = Path.Combine(phraseDirectory, "legacy");
            var preferences = AppSettings.Load(preferenceDirectory, legacyDirectory);
            var window = new MainWindow(phraseStore, preferences);

            var chatbox = Control<CheckBox>(window, "ChatboxCheck");
            var input = Control<TextBox>(window, "MessageInput");
            var counter = Control<TextBlock>(window, "Counter");
            var provider = Control<ComboBox>(window, "VoiceMode");
            input.Clear();
            chatbox.IsChecked = true;

            var output = GetOutput(args);
            Render(window, output, 504, 315);
            Console.WriteLine("Rendered " + output);
            RenderPanel(window, "AudioPopup", Path.Combine(Path.GetDirectoryName(output)!, "layout-audio.png"), 444, 650);
            RenderPanel(window, "SettingsPopup", Path.Combine(Path.GetDirectoryName(output)!, "layout-settings.png"), 444, 650);
            RenderPanel(window, "QuickTtsPopup", Path.Combine(Path.GetDirectoryName(output)!, "layout-quick-tts.png"), 370, 540);
            if (args.Contains("--render-only", StringComparer.Ordinal)) return 0;

            Check(!Control<Popup>(window, "AudioPopup").IsOpen, "audio options start hidden in the toolbar");
            Check(!Control<Popup>(window, "SettingsPopup").IsOpen, "settings start hidden in the toolbar");
            Check(!Control<Popup>(window, "HelpPopup").IsOpen, "help starts hidden in the toolbar");
            Check(!Control<Popup>(window, "QuickTtsPopup").IsOpen, "Quick TTS starts hidden next to Audio");
            Check(Control<ComboBox>(window, "MicDevice").Visibility == Visibility.Collapsed, "automatic microphone route selector is hidden");
            Check((string?)((TtsVoiceOption)provider.SelectedItem).Id == "fish", "the linked Fish Audio voice is selected by default");
            Check(window.Title == "VRCTTS — Hatsune Miku", "window and selected voice show the new app name");
            Check(Control<StackPanel>(window, "FishPanel").Visibility == Visibility.Visible, "Fish Audio key setup is available directly in Settings");
            Check(provider.Items.Count == 19 && provider.Items.Cast<TtsVoiceOption>().Count(voice => voice.Id == "justin") == 1,
                "voice list includes all additions and keeps one Justin entry");
            var requestedFishIds = new[]
            {
                "04827d1eecc4460aa0ca7f73aedbd511", "df3cfdc9b9dd42e9a0f589569f598263", "caec241606a642b78cd9f7b321bb14b3",
                "652d734759a14fbab52177325e835be4", "e33adaeadc644b1d9de55bbc84a2341c", "a9261e0625ce4253914f5349fa0d7852",
                "98655a12fa944e26b274c535e5e03842", "a2346273eb314b6cb82ba83dbc9d9fee", "a3b3f0a9c49340bd8fa722d83c81cb08",
                "c85fb11f91f84312a4bd16756f298ae2", "265ee53e0b6d4a2ab028ca1ff1dd94d0", "d34183dcf4a041f5a82aa3dd19644329",
                "03397b4c4be74759b72533b663fbd001"
            };
            Check(requestedFishIds.All(id => provider.Items.Cast<TtsVoiceOption>().Any(voice => voice.Id == "fish:" + id && voice.FishReferenceId == id)),
                "every requested Fish model is available in the selector with its exact reference ID");
            Check(VisualText(provider).Contains("Hatsune Miku"), "the selected voice displays its name without internal model details");
            var toolbar = (StackPanel)Control<ToggleButton>(window, "AudioToggle").Parent;
            Check(toolbar.Children.IndexOf(Control<ToggleButton>(window, "QuickTtsToggle")) == toolbar.Children.IndexOf(Control<ToggleButton>(window, "AudioToggle")) - 1,
                "Quick TTS is immediately to the left of Audio");
            SelectMode(provider, "justin");
            Check(AppSettings.Load(preferenceDirectory, legacyDirectory).VoiceMode == "justin", "choosing a nondefault voice saves the choice immediately");
            var reopened = new MainWindow(new QuickPhraseStore(Path.Combine(phraseDirectory, "phrases.json")), AppSettings.Load(preferenceDirectory, legacyDirectory));
            Check((string?)((TtsVoiceOption)Control<ComboBox>(reopened, "VoiceMode").SelectedItem).Id == "justin", "reopening restores the previous voice rather than resetting to Fish Audio");
            reopened.Close();

            // Exercise the real editor with an isolated phrase file. This window is
            // never shown, so Loaded cannot create or repair microphone drivers.
            var phrases = Control<ItemsControl>(window, "QuickPhraseList");
            var phraseInput = Control<TextBox>(window, "QuickPhraseInput");
            Check(phrases.Items.Cast<QuickPhrase>().Single().Text == "mamagüevo", "Quick TTS includes the accented starter phrase");
            input.Text = "keep this unfinished draft";
            Click(window, "AddQuickPhraseButton");
            phraseInput.Text = "¡Hola, amigo!";
            RenderPanel(window, "QuickTtsPopup", Path.Combine(Path.GetDirectoryName(output)!, "layout-quick-tts-editor.png"), 370, 540);
            Click(window, "SaveQuickPhraseButton");
            Check(phraseStore.Load().Count == 2 && phraseStore.Load()[1].Text == "¡Hola, amigo!", "adding a phrase saves it immediately with accents");
            Check(input.Text == "keep this unfinished draft", "adding a phrase preserves the main draft");
            var saved = phrases.Items.Cast<QuickPhrase>().Last();
            InvokePhraseHandler(window, "OnEditQuickPhrase", saved);
            phraseInput.Text = "¡Hasta luego!";
            Click(window, "SaveQuickPhraseButton");
            Check(phraseStore.Load().Single(p => p.Id == saved.Id).Text == "¡Hasta luego!", "editing keeps phrase identity and persists the new text");
            var otherWindowStore = new QuickPhraseStore(Path.Combine(phraseDirectory, "phrases.json"));
            var otherWindowPhrases = otherWindowStore.Load().ToList();
            otherWindowPhrases.Add(new QuickPhrase(Guid.NewGuid(), "Buenos días"));
            otherWindowStore.Save(otherWindowPhrases);
            InvokePhraseHandler(window, "OnEditQuickPhrase", phrases.Items.Cast<QuickPhrase>().Single(p => p.Id == saved.Id));
            phraseInput.Text = "¿Qué tal?";
            Click(window, "SaveQuickPhraseButton");
            Check(phrases.Items.Count == 3 && phraseInput.Text == "¿Qué tal?" && Control<StackPanel>(window, "QuickPhraseEditor").Visibility == Visibility.Visible,
                "another window's saved change refreshes the list and preserves the pending edit");
            Check(otherWindowStore.Load().Single(p => p.Id == saved.Id).Text == "¡Hasta luego!", "conflicting edit never silently overwrites the phrase file");
            Click(window, "SaveQuickPhraseButton");
            Check(phraseStore.Load().Count == 3 && phraseStore.Load().Single(p => p.Id == saved.Id).Text == "¿Qué tal?", "reviewed retry preserves the other window's new phrase");
            Click(window, "AddQuickPhraseButton");
            phraseInput.Text = "mamagüevo";
            Click(window, "SaveQuickPhraseButton");
            Check(phraseStore.Load().Count == 3 && Control<StackPanel>(window, "QuickPhraseEditor").Visibility == Visibility.Visible, "duplicate save keeps the editor open and does not change saved data");
            Click(window, "CancelQuickPhraseButton");
            Check(Control<StackPanel>(window, "QuickPhraseEditor").Visibility == Visibility.Collapsed, "cancel closes the phrase editor");
            Control<CheckBox>(window, "HeadphonesCheck").IsChecked = false;
            Control<CheckBox>(window, "MicCheck").IsChecked = false;
            InvokePhraseHandler(window, "OnPlayQuickPhrase", phrases.Items.Cast<QuickPhrase>().First());
            Check(input.Text == "keep this unfinished draft", "playing a quick phrase never replaces the draft");
            Check(Control<TextBlock>(window, "Status").Text.Contains("Enable headphones", StringComparison.Ordinal), "quick phrase uses the existing route validation without requesting speech");
            Check(Control<Button>(window, "SpeakButton").IsEnabled, "failed quick playback restores ready state");
            InvokePhraseHandler(window, "OnEditQuickPhrase", phrases.Items.Cast<QuickPhrase>().Last());
            Click(window, "DeleteQuickPhraseButton");
            Check(phraseStore.Load().Count == 2, "delete saves the changed list immediately");
            while (phrases.Items.Count != 0)
            {
                InvokePhraseHandler(window, "OnEditQuickPhrase", phrases.Items.Cast<QuickPhrase>().First());
                Click(window, "DeleteQuickPhraseButton");
            }
            Check(phraseStore.Load().Count == 0 && Control<TextBlock>(window, "QuickPhraseEmpty").Visibility == Visibility.Visible, "deleting all phrases persists an empty list and shows the empty state");

            var minimumLayout = Path.Combine(Path.GetDirectoryName(output)!, "layout-minimum.png");
            Render(window, minimumLayout, 414, 275);
            var brand = Control<TextBlock>(window, "BrandTitle");
            var audioToggle = Control<ToggleButton>(window, "AudioToggle");
            var header = (FrameworkElement)brand.Parent;
            Check(brand.TranslatePoint(new Point(brand.DesiredSize.Width, 0), header).X <= audioToggle.TranslatePoint(new Point(), header).X,
                "toolbar fits beside the brand at the minimum window width");

            SelectMode(provider, "justin");
            chatbox.IsChecked = true;
            input.Text = new string('a', 144);
            Check(input.MaxLength == 144, "chatbox on: input caps typing at 144 characters");
            Counter(counter, 144, 144, "chatbox on: ASCII counter");

            chatbox.IsChecked = false;
            Check(input.MaxLength == 550, "chatbox off: Justin keeps a consistent 550 input-character cap");
            input.Text = new string('a', 550);
            Counter(counter, 550, 550, "chatbox off: Justin ASCII byte counter");
            Check(counter.Text.Contains("bytes", StringComparison.OrdinalIgnoreCase), "Justin counter identifies UTF-8 bytes");

            input.Text = string.Concat(Enumerable.Repeat("😀", 138));
            Counter(counter, 552, 550, "chatbox off: multibyte emoji uses UTF-8 byte limit");
            Check(IsOverLimitColor(counter), "over-byte message is marked with the error color");

            chatbox.IsChecked = true;
            Check(input.MaxLength == 144, "turning chatbox back on restores 144-character input cap");
            Counter(counter, 276, 144, "chatbox on: emoji counted as conservative UTF-16 units");
            Check(input.Text == string.Concat(Enumerable.Repeat("😀", 138)), "mode toggle preserves the existing message");
            Check(IsOverLimitColor(counter), "preserved overlong chatbox message is marked with the error color");

            chatbox.IsChecked = false;
            SelectMode(provider, "fish");
            Check(input.MaxLength == 2000, "chatbox off: Fish Audio input cap is 2000 characters");
            input.Text = new string('a', 2000);
            Counter(counter, 2000, 2000, "Fish ASCII character counter");
            input.Text = string.Concat(Enumerable.Repeat("😀", 1000));
            Counter(counter, 2000, 2000, "Fish emoji counted as UTF-16 characters");
            Check(!IsOverLimitColor(counter), "message at the Fish limit is not marked invalid");

            chatbox.IsChecked = true;
            Check(input.MaxLength == 144, "chatbox limit overrides selected Fish voice");
            Check(input.Text.Length == 2000, "turning chatbox on never erases a draft");
            Counter(counter, 2000, 144, "preserved Fish draft shows the smaller chatbox limit");

            chatbox.IsChecked = false;
            SelectMode(provider, "fish");
            Check(input.MaxLength == 2000, "chatbox off: Fish Audio uses the 2000-character app cap");
            input.Text = "mamagüevo 😀";
            Counter(counter, input.Text.Length, 2000, "Fish Audio counter uses characters");
            Check(Control<StackPanel>(window, "FishPanel").Visibility == Visibility.Visible && Control<StackPanel>(window, "JustinPanel").Visibility == Visibility.Collapsed,
                "switching to Fish Audio shows only its own credentials");
            chatbox.IsChecked = true;
            Check(input.MaxLength == 144 && input.Text == "mamagüevo 😀", "Fish Audio honors the chatbox limit and preserves the draft");

            chatbox.IsChecked = false;
            Control<PasswordBox>(window, "FishToken").Password = "isolated-test-fish-key";
            SelectMode(provider, VoiceCatalog.XqcVoiceId);
            Check(window.Title == "VRCTTS — xQc", "the new Fish voice appears with its actual name");
            Check(Control<PasswordBox>(window, "FishToken").Password == "isolated-test-fish-key", "both Fish voices share the same API key");
            Check(AppSettings.Load(preferenceDirectory, legacyDirectory).VoiceMode == VoiceCatalog.XqcVoiceId, "the new Fish voice is remembered on restart");
            Check(input.MaxLength == 2000, "all Fish voices use the same app message limit");
            RenderPanel(window, "SettingsPopup", Path.Combine(Path.GetDirectoryName(output)!, "layout-settings-xqc.png"), 444, 650);
            Check(VisualText(provider).Contains("xQc"), "the new voice displays its short name in the selector");
            SelectMode(provider, VoiceCatalog.HasanVoiceId);
            Check(window.Title == "VRCTTS — Hasan Piker", "Hasan Piker is available as a distinct Lazybird voice");
            Check(Control<StackPanel>(window, "LazybirdPanel").Visibility == Visibility.Visible
                && Control<StackPanel>(window, "FishPanel").Visibility == Visibility.Collapsed
                && Control<StackPanel>(window, "JustinPanel").Visibility == Visibility.Collapsed,
                "Lazybird shows only its own account setup");
            Check(Control<PasswordBox>(window, "LazybirdToken").Password == "", "the Fish key is never reused as a Lazybird key");
            Check(input.MaxLength == 2000 && counter.ToolTip.ToString()!.Contains("Lazybird", StringComparison.Ordinal), "Lazybird has the correct app limit and help text");
            Control<PasswordBox>(window, "LazybirdToken").Password = "isolated-test-lazybird-key";
            SelectMode(provider, "fish");
            SelectMode(provider, VoiceCatalog.HasanVoiceId);
            var savedKeys = AppSettings.Load(preferenceDirectory, legacyDirectory);
            Check(savedKeys.ReadFishToken() == "isolated-test-fish-key" && savedKeys.ReadLazybirdToken() == "isolated-test-lazybird-key",
                "Fish and Lazybird keys remain independent when switching providers");
            Check(savedKeys.VoiceMode == VoiceCatalog.HasanVoiceId, "the Lazybird choice is remembered on restart");
            RenderPanel(window, "SettingsPopup", Path.Combine(Path.GetDirectoryName(output)!, "layout-settings-lazybird.png"), 444, 650);
            input.Text = new string('a', 2000);
            SelectMode(provider, "justin");
            Check(input.MaxLength == 550 && input.Text.Length == 2000, "switching to automatic Justin preserves an overlong Fish draft");
            Counter(counter, 2000, 550, "Justin shows a stable byte limit for online and fallback use");
            Check(Control<StackPanel>(window, "JustinPanel").Visibility == Visibility.Visible, "Justin shows one automatic routing panel");
            Check(Control<StackPanel>(window, "FishPanel").Visibility == Visibility.Collapsed, "Justin hides Fish credentials");
            Check(Control<StackPanel>(window, "LazybirdPanel").Visibility == Visibility.Collapsed, "Justin hides Lazybird credentials");
            Check(!string.IsNullOrWhiteSpace(Control<TextBlock>(window, "OfflineHint").Text), "offline fallback availability is visible without another voice choice");
            RenderPanel(window, "SettingsPopup", Path.Combine(Path.GetDirectoryName(output)!, "layout-settings-justin.png"), 444, 650);
            chatbox.IsChecked = true;
            Check(input.MaxLength == 144 && input.Text.Length == 2000, "chatbox enabled over a Justin draft preserves text and restores 144 cap");
            Check(IsOverLimitColor(counter), "overlong Justin draft requires shortening for VRChat");

            // Keep the initial clean-state rendering. The tests above intentionally
            // create overlong drafts and do not save or close this owned window.
            Console.WriteLine($"All {checks} WPF integration checks passed.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 1;
        }
        finally
        {
            // The absolute path is created above directly under this process's temp
            // directory, with a unique task-owned name; no user settings are touched.
            if (Directory.Exists(phraseDirectory)) Directory.Delete(phraseDirectory, recursive: true);
        }
    }

    private static void Click(Window window, string name) =>
        Control<Button>(window, name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    private static IEnumerable<string> VisualText(DependencyObject element)
    {
        if (element is TextBlock text) yield return text.Text;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(element); i++)
            foreach (var value in VisualText(VisualTreeHelper.GetChild(element, i))) yield return value;
    }

    private static void InvokePhraseHandler(Window window, string name, QuickPhrase phrase) =>
        typeof(MainWindow).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window,
            [new Button { Tag = phrase }, new RoutedEventArgs(Button.ClickEvent)]);

    private static void SelectMode(ComboBox provider, string mode) => provider.SelectedItem =
        provider.Items.Cast<TtsVoiceOption>().Single(item => item.Id == mode);

    private static string GetOutput(string[] args)
    {
        var argument = Array.IndexOf(args, "--output");
        return Path.GetFullPath(argument >= 0 && argument + 1 < args.Length
            ? args[argument + 1]
            : Path.Combine(Environment.CurrentDirectory, "artifacts", "layout", "layout-main.png"));
    }

    private static T Control<T>(Window window, string name) where T : FrameworkElement =>
        window.FindName(name) as T ?? throw new InvalidOperationException($"Missing {typeof(T).Name}: {name}");

    private static void Check(bool condition, string name)
    {
        if (!condition) throw new InvalidOperationException("FAILED: " + name);
        checks++;
        Console.WriteLine("PASS " + name);
    }

    private static void Counter(TextBlock counter, int used, int maximum, string name)
    {
        var match = Regex.Match(counter.Text, @"(?<used>\d+)\s*/\s*(?<maximum>\d+)");
        Check(match.Success && int.Parse(match.Groups["used"].Value) == used
            && int.Parse(match.Groups["maximum"].Value) == maximum,
            name + $" (actual: {counter.Text})");
    }

    private static bool IsOverLimitColor(TextBlock counter) =>
        counter.Foreground is SolidColorBrush brush && brush.Color == Color.FromRgb(255, 178, 157);

    private static void RenderPanel(Window window, string name, string path, int width, int maximumHeight)
    {
        // Render only the popup's owned child tree; never set IsOpen or create a native popup window.
        var popup = Control<Popup>(window, name);
        var content = popup.Child as FrameworkElement
            ?? throw new InvalidOperationException($"The {name} has no renderable child.");
        content.Measure(new Size(width, maximumHeight));
        var height = Math.Max(1, (int)Math.Ceiling(Math.Min(content.DesiredSize.Height, maximumHeight)));
        RenderElement(content, window.Background, path, width, height);
        Console.WriteLine("Rendered " + path);
    }

    private static void Render(Window window, string path, int width, int height)
    {
        var content = window.Content as FrameworkElement
            ?? throw new InvalidOperationException("The window has no renderable content.");
        RenderElement(content, window.Background, path, width, height);
    }

    private static void RenderElement(FrameworkElement content, Brush background, string path, int width, int height)
    {
        content.Measure(new Size(width, height));
        content.Arrange(new Rect(0, 0, width, height));
        content.UpdateLayout();

        var drawing = new DrawingVisual();
        using (var context = drawing.RenderOpen())
        {
            var rectangle = new Rect(0, 0, width, height);
            context.DrawRectangle(background, null, rectangle);
            // Fix the source viewbox to layout coordinates. Auto viewboxes use the
            // descendant ink bounds and would crop the deliberate outer margins.
            context.DrawRectangle(new VisualBrush(content)
            {
                ViewboxUnits = BrushMappingMode.Absolute,
                Viewbox = rectangle,
                Stretch = Stretch.Fill
            }, null, rectangle);
        }
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(drawing);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var output = File.Create(path);
        encoder.Save(output);
    }
}
