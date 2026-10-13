StairGuard Native, Windows x64 — C#/.NET 8 + ONNX Runtime CPU.
===============================================================

ONLY camera: 8e6d2d1e-4205-4759-b79e-f9e8c04549cc
Server: http://10.1.60.188:8080
No Python, no camera settings changes, no Macroscop events required.

Installation / first compilation:
1. Windows 10/11 x64. No Python and no manual .NET SDK installation.
2. Unpack this archive into a temporary folder and run Install.cmd.
3. If no .NET SDK 8+ is found, installer downloads a temporary SDK from Microsoft
   (no admin rights required), then removes it after successful compilation.
   Installer downloads the 12.8 MB YOLOv8n ONNX model over HTTPS,
   checks its SHA256 hash, restores NuGet packages from the official nuget.org
   feed using bundled NuGet.Config, and builds an EXE.
4. EXE is installed under %LOCALAPPDATA%\StairGuard\StairGuard.exe.
5. Enter Macroscop login/password, choose the stairs rectangle with the mouse,
   and draw an arrow in the direction of people climbing toward you.
6. Tick "Launch with Windows" and Save.

Daily use:
- The compiled EXE runs with a tray icon. It opens no console.
- Tray menu: settings, LIVE CAMERA VIDEO, pause, open log in Notepad, exit.
- One 640x360 requested MJPEG stream at 2fps, reused by live viewer and AI. Observed on your server: 640x352,
  about 1.68 fps and about 38.2 KiB/s.
- Person detection: YOLOv8n, COCO category person, CPU at max two inference
  threads; line-of-travel estimation uses point tracking between frames.
- A person who moves >=25 px along your chosen arrow inside the ROI after
  at least three samples causes a topmost popup with an enlarged crop.
- The popup closes after 15 seconds; alert cooldown default 30 seconds.

Important limitations:
- Not tested against your Windows machine; compiled runtime build has NOT
  been executed on Windows in the author's environment.
- C# source is supplied, NOT a precompiled or signed EXE.
- This is heuristic tracking, not a guaranteed person identity tracker;
  occlusion and low FPS can produce missed or false events.
- 2 FPS may miss someone moving very quickly. CPU inference cost depends
  on your hardware. Test with a few real walks before relying on alarms.
- Server uses HTTP + MD5-of-password in /mobile URL. This is NOT end-to-end
  confidential. Use only inside a trusted, controlled network.
- Windows DPAPI encrypts the Macroscop MD5 credential under your local Windows
  account. It is still password-equivalent; secure your Windows user profile.
- Files and logs: %LOCALAPPDATA%\StairGuard. Logs omit request URLs and secrets.
- First build requires Internet access to Microsoft (.NET SDK if absent),
  NuGet.org and Hugging Face; offline build is not supported by this archive.
- Self-contained publish requires the SDK ONLY for one-time compilation.
  No .NET runtime/SDK or Python is needed for subsequent autostart.

Model:
 https://huggingface.co/webml/yolov8n/resolve/main/onnx/yolov8n.onnx
 SHA256 190ba5f1e61411a001683e349d6b2cdb0804c0dc67a5e34cd8ff6fd00ee54b4d
License for external model is subject to its upstream terms; inspect
upstream licensing before any distribution or commercial use.

NuGet NU1100 fix (2026-10-13):
- The first installer did not specify a package source. On a machine with no
  configured NuGet feed, restore ended with NU1100 in ~0.1 s.
- This revision includes NuGet.Config and runs explicit `dotnet restore
  --configfile ...` before `dotnet publish --no-restore`.
- If your company network blocks api.nuget.org, the build will still fail.
  Ask for approved package access or build on another Windows machine and
  copy the self-contained published output. The installer cannot fix a firewall.
- Previous downloaded YOLO model is reused. Temporary SDK is reused when
  an earlier build failed. Camera GUID remains unchanged.


UI / VIDEO FIX (v1.1, 2026-10-13):
- The tray option "Смотреть видео с камеры" displays live video, NOT a snapshot.
  It reuses frames from the SINGLE MJPEG camera connection. Closing the viewer
  does not interrupt background person detection.
- MJPEG reading and ONNX detection now run independently with a latest-only
  capacity-1 frame queue; old frames are skipped when the CPU is busy.
- ROI drag uses a custom WinForms double-buffered canvas to eliminate flicker.
- Settings dialog uses responsive layout, a scrollable form, resizable dimensions
  and DPI-aware scaling instead of hardcoded row heights.
- "Открыть журнал" launches Notepad for
  %LOCALAPPDATA%\StairGuard\stairguard.log, creating the log if it does not exist.
- Settings and encrypted credential remain in the same profile directory across
  in-place upgrades. Only the specified camera GUID is used.
- A test snapshot still exists INSIDE the settings screen, distinct from the live
  camera viewer in the tray menu. It is intended only for initial setup.

Update procedure:
1. Close StairGuard completely using Tray -> "Выход".
2. Extract this ZIP into a new folder, run Install.cmd again.
3. All previous settings and encrypted passwords are kept.
4. During an update, the installer downloads tools only when absent.

Build verification: the source and package were checked statically in the authoring
runtime. A Windows build and actual Macroscop/Windows UI test are still required.
