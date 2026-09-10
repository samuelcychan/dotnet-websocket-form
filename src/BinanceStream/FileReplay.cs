using System.Runtime.CompilerServices;

namespace BinanceStream;

public static class FileReplay
{
    /// <summary>Replay one raw or combined Binance JSON message per line.</summary>
    public static async IAsyncEnumerable<MarketEvent> ReadAsync(string path, TimeSpan interval,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (interval < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(interval));
        using var reader = File.OpenText(path);
        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            yield return MarketEventParser.Parse(line);
            await Task.Delay(interval, cancellationToken).ConfigureAwait(false);
        }
    }
}
