using System.Drawing;

namespace StairGuard;

// Separate geometry from YOLO inference so association regressions can be unit-tested.
internal static class TrackerMatching
{
    public static float Radius(int frameWidth, int configuredPixels) =>
        Math.Clamp(configuredPixels, 70, 250) * frameWidth / 640f;

    public static float DistanceSquared(PointF a, PointF b)
    {
        float dx = a.X - b.X;
        float dy = a.Y - b.Y;
        return dx * dx + dy * dy;
    }

    public static bool CanMatch(PointF a, PointF b, int frameWidth, int configuredPixels)
    {
        float radius = Radius(frameWidth, configuredPixels);
        return DistanceSquared(a, b) < radius * radius;
    }
}
