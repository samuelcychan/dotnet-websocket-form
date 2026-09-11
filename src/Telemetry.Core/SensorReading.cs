using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Telemetry.Core;

public sealed record SensorReading(string SourceId, string DeviceId, string Metric, string Unit,
    double Value, DateTimeOffset Timestamp, DateTimeOffset ReceivedAt, string? MessageId,
    string Topic, bool Retained = false, bool ReceiptTimeUsed = false)
{
    public string SeriesKey => JsonSerializer.Serialize(new[] { SourceId, DeviceId, Metric, Unit });
}

public static class ReadingParser
{
    public const int MaximumPayloadBytes = 65536;

    public static SensorReading Parse(string json, string sourceId, string topic, bool retained = false,
        string? flatPressureDevice = null, DateTimeOffset? receivedAt = null)
    {
        if (Encoding.UTF8.GetByteCount(json) > MaximumPayloadBytes) throw new FormatException("Payload exceeds 64 KiB.");
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw new FormatException("Payload must be an object.");
        var now = receivedAt ?? DateTimeOffset.UtcNow;
        string Required(string name)
        {
            if (!root.TryGetProperty(name, out var field) || field.ValueKind != JsonValueKind.String)
                throw new FormatException($"Missing string field: {name}.");
            var text = field.GetString()!;
            if (string.IsNullOrWhiteSpace(text) || text.Length > 128 || text.Any(char.IsControl))
                throw new FormatException($"Invalid field: {name}.");
            return text;
        }
        if (flatPressureDevice is not null)
        {
            if (!root.TryGetProperty("pressure", out var pressure) || pressure.ValueKind != JsonValueKind.Number || !pressure.TryGetDouble(out var p) || !double.IsFinite(p))
                throw new FormatException("Flat pressure profile requires a finite pressure number.");
            return new(sourceId, flatPressureDevice, "pressure", "hPa", p, now, now, null, topic, retained, true);
        }
        if (!root.TryGetProperty("schemaVersion", out var version) || version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out var v) || v != 1)
            throw new FormatException("Unsupported schemaVersion; expected 1.");
        if (!root.TryGetProperty("value", out var number) || number.ValueKind != JsonValueKind.Number ||
            !number.TryGetDouble(out var value) || !double.IsFinite(value)) throw new FormatException("value must be finite.");
        var timestamp = Required("timestamp");
        if (!(timestamp.EndsWith('Z') || timestamp.Length >= 6 && timestamp[^3] == ':' && timestamp[^6] is '+' or '-') ||
            !root.GetProperty("timestamp").TryGetDateTimeOffset(out var time))
            throw new FormatException("timestamp requires an ISO 8601 timezone.");
        var id = root.TryGetProperty("messageId", out var messageId) && messageId.ValueKind != JsonValueKind.Null
            ? Required("messageId") : null;
        return new(sourceId, Required("deviceId"), Required("metric"), Required("unit"), value,
            time.ToUniversalTime(), now, id, topic, retained);
    }
}

public sealed record AlertRule(double Low, double High, double Hysteresis = 1, int CooldownSeconds = 30)
{
    public void Validate()
    {
        if (!double.IsFinite(Low) || !double.IsFinite(High) || !double.IsFinite(Hysteresis) ||
            Low >= High || Hysteresis < 0 || Hysteresis >= (High - Low) / 2 || CooldownSeconds < 0)
            throw new ArgumentException("Require low < high, hysteresis >= 0 and < half the range, and cooldown >= 0.");
    }

    public string Next(string previous, double value) => previous switch
    {
        "Low" when value < Low + Hysteresis => "Low",
        "High" when value > High - Hysteresis => "High",
        _ when value < Low => "Low",
        _ when value > High => "High",
        _ => "Normal"
    };
}
