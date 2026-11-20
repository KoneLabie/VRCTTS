VRCTTS is a Windows x64 TTS app with headphone and virtual-microphone routing,
saved Quick TTS phrases, and optional VRChat OSC chatbox output.

- Recommended: download and extract the **win-x64.zip** portable package.
- The **compact-win-x64.zip** requires the .NET 10 Desktop Runtime, x64.
- The **Source.zip** includes the build scripts, automated checks, and notices.
- Compare downloaded ZIPs with **SHA256SUMS.txt**.

First launch may need administrator approval to install the bundled, signed
VB-CABLE driver. The app executable itself is unsigned. In VRChat, select
**VRCTTS** as the microphone and enable OSC for chatbox text.

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
