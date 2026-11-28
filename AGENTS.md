# ClipThat — AI Architecture & Project State Reference

> **AI Reference File (Read-Only Context)**
> This document is written strictly for AI coding assistants to rapidly understand ClipThat's architecture, file layout, runtime behavior, invariants, and current project state.
> - Do **not** treat this as end-user documentation (user-facing docs live in `README.md` in Polish).
> - Only update this file when architecture, key files, build/test commands, or known issues materially change.

---

## 1. Project Snapshot

- **What it is:** Lightweight Windows 10/11 WPF desktop replay recorder and clip trimmer ("ClipThat") with continuous RAM ring-buffer recording (`1–10 min`), 3-track WASAPI AAC audio (`System`, `Microphone`, `CombinedMix`), global save hotkey, and built-in multi-track trim/export editor.
- **Target runtime:** `.NET 8` (`net8.0-windows10.0.22621.0`, `SupportedOSPlatformVersion 10.0.19041.0`, `UseWPF`, `UseWindowsForms`, `AllowUnsafeBlocks`, `<RollForward>Major</RollForward>`).
- **NuGet dependencies:** `NAudio 2.4.0`, `Vortice.Direct3D11 3.8.3`, `Vortice.MediaFoundation 3.8.3`, `WPF-UI 4.3.0`.
- **User-facing language:** **Polish (`pl-PL`)** for all UI labels, dialogs, tray tooltips/notifications, error messages, and `README.md`.
- **Current verified performance baseline:** **~0.1% CPU** and **~200 MB RAM** in background tray mode at 1080p 60 FPS / 3 min on AMD Radeon RX 9070 XT (native zero-copy AMD AMF H.264, Peak-Constrained VBR, WPF window closed to tray).
- **Automated tests:** `86 / 86` xUnit tests passing (`ClipThat.Tests`).

---

## 2. Hard Rules & Operational Invariants for AI Agents

1. **Never kill the user's running `ClipThat.exe` unprompted.** When `ClipThat.exe` is running in the tray, `bin\Release\net8.0-windows10.0.22621.0\ClipThat.exe` is locked (`MSB3026`/`MSB3027`). Build and run tests in `-c Debug` unless the user asks to rebuild Release or closes the app.
2. **Batch/PowerShell line endings MUST be Windows `CRLF` (`\r\n`).** Enforced via `.gitattributes` (`*.bat`, `*.cmd`, `*.ps1` → `text eol=crlf`). Unix `LF` breaks `cmd.exe` label lookups (`call :StopExistingInstance`).
3. **Local signing policy:** `ClipThat.csproj` (`SignOutputs` / `SignPublishedOutputs`) only signs binaries if a valid `CN=ClipThatDev` or `CN=ClipThat Local` certificate already exists in `Cert:\CurrentUser\My`. Never auto-create or inject certificates into `Cert:\CurrentUser\Root` (triggers blocking Windows Security Warning dialogs on end-user PCs).
4. **External tools (`Assets\ffmpeg.exe`):** Gitignored. Auto-downloaded and SHA-256 verified from `gyan.dev` during build (`EnsureFfmpegAsset` target in `ClipThat.csproj`). Regular builds copy `ffmpeg.exe` beside output; `PublishSingleFile=true` embeds it as `ClipThat.Assets.ffmpeg.exe` (extracted by `PortableFfmpegProvider`).
5. **Single-instance & shutdown:** `App.xaml` sets `ShutdownMode="OnExplicitShutdown"`. Named kernel objects:
   - Mutex: `Local\ClipThat_SingleInstance_Mutex`
   - Events: `Local\ClipThat_ShowWindow_Event`, `Local\ClipThat_Exit_Event`
   - CLI args: `--minimized` (start in tray), `--exit` (gracefully close running instance).
6. **Build & test commands:**
   - Debug build (safe while tray app runs): `dotnet build ClipThat.csproj -c Debug --nologo`
   - Unit tests: `dotnet test ClipThat.Tests/ClipThat.Tests.csproj -c Debug --nologo`
   - User launch / Release rebuild: `Start_ClipThat.bat` (or `Start_ClipThat.bat --rebuild` to gracefully stop running instance, rebuild `Release`, and restart).

---

