using System.Drawing;
namespace StairGuard;
internal sealed class Settings
{
    public RectangleF? Region { get; set; }
    public List<PointF>? RegionPolygon { get; set; }
}
