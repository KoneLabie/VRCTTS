VRCTTS is a Windows x64 TTS app with headphone and virtual-microphone routing,
saved Quick TTS phrases, and optional VRChat OSC chatbox output.

- Recommended: run **VRCTTS-2.4.0-Setup.exe**, then open **VRCTTS** from the Start menu.
- The installer includes the .NET 10 runtime, installs for your Windows user,
  and offers an optional desktop shortcut.
- For portable use, download and extract **win-x64.zip**.
- The **compact-win-x64.zip** requires the .NET 10 Desktop Runtime, x64.
- The **Source.zip** includes the build scripts, automated checks, and notices.
- Compare the installer and downloaded ZIPs with **SHA256SUMS.txt**.

Installing the app does not need administrator approval. First launch may
need it to install the bundled, signed VB-CABLE driver. The installer and
app executable are unsigned. In VRChat, select
**VRCTTS** as the microphone and enable OSC for chatbox text.

Uninstalling VRCTTS removes its installed program and shortcuts. Saved phrases,
settings, API keys, and the shared VB-CABLE microphone driver are preserved.

Building the installer from source automatically downloads the official portable
Inno Setup 7.1.1 compiler and verifies its pinned checksum and publisher signature.
No preinstalled compiler is required. `package.ps1` and `build-installer.ps1`
accept `-IsccPath` to use a compiler you supply. CI checks isolated installer
installation and removal before publishing packages.

Fish voices require a user-supplied Fish Audio API key. Hasan Piker uses a
separate Lazybird API key. Justin tries its online service first, then an
installed and activated original IVONA Justin voice. No offline voice model,
installer, activation key, or provider credentials are bundled.

This release uses supported .NET 10 LTS, validates all 19 voice selections,
avoids caching failed playback, and restores the microphone name on an
existing healthy cable. Automated checks and silent Windows audio-route
checks passed locally. Authenticated Fish/Lazybird generation, licensed
offline Justin, a new driver installation, and in-game speech remain
environment-specific checks; they are not claimed as verified here.
