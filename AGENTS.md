# ClipThat — AI Architecture & Project State Reference

> **AI Reference File (Read-Only Context)**
> This document is written strictly for AI coding assistants to rapidly understand ClipThat's architecture, file layout, runtime behavior, invariants, and current project state.
> - Do **not** treat this as end-user documentation (user-facing docs live in `README.md` in Polish).
> - Only update this file when architecture, key files, build/test commands, or known issues materially change.

---

## 1. Project Snapshot

- **What it is:** Lightweight Windows 10/11 WPF desktop replay recorder and clip trimmer ("ClipThat") with continuous RAM ring-buffer recording (`1–10 min`), 3-track WASAPI AAC audio (`System`, `Microphone`, `CombinedMix`), global save hotkey, built-in multi-track trim/export editor, and integrated one-click GitHub updater.
- **Target runtime:** `.NET 8` (`net8.0-windows10.0.22621.0`, `SupportedOSPlatformVersion 10.0.19041.0`, `UseWPF`, `UseWindowsForms`, `AllowUnsafeBlocks`, `<RollForward>Major</RollForward>`).
- **NuGet dependencies:** `NAudio 2.4.0`, `Vortice.Direct3D11 3.8.3`, `Vortice.MediaFoundation 3.8.3`, `WPF-UI 4.3.0`.
- **User-facing language:** **Polish (`pl-PL`)** for all UI labels, dialogs, tray tooltips/notifications, error messages, and `README.md`.
- **Current verified performance baseline:** **~0.1% CPU** and **~200 MB RAM** in background tray mode at 1080p 60 FPS / 3 min on AMD Radeon RX 9070 XT (native zero-copy AMD AMF H.264, Peak-Constrained VBR, high-res waitable frame pacing, WPF window closed to tray).
- **Automated tests:** `97 / 97` xUnit tests passing (`ClipThat.Tests`).

---

## 2. Hard Rules & Operational Invariants for AI Agents

1. **Never kill the user's running `ClipThat.exe` unprompted.** When `ClipThat.exe` is running in the tray, `bin\Release\net8.0-windows10.0.22621.0\ClipThat.exe` is locked (`MSB3026`/`MSB3027`). Build and run tests in `-c Debug` unless the user asks to rebuild Release or closes the app.
2. **Batch/PowerShell line endings MUST be Windows `CRLF` (`\r\n`).** Enforced via `.gitattributes` (`*.bat`, `*.cmd`, `*.ps1` → `text eol=crlf`). Unix `LF` breaks `cmd.exe` label lookups (`call :StopExistingInstance`).
3. **Local signing policy:** `ClipThat.csproj` (`SignOutputs` / `SignPublishedOutputs`) only signs binaries if a valid `CN=ClipThatDev` or `CN=ClipThat Local` certificate already exists in `Cert:\CurrentUser\My`. Never auto-create or inject certificates into `Cert:\CurrentUser\Root` (triggers blocking Windows Security Warning dialogs on end-user PCs).
4. **External tools (`Assets\ffmpeg.exe`):** Gitignored. Auto-downloaded and SHA-256 verified from `gyan.dev` during build (`EnsureFfmpegAsset` target in `ClipThat.csproj`, atomic temp move, skipped on `DesignTimeBuild`). Regular builds copy `ffmpeg.exe` beside output; `PublishSingleFile=true` embeds it as `ClipThat.Assets.ffmpeg.exe` (extracted by `PortableFfmpegProvider`).
5. **Single-instance & shutdown:** `App.xaml` sets `ShutdownMode="OnExplicitShutdown"`. Named kernel objects:
   - Mutex: `Local\ClipThat_SingleInstance_Mutex`
   - Events: `Local\ClipThat_ShowWindow_Event`, `Local\ClipThat_Exit_Event`
   - CLI args: `--minimized` (start in tray; second instance does not wake window), `--exit` (gracefully close running instance).
   - `App.SessionEnding` and `App.OnExit` perform synchronous shutdown cleanup so Windows logoff/shutdown never leaves leaked handles or temp files.
