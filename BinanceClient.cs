using System.Globalization;
using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace BtcSignalBot;

/// <summary>
/// Lấy dữ liệu nến từ Binance: REST cho lịch sử, WebSocket cho real-time.
/// Market data public, không cần API key.
/// <paramref name="futures"/> = true: hợp đồng vĩnh cửu USDⓈ-M (BTCUSDT.P trên TradingView), ngược lại là Spot.
/// </summary>
public sealed class BinanceClient(bool futures) : IDisposable
{
    private readonly string _klinesPath = futures ? "/fapi/v1/klines" : "/api/v3/klines";
    // Futures: stream nến nằm ở /market/ws; đường /ws cũ vẫn kết nối được nhưng không gửi dữ liệu kline.
    private readonly string _wsBase = futures ? "wss://fstream.binance.com/market/ws" : "wss://stream.binance.com:9443/ws";
    private static readonly TimeSpan SilenceTimeout = TimeSpan.FromSeconds(30);

    private readonly HttpClient _http = new()
    {
        BaseAddress = new Uri(futures ? "https://fapi.binance.com" : "https://api.binance.com"),
        Timeout = TimeSpan.FromSeconds(15)
    };

    /// <summary>
    /// Tải tối đa <paramref name="total"/> nến ĐÃ ĐÓNG gần nhất (phân trang lùi, 1000 nến/lần).
    /// </summary>
    public async Task<List<Candle>> GetHistoryAsync(string symbol, string interval, int total, CancellationToken ct)
    {
        var result = new List<Candle>();
        long? endTime = null;
        long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        while (result.Count < total)
        {
            int limit = Math.Min(1000, total - result.Count);
            string url = $"{_klinesPath}?symbol={symbol}&interval={interval}&limit={limit}"
                         + (endTime is { } e ? $"&endTime={e}" : "");

            await using var stream = await _http.GetStreamAsync(url, ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

            var batch = new List<Candle>();
            foreach (var k in doc.RootElement.EnumerateArray())
            {
                long closeTime = k[6].GetInt64();
                if (closeTime >= nowMs) continue; // bỏ nến đang chạy, chỉ giữ nến đã đóng
                batch.Add(new Candle(
                    DateTimeOffset.FromUnixTimeMilliseconds(k[0].GetInt64()),
                    ParseNum(k[1]), ParseNum(k[2]), ParseNum(k[3]), ParseNum(k[4]), ParseNum(k[5])));
            }

            if (batch.Count == 0) break;
            result.InsertRange(0, batch);
            endTime = batch[0].OpenTime.ToUnixTimeMilliseconds() - 1;
            if (batch.Count < limit - 1) break; // hết dữ liệu
        }

        return result;
    }

    /// <summary>
    /// Stream nến real-time. Trả về (nến, đã đóng chưa). Kết thúc khi server đóng kết nối.
    /// </summary>
    public async IAsyncEnumerable<(Candle Candle, bool IsClosed)> StreamKlinesAsync(
        string symbol, string interval, [EnumeratorCancellation] CancellationToken ct)
    {
        using var ws = new ClientWebSocket();
        ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
        await ws.ConnectAsync(new Uri($"{_wsBase}/{symbol.ToLowerInvariant()}@kline_{interval}"), ct);

        var buffer = new byte[16 * 1024];
        using var ms = new MemoryStream();

        while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            ms.SetLength(0);
            WebSocketReceiveResult r;
            do
            {
                // Kline gửi ~mỗi 250ms-2s; im lặng quá lâu = kết nối "chết" -> ném lỗi để vòng ngoài kết nối lại
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

            using var doc = JsonDocument.Parse(ms.ToArray());
            if (!doc.RootElement.TryGetProperty("k", out var k)) continue;

            var candle = new Candle(
                DateTimeOffset.FromUnixTimeMilliseconds(k.GetProperty("t").GetInt64()),
                ParseNum(k.GetProperty("o")),
                ParseNum(k.GetProperty("h")),
                ParseNum(k.GetProperty("l")),
                ParseNum(k.GetProperty("c")),
                ParseNum(k.GetProperty("v")));

            yield return (candle, k.GetProperty("x").GetBoolean());
        }
    }

    // Binance trả số dạng chuỗi "68123.45" -> bắt buộc InvariantCulture,
    // nếu không máy để locale vi-VN sẽ parse sai dấu thập phân.
    private static double ParseNum(JsonElement e) =>
        double.Parse(e.GetString()!, NumberStyles.Float, CultureInfo.InvariantCulture);

    public void Dispose() => _http.Dispose();
}
