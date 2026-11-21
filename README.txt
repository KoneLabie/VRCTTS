VRCTTS 2.4.0 — WINDOWS x64

Type a message, hear your selected voice in your headphones, and send the same speech to
VRChat through a virtual microphone. Optional OSC displays the text in the
VRChat chatbox. Voicemeeter is not required.

SETUP — SIX STEPS
1. Run VRCTTS-2.4.0-Setup.exe, then open VRCTTS from the Start menu.
   The per-user installer includes the .NET 10 runtime and offers an optional
   desktop shortcut. For portable use, extract the ZIP and run VRCTTS.exe.
   Only the compact ZIP needs a separate .NET 10 Desktop Runtime installation.
2. An existing healthy app microphone is renamed to VRCTTS automatically.
   For a new installation, approve the Windows administrator prompt to create the
   microphone. The app uses its own elevated helper and Windows SetupAPI
   to install the included signed basic VB-CABLE driver and name the input
   "VRCTTS". Windows may append the driver name in device lists. Setup may require
   a Windows restart. An existing basic VB-CABLE installation is updated
   in place; setup does not uninstall it. No separate installer UI is needed.
3. Open Audio in the toolbar and choose your headphones. The app selects
   the virtual microphone route automatically. Adjust headphone and mic
   volumes here; leave your Windows playback device on your headphones.
4. In VRChat's audio settings, select
   "VRCTTS" as the microphone
   and unmute in VRChat.
5. For chatbox text, enable VRChat's OSC:
   Action Menu > Options > OSC > Enabled.
   The default local input port is 9000. Change it in the app's Settings
   only if you changed VRChat's input port.
6. Type and press Enter or click Speak. Shift+Enter adds a line; Escape
   or Stop cancels speech. The VRChat chatbox and Chime switches stay on
   the main window. Audio, Quick TTS, Settings, and ? contain the other controls.
   You can type the next message while speech plays. Text clears after
   successful speech only when the message has not changed.

INSTALLATION AND UNINSTALL
The installer installs the app in %LOCALAPPDATA%\Programs\VRCTTS for the
current Windows user. Installing the app does not need administrator
approval; its first microphone setup may request administrator approval.
The installer and app are unsigned; the included vendor driver is signed.
Uninstall VRCTTS through Windows Installed apps. This removes the installed
program and shortcuts while retaining %LOCALAPPDATA%\VRCTTS preferences,
saved phrases, and API keys. The shared VB-CABLE driver remains installed.
SHA256SUMS.txt contains checksums for the installer and all three ZIPs.

VOICE SELECTION
Settings > TTS voice selects the voice. Hatsune Miku from the linked Fish
Audio page is the first-launch default. Create a Fish Audio account/API key,
then paste the key into Settings > Fish Audio API key. Use Get API key to
open Fish Audio's setup page. VRCTTS requests the free s2.1-pro-free model
explicitly; it does not retry with a paid model. Internet is required.
https://fish.audio/m/acc8237220d8470985ec9be6c4c480a9/
All Fish voices use this same API key. The list now includes Hatsune Miku,
xQc, Alex Jones, Nick Fuentes, clav, Yameii Online, Kai Cenat, mrbeast,
E-girl, Markiplier, teto, GOJO, Subaru, Naruto, Elon Musk (Noise reduction),
Chris, and Casual Young Male. Type a voice name while the selector is focused
to jump through the list. Voice page opens the selected Fish profile.
https://fish.audio/app/api-keys
https://docs.fish.audio/developer-guide/getting-started/quickstart

Hasan Piker uses Lazybird instead of Fish Audio. Select Hasan Piker, then
enter a Lazybird API key in Settings. Lazybird generation consumes account
character credits, including any free API allowance on your account. The
Fish key is never sent to Lazybird, and the Lazybird key is never sent to Fish.
https://www.lazybird.app/hasan-piker-ai-voice
https://www.lazybird.app/docs/api-reference
https://www.lazybird.app/text-to-speech-api

Chris and Casual Young Male were added as smoother options to try. Their
Fish profiles describe young, calm/friendly, clear and smooth voices. This
is a profile-based suggestion, not an audition or an exact Justin match:
https://fish.audio/m/305611ec428640ee824979aea5c7e19c/
https://fish.audio/m/ffeb710f6d8b4bb6a2099428ad51cf95/

