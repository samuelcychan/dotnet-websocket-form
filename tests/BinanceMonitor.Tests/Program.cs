using BinanceMonitor.Views;
using Telemetry.Core;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        var directory = Path.Combine(Path.GetTempPath(), "sensor-ui-" + Guid.NewGuid().ToString("N"));
        using var form = new SensorForm(directory);
        T Find<T>(string name) where T : Control => (T)form.Controls.Find(name, true).Single();
        void Until(Func<bool> done)
        {
            var until = DateTime.UtcNow.AddSeconds(20);
            while (!done())
            {
                if (DateTime.UtcNow >= until) throw new Exception("UI condition timed out: " + Find<Label>("SensorStatus").Text);
                Application.DoEvents(); Thread.Sleep(20);
            }
            Application.DoEvents();
        }
        form.Show();
        Until(() => Find<Button>("ReplayPressure").Enabled);
        Find<Button>("ReplayPressure").PerformClick();
        Until(() => Find<Button>("ReplayPressure").Enabled);
        if (Find<DataGridView>("SensorReadings").Rows.Count != 6) throw new Exception("Replay did not render six readings.");
        if (Find<ComboBox>("SelectedSensor").Items.Count != 1) throw new Exception("Series selector mismatch.");
        var chart = Find<SensorChart>("SensorChart");
        var reading = chart.Latest() ?? throw new Exception("Chart empty after replay.");
        for (var i = 0; i < 350; i++) chart.Add(reading with { Value = 1013.2 + Math.Sin(i / 12d) * 12, Timestamp = reading.Timestamp.AddSeconds(i) });
        Application.DoEvents();
        using var bitmap = new Bitmap(form.Width, form.Height);
        form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
        bitmap.Save(Path.Combine(directory, "sensor-workspace.png"));
        form.Size = form.MinimumSize; Application.DoEvents();
        using var minimum = new Bitmap(form.Width, form.Height);
        form.DrawToBitmap(minimum, new Rectangle(Point.Empty, minimum.Size));
        minimum.Save(Path.Combine(directory, "sensor-minimum.png"));
        var tab = Find<TabPage>("HistoryTab"); ((TabControl)tab.Parent!).SelectedTab = tab;
        Find<Button>("SearchHistory").PerformClick();
        Until(() => Find<DataGridView>("SensorHistory").Rows.Count == 6);
        ((TabControl)tab.Parent!).SelectedIndex = 0;
        Find<Button>("ReplayPressure").PerformClick();
        Find<Button>("StopMqtt").PerformClick();
        Until(() => Find<Button>("ReplayPressure").Enabled);
        Find<Button>("ReplayPressure").PerformClick();
        form.Close();
        Until(() => form.IsDisposed);
        Console.WriteLine("PASS: replay, bounded-chart rendering, resize, history, cancellation and active close");
        Console.WriteLine("UI artifacts: " + directory);
    }
}