6. **Build & test commands:**
   - Debug build (safe while tray app runs): `dotnet build ClipThat.csproj -c Debug --nologo`
   - Unit tests: `dotnet test ClipThat.Tests/ClipThat.Tests.csproj -c Debug --nologo`
   - User launch / Release rebuild: `Start_ClipThat.bat` (or `Start_ClipThat.bat --rebuild` to gracefully stop running instance, rebuild `Release`, and restart).

---

## 3. End-to-End Architecture & Data Flows

### 3.1 Application, Tray & Updater Lifecycle (`App.xaml.cs`, `UI/MainWindow.xaml.*`, `Services/AppUpdateService.cs`)
- `App.OnStartup` handles `--exit`, acquires the single-instance mutex, initializes `SettingsService`, `DisplayService`, `AudioDeviceService`, `ResourceEstimatorService`, `StartupService`, `ClipEditorService`, `ReplayEngine`, WinForms `NotifyIcon` tray menu, and `GlobalHotkeyService`.
- `MainWindow` displays `Assets/app.ico` (`pack://application:,,,/Assets/app.ico`) in `<ui:TitleBar.Icon>` beside `"ClipThat"`, navigation tabs (`Ustawienia`, `Nagrania`), and a pinned footer action `"Aktualizacja"` (`ArrowDownload24`) in `<ui:NavigationView.FooterMenuItems>`.
- Clicking **Aktualizacja** runs `AppUpdateService.CheckForUpdatesAsync()` against GitHub (`commits/main`):
  - Git repos compare `HEAD` (and `merge-base --is-ancestor` so local ahead branches show up-to-date) and guard against uncommitted edits (`git status --porcelain`).
  - ZIP installs compare `.clipthat_version` SHA or tracked source mtime (2 min tolerance).
  - If up to date → shows dark Fluent `ConfirmDialog` (`ConfirmDialogTone.Success`); if updates exist → prompts user (`ConfirmDialogTone.Accent`), launches a self-cleaning UTF-8 BOM PowerShell updater script in `%TEMP%` with **same-volume** backup (`.clipthat_update_backup_*`) preserving `Assets\ffmpeg.exe`, exits gracefully via `App.RequestExitForUpdateAsync()`, rebuilds `Release`, and relaunches via `Start_ClipThat.bat --rebuild`.
- Closing `MainWindow` **destroys** the WPF window (instead of `Hide()`) whenever `!_pageProvider.IsExporting`, then schedules a delayed non-blocking `GC.Collect(2, GCCollectionMode.Optimized, blocking: false)` after 1.5 s to reclaim ~60–110 MB of WPF visual/Mica memory while gaming. If hidden during an active export, `ClipsPage.ExportCompleted` auto-closes the hidden window as soon as export finishes.
- `ExitApplicationAsync` closes `MainWindow` *before* disposing `GlobalHotkeyService` and tears down `ReplayEngine` safely even on `HotkeyBox` capture.

### 3.2 Video Capture & GPU Processing (`Engine/Capture`, `Engine/Video`)
```
Monitor (HMONITOR)
  ├─ Primary:  DxgiFrameCapture (DXGI Desktop Duplication + cached HLSL DxgiCursorCompositor, high-res waitable timer pacing, 100 ms idle timeout)
  └─ Fallback: WgcFrameCapture  (Windows.Graphics.Capture; paced MinUpdateInterval; used for rotated displays, DXGI access denied, or recovery)
       │
       ▼ (shared ID3D11Device bound to target monitor's exact DXGI adapter via MonitorDeviceFactory)
  GpuVideoProcessor
    ├─ Hardware path: ID3D11VideoProcessor BGRA/HDR -> scaled even-dimension NV12 texture pool (OutputSurfacePoolSize = 12, LRU VPIV cache, SDR/HDR format tracking, runtime CPU fallback)
    └─ Fallback path: GPU staging readback (non-stalling spin/yield) -> CPU NV12 buffer (for Tier 2/3 encoders)
```
- `ReplayEngine` decouples capture callbacks from encoding via a 1-slot latest-frame handoff (`VideoEncodeWorkerLoop`) and protects textures currently owned by the encoder via `IVideoEncoder.IsTextureInUse(texture)` + synchronized `_activeEncodeTexture`.

