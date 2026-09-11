using System.ComponentModel;
using System.Drawing.Drawing2D;
using Telemetry.Core;

namespace BinanceMonitor.Views;

public sealed class SensorChart : Control
{
    private readonly Dictionary<string, Queue<SensorReading>> series = new();
    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string? SelectedSeries { get; set; }
    public SensorChart() { DoubleBuffered = true; ResizeRedraw = true; BackColor = Color.FromArgb(24, 31, 43); ForeColor = Color.Gainsboro; }
    public void Clear() { series.Clear(); SelectedSeries = null; Invalidate(); }
    public bool Add(SensorReading reading)
    {
        var added = false;
        if (!series.TryGetValue(reading.SeriesKey, out var points))
        {
            if (series.Count >= 100) return false;
            series[reading.SeriesKey] = points = new();
            added = true;
        }
        points.Enqueue(reading);
        while (points.Count > 300) points.Dequeue();
        Invalidate();
        return added;
    }
    public SensorReading? Latest() => SelectedSeries is not null && series.TryGetValue(SelectedSeries, out var points)
        ? points.LastOrDefault() : null;
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var brush = new SolidBrush(ForeColor);
        var latest = Latest();
        if (latest is null) { g.DrawString("Connect to MQTT or replay the pressure fixture", Font, brush, 12, 12); return; }
        var points = series[latest.SeriesKey].ToArray();
        g.DrawString($"{latest.DeviceId} · {latest.Metric} ({latest.Unit}) · {points.Length}/300 samples · arrival order", Font, brush, 12, 10);
        var min = points.Min(p => p.Value);
        var max = points.Max(p => p.Value);
        // Scale through normalized doubles to avoid overflow for finite but extreme payload values.
        var magnitude = Math.Max(1, Math.Max(Math.Abs(min), Math.Abs(max)));
        min /= magnitude; max /= magnitude;
        var pad = Math.Max((max - min) * .08, 1e-6);
        min -= pad; max += pad;
        var box = new RectangleF(115, 40, Width - 140, Height - 85);
        if (box.Width < 40 || box.Height < 30) return;
        using var grid = new Pen(Color.FromArgb(48, 60, 76));
        for (var i = 0; i <= 4; i++)
        {
            var y = box.Top + box.Height * i / 4;
            g.DrawLine(grid, box.Left, y, box.Right, y);
            g.DrawString(((max - (max - min) * i / 4) * magnitude).ToString("G7"), Font, brush, 4, y - Font.Height / 2f);
        }
        using var line = new Pen(Color.Turquoise, 2);
        PointF? previous = null;
        for (var i = 0; i < points.Length; i++)
        {
            var p = new PointF(box.Left + box.Width * i / Math.Max(1, points.Length - 1),
                box.Bottom - (float)((points[i].Value / magnitude - min) / (max - min)) * box.Height);
            if (previous is { } last) g.DrawLine(line, last, p);
            g.FillEllipse(brush, p.X - 2, p.Y - 2, 4, 4);
            previous = p;
        }
        g.DrawString($"{points[0].Timestamp.ToLocalTime():HH:mm:ss} → {latest.Timestamp.ToLocalTime():HH:mm:ss} event time · evenly spaced samples", Font, brush, box.Left, box.Bottom + 10);
    }
}
