# VRCTTS

A compact Windows TTS app for VRChat. Type a message, hear it in your headphones, and send the same audio through the **VRCTTS** virtual microphone. Optional OSC sends the text to VRChat's chatbox. Voicemeeter is not required.

## Download and run

Open this repository's **Releases** tab and choose:

| Package | Use |
| --- | --- |
| `VRCTTS-2.4.0-Setup.exe` | Recommended installer. Installs for your Windows user and includes the .NET 10 runtime. |
| `VRCTTS-2.4.0-win-x64.zip` | Portable app with the .NET 10 runtime included. |
| `VRCTTS-2.4.0-compact-win-x64.zip` | Smaller app; requires the **.NET 10 Desktop Runtime, x64**. |
| `VRCTTS-2.4.0-Source.zip` | Curated source, installer/build scripts, tests, and third-party notices. |

Run the installer, then open **VRCTTS** from the Start menu. The installer uses `%LOCALAPPDATA%\Programs\VRCTTS` and offers an optional desktop shortcut. No separate .NET installation is needed. For a portable package, extract the ZIP before running `VRCTTS.exe`. Close earlier VRCTTS versions before configuring new voices or keys. SHA-256 checksums for the installer and ZIPs are provided in `SHA256SUMS.txt`; use `Get-FileHash .\downloaded-file -Algorithm SHA256` to compare a download.

The installer and application EXE are unsigned. The included original VB-CABLE driver is signed by its vendor. Installing the app for your own user does not require administrator approval; creating a new microphone when the app starts does, and may require a restart.

Uninstall **VRCTTS** through Windows **Installed apps**. Uninstalling removes the installed program and its shortcuts, while preserving saved phrases, settings, and API keys under `%LOCALAPPDATA%\VRCTTS`. The shared VB-CABLE microphone driver remains installed.

## First setup

1. Open **VRCTTS** from the Start menu after installation, or run the extracted portable `VRCTTS.exe`. Approve the administrator prompt if the microphone needs installing.
2. Open **Audio** and choose your physical headphones. The microphone route is selected automatically.
3. In VRChat, select **VRCTTS** as your microphone. Windows may display `VRCTTS (VB-Audio Virtual Cable)`.
4. For chatbox text, enable **Action Menu → Options → OSC → Enabled** in VRChat.
5. Open **Settings → TTS voice**, choose a voice, and enter its provider's API key if required.
6. Type and press **Enter**. **Shift+Enter** adds a line; **Escape** or **Stop** cancels speech.

**Quick TTS** is to the left of **Audio**. Add, edit, delete, and play saved phrases there. It includes “mamagüevo.” Recent speech clips stay in memory to make repeats faster, and playing a saved phrase leaves your draft untouched.

## Voices

**Hatsune Miku** is the first-launch default. Your last explicit voice choice is saved and restored.

