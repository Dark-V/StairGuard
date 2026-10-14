using System.Drawing;
using System.Text.Json;
using StairGuard;

static void Check(bool result, string message)
{
    if (!result) throw new Exception(message);
}
var concave = new List<PointF>
{
    new(.1f, .1f), new(.9f, .1f), new(.9f, .3f),
    new(.3f, .3f), new(.3f, .9f), new(.1f, .9f)
};
Check(RegionGeometry.IsValid(concave), "Valid concave zone rejected");
Check(RegionGeometry.Contains(concave, .2f, .8f), "Inside leg excluded");
Check(RegionGeometry.Contains(concave, .8f, .2f), "Inside top excluded");
Check(!RegionGeometry.Contains(concave, .8f, .8f), "Concave notch included");
Check(RegionGeometry.Contains(concave, .1f, .5f), "Boundary excluded");
var legacy = new Settings { Region = new RectangleF(.2f, .3f, .5f, .4f) };
var legacyPolygon = RegionGeometry.Resolve(legacy);
Check(legacyPolygon is not null && legacyPolygon.Count == 4, "Legacy rectangle failed");
Check(RegionGeometry.Contains(legacyPolygon!, .6f, .6f), "Legacy inside failed");
Check(!RegionGeometry.Contains(legacyPolygon!, .1f, .1f), "Legacy outside failed");
Check(!RegionGeometry.IsValid(new List<PointF>
{
    new(.1f, .1f), new(.9f, .9f), new(.1f, .9f), new(.9f, .1f)
}), "Self intersection must fail");
var json = JsonSerializer.Serialize(new Settings { RegionPolygon = concave });
var loaded = JsonSerializer.Deserialize<Settings>(json)!;
Check(loaded.RegionPolygon is not null &&
      RegionGeometry.IsValid(loaded.RegionPolygon) &&
      !RegionGeometry.Contains(loaded.RegionPolygon, .8f, .8f), "Polygon JSON roundtrip failed");
// Reproduce the 14:16:37 camera case: centers jumped ~83px between
// YOLO samples. The old 70px gate silently created a new track each time.
var first = new PointF(478, 201);
var second = new PointF(534, 140);
Check(!TrackerMatching.CanMatch(first, second, 640, 70),
    "Legacy association gate should reject the sampled 83px jump");
Check(TrackerMatching.CanMatch(first, second, 640, 140),
    "Updated tracker radius must preserve the observed movement track");
Check(TrackerMatching.CanMatch(
    new PointF(first.X * 2, first.Y * 2),
    new PointF(second.X * 2, second.Y * 2), 1280, 140),
    "Association radius must scale with stream resolution");
Check(!TrackerMatching.CanMatch(first, new PointF(620, 320), 640, 140),
    "Association must reject distant unrelated tracks");
Console.WriteLine("Tracker association regression: PASS");
Console.WriteLine("Polygon ROI geometry tests: PASS");
