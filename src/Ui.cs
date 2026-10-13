using System.Drawing;
using System.Drawing.Drawing2D;
using System.Threading.Channels;
using System.Diagnostics;
using System.Drawing.Imaging;
using System.Windows.Forms;

namespace StairGuard;

internal sealed class TrayForm : Form
{
    readonly NotifyIcon tray;
    readonly ToolStripMenuItem pauseItem;
    Settings settings = Settings.Load();
    CancellationTokenSource? feedToken;
    Task? feedTask;
    bool paused;
    bool shuttingDown;
    byte[]? latestJpeg;
    readonly object imageGate = new();
    long latestVersion;
    PreviewForm? preview;
    int popupBusy;
    readonly SemaphoreSlim restartLock = new(1, 1);

    public TrayForm()
    {
        Text = "StairGuard";
        Size = new Size(1, 1);
        StartPosition = FormStartPosition.Manual;
        Location = new Point(-3000, -3000);
        ShowInTaskbar = false;
        Opacity = 0;
        FormBorderStyle = FormBorderStyle.FixedToolWindow;
        var menu = new ContextMenuStrip();
        menu.Items.Add("Настройки и зона лестницы", null, (_, _) => OpenSettings());
        menu.Items.Add("Смотреть видео с камеры", null, (_, _) => ShowPreview());
        pauseItem = new ToolStripMenuItem("Приостановить", null, (_, _) => TogglePause());
        menu.Items.Add(pauseItem);
        menu.Items.Add("Открыть журнал", null, (_, _) => OpenJournal());
        menu.Items.Add("Последние 5 срабатываний", null, (_, _) => OpenTriggers());
        menu.Items.Add("Выход", null, (_, _) => { shuttingDown = true; Close(); });
        tray = new NotifyIcon
        {
            Icon = SystemIcons.Information,
            Text = "StairGuard — запуск",
            Visible = true,
            ContextMenuStrip = menu
        };
        tray.DoubleClick += (_, _) => OpenSettings();
        Shown += (_, _) =>
        {
            Hide();
            BeginInvoke(new Action(() =>
            {
                if (!settings.Ready) OpenSettings();
                else _ = RestartFeedAsync();
            }));
        };
    }

    void SetStatus(string label)
    {
        if (shuttingDown || IsDisposed) return;
        if (InvokeRequired) { BeginInvoke(new Action(() => SetStatus(label))); return; }
        tray.Text = label.Length > 60 ? label[..60] : label;
    }

    void OpenSettings()
    {
        using var dialog = new SettingsDialog(settings);
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        settings = dialog.Result;
        settings.Save();
        Settings.SetAutostart(dialog.StartWithWindows);
        _ = RestartFeedAsync();
    }

    void TogglePause()
    {
        paused = !paused;
        pauseItem.Text = paused ? "Продолжить" : "Приостановить";
        _ = RestartFeedAsync();
    }

    async Task RestartFeedAsync()
    {
        await restartLock.WaitAsync();
        try
        {
        feedToken?.Cancel();
        if (feedTask != null)
        {
            try { await feedTask; }
            catch (Exception e) { Log.Write(e.Message); }
            feedTask = null;
        }
        feedToken?.Dispose();
        feedToken = null;
        if (paused || !settings.Ready || shuttingDown)
        {
            SetStatus(paused ? "StairGuard — пауза" : "StairGuard — ожидание настройки");
            return;
        }
        var config = settings;
        feedToken = new CancellationTokenSource();
        var token = feedToken.Token;
        feedTask = Task.Run(() => ReadLoop(config, token), token);
        }
        finally { restartLock.Release(); }
    }

