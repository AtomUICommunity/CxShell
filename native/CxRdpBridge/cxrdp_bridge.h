#pragma once

#include <stdint.h>

#if defined(_WIN32)
#if defined(CX_RDP_BRIDGE_EXPORTS)
#define CX_RDP_API __declspec(dllexport)
#else
#define CX_RDP_API __declspec(dllimport)
#endif
#else
#define CX_RDP_API __attribute__((visibility("default")))
#endif

#ifdef __cplusplus
extern "C" {
#endif

typedef void (*cxrdp_frame_callback)(void* user_data, int width, int height, int stride, const uint8_t* bgra_pixels);
typedef void (*cxrdp_status_callback)(void* user_data, const char* message);
typedef void (*cxrdp_disconnect_callback)(void* user_data);
typedef void (*cxrdp_clipboard_text_callback)(void* user_data, const char* text);
typedef void (*cxrdp_clipboard_image_callback)(void* user_data, uint32_t image_format, const uint8_t* data, uint32_t length);
typedef void (*cxrdp_clipboard_files_callback)(void* user_data, const char* file_paths_newline_separated);
typedef void (*cxrdp_pointer_callback)(
    void* user_data,
    int kind,
    int width,
    int height,
    int hot_x,
    int hot_y,
    const uint8_t* bgra_pixels);

#define CX_RDP_BRIDGE_API_VERSION 15u
#define CX_RDP_CAPABILITY_CLIPBOARD 0x00000001u
#define CX_RDP_CAPABILITY_DRIVE_REDIRECTION 0x00000002u
#define CX_RDP_CAPABILITY_AUDIO_PLAYBACK 0x00000004u
#define CX_RDP_CAPABILITY_MICROPHONE 0x00000008u
#define CX_RDP_CAPABILITY_KEYBOARD_HOOK 0x00000010u
/* The bridge keeps an idle session alive with TCP keepalive probes and resumes
   a dropped transport through the RDP auto reconnect cookie (ARC). */
#define CX_RDP_CAPABILITY_AUTO_RECONNECT 0x00000020u
/* The bridge can resize the remote desktop and change its advertised DPI in
   place through the RDP display-control channel, without a reconnect. */
#define CX_RDP_CAPABILITY_RUNTIME_RESIZE 0x00000040u
/* The bridge starts with the negotiated security layer, remembers the profile
   that worked for a host, and exposes per-session clipboard/profile options. */
#define CX_RDP_CAPABILITY_FAST_CONNECT 0x00000080u
/* The bridge understands RDP surface frame markers: it buffers the GDI surface
   while a frame is open and publishes the complete frame on its END marker. */
#define CX_RDP_CAPABILITY_FRAME_MARKER 0x00000100u
/* The bridge exposes RDP client performance flags (currently the desktop
   wallpaper flag) as per-session options and sends them on connect. */
#define CX_RDP_CAPABILITY_PERFORMANCE_FLAGS 0x00000200u
/* The bridge reports the dirty rectangle of every GDI paint batch, so the host
   can wait until the server has actually painted the whole desktop instead of
   guessing a frame boundary from idle time alone. */
#define CX_RDP_CAPABILITY_DIRTY_RECT 0x00000400u
/* The bridge negotiates the RDP graphics pipeline (MS-RDPEGFX): the server
   groups surface commands between START_FRAME/END_FRAME and FreeRDP's GDI
   backend calls EndPaint exactly once per complete frame, just like MSTSC. */
#define CX_RDP_CAPABILITY_GFX 0x00000800u
/* The bridge can synchronize CapsLock/NumLock/ScrollLock state to the remote session. */
#define CX_RDP_CAPABILITY_SYNC_LOCK_KEYS 0x00001000u
/* The bridge forwards remote mouse pointer bitmaps and hotspots to the host. */
#define CX_RDP_CAPABILITY_POINTER_SHAPE 0x00002000u
#define CX_RDP_CAPABILITY_CLIPBOARD_IMAGE 0x00004000u
#define CX_RDP_CAPABILITY_CLIPBOARD_FILES 0x00008000u
#define CX_RDP_CAPABILITY_CLIPBOARD_RICH_TEXT 0x00010000u
#define CX_RDP_CAPABILITY_CLIPBOARD_LOCK 0x00020000u
#define CX_RDP_CAPABILITY_MULTI_MONITOR 0x00040000u
#define CX_RDP_CAPABILITY_GFX_H264 0x00080000u

typedef struct CxRdpMonitorRect
{
    int32_t left;
    int32_t top;
    uint32_t width;
    uint32_t height;
    uint32_t desktop_scale_factor;
    uint32_t device_scale_factor;
    int32_t is_primary;
} CxRdpMonitorRect;

#define CX_RDP_CLIPBOARD_IMAGE_PNG 1u
#define CX_RDP_CLIPBOARD_IMAGE_DIB 2u

#define CX_RDP_POINTER_DEFAULT 0
#define CX_RDP_POINTER_NULL 1
#define CX_RDP_POINTER_CUSTOM 2

#define CX_RDP_AUDIO_PLAY_LOCAL 0
#define CX_RDP_AUDIO_PLAY_REMOTE 1
#define CX_RDP_AUDIO_DO_NOT_PLAY 2

CX_RDP_API uint32_t cxrdp_get_api_version(void);
CX_RDP_API uint32_t cxrdp_get_capabilities(void);

CX_RDP_API void* cxrdp_create(void);
CX_RDP_API void cxrdp_destroy(void* handle);
CX_RDP_API void cxrdp_set_callbacks(
    void* handle,
    cxrdp_frame_callback frame_callback,
    cxrdp_status_callback status_callback,
    cxrdp_disconnect_callback disconnect_callback,
    void* user_data);
CX_RDP_API int cxrdp_connect(
    void* handle,
    const char* host,
    int port,
    const char* username,
    const char* password,
    int width,
    int height,
    int color_depth);
CX_RDP_API void cxrdp_disconnect(void* handle);
CX_RDP_API void cxrdp_send_pointer(void* handle, uint16_t flags, uint16_t x, uint16_t y);
CX_RDP_API void cxrdp_send_key(void* handle, uint32_t key, int down);
CX_RDP_API void cxrdp_send_unicode_key(void* handle, uint16_t code, int down);
CX_RDP_API void cxrdp_sync_lock_keys(void* handle, uint32_t flags);
CX_RDP_API void cxrdp_set_clipboard_callback(void* handle, cxrdp_clipboard_text_callback clipboard_text_callback);
CX_RDP_API void cxrdp_set_clipboard_image_callback(void* handle, cxrdp_clipboard_image_callback clipboard_image_callback);
CX_RDP_API void cxrdp_set_clipboard_files_callback(void* handle, cxrdp_clipboard_files_callback clipboard_files_callback);
CX_RDP_API void cxrdp_set_pointer_callback(void* handle, cxrdp_pointer_callback pointer_callback);
CX_RDP_API void cxrdp_set_clipboard_text(void* handle, const char* text);
CX_RDP_API void cxrdp_set_clipboard_rich_text(
    void* handle,
    const char* plain_text,
    const uint8_t* html_bytes,
    uint32_t html_length,
    const uint8_t* rtf_bytes,
    uint32_t rtf_length);
CX_RDP_API void cxrdp_set_clipboard_image(
    void* handle,
    uint32_t image_format,
    const uint8_t* data,
    uint32_t length);
CX_RDP_API void cxrdp_set_clipboard_files(void* handle, const char* file_paths_newline_separated);
CX_RDP_API int cxrdp_set_clipboard_lock_enabled(void* handle, int enabled);
CX_RDP_API int cxrdp_set_drive_redirection(
    void* handle,
    int enabled,
    const char* drive_name,
    const char* local_path);
CX_RDP_API int cxrdp_set_audio_redirection(
    void* handle,
    int playback_mode,
    int microphone_enabled);
CX_RDP_API int cxrdp_set_keyboard_options(
    void* handle,
    int apply_key_combinations_remotely);

/* Set the advertised RDP DPI scale percentage before connecting.
   0 keeps the remote session's own scaling; 100/125/150/175/200 are typical. */
CX_RDP_API int cxrdp_set_display_scale(void* handle, int scalePercent);

/* Queue a resolution + DPI change on a live session. Returns 0 when accepted.
   The worker applies it through the display-control channel; the host must
   keep framebuffer dimensions from the frame callback. */
CX_RDP_API int cxrdp_update_display(void* handle, int width, int height, int scalePercent);
CX_RDP_API int cxrdp_resize_monitors(void* handle, const CxRdpMonitorRect* monitors, int count);

/* Prefer the security profile that succeeded on a previous connection to this
   host: "negotiate", "nla", "tls" or "rdp". An empty/NULL name restores
   automatic negotiation. Call before cxrdp_connect(). */
CX_RDP_API int cxrdp_set_security_profile(void* handle, const char* profileName);

/* Enable or disable the RDP clipboard (cliprdr) channel for this session.
   Call before cxrdp_connect(). Default: enabled. */
CX_RDP_API int cxrdp_set_clipboard_channel(void* handle, int enabled);

/* Enable server frame-marker based presentation before connecting.
   With markers the bridge only publishes a framebuffer at a frame END marker;
   without them it publishes after every paint batch. Default: enabled.
   Requires CX_RDP_CAPABILITY_FRAME_MARKER (API 8). */
CX_RDP_API int cxrdp_set_frame_marker_enabled(void* handle, int enabled);

/* Ask the server to hide the remote desktop wallpaper (RDP performance flag
   PERF_DISABLE_WALLPAPER). Call before cxrdp_connect(). Default: enabled.
   Requires CX_RDP_CAPABILITY_PERFORMANCE_FLAGS (API 9). */
CX_RDP_API int cxrdp_set_disable_wallpaper(void* handle, int enabled);

/* Returns the dirty rectangle of the most recent paint batch in desktop
   coordinates. Returns 0 and fills the out parameters when valid, or 1 when
   the region is unknown (older servers / non-GDI updates). Must be called
   from inside the frame callback; the values belong to that callback only.
   Requires CX_RDP_CAPABILITY_DIRTY_RECT (API 10). */
CX_RDP_API int cxrdp_get_frame_dirty_rect(
    void* handle,
    int* x,
    int* y,
    int* width,
    int* height);

/* Enable the RDP graphics pipeline (EGFX) for this session. Call before
   cxrdp_connect(). When negotiated, frame boundaries come from the protocol
   instead of host-side heuristics. Default: enabled.
   Requires CX_RDP_CAPABILITY_GFX (API 11). */
CX_RDP_API int cxrdp_set_graphics_pipeline(void* handle, int enabled);
CX_RDP_API int cxrdp_set_h264_enabled(void* handle, int enabled);

CX_RDP_API const char* cxrdp_get_last_error(void* handle);

#ifdef __cplusplus
}
#endif
