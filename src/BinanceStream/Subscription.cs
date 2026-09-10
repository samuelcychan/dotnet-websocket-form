using System.Text.RegularExpressions;

namespace BinanceStream;

public enum StreamKind { Trade, AggregateTrade, BookTicker }

public sealed record Subscription
{
    public string Symbol { get; }
    public StreamKind Kind { get; }
    public Subscription(string symbol, StreamKind kind)
    {
        ArgumentNullException.ThrowIfNull(symbol);
        if (!Regex.IsMatch(symbol, "^[a-zA-Z0-9]{2,30}$"))
            throw new ArgumentException("Use a 2–30 character alphanumeric symbol, for example BTCUSDT.", nameof(symbol));
        if (!Enum.IsDefined(kind)) throw new ArgumentOutOfRangeException(nameof(kind));
        Symbol = symbol.ToLowerInvariant();
        Kind = kind;
    }
    public override string ToString() => $"{Symbol}@{Kind switch
    {
        StreamKind.Trade => "trade",
        StreamKind.AggregateTrade => "aggTrade",
        _ => "bookTicker"
    }}";
}
