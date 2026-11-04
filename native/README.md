# Native Dependencies

The SAPI adapters and host are built with C++17 and the Windows SDK. CMake builds
the adapter matching the selected target architecture. A 64-bit build also
builds the host and the .NET Framework settings application. Build the adapter
again for x86, then distribute both DLLs beside the single x64 host and manager.
LLVM-MinGW builds need `SAPI_DDK_INCLUDE` pointing to the Windows SDK's `um`
folder containing `sapiddk.h`; only that header is copied into the build tree,
avoiding incompatible mixed Windows SDK/MinGW foundational headers.

The host uses the same native Phonon ABI and payload as
[Phonon for NVDA](https://github.com/OnjLouis/phononNVDA/tree/main/native):
Gradium `xn-ptts` v0.5.0, revision
`e851445158f07061f063e3edff0080f9426c0da2`, with the supplied Windows/Vulkan
patches applied before building. Those patches add Vulkan compute unit 3, bound
the CPU pool and fix Vulkan initialization cleanup. Apply `ptts-performance.patch`
after `ptts-windows.patch` for the merged upstream flow-conditioning and bounded
voice-cache improvements ([PR 296](https://github.com/gradium-ai/xn-ptts/pull/296)
and [PR 325](https://github.com/gradium-ai/xn-ptts/pull/325)). Credit to David Blum
and Rudraksh Karpe for those changes. They preserve the existing ABI, model and
sampling defaults. The source and patches must
be retained when rebuilding the native DLL. The matching LLVM libunwind runtime
is required beside `ptts_ffi.dll` under `runtime`.

CMake retrieves exactly two Sonic source files from revision
`b93885dcb70aae50c6f76b0fe4e0868f029a077e` and verifies their SHA-256 hashes.
Sonic is Apache-2.0; retain its original licence and notices. Application and
adapter source is GPL-3.0-or-later, which permits this combination.

Model and voice data are not included in the source repository. Obtain them
under their original terms, not from a live application's profile. The runtime
needs the multilingual configuration, tokenizer and original float model under
`model`, and the catalogue's named speaker tensors under `model/voices`.
Do not ship a different checkpoint with this configuration. The package's
`voices.json` records stable token IDs, voice names, spoken language and SAPI
language IDs. Unknown character-voice gender stays unspecified rather than guessed.

The host exchanges bounded binary frames with its parent; it never opens an
audio device. The SAPI adapters own a cancellation event and a kill-on-close
job, inherit only the necessary handles, discard cancelled frames, and keep the
native handle on its owning host thread. Do not unload/free it during synthesis.
No logging occurs in the audio callback.

## Windows Installer

The `installer` sources use WiX Toolset 6 and one fixed Program Files folder.
Compile `prerequisites.cpp` as an x64 DLL, linking `msi`, `advapi32` and `rstrtmgr`.
With LLVM-MinGW, copy only the Windows SDK's `RestartManager.h` into a separate
include folder, as for the SAPI header above. Do not define
`PHONON_INSTALLER_TEST` in a distribution build.

Build `Package.wxs` and `Payload.wxs` with `wix build -arch x64`. Define
`ProductName=Phonon SAPI`, `Version`, a version-specific `ProductCode`,
`UpgradeCode={7688E43D-5355-50FC-AFF3-541E76F34049}`, `Scope=perMachine`,
`RegistryRoot=HKLM`, empty `RegistryPrefix64` and `RegistryPrefix32`,
`PayloadRoot` pointing to the fresh payload, `Checker` pointing to the compiled
prerequisite DLL, and `TestMode=0`. Keep existing component GUIDs and the upgrade
code stable across releases. `Payload.wxs` lists the packaged files and both
registry views; update that inventory when assets or voices change.

The early safety check refuses an in-use installation rather than shutting down
speech applications. Windows Installer owns transactional replacement, rollback
and removal. Successful updates delete only their exact, validated temporary
download; user-downloaded installers and saved preferences are not removed.