    // One camera connection feeds the live preview and a rate-limited inference
    // queue. Incoming JPEGs are not decoded until displayed or analysed.
    async Task ReadLoop(Settings config, CancellationToken token)
    {
        var tracker = new MovementTracker();
        var queue = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(1)
        {
            SingleReader = true,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.DropOldest
        });
        Task inference = Task.Run(async () =>
        {
            try
            {
                using var detector = new Detector(Paths.Model);
                var motion = new MotionGate(config);
                await foreach (var jpeg in queue.Reader.ReadAllAsync(token))
                {
                    try
                    {
                        using var ms = new MemoryStream(jpeg, writable: false);
                        using var decoded = Image.FromStream(ms);
                        using var frame = new Bitmap(decoded);
                        if (config.MotionGateEnabled && !motion.HasMovement(frame))
                            continue;
                        var people = detector.Find(frame, config.ConfidencePercent / 100f);
                        var triggered = tracker.Update(people, config, frame.Width, frame.Height);
                        if (triggered != null) Alert(frame, triggered);
                    }
                    catch (Exception e)
                    {
                        Log.Write($"Распознавание: {e.GetType().Name}: {SafeError(e.Message)}");
                    }
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (Exception e)
            {
                Log.Write($"Модель: {e.GetType().Name}: {SafeError(e.Message)}");
                SetStatus("StairGuard — ошибка модели; см. журнал");
            }
        }, token);
        try
        {
            SetStatus("StairGuard — наблюдение за лестницей");
            long lastQueuedAt = 0;
            long intervalTicks = (long)(Stopwatch.Frequency / AnalysisRates.Normalize(config.AnalysisFps));
            while (!token.IsCancellationRequested)
            {
                try
                {
                    await Mjpeg.Consume(config, jpeg =>
                    {
                        lock (imageGate)
                        {
                            latestJpeg = jpeg;
                            ++latestVersion;
                        }
                        long now = Stopwatch.GetTimestamp();
                        if (lastQueuedAt == 0 || now - lastQueuedAt >= intervalTicks)
                        {
                            lastQueuedAt = now;
                            queue.Writer.TryWrite(jpeg); // latest only; never block video
                        }
                        return Task.CompletedTask;
                    }, token);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
                catch (Exception e)
                {
                    Log.Write($"Поток: {e.GetType().Name}: {SafeError(e.Message)}");
                    SetStatus("StairGuard — переподключение к камере");
                    await Task.Delay(TimeSpan.FromSeconds(5), token);
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        finally
        {
            queue.Writer.TryComplete();
            try { await inference; }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        }
    }

    static string SafeError(string message)
    {
        // Only known code-generated text. Never persist arbitrary HTTP URLs.
        if (message.Contains("http", StringComparison.OrdinalIgnoreCase)) return "Сетевая ошибка (URL скрыт)";
        return message.Length > 220 ? message[..220] : message;
    }

    void Alert(Bitmap frame, Person person)
    {
        if (Interlocked.CompareExchange(ref popupBusy, 1, 0) != 0) return;
        RectangleF b = person.Bounds;
        float marginX = b.Width * 0.30f, marginY = b.Height * 0.20f;
        int x = Math.Max(0, (int)(b.Left - marginX));
        int y = Math.Max(0, (int)(b.Top - marginY));
        int r = Math.Min(frame.Width, (int)(b.Right + marginX));
        int d = Math.Min(frame.Height, (int)(b.Bottom + marginY));
        if (r - x < 5 || d - y < 5) { Volatile.Write(ref popupBusy, 0); return; }
        using var crop = frame.Clone(new Rectangle(x, y, r - x, d - y), frame.PixelFormat);
        var copy = new Bitmap(crop);
        if (shuttingDown || !IsHandleCreated || IsDisposed) { copy.Dispose(); Volatile.Write(ref popupBusy, 0); return; }
        try
        {
            BeginInvoke(new Action(() =>
            {
                if (shuttingDown) { copy.Dispose(); Volatile.Write(ref popupBusy, 0); return; }
                try
                {
                    var alert = new AlertForm(copy);
                    alert.FormClosed += (_, _) => Volatile.Write(ref popupBusy, 0);
                    alert.Show();
                    Log.Write($"Подъём по лестнице, уверенность {person.Confidence:P0}");
                    // frame belongs to the inference loop and may already be disposed;
                    // copy is retained by the alert form and is valid here.
                    TriggerHistory.Add(person.Confidence, copy, new Rectangle(0, 0, copy.Width, copy.Height));
                }
                catch { copy.Dispose(); Volatile.Write(ref popupBusy, 0); }
            }));
        }
        catch { copy.Dispose(); Volatile.Write(ref popupBusy, 0); }
    }

    (Bitmap? image, long version) GetPreviewFrame(long seenVersion)
    {
        byte[]? jpeg;
        long version;
        lock (imageGate)
        {
            if (latestJpeg == null || latestVersion == seenVersion) return (null, seenVersion);
            jpeg = latestJpeg;
            version = latestVersion;
        }
        using var ms = new MemoryStream(jpeg, writable: false);
        using var image = Image.FromStream(ms);
        return (new Bitmap(image), version);
    }

    void ShowPreview()
    {
        if (!settings.Ready) { OpenSettings(); return; }
        if (preview is { IsDisposed: false })
        {
            preview.Show();
            preview.WindowState = FormWindowState.Normal;
            preview.Activate();
            return;
        }
        preview = new PreviewForm(GetPreviewFrame);
        preview.FormClosed += (_, _) => preview = null;
        preview.Show();
        preview.Activate();
    }

    void OpenJournal()
    {
        try
        {
            Directory.CreateDirectory(Paths.Home);
            if (!File.Exists(Paths.Journal))
                File.WriteAllText(Paths.Journal, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] StairGuard: журнал создан.{Environment.NewLine}");
            var info = new ProcessStartInfo("notepad.exe") { UseShellExecute = true };
            info.ArgumentList.Add(Paths.Journal);
            Process.Start(info);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Не удалось открыть файл журнала: {SafeError(ex.Message)}", "StairGuard");
        }
    }

    void OpenTriggers()
    {
        try
        {
            Directory.CreateDirectory(Paths.Home);
            if (!File.Exists(Path.Combine(Paths.Home, "triggers.log")))
                File.WriteAllText(Path.Combine(Paths.Home, "triggers.log"), "Срабатываний ещё не было.");
            Process.Start(new ProcessStartInfo("notepad.exe")
            {
                Arguments = $"\"{Path.Combine(Paths.Home, "triggers.log")}\"",
                UseShellExecute = true
            });
        }
        catch (Exception ex) { Log.Write(ex); }
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        shuttingDown = true;
        feedToken?.Cancel();
        preview?.Close();
        tray.Visible = false;
        tray.Dispose();
        lock (imageGate) { latestJpeg = null; }
        base.OnFormClosed(e);
    }
}

// The preview reuses frames from the ONE existing MJPEG reader; opening it adds no network load.
internal sealed class PreviewForm : Form
{
    readonly PictureBox picture = new()
    {
        Dock = DockStyle.Fill,
        BackColor = Color.FromArgb(10, 13, 19),
        SizeMode = PictureBoxSizeMode.Zoom
    };
    readonly Label status = new()
    {
        Dock = DockStyle.Bottom,
        Height = 30,
        TextAlign = ContentAlignment.MiddleLeft,
        Padding = new Padding(12, 0, 0, 0),
        Text = "Подключение: ожидание первого кадра…",
        ForeColor = Color.Gainsboro
    };
    readonly System.Windows.Forms.Timer refresh = new() { Interval = 125 };
    readonly Func<long, (Bitmap? image, long version)> source;
    long version = -1;
    long lastFrameTick;

    public PreviewForm(Func<long, (Bitmap? image, long version)> source)
    {
        this.source = source;
        Text = "StairGuard — видео с лестницы (прямой эфир)";
        StartPosition = FormStartPosition.Manual;
        MinimumSize = new Size(500, 340);
        ClientSize = new Size(800, 490);
        BackColor = Color.FromArgb(23, 28, 35);
        ForeColor = Color.White;
        AutoScaleMode = AutoScaleMode.Dpi;
        Controls.Add(picture);
        Controls.Add(status);
        refresh.Tick += (_, _) => RefreshImage();
        Shown += (_, _) =>
        {
            var area = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0,0,1920,1080);
            Location = new Point(area.Right - Width - 20, area.Top + 20);
            refresh.Start();
        };
        FormClosed += (_, _) =>
        {
            refresh.Stop();
            refresh.Dispose();
            var old = picture.Image;
            picture.Image = null;
            old?.Dispose();
        };
    }

    void RefreshImage()
    {
        try
        {
            var (next, sequence) = source(version);
            if (next != null)
            {
                var old = picture.Image;
                picture.Image = next;
                old?.Dispose();
                version = sequence;
                lastFrameTick = Environment.TickCount64;
                status.Text = $"LIVE  •  {next.Width}×{next.Height}  •  получение кадров Macroscop";
            }
            else if (lastFrameTick != 0 && Environment.TickCount64 - lastFrameTick > 5000)
                status.Text = "Нет новых кадров более 5 секунд. Проверь журнал и подключение.";
        }
        catch (Exception e)
        {
            status.Text = $"Ошибка просмотра: {e.GetType().Name}";
        }
    }
}

internal sealed class AlertForm : Form
{
    readonly PictureBox picture;
    readonly System.Windows.Forms.Timer timer;
    public AlertForm(Bitmap crop, bool manual = false)
    {
        Text = manual ? "StairGuard — просмотр камеры" : "StairGuard — движение по лестнице";
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = new Font("Segoe UI", 10);
        MinimumSize = new Size(430, 320);
        ClientSize = new Size(510, 380);
        BackColor = Color.FromArgb(24, 28, 35);
        ForeColor = Color.White;
        TopMost = true;
        ShowInTaskbar = false;
        FormBorderStyle = FormBorderStyle.FixedToolWindow;
        var area = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1920, 1080);
        Location = new Point(area.Right - Width - 16, area.Bottom - Height - 16);
        var title = new Label
        {
            Text = manual ? "Текущий кадр" : "Человек движется вверх по лестнице",
            ForeColor = Color.White,
            Dock = DockStyle.Top,
            Height = 46,
            Padding = new Padding(12, 10, 0, 0),
            Font = new Font("Segoe UI", 11, FontStyle.Bold)
        };
        picture = new PictureBox { Dock = DockStyle.Fill, Image = crop, SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.FromArgb(8, 10, 13) };
        Controls.Add(picture);
        Controls.Add(title);
        timer = new System.Windows.Forms.Timer { Interval = 15_000 };
        timer.Tick += (_, _) => Close();
        if (!manual) Shown += (_, _) => timer.Start();
        FormClosed += (_, _) => { timer.Stop(); timer.Dispose(); picture.Image?.Dispose(); };
    }
}

internal sealed class SettingsDialog : Form
{
    readonly TextBox server = new() { Dock = DockStyle.Fill };
    readonly TextBox login = new() { Dock = DockStyle.Fill };
    readonly TextBox password = new() { Dock = DockStyle.Fill, UseSystemPasswordChar = true, PlaceholderText = "Не менять сохранённый пароль" };
    readonly ComboBox videoPreset = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 185 };
    readonly Label videoDetails = new() { AutoSize = true, ForeColor = Color.DimGray };
    readonly ComboBox analysisRate = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 185 };
    readonly CheckBox motionGate = new() { Text = "Запускать YOLO только при движении на лестнице", AutoSize = true, Checked = true };
    readonly NumericUpDown conf = new() { Minimum = 20, Maximum = 90, Width = 95 };
    readonly NumericUpDown cooldown = new() { Minimum = 5, Maximum = 300, Width = 95 };
    readonly CheckBox autostart = new() { Text = "Запускать автоматически при входе в Windows", AutoSize = true };
    readonly Label zone = new() { AutoSize = true, ForeColor = Color.FromArgb(22, 101, 52) };
    readonly Button select = new() { Text = "Настроить область и направление…", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowOnly, MinimumSize = new Size(270, 40) };
    readonly Button previewButton = new() { Text = "Тестовый снимок", AutoSize = true, MinimumSize = new Size(140, 40) };
    readonly Button save = new() { Text = "Сохранить и запустить", AutoSize = true, MinimumSize = new Size(195, 42) };
    Settings work;
    public Settings Result => work;
    public bool StartWithWindows => autostart.Checked;

    public SettingsDialog(Settings old)
    {
        work = new Settings
        {
            Server = old.Server, Login = old.Login, Credential = old.Credential,
            VideoPreset = old.VideoPreset, AnalysisFps = old.AnalysisFps, MotionGateEnabled = old.MotionGateEnabled,
            ConfidencePercent = old.ConfidencePercent, CooldownSeconds = old.CooldownSeconds,
            Region = old.Region, RegionPolygon = old.RegionPolygon is null ? null : new List<PointF>(old.RegionPolygon),
            ArrowFrom = old.ArrowFrom, ArrowTo = old.ArrowTo
        };
        Text = "StairGuard — настройки";
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = new Font("Segoe UI", 10);
        StartPosition = FormStartPosition.Manual;
        MinimumSize = new Size(510, 440);
        ClientSize = new Size(740, 590);
        FormBorderStyle = FormBorderStyle.Sizable;
        MaximizeBox = false;
        BackColor = Color.FromArgb(247, 249, 252);
        ForeColor = Color.FromArgb(31, 41, 55);

        var header = new Panel { Dock = DockStyle.Top, Height = 100, BackColor = Color.FromArgb(29, 47, 72) };
        var headerTitle = new Label { Text = "StairGuard", ForeColor = Color.White, Font = new Font("Segoe UI", 17, FontStyle.Bold), AutoSize = true, Location = new Point(22, 14) };
        var headerDesc = new Label { Text = "Мониторинг одной камеры • локальное распознавание людей", ForeColor = Color.FromArgb(210, 225, 240), AutoSize = true, Location = new Point(23, 53) };
        header.Controls.Add(headerTitle);
        header.Controls.Add(headerDesc);
        var footer = new Panel { Dock = DockStyle.Bottom, Height = 74, Padding = new Padding(22, 10, 22, 12) };
        var cancel = new Button { Text = "Отмена", DialogResult = DialogResult.Cancel, AutoSize = true, MinimumSize = new Size(115, 42) };
        var actions = new FlowLayoutPanel { Dock = DockStyle.Right, AutoSize = true, FlowDirection = FlowDirection.RightToLeft, WrapContents = false };
        actions.Controls.Add(save);
        actions.Controls.Add(cancel);
        footer.Controls.Add(actions);
        AcceptButton = save;
        CancelButton = cancel;

        var scroller = new Panel { Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(24, 22, 24, 18) };
        var table = new TableLayoutPanel
        {
            Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 2, RowCount = 0, CellBorderStyle = TableLayoutPanelCellBorderStyle.None,
            Padding = new Padding(0), Margin = Padding.Empty
        };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 170));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        void Add(Control content, string? title = null, int marginBottom = 12)
        {
            int row = table.RowCount++;
            table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            if (title != null)
            {
                var lbl = new Label { Text = title, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 8, 14, marginBottom) };
                table.Controls.Add(lbl, 0, row);
                content.Margin = new Padding(0, 4, 0, marginBottom);
                table.Controls.Add(content, 1, row);
            }
            else
            {
                content.Margin = new Padding(0, 3, 0, marginBottom);
                table.Controls.Add(content, 0, row);
                table.SetColumnSpan(content, 2);
            }
        }
        Add(new Label { Text = "Камера: " + Paths.Channel, AutoSize = true, Font = new Font(Font, FontStyle.Bold), ForeColor = Color.FromArgb(36, 72, 115) }, null, 16);
        server.Text = work.Server;
        login.Text = work.Login;
        videoPreset.Items.AddRange(VideoProfiles.Names);
        videoPreset.SelectedItem = VideoProfiles.Names.Contains(work.VideoPreset) ? work.VideoPreset : VideoProfiles.Economy;
        videoDetails.Text = VideoProfiles.Describe(work.VideoPreset);
        videoPreset.SelectedIndexChanged += (_, _) => videoDetails.Text = VideoProfiles.Describe(videoPreset.SelectedItem?.ToString() ?? VideoProfiles.Economy);
        foreach (double rate in AnalysisRates.Values) analysisRate.Items.Add(AnalysisRates.Label(rate));
        analysisRate.SelectedIndex = Array.IndexOf(AnalysisRates.Values, AnalysisRates.Normalize(work.AnalysisFps));
        motionGate.Checked = work.MotionGateEnabled;
        conf.Value = Math.Clamp(work.ConfidencePercent, (int)conf.Minimum, (int)conf.Maximum);
        cooldown.Value = Math.Clamp(work.CooldownSeconds, (int)cooldown.Minimum, (int)cooldown.Maximum);
        Add(server, "Сервер Macroscop");
        Add(login, "Логин");
        Add(password, "Пароль / MD5");
        Add(videoPreset, "Режим видеопотока");
        Add(videoDetails, "Видео Macroscop");
        Add(analysisRate, "Частота анализа YOLO");
        Add(motionGate, null, 8);
        Add(conf, "Уверенность, %");
        Add(cooldown, "Повтор тревоги, сек.");
        autostart.Checked = Settings.Autostart();
        Add(autostart, null, 16);
        var pickerRow = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = true, MaximumSize = new Size(640, 0), FlowDirection = FlowDirection.LeftToRight };
        pickerRow.Controls.Add(select);
        pickerRow.Controls.Add(previewButton);
        Add(pickerRow, null, 4);
        zone.Text = RegionGeometry.Resolve(work) is not null && work.ArrowFrom.HasValue && work.ArrowTo.HasValue
            ? "Область и направление заданы" : "Область и направление ещё не выбраны";
        Add(zone, null, 8);
        Add(new Label
        {
            Text = "Обведи лестницу произвольным контуром по точкам, замкни область и укажи стрелкой направление подъёма к тебе.",
            AutoSize = true, MaximumSize = new Size(620, 0), ForeColor = Color.DimGray
        }, null, 8);
        scroller.Controls.Add(table);
        // Dock=Top layout keeps the settings scrollable on 125–200% scaling.
        Controls.Add(scroller);
        Controls.Add(footer);
        Controls.Add(header);
        Shown += (_, _) => FitToScreen();
        select.Click += async (_, _) => await SelectRegionAsync();
        previewButton.Click += async (_, _) => await ShowTestFrameAsync();
        save.Click += (_, _) => SaveClicked();
    }

    void FitToScreen()
    {
        var area = Screen.FromControl(this).WorkingArea;
        int w = Math.Min(Width, Math.Max(480, area.Width - 48));
        int h = Math.Min(Height, Math.Max(420, area.Height - 48));
        if (Width != w || Height != h)
        {
            MinimumSize = new Size(Math.Min(510, w), Math.Min(440, h));
            Size = new Size(w, h);
            CenterToScreen();
        }
    }

    bool ApplyCredentials()
    {
        work.Server = server.Text.Trim();
        work.Login = login.Text.Trim();
        work.VideoPreset = videoPreset.SelectedItem?.ToString() ?? VideoProfiles.Economy;
        VideoProfiles.Apply(work);
        work.AnalysisFps = AnalysisRates.Values[Math.Max(0, analysisRate.SelectedIndex)];
        work.MotionGateEnabled = motionGate.Checked;
        work.ConfidencePercent = (int)conf.Value;
        work.CooldownSeconds = (int)cooldown.Value;
        if (string.IsNullOrWhiteSpace(work.Server) || string.IsNullOrWhiteSpace(work.Login))
        { MessageBox.Show("Укажи адрес сервера и логин."); return false; }
        if (password.Text.Length > 0)
        {
            work.Credential = Vault.Seal(password.Text);
            password.Clear();
        }
        if (string.IsNullOrWhiteSpace(work.Credential))
        { MessageBox.Show("Введи пароль Macroscop."); return false; }
        return true;
    }

    async Task ShowTestFrameAsync()
    {
        if (!ApplyCredentials()) return;
        previewButton.Enabled = false;
        try
        {
            using var frame = await Mjpeg.Grab(work, CancellationToken.None);
            new AlertForm(new Bitmap(frame), true).Show(this);
        }
        catch (Exception e) { MessageBox.Show("Не удалось получить кадр: " + e.GetType().Name, "StairGuard"); }
        finally { previewButton.Enabled = true; }
    }

    async Task SelectRegionAsync()
    {
        if (!ApplyCredentials()) return;
        select.Enabled = false;
        select.Text = "Получаю кадр…";
        try
        {
            using Bitmap frame = await Mjpeg.Grab(work, CancellationToken.None);
            using var picker = new RegionPicker(new Bitmap(frame), RegionGeometry.Resolve(work), work.ArrowFrom, work.ArrowTo);
            if (picker.ShowDialog(this) == DialogResult.OK)
            {
                work.RegionPolygon = picker.Selection;
                work.Region = null;
                work.ArrowFrom = picker.From;
                work.ArrowTo = picker.To;
                zone.Text = "Область и направление заданы";
            }
        }
        catch (Exception e)
        {
            MessageBox.Show("Не удалось получить кадр. Проверь адрес, логин и пароль.\n" + e.GetType().Name,
                "StairGuard", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally { select.Enabled = true; select.Text = "Настроить область и направление…"; }
    }

    void SaveClicked()
    {
        if (!ApplyCredentials()) return;
        if (!work.Ready)
        { MessageBox.Show("Сначала выбери область лестницы и направление движения."); return; }
        DialogResult = DialogResult.OK;
        Close();
    }
}

// All painting happens in one double-buffered surface, eliminating GDI flicker
// when dragging ROI rectangles and direction arrows.
internal sealed class SmoothCanvas : Panel
{
    public SmoothCanvas()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        UpdateStyles();
    }
}