### 3.3 Video Encoding (`Engine/Encoding`, `Engine/ReplayEngine.cs`)
- Unified contract: `IVideoEncoder`.
- **Encoder selection & fallback order (`ReplayEngine.CreateEncoder`):**
  1. **AMD GPUs (Tier 1):** `AmfH264Encoder` tried first when `AmfH264Encoder.ShouldTryForAdapter(adapterName)` detects AMD/Radeon + loadable `amfrt64.dll`. Uses native C ABI zero-copy `AMFContext::InitDX11` + `CreateSurfaceFromDX11Native` directly on `GpuVideoProcessor` NV12 textures and `QueryInterface(IID_AMFBuffer)` for output readback. *(Bypasses a broken AMD Adrenalin RDNA4 `AMDh264Encoder` Media Foundation MFT that stalls after 1 frame.)*
  2. **NVIDIA NVENC / Intel QSV / AMD MFT fallback:** `MfH264Encoder` 3-tier negotiation:
     - `HardwareGpuZeroCopy` (async MFT + `IMFDXGIDeviceManager` D3D11 zero-copy NV12)
     - `HardwareCpuBuffer` (hardware MFT with CPU NV12 buffer)
     - `SoftwareFallback` (Microsoft H.264 Software MFT with `CODECAPI_AVEncCommonQualityVsSpeed = 33`)
- **Encoding profile:** Peak-Constrained VBR (fallback: Unconstrained VBR → CBR), 0 B-frames (`CODECAPI_AVEncMPVDefaultBPictureCount` = `8D390AAC-DC5C-4200-B57F-814D04BABAB2`), 2-second GOP (`fps * 2`), BT.709 studio range (16–235), target bitrates from `ResourceEstimatorService` (`1080p60`: **12.0 Mbps target / 16.2 Mbps peak**, `1080p30`: 8.0 Mbps, `720p60`: 6.5 Mbps, `720p30`: 4.5 Mbps; `PeakBitrateMultiplier = 1.35`).
- **Watchdog & recovery:** `ReplayEngine` runs a 1.5 s keyframe watchdog (with idle deferrals on static desktops) that downgrades encoder tier on stall, lock-safe startup priming timer, static keep-alive timer, and automatic pipeline recovery on display/session interruptions.

### 3.4 Multi-Track Audio Capture (`Engine/Audio/DualAudioCaptureService.cs`)
```
WASAPI System Loopback (20 ms buffer + 40 ms silence watchdog) ─┐
                                                                ├─► AudioPcmConverter (NAudio 5.1/7.1 downmix + resample -> 48 kHz 16-bit Stereo PCM)
WASAPI Microphone Capture (optional; "none" disables)         ──┘      │
                                                                       ├─► AacMftEncoder (128 kbps raw AAC) -> MediaTrackType.SystemAudio
                                                                       ├─► AacMftEncoder (128 kbps raw AAC) -> MediaTrackType.MicrophoneAudio
                                                                       └─► CombinedAudioMixPipeline (TrackPcmTimelineBuffer 2 s ring + MixSamplesSoftClip)
                                                                             └─► AacMftEncoder (128 kbps raw AAC) -> MediaTrackType.CombinedMixAudio
```
- Implements `IMMNotificationClient` to hot-swap default playback/capture devices automatically when Windows endpoints change, with bounded MFT stream-change negotiation and consecutive audio error escalation.

