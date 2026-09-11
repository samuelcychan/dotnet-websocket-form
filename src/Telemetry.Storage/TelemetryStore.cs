using System.Globalization;
using System.Text;
using Microsoft.Data.Sqlite;
using Telemetry.Core;

namespace Telemetry.Storage;

public sealed record StoredReading(long Id, SensorReading Reading);
public sealed record HistoryFilter(DateTimeOffset From, DateTimeOffset To, string Device = "", string Metric = "",
    double? Minimum = null, double? Maximum = null, string Source = "", string Unit = "");
public sealed record AlertEvent(long Id, string Series, string Transition, string Time, bool Acknowledged, bool Notify);
public sealed record WriteResult(bool Inserted, string? Transition);
public sealed record StoredRule(string Series, double Low, double High, double Hysteresis, int CooldownSeconds, int Version, string State, bool Enabled);

/// <summary>Short-lived connections; all calls are blocking and belong on a background worker.</summary>
public sealed class TelemetryStore(string path)
{
    public string Path { get; } = System.IO.Path.GetFullPath(path);

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = Path, DefaultTimeout = 5, Pooling = true }.ToString());
        connection.Open();
        return connection;
    }

    private static SqliteCommand Command(SqliteConnection c, string sql, SqliteTransaction? tx = null,
        params (string Name, object? Value)[] parameters)
    {
        var command = c.CreateCommand();
        command.CommandText = sql;
        command.Transaction = tx;
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return command;
    }

    public void Initialize()
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        using var c = Open();
        using var version = Command(c, "PRAGMA user_version");
        var schemaVersion = Convert.ToInt32(version.ExecuteScalar());
        if (schemaVersion > 2) throw new IOException("Database belongs to a newer application.");
        using var journal = Command(c, "PRAGMA journal_mode=WAL;");
        journal.ExecuteNonQuery();
        using var tx = c.BeginTransaction();
        using var schema = Command(c, """
            CREATE TABLE IF NOT EXISTS Readings(
                Id INTEGER PRIMARY KEY AUTOINCREMENT, Source TEXT NOT NULL, Device TEXT NOT NULL, Metric TEXT NOT NULL,
                Unit TEXT NOT NULL, Value REAL NOT NULL, EventTime INTEGER NOT NULL, ReceivedTime INTEGER NOT NULL,
                MessageId TEXT, Topic TEXT NOT NULL, Retained INTEGER NOT NULL, ReceiptTimeUsed INTEGER NOT NULL);
            CREATE UNIQUE INDEX IF NOT EXISTS ReadingIdentity ON Readings(Source,Device,Metric,MessageId) WHERE MessageId IS NOT NULL;
            CREATE INDEX IF NOT EXISTS ReadingTime ON Readings(EventTime,Id);
            CREATE INDEX IF NOT EXISTS ReadingSeries ON Readings(Source,Device,Metric,Unit,EventTime);
            CREATE TABLE IF NOT EXISTS Rules(
                Series TEXT PRIMARY KEY, Low REAL NOT NULL, High REAL NOT NULL, Hysteresis REAL NOT NULL,
                Cooldown INTEGER NOT NULL, Version INTEGER NOT NULL, State TEXT NOT NULL DEFAULT 'Normal',
                LastEvent INTEGER NOT NULL DEFAULT 0, LastNotify INTEGER NOT NULL DEFAULT 0, Enabled INTEGER NOT NULL DEFAULT 1);
            CREATE TABLE IF NOT EXISTS RuleVersions(
                Series TEXT NOT NULL, Version INTEGER NOT NULL, Low REAL NOT NULL, High REAL NOT NULL,
                Hysteresis REAL NOT NULL, Cooldown INTEGER NOT NULL, Enabled INTEGER NOT NULL,
                PRIMARY KEY(Series,Version));
            CREATE TABLE IF NOT EXISTS AlertEvents(
                Id INTEGER PRIMARY KEY, ReadingId INTEGER, Series TEXT NOT NULL, Transition TEXT NOT NULL,
                Time INTEGER NOT NULL, RuleVersion INTEGER NOT NULL, Acknowledged INTEGER NOT NULL DEFAULT 0,
                Notify INTEGER NOT NULL DEFAULT 1);
            PRAGMA user_version=2;
            """, tx);
        schema.ExecuteNonQuery();
        if (schemaVersion == 1)
        {
            using var migration = Command(c, "ALTER TABLE Rules ADD COLUMN Enabled INTEGER NOT NULL DEFAULT 1", tx);
            migration.ExecuteNonQuery();
        }
        using var snapshot = Command(c, "INSERT OR IGNORE INTO RuleVersions SELECT Series,Version,Low,High,Hysteresis,Cooldown,Enabled FROM Rules", tx);
        snapshot.ExecuteNonQuery();
        tx.Commit();
    }

    public WriteResult Write(SensorReading r)
    {
        if (!double.IsFinite(r.Value)) throw new ArgumentException("Reading must be finite.");
        using var c = Open();
        using var tx = c.BeginTransaction();
        using var insert = Command(c, """
            INSERT INTO Readings(Source,Device,Metric,Unit,Value,EventTime,ReceivedTime,MessageId,Topic,Retained,ReceiptTimeUsed)
            VALUES($s,$d,$m,$u,$v,$t,$r,$id,$topic,$retained,$receipt) ON CONFLICT DO NOTHING;
            """, tx, ("$s", r.SourceId), ("$d", r.DeviceId), ("$m", r.Metric), ("$u", r.Unit), ("$v", r.Value),
            ("$t", r.Timestamp.ToUnixTimeMilliseconds()), ("$r", r.ReceivedAt.ToUnixTimeMilliseconds()),
            ("$id", r.MessageId), ("$topic", r.Topic), ("$retained", r.Retained), ("$receipt", r.ReceiptTimeUsed));
        if (insert.ExecuteNonQuery() == 0) { tx.Commit(); return new(false, null); }
        using var idCommand = Command(c, "SELECT last_insert_rowid()", tx);
        var id = (long)idCommand.ExecuteScalar()!;
        string? transition = null;
        var age = r.ReceivedAt - r.Timestamp;
        if (!r.Retained && age >= TimeSpan.FromSeconds(-5) && age <= TimeSpan.FromSeconds(30))
        {
            using var ruleCommand = Command(c, "SELECT Low,High,Hysteresis,Cooldown,Version,State,LastEvent,LastNotify FROM Rules WHERE Series=$s AND Enabled=1", tx, ("$s", r.SeriesKey));
            using var reader = ruleCommand.ExecuteReader();
            if (reader.Read())
            {
                var rule = new AlertRule(reader.GetDouble(0), reader.GetDouble(1), reader.GetDouble(2), reader.GetInt32(3));
                var ruleVersion = reader.GetInt32(4);
                var previous = reader.GetString(5);
                var lastEvent = reader.GetInt64(6);
                var lastNotify = reader.GetInt64(7);
                reader.Close();
                var eventTime = r.Timestamp.ToUnixTimeMilliseconds();
                if (eventTime > lastEvent)
                {
                    var next = rule.Next(previous, r.Value);
                    var now = r.ReceivedAt.ToUnixTimeMilliseconds();
                    var notify = now - lastNotify >= rule.CooldownSeconds * 1000L;
                    if (next != previous)
                    {
                        transition = $"{previous} → {next}";
                        using var alert = Command(c, "INSERT INTO AlertEvents(ReadingId,Series,Transition,Time,RuleVersion,Notify) VALUES($id,$s,$transition,$t,$version,$notify)", tx,
                            ("$id", id), ("$s", r.SeriesKey), ("$transition", transition), ("$t", now), ("$version", ruleVersion), ("$notify", notify));
                        alert.ExecuteNonQuery();
                    }
                    using var update = Command(c, "UPDATE Rules SET State=$state,LastEvent=$t,LastNotify=$n WHERE Series=$s", tx,
                        ("$state", next), ("$t", eventTime), ("$n", transition is not null && notify ? now : lastNotify), ("$s", r.SeriesKey));
                    update.ExecuteNonQuery();
                }
            }
        }
        // Reading, alert transition, and rule checkpoint are atomic: restart cannot skip an accepted reading.
        tx.Commit();
        return new(true, transition);
    }

    public void SaveRule(string series, AlertRule rule)
    {
        rule.Validate();
        using var c = Open();
        using var tx = c.BeginTransaction();
        using var command = Command(c, """
            INSERT INTO Rules(Series,Low,High,Hysteresis,Cooldown,Version) VALUES($s,$l,$h,$hy,$c,1)
            ON CONFLICT(Series) DO UPDATE SET Low=$l,High=$h,Hysteresis=$hy,Cooldown=$c,Version=Version+1,
            State='Normal',LastEvent=0,LastNotify=0,Enabled=1;
            """, tx, ("$s", series), ("$l", rule.Low), ("$h", rule.High), ("$hy", rule.Hysteresis), ("$c", rule.CooldownSeconds));
        command.ExecuteNonQuery();
        using var snapshot = Command(c, "INSERT INTO RuleVersions SELECT Series,Version,Low,High,Hysteresis,Cooldown,Enabled FROM Rules WHERE Series=$s", tx, ("$s", series));
        snapshot.ExecuteNonQuery(); tx.Commit();
    }

    public void DeleteRule(string series)
    {
        using var c = Open();
        using var tx = c.BeginTransaction();
        using var command = Command(c, "UPDATE Rules SET Enabled=0,Version=Version+1,State='Normal' WHERE Series=$s", tx, ("$s", series));
        command.ExecuteNonQuery();
        using var snapshot = Command(c, "INSERT INTO RuleVersions SELECT Series,Version,Low,High,Hysteresis,Cooldown,Enabled FROM Rules WHERE Series=$s", tx, ("$s", series));
        snapshot.ExecuteNonQuery(); tx.Commit();
    }

    public IReadOnlyList<StoredRule> Rules()
    {
        using var c = Open();
        using var command = Command(c, "SELECT Series,Low,High,Hysteresis,Cooldown,Version,State,Enabled FROM Rules ORDER BY Series");
        using var reader = command.ExecuteReader();
        var rows = new List<StoredRule>();
        while (reader.Read()) rows.Add(new(reader.GetString(0), reader.GetDouble(1), reader.GetDouble(2),
            reader.GetDouble(3), reader.GetInt32(4), reader.GetInt32(5), reader.GetString(6), reader.GetBoolean(7)));
        return rows;
    }

    public long DeleteReadingsBefore(DateTimeOffset before, CancellationToken token)
    {
        long removed = 0;
        using var c = Open();
        while (true)
        {
            token.ThrowIfCancellationRequested();
            using var command = Command(c, "DELETE FROM Readings WHERE Id IN (SELECT Id FROM Readings WHERE EventTime < $before LIMIT 1000)",
                null, ("$before", before.ToUnixTimeMilliseconds()));
            using var registration = token.Register(command.Cancel);
            var count = command.ExecuteNonQuery();
            removed += count;
            if (count == 0) return removed;
        }
    }

    public void Backup(string destination)
    {
        if (System.IO.Path.GetFullPath(destination).Equals(Path, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Choose a different backup file.");
        using var source = Open();
        using var target = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = destination }.ToString());
        target.Open(); source.BackupDatabase(target);
    }

    public IReadOnlyList<AlertEvent> Alerts()
    {
        using var c = Open();
        using var command = Command(c, "SELECT Id,Series,Transition,Time,Acknowledged,Notify FROM AlertEvents ORDER BY Id DESC LIMIT 500");
        using var reader = command.ExecuteReader();
        var result = new List<AlertEvent>();
        while (reader.Read()) result.Add(new(reader.GetInt64(0), reader.GetString(1), reader.GetString(2),
            DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(3)).ToLocalTime().ToString("G"), reader.GetBoolean(4), reader.GetBoolean(5)));
        return result;
    }

    public void Acknowledge(long id)
    {
        using var c = Open();
        using var command = Command(c, "UPDATE AlertEvents SET Acknowledged=1 WHERE Id=$id", null, ("$id", id));
        command.ExecuteNonQuery();
    }

    public long LatestId()
    {
        using var c = Open();
        using var command = Command(c, "SELECT COALESCE(MAX(Id),0) FROM Readings");
        return (long)command.ExecuteScalar()!;
    }

    public List<StoredReading> Query(HistoryFilter filter, long afterId = 0, long cutoff = long.MaxValue,
        int limit = 200, CancellationToken token = default)
    {
        if (filter.From >= filter.To || filter.Minimum > filter.Maximum) throw new ArgumentException("Invalid history range.");
        using var c = Open();
        using var command = Command(c, """
            SELECT * FROM Readings WHERE Id>$after AND Id<=$cutoff AND EventTime >= $from AND EventTime < $to
            AND ($device='' OR Device=$device) AND ($metric='' OR Metric=$metric)
            AND ($source='' OR Source=$source) AND ($unit='' OR Unit=$unit)
            AND ($min IS NULL OR Value >= $min) AND ($max IS NULL OR Value <= $max) ORDER BY Id LIMIT $limit
            """, null, ("$after", afterId), ("$cutoff", cutoff), ("$from", filter.From.ToUnixTimeMilliseconds()),
            ("$to", filter.To.ToUnixTimeMilliseconds()), ("$device", filter.Device), ("$metric", filter.Metric),
            ("$source", filter.Source), ("$unit", filter.Unit), ("$min", filter.Minimum), ("$max", filter.Maximum), ("$limit", Math.Clamp(limit, 1, 1000)));
        using var registration = token.Register(command.Cancel);
        using var reader = command.ExecuteReader();
        var rows = new List<StoredReading>();
        while (reader.Read())
        {
            token.ThrowIfCancellationRequested();
            rows.Add(new(reader.GetInt64(0), new(reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4),
                reader.GetDouble(5), DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(6)),
                DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(7)), reader.IsDBNull(8) ? null : reader.GetString(8),
                reader.GetString(9), reader.GetBoolean(10), reader.GetBoolean(11))));
        }
        return rows;
    }

    public static string CsvText(string value)
    {
        var trimmed = value.TrimStart();
        if (trimmed.Length > 0 && "=+-@".Contains(trimmed[0]) || value.StartsWith('\t') || value.StartsWith('\r') || value.StartsWith('\n')) value = "'" + value;
        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }

    public async Task<long> ExportAsync(string destination, HistoryFilter filter, CancellationToken token)
    {
        var cutoff = LatestId();
        var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".partial";
        long count = 0, after = 0;
        try
        {
            await using (var writer = new StreamWriter(temporary, false, new UTF8Encoding(true)))
            {
                await writer.WriteLineAsync("Id,Source,Device,Metric,Unit,Value,TimestampUtc,ReceivedUtc,MessageId,Topic,Retained,ReceiptTimeUsed");
                while (true)
                {
                    token.ThrowIfCancellationRequested();
                    var rows = Query(filter, after, cutoff, 1000, token);
                    if (rows.Count == 0) break;
                    foreach (var row in rows)
                    {
                        var r = row.Reading;
                        await writer.WriteLineAsync(string.Join(',', row.Id.ToString(CultureInfo.InvariantCulture), CsvText(r.SourceId),
                            CsvText(r.DeviceId), CsvText(r.Metric), CsvText(r.Unit), r.Value.ToString("R", CultureInfo.InvariantCulture),
                            r.Timestamp.ToString("O"), r.ReceivedAt.ToString("O"), CsvText(r.MessageId ?? ""), CsvText(r.Topic), r.Retained, r.ReceiptTimeUsed));
                        after = row.Id;
                        count++;
                    }
                }
            }
            token.ThrowIfCancellationRequested();
            File.Move(temporary, destination, true);
            return count;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