- **Fish Audio:** 17 voice profiles share one Fish API key. VRCTTS explicitly requests the `s2.1-pro-free` developer model. [Create a Fish API key](https://fish.audio/app/api-keys).
- **Hasan Piker:** uses Lazybird with a separate Lazybird API key and account credits. [Lazybird API setup](https://www.lazybird.app/docs/api-reference).
- **Justin:** one choice automatically tries Streamlabs online first without an account. If the request fails or takes longer than eight seconds, it tries an installed, activated original IVONA Justin SAPI5 voice. No offline Justin model, installer, trial, or activation key is bundled. No other Windows voice is substituted.

The Fish list includes Hatsune Miku, xQc, Alex Jones, Nick Fuentes, clav, Yameii Online, Kai Cenat, mrbeast, E-girl, Markiplier, teto, GOJO, Subaru, Naruto, Elon Musk (Noise reduction), Chris, and Casual Young Male. The selected voice's profile is available through **Voice page** in Settings. Chris and Casual Young Male are profile-based suggestions for a smooth, relaxed delivery; they have not been auditioned as exact Justin matches.

Online services and community voice profiles can change or become unavailable. Streamlabs' endpoint is undocumented. API accounts and keys are supplied by the user; the repository and release packages contain none.

## Text limits and privacy

| Mode | App limit |
| --- | --- |
| VRChat chatbox enabled | 144 UTF-16 code units, up to 9 explicit lines |
| Justin, chatbox disabled | 550 UTF-8 bytes |
| Fish or Lazybird, chatbox disabled | 2,000 UTF-16 code units |

Switching modes preserves your existing text; an over-limit draft needs shortening before sending. The chime switch applies to chatbox messages.

Online speech sends text only to the selected provider. Chatbox messages go to this PC using UDP OSC, normally port 9000. OSC is one-way: “sent” confirms local transmission, not receipt or display by VRChat.

Preferences and explicitly saved Quick TTS phrases are stored under `%LOCALAPPDATA%\VRCTTS`. API keys are stored separately and protected for your Windows user with DPAPI. Drafts and speech history are not saved. Audio remains in memory. There is no analytics, startup task, or automatic updater.

## Microphone driver

The underlying device is the original basic **VB-CABLE by VB-Audio**, a separate donationware driver bundled unchanged. Its donationware terms still apply. The app names the microphone VRCTTS and routes audio; it does not replace the vendor driver or require a mixer app to run.

- [VB-CABLE origin and download](https://www.vb-cable.com/)
- [Product page](https://vb-audio.com/Cable/)
- [Donation and license support](https://shop.vb-audio.com/)
- [Vendor redistribution terms](https://vb-audio.com/Services/licensing.htm)

An existing healthy basic cable is reused. The driver remains installed when the app closes. Use **Settings → Microphone & chatbox settings → Repair microphone** if needed. Check Windows playback still uses your headphones after a requested reboot.

## Build, check, and package

Requires Windows x64, PowerShell, and a supported **.NET 10 SDK** matching `global.json`. Installer packaging automatically downloads the official portable **Inno Setup 7.1.1** compiler, verifies its pinned SHA-256 checksum and publisher signature, and caches it under `artifacts/tools/`. No preinstalled Inno Setup is required. The first packaging run needs internet access.

```powershell
./scripts/test.ps1
./build.ps1                 # compact EXE
./build.ps1 -Portable       # includes the runtime
./package.ps1              # installer, versioned ZIPs, and SHA256SUMS.txt
```

Scripts accept `-DotnetPath C:\path\to\dotnet.exe` when using a separately installed SDK. `package.ps1` and `build-installer.ps1` also accept `-IsccPath C:\path\to\ISCC.exe` to use a compiler you supply. Build output and fresh package staging stay under `artifacts/`, which is ignored by Git. Packaging uses an explicit source allowlist and never includes personal settings, speech files, private voice installers, or old binaries.

CI runs eight isolated console-check projects on Windows, builds the installer and packages, then checks installation and removal in an isolated directory before uploading them. Version tags such as `v2.4.0` run the same checks before a GitHub release is created; the tag must match the version in `VRCTTS.csproj`. Each release publishes one installer, three ZIPs, and a checksum file. The release workflow can also be run manually for an existing tag. Pushing a change to the release workflow on `main` publishes the existing tag declared by the project version, which covers repositories whose initial publication did not trigger a tag event. Release builds always check out that immutable tag, and existing assets are never overwritten automatically. No workflow requires provider API keys or exercises real microphone playback, driver installation, or live speech APIs.

Automated checks cover request formats, model routing, key separation, bounded downloads, cancellation, Justin fallback, audio decoding, OSC packets/local delivery, phrase persistence/cache isolation, legacy migration, saved voice selection, and WPF layout. Fish and Lazybird authenticated generation, licensed offline Justin, a clean driver installation, and in-game microphone speech still require environment-specific verification. The checks do not claim those live scenarios passed.

## Project and dependency licensing

No standalone open-source license has been selected for VRCTTS itself. Dependency licenses and vendor terms are listed in [THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt), [DOTNET-LICENSE.txt](DOTNET-LICENSE.txt), and [DOTNET-NOTICES.txt](DOTNET-NOTICES.txt). Those dependency licenses do not serve as a project license.

See [README.txt](README.txt) for detailed setup and troubleshooting.
