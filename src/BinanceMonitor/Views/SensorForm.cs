using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Telemetry.Core;
using Telemetry.Mqtt;
using Telemetry.Storage;

namespace BinanceMonitor.Views;

public sealed class SensorForm : Form
{
    private readonly TextBox host = new() { Text = "127.0.0.1", Width = 140 };
    private readonly NumericUpDown port = new() { Minimum = 1, Maximum = 65535, Value = 1883, Width = 75 };
    private readonly TextBox topic = new() { Text = "emulator/+/telemetry", Width = 200 };
    private readonly TextBox source = new() { Text = "hivemq-local", Width = 130 };
    private readonly TextBox username = new() { Width = 100 };
    private readonly TextBox password = new() { Width = 100, UseSystemPasswordChar = true };
    private readonly CheckBox tls = new() { Text = "TLS", AutoSize = true };
    private readonly CheckBox flat = new() { Text = "Flat pressure (sensor-01)", AutoSize = true };
    private readonly Button connect = new() { Text = "Connect", AutoSize = true, Enabled = false };
    private readonly Button stop = new() { Text = "Stop", AutoSize = true, Enabled = false };
    private readonly Button replay = new() { Text = "Replay pressure", AutoSize = true, Enabled = false };
    private readonly Button saveProfile = new() { Text = "Save profile", AutoSize = true };
    private readonly Label status = new() { Dock = DockStyle.Fill, AutoEllipsis = true, Text = "Opening database…" };
    private readonly Label latest = new() { Dock = DockStyle.Fill, Font = new Font("Segoe UI", 14), AutoEllipsis = true };
    private readonly ComboBox selected = new() { Width = 650, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly SensorChart chart = new() { Dock = DockStyle.Fill };
    private readonly DataGridView liveGrid = Grid();
    private readonly DataGridView historyGrid = Grid();
    private readonly DataGridView alertsGrid = Grid();
    private readonly DataGridView rulesGrid = Grid();
    private readonly SensorChart historyChart = new() { Dock = DockStyle.Fill };
    private readonly DateTimePicker from = new() { Value = DateTime.Today, Format = DateTimePickerFormat.Custom, CustomFormat = "yyyy-MM-dd HH:mm", Width = 165 };
    private readonly DateTimePicker to = new() { Value = DateTime.Today.AddDays(1), Format = DateTimePickerFormat.Custom, CustomFormat = "yyyy-MM-dd HH:mm", Width = 165 };
    private readonly TextBox deviceFilter = new() { Width = 100, PlaceholderText = "Device (all)" };
    private readonly TextBox metricFilter = new() { Width = 100, PlaceholderText = "Metric (all)" };
    private readonly TextBox sourceFilter = new() { Width = 100, PlaceholderText = "Source (all)" };
    private readonly TextBox unitFilter = new() { Width = 80, PlaceholderText = "Unit (all)" };
    private readonly TextBox minFilter = new() { Width = 80, PlaceholderText = "Min" };
    private readonly TextBox maxFilter = new() { Width = 80, PlaceholderText = "Max" };
    private readonly TextBox low = new() { Text = "960", Width = 70 };
    private readonly TextBox high = new() { Text = "1040", Width = 70 };
    private readonly TextBox hysteresis = new() { Text = "1", Width = 60 };
    private readonly NumericUpDown cooldown = new() { Value = 30, Maximum = 86400, Width = 75 };
    private readonly ConcurrentQueue<SensorReading> pending = new();
    private readonly System.Windows.Forms.Timer timer = new() { Interval = 100 };
    private readonly TelemetryStore store;
    private readonly string profilePath;
    private CancellationTokenSource? session;
    private CancellationTokenSource? operation;
    private Task? running;
    private Task? operating;
    private MqttSource? mqtt;
    private long accepted, duplicates, skipped, afterId, cutoff;
    private HistoryFilter? activeFilter;
    private string state = "Ready";
    private string lastAlert = "";
    private bool closing;

    public SensorForm(string? dataDirectory = null)
    {
        Text = "Sensor Monitor · MQTT / HiveMQ";
        Size = new Size(1200, 850); MinimumSize = new Size(1100, 760);
        Font = new Font("Segoe UI", 10);
        var directory = dataDirectory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BinanceMonitor");
        store = new(Path.Combine(directory, "telemetry.db"));
        profilePath = Path.Combine(directory, "mqtt-profile.json");
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 4, ColumnCount = 1, Padding = new Padding(12) };
        layout.RowStyles.Add(new(SizeType.Absolute, 115)); layout.RowStyles.Add(new(SizeType.Percent, 100));
        layout.RowStyles.Add(new(SizeType.Absolute, 35)); layout.RowStyles.Add(new(SizeType.Absolute, 30));
        var settings = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoScroll = true };
        AddFields(settings, ("Host", host), ("Port", port), ("Topic", topic), ("Source", source), ("User", username), ("Password", password));
        settings.Controls.AddRange([tls, flat, connect, stop, replay, saveProfile]);
        layout.Controls.Add(settings, 0, 0);
        var tabs = new TabControl { Dock = DockStyle.Fill };
        var live = new TabPage("Live sensors"); var history = new TabPage("History / CSV"); var alerts = new TabPage("Alerts");
        tabs.TabPages.AddRange([live, history, alerts]); layout.Controls.Add(tabs, 0, 1);
        var liveLayout = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 4, ColumnCount = 1 };
        liveLayout.RowStyles.Add(new(SizeType.Absolute, 38)); liveLayout.RowStyles.Add(new(SizeType.Absolute, 40));
        liveLayout.RowStyles.Add(new(SizeType.Percent, 60)); liveLayout.RowStyles.Add(new(SizeType.Percent, 40));
        liveLayout.Controls.Add(selected, 0, 0); liveLayout.Controls.Add(latest, 0, 1);
        liveLayout.Controls.Add(chart, 0, 2); liveLayout.Controls.Add(liveGrid, 0, 3); live.Controls.Add(liveLayout);
        foreach (var name in new[] { "Event time", "Device", "Metric", "Value", "Unit", "Retained" }) liveGrid.Columns.Add(name, name);
        var historyControls = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 100 };
        var search = new Button { Text = "Search", AutoSize = true }; var next = new Button { Text = "Next 200", AutoSize = true };
        var export = new Button { Text = "Export all matching", AutoSize = true }; var cancel = new Button { Text = "Cancel query/export", AutoSize = true };
        historyControls.Controls.AddRange([from, to, deviceFilter, metricFilter, sourceFilter, unitFilter, minFilter, maxFilter, search, next, export, cancel]);
        var historyLayout = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 1 };
        historyLayout.RowStyles.Add(new(SizeType.Percent, 55)); historyLayout.RowStyles.Add(new(SizeType.Percent, 45));
        historyLayout.Controls.Add(historyGrid, 0, 0); historyLayout.Controls.Add(historyChart, 0, 1);
        history.Controls.Add(historyLayout); history.Controls.Add(historyControls);
        var alertControls = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 110 };
        AddFields(alertControls, ("Low", low), ("High", high), ("Hysteresis", hysteresis), ("Cooldown sec", cooldown));
        var setRule = new Button { Text = "Save rule for selected live series", AutoSize = true };
        var deleteRule = new Button { Text = "Disable selected rule", AutoSize = true };
        var refreshAlerts = new Button { Text = "Refresh", AutoSize = true };
        var ack = new Button { Text = "Acknowledge selected", AutoSize = true };
        alertControls.Controls.AddRange([setRule, deleteRule, refreshAlerts, ack]);
        var alertLayout = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 1 };
        alertLayout.RowStyles.Add(new(SizeType.Percent, 35)); alertLayout.RowStyles.Add(new(SizeType.Percent, 65));
        alertLayout.Controls.Add(rulesGrid, 0, 0); alertLayout.Controls.Add(alertsGrid, 0, 1);
        alerts.Controls.Add(alertLayout); alerts.Controls.Add(alertControls);
        var maintenance = new TabPage("Storage"); tabs.TabPages.Add(maintenance);
        var maintenanceControls = new FlowLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(16) };
        var backup = new Button { Text = "Back up database", AutoSize = true };
        var retentionDays = new NumericUpDown { Minimum = 1, Maximum = 36500, Value = 30, Width = 90 };
        var purge = new Button { Text = "Delete readings older than days", AutoSize = true };
        var storageInfo = new Label { AutoSize = true, Text = "Retention is manual. Alert history and rules are preserved. Cancel leaves already completed deletion batches in place." };
        maintenanceControls.Controls.AddRange([backup, retentionDays, purge, storageInfo]); maintenance.Controls.Add(maintenanceControls);
        layout.Controls.Add(status, 0, 2);
        layout.Controls.Add(new Label { Text = $"Database: {store.Path} · UTC storage / local display · No automatic deletion", Dock = DockStyle.Fill, AutoEllipsis = true }, 0, 3);
        Controls.Add(layout);
        connect.Name = "ConnectMqtt"; replay.Name = "ReplayPressure"; stop.Name = "StopMqtt";
        selected.Name = "SelectedSensor"; liveGrid.Name = "SensorReadings"; status.Name = "SensorStatus";
        chart.Name = "SensorChart"; search.Name = "SearchHistory"; historyGrid.Name = "SensorHistory";
        history.Name = "HistoryTab"; alerts.Name = "AlertsTab";
        selected.SelectedIndexChanged += (_, _) => { chart.SelectedSeries = (selected.SelectedItem as SeriesChoice)?.Key; chart.Invalidate(); };
        connect.Click += async (_, _) => await StartAsync(false);
        replay.Click += async (_, _) => await StartAsync(true);
        stop.Click += (_, _) => session?.Cancel();
        saveProfile.Click += (_, _) => { try { SaveProfile(); state = "Profile saved (password protected for this Windows user)"; } catch (Exception ex) { state = ex.Message; } };
        backup.Click += async (_, _) =>
        {
            if (operation is not null) return;
            using var dialog = new SaveFileDialog { Filter = "SQLite database|*.db", FileName = "telemetry-backup.db" };
            if (dialog.ShowDialog(this) == DialogResult.OK)
                await WorkAsync(_ => { store.Backup(dialog.FileName); state = "Database backup complete"; return Task.CompletedTask; });
        };
        purge.Click += async (_, _) =>
        {
            if (operation is not null) return;
            var before = DateTimeOffset.UtcNow.AddDays(-(int)retentionDays.Value);
            if (MessageBox.Show(this, $"Permanently delete readings before {before:O}? Back up first if needed.", "Delete old readings", MessageBoxButtons.OKCancel) != DialogResult.OK) return;
            await WorkAsync(token => { var count = store.DeleteReadingsBefore(before, token); state = $"Deleted {count:N0} old readings"; return Task.CompletedTask; });
        };
        search.Click += async (_, _) => await QueryAsync(true);
        next.Click += async (_, _) => await QueryAsync(false);
        cancel.Click += (_, _) => operation?.Cancel();
        export.Click += async (_, _) =>
        {
            if (operation is not null) return;
            using var dialog = new SaveFileDialog { Filter = "CSV|*.csv", FileName = "sensor-readings.csv" };
            if (dialog.ShowDialog(this) != DialogResult.OK) return;
            try { var filter = Filter(); await WorkAsync(async token => { var count = await store.ExportAsync(dialog.FileName, filter, token); state = $"Exported {count:N0} readings"; }); }
            catch (Exception ex) { state = ex.Message; }
        };
        setRule.Click += async (_, _) =>
        {
            try
            {
                var key = (selected.SelectedItem as SeriesChoice)?.Key ?? throw new ArgumentException("Select a live series first.");
                var rule = new AlertRule(Number(low.Text), Number(high.Text), Number(hysteresis.Text), (int)cooldown.Value);
                rule.Validate();
                await WorkAsync(_ => { store.SaveRule(key, rule); state = "Rule saved; state reset to Normal for the new version"; return Task.CompletedTask; });
            }
            catch (Exception ex) { state = ex.Message; }
        };
        deleteRule.Click += async (_, _) =>
        {
            if (selected.SelectedItem is SeriesChoice choice) await WorkAsync(_ => { store.DeleteRule(choice.Key); state = "Rule disabled"; return Task.CompletedTask; });
        };
        refreshAlerts.Click += async (_, _) => await RefreshAlertsAsync();
        ack.Click += async (_, _) =>
        {
            if (alertsGrid.CurrentRow?.DataBoundItem is AlertEvent alert)
            { await WorkAsync(_ => { store.Acknowledge(alert.Id); return Task.CompletedTask; }); await RefreshAlertsAsync(); }
        };
        timer.Tick += (_, _) => Render(); timer.Start();
        Shown += async (_, _) =>
        {
            var initialized = false;
            await WorkAsync(_ => { store.Initialize(); initialized = true; return Task.CompletedTask; });
            if (initialized && !closing) { connect.Enabled = replay.Enabled = true; state = "Ready"; }
            try { LoadProfile(); } catch (Exception ex) { state = $"Profile not loaded: {ex.Message}"; }
        };
        FormClosing += OnClosing;
    }

    private static DataGridView Grid() => new() { Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false,
        AllowUserToDeleteRows = false, RowHeadersVisible = false, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
        SelectionMode = DataGridViewSelectionMode.FullRowSelect, MultiSelect = false };
    private static void AddFields(FlowLayoutPanel panel, params (string Name, Control Control)[] fields)
    {
        foreach (var (name, control) in fields) { panel.Controls.Add(new Label { Text = name, AutoSize = true, Padding = new Padding(0, 5, 0, 0) }); panel.Controls.Add(control); }
    }
    private static double Number(string text)
    {
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) || !double.IsFinite(number))
            throw new ArgumentException("Enter a finite number using '.' as the decimal separator.");
        return number;
    }
    private HistoryFilter Filter() => new(new DateTimeOffset(from.Value).ToUniversalTime(), new DateTimeOffset(to.Value).ToUniversalTime(),
        deviceFilter.Text.Trim(), metricFilter.Text.Trim(), string.IsNullOrWhiteSpace(minFilter.Text) ? null : Number(minFilter.Text),
        string.IsNullOrWhiteSpace(maxFilter.Text) ? null : Number(maxFilter.Text), sourceFilter.Text.Trim(), unitFilter.Text.Trim());
    private MqttProfile Profile() => new(host.Text.Trim(), (int)port.Value, topic.Text.Trim(), source.Text.Trim(),
        "monitor-" + Environment.MachineName, tls.Checked, username.Text, flat.Checked ? "sensor-01" : null);

    private void SaveProfile()
    {
        var profile = Profile(); profile.Validate();
        var encrypted = Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(password.Text), null, DataProtectionScope.CurrentUser));
        Directory.CreateDirectory(Path.GetDirectoryName(profilePath)!);
        var temporary = profilePath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(new SavedProfile(profile, encrypted), new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporary, profilePath, true);
    }
    private void LoadProfile()
    {
        if (!File.Exists(profilePath)) return;
        var saved = JsonSerializer.Deserialize<SavedProfile>(File.ReadAllText(profilePath))!;
        saved.Profile.Validate();
        host.Text = saved.Profile.Host; port.Value = saved.Profile.Port; topic.Text = saved.Profile.Topic;
        source.Text = saved.Profile.SourceId; tls.Checked = saved.Profile.Tls; username.Text = saved.Profile.Username;
        flat.Checked = saved.Profile.FlatPressureDevice is not null;
        password.Text = Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(saved.Secret), null, DataProtectionScope.CurrentUser));
    }

    private async Task StartAsync(bool fromFile)
    {
        if (session is not null) return;
        MqttProfile profile;
        try { profile = Profile(); profile.Validate(); } catch (Exception ex) { state = ex.Message; return; }
        var secret = password.Text;
        session = new(); var token = session.Token;
        connect.Enabled = replay.Enabled = saveProfile.Enabled = false; stop.Enabled = true;
        foreach (var field in new Control[] { host, port, topic, source, username, password, tls, flat }) field.Enabled = false;
        pending.Clear(); chart.Clear(); selected.Items.Clear(); liveGrid.Rows.Clear();
        accepted = duplicates = skipped = 0; mqtt = null; lastAlert = "";
        running = Task.Run(async () =>
        {
            try
            {
                Task Persist(SensorReading reading, CancellationToken cancellationToken)
                {
                    var result = store.Write(reading);
                    if (result.Inserted)
                    {
                        Interlocked.Increment(ref accepted); pending.Enqueue(reading);
                        while (pending.Count > 1000 && pending.TryDequeue(out _)) Interlocked.Increment(ref skipped);
                        if (result.Transition is not null) Volatile.Write(ref lastAlert, result.Transition);
                    }
                    else Interlocked.Increment(ref duplicates);
                    return Task.CompletedTask;
                }
                if (fromFile)
                {
                    state = "Replaying pressure fixture (new IDs and acquisition times)";
                    var replayId = Guid.NewGuid().ToString("N");
                    await foreach (var line in File.ReadLinesAsync(Path.Combine(AppContext.BaseDirectory, "samples", "telemetry", "pressure.jsonl"), token))
                    {
                        if (string.IsNullOrWhiteSpace(line)) continue;
                        var r = ReadingParser.Parse(line, "replay", "fixture") with { Timestamp = DateTimeOffset.UtcNow, ReceivedAt = DateTimeOffset.UtcNow };
                        await Persist(r with { MessageId = replayId + r.MessageId }, token);
                        await Task.Delay(500, token);
                    }
                    state = "Replay complete";
                }
                else
                {
                    mqtt = new MqttSource(); mqtt.StatusChanged += value => Volatile.Write(ref state, value);
                    await mqtt.RunAsync(profile, secret, Persist, token);
                    state = "Disconnected";
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { state = "Disconnected"; }
            catch (Exception ex) { state = $"Faulted: {ex.Message}"; }
        });
        await running;
        session.Dispose(); session = null;
        if (!closing)
        {
            connect.Enabled = replay.Enabled = saveProfile.Enabled = true; stop.Enabled = false;
            foreach (var field in new Control[] { host, port, topic, source, username, password, tls, flat }) field.Enabled = true;
            Render();
        }
    }

    private void Render()
    {
        for (var i = 0; i < 100 && pending.TryDequeue(out var r); i++)
        {
            if (chart.Add(r))
            {
                selected.Items.Add(new SeriesChoice(r.SeriesKey, $"{r.SourceId} / {r.DeviceId} / {r.Metric} [{r.Unit}]"));
                if (selected.SelectedIndex < 0) selected.SelectedIndex = 0;
            }
            liveGrid.Rows.Insert(0, r.Timestamp.ToLocalTime().ToString("HH:mm:ss.fff"), r.DeviceId, r.Metric, r.Value, r.Unit, r.Retained);
            while (liveGrid.Rows.Count > 500) liveGrid.Rows.RemoveAt(500);
        }
        if (chart.Latest() is { } current)
        {
            var stale = current.Retained || DateTimeOffset.UtcNow - current.ReceivedAt > TimeSpan.FromSeconds(5);
            latest.Text = $"{current.DeviceId}: {current.Value:G10} {current.Unit} · {(stale ? "Stale / snapshot" : "Live")} · {lastAlert}";
        }
        status.Text = $"{Volatile.Read(ref state)} · Stored {Interlocked.Read(ref accepted):N0} · Duplicates {Interlocked.Read(ref duplicates):N0} · Rejected {mqtt?.Rejected ?? 0} · Display skips {Interlocked.Read(ref skipped):N0}";
    }

    private async Task WorkAsync(Func<CancellationToken, Task> action)
    {
        if (operation is not null || closing) return;
        operation = new();
        operating = Task.Run(() => action(operation.Token));
        try { await operating; }
        catch (OperationCanceledException) { state = "Operation cancelled"; }
        catch (Exception ex) { state = $"Error: {ex.Message}"; }
        finally { operation.Dispose(); operation = null; }
    }
    private async Task QueryAsync(bool reset)
    {
        if (operation is not null) return;
        try
        {
            if (reset) { activeFilter = Filter(); afterId = 0; }
            if (activeFilter is null) return;
            List<StoredReading>? rows = null;
            await WorkAsync(token => { if (reset) cutoff = store.LatestId(); rows = store.Query(activeFilter, afterId, cutoff, 200, token); return Task.CompletedTask; });
            if (closing || rows is null) return;
            historyGrid.DataSource = rows.Select(x => new { x.Id, x.Reading.SourceId, x.Reading.DeviceId, x.Reading.Metric, x.Reading.Unit,
                x.Reading.Value, Time = x.Reading.Timestamp.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss.fff"), x.Reading.Retained }).ToList();
            historyChart.Clear();
            foreach (var row in rows) historyChart.Add(row.Reading);
            historyChart.SelectedSeries = rows.FirstOrDefault()?.Reading.SeriesKey;
            historyChart.Invalidate();
            if (rows.Count > 0) afterId = rows[^1].Id;
            state = $"History: {rows.Count} rows by ingestion ID; chart shows first series in this page. Filter device/metric/unit to narrow.";
        }
        catch (Exception ex) { state = ex.Message; }
    }
    private async Task RefreshAlertsAsync()
    {
        IReadOnlyList<AlertEvent>? rows = null;
        IReadOnlyList<StoredRule>? rules = null;
        await WorkAsync(_ => { rows = store.Alerts(); rules = store.Rules(); return Task.CompletedTask; });
        if (!closing && rows is not null) { alertsGrid.DataSource = rows; rulesGrid.DataSource = rules; }
    }
    private async void OnClosing(object? sender, FormClosingEventArgs e)
    {
        if (closing) return;
        e.Cancel = true; closing = true; timer.Stop(); session?.Cancel(); operation?.Cancel();
        if (running is not null) await running;
        if (operating is not null) { try { await operating; } catch (Exception) { } }
        timer.Dispose(); Close();
    }
    private sealed record SavedProfile(MqttProfile Profile, string Secret);
    private sealed record SeriesChoice(string Key, string Label) { public override string ToString() => Label; }
}
