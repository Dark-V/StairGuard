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


v1.5 — independent video & analysis + presets
===============================================
Camera: exclusively 8e6d2d1e-4205-4759-b79e-f9e8c04549cc.
In Settings, the "Режим видеопотока" selector replaces manual width,
height, FPS and JPEG quality entries:
- Экономичный: 640x352 @ 5 FPS, JPEG quality 60 (requested)
- Баланс:      640x352 @10 FPS, JPEG quality 75 (requested)
- Качество:    1280x720@10 FPS, JPEG quality 85 (requested)

Separate "Частота анализа YOLO": 0.5 / 1 / 2 FPS. Defaults to 1.
Optionally enable the lightweight motion prefilter (on by default).
The inference queue stores only one latest frame; no processing backlog.
When video preview is hidden, incoming JPEGs aren't decoded for display;
analysis decompresses only sampled frames, and YOLO runs only on motion.
Motion gating works only within the selected ROI and uses a lightweight
96x64 grayscale comparison with compensation for uniform brightness shifts.

The ONNX model has a fixed 640x640 input. Increasing/decreasing video stream
resolution does not change the model tensor size. Rate limiting and movement
filtering, not a fictitious low-resolution YOLO setting, save CPU.

IMPORTANT: These are requested stream parameters: Macroscop may cap FPS,
resize output, or ignore JPEG quality. Measure actual throughput locally.
When upgrading v1.4 settings, video preset defaults to "Экономичный";
password, selected ROI, direction and thresholds are retained.
Trigger snapshots: %LOCALAPPDATA%\StairGuard\Triggers (last five).
No second camera stream is started for preview.

Windows compile validation is configured via .github/workflows/build.yml.


v1.6 — произвольная многоугольная зона лестницы
===============================================
Настройки -> Настроить область и направление...
1. Нажми «1. Контур зоны».
2. Кликай по границе лестницы, добавляя точки (от 3 до 64).
   Контур может быть вогнутым, например огибать лестничные повороты.
3. Замкни фигуру кликом по первой жёлтой точке или кнопкой
   «Замкнуть контур».
4. Для коррекции нажми «1. Контур зоны» и перетаскивай белые вершины.
   «Убрать точку» удаляет последнюю точку, «Новый контур» очищает фигуру.
5. Проведи стрелку направления подъёма и нажми «Готово».
6. Сохрани настройки.

Многоугольник сохраняется в settings.json (RegionPolygon в координатах 0..1).
При обновлении прежняя прямоугольная Region автоматически используется
как четырёхточечная фигура; пароль, FPS и история тревог сохраняются.
Фильтр движения учитывает только внутренние пиксели фигуры,
а трекинг принимает людей только с центром внутри этой области.
Никаких изменений настроек Macroscop.


v1.8 — значок StairGuard и автоочистка Windows Toast
===================================================
Оригинальная иконка (камера + лестница): src/Assets/StairGuard.svg;
многоразмерный src/Assets/StairGuard.ico встроен в EXE и трей;
src/Assets/StairGuard.png встроен как ресурс и используется в Toast.
После установки отдельные файлы иконок не нужны.

Настоящие уведомления о подъёме остаются в Центре уведомлений Windows
не дольше 5 минут, пока StairGuard запущен. Приложение каждые 10 секунд
вызывает Windows ToastNotificationManagerCompat.History.Remove(tag, group)
только для своих тревог. Срок также задаётся через ExpirationTime (это
не заменяет явное удаление из истории на настольной Windows).
Расписание удаления хранится в LocalAppData/StairGuard/toast-expirations.json,
поэтому просроченные записи удалятся и после перезапуска приложения.
Если StairGuard выключен в момент истечения 5 минут, удаление произойдёт
при следующем запуске, а не в фоне. Журнал посещений и JPEG не удаляются.
Тестовые Toast имеют отдельный жизненный цикл и не затрагиваются очисткой.
