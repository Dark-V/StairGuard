using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;

namespace StairGuard;

internal sealed class TriggerHistoryForm : Form
{
    readonly ListView events = new()
    {
        Dock = DockStyle.Fill,
        View = View.Details,
        FullRowSelect = true,
        HideSelection = false,
        MultiSelect = false,
        BorderStyle = BorderStyle.None
    };

    readonly PictureBox photo = new()
    {
        Dock = DockStyle.Fill,
        BackColor = Color.FromArgb(12, 16, 22),
        SizeMode = PictureBoxSizeMode.Zoom
    };

    readonly Label details = new()
    {
        Dock = DockStyle.Top,
        Height = 78,
        Padding = new Padding(16, 12, 16, 8),
        Font = new Font("Segoe UI", 10),
        ForeColor = Color.White,
        BackColor = Color.FromArgb(29, 39, 53)
    };

    readonly Label empty = new()
    {
        Dock = DockStyle.Fill,
        Text = "Срабатываний пока нет",
        TextAlign = ContentAlignment.MiddleCenter,
        Font = new Font("Segoe UI", 14),
        ForeColor = Color.DimGray
    };

    public TriggerHistoryForm()
    {
        Text = "StairGuard — журнал посещений";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(820, 500);
        ClientSize = new Size(1040, 650);
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = new Font("Segoe UI", 10);
        BackColor = Color.White;

        events.Columns.Add("Дата и время", 170);
        events.Columns.Add("Тип", 105);
        events.Columns.Add("Уверенность", 110);
        events.SelectedIndexChanged += (_, _) => ShowSelected();
        events.DoubleClick += (_, _) => OpenSelectedImage();

        var leftHeader = new Label
        {
            Text = "События",
            Dock = DockStyle.Top,
            Height = 46,
            Padding = new Padding(12, 12, 0, 0),
            Font = new Font("Segoe UI", 12, FontStyle.Bold),
            BackColor = Color.FromArgb(245, 247, 250)
        };

        var split = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Vertical,
            SplitterDistance = 390,
            FixedPanel = FixedPanel.Panel1
        };
        split.Panel1.Controls.Add(events);
        split.Panel1.Controls.Add(leftHeader);

        var photoHost = new Panel { Dock = DockStyle.Fill, BackColor = photo.BackColor };
        photoHost.Controls.Add(photo);
        photoHost.Controls.Add(empty);
        empty.BringToFront();

        split.Panel2.Controls.Add(photoHost);
        split.Panel2.Controls.Add(details);

        var refresh = new Button { Text = "Обновить", AutoSize = true };
        var openLog = new Button { Text = "Открыть triggers.log", AutoSize = true };
        var openFolder = new Button { Text = "Папка снимков", AutoSize = true };
        refresh.Click += (_, _) => Reload();
        openLog.Click += (_, _) => OpenPath(Paths.TriggersJournal);
        openFolder.Click += (_, _) => OpenPath(Path.Combine(Paths.Home, "Triggers"));

        var bar = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 52,
            Padding = new Padding(10, 8, 10, 6),
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            BackColor = Color.FromArgb(245, 247, 250)
        };
        bar.Controls.Add(refresh);
        bar.Controls.Add(openLog);
        bar.Controls.Add(openFolder);

        Controls.Add(split);
        Controls.Add(bar);
        Shown += (_, _) => Reload();
        FormClosed += (_, _) => SetPhoto(null);
    }

    void Reload()
    {
        var selectedPath = events.SelectedItems.Count == 1
            ? (events.SelectedItems[0].Tag as TriggerEntry)?.ImagePath
            : null;

        events.BeginUpdate();
        try
        {
            events.Items.Clear();
            foreach (var entry in TriggerHistory.ReadRecent(500))
            {
                var item = new ListViewItem(entry.Time.ToString("dd.MM.yyyy HH:mm:ss"));
                item.SubItems.Add(entry.IsTest ? "ТЕСТ" : "Посещение");
                item.SubItems.Add(entry.Confidence.ToString("P0"));
                item.Tag = entry;
                events.Items.Add(item);
                if (selectedPath != null &&
                    string.Equals(entry.ImagePath, selectedPath, StringComparison.OrdinalIgnoreCase))
                    item.Selected = true;
            }
        }
        finally { events.EndUpdate(); }

        if (events.SelectedItems.Count == 0 && events.Items.Count > 0)
            events.Items[0].Selected = true;
        else if (events.Items.Count == 0)
        {
            details.Text = "Нет сохранённых событий.";
            SetPhoto(null);
        }
    }

    void ShowSelected()
    {
        if (events.SelectedItems.Count != 1 ||
            events.SelectedItems[0].Tag is not TriggerEntry entry)
        {
            details.Text = "";
            SetPhoto(null);
            return;
        }

        details.Text =
            $"{(entry.IsTest ? "Тестовый триггер" : "Посещение")}\r\n" +
            $"{entry.Time:dd.MM.yyyy HH:mm:ss}    Уверенность: {entry.Confidence:P0}";

        SetPhoto(entry.ImagePath);
    }

    void SetPhoto(string? path)
    {
        var old = photo.Image;
        photo.Image = null;
        old?.Dispose();

        bool valid = path != null && File.Exists(path);
        empty.Visible = !valid;
        photo.Visible = valid;
        if (!valid)
        {
            empty.Text = path == null ? "Снимок для этого события отсутствует" : "Файл снимка не найден";
            return;
        }

        try
        {
            using var file = File.OpenRead(path!);
            using var image = Image.FromStream(file);
            photo.Image = new Bitmap(image);
        }
        catch (Exception ex)
        {
            empty.Visible = true;
            photo.Visible = false;
            empty.Text = "Не удалось открыть снимок";
            Log.Write($"Просмотр снимка журнала: {ex}");
        }
    }

    void OpenSelectedImage()
    {
        if (events.SelectedItems.Count == 1 &&
            events.SelectedItems[0].Tag is TriggerEntry entry &&
            entry.ImagePath != null && File.Exists(entry.ImagePath))
            OpenPath(entry.ImagePath);
    }

    static void OpenPath(string path)
    {
        try
        {
            if (Directory.Exists(path) || File.Exists(path))
                Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
        }
        catch (Exception ex) { Log.Write($"Открытие {path}: {ex}"); }
    }
}
