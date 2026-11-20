using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using CncHmiDemo.Models;
using Drawing = System.Drawing;

namespace CncHmiDemo.Services;

/// <summary>使用 GDI+ 将切割路径渲染为位图，供 WPF 路径预览界面展示。</summary>
public static class GdiPathRenderer
{
    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);

    public static BitmapSource Render(
        IReadOnlyList<PathPoint> points,
        PathPoint tool,
        double progressPercent,
        int width,
        int height)
    {
        using var bitmap = new Drawing.Bitmap(width, height, PixelFormat.Format32bppArgb);
        using var graphics = Drawing.Graphics.FromImage(bitmap);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        graphics.Clear(Drawing.Color.FromArgb(13, 21, 36));

        // 视口坐标映射（mm）
        const double minX = 0;
        const double maxX = 340;
        const double minY = 0;
        const double maxY = 260;
        const float padLeft = 52;
        const float padTop = 60;
        const float padRight = 26;
        const float padBottom = 48;
        var plotWidth = width - padLeft - padRight;
        var plotHeight = height - padTop - padBottom;
        var scale = Math.Min(plotWidth / (maxX - minX), plotHeight / (maxY - minY));
        var originX = padLeft + (plotWidth - (maxX - minX) * scale) / 2;
        var originY = padTop + (plotHeight - (maxY - minY) * scale) / 2;

        Drawing.PointF Map(PathPoint p) =>
            new((float)(originX + (p.X - minX) * scale), (float)(originY + (maxY - p.Y) * scale));

        // 网格
        using var gridPen = new Drawing.Pen(Drawing.Color.FromArgb(30, 46, 70), 1f);
        using var gridStrongPen = new Drawing.Pen(Drawing.Color.FromArgb(42, 62, 92), 1f);
        for (var mm = 0; mm <= (int)maxX; mm += 20)
        {
            var x = Map(new PathPoint(mm, 0)).X;
            graphics.DrawLine(mm % 100 == 0 ? gridStrongPen : gridPen, x, padTop, x, height - padBottom);
        }
        for (var mm = 0; mm <= (int)maxY; mm += 20)
        {
            var y = Map(new PathPoint(0, mm)).Y;
            graphics.DrawLine(mm % 100 == 0 ? gridStrongPen : gridPen, padLeft, y, width - padRight, y);
        }

        using var axisPen = new Drawing.Pen(Drawing.Color.FromArgb(80, 110, 150), 1.2f);
        graphics.DrawLine(axisPen, padLeft, height - padBottom, width - padRight, height - padBottom);
        graphics.DrawLine(axisPen, padLeft, padTop, padLeft, height - padBottom);

        using var labelFont = new Drawing.Font("Microsoft YaHei UI", 9f);
        using var smallFont = new Drawing.Font("Microsoft YaHei UI", 8.5f);
        using var titleFont = new Drawing.Font("Microsoft YaHei UI", 11f, Drawing.FontStyle.Bold);
        using var mutedBrush = new Drawing.SolidBrush(Drawing.Color.FromArgb(122, 145, 176));
        using var titleBrush = new Drawing.SolidBrush(Drawing.Color.FromArgb(228, 236, 248));
        using var accentBrush = new Drawing.SolidBrush(Drawing.Color.FromArgb(47, 168, 255));
        using var warnBrush = new Drawing.SolidBrush(Drawing.Color.FromArgb(255, 176, 32));
        using var greenBrush = new Drawing.SolidBrush(Drawing.Color.FromArgb(55, 211, 165));

        graphics.DrawString("GDI+ 切割路径预览", titleFont, titleBrush, padLeft, 8);
        graphics.DrawString($"刀具坐标  X {tool.X,6:F1}   Y {tool.Y,6:F1}  mm", labelFont, accentBrush, padLeft, 32);
        graphics.DrawString($"进度 {progressPercent:F1}%", labelFont, titleBrush, width - 112, 10);

        // 坐标刻度
        for (var mm = 0; mm <= 300; mm += 100)
        {
            var p = Map(new PathPoint(mm, 0));
            graphics.DrawString($"{mm}", labelFont, mutedBrush, p.X - 8, height - padBottom + 6);
        }
        for (var mm = 0; mm <= 200; mm += 100)
        {
            var q = Map(new PathPoint(0, mm));
            graphics.DrawString($"{mm}", labelFont, mutedBrush, 14, q.Y - 7);
        }
        graphics.DrawString("X (mm)", labelFont, mutedBrush, width - padRight - 46, height - 26);

        // 路径：绿色已切割 / 蓝色待切割 / 虚线空移
        var lengths = new double[points.Count];
        for (var i = 1; i < points.Count; i++)
        {
            lengths[i] = lengths[i - 1] + Distance(points[i - 1], points[i]);
        }
        var cutLength = lengths[^1] * Math.Clamp(progressPercent, 0, 100) / 100.0;

        using var pathPen = new Drawing.Pen(Drawing.Color.FromArgb(47, 106, 160), 2.2f);
        using var cutPen = new Drawing.Pen(Drawing.Color.FromArgb(55, 211, 165), 2.8f);
        using var travelPen = new Drawing.Pen(Drawing.Color.FromArgb(72, 104, 140), 1.3f)
        {
            DashStyle = DashStyle.Dash
        };

        for (var i = 1; i < points.Count; i++)
        {
            var a = Map(points[i - 1]);
            var b = Map(points[i]);
            if (SamplePath.IsTravelSegment(i))
            {
                graphics.DrawLine(travelPen, a, b);
                continue;
            }

            var pen = lengths[i] <= cutLength ? cutPen : pathPen;
            graphics.DrawLine(pen, a, b);
        }

        // 起点 / 终点
        using var startBrush = new Drawing.SolidBrush(Drawing.Color.FromArgb(55, 211, 165));
        using var endBrush = new Drawing.SolidBrush(Drawing.Color.FromArgb(255, 92, 92));
        var startPoint = Map(points[0]);
        var endPoint = Map(points[^1]);
        graphics.FillEllipse(startBrush, startPoint.X - 4, startPoint.Y - 4, 8, 8);
        graphics.FillEllipse(endBrush, endPoint.X - 4, endPoint.Y - 4, 8, 8);

        // 刀具标记
        var toolPoint = Map(tool);
        using var toolPen = new Drawing.Pen(Drawing.Color.FromArgb(255, 176, 32), 1.5f);
        graphics.DrawEllipse(toolPen, toolPoint.X - 9, toolPoint.Y - 9, 18, 18);
        graphics.DrawLine(toolPen, toolPoint.X - 15, toolPoint.Y, toolPoint.X + 15, toolPoint.Y);
        graphics.DrawLine(toolPen, toolPoint.X, toolPoint.Y - 15, toolPoint.X, toolPoint.Y + 15);
        graphics.DrawString("刀具", smallFont, warnBrush, toolPoint.X + 14, toolPoint.Y + 8);

        // 图例（右上角，避免与 X 轴刻度重叠）
        var legendX = width - 330f;
        const float legendY = 36f;
        graphics.DrawLine(cutPen, legendX, legendY, legendX + 16, legendY);
        graphics.DrawString("已切割", smallFont, mutedBrush, legendX + 20, legendY - 8);
        legendX += 72;
        graphics.DrawLine(pathPen, legendX, legendY, legendX + 16, legendY);
        graphics.DrawString("待切割", smallFont, mutedBrush, legendX + 20, legendY - 8);
        legendX += 72;
        graphics.DrawLine(travelPen, legendX, legendY, legendX + 16, legendY);
        graphics.DrawString("空移", smallFont, mutedBrush, legendX + 20, legendY - 8);

        var hBitmap = bitmap.GetHbitmap();
        try
        {
            var source = Imaging.CreateBitmapSourceFromHBitmap(
                hBitmap,
                IntPtr.Zero,
                Int32Rect.Empty,
                BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            return source;
        }
        finally
        {
            DeleteObject(hBitmap);
        }
    }

    private static double Distance(PathPoint a, PathPoint b)
        => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));
}