Justin appears once in the list. It automatically tries Streamlabs online
without an account. If the online request fails or takes longer than eight
seconds, it tries the installed, activated original IVONA Justin SAPI5 voice.
Stop/cancellation never starts a fallback. If neither source works, the app
shows an error and does not substitute another voice. The offline voice must
already be installed and activated; no Justin voice or license is bundled.
Each explicit voice choice saves immediately
and is restored on the next launch, including when you choose a nondefault.
Closing another window of this build does not undo a newer voice selection.
Close previous app versions before reopening this update and configuring
new voices or keys; previous binaries do not recognize the new settings.
Existing audio preferences and saved phrases are copied from the previous
Justin Bridge app once; those original files are retained unchanged. On this
first upgrade, the default changes to Fish Audio; later launches keep your choice.

QUICK TTS
Open Quick TTS to the left of Audio and click a phrase to speak it immediately.
"mamagüevo" is included as the first phrase. Use + Add phrase to save your
own, or Edit to change/delete one. Enter saves; Shift+Enter adds a line.
Up to 50 phrases can be saved, with up to 5,000 characters each. Playback
still checks the current voice service and VRChat chatbox limits below.
Quick phrases use the current headphones, microphone, chatbox, and chime
settings. They never replace or clear the message you are drafting.
The first play generates the selected voice normally. Recent
clips are then cached in memory (up to 16 clips / 32 MiB), so repeats avoid
another voice-service request. Closing the app clears that audio cache.
Changing the voice or its credentials never reuses another voice's clip.
Offline Justin fallback clips are not cached, so the next play can try the
online voice again after your connection recovers.
Phrase edits and deletions save immediately to:
%LOCALAPPDATA%\VRCTTS\quick-phrases.json

MESSAGE LIMITS
- Chatbox on: 144 UTF-16 code units, up to 9 explicit lines.
- Chatbox off, Justin: 550 UTF-8 bytes for both automatic routes. English letters usually use
  one byte; emoji and accented letters can use more.
- Chatbox off, Fish Audio: 2,000 UTF-16 code units (VRCTTS app limit).
- Chatbox off, Lazybird: 2,000 UTF-16 code units (VRCTTS app limit).
The counter changes with the mode. Switching modes preserves your text;
shorten an over-limit message before sending. VRChat also counts wrapped
lines, so narrow chatbox settings may hide part of a message.

VOICE, INTERNET, AND PRIVACY
The default is the linked Hatsune Miku Fish Audio voice. Fish Audio requires
an account and API key, including for its free developer model. Free service
availability, limits, and latency can change.

Justin through Streamlabs needs no login or API key. Live generation was
tested previously. Its endpoint is
undocumented; availability and rate limits may change. Internet is required.
If Justin's online service fails, VRCTTS automatically uses the installed
offline Justin engine. Fish and Lazybird voices never fall back to Justin.

Justin's automatic offline fallback uses an installed, activated original IVONA Justin
SAPI5 voice. No voice installer, model, trial, clone, or activation key is
included. It refuses to substitute another Windows voice. This update does
not provide a key-free offline Justin voice.

Online generation sends text to the selected voice service; cached repeats
do not. Offline generation stays on this PC. Speech audio stays in memory.
Only phrases you explicitly save in Quick TTS are stored on disk; drafts
and speech history are not saved. Preferences are saved in:
%LOCALAPPDATA%\VRCTTS\settings.json
Fish Audio and Lazybird keys are stored separately and protected for your
Windows user with DPAPI.
Chatbox messages go only to this computer via UDP OSC. OSC is one-way:
"sent" confirms local transmission, not receipt or display by VRChat.

MICROPHONE AND VB-AUDIO
VRCTTS routes speech with WASAPI and configures the named microphone.
The underlying Windows audio device is supplied by genuine VB-CABLE by
VB-Audio, a separate signed donationware driver embedded in the executable.
No mixer app needs to run. The driver remains installed after VRCTTS
closes; speech stops with the app. Settings > Repair microphone repeats
driver setup if needed and may request administrator approval again.
https://vb-audio.com/Cable/
https://www.vb-cable.com/
https://vb-audio.com/Services/licensing.htm
Donation and license support: https://shop.vb-audio.com/

