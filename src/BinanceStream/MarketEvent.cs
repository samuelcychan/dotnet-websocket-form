using System.Globalization;
using System.Text.Json;

namespace BinanceStream;

public abstract record MarketEvent(string Symbol);
public sealed record Trade(string Symbol, long Id, decimal Price, decimal Quantity,
    DateTimeOffset Time, bool BuyerIsMaker, bool Aggregated) : MarketEvent(Symbol);
public sealed record BookTicker(string Symbol, long UpdateId, decimal BidPrice,
    decimal BidQuantity, decimal AskPrice, decimal AskQuantity) : MarketEvent(Symbol);
public sealed record UnknownEvent(string Symbol, string Json) : MarketEvent(Symbol);

public static class MarketEventParser
{
    public static MarketEvent Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.TryGetProperty("data", out var data)) root = data;
        var symbol = root.TryGetProperty("s", out var s) ? s.GetString() ?? "" : "";
        var type = root.TryGetProperty("e", out var e) ? e.GetString() : null;
        if (type is "trade" or "aggTrade")
            return new Trade(symbol, root.GetProperty(type == "trade" ? "t" : "a").GetInt64(),
                Number(root, "p"), Number(root, "q"),
                DateTimeOffset.FromUnixTimeMilliseconds(root.GetProperty("T").GetInt64()),
                root.GetProperty("m").GetBoolean(), type == "aggTrade");
        if (root.TryGetProperty("u", out var update) && root.TryGetProperty("b", out _)
            && root.TryGetProperty("a", out _))
            return new BookTicker(symbol, update.GetInt64(), Number(root, "b"), Number(root, "B"),
                Number(root, "a"), Number(root, "A"));
        return new UnknownEvent(symbol, root.GetRawText());
    }

    private static decimal Number(JsonElement root, string name) =>
        decimal.Parse(root.GetProperty(name).GetString()!, CultureInfo.InvariantCulture);
}
