namespace CncHmiDemo.Models;

public readonly record struct PathPoint(double X, double Y);

/// <summary>
/// 示例切割路径：外轮廓 + 两个圆孔（单位 mm）。
/// 下位机模拟器、GDI+ 路径预览与刀具位置共用同一份路径数据；
/// 轮廓之间的连接段标记为空移（快速移动），不参与切割。
/// </summary>
public static class SamplePath
{
    private static readonly HashSet<int> TravelSegmentEndIndices = BuildTravelIndices();

    public static readonly IReadOnlyList<PathPoint> Points = Build();
    public static readonly double TotalLength = ComputeLength(Points);

    public static bool IsTravelSegment(int endPointIndex) => TravelSegmentEndIndices.Contains(endPointIndex);

    public static PathPoint At(double progressPercent)
    {
        if (Points.Count < 2) return new PathPoint(0, 0);
        var distance = Math.Clamp(progressPercent, 0, 100) / 100.0 * TotalLength;
        for (var i = 1; i < Points.Count; i++)
        {
            var a = Points[i - 1];
            var b = Points[i];
            var len = Distance(a, b);
            if (len <= 0.0001) continue;
            if (distance <= len)
            {
                var t = distance / len;
                return new PathPoint(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t);
            }
            distance -= len;
        }
        return Points[^1];
    }

    private static HashSet<int> BuildTravelIndices()
    {
        // 外轮廓 0~4，随后空移到第一个圆孔起点（索引 5）；
        // 第一个圆孔 5~45，再空移到第二个圆孔起点（索引 46）。
        return new HashSet<int> { 5, 46 };
    }

    private static List<PathPoint> Build()
    {
        var points = new List<PathPoint>
        {
            new(40, 40),
            new(300, 40),
            new(300, 220),
            new(40, 220),
            new(40, 40)
        };

        AddCircle(points, 120, 130, 26);
        AddCircle(points, 220, 130, 26);
        return points;
    }

    private static void AddCircle(List<PathPoint> list, double cx, double cy, double r)
    {
        const int segments = 40;
        for (var i = 0; i <= segments; i++)
        {
            var angle = Math.PI * 2 * i / segments;
            list.Add(new PathPoint(cx + Math.Cos(angle) * r, cy + Math.Sin(angle) * r));
        }
    }

    private static double Distance(PathPoint a, PathPoint b)
        => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

    private static double ComputeLength(IReadOnlyList<PathPoint> points)
    {
        var total = 0.0;
        for (var i = 1; i < points.Count; i++) total += Distance(points[i - 1], points[i]);
        return total;
    }
}
