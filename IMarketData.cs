namespace BtcSignalBot;

/// <summary>Nguồn dữ liệu nến của một sàn: lịch sử qua REST, real-time qua WebSocket.</summary>
public interface IMarketData : IDisposable
{
    /// <summary>Tên hiển thị, vd. "OKX Futures".</summary>
    string Market { get; }

    /// <summary>Tải tối đa <paramref name="total"/> nến ĐÃ ĐÓNG gần nhất, cũ → mới.</summary>
    Task<List<Candle>> GetHistoryAsync(string symbol, string interval, int total, CancellationToken ct);

    /// <summary>Stream nến real-time. Trả về (nến, đã đóng chưa). Kết thúc khi server đóng kết nối.</summary>
    IAsyncEnumerable<(Candle Candle, bool IsClosed)> StreamKlinesAsync(string symbol, string interval, CancellationToken ct);
}
