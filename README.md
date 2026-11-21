# Phonon SAPI

Offline Phonon voices for Windows applications that use Microsoft SAPI 5.
Includes 102 voices in English, French, German, Spanish and Portuguese, including
English character voices. The model and voices are included, with no speech
server or first-use download required.

## Changelog

### 1.0.10

- Preview a voice before importing it. Correct an imported voice's language without replacing it.
- Status reports built-in and imported voice counts as well as the current speech backend.

### 1.0.9

- English speech reads UK currency in pounds and pence, including "one penny."

### 1.0.8

- Choose shorter or longer pauses in Phonon speech from the settings app.
- Enable **Play when voice changes** to hear the preview while selecting a voice.
- Press **Ctrl+T** to play and **Ctrl+R** to reset the sample without leaving the current control.
- Setup asks before installing and helps you deal with apps that are using Phonon.

### 1.0.5

- The fastest settings in SAPI applications now speak faster. The default
  speed, slower rates and lower speed increases are unchanged.

### 1.0.4

- Check for updates with Shift+F1. The shortcut is announced separately from the button name.
- The updater lists the apps using Phonon. When Windows can safely reopen them, it can close and reopen them for you. Otherwise, you can queue the update for your next restart.

### 1.0.3

- Import compatible Phonon voice files and use them in 32-bit and 64-bit SAPI applications.
- Preview samples follow the selected language without replacing your own text.

### 1.0.1

- Improved speech-generation efficiency on CPU and GPU without changing the model or voice data.
- Kept frequently used voices ready while browsing larger voice selections, avoiding unnecessary delays when returning to them.

### 1.0.0

First release, with multilingual voices, GPU acceleration, rate, pitch and volume
controls, fast voice switching, a settings application, diagnostics and signed
updates. Supports both 32-bit and 64-bit SAPI applications.

## Installation

Requires 64-bit Windows 10 or later, .NET Framework 4.8 and an AVX2-capable
processor. Both 32-bit and 64-bit SAPI applications can use the voices. A compatible installed Vulkan
graphics driver is recommended; CPU operation is available but may be slower.
No CUDA toolkit or graphics-driver installer is included.

Open the downloaded **Phonon SAPI Setup** and choose **Install** or **Cancel**.
Administrator permission is requested only after choosing to continue.
If apps are using Phonon, Setup names them and lets you retry with the same
download. When all listed apps support safe reopening, you can let Windows close
and reopen them. Keep your screen reader running. Switch it to another voice
if it is listed, or choose **Install at next restart** for an upgrade.
Setup installs the application, registers the voices and adds a Start menu
shortcut. Your default Windows voice is not changed. Reopen
your reading application, then choose a voice such as **Elodie-Rose - Phonon
(English UK)**.
The settings application groups voices by language, then alphabetically. SAPI
and other applications may order them differently.

Open **Phonon SAPI** from the Start menu to preview voices, choose GPU acceleration,
read this manual or check for updates. Language filters the application's list;
it does not translate text. Other SAPI applications list the voices individually.
The preview sample follows the selected voice's language. Your own text stays
in the box when you switch voices. **Reset Sample** restores the example for the
selected voice.

Open **Manage Imported Voices** to find all your imports in one list. Use
**Add Voice** to choose a compatible `.safetensors` file. Use **Preview Voice**
to hear it before saving. Preview uses English until you choose a language.
Give it a name and select its spoken language. If the language is wrong, select
the import and choose **Change Language**; it remains the same voice in your
speech applications. Phonon keeps a copy for your Windows account and leaves
the original file alone. Select the import and use **Remove Voice** to
remove that copy. Reopen other speech applications if their voice list has not
refreshed. If an application still holds a removed voice open, Phonon uses a
built-in voice until you select another one.
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
**Pause length** keeps the original timing with **Default** or adjusts longer
gaps within speech with **Shorter** or **Longer**. It applies to the next
utterance in any application using a Phonon SAPI voice.
**Status** reports built-in and imported voice counts and the actual initialized engine: GPU (Vulkan), CPU
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
always-running updater. **Check for updates** or **Shift+F1** checks immediately. Update checks
and approved downloads contact GitHub; synthesis itself is entirely offline.

Updates require a valid signature and matching product, version, size and hash.
If another application is using Phonon, the settings window stays open and
lists the applications using it. Keep your screen reader running, but switch
it to another voice if it is listed. Save your work, close the other listed applications and choose
**Retry**, or choose **Cancel**. If all the listed apps support Windows' app
restart feature, **Close apps and update** can close and reopen them for you.
Save your work first. The updater does not automatically close screen readers.
If you cannot safely close an app, choose
**Install at next restart**. After you approve the administrator prompt, the
verified update is queued. Save your work and restart Windows when ready;
Phonon does not restart it or close any apps for you. Open Phonon SAPI again
after signing in to see the result. To cancel before restarting, check for
updates again and confirm that you want to cancel the scheduled update.
Once Windows Installer starts, the settings window closes. Reopen Phonon SAPI
from the Start menu afterward. Windows Installer checks again for applications
using Phonon and does not force a restart.

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

The application also has **Phonon from Gradium**, **Software catalogue** and **Donate** buttons.
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
