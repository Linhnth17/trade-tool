using System.Globalization;
using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace BtcSignalBot;

/// <summary>
/// Lấy dữ liệu nến từ OKX: REST cho lịch sử, WebSocket cho real-time. Market data public, không cần API key.
/// <paramref name="futures"/> = true: hợp đồng vĩnh cửu (BTC-USDT-SWAP), ngược lại là Spot (BTC-USDT).
/// Symbol truyền vào theo kiểu Binance ("BTCUSDT") và được đổi sang kiểu OKX.
/// </summary>
public sealed class OkxClient(bool futures) : IMarketData
{
    private const string RestBase = "https://www.okx.com";
    // Kênh nến của OKX nằm ở /business (không phải /public)
    private const string WsUrl = "wss://ws.okx.com:8443/ws/v5/business";
    private const int PageSize = 100; // history-candles trả tối đa 100 nến/lần
    private static readonly TimeSpan SilenceTimeout = TimeSpan.FromSeconds(30);

    private readonly HttpClient _http = new() { BaseAddress = new Uri(RestBase), Timeout = TimeSpan.FromSeconds(15) };

    public string Market => futures ? "OKX Futures" : "OKX Spot";

    /// <summary>"BTCUSDT" → "BTC-USDT-SWAP" (futures) / "BTC-USDT" (spot).</summary>
    public string InstId(string symbol)
    {
        string quote = new[] { "USDT", "USDC", "USD" }.FirstOrDefault(symbol.EndsWith) ?? "USDT";
        string pair = $"{symbol[..^quote.Length]}-{quote}";
        return futures ? pair + "-SWAP" : pair;
    }

    /// <summary>"5m" → "5m", "1h" → "1H", "1d" → "1D", "1w" → "1W".</summary>
    private static string Bar(string interval) =>
        interval[^1] is 'h' or 'd' or 'w' ? interval[..^1] + char.ToUpperInvariant(interval[^1]) : interval;

    public async Task<List<Candle>> GetHistoryAsync(string symbol, string interval, int total, CancellationToken ct)
    {
        var result = new List<Candle>();
        long? before = null; // OKX "after" = lấy các nến cũ hơn mốc thời gian này
        string inst = InstId(symbol), bar = Bar(interval);

        while (result.Count < total)
        {
            string url = $"/api/v5/market/history-candles?instId={inst}&bar={bar}&limit={PageSize}" +
                         (before is { } b ? $"&after={b}" : "");
            await using var stream = await _http.GetStreamAsync(url, ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            var root = doc.RootElement;
            if (root.GetProperty("code").GetString() != "0")
                throw new InvalidOperationException($"OKX: {root.GetProperty("msg").GetString()}");

            var rows = root.GetProperty("data");
            if (rows.GetArrayLength() == 0) break;

            var batch = new List<Candle>();
            foreach (var k in rows.EnumerateArray()) // OKX trả mới → cũ
            {
                if (k[8].GetString() != "1") continue; // bỏ nến đang chạy
                batch.Add(Parse(k));
            }
            batch.Reverse();
            result.InsertRange(0, batch);
            before = long.Parse(rows[rows.GetArrayLength() - 1][0].GetString()!);
            if (rows.GetArrayLength() < PageSize) break; // hết dữ liệu

            await Task.Delay(120, ct); // giới hạn 20 request / 2 giây
        }

        return result.Count > total ? result.GetRange(result.Count - total, total) : result;
    }

    public async IAsyncEnumerable<(Candle Candle, bool IsClosed)> StreamKlinesAsync(
        string symbol, string interval, [EnumeratorCancellation] CancellationToken ct)
    {
        using var ws = new ClientWebSocket();
        await ws.ConnectAsync(new Uri(WsUrl), ct);
        string sub = $$"""{"op":"subscribe","args":[{"channel":"candle{{Bar(interval)}}","instId":"{{InstId(symbol)}}"}]}""";
        await ws.SendAsync(new ReadOnlyMemory<byte>(Encoding.UTF8.GetBytes(sub)), WebSocketMessageType.Text, true, ct);

        // OKX ngắt kết nối nếu 30 giây không có gì: gửi "ping" định kỳ
        using var pingCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _ = Task.Run(async () =>
        {
            try
            {
                while (!pingCts.IsCancellationRequested && ws.State == WebSocketState.Open)
                {
                    await Task.Delay(TimeSpan.FromSeconds(20), pingCts.Token);
                    await ws.SendAsync(new ReadOnlyMemory<byte>("ping"u8.ToArray()), WebSocketMessageType.Text, true, pingCts.Token);
                }
            }
            catch (Exception) { /* kết nối đã đóng — vòng nhận sẽ tự kết thúc */ }
        }, pingCts.Token);

        var buffer = new byte[16 * 1024];
        using var ms = new MemoryStream();
        Candle? last = null;
        bool lastClosed = false;

        try
        {
            while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                ms.SetLength(0);
                WebSocketReceiveResult r;
                do
                {
                    using var silence = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    silence.CancelAfter(SilenceTimeout);
                    try
                    {
                        r = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), silence.Token);
                    }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                    {
                        throw new TimeoutException($"Không nhận được dữ liệu sau {SilenceTimeout.TotalSeconds:F0} giây");
                    }
                    if (r.MessageType == WebSocketMessageType.Close) yield break;
                    ms.Write(buffer, 0, r.Count);
                } while (!r.EndOfMessage);

                if (ms.Length == 4 && Encoding.ASCII.GetString(ms.ToArray()) == "pong") continue;

                using var doc = JsonDocument.Parse(ms.ToArray());
                var root = doc.RootElement;
                if (root.TryGetProperty("event", out var ev) && ev.GetString() == "error")
                    throw new InvalidOperationException($"OKX: {root.GetProperty("msg").GetString()}");
                if (!root.TryGetProperty("data", out var data)) continue; // xác nhận subscribe...

                foreach (var k in data.EnumerateArray())
                {
                    var candle = Parse(k);
                    bool closed = k[8].GetString() == "1";

                    // Sang nến mới mà chưa nhận được bản "đã đóng" của nến trước → coi nến trước là đã đóng
                    if (last is not null && !lastClosed && candle.OpenTime > last.OpenTime)
                        yield return (last, true);

                    if (closed && last is not null && lastClosed && last.OpenTime == candle.OpenTime) continue; // trùng
                    yield return (candle, closed);
                    last = candle;
                    lastClosed = closed;
                }
            }
        }
        finally
        {
            pingCts.Cancel();
        }
    }

    // [ts, o, h, l, c, vol, volCcy, volCcyQuote, confirm]
    // Volume tính theo BTC: hợp đồng SWAP thì volCcy (vol là số hợp đồng), Spot thì vol.
    private Candle Parse(JsonElement k) => new(
        DateTimeOffset.FromUnixTimeMilliseconds(long.Parse(k[0].GetString()!)),
        Num(k[1]), Num(k[2]), Num(k[3]), Num(k[4]),
        Num(futures ? k[6] : k[5]));

    private static double Num(JsonElement e) =>
        double.Parse(e.GetString()!, NumberStyles.Float, CultureInfo.InvariantCulture);

    public void Dispose() => _http.Dispose();
}
