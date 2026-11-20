using System.Windows;
using System.Windows.Media;

namespace CncHmiDemo.Controls;

/// <summary>线程安全的环形缓冲，供实时曲线控件绘制。</summary>
public sealed class ChartBuffer
{
    private readonly double[] _speed;
    private readonly double[] _spindle;
    private readonly object _sync = new();
    private int _head;
    private int _count;

    public ChartBuffer(int capacity = 240)
    {
        Capacity = capacity;
        _speed = new double[capacity];
        _spindle = new double[capacity];
    }

    public int Capacity { get; }
    public event Action? Changed;

    public void Append(double speed, double spindle)
    {
        lock (_sync)
        {
            _speed[_head] = speed;
            _spindle[_head] = spindle;
            _head = (_head + 1) % Capacity;
            if (_count < Capacity) _count++;
        }
        Changed?.Invoke();
    }

    public (double[] Speed, double[] Spindle, int Count) Snapshot()
    {
        lock (_sync)
        {
            var speed = new double[_count];
            var spindle = new double[_count];
            var start = (_head - _count + Capacity) % Capacity;
            for (var i = 0; i < _count; i++)
            {
                var index = (start + i) % Capacity;
                speed[i] = _speed[index];
                spindle[i] = _spindle[index];
            }
            return (speed, spindle, _count);
        }
    }
}

/// <summary>WPF 自绘实时曲线：切割速度（左轴）与主轴转速（右轴）。</summary>
public sealed class RealtimeChart : FrameworkElement
{
    public static readonly DependencyProperty BufferProperty = DependencyProperty.Register(
        nameof(Buffer),
        typeof(ChartBuffer),
        typeof(RealtimeChart),
        new PropertyMetadata(null, OnBufferChanged));

    private bool _invalidatePending;

    public ChartBuffer? Buffer
    {
        get => (ChartBuffer?)GetValue(BufferProperty);
        set => SetValue(BufferProperty, value);
    }

    private static void OnBufferChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var chart = (RealtimeChart)d;
        if (e.OldValue is ChartBuffer oldBuffer) oldBuffer.Changed -= chart.OnBufferChanged;
        if (e.NewValue is ChartBuffer newBuffer) newBuffer.Changed += chart.OnBufferChanged;
        chart.InvalidateVisual();
    }

    private void OnBufferChanged()
    {
        if (_invalidatePending) return;
        _invalidatePending = true;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            _invalidatePending = false;
            InvalidateVisual();
        }), System.Windows.Threading.DispatcherPriority.Render);
    }

    protected override void OnRender(DrawingContext dc)
    {
        var width = ActualWidth;
        var height = ActualHeight;
        if (width <= 10 || height <= 10) return;

        var background = new SolidColorBrush(Color.FromRgb(13, 21, 36));
        background.Freeze();
        var borderPen = new Pen(new SolidColorBrush(Color.FromRgb(34, 49, 74)), 1);
        borderPen.Freeze();
        dc.DrawRoundedRectangle(background, borderPen, new Rect(0, 0, width, height), 8, 8);

        const double padLeft = 46;
        const double padRight = 46;
        const double padTop = 26;
        const double padBottom = 24;
        var plotWidth = width - padLeft - padRight;
        var plotHeight = height - padTop - padBottom;
        if (plotWidth <= 10 || plotHeight <= 10) return;

        var gridPen = new Pen(new SolidColorBrush(Color.FromRgb(25, 39, 60)), 1);
        gridPen.Freeze();
        for (var i = 0; i <= 4; i++)
        {
            var y = padTop + plotHeight * i / 4;
            dc.DrawLine(gridPen, new Point(padLeft, y), new Point(width - padRight, y));
        }
        for (var i = 0; i <= 8; i++)
        {
            var x = padLeft + plotWidth * i / 8;
            dc.DrawLine(gridPen, new Point(x, padTop), new Point(x, padTop + plotHeight));
        }

        var typeface = new Typeface(new FontFamily("Microsoft YaHei UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        var labelBrush = new SolidColorBrush(Color.FromRgb(110, 132, 162));
        labelBrush.Freeze();
        var accent = new SolidColorBrush(Color.FromRgb(47, 168, 255));
        accent.Freeze();
        var accent2 = new SolidColorBrush(Color.FromRgb(55, 211, 165));
        accent2.Freeze();
        var faint = new SolidColorBrush(Color.FromRgb(160, 178, 202));
        faint.Freeze();

        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        FormattedText Text(string text, Brush brush, double size = 10)
            => new(text, System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface, size, brush, dpi);

        // 图例
        dc.DrawRectangle(accent, null, new Rect(padLeft + 2, 8, 14, 3));
        dc.DrawText(Text("切割速度 mm/min", faint, 10), new Point(padLeft + 20, 2));
        dc.DrawRectangle(accent2, null, new Rect(padLeft + 158, 8, 14, 3));
        dc.DrawText(Text("主轴转速 rpm", faint, 10), new Point(padLeft + 176, 2));

        // 坐标轴标签
        for (var i = 0; i <= 4; i++)
        {
            var y = padTop + plotHeight * i / 4;
            dc.DrawText(Text($"{1200 - i * 300}", labelBrush, 9), new Point(6, y - 7));
            dc.DrawText(Text($"{16000 - i * 4000}", labelBrush, 9), new Point(width - padRight + 6, y - 7));
        }

        var buffer = Buffer;
        if (buffer is null) return;
        var (speed, spindle, count) = buffer.Snapshot();
        if (count < 2) return;

        var speedPen = new Pen(accent, 1.6);
        speedPen.Freeze();
        var spindlePen = new Pen(accent2, 1.4);
        spindlePen.Freeze();

        var speedGeometry = new StreamGeometry();
        using (var ctx = speedGeometry.Open())
        {
            for (var i = 0; i < count; i++)
            {
                var x = padLeft + plotWidth * i / (buffer.Capacity - 1);
                var y = padTop + plotHeight * (1 - Math.Clamp(speed[i] / 1200.0, 0, 1));
                var point = new Point(x, y);
                if (i == 0) ctx.BeginFigure(point, false, false);
                else ctx.LineTo(point, true, false);
            }
        }
        speedGeometry.Freeze();
        dc.DrawGeometry(null, speedPen, speedGeometry);

        var spindleGeometry = new StreamGeometry();
        using (var ctx = spindleGeometry.Open())
        {
            for (var i = 0; i < count; i++)
            {
                var x = padLeft + plotWidth * i / (buffer.Capacity - 1);
                var y = padTop + plotHeight * (1 - Math.Clamp(spindle[i] / 16000.0, 0, 1));
                var point = new Point(x, y);
                if (i == 0) ctx.BeginFigure(point, false, false);
                else ctx.LineTo(point, true, false);
            }
        }
        spindleGeometry.Freeze();
        dc.DrawGeometry(null, spindlePen, spindleGeometry);

        var tickBrush = new SolidColorBrush(Color.FromRgb(90, 108, 134));
        tickBrush.Freeze();
        dc.DrawText(Text("0 s", tickBrush, 9), new Point(padLeft - 4, height - padBottom + 4));
        dc.DrawText(Text("-36 s", tickBrush, 9), new Point(padLeft + plotWidth - 28, height - padBottom + 4));
    }
}