## 3. End-to-End Architecture & Data Flows

### 3.1 Application & Tray Lifecycle (`App.xaml.cs`, `UI/MainWindow.xaml.cs`)
- `App.OnStartup` handles `--exit`, acquires the single-instance mutex, initializes `SettingsService`, `DisplayService`, `AudioDeviceService`, `ResourceEstimatorService`, `StartupService`, `ClipEditorService`, `ReplayEngine`, WinForms `NotifyIcon` tray menu, and `GlobalHotkeyService`.
- Closing `MainWindow` **destroys** the WPF window (instead of `Hide()`) whenever `!_pageProvider.IsExporting`, then schedules a delayed non-blocking `GC.Collect(2, GCCollectionMode.Optimized, blocking: false)` after 1.5 s to reclaim ~60–110 MB of WPF visual/Mica memory while gaming. Reopening from tray constructs a fresh `MainWindow` on demand.
- Active/inactive Mica surface tint brushes (`AppRailBrush`, `AppCardBrush`, `AppClipItemBrush`, `AppWorkspaceBrush`) live in `App.xaml` and toggle in `MainWindow.UpdateSurfaceBrushes`.

### 3.2 Video Capture & GPU Processing (`Engine/Capture`, `Engine/Video`)
```
Monitor (HMONITOR)
  ├─ Primary:  DxgiFrameCapture (DXGI Desktop Duplication + DxgiCursorCompositor HLSL overlay, SDR + HDR scRGB/PQ)
  └─ Fallback: WgcFrameCapture  (Windows.Graphics.Capture; used for rotated displays, DXGI access denied, or recovery)
       │
       ▼ (shared ID3D11Device bound to target monitor's exact DXGI adapter via MonitorDeviceFactory)
  GpuVideoProcessor
    ├─ Hardware path: ID3D11VideoProcessor BGRA/HDR -> scaled even-dimension NV12 texture pool (OutputSurfacePoolSize = 8)
    └─ Fallback path: GPU staging readback -> CPU NV12 buffer (for Tier 2/3 encoders)
```
- `ReplayEngine` decouples capture callbacks from encoding via a 1-slot latest-frame handoff (`VideoEncodeWorkerLoop`) and protects textures currently owned by the encoder via `IVideoEncoder.IsTextureInUse(texture)` + `_activeEncodeTexture`.

### 3.3 Video Encoding (`Engine/Encoding`, `Engine/ReplayEngine.cs`)
- Unified contract: `IVideoEncoder`.
- **Encoder selection & fallback order (`ReplayEngine.CreateEncoder`):**
  1. **AMD GPUs (Tier 1):** `AmfH264Encoder` tried first when `AmfH264Encoder.ShouldTryForAdapter(adapterName)` detects AMD/Radeon + loadable `amfrt64.dll`. Uses native C ABI zero-copy `AMFContext::InitDX11` + `CreateSurfaceFromDX11Native` directly on `GpuVideoProcessor` NV12 textures. *(Bypasses a broken AMD Adrenalin RDNA4 `AMDh264Encoder` Media Foundation MFT that stalls after 1 frame.)*
  2. **NVIDIA NVENC / Intel QSV / AMD MFT fallback:** `MfH264Encoder` 3-tier negotiation:
     - `HardwareGpuZeroCopy` (async MFT + `IMFDXGIDeviceManager` D3D11 zero-copy NV12)
     - `HardwareCpuBuffer` (hardware MFT with CPU NV12 buffer)
     - `SoftwareFallback` (Microsoft H.264 Software MFT with `CODECAPI_AVEncCommonQualityVsSpeed = 33`)
- **Encoding profile:** Peak-Constrained VBR (fallback: Unconstrained VBR → CBR), 2-second GOP (`fps * 2`), BT.709 studio range (16–235), target bitrates from `ResourceEstimatorService` (`1080p60`: **12.0 Mbps target / 16.2 Mbps peak**, `1080p30`: 8.0 Mbps, `720p60`: 6.5 Mbps, `720p30`: 4.5 Mbps; `PeakBitrateMultiplier = 1.35`).
- **Watchdog & recovery:** `ReplayEngine` runs a 1.5 s keyframe watchdog (with idle deferrals on static desktops) that downgrades encoder tier on stall, startup priming timer, static keep-alive timer, and automatic pipeline recovery on display/session interruptions.