TROUBLESHOOTING
- If setup requests a restart, restart Windows and reopen VRCTTS.
  If the microphone is still missing, use Settings > Repair microphone.
- After a driver restart, confirm Windows playback still uses your headphones.
  Setup restores audio defaults during installation; Windows can choose a
  new default later during reboot, which may need correcting in Sound settings.
- For a disconnected headset, reconnect it and use Audio > Refresh.
- If VRChat hears nothing, check its selected named microphone, unmute,
  and confirm "Send to VRCTTS" is enabled under Audio.
  Check VRChat's microphone sensitivity if speech is cut off.
- To test headphones alone, turn off the microphone switch under Audio.
- For missing text, check OSC and the input port. Other players may need
  to allow OSC chatboxes in their own VRChat settings.
- Wait before retrying a busy or rate-limited voice service.
The application is a local unsigned build; the embedded vendor driver is
signed. No startup task, analytics, or automatic updater is installed.

MICROPHONE SETUP
An installed, healthy cable is reused without reinstalling it. The app
identifies the standard speaker pin by adapter identity and form factor;
it accepts the current driver's "Speakers" name rather than requiring
"CABLE Input". Error 259 is treated as an already-current-driver result
only when a healthy matching driver and active endpoint pair are verified.
The microphone's user-editable label is exactly "VRCTTS". Windows
may display it as "VRCTTS (VB-Audio Virtual Cable)"; older WinMM
lists can truncate the adapter suffix. The app uses the Windows audio
policy service to change the label and verifies it before reporting success.
Audio/driver
setup diagnostics are saved locally without speech text or account tokens.
Latest diagnostic: %LOCALAPPDATA%\VRCTTS\microphone-setup-diagnostics.json

VALIDATION
Automated checks cover OSC packets/local delivery, audio decoding and route
validation, Fish API requests/authentication/free-model selection, provider
errors/bounded downloads/cancellation, Justin online-to-offline fallback and
Stop behavior, all 17 Fish model IDs, Lazybird's Hasan request and key,
dynamic text limits, phrase editing
and cache isolation, legacy data migration, and persisted voice selection.
WPF layout and integration checks use isolated preferences and never speak.
Fish Audio and Lazybird have not been tested with real account keys; their
request paths were verified with controlled HTTP responses. Previous live Streamlabs generation
and dual-output silent playback passed. In-game microphone speech has not
been exercised by this update.

SOURCE / BUILD
Source is in the accompanying source ZIP. Requires .NET 10 SDK on Windows.
Run build.ps1 for a compact build, or build.ps1 -Portable to include the
.NET Desktop runtime. Run scripts\test.ps1 for all eight isolated check
projects, then package.ps1 for the installer, versioned portable/compact/source
ZIPs, and SHA256SUMS.txt. Installer packaging automatically downloads the
official portable Inno Setup 7.1.1 compiler, verifies its pinned SHA-256
checksum and publisher signature, and caches it under artifacts/tools/.
No preinstalled Inno Setup is required; the first packaging run needs internet.
Build and packaging output goes to artifacts/. Scripts accept
-DotnetPath C:\path\to\dotnet.exe for a separately installed SDK.
package.ps1 and build-installer.ps1 also accept
-IsccPath C:\path\to\ISCC.exe to use a compiler you supply.
CI checks isolated installer installation and removal before uploading packages.
Tests:
  dotnet run --project VRCTTS.Tests
  dotnet run --project VoiceTests
  dotnet run --project LayoutCheck
  dotnet run --project QuickPhraseTests
  dotnet run --project QuickPhraseCacheTests
  dotnet run --project AudioFormatTests
  dotnet run --project OfflineVoiceTests
  dotnet run --project SettingsTests
Optional real-device silence checks:
  dotnet run --project VRCTTS.Tests -- --audio-smoke
Optional live generation (generic text sent to Streamlabs):
  dotnet run --project VoiceTests -- --live

See the included third-party notices and runtime license files.
