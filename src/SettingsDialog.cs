using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;

namespace StairGuard;

internal sealed class SettingsDialog : Form
{
    readonly TextBox server = new() { Dock = DockStyle.Fill };
    readonly TextBox login = new() { Dock = DockStyle.Fill };
    readonly TextBox password = new() { Dock = DockStyle.Fill, UseSystemPasswordChar = true, PlaceholderText = "Не менять сохранённый пароль" };
    readonly ComboBox videoPreset = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 220 };
    readonly Label videoDetails = new() { AutoSize = true, ForeColor = Color.FromArgb(40, 76, 110) };
    readonly ComboBox analysisRate = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 160 };
    readonly CheckBox motionGate = new() { Text = "Пропускать кадры без движения", AutoSize = true };
    readonly CheckBox debugYolo = new() { Text = "Подробный режим отладки YOLO", AutoSize = true };
    readonly NumericUpDown conf = new() { Minimum = 10, Maximum = 95, Width = 110 };
    readonly NumericUpDown minHits = new() { Minimum = 2, Maximum = 8, Width = 110 };
    readonly NumericUpDown travel = new() { Minimum = 8, Maximum = 100, Width = 110 };
    readonly NumericUpDown matchRadius = new() { Minimum = 70, Maximum = 250, Width = 110 };
    readonly NumericUpDown cooldown = new() { Minimum = 5, Maximum = 300, Width = 110 };
    readonly CheckBox autostart = new() { Text = "Запускать при входе в Windows", AutoSize = true };
    readonly Label zone = new() { AutoSize = true, ForeColor = Color.FromArgb(22, 101, 52) };
    readonly Button select = new() { Text = "Изменить контур и направление", AutoSize = true, MinimumSize = new Size(245, 38) };
    readonly Button previewButton = new() { Text = "Проверить кадр", AutoSize = true, MinimumSize = new Size(155, 38) };
    readonly Button manualTriggerButton = new() { Text = "Проверить уведомление Windows", AutoSize = true, MinimumSize = new Size(245, 38) };
    readonly ComboBox soundMode = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 265 };
    readonly TextBox soundPath = new() { Dock = DockStyle.Fill, ReadOnly = true, PlaceholderText = "Файл звука не выбран" };
    readonly Button browseSound = new() { Text = "Выбрать файл…", AutoSize = true };
    readonly Button previewSound = new() { Text = "Прослушать", AutoSize = true };
    readonly Button save = new() { Text = "Сохранить и запустить", AutoSize = true, MinimumSize = new Size(195, 42) };

    readonly Action triggerTest;
    readonly Action openHistory;
    readonly Action openJournal;
    readonly Action openPreview;

    Settings work;
    public Settings Result => work;
    public bool StartWithWindows => autostart.Checked;

    public SettingsDialog(Settings old, Action triggerTest, Action openHistory, Action openJournal, Action openPreview)
    {
        this.triggerTest = triggerTest;
        this.openHistory = openHistory;
        this.openJournal = openJournal;
        this.openPreview = openPreview;
        work = new Settings
        {
            Server = old.Server, Login = old.Login, Credential = old.Credential,
            VideoPreset = old.VideoPreset, AnalysisFps = old.AnalysisFps,
            MotionGateEnabled = old.MotionGateEnabled,
            ConfidencePercent = old.ConfidencePercent, CooldownSeconds = old.CooldownSeconds,
            TrackerMinHits = old.TrackerMinHits, TrackerTravelPixels = old.TrackerTravelPixels,
            TrackerMatchPixels = old.TrackerMatchPixels, DebugYolo = old.DebugYolo,
            NotificationSound = NotificationAudio.Normalize(old.NotificationSound),
            CustomSoundPath = old.CustomSoundPath ?? "",
            Region = old.Region,
            RegionPolygon = old.RegionPolygon is null ? null : new List<PointF>(old.RegionPolygon),
            ArrowFrom = old.ArrowFrom, ArrowTo = old.ArrowTo
        };

        Text = "StairGuard — настройки";
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = new Font("Segoe UI", 10);
        MinimumSize = new Size(630, 485);
        ClientSize = new Size(810, 620);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.FromArgb(246, 249, 253);
        ForeColor = Color.FromArgb(31, 41, 55);

        var header = new Panel { Dock = DockStyle.Top, Height = 82, BackColor = Color.FromArgb(29, 47, 72) };
        header.Controls.Add(new Label
        {
            Text = "StairGuard", AutoSize = true, Location = new Point(22, 10),
            Font = new Font("Segoe UI", 17, FontStyle.Bold), ForeColor = Color.White
        });
        header.Controls.Add(new Label
        {
            Text = "Камера • распознавание • оповещения • система", AutoSize = true,
            Location = new Point(23, 50), ForeColor = Color.FromArgb(205, 222, 240)
        });

        var footer = new Panel { Dock = DockStyle.Bottom, Height = 69, Padding = new Padding(18, 10, 18, 9) };
        var cancel = new Button { Text = "Отмена", DialogResult = DialogResult.Cancel, AutoSize = true, MinimumSize = new Size(110, 40) };
        var actions = new FlowLayoutPanel { Dock = DockStyle.Right, AutoSize = true, WrapContents = false, FlowDirection = FlowDirection.RightToLeft };
        actions.Controls.Add(save);
        actions.Controls.Add(cancel);
        footer.Controls.Add(actions);
        AcceptButton = save;
        CancelButton = cancel;

        var tabs = new TabControl { Dock = DockStyle.Fill, Padding = new Point(18, 8) };
        var (camera, cameraStack) = NewTab("Камера");
        var (detection, detectionStack) = NewTab("Распознавание");
        var (notifications, notificationStack) = NewTab("Уведомления");
        var (system, systemStack) = NewTab("Система");
        tabs.TabPages.AddRange(new[] { camera, detection, notifications, system });

        BuildCamera(cameraStack);
        BuildDetection(detectionStack);
        BuildNotifications(notificationStack);
        BuildSystem(systemStack);

        Controls.Add(tabs);
        Controls.Add(footer);
        Controls.Add(header);
        Shown += (_, _) => FitToScreen();
        save.Click += (_, _) => SaveClicked();
        select.Click += async (_, _) => await SelectRegionAsync();
        previewButton.Click += async (_, _) => await ShowTestFrameAsync();
        manualTriggerButton.Click += (_, _) => triggerTest();
        browseSound.Click += (_, _) => ChooseSound();
        previewSound.Click += (_, _) => PreviewSound();
        soundMode.SelectedIndexChanged += (_, _) => UpdateSoundControls();
    }

    static (TabPage page, TableLayoutPanel stack) NewTab(string title)
    {
        var page = new TabPage { Text = title, BackColor = Color.FromArgb(246, 249, 253), Padding = new Padding(14), AutoScroll = true };
        var stack = new TableLayoutPanel
        {
            Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1, RowCount = 0, Padding = Padding.Empty
        };
        stack.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        page.Controls.Add(stack);
        return (page, stack);
    }

    TableLayoutPanel Section(TableLayoutPanel stack, string title)
    {
        var group = new GroupBox
        {
            Text = title, Dock = DockStyle.Top, AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(13, 10, 13, 10),
            Font = new Font("Segoe UI", 10, FontStyle.Bold),
            ForeColor = Color.FromArgb(36, 66, 105),
            BackColor = Color.White,
            Margin = new Padding(0, 2, 0, 14)
        };
        var rows = new TableLayoutPanel
        {
            Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 2, RowCount = 0,
            Font = new Font("Segoe UI", 9.5f),
            ForeColor = Color.FromArgb(31, 41, 55),
            Padding = new Padding(3, 5, 3, 6)
        };
        rows.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 215));
        rows.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        group.Controls.Add(rows);
        stack.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        stack.Controls.Add(group, 0, stack.RowCount++);
        return rows;
    }

    static void Row(TableLayoutPanel table, string label, Control input, string help)
    {
        int index = table.RowCount++;
        table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        table.Controls.Add(new Label
        {
            Text = label, AutoSize = true, Anchor = AnchorStyles.Left,
            Margin = new Padding(4, 10, 9, 4), Font = new Font("Segoe UI", 9.5f, FontStyle.Bold)
        }, 0, index);
        input.Margin = new Padding(4, 4, 4, 3);
        table.Controls.Add(input, 1, index);
        Hint(table, help);
    }

    static void Hint(TableLayoutPanel table, string help)
    {
        int index = table.RowCount++;
        table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var note = new Label
        {
            Text = help, AutoSize = true,
            MaximumSize = new Size(660, 0),
            Margin = new Padding(7, 0, 6, 13),
            ForeColor = Color.FromArgb(99, 111, 125),
            Font = new Font("Segoe UI", 9f)
        };
        table.Controls.Add(note, 0, index);
        table.SetColumnSpan(note, 2);
    }

    static FlowLayoutPanel Buttons(params Control[] controls)
    {
        var panel = new FlowLayoutPanel
        {
            AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlowDirection = FlowDirection.LeftToRight, WrapContents = true,
            MaximumSize = new Size(620, 0)
        };
        panel.Controls.AddRange(controls);
        return panel;
    }

    void BuildCamera(TableLayoutPanel stack)
    {
        var connection = Section(stack, "Подключение Macroscop");
        server.Text = work.Server;
        login.Text = work.Login;
        Row(connection, "Адрес сервера", server,
            "Адрес локального сервера Macroscop, например http://10.1.60.188:8080. Пароль в URL не вставляй.");
        Row(connection, "Логин", login, "Учётная запись для чтения видеопотока камеры.");
        Row(connection, "Пароль / MD5", password,
            "Оставь пустым для сохранения текущего секрета. Новый пароль защищается средствами Windows DPAPI.");
        Hint(connection, "Канал: " + Paths.Channel + ". Используется один сетевой MJPEG-поток.");

        var stream = Section(stack, "Качество потока");
        videoPreset.Items.AddRange(VideoProfiles.Names);
        videoPreset.SelectedItem = VideoProfiles.Names.Contains(work.VideoPreset) ? work.VideoPreset : VideoProfiles.Economy;
        videoDetails.Text = VideoProfiles.Describe(work.VideoPreset);
        videoPreset.SelectedIndexChanged += (_, _) =>
            videoDetails.Text = VideoProfiles.Describe(videoPreset.SelectedItem?.ToString() ?? VideoProfiles.Economy);
        Row(stream, "Профиль видео", videoPreset,
            "Определяет запрашиваемое разрешение, FPS и JPEG-качество от Macroscop.");
        Row(stream, "Параметры", videoDetails,
            "Фактическая частота и разрешение зависят от сервера и пропускной способности.");

        var roi = Section(stack, "Зона лестницы и направление");
        zone.Text = RegionGeometry.Resolve(work) is not null && work.ArrowFrom.HasValue && work.ArrowTo.HasValue
            ? "Полигон и стрелка сохранены" : "Не задано — настрой перед запуском";
        Row(roi, "Граница и стрелка", zone,
            "Триггер возможен, когда центр распознанного человека попадает внутрь полигона и перемещается вдоль стрелки.");
        Row(roi, "Редактор", Buttons(select, previewButton),
            "Обведи лестницу любым многоугольником и укажи стрелкой направление подъёма.");
        var live = new Button { Text = "Открыть живое видео", AutoSize = true };
        live.Click += (_, _) => openPreview();
        Row(roi, "Предпросмотр", live,
            "Показывает текущие кадры из уже открытого потока. Не открывает второе сетевое подключение.");
    }

    void BuildDetection(TableLayoutPanel stack)
    {
        var yolo = Section(stack, "Поиск людей YOLO");
        foreach (double rate in AnalysisRates.Values) analysisRate.Items.Add(AnalysisRates.Label(rate));
        analysisRate.SelectedIndex = Math.Max(0, Array.IndexOf(AnalysisRates.Values, AnalysisRates.Normalize(work.AnalysisFps)));
        motionGate.Checked = work.MotionGateEnabled;
        conf.Value = Math.Clamp(work.ConfidencePercent, (int)conf.Minimum, (int)conf.Maximum);
        Row(yolo, "Анализ кадров", analysisRate,
            "Сколько кадров в секунду отдаётся модели YOLO, независимо от FPS камеры. Больше — лучше захват быстрых проходов, выше нагрузка.");
        Row(yolo, "Порог уверенности, %", conf,
            "Минимальная уверенность YOLO для класса person. Ниже — больше обнаружений и возможных ложных срабатываний.");
        Row(yolo, "Фильтр движения", motionGate,
            "Экономит CPU, но может пропускать человека при слабом движении. Для диагностики лучше временно отключить.");

        var track = Section(stack, "Подтверждение движения по лестнице");
        minHits.Value = Math.Clamp(work.TrackerMinHits, (int)minHits.Minimum, (int)minHits.Maximum);
        travel.Value = Math.Clamp(work.TrackerTravelPixels, (int)travel.Minimum, (int)travel.Maximum);
        matchRadius.Value = Math.Clamp(work.TrackerMatchPixels, (int)matchRadius.Minimum, (int)matchRadius.Maximum);
        Row(track, "Подтверждений трека", minHits,
            "Количество последовательных распознаваний человека. Меньше — быстрее реакция, но больше ложных тревог.");
        Row(track, "Минимальное смещение", travel,
            "Продвижение центра человека вдоль стрелки в пикселях при базовой ширине 640. Не длина всего пути по лестнице.");
        Row(track, "Радиус сопоставления", matchRadius,
            "Максимальный скачок между кадрами для одного трека (масштабируется относительно 640 px). При быстром шаге увеличь.");
        Row(track, "Повторное оповещение", cooldown,
            "Минимальный интервал в секундах между автоматическими событиями, даже если новый человек обнаружен.");
    }

    void BuildNotifications(TableLayoutPanel stack)
    {
        var sounds = Section(stack, "Звук Windows-уведомления");
        soundMode.Items.AddRange(NotificationAudio.Modes);
        soundMode.SelectedItem = NotificationAudio.Normalize(work.NotificationSound);
        soundPath.Text = work.CustomSoundPath;
        Row(sounds, "Режим звука", soundMode,
            "Стандартный звук Toast, системные звуки Windows (включая ошибку), свой аудиофайл или полная тишина.");
        Row(sounds, "Свой аудиофайл", soundPath,
            "WAV, MP3, WMA, M4A, AIF, AIFF; до 20 МБ. При сохранении файл копируется в папку StairGuard/Audio.");
        Row(sounds, "Действия", Buttons(browseSound, previewSound),
            "Прослушивание проверяет только звук. Фактическое Windows-уведомление проверяется отдельной кнопкой ниже.");
        UpdateSoundControls();

        var verify = Section(stack, "Проверка оповещения");
        Row(verify, "Ручной тест", manualTriggerButton,
            "Проверяет запись JPEG, журнал посещений и системный Toast с картинкой. Новый звук применяется после сохранения.");
        Hint(verify, "В настоящем оповещении выводятся StairGuard, «Подъём по лестнице», время и уверенность. Без фразы «Человек обнаружен».");
    }

    void BuildSystem(TableLayoutPanel stack)
    {
        var startup = Section(stack, "Запуск и диагностика");
        autostart.Checked = Settings.Autostart();
        debugYolo.Checked = work.DebugYolo;
        Row(startup, "Автозапуск", autostart,
            "Автоматически запускать StairGuard при входе текущего пользователя Windows.");
        Row(startup, "Диагностика YOLO", debugYolo,
            "Пишет в технический журнал кадры, YOLO, Motion Gate, ROI, треки и причины несрабатывания. Может быстро увеличить размер журнала.");

        var logs = Section(stack, "Журналы и события");
        var history = new Button { Text = "Журнал посещений с фото", AutoSize = true };
        var tech = new Button { Text = "Технический журнал", AutoSize = true };
        history.Click += (_, _) => openHistory();
        tech.Click += (_, _) => openJournal();
        Row(logs, "Посмотреть", Buttons(history, tech),
            "Посещения хранятся в triggers.log и имеют изображения; диагностика и ошибки — в stairguard.log.");
        Hint(logs, "Файлы размещаются в %LOCALAPPDATA%\\StairGuard. Изменения настроек вступают в силу после «Сохранить и запустить».");
    }

    void UpdateSoundControls()
    {
        bool custom = string.Equals(soundMode.SelectedItem?.ToString(), NotificationAudio.Custom, StringComparison.Ordinal);
        browseSound.Enabled = custom;
        soundPath.Enabled = custom;
        previewSound.Enabled = true;
    }

    void ChooseSound()
    {
        using var chooser = new OpenFileDialog
        {
            Title = "Выбери звук для StairGuard",
            Filter = "Аудиофайлы|*.wav;*.mp3;*.wma;*.m4a;*.aif;*.aiff|Все файлы|*.*",
            CheckFileExists = true, Multiselect = false
        };
        if (chooser.ShowDialog(this) != DialogResult.OK) return;
        if (!NotificationAudio.IsSupportedFile(chooser.FileName))
        {
            MessageBox.Show("Поддерживаются WAV, MP3, WMA, M4A, AIF и AIFF.", "StairGuard",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        soundPath.Text = chooser.FileName;
    }

    void PreviewSound()
    {
        var mode = soundMode.SelectedItem?.ToString() ?? NotificationAudio.Default;
        if (mode == NotificationAudio.Custom && !File.Exists(soundPath.Text))
        {
            MessageBox.Show("Сначала выбери существующий аудиофайл.", "StairGuard");
            return;
        }
        NotificationAudio.Play(mode, soundPath.Text);
    }

    void FitToScreen()
    {
        var area = Screen.FromControl(this).WorkingArea;
        int w = Math.Min(Width, Math.Max(600, area.Width - 48));
        int h = Math.Min(Height, Math.Max(470, area.Height - 48));
        if (Width == w && Height == h) return;
        MinimumSize = new Size(Math.Min(630, w), Math.Min(485, h));
        Size = new Size(w, h);
        CenterToScreen();
    }

    bool ApplyCredentials()
    {
        work.Server = server.Text.Trim();
        work.Login = login.Text.Trim();
        work.VideoPreset = videoPreset.SelectedItem?.ToString() ?? VideoProfiles.Economy;
        VideoProfiles.Apply(work);
        work.AnalysisFps = AnalysisRates.Values[Math.Max(0, analysisRate.SelectedIndex)];
        work.MotionGateEnabled = motionGate.Checked;
        work.DebugYolo = debugYolo.Checked;
        work.ConfidencePercent = (int)conf.Value;
        work.TrackerMinHits = (int)minHits.Value;
        work.TrackerTravelPixels = (int)travel.Value;
        work.TrackerMatchPixels = (int)matchRadius.Value;
        work.CooldownSeconds = (int)cooldown.Value;

        if (string.IsNullOrWhiteSpace(work.Server) || string.IsNullOrWhiteSpace(work.Login))
        {
            MessageBox.Show("Укажи адрес сервера и логин."); return false;
        }
        if (password.Text.Length > 0)
        {
            work.Credential = Vault.Seal(password.Text);
            password.Clear();
        }
        if (string.IsNullOrWhiteSpace(work.Credential))
        {
            MessageBox.Show("Введи пароль Macroscop."); return false;
        }
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
        catch (Exception ex)
        {
            MessageBox.Show("Не удалось получить кадр: " + ex.GetType().Name, "StairGuard");
        }
        finally { previewButton.Enabled = true; }
    }

    async Task SelectRegionAsync()
    {
        if (!ApplyCredentials()) return;
        select.Enabled = false;
        select.Text = "Получаю кадр…";
        try
        {
            using var frame = await Mjpeg.Grab(work, CancellationToken.None);
            using var picker = new RegionPicker(new Bitmap(frame), RegionGeometry.Resolve(work), work.ArrowFrom, work.ArrowTo);
            if (picker.ShowDialog(this) == DialogResult.OK)
            {
                work.RegionPolygon = picker.Selection;
                work.Region = null;
                work.ArrowFrom = picker.From;
                work.ArrowTo = picker.To;
                zone.Text = "Контур и стрелка сохранены";
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show("Не удалось получить кадр. Проверь адрес, логин и пароль.\n" + ex.GetType().Name,
                "StairGuard", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally { select.Enabled = true; select.Text = "Изменить контур и направление"; }
    }

    void SaveClicked()
    {
        if (!ApplyCredentials()) return;
        if (!work.Ready)
        {
            MessageBox.Show("Сначала выбери область лестницы и направление движения."); return;
        }

        work.NotificationSound = NotificationAudio.Normalize(soundMode.SelectedItem?.ToString());
        if (work.NotificationSound == NotificationAudio.Custom)
        {
            try { work.CustomSoundPath = NotificationAudio.ImportCustomSound(soundPath.Text); }
            catch (Exception ex)
            {
                MessageBox.Show("Не удалось сохранить звук:\n" + ex.Message,
                    "StairGuard", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
        }
        DialogResult = DialogResult.OK;
        Close();
    }
}