### 3.4 Multi-Track Audio Capture (`Engine/Audio/DualAudioCaptureService.cs`)
```
WASAPI System Loopback (20 ms buffer + silence watchdog) ─┐
                                                          ├─► AudioPcmConverter (NAudio 5.1/7.1 downmix + resample -> 48 kHz 16-bit Stereo PCM)
WASAPI Microphone Capture (optional; "none" disables)   ──┘      │
                                                                 ├─► AacMftEncoder (128 kbps raw AAC) -> MediaTrackType.SystemAudio
                                                                 ├─► AacMftEncoder (128 kbps raw AAC) -> MediaTrackType.MicrophoneAudio
                                                                 └─► CombinedAudioMixPipeline (TrackPcmTimelineBuffer 2 s ring + MixSamplesSoftClip)
                                                                       └─► AacMftEncoder (128 kbps raw AAC) -> MediaTrackType.CombinedMixAudio
```
- Implements `IMMNotificationClient` to hot-swap default playback/capture devices automatically when Windows endpoints change.

### 3.5 Circular RAM Buffer (`Engine/Buffer/MediaRingBuffer.cs`)
- Thread-safe GOP-aligned circular buffer (`1–10 min` window, enforces leading video keyframe and a `1024 MB` minimum free physical RAM guard).
- Packet payloads are allocated in **unmanaged native memory slabs** (`NativeMemory`, 2 MiB pooled slabs + dedicated slabs for >512 KB frames) to avoid managed GC heap pressure.
- `SnapshotPackets()` retains zero-copy ref-counted packet slabs so saving an MP4 does not copy buffers or stall live recording.

### 3.6 Replay Saving / MP4 Muxing (`Engine/Muxing/Mp4ReplayMuxer.cs`)
1. **Primary path:** Native Media Foundation `IMFSinkWriter` remux (stream copy, no re-encode) into `.clipthat_mux_<guid>.tmp`, atomically renamed to `ssmmHHddMMyyyy.mp4`.
2. **Track selection patch:** `PatchAudioTrackSelectionFlags` edits `moov → trak → tkhd` (`alternate_group = 1` + enabled flags) so players (Windows Media Player, VLC, Discord, browsers) default to the last audio track (`CombinedMixAudio` when 3 tracks exist), while editors can still access all tracks.
3. **Fallback path:** Writes a VFR-accurate 90 kHz PTS MPEG-TS intermediate (`WriteMpegTsIntermediate`) and remuxes via `PortableFfmpegProvider` (`ffmpeg.exe -c copy`).