internal sealed class RegionPicker : Form
{
    readonly Bitmap frame;
    readonly SmoothCanvas canvas = new() { Dock = DockStyle.Fill, BackColor = Color.Black };
    readonly Button regionButton = new() { Text = "1. Контур зоны", AutoSize = true, MinimumSize = new Size(155, 36) };
    readonly Button closeButton = new() { Text = "Замкнуть контур", AutoSize = true, MinimumSize = new Size(165, 36) };
    readonly Button undoButton = new() { Text = "Убрать точку", AutoSize = true, MinimumSize = new Size(140, 36) };
    readonly Button resetButton = new() { Text = "Новый контур", AutoSize = true, MinimumSize = new Size(145, 36) };
    readonly Button arrowButton = new() { Text = "2. Стрелка", AutoSize = true, MinimumSize = new Size(145, 36) };
    readonly Button ok = new() { Text = "Готово", AutoSize = true, MinimumSize = new Size(110, 36) };
    readonly Label help = new()
    {
        Dock = DockStyle.Top, Height = 48, TextAlign = ContentAlignment.MiddleLeft,
        Padding = new Padding(12, 0, 0, 0)
    };

    readonly List<PointF> vertices = new();
    bool closed;
    bool makingRegion = true;
    int draggedVertex = -1;
    PointF originalVertex;
    PointF? press;
    PointF? dragging;
    PointF? hover;