### 3.5 Circular RAM Buffer (`Engine/Buffer/MediaRingBuffer.cs`)
- Thread-safe GOP-aligned circular buffer (`1–10 min` window, enforces leading video keyframe and a `1024 MB` minimum free physical RAM guard).
- Packet payloads are allocated in **unmanaged native memory slabs** (`NativeMemory`, 2 MiB pooled slabs + dedicated slabs for >512 KB frames) to avoid managed GC heap pressure.
- `SnapshotPackets()` retains zero-copy ref-counted packet slabs so saving an MP4 does not copy buffers or stall live recording.

### 3.6 Replay Saving / MP4 Muxing (`Engine/Muxing/Mp4ReplayMuxer.cs`)
1. **Primary path:** Native Media Foundation `IMFSinkWriter` remux (stream copy, no re-encode) into `.clipthat_mux_<guid>.tmp`, atomically renamed to `ssmmHHddMMyyyy.mp4`.
2. **Track selection patch:** `PatchAudioTrackSelectionFlags` edits `moov → trak → tkhd` (`alternate_group = 1` + enabled flags) so players (Windows Media Player, VLC, Discord, browsers) default to the last audio track (`CombinedMixAudio` when 3 tracks exist), while editors can still access all tracks.
3. **Fallback path:** Writes a strictly monotonic 90 kHz PTS VFR MPEG-TS intermediate (`WriteMpegTsIntermediate`) and remuxes via `PortableFfmpegProvider` (`ffmpeg.exe -c copy`).

