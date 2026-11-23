# CxRdpBridge

`CxRdpBridge` is the native RDP bridge used by CxShell. It wraps FreeRDP behind a small C ABI so the .NET/Avalonia application does not need to P/Invoke large FreeRDP structs directly.

## Build outline

The supported dependency path is vcpkg + FreeRDP 3.x. On Windows:

```powershell
tools\build-rdp-bridge.ps1 -VcpkgRoot D:\develop\vcpkg -OutputDir .buildcheck-rdp
```

Windows builds must use the MSVC toolchain from Visual Studio Build Tools. The PowerShell script rejects MinGW runtime dependencies and copies the required Visual C++ runtime DLLs beside the bridge.

On macOS/Linux:

```bash
VCPKG_ROOT="$HOME/vcpkg" OUTPUT_DIR="./publish" ./tools/build-rdp-bridge.sh
```

The native bridge and FreeRDP/WinPR runtime libraries must be copied next to `CxShell.dll`, for example:

```text
.buildcheck-rdp/
  CxShell.dll
  CxRdpBridge.dll
  freerdp*.dll
  winpr*.dll
```

The application also probes the .NET native asset layout:

```text
runtimes/
  win-x64/native/CxRdpBridge.dll
  osx-arm64/native/libCxRdpBridge.dylib
  osx-x64/native/libCxRdpBridge.dylib
  linux-x64/native/libCxRdpBridge.so
```

FreeRDP runtime libraries must live in the same native directory as the bridge. Linux and macOS builds set rpath to the bridge directory (`$ORIGIN` or `@loader_path`) so those adjacent dependencies can be resolved by the OS loader.

## Bridge API

The bridge exposes a versioned C ABI. Managed callers should inspect `cxrdp_get_api_version` and `cxrdp_get_capabilities` before using optional features. API version 11 exposes:

- Connect with username/password using FreeRDP.
- Publish BGRA framebuffer updates to C#.
- Accept pointer events from Avalonia.
- Accept basic keyboard events from Avalonia.
- Report status and disconnect callbacks.
- Synchronize Unicode text through the RDP clipboard channel.
- Redirect one explicitly selected local folder through the RDPDR drive channel.
- Play remote audio locally through `rdpsnd` when the build contains an audio backend.
- Redirect the local microphone through `audin` and `drdynvc` when supported.
- Apply RDP keyboard-hook policy for local or remote system key combinations.
- Configure per-session client performance flags (`cxrdp_set_disable_wallpaper`, API 9 / `CX_RDP_CAPABILITY_PERFORMANCE_FLAGS`); the bridge folds them into `FreeRDP_PerformanceFlags` with `freerdp_performance_flags_make` before connecting.
- Report the dirty rectangle of each GDI paint batch (`cxrdp_get_frame_dirty_rect`, API 10 / `CX_RDP_CAPABILITY_DIRTY_RECT`). Call it from inside the frame callback: the values belong to that callback only. It reads `gdi->primary->hdc->hwnd->invalid`, so the host can tell which parts of the desktop a batch touched. Returns 1 when the region is unknown. Surface frame-marker END events report the full desktop as the dirty region.
- Advertise legacy surface frame markers on every connection (`FreeRDP_SurfaceCommandsEnabled`, `FreeRDP_FrameMarkerCommandEnabled`, `FreeRDP_SurfaceFrameMarkerEnabled`, `FreeRDP_AltSecFrameMarkerSupport`). These are GCC capability flags only - the transport stays on GDI bitmap updates, so `cx_end_paint` still supplies the framebuffer. When the server responds with `TS_SURFCMD_FRAME_MARKER` BEGIN/END, `cx_surface_frame_marker` suppresses publishing while a frame is open and pushes one complete frame at END; the host then switches to frame-boundary presentation. These bits are REQUIRED: without them the server sends no markers at all and the host can only fall back to debouncing paint batches. A stalled BEGIN is released after 120 ms and that batch is published anyway, so a missing END cannot swallow live pointer updates for a full second.
- **The graphics DVC must be registered by its ADDIN name.** `FreeRDP_DynamicChannelArray` stores addin names (`"disp"`, `"audin"`, `"rdpgfx"`), not wire names. Adding `RDPGFX_DVC_CHANNEL_NAME` (`Microsoft::Windows::RDS::Graphics`) made drdynvc unable to resolve a plugin, and FreeRDP aborted the whole connection with `ERRCONNECT_POST_CONNECT_FAILED` (`0x00020003`) ~10 ms after `PostConnect` - the security fallback then reported the unrelated `ERRCONNECT_CONNECT_TRANSPORT_FAILED`. Use `RDPGFX_CHANNEL_NAME` (`"rdpgfx"`). Verified on a real server: `gfxChannel=rdpgfx present=added addin=dynamic` -> `RDP GFX channel connected.` -> `RDP GFX pipeline bound to GDI.` -> first frame `collectMs=5` (legacy debounce needed ~700 ms).
- **EGFX is refused unless the client addin resolves.** `ensure_gfx_dynamic_channel` probes `freerdp_load_channel_addin_entry("rdpgfx", ...)` (static / `drdynvc` dynamic / dynamic variants) first; when nothing resolves it clears `FreeRDP_SupportGraphicsPipeline`, disables the request and reports `RDP GFX addin unavailable in this FreeRDP build; using legacy bitmap updates.` The connect worker additionally retries the same security profile once without EGFX after any `POST_CONNECT_FAILED` (`RDP post connect rejected the graphics pipeline; retrying with legacy bitmap updates.`), so a bad EGFX setting can never leave the tab dead.
- Negotiate the RDP graphics pipeline (`cxrdp_set_graphics_pipeline`, API 11 / `CX_RDP_CAPABILITY_GFX`). **Experimental and off by default**: when enabled the bridge advertises EGFX (MS-RDPEGFX), planar/progressive codecs and frame acknowledgements. `START_FRAME`/`END_FRAME` give real protocol-level frame boundaries, so FreeRDP's GDI backend calls `EndPaint` once per complete frame. `cx_post_connect` reports `RDP GFX frame mode active.` when `context->gdi->gfx` exists. The earlier black screen came from a missing `gdi_graphics_pipeline_init()` call: EGFX was advertised, but the decoded surfaces had no GDI target, so `EndPaint` never fired. `cx_channel_connected` now binds `RdpgfxClientContext` to the GDI backend (deferring to PostConnect if the DVC opens before `gdi_init`), `cx_post_disconnect` unbinds before `gdi_free`, and `cx_load_channels` ensures the `RDPGFX_DVC_CHANNEL_NAME` dynamic channel plus `drdynvc` are registered. When bound, the bridge emits `RDP GFX frame mode active.` and the host publishes one complete frame per `EndPaint`. **Still opt-in** (`SessionInfo.RdpGraphicsPipelineEnabled` in the RDP edit dialog, or `CXSHELL_RDP_GFX=1`) until confirmed against a real server; legacy surface frame markers remain the safe default.

On Windows, CxShell supplements FreeRDP's keyboard policy with a focus-scoped low-level hook for system-reserved shortcuts. The default vcpkg Linux build disables ALSA and PulseAudio, so Linux packages report audio capabilities as unavailable instead of silently enabling a nonfunctional channel.