    // The completed polygon, in normalized image coordinates.
    public List<PointF>? Selection => closed && RegionGeometry.IsValid(vertices)
        ? new List<PointF>(vertices) : null;
    public PointF? From { get; private set; }
    public PointF? To { get; private set; }

    public RegionPicker(Bitmap image, IReadOnlyList<PointF>? polygon, PointF? from, PointF? to)
    {
        frame = image;
        if (RegionGeometry.IsValid(polygon))
        {
            vertices.AddRange(polygon!);
            closed = true;
        }
        From = from;
        To = to;
        Text = "StairGuard — произвольная зона лестницы";
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = new Font("Segoe UI", 10);
        ClientSize = new Size(1080, 720);
        MinimumSize = new Size(710, 510);
        StartPosition = FormStartPosition.Manual;

        var tools = new FlowLayoutPanel
        {
            Dock = DockStyle.Top, Height = 92, Padding = new Padding(5, 4, 5, 4),
            WrapContents = true, AutoScroll = true
        };
        tools.Controls.AddRange(new Control[]
        {
            regionButton, closeButton, undoButton, resetButton, arrowButton, ok
        });
        Controls.Add(canvas);
        Controls.Add(help);
        Controls.Add(tools);

        regionButton.Click += (_, _) => { makingRegion = true; UpdateControls(); canvas.Invalidate(); };
        closeButton.Click += (_, _) => FinishPolygon();
        undoButton.Click += (_, _) =>
        {
            if (vertices.Count == 0) return;
            vertices.RemoveAt(vertices.Count - 1);
            closed = false;
            makingRegion = true;
            From = To = null;
            UpdateControls();
            canvas.Invalidate();
        };
        resetButton.Click += (_, _) =>
        {
            vertices.Clear();
            closed = false;
            makingRegion = true;
            From = To = null;
            UpdateControls();
            canvas.Invalidate();
        };
        arrowButton.Click += (_, _) =>
        {
            if (!closed)
            {
                MessageBox.Show("Сначала замкни контур минимум из трёх точек.");
                return;
            }
            makingRegion = false;
            UpdateControls();
            canvas.Invalidate();
        };
        ok.Click += (_, _) =>
        {
            if (!closed || !RegionGeometry.IsValid(vertices))
            {
                MessageBox.Show("Замкни корректный контур лестницы минимум из трёх точек.");
                return;
            }
            if (!From.HasValue || !To.HasValue ||
                MathF.Abs(From.Value.X - To.Value.X) + MathF.Abs(From.Value.Y - To.Value.Y) < .035f)
            {
                MessageBox.Show("Проведи стрелку направления подъёма.");
                return;
            }
            DialogResult = DialogResult.OK;
            Close();
        };

        canvas.Paint += (_, e) => Draw(e.Graphics);
        canvas.MouseDown += (_, e) =>
        {
            if (e.Button != MouseButtons.Left || !TryNormalized(e.Location, out PointF p))
                return;
            if (makingRegion)
            {
                if (closed)
                {
                    draggedVertex = FindHandle(e.Location);
                    if (draggedVertex >= 0)
                    {
                        originalVertex = vertices[draggedVertex];
                        canvas.Capture = true;
                    }
                }
                else
                {
                    if (vertices.Count >= 3 && FindHandle(e.Location) == 0)
                        FinishPolygon();
                    else if (vertices.Count < 64)
                    {
                        vertices.Add(p);
                        UpdateControls();
                        canvas.Invalidate();
                    }
                }
            }
            else if (closed)
            {
                press = p;
                dragging = p;
                canvas.Capture = true;
            }
        };
        canvas.MouseMove += (_, e) =>
        {
            hover = TryNormalized(e.Location, out PointF p) ? p : null;
            if (draggedVertex >= 0 && hover.HasValue)
                vertices[draggedVertex] = hover.Value;
            if (press.HasValue && hover.HasValue) dragging = hover.Value;
            canvas.Invalidate();
        };
        canvas.MouseUp += (_, e) =>
        {
            if (draggedVertex >= 0)
            {
                if (!RegionGeometry.IsValid(vertices)) vertices[draggedVertex] = originalVertex;
                draggedVertex = -1;
            }
            else if (press.HasValue)
            {
                if (TryNormalized(e.Location, out var end))
                {
                    From = press;
                    To = end;
                }
                press = dragging = null;
            }
            canvas.Capture = false;
            canvas.Invalidate();
        };
        Shown += (_, _) =>
        {
            var area = Screen.FromControl(this).WorkingArea;
            int w = Math.Min(Width, Math.Max(600, area.Width - 48));
            int h = Math.Min(Height, Math.Max(440, area.Height - 48));
            if (Width != w || Height != h)
            {
                MinimumSize = new Size(Math.Min(710, w), Math.Min(510, h));
                Size = new Size(w, h);
                CenterToScreen();
            }
        };
        FormClosed += (_, _) => frame.Dispose();
        UpdateControls();
    }

