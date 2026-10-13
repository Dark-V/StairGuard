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
Console.WriteLine("Polygon ROI geometry tests: PASS");