### 3.7 Clips Browser, Preview & Trim Export (`UI/Pages/ClipsPage.*`, `Services/ClipEditorService.cs`, `Services/MfTrackAudioWaveStream.cs`)
- Watches output folder with `FileSystemWatcher` and reads metadata via `Mp4ReplayMuxer.TryReadMp4Metadata` → Windows Shell `StorageFile` → `ffprobe` fallback.
- Preview plays muted video in WPF `MediaElement` synced to an NAudio `WaveOutEvent` + `MixingSampleProvider` + per-track `VolumeSampleProvider`.
  - Fast audio preview path: `MfTrackAudioWaveStream` streams decoded PCM directly from MP4 audio tracks 0 and 1 via `IMFSourceReader` (zero temp files).
  - Fallback path: extracts temporary WAV tracks to `%TEMP%\ClipThat\Preview\` via FFmpeg.
- Export trims via FFmpeg (`-ss` before `-i`, `-t` after `-i`) using `-c:v copy` (or optional hardware/software re-encode `h264_nvenc` / `h264_amf` / `h264_qsv` / `libx264`), supporting `MixSingleTrackForDiscord` (1 mixed AAC track) and `KeepSeparateAudioTracks` (3 tracks: System, Mic, Mix).

---

## 4. Repository File Map

### Root & Scripts
| File | Lines | Role |
|---|---:|---|
| `ClipThat.csproj` | 63 | Main WPF .NET 8 project, NuGet refs, `EnsureFfmpegAsset` & `SignOutputs` targets |
| `App.xaml` / `App.xaml.cs` | 20 / 942 | Entry point, single-instance IPC, tray icon/menu, global hotkey hook, exception logging, Mica accent/theme |
| `Start_ClipThat.bat` | 149 | End-user launcher (auto-installs .NET 8 SDK via winget if missing, incremental Release rebuild check, `--rebuild`, `--minimized`, `--exit`) |
| `Aktualizuj_ClipThat.bat` | 70 | Self-updater (`git pull` when `.git` exists, or GitHub ZIP download preserving `Assets\ffmpeg.exe`) |
| `.gitattributes` | 3 | Forces `CRLF` on `*.bat`, `*.cmd`, `*.ps1` for both `git clone` and GitHub ZIP archives |
| `README.md` | 25 | End-user Polish setup & update guide |
| `Assets/app.ico` | — | Application icon |
| `Assets/ffmpeg.exe` | — | Gitignored; auto-downloaded FFmpeg binary |

### `Engine/` — Capture, Processing, Encoding, Audio, Buffer, Muxing
| File | Lines | Role |
|---|---:|---|
| `Engine/IReplayEngine.cs` | 53 | Public replay engine contract & `EngineStatusLevel` enum |
| `Engine/ReplayEngine.cs` | 1674 | Pipeline coordinator, capture/encoder orchestration, watchdog, tier downgrade, session recovery, replay save |
| `Engine/Capture/IFrameCaptureSource.cs` | 371 | Capture backend interface + `MonitorDeviceFactory` (monitor-to-DXGI-adapter matching & D3D11 device creation) |
| `Engine/Capture/DxgiFrameCapture.cs` | 666 | Primary DXGI Desktop Duplication capture loop with frame pacing & duplication recovery |
| `Engine/Capture/DxgiCursorCompositor.cs` | 734 | Pure-GPU HLSL D3D11 cursor compositor (Color, MaskedColor, Monochrome; SDR & HDR PQ/scRGB) |
| `Engine/Capture/WgcFrameCapture.cs` | 479 | Fallback `Windows.Graphics.Capture` free-threaded capture backend |
| `Engine/Video/GpuVideoProcessor.cs` | 1007 | D3D11 `ID3D11VideoProcessor` BGRA/HDR → NV12 scaling, pooled NV12 textures (`8`), CPU staging fallback |
| `Engine/Encoding/IVideoEncoder.cs` | 47 | Common H.264 encoder contract |
| `Engine/Encoding/AmfH264Encoder.cs` | 776 | Native AMD AMF (`amfrt64.dll`) zero-copy D3D11 H.264 encoder (unsafe C ABI vtable interop) |
| `Engine/Encoding/MfH264Encoder.cs` | 2335 | 3-tier Media Foundation H.264 encoder (NVENC/QSV/AMF MFT zero-copy, HW CPU-buffer, MS Software MFT) |
| `Engine/Encoding/AnnexBHelper.cs` | 88 | Shared H.264 Annex-B IDR detection & SPS/PPS extraction |
| `Engine/Audio/DualAudioCaptureService.cs` | 2928 | WASAPI system loopback + mic capture, NAudio PCM conversion/downmix, 3-track timeline mixer + soft-clip, AAC MFT encoder, hot-swap |
| `Engine/Buffer/MediaRingBuffer.cs` | 1057 | Thread-safe GOP-aligned circular buffer backed by unmanaged `NativeMemory` slab pool + `EncodedPacket` / `EncodedPacketSnapshot` |
| `Engine/Muxing/Mp4ReplayMuxer.cs` | 1467 | Native MF `IMFSinkWriter` MP4 remuxer, `tkhd` audio track flag patcher, MP4 box metadata reader, MPEG-TS + FFmpeg fallback |
| `Engine/Interop/CaptureInterop.cs` | 345 | Win32/WinRT interop (`HMONITOR`, `GraphicsCaptureItem`, `IDirect3DDevice`, `IDirect3DDxgiInterfaceAccess`) |
| `Engine/Interop/EngineDisposeHelper.cs` | 29 | Shared best-effort `SafeDispose` helper across `Engine/` |
| `Engine/Interop/MediaFoundationMftHelper.cs` | 177 | Shared MF MFT activate unwrapping, `OutputDataBuffer` COM alias cleanup, refcount & stream-ID helpers |
| `Engine/Diagnostics/EngineLogger.cs` | 89 | Thread-safe rotating file logger (`%LocalAppData%\ClipThat\engine.log`, 5 MB cap) |

### `Models/` & `Services/`
| File | Lines | Role |
|---|---:|---|
| `Models/AppSettings.cs` | 117 | User settings POCO, `Normalize()` clamping/sanitization, `Clone()` |
| `Models/AudioDeviceInfo.cs` | 42 | Audio endpoint view model (`DeviceId`, `Name`, `IsDefault`, `IsDisabled`) |
| `Models/ClipItem.cs` | 122 | Recorded clip view model, `ssmmHHddMMyyyy` filename date parser, Polish size/duration/track formatters |
| `Models/DisplayDeviceInfo.cs` | 30 | Physical display bounds, index, refresh rate model |
| `Models/ResolutionPreset.cs` | 13 | Enum (`P240=240`, `P360=360`, `P480=480`, `P720=720`, `P1080=1080`) |
| `Models/ResourceEstimate.cs` | 9 | RAM/disk/bitrate estimate record |
| `Services/SettingsService.cs` | 166 | Atomic JSON persistence (`%AppData%\ClipThat\settings.json`), corrupt backup (`.corrupt.bak`), thread-safe clone cache |
| `Services/ResourceEstimatorService.cs` | 72 | Target & peak VBR bitrates, MiB disk & RAM buffer estimator |
| `Services/DisplayService.cs` | 148 | Enumerates physical monitors via WinForms + Win32 `EnumDisplaySettingsW`/`EnumDisplayDevicesW` |
| `Services/AudioDeviceService.cs` | 330 | WASAPI playback/capture endpoint enumeration, resolution, and `IMMNotificationClient` change bridge |
| `Services/GlobalHotkeyService.cs` | 314 | Win32 `RegisterHotKey` message-only `HwndSource` service + Polish key label formatter |
| `Services/StartupService.cs` | 203 | Windows autostart management (`HKCU\...\Run\ClipThat` + `StartupApproved` check) |
| `Services/PortableFfmpegProvider.cs` | 289 | Adjacent/embedded `ffmpeg.exe` resolution & atomic extraction to `%LocalAppData%\ClipThat\bin\ffmpeg.exe` |
| `Services/ClipEditorService.cs` | 1731 | Clip folder scanning + metadata cache, FFmpeg download/SHA-256 verify, HW encoder probe, preview WAV extraction, trim/mix export |
| `Services/MfTrackAudioWaveStream.cs` | 528 | Zero-disk-I/O NAudio `WaveStream` decoding individual MP4 audio tracks directly via MF `IMFSourceReader` |

### `UI/`
| File | Lines | Role |
|---|---:|---|
| `UI/MainWindow.xaml` / `.cs` | 66 / 236 | `FluentWindow` navigation host (`SettingsPage`, `ClipsPage`), Mica active/inactive brushes, close-to-tray lifecycle |
| `UI/UiBrushHelper.cs` | 22 | Shared `CreateFrozenBrush` helper |
| `UI/Pages/SettingsPage.xaml` / `.cs` | 434 / 842 | Polish settings UI: monitor layout, buffer slider + live estimator, resolution/FPS, audio selectors, hotkey box, folder picker, autostart |
| `UI/Pages/ClipsPage.xaml` / `.cs` | 788 / 2277 | Clips browser, collapsible sidebar, muted video preview + NAudio dual-track mixer, trim timeline, export controls & progress |
| `UI/Controls/MonitorLayoutControl.xaml` / `.cs` | 47 / 559 | Proportional multi-monitor canvas with on-screen display identification overlay badges |
| `UI/Controls/RangeTrimSlider.xaml` / `.cs` | 173 / 497 | Unified playback + dual-handle range trim bar (`MinimumGapSeconds = 0.5`) |
| `UI/Controls/HotkeyBox.xaml` / `.cs` | 77 / 264 | Interactive global hotkey recorder control |
| `UI/Dialogs/ConfirmDialog.xaml` / `.cs` | 135 / 91 | Styled Fluent confirmation modal (e.g., clip deletion) |

### `ClipThat.Tests/` (86 tests total)
- `AudioAndCodecPipelineTests.cs` — 5.1/7.1 downmix, soft-clip mixer, Annex-B IDR/SPS/PPS parsing, `AmfVariant` 24-byte x64 ABI & AMD adapter filter.
- `ClipEditorServiceTests.cs` — FFmpeg argument ordering/codecs/filters, Polish locale decimal invariance, `ScanClipsAsync` sorting & cache invalidation, export validation, stderr time & SHA-256 parsing.
- `CoreServicesAndModelsTests.cs` — filename sanitization, `ResourceEstimatorService`, `AppSettings.Normalize`/`Clone`, `GpuVideoProcessor.GetTargetDimensions` aspect ratios.
- `MediaRingBufferTests.cs` — leading keyframe enforcement, GOP eviction, static-desktop audio trimming, native slab pool, snapshot isolation, concurrent add+dispose.
- `Mp4ReplayMuxerTests.cs` — MP4 `mvhd` v0/v1 + 64-bit/size-0 boxes, `PatchAudioTrackSelectionFlags`, 188-byte MPEG-TS 90 kHz VFR PTS & 3-track multi-packet CC wrap.
- `ServicesLifecycleAndPlatformTests.cs` — `PortableFfmpegProvider`, `AudioDeviceService`, `DisplayService`, `StartupService`, `MfTrackAudioWaveStream`, `GlobalHotkeyService` STA lifecycle.
- `SettingsAndModelsTests.cs` — atomic settings save/reload/isolation/concurrency, corrupt JSON backup, `ClipItem` parsing/formatting, hotkey label formatting.
- `TrackPcmTimelineBufferTests.cs` — timeline jitter snapping, gap zero-fill, large jump reset, >96k-frame circular wrap-around.

---

## 5. Runtime File & Registry Locations

- **Config:** `%AppData%\ClipThat\settings.json` (corrupt backup: `settings.json.corrupt.bak`)
- **Logs:** `%LocalAppData%\ClipThat\engine.log` (5 MB rotation → `.old`), `%LocalAppData%\ClipThat\ui-errors.log` (2 MB rotation → `.old`)
- **Extracted FFmpeg:** `%LocalAppData%\ClipThat\bin\ffmpeg.exe` (or beside `ClipThat.exe` / `Tools\ffmpeg.exe`)
- **Temp preview/mux:** `%TEMP%\ClipThat\Preview\`, `%TEMP%\ClipThat\Mux\`
- **Default recordings:** `%UserProfile%\Videos\ClipThat\` (temp files: `.clipthat_mux_*.tmp`, `.clipthat_export_*.tmp`)
- **Autostart registry:** `HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\Run\ClipThat` (checked against `...\StartupApproved\Run`)

---

## 6. Known Open Audit Backlog (Not Yet Addressed)

A full codebase audit completed on 2026-11-28 resolved dead code, code duplication, Roslyn `CA` warnings, and test gaps. The following verified bugs and optimization candidates remain intentionally untouched for future work:

### High-Priority Bugs
1. **`MfH264Encoder.cs:60` — Wrong GUID for `CODECAPI_AVEncMPVDefaultBPictureCount`:** Declared `8D390AAC-DC5C-4200-B57F-BE7D96342F2D`; official Windows SDK `codecapi.h` GUID is `8D390AAC-DC5C-4200-B57F-814D04BABAB2`, so disabling B-frames silently fails on MF encoders.
2. **`MfH264Encoder.cs` & `DualAudioCaptureService.cs` — Missing `DeleteAllItems()` on reusable `IMFSample`:** `EnsureReusableOutputSampleLocked` resets buffer length but not sample attributes (`MFSampleExtension_CleanPoint`), allowing P-frames to inherit `CleanPoint = 1` on MFTs that only set it on keyframes.
3. **`GpuVideoProcessor.cs:19` — `OutputSurfacePoolSize = 8` vs up to 9 textures in flight in async MF Tier 1:** `4` in-flight + `3` queued + `1` active + `1` pending can overwrite an in-use NV12 surface; raising pool size to `10–12` (or dropping on exhaustion) prevents GPU races.
4. **`ReplayEngine.cs` — 250 ms self-deadlock in `StopStartupPrimingTimer()`:** Invoked inside `OnStartupPrimingTick` callback using `timer.Dispose(WaitHandle)` + `WaitOne(250)`.
5. **`ReplayEngine.cs` & `AmfH264Encoder.cs` — Concurrency edge cases:** `_activeEncodeTexture` write/clear outside `_frameSubmitLock` in `VideoEncodeWorkerLoop`; `AmfH264Encoder.EncodeFrame` records texture in `_recentInFlightGpuTextures` before `_encSubmitInput` succeeds; `AmfH264Encoder.Dispose` calls `_encDrain` even if `Monitor.TryEnter` fails; missing `QueryInterface(IID_AMFBuffer)` in `AmfH264Encoder`.
6. **`Aktualizuj_ClipThat.bat:56` — Cross-volume `Move-Item` failure:** Moves directories from `$appDir` (e.g. `E:\ClipThat`) to `$env:TEMP` (`C:\...`), which always throws in PowerShell 5.1 across volumes.
7. **`StartupService.cs:192` — Lingering minimized `cmd.exe` window on `.bat` autostart:** `cmd.exe /c start "" /min "{bat}" --minimized` invokes `cmd /K` while `Start_ClipThat.bat` uses `exit /b 0`; also final fallback writes `"dotnet.exe" --minimized`.
8. **`App.xaml.cs:836-848` — `ExitApplicationAsync` dispose order:** Disposes `_globalHotkeyService` before `_mainWindow.ForceClose()`, which can throw `ObjectDisposedException` if `HotkeyBox` is actively capturing, skipping `_replayEngine.StopAsync()`/`Dispose()`.
9. **`MainWindow.xaml.cs` + `ClipsPage.xaml.cs` — Hidden-window preview/watcher resurrection during export:** `Hide()` leaves `IsLoaded == true`, so `RefreshClipsAsync()` re-opens `FileSystemWatcher`, `MediaElement`, `IMFSourceReader`, and `WaveOutEvent` in a hidden window that never auto-closes after export completes.
10. **`ClipEditorService.cs` — FFmpeg cancel temp-file leak & missing `-nostdin`:** Does not await process exit after `Kill()` before `TryDeleteFile`, leaving orphaned `.clipthat_export_*.tmp` / `.wav` files; error detail takes only `recentErrorLines.Last()`.
11. **`AppSettings.cs:61-70` — `.NET 8` path validation:** `Path.GetFullPath` accepts `< > ? * |` on .NET 8 and its return value is discarded (relative paths stay relative).
12. **`DualAudioCaptureService.cs` (`AacMftEncoder`) — Unbounded `TransformStreamChange` loop & unfiltered type `0` in `HandleOutputStreamChange`.**

### Notable Medium Bugs & Optimization Candidates
- **Medium bugs:** `GpuVideoProcessor` slot-0 VPIV VRAM leak on SDR↔HDR format change without resize; `ReplayEngine.Dispose` `_gate.Dispose()` race; CPU fallback path in `HandleCapturedFrame` not routing encoder faults to `_onEncodeError`; `Mp4ReplayMuxer` 90 kHz PTS collision for packets `< 112` ticks apart; `SettingsPage.Detach()` skipping `ReplayEngine.UpdateSettingsAsync` on pending slider save; `App.OnExit` not cleaning up resources on Windows logoff/shutdown; `EnsureFfmpegAsset` missing `DesignTimeBuild` check & `WorkingDirectory`; `MfTrackAudioWaveStream` `CurrentMediaTypeChanged` & failed-seek state; `AudioDeviceService` COM `MMDevice` leak on exception paths; `SettingsService` keeping corrupt JSON on disk & caching defaults on transient `IOException`.
- **Optimizations:** Replace continuous `timeBeginPeriod(1)` + 16 ms DXGI idle timeout with high-res waitable timer + ~100 ms idle timeout; increase `WgcFrameCapture.MinUpdateInterval` on 240/360 Hz displays; reduce 50 Hz audio silence watchdog & idle worker wakeups; lazy-init `WaveOutEvent`/`IMFSourceReader` in `ClipsPage` on first Play; move `SettingsPage` WASAPI enumeration off UI thread; replace `LinkedList<EncodedPacket>` in `MediaRingBuffer` with struct ring array to cut Gen2 object count.