    void FinishPolygon()
    {
        if (!RegionGeometry.IsValid(vertices))
        {
            MessageBox.Show("Контур должен содержать минимум три точки и охватывать ненулевую площадь.");
            return;
        }
        closed = true;
        makingRegion = false;
        UpdateControls();
        canvas.Invalidate();
    }

    void UpdateControls()
    {
        closeButton.Enabled = !closed && vertices.Count >= 3;
        undoButton.Enabled = vertices.Count > 0;
        arrowButton.Enabled = closed;
        help.Text = !closed
            ? "Щёлкай по краю лестницы, добавляя вершины. Замкни контур первой точкой или кнопкой. " +
              "Можно обводить изгибы и повороты."
            : makingRegion
                ? "Контур замкнут. Перетаскивай вершины для корректировки или нажми «Новый контур»."
                : "Зажми мышь и проведи стрелку в направлении подъёма К ТЕБЕ. Затем нажми «Готово».";
    }

    Rectangle ViewRect()
    {
        float factor = Math.Min((float)Math.Max(1, canvas.Width) / frame.Width,
                                (float)Math.Max(1, canvas.Height) / frame.Height);
        int w = Math.Max(1, (int)(frame.Width * factor));
        int h = Math.Max(1, (int)(frame.Height * factor));
        return new Rectangle((canvas.Width - w) / 2, (canvas.Height - h) / 2, w, h);
    }

