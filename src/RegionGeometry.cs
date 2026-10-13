using System.Drawing;

namespace StairGuard;

// Coordinates of the user-drawn zone are normalized to the video frame (0..1).
// Supports concave polygons as well as the rectangle stored by older versions.
internal static class RegionGeometry
{
    public static List<PointF>? Resolve(Settings settings)
    {
        if (IsValid(settings.RegionPolygon))
            return new List<PointF>(settings.RegionPolygon!);

        if (settings.Region is not RectangleF r || r.Width <= 0 || r.Height <= 0 ||
            r.Left < 0 || r.Top < 0 || r.Right > 1 || r.Bottom > 1)
            return null;

        var vertices = new List<PointF>
        {
            new(r.Left, r.Top), new(r.Right, r.Top),
            new(r.Right, r.Bottom), new(r.Left, r.Bottom)
        };
        return IsValid(vertices) ? vertices : null;
    }

    public static bool IsValid(IReadOnlyList<PointF>? points)
    {
        if (points is null || points.Count < 3 || points.Count > 128) return false;
        double twiceArea = 0;
        for (int i = 0; i < points.Count; ++i)
        {
            var a = points[i];
            var b = points[(i + 1) % points.Count];
            if (!float.IsFinite(a.X) || !float.IsFinite(a.Y) ||
                a.X < 0 || a.X > 1 || a.Y < 0 || a.Y > 1)
                return false;
            twiceArea += (double)a.X * b.Y - (double)b.X * a.Y;
        }
        return Math.Abs(twiceArea) >= 0.0002;
    }

    public static RectangleF Bounds(IReadOnlyList<PointF> polygon)
    {
        float minX = 1, minY = 1, maxX = 0, maxY = 0;
        foreach (PointF p in polygon)
        {
            minX = Math.Min(minX, p.X);
            minY = Math.Min(minY, p.Y);
            maxX = Math.Max(maxX, p.X);
            maxY = Math.Max(maxY, p.Y);
        }
        return RectangleF.FromLTRB(minX, minY, maxX, maxY);
    }

    public static bool Contains(IReadOnlyList<PointF> polygon, float x, float y)
    {
        if (polygon.Count < 3) return false;
        bool inside = false;
        for (int i = 0, j = polygon.Count - 1; i < polygon.Count; j = i++)
        {
            var a = polygon[j];
            var b = polygon[i];
            // Include points on the outline so no gaps appear along the edges.
            float cross = (x - a.X) * (b.Y - a.Y) - (y - a.Y) * (b.X - a.X);
            if (Math.Abs(cross) < 0.000005f &&
                x >= Math.Min(a.X, b.X) - 0.00001f && x <= Math.Max(a.X, b.X) + 0.00001f &&
                y >= Math.Min(a.Y, b.Y) - 0.00001f && y <= Math.Max(a.Y, b.Y) + 0.00001f)
                return true;

            if ((a.Y > y) != (b.Y > y) &&
                x < (b.X - a.X) * (y - a.Y) / (b.Y - a.Y) + a.X)
                inside = !inside;
        }
        return inside;
    }
}
