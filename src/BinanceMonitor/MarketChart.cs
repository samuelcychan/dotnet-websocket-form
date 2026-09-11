using System.ComponentModel;
using System.Drawing.Drawing2D;
using BinanceStream;

namespace BinanceMonitor;

/// <summary>A bounded display of market prices. Call only from the UI thread.</summary>
public sealed class MarketChart : Control
{
    private const int PointLimit = 300;
    private const int SymbolLimit = 100;
    private readonly Dictionary<string, Queue<PricePoint>> history = new(StringComparer.OrdinalIgnoreCase);
    private string? selectedSymbol;

    public MarketChart()
    {
        DoubleBuffered = true;
        ResizeRedraw = true;
        BackColor = Color.FromArgb(24, 31, 43);
        ForeColor = Color.Gainsboro;
        AccessibleName = "Market price chart";
    }

    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string? SelectedSymbol
    {
        get => selectedSymbol;
        set { selectedSymbol = value; Invalidate(); }
    }

    public void Clear()
    {
        history.Clear();
        SelectedSymbol = null;
    }

    public bool Add(MarketEvent item)
    {
        PricePoint point;
        switch (item)
        {
            case Trade trade:
                point = new(DateTimeOffset.Now, trade.Price, null);
                break;
            case BookTicker book:
                point = new(DateTimeOffset.Now, book.BidPrice, book.AskPrice);
                break;
            default:
                return false;
        }
        var added = false;
        if (!history.TryGetValue(item.Symbol, out var points))
        {
            if (history.Count >= SymbolLimit) return false;
            history[item.Symbol] = points = new Queue<PricePoint>();
            added = true;
        }
        points.Enqueue(point);
        while (points.Count > PointLimit) points.Dequeue();
        if (string.Equals(selectedSymbol, item.Symbol, StringComparison.OrdinalIgnoreCase)) Invalidate();
        return added;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var graphics = e.Graphics;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var text = new SolidBrush(ForeColor);
        graphics.DrawString($"{selectedSymbol ?? "PRICE"} · Trade (gold) / Bid (teal) / Ask (blue)", Font, text, 12, 8);
        if (selectedSymbol is null || !history.TryGetValue(selectedSymbol, out var queue) || queue.Count == 0)
        {
            graphics.DrawString("Connect or replay a sample to see prices", Font, text, 12, 45);
            return;
        }
        var points = queue.ToArray();
        // Convert only display coordinates; original market values remain decimal.
        var minimum = points.Min(p => Math.Min((double)p.First, (double)(p.Ask ?? p.First)));
        var maximum = points.Max(p => Math.Max((double)p.First, (double)(p.Ask ?? p.First)));
        var padding = Math.Max((maximum - minimum) * 0.08, Math.Max(Math.Abs(maximum) * 0.000001, 0.00000001));
        minimum -= padding;
        maximum += padding;
        var labelWidth = Math.Max(graphics.MeasureString(minimum.ToString("G10"), Font).Width,
            graphics.MeasureString(maximum.ToString("G10"), Font).Width);
        var plot = new RectangleF(labelWidth + 20, 40, Width - labelWidth - 40, Height - 84);
        if (plot.Width < 40 || plot.Height < 30) return;
        using var gridPen = new Pen(Color.FromArgb(48, 60, 76));
        for (var tick = 0; tick <= 4; tick++)
        {
            var y = plot.Top + plot.Height * tick / 4;
            graphics.DrawLine(gridPen, plot.Left, y, plot.Right, y);
            graphics.DrawString((maximum - (maximum - minimum) * tick / 4).ToString("G10"), Font, text, 4, y - Font.Height / 2f);
        }
        // Space samples evenly: book tickers do not provide exchange timestamps.
        PointF Position(int index, decimal value) => new(
            plot.Left + plot.Width * index / Math.Max(1, points.Length - 1),
            plot.Bottom - (float)(((double)value - minimum) / (maximum - minimum)) * plot.Height);
        void DrawSeries(Color color, Func<PricePoint, decimal?> select)
        {
            using var pen = new Pen(color, 2);
            using var brush = new SolidBrush(color);
            PointF? previous = null;
            for (var index = 0; index < points.Length; index++)
            {
                var value = select(points[index]);
                if (value is null) { previous = null; continue; }
                var current = Position(index, value.Value);
                if (previous is { } last) graphics.DrawLine(pen, last, current);
                graphics.FillEllipse(brush, current.X - 2, current.Y - 2, 4, 4);
                previous = current;
            }
        }
        DrawSeries(Color.FromArgb(240, 185, 11), p => p.Ask is null ? p.First : null);
        DrawSeries(Color.Turquoise, p => p.Ask is not null ? p.First : null);
        DrawSeries(Color.DeepSkyBlue, p => p.Ask);
        graphics.DrawString($"{points[0].Received:HH:mm:ss} → {points[^1].Received:HH:mm:ss} local display time · {points.Length}/{PointLimit} samples · evenly spaced", Font, text, plot.Left, plot.Bottom + 10);
    }

    private sealed record PricePoint(DateTimeOffset Received, decimal First, decimal? Ask);
}