### 3.7 Clips Browser, Preview & Trim Export (`UI/Pages/ClipsPage.*`, `Services/ClipEditorService.cs`, `Services/MfTrackAudioWaveStream.cs`)
- Watches output folder with `FileSystemWatcher` (suspended whenever unloaded or hidden in tray) and reads metadata via `Mp4ReplayMuxer.TryReadMp4Metadata` → Windows Shell `StorageFile` → `ffprobe` fallback (with negative caching).
- Preview plays muted video in WPF `MediaElement` synced to an NAudio `WaveOutEvent` + `MixingSampleProvider` + per-track `VolumeSampleProvider` initialized **lazily on first Play**.
  - Fast audio preview path: `MfTrackAudioWaveStream` streams decoded PCM directly from MP4 audio tracks 0 and 1 via `IMFSourceReader` (zero temp files, fast pre-target seek skip, thread-safe format/length updates).
  - Fallback path: extracts temporary WAV tracks to `%TEMP%\ClipThat\Preview\` via FFmpeg (`-nostdin`, deterministic cleanup on cancel).
- Export trims via FFmpeg (`-nostdin`, `-ss` before `-i`, `-t` after `-i`) using `-c:v copy` (or optional hardware/software re-encode `h264_nvenc` / `h264_amf` / `h264_qsv` / `libx264`), supporting `MixSingleTrackForDiscord` (1 mixed AAC track) and `KeepSeparateAudioTracks` (3 tracks: System, Mic, Mix).

---

## 4. Repository File Map

### Root & Scripts
| File | Role |
|---|---|
| `ClipThat.csproj` | Main WPF .NET 8 project, `Assets\app.ico` WPF Resource, NuGet refs, `EnsureFfmpegAsset` & env-var safe `SignOutputs` targets |
| `App.xaml` / `App.xaml.cs` | Entry point, single-instance IPC, tray icon/menu, global hotkey hook, coordinated save/toggle/exit actions, OS shutdown cleanup, exception logging, Mica accent/theme |
| `Start_ClipThat.bat` | End-user launcher (Windows `CRLF`; auto-installs .NET 8 SDK via winget if missing, incremental Release rebuild check, `--rebuild`, `--minimized`, `--exit`) |
| `.gitattributes` | Forces `CRLF` on `*.bat`, `*.cmd`, `*.ps1` for both `git clone` and GitHub ZIP archives |
| `README.md` | End-user Polish setup & in-app update guide |
| `Assets/app.ico` | Application icon (exe icon, tray icon, and TitleBar icon) |
| `Assets/ffmpeg.exe` | Gitignored; auto-downloaded FFmpeg binary |

### `Engine/` — Capture, Processing, Encoding, Audio, Buffer, Muxing
| File | Role |
|---|---|
| `Engine/IReplayEngine.cs` | Public replay engine contract & `EngineStatusLevel` enum |
| `Engine/ReplayEngine.cs` | Pipeline coordinator, capture/encoder orchestration, watchdog, tier downgrade, session recovery, replay save |
| `Engine/Capture/IFrameCaptureSource.cs` | Capture backend interface + `MonitorDeviceFactory` (monitor-to-DXGI-adapter matching & D3D11 device creation) |
| `Engine/Capture/DxgiFrameCapture.cs` | Primary DXGI Desktop Duplication capture loop with high-res waitable timer pacing & duplication recovery |
| `Engine/Capture/DxgiCursorCompositor.cs` | Pure-GPU cached-bytecode HLSL D3D11 cursor compositor (Color, MaskedColor, Monochrome; SDR & HDR PQ/scRGB) |
| `Engine/Capture/WgcFrameCapture.cs` | Fallback `Windows.Graphics.Capture` free-threaded capture backend with paced `MinUpdateInterval` |
| `Engine/Video/GpuVideoProcessor.cs` | D3D11 `ID3D11VideoProcessor` BGRA/HDR → NV12 scaling, 12-surface NV12 pool, LRU VPIV cache, runtime CPU staging fallback |
| `Engine/Encoding/IVideoEncoder.cs` | Common H.264 encoder contract |
| `Engine/Encoding/AmfH264Encoder.cs` | Native AMD AMF (`amfrt64.dll`) zero-copy D3D11 H.264 encoder (`QueryInterface(IID_AMFBuffer)`, unsafe C ABI vtable interop) |
| `Engine/Encoding/MfH264Encoder.cs` | 3-tier Media Foundation H.264 encoder (NVENC/QSV/AMF MFT zero-copy, HW CPU-buffer, MS Software MFT) |
| `Engine/Encoding/AnnexBHelper.cs` | Shared H.264 Annex-B IDR detection, zero-alloc `ContainsSpsPps`, & SPS/PPS extraction |
| `Engine/Audio/DualAudioCaptureService.cs` | WASAPI system loopback + mic capture, NAudio PCM conversion/downmix, 3-track timeline mixer + soft-clip, AAC MFT encoder, hot-swap |
| `Engine/Buffer/MediaRingBuffer.cs` | Thread-safe GOP-aligned circular buffer backed by unmanaged `NativeMemory` slab pool + `EncodedPacket` / `EncodedPacketSnapshot` |
| `Engine/Muxing/Mp4ReplayMuxer.cs` | Native MF `IMFSinkWriter` MP4 remuxer, `tkhd` audio track flag patcher, MP4 box metadata reader, monotonic 90 kHz MPEG-TS + FFmpeg fallback |
| `Engine/Interop/CaptureInterop.cs` | Win32/WinRT interop (`HMONITOR`, `GraphicsCaptureItem`, `IDirect3DDevice`, `IDirect3DDxgiInterfaceAccess`) |
| `Engine/Interop/EngineDisposeHelper.cs` | Shared best-effort `SafeDispose` helper across `Engine/` |
| `Engine/Interop/MediaFoundationMftHelper.cs` | Shared MF MFT activate unwrapping, `OutputDataBuffer` COM alias cleanup, refcount & stream-ID helpers |
| `Engine/Diagnostics/EngineLogger.cs` | Thread-safe rotating file logger with cached paths (`%LocalAppData%\ClipThat\engine.log`, 5 MB cap) |

### `Models/` & `Services/`
| File | Role |
|---|---|
| `Models/AppSettings.cs` | User settings POCO, `Normalize()` clamping + Windows path validation/resolution, `Clone()` |
| `Models/AudioDeviceInfo.cs` | Audio endpoint view model (`DeviceId`, `Name`, `IsDefault`, `IsDisabled`) |
| `Models/ClipItem.cs` | Recorded clip view model, `ssmmHHddMMyyyy` filename date parser, Polish size/duration/track formatters |
| `Models/DisplayDeviceInfo.cs` | Physical display bounds, index, refresh rate model |
| `Models/ResolutionPreset.cs` | Enum (`P240=240`, `P360=360`, `P480=480`, `P720=720`, `P1080=1080`) |
| `Models/ResourceEstimate.cs` | RAM/disk/bitrate estimate record |
| `Services/AppUpdateService.cs` | GitHub `commits/main` version check (Git + ZIP `.clipthat_version`/mtime) and detached self-cleaning same-volume PowerShell updater |
| `Services/SettingsService.cs` | Atomic JSON persistence (`%AppData%\ClipThat\settings.json`), corrupt backup & auto-repair, transient-error-safe clone cache |
| `Services/ResourceEstimatorService.cs` | Target & peak VBR bitrates, MiB disk & RAM buffer estimator |
| `Services/DisplayService.cs` | Enumerates physical monitors via WinForms + Win32 `EnumDisplaySettingsW`/`EnumDisplayDevicesW` |
| `Services/AudioDeviceService.cs` | WASAPI playback/capture endpoint enumeration (single-enumerator combined fetch), leak-free resolution, and `IMMNotificationClient` change bridge |
| `Services/GlobalHotkeyService.cs` | Win32 `RegisterHotKey` message-only `HwndSource` service + Polish key label formatter |
| `Services/StartupService.cs` | Windows autostart management (`HKCU\...\Run\ClipThat` + `StartupApproved` check; non-lingering inner `cmd.exe /c`) |
| `Services/PortableFfmpegProvider.cs` | Adjacent/embedded `ffmpeg.exe` resolution & atomic extraction to `%LocalAppData%\ClipThat\bin\ffmpeg.exe` |
| `Services/ClipEditorService.cs` | Clip folder scanning + negative metadata cache, single-pass SHA-256 FFmpeg download, HW encoder probe, preview WAV extraction, trim/mix export |
| `Services/MfTrackAudioWaveStream.cs` | Zero-disk-I/O NAudio `WaveStream` decoding individual MP4 audio tracks directly via MF `IMFSourceReader` |

### `UI/`
| File | Role |
|---|---|
| `UI/MainWindow.xaml` / `.cs` | `FluentWindow` navigation host (`SettingsPage`, `ClipsPage`), TitleBar `app.ico` icon, bottom-rail `"Aktualizacja"` updater flow, Mica active/inactive brushes, close-to-tray + post-export auto-close lifecycle |
| `UI/UiBrushHelper.cs` | Shared `CreateFrozenBrush` helper |
| `UI/SmoothScrollBehavior.cs` | Global `ScrollViewer` class handler (`ScrollViewer`/`PassiveScrollViewer`/`DynamicScrollViewer`) for frame-rate-independent inertial wheel scrolling with zero idle CPU |
| `UI/Pages/SettingsPage.xaml` / `.cs` | Polish settings UI: monitor layout, buffer slider + live estimator, resolution/FPS, async WASAPI audio selectors, hotkey box, folder picker, autostart |
| `UI/Pages/ClipsPage.xaml` / `.cs` | Virtualized clips browser, collapsible sidebar, muted video preview + lazy NAudio dual-track mixer, trim timeline, export controls & progress |
| `UI/Controls/MonitorLayoutControl.xaml` / `.cs` | Proportional multi-monitor canvas with on-screen display identification overlay badges |
| `UI/Controls/RangeTrimSlider.xaml` / `.cs` | Unified playback + dual-handle range trim bar (`MinimumGapSeconds = 0.5`, lightweight playhead-only updates) |
| `UI/Controls/HotkeyBox.xaml` / `.cs` | Interactive global hotkey recorder control |
| `UI/Dialogs/ConfirmDialog.xaml` / `.cs` | Styled dark Fluent modal supporting `Destructive`, `Accent`, and `Success` tones plus single-button info mode |

### `ClipThat.Tests/` (97 tests total)
- `AudioAndCodecPipelineTests.cs` — 5.1/7.1 downmix, soft-clip mixer, Annex-B IDR/SPS/PPS/`ContainsSpsPps` parsing, `AmfVariant` 24-byte x64 ABI & AMD adapter filter.
- `ClipEditorServiceTests.cs` — FFmpeg argument ordering/`-nostdin`/codecs/filters, Polish locale decimal invariance, `ScanClipsAsync` sorting & cache invalidation, export validation, stderr time & SHA-256 parsing.
- `CoreServicesAndModelsTests.cs` — filename sanitization & 160-char length clamping, `ResourceEstimatorService`, `AppSettings.Normalize` (Windows path char rejection, relative-to-full path resolution, hotkey masks)/`Clone`, `GpuVideoProcessor.GetTargetDimensions`.
- `MediaRingBufferTests.cs` — leading keyframe enforcement, GOP eviction, static-desktop audio trimming, native slab pool, snapshot isolation, concurrent add+dispose.
- `Mp4ReplayMuxerTests.cs` — MP4 `mvhd` v0/v1 + 64-bit/size-0 boxes, `PatchAudioTrackSelectionFlags`, 188-byte MPEG-TS 90 kHz VFR PTS, strict `<112`-tick monotonicity, & 3-track multi-packet CC wrap.
- `ServicesLifecycleAndPlatformTests.cs` — `PortableFfmpegProvider`, `AudioDeviceService`, `DisplayService`, `StartupService` (including non-lingering `.bat` command builder), `AppUpdateService` (GitHub JSON parse, SHA/mtime evaluation, safe same-volume PowerShell updater generation), `MfTrackAudioWaveStream`, `GlobalHotkeyService` STA lifecycle.
- `SettingsAndModelsTests.cs` — atomic settings save/reload/isolation/concurrency, corrupt JSON backup + disk auto-repair, `ClipItem` parsing/missing-file guard/pluralized track badges, hotkey label formatting.
- `TrackPcmTimelineBufferTests.cs` — timeline jitter snapping, gap zero-fill, large jump reset, >96k-frame circular wrap-around.

---

## 5. Runtime File & Registry Locations

- **Config:** `%AppData%\ClipThat\settings.json` (corrupt backup: `settings.json.corrupt.bak`)
- **ZIP version marker:** `<AppRoot>\.clipthat_version` (written after in-app ZIP update)
- **Logs:** `%LocalAppData%\ClipThat\engine.log` (5 MB rotation → `.old`), `%LocalAppData%\ClipThat\ui-errors.log` (2 MB rotation → `.old`)
- **Extracted FFmpeg:** `%LocalAppData%\ClipThat\bin\ffmpeg.exe` (or beside `ClipThat.exe` / `Tools\ffmpeg.exe`)
- **Temp preview/mux/updater:** `%TEMP%\ClipThat\Preview\`, `%TEMP%\ClipThat\Mux\`, `%TEMP%\ClipThat-Updater-*.ps1` (self-deleted)
- **Default recordings:** `%UserProfile%\Videos\ClipThat\` (temp files: `.clipthat_mux_*.tmp`, `.clipthat_export_*.tmp`)
- **Autostart registry:** `HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\Run\ClipThat` (checked against `...\StartupApproved\Run`)

---

## 6. Known Open Audit Backlog

All high- and medium-priority bugs, resource leaks, and high-impact optimizations identified during the 2026-11-28 full codebase audit have been resolved and verified (96 / 96 passing unit tests, 0 build warnings/errors).
