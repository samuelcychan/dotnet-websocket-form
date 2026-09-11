using System.Collections.Concurrent;
using BinanceStream;

namespace BinanceMonitor;

public sealed class MainForm : Form
{
    private readonly TextBox symbols = new() { Text = "BTCUSDT, ETHUSDT", Width = 230 };
    private readonly ComboBox kind = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 150 };
    private readonly Button connect = new() { Text = "Connect", AutoSize = true };
    private readonly Button stop = new() { Text = "Disconnect", AutoSize = true, Enabled = false };
    private readonly Button replay = new() { Text = "Replay sample", AutoSize = true };
    private readonly ComboBox chartSymbol = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 160, AccessibleName = "Chart symbol" };
    private readonly MarketChart chart = new() { Dock = DockStyle.Fill };
    private readonly Label status = new() { Text = "Ready · Public market data · No API key required", Dock = DockStyle.Fill, AutoSize = true };
    private readonly Label quote = new() { Text = "Waiting for market data", Dock = DockStyle.Fill, AutoSize = true, Font = new Font("Segoe UI", 16) };
    private readonly DataGridView grid = new() { Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false,
        AllowUserToDeleteRows = false, RowHeadersVisible = false, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
        BackgroundColor = Color.FromArgb(24, 31, 43), BorderStyle = BorderStyle.None };
    private readonly ConcurrentQueue<MarketEvent> pending = new();
    private readonly System.Windows.Forms.Timer timer = new() { Interval = 100 };
    private CancellationTokenSource? session;
    private Task? running;
    private string latestStatus = "Ready";
    private long received;
    private long dropped;
    private bool closing;

    public MainForm()
    {
        Text = "Binance Stream · Market Monitor";
        Size = new Size(1100, 850);
        MinimumSize = new Size(1000, 720);
        Font = new Font("Segoe UI", 10);
        BackColor = Color.FromArgb(16, 22, 32);
        ForeColor = Color.Gainsboro;
        kind.DataSource = Enum.GetValues<StreamKind>();
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(24), RowCount = 7, ColumnCount = 1 };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 60));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 60));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 60));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 55));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 45));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
        layout.Controls.Add(new Label { Text = "MARKET / LIVE STREAM", Font = new Font("Segoe UI", 22, FontStyle.Bold), AutoSize = true }, 0, 0);
        var controls = new FlowLayoutPanel { Dock = DockStyle.Fill };
        controls.Controls.AddRange([symbols, kind, connect, stop, replay]);
        foreach (var button in new[] { connect, stop, replay }) { button.BackColor = Color.FromArgb(240, 185, 11); button.ForeColor = Color.Black; button.FlatStyle = FlatStyle.Flat; }
        layout.Controls.Add(controls, 0, 1);
        layout.Controls.Add(quote, 0, 2);
        var chartControls = new FlowLayoutPanel { Dock = DockStyle.Fill };
        chartControls.Controls.Add(new Label { Text = "Chart symbol", AutoSize = true, Margin = new Padding(0, 5, 12, 0) });
        chartControls.Controls.Add(chartSymbol);
        layout.Controls.Add(chartControls, 0, 3);
        layout.Controls.Add(chart, 0, 4);
        layout.Controls.Add(grid, 0, 5);
        layout.Controls.Add(status, 0, 6);
        Controls.Add(layout);
        foreach (var name in new[] { "Time / update", "Symbol", "Type", "Price / bid", "Quantity", "Ask / taker" }) grid.Columns.Add(name, name);
        grid.DefaultCellStyle.BackColor = Color.FromArgb(24, 31, 43);
        grid.DefaultCellStyle.ForeColor = Color.Gainsboro;
        grid.DefaultCellStyle.SelectionBackColor = Color.FromArgb(45, 62, 80);
        grid.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(35, 44, 58);
        grid.ColumnHeadersDefaultCellStyle.ForeColor = Color.Gainsboro;
        grid.EnableHeadersVisualStyles = false;
        connect.Click += async (_, _) => await StartAsync(false);
        replay.Click += async (_, _) => await StartAsync(true);
        stop.Click += (_, _) => session?.Cancel();
        chartSymbol.SelectedIndexChanged += (_, _) => chart.SelectedSymbol = chartSymbol.SelectedItem as string;
        timer.Tick += (_, _) => RenderPending();
        timer.Start();
        FormClosing += OnClosing;
    }

    private async Task StartAsync(bool fromFile)
    {
        if (session is not null) return;
        Subscription[] subscriptions;
        try
        {
            subscriptions = symbols.Text.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .Select(s => new Subscription(s, (StreamKind)kind.SelectedItem!)).Distinct().ToArray();
            if (subscriptions.Length is < 1 or > 100) throw new ArgumentException("Enter 1–100 symbols separated by commas.");
        }
        catch (ArgumentException ex) { status.Text = ex.Message; return; }
        session = new CancellationTokenSource();
        connect.Enabled = replay.Enabled = symbols.Enabled = kind.Enabled = false;
        stop.Enabled = true;
        grid.Rows.Clear();
        chart.Clear();
        chartSymbol.Items.Clear();
        pending.Clear();
        received = dropped = 0;
        quote.Text = "Waiting for market data";
        latestStatus = fromFile ? "Replaying sample (recorded data)" : "Connecting";
        var token = session.Token;
        running = Task.Run(async () =>
        {
            try
            {
                var client = new BinanceClient();
                client.StatusChanged += value => Volatile.Write(ref latestStatus, value);
                var source = fromFile
                    ? FileReplay.ReadAsync(Path.Combine(AppContext.BaseDirectory, "samples", "market.jsonl"), TimeSpan.FromMilliseconds(500), token)
                    : client.StreamAsync(subscriptions, token);
                await foreach (var item in source.WithCancellation(token))
                {
                    pending.Enqueue(item);
                    Interlocked.Increment(ref received);
                    while (pending.Count > 1000 && pending.TryDequeue(out _)) Interlocked.Increment(ref dropped);
                }
                Volatile.Write(ref latestStatus, "Replay complete");
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { Volatile.Write(ref latestStatus, "Disconnected"); }
            catch (Exception ex) { Volatile.Write(ref latestStatus, $"Error: {ex.Message}"); }
        });
        await running;
        session.Dispose();
        session = null;
        if (!closing)
        {
            connect.Enabled = replay.Enabled = symbols.Enabled = kind.Enabled = true;
            stop.Enabled = false;
            RenderPending();
        }
    }

    private void RenderPending()
    {
        for (var i = 0; i < 100 && pending.TryDequeue(out var item); i++)
        {
            if (chart.Add(item))
            {
                chartSymbol.Items.Add(item.Symbol);
                if (chartSymbol.SelectedIndex < 0) chartSymbol.SelectedIndex = 0;
            }
            switch (item)
            {
                case Trade t:
                    grid.Rows.Insert(0, t.Time.LocalDateTime.ToString("HH:mm:ss.fff"), t.Symbol,
                        t.Aggregated ? "Aggregate" : "Trade", t.Price, t.Quantity, t.BuyerIsMaker ? "Sell" : "Buy");
                    quote.Text = $"{t.Symbol}   {t.Price:N8}   ·   Last trade";
                    break;
                case BookTicker b:
                    grid.Rows.Insert(0, b.UpdateId, b.Symbol, "Best quote", b.BidPrice, b.BidQuantity, b.AskPrice);
                    quote.Text = $"{b.Symbol}   Bid {b.BidPrice} / Ask {b.AskPrice}   ·   Spread {b.AskPrice - b.BidPrice}";
                    break;
            }
            while (grid.Rows.Count > 500) grid.Rows.RemoveAt(grid.Rows.Count - 1);
        }
        status.Text = $"{Volatile.Read(ref latestStatus)}   ·   {Interlocked.Read(ref received):N0} events   ·   {Interlocked.Read(ref dropped):N0} display skips";
    }

    private async void OnClosing(object? sender, FormClosingEventArgs e)
    {
        if (closing) return;
        e.Cancel = true;
        closing = true;
        timer.Stop();
        session?.Cancel();
        if (running is not null) await running;
        timer.Dispose();
        Close();
    }
}
