using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace StairGuard;

internal record Person(RectangleF Bounds, float Confidence)
{
    public PointF Center => new(Bounds.Left + Bounds.Width / 2, Bounds.Top + Bounds.Height / 2);
}

internal sealed class Detector : IDisposable
{
    const int Size = 640;
    readonly InferenceSession session;
    readonly string inputName;
    readonly string outputName;

    public Detector(string model)
    {
        if (!File.Exists(model)) throw new FileNotFoundException("Отсутствует yolov8n.onnx. Запусти Build-and-Install.ps1", model);
        var options = new SessionOptions();
        options.IntraOpNumThreads = 2;
        options.InterOpNumThreads = 1;
        options.GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL;
        session = new InferenceSession(model, options);
        inputName = session.InputMetadata.Keys.First();
        outputName = session.OutputMetadata.Keys.First();
    }

    public List<Person> Find(Bitmap frame, float threshold)
    {
        float scale = Math.Min((float)Size / frame.Width, (float)Size / frame.Height);
        int dstW = Math.Max(1, (int)Math.Round(frame.Width * scale));
        int dstH = Math.Max(1, (int)Math.Round(frame.Height * scale));
        int padX = (Size - dstW) / 2;
        int padY = (Size - dstH) / 2;
        using var bmp = new Bitmap(Size, Size, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.FromArgb(114, 114, 114));
            g.InterpolationMode = InterpolationMode.Bilinear;
            g.DrawImage(frame, padX, padY, dstW, dstH);
        }

        var data = bmp.LockBits(new Rectangle(0, 0, Size, Size), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        float[] planar = new float[3 * Size * Size];
        try
        {
            byte[] pixels = new byte[data.Stride * Size];
            Marshal.Copy(data.Scan0, pixels, 0, pixels.Length);
            int plane = Size * Size;
            for (int y = 0; y < Size; y++)
            for (int x = 0; x < Size; x++)
            {
                int src = y * data.Stride + 4 * x;
                int dst = y * Size + x;
                planar[dst] = pixels[src + 2] / 255f;
                planar[plane + dst] = pixels[src + 1] / 255f;
                planar[2 * plane + dst] = pixels[src] / 255f;
            }
        }
        finally { bmp.UnlockBits(data); }

        var tensor = new DenseTensor<float>(planar, new[] { 1, 3, Size, Size });
        using var output = session.Run(new[] { NamedOnnxValue.CreateFromTensor(inputName, tensor) });
        var result = output.First(o => o.Name == outputName).AsTensor<float>();
        int[] dims = result.Dimensions.ToArray();
        if (dims.Length != 3 || dims[0] != 1) throw new InvalidDataException("Неожиданный формат выходного тензора YOLO");
        bool channelsFirst = dims[1] >= 84 && dims[1] <= 85 && dims[2] > dims[1];
        int features = channelsFirst ? dims[1] : dims[2];
        int anchors = channelsFirst ? dims[2] : dims[1];
        if (features != 84) throw new InvalidDataException($"Ожидался YOLOv8 COCO [1,84,N], получено [{string.Join(',', dims)}]");
        float[] arr = result.ToArray();
        float Val(int a, int feature) => channelsFirst ? arr[feature * anchors + a] : arr[a * features + feature];

        var persons = new List<Person>();
        for (int a = 0; a < anchors; a++)
        {
            // COCO category 0 = person; YOLOv8 has no objectness multiplier.
            float confidence = Val(a, 4);
            if (confidence < threshold) continue;
            float cx = Val(a, 0), cy = Val(a, 1), w = Val(a, 2), h = Val(a, 3);
            float left = Math.Clamp((cx - w / 2 - padX) / scale, 0, frame.Width);
            float top = Math.Clamp((cy - h / 2 - padY) / scale, 0, frame.Height);
            float right = Math.Clamp((cx + w / 2 - padX) / scale, 0, frame.Width);
            float bottom = Math.Clamp((cy + h / 2 - padY) / scale, 0, frame.Height);
            if (right - left < 8 || bottom - top < 20) continue;
            persons.Add(new Person(RectangleF.FromLTRB(left, top, right, bottom), confidence));
        }
        // Keep only strongest detections and suppress duplicate bounding boxes.
        var candidates = persons.OrderByDescending(p => p.Confidence).Take(40);
        var selected = new List<Person>();
        foreach (var person in candidates)
        {
            if (selected.All(other => IoU(person.Bounds, other.Bounds) < 0.45f)) selected.Add(person);
            if (selected.Count == 12) break;
        }
        return selected;
    }

    static float IoU(RectangleF a, RectangleF b)
    {
        float x1 = Math.Max(a.Left, b.Left), y1 = Math.Max(a.Top, b.Top);
        float x2 = Math.Min(a.Right, b.Right), y2 = Math.Min(a.Bottom, b.Bottom);
        float area = Math.Max(0, x2 - x1) * Math.Max(0, y2 - y1);
        return area / Math.Max(1, a.Width * a.Height + b.Width * b.Height - area);
    }
    public void Dispose() => session.Dispose();
}

internal sealed class MovementTracker
{
    sealed class Track
    {
        public PointF Origin;
        public PointF Last;
        public int Hits;
        public long LastTick;
        public bool Triggered;
    }
    readonly List<Track> tracks = new();
    long lastAlert;

    public Person? Update(IReadOnlyList<Person> detections, Settings settings, int width, int height)
    {
        long now = Environment.TickCount64;
        tracks.RemoveAll(t => now - t.LastTick > 4000);
        if (!settings.Region.HasValue || !settings.ArrowFrom.HasValue || !settings.ArrowTo.HasValue) return null;
        var r = settings.Region.Value;
        float ax = (settings.ArrowTo.Value.X - settings.ArrowFrom.Value.X) * width;
        float ay = (settings.ArrowTo.Value.Y - settings.ArrowFrom.Value.Y) * height;
        float length = MathF.Sqrt(ax * ax + ay * ay);
        if (length < 10) return null;
        ax /= length; ay /= length;
        var used = new HashSet<Track>();
        Person? alert = null;
        foreach (var person in detections)
        {
            PointF center = person.Center;
            if (center.X < r.Left * width || center.X > r.Right * width ||
                center.Y < r.Top * height || center.Y > r.Bottom * height) continue;
            Track? best = null;
            float distance = 70 * 70;
            foreach (Track tr in tracks)
            {
                if (used.Contains(tr)) continue;
                float dx = tr.Last.X - center.X, dy = tr.Last.Y - center.Y;
                float d = dx * dx + dy * dy;
                if (d < distance) { distance = d; best = tr; }
            }
            if (best == null)
            {
                best = new Track { Origin = center, Last = center, Hits = 1, LastTick = now };
                tracks.Add(best);
            }
            else
            {
                best.Last = center;
                best.Hits++;
                best.LastTick = now;
            }
            used.Add(best);
            float dx0 = center.X - best.Origin.X, dy0 = center.Y - best.Origin.Y;
            float forward = dx0 * ax + dy0 * ay;
            float lateral = MathF.Abs(-dx0 * ay + dy0 * ax);
            if (forward < -12)
            {
                best.Origin = center; best.Hits = 1; best.Triggered = false;
                continue;
            }
            // Person followed for 3+ samples, moved >=25 pixels in arrow direction.
            if (!best.Triggered && best.Hits >= 3 && forward >= 25 && lateral < forward * 1.25f &&
                (lastAlert == 0 || now - lastAlert >= settings.CooldownSeconds * 1000L))
            {
                lastAlert = now;
                best.Triggered = true;
                alert ??= person;
            }
        }
        return alert;
    }
}
