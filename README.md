# Phonon SAPI

Offline Phonon voices for Windows applications that use Microsoft SAPI 5.
Includes 102 voices in English, French, German, Spanish and Portuguese, including
English character voices. The model and voices are included, with no speech
server or first-use download required.

## Changelog

### 1.0.0

First release, with multilingual voices, GPU acceleration, rate, pitch and volume
controls, fast voice switching, a settings application, diagnostics and signed
updates. Supports both 32-bit and 64-bit SAPI applications.

## Installation

Requires 64-bit Windows 10 or later, .NET Framework 4.8 and an AVX2-capable
processor. Both 32-bit and 64-bit SAPI applications can use the voices. A compatible installed Vulkan
graphics driver is recommended; CPU operation is available but may be slower.
No CUDA toolkit or graphics-driver installer is included.

Run the downloaded **Phonon SAPI .msi** installer and approve the administrator
prompt. It installs the application, registers the voices and adds a Start menu
shortcut. Your default Windows voice is not changed. Reopen
your reading application, then choose a voice such as **Elodie-Rose - Phonon
(English UK)**.
The settings application groups voices by language, then alphabetically. SAPI
and other applications may order them differently.

Open **Phonon SAPI** from the Start menu to preview voices, choose GPU acceleration,
read this manual or check for updates. Language filters the application's list;
it does not translate text. Other SAPI applications list the voices individually.
The speaking rate and volume are normally controlled by the reading application;
pitch is supported through SAPI XML. Audio uses that application's selected
SAPI output device or file, not a separate Phonon playback device.
In the settings application, rate and pitch range from 0 to 100; 50 is neutral.
Other SAPI applications retain their own control ranges.

This is a SAPI 5 engine, not a OneCore voice package. Applications that only use
Windows OneCore voices are not supported. Windows on ARM and non-AVX2 processors
have not been verified.

## Speech Behaviour

The model generates human-like speech. Single letters, abbreviations and numbers
can be less reliable than longer phrases. Pause and resume are handled by the
SAPI application; Stop or Purge cancels generation and discards pending output.
The model stays ready for faster voice changes while your reading application
is using it. No background speech service starts at sign-in.

GPU acceleration is on by default. If GPU initialization fails, the host quietly
uses CPU synthesis. Changing the GPU option takes effect on the next utterance.
**Synthesis backend** reports the actual initialized engine: GPU (Vulkan), CPU
with GPU disabled, or CPU fallback when GPU initialization was unavailable.
Before speech has initialized an engine, it reports that no result is available.
Different applications can hold independent engines and therefore use additional
memory. Text, audio and voice data are not uploaded.

Bookmarks and explicit XML pauses are supported. Precise word highlighting,
phoneme/viseme events, SAPI pronunciation lexicons and sentence-skipping are not
supported. Skip requests report zero skipped sentences.

## Updates And Removal

Update checking can be Never, Hourly or Daily. Daily is the default. Checks occur
when the Phonon SAPI application is opened and while it remains open; there is no
always-running updater. **Check for updates** checks immediately. Update checks
and approved downloads contact GitHub; synthesis itself is entirely offline.

Updates require a valid signature and matching product, version, size and hash.
Close applications using Phonon before updating; select another voice first if
your screen reader is using it. After your approval, Windows Installer opens
and the settings window closes. Reopen Phonon SAPI from the Start menu afterward.
An application still using the voice blocks installation; nothing is forcibly
closed, and no restart is forced.

Updates replace the existing application in one folder rather than retaining
copies of previous versions. Failed upgrades roll back. Saved preferences and
the default voice are preserved. Successful updates remove their temporary
downloaded installer.
Abandoned downloads older than 24 hours are cleared the next time the settings
application opens.

**Repair installation** uses Windows Installer to restore application files.
It may ask for the original installer; download the matching version again if
you no longer have it. **Remove application**, or Uninstall in Windows' installed
apps list, removes the application and its voice registrations while preserving
your saved preferences. Other speech engines are not removed.

The application records bounded diagnostics under Local AppData in the
PhononSAPI folder; it does not record spoken text or audio.
**Diagnostics** displays the recent application and host logs without opening
Explorer. **Refresh** updates them on demand; **Copy** copies the displayed
text. The view does not refresh automatically while you read it.

## Downloads And Support

- [Download Phonon SAPI](https://github.com/OnjLouis/phononSAPI/releases/latest)
- [Project, source code and issue tracker](https://github.com/OnjLouis/phononSAPI)
- [Phonon for NVDA](https://github.com/OnjLouis/phononNVDA)
- [Phonon for Mac (Apple Silicon)](https://github.com/OnjLouis/phononMac/releases/latest)
- [Andre Louis software catalogue](https://onj.me/software)
- [Contact Andre Louis](https://onj.me/contact)
- [Donate to support development](https://onj.me/donate)

The application also has **Software catalogue** and **Donate** buttons.
Donations are optional and do not unlock features.

## Credits And Licences

By Andre Louis, with development assistance from OpenAI Codex.
Built using [Gradium's Phonon runtime](https://github.com/gradium-ai/xn-ptts),
which builds on Kyutai's Pocket TTS and Mimi. Voice metadata and recordings are
from [Gradium's catalogue](https://huggingface.co/gradium/phonon-voices).
Rate and pitch processing use [Sonic by Bill Cox](https://github.com/waywardgeek/sonic).
The Windows installer is built using [WiX Toolset](https://github.com/wixtoolset/wix).
Microsoft's [SAPI engine porting guide](https://learn.microsoft.com/en-us/previous-versions/windows/desktop/ee431802(v=vs.85))
documents the Windows speech interface used by this product.

Application and adapter code are GPL-3.0-or-later. Phonon's runtime is MIT OR
Apache-2.0, Sonic and the voice catalogue are Apache-2.0, and the bundled
[40M multilingual model](https://huggingface.co/gradium/phonon-40m-multilingual)
is CC BY-NC 4.0, restricting commercial use. The model was repackaged for the
native runtime, not retrained. Original notices and licence texts are included
in the Licenses folder. This is an independent project, not an official or
endorsed Gradium or Microsoft product.
