# Changelog

## v2.0.0

| Change | Detail |
|---|---|
| 🔊 Audio meter | Live output level from the default audio device, shown as a percentage, dBFS value, and meter bar |
| 🎶 16-band spectrum | Real-time WASAPI loopback visualization with peak holds and particles; capture and rendering pause while the panel is hidden |
| 🎨 Floating ball styles | Choose built-in styles or import a custom PNG, JPEG, BMP, or animated GIF image |
| ↔️ Resizable panel | Resize the expanded panel by dragging its outer edges or corners; retain the floating ball position and manually selected height |
| 🖌️ Custom panel appearance | Adjust background opacity, RGB background and text colors, and font in Settings; use per-color spectrum, RGB, and Hex pickers, live preview, and reset to defaults |
| 🛠️ Floating ball position fix | Preserve the floating ball's screen position when module changes, including opening or closing Audio meter; keep it inside its monitor's working area when the panel resizes |
| 🖱️ Interaction reliability | A click without dragging keeps the panel open; mouse capture recovers reliably; unrelated settings changes no longer restart the ball animation or retrigger edge snapping |

## v2.0.0-beta.3

| Change | Detail |
|---|---|
| 🔊 Audio meter | Live output level from the default audio device, shown as a percentage, dBFS value, and meter bar |
| 🎶 16-band spectrum | Added real-time WASAPI loopback spectrum, peak holds, and particles; capture and rendering pause while the panel is hidden |
| 🎨 Floating ball styles | Choose built-in styles or import a custom PNG, JPEG, BMP, or animated GIF image |
| ↔️ Resizable panel | Resize the expanded panel by dragging its outer edges or corners; the floating ball and glow stay aligned |
| 🖌️ Custom panel appearance | Adjust panel background opacity, RGB background and text colors, and font from Settings; use the precise spectrum, RGB, and Hex color picker, live preview, and reset button |
| 🛠️ Floating ball position fix | Preserve the floating ball's screen position when enabling or disabling modules, including Audio meter, changes the panel size; keep it inside its monitor's working area |
| 🖱️ Interaction reliability | A click without dragging keeps the panel open, mouse capture is released or recovered reliably, unrelated settings changes no longer restart the ball animation or retrigger edge snapping |

## v2.0.0-beta.1

| Change | Detail |
|---|---|
| 🚨 IP query failure alert | Red toast + tray balloon when the public IP query fails (notifies once on the first failure and again on repeated failures), hinting at a possible network/VPN outage; toggle in Settings |
| 🟢 Network recovery toast | Green toast with the recovered public IP once the query succeeds again; governed by the same toggle |

## v1.4.2

| Change | Detail |
|---|---|
| 🛠️ Fix known issues | Fixed a bug in Public IP/Location |

## v1.4.1

| Change | Detail |
|---|---|
| 🔧 Bug fixes | Improved visual experience |

## v1.4.0

| Change | Detail |
|---|---|
| ??? | Nothing happened (?) |

## v1.3.0

| Change | Detail |
|---|---|
| 🧲 Snap to edge | Floating ball snaps to the nearest screen edge after dragging (toggle in Settings). Note: it snaps to the working area edge — a ball snapped to the bottom rests above the taskbar, not at the physical screen edge |
| 🔧 Pinned panel fix | Dragging with a pinned panel no longer occludes content or sticks the ball |

## v1.2.0

| Change | Detail |
|---|---|
| 🛠️ Known issue fixes | Improved collector lifecycle, shutdown handling, process switching, and diagnostic logging |
| 🖼️ Frame Performance | No longer experimental |

## v1.1.2

| Change | Detail |
|---|---|
| 🎨 New app icon | Minor update — refreshed tray & exe icon |

## v1.1.1

| Change | Detail |
|---|---|
| 📁 Folder-based publish | Switched from single-file exe to folder publish to reduce antivirus false positives |

## v1.1.0

| Change | Detail |
|---|---|
| 🖼️ Frame Performance (Experimental) | Desktop output + PresentMon foreground app mode |
| ⚙️ Frame monitoring settings | Desktop/App mode switch + admin restart prompt |
| 🔧 PresentMon integration | Bundled PresentMon 2.5.1, ETW session management |

## v1.0.0

Initial release.