    bool TryNormalized(Point pixel, out PointF result)
    {
        Rectangle rect = ViewRect();
        if (!rect.Contains(pixel))
        {
            result = default;
            return false;
        }
        result = new PointF(Math.Clamp((pixel.X - rect.X) / (float)rect.Width, 0, 1),
                            Math.Clamp((pixel.Y - rect.Y) / (float)rect.Height, 0, 1));
        return true;
    }

    int FindHandle(Point mouse)
    {
        var rect = ViewRect();
        float maxDistance = 16f * 16f;
        int index = -1;
        for (int i = 0; i < vertices.Count; i++)
        {
            float dx = rect.X + vertices[i].X * rect.Width - mouse.X;
            float dy = rect.Y + vertices[i].Y * rect.Height - mouse.Y;
            float distance = dx * dx + dy * dy;
            if (distance <= maxDistance) { maxDistance = distance; index = i; }
        }
        return index;
    }

    void Draw(Graphics g)
    {
        g.Clear(Color.Black);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.InterpolationMode = InterpolationMode.HighQualityBilinear;
        var area = ViewRect();
        g.DrawImage(frame, area);

        PointF Pixel(PointF p) => new(area.Left + p.X * area.Width, area.Top + p.Y * area.Height);
        if (vertices.Count > 0)
        {
            PointF[] points = vertices.Select(Pixel).ToArray();
            if (closed && vertices.Count >= 3)
            {
                using var fill = new SolidBrush(Color.FromArgb(55, Color.LimeGreen));
                g.FillPolygon(fill, points);
                using var outline = new Pen(Color.Lime, 3);
                g.DrawPolygon(outline, points);
            }
            else if (points.Length >= 2)
            {
                using var outline = new Pen(Color.Lime, 3);
                g.DrawLines(outline, points);
            }

            if (!closed && makingRegion && hover.HasValue)
            {
                using var guide = new Pen(Color.FromArgb(170, Color.Lime), 2) { DashStyle = DashStyle.Dash };
                g.DrawLine(guide, points[^1], Pixel(hover.Value));
            }

            for (int i = 0; i < points.Length; i++)
            {
                float size = i == 0 ? 13 : 11;
                using var fill = new SolidBrush(i == 0 ? Color.Gold : Color.White);
                g.FillEllipse(fill, points[i].X - size / 2, points[i].Y - size / 2, size, size);
                g.DrawEllipse(Pens.DarkGreen, points[i].X - size / 2, points[i].Y - size / 2, size, size);
            }
        }

        void DrawArrow(PointF from, PointF to)
        {
            using var pen = new Pen(Color.OrangeRed, 4);
            pen.CustomEndCap = new AdjustableArrowCap(5, 6);
            g.DrawLine(pen, Pixel(from), Pixel(to));
        }
        if (From.HasValue && To.HasValue) DrawArrow(From.Value, To.Value);
        if (press.HasValue && dragging.HasValue) DrawArrow(press.Value, dragging.Value);
    }
}
