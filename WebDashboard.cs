using System.Net;
using System.Text;
using System.Text.Json;

namespace BtcSignalBot;

/// <summary>
/// Web dashboard nhỏ (HttpListener): chart nến real-time + điểm LONG/SHORT + TP/SL
/// + trạng thái phiên chạy thử. Trang tự hỏi /api/state mỗi giây.
///   GET /                   trang HTML
///   GET /api/state          JSON trạng thái mới nhất
///   GET /report.xlsx        tải báo cáo Excel của phiên đang chạy
///   GET /reports            danh sách tất cả báo cáo Excel đã lưu (mỗi phiên một file)
///   GET /health             "ok" — cho dịch vụ ping giữ app không bị ngủ (UptimeRobot...)
/// </summary>
public sealed class WebDashboard : IDisposable
{
    private const int MaxCandles = 400;
    private const string ReportsDir = "reports";

    private readonly HttpListener _listener = new();
    private volatile string _stateJson = "{}";
    private volatile string? _reportPath;

    // Cỡ lệnh dùng để quy đổi vị thế của chiến lược ra ký quỹ / PNL kiểu Binance khi lệnh không thuộc phiên thử
    private readonly double _stake, _leverage, _feePct;

    /// <param name="publicBinding">true: nghe trên mọi địa chỉ (chạy trên server/Docker); false: chỉ localhost.</param>
    public WebDashboard(int port, double stake, double leverage, double feePct, bool publicBinding = false)
    {
        Port = port;
        (_stake, _leverage, _feePct) = (stake, leverage, feePct);
        _listener.Prefixes.Add(publicBinding ? $"http://*:{port}/" : $"http://localhost:{port}/");
    }

    public int Port { get; }
    public string Url => $"http://localhost:{Port}/";

    public void Start(CancellationToken ct)
    {
        _listener.Start();
        _ = Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested && _listener.IsListening)
            {
                HttpListenerContext ctx;
                try { ctx = await _listener.GetContextAsync(); }
                catch (Exception) when (ct.IsCancellationRequested || !_listener.IsListening) { break; }
                _ = Task.Run(() => Handle(ctx));
            }
        }, ct);
    }

    private void Handle(HttpListenerContext ctx)
    {
        try
        {
            var path = ctx.Request.Url?.AbsolutePath ?? "/";
            if (path == "/api/state")
                Write(ctx, "application/json; charset=utf-8", Encoding.UTF8.GetBytes(_stateJson));
            else if (path == "/health")
                Write(ctx, "text/plain; charset=utf-8", "ok"u8.ToArray());
            else if (path == "/report.xlsx" && _reportPath is { } rp && File.Exists(rp))
                SendFile(ctx, rp);
            else if (path == "/reports")
                Write(ctx, "text/html; charset=utf-8", Encoding.UTF8.GetBytes(ReportsPage()));
            else if (path.StartsWith("/reports/") && SafeReport(Uri.UnescapeDataString(path["/reports/".Length..])) is { } file)
                SendFile(ctx, file);
            else if (path == "/")
                Write(ctx, "text/html; charset=utf-8", Encoding.UTF8.GetBytes(DashboardPage.Html));
            else
            {
                ctx.Response.StatusCode = 404;
                ctx.Response.Close();
            }
        }
        catch (Exception)
        {
            try { ctx.Response.Abort(); } catch { /* client đã ngắt */ }
        }
    }

    private static void SendFile(HttpListenerContext ctx, string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var ms = new MemoryStream();
        fs.CopyTo(ms);
        ctx.Response.AddHeader("Content-Disposition", $"attachment; filename=\"{Path.GetFileName(path)}\"");
        Write(ctx, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", ms.ToArray());
    }

    /// <summary>Chỉ cho tải file .xlsx nằm đúng trong thư mục reports (chặn kiểu "../../etc/passwd").</summary>
    private static string? SafeReport(string name)
    {
        if (name != Path.GetFileName(name) || !name.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase)) return null;
        var full = Path.Combine(ReportsDir, name);
        return File.Exists(full) ? full : null;
    }

    private static string ReportsPage()
    {
        var files = Directory.Exists(ReportsDir)
            ? new DirectoryInfo(ReportsDir).GetFiles("*.xlsx").OrderByDescending(f => f.LastWriteTimeUtc).ToList()
            : [];
        var rows = string.Concat(files.Select(f =>
            $"<tr><td><a href=\"/reports/{Uri.EscapeDataString(f.Name)}\">{WebUtility.HtmlEncode(f.Name)}</a></td>" +
            $"<td>{f.LastWriteTime:dd/MM/yyyy HH:mm}</td><td>{f.Length / 1024.0:0.#} KB</td></tr>"));
        return $$"""
            <!doctype html><html lang="vi"><head><meta charset="utf-8">
            <meta name="viewport" content="width=device-width, initial-scale=1"><title>Báo cáo</title>
            <style>body{margin:0;padding:20px 16px;background:#0b0e14;color:#e6e9ef;font:14px system-ui,sans-serif}
            a{color:#38bdf8}table{border-collapse:collapse;width:100%;max-width:720px}
            td,th{padding:8px 10px;border-bottom:1px solid #222a3a;text-align:left}th{color:#8b93a7}</style></head>
            <body><p><a href="/">← Dashboard</a></p><h2>Báo cáo Excel các phiên</h2>
            <table><tr><th>File</th><th>Cập nhật</th><th>Dung lượng</th></tr>{{(rows.Length > 0 ? rows : "<tr><td colspan=3>Chưa có báo cáo.</td></tr>")}}</table>
            <p style="color:#8b93a7">Lưu ý: gói free không có ổ đĩa lâu dài, khởi động lại / deploy lại là mất các file này.</p>
            </body></html>
            """;
    }

    private static void Write(HttpListenerContext ctx, string contentType, byte[] body)
    {
        ctx.Response.ContentType = contentType;
        ctx.Response.AddHeader("Cache-Control", "no-store");
        ctx.Response.ContentLength64 = body.Length;
        ctx.Response.OutputStream.Write(body);
        ctx.Response.Close();
    }

    /// <summary>Cập nhật trạng thái từ khung hình chart (gọi từ vòng lặp chính, ~2 lần/giây).</summary>
    public void Update(ChartFrame f, PaperSession? session)
    {
        _reportPath = session is not null && File.Exists(session.ReportPath) ? session.ReportPath : null;

        int start = Math.Max(0, f.Candles.Count - MaxCandles);
        long Sec(DateTimeOffset t) => t.ToUnixTimeSeconds();
        var candles = new List<object>();
        var emaFast = new List<object>();
        var emaSlow = new List<object>();
        var regime = new List<object>();
        for (int i = start; i < f.Candles.Count; i++)
        {
            var c = f.Candles[i];
            var s = f.Series.At(i);
            candles.Add(new { time = Sec(c.OpenTime), open = c.Open, high = c.High, low = c.Low, close = c.Close });
            // dải trạng thái dưới chart: xám = sideway (bỏ qua), xanh/đỏ nhạt = có xu hướng tăng/giảm
            bool side = i < f.Sideway.Count && f.Sideway[i];
            regime.Add(new
            {
                time = Sec(c.OpenTime), value = 1,
                color = side ? "rgba(139,147,167,.55)" : s.PlusDi >= s.MinusDi ? "rgba(34,197,94,.35)" : "rgba(239,68,68,.35)",
            });
            if (!double.IsNaN(s.EmaFast)) emaFast.Add(new { time = Sec(c.OpenTime), value = s.EmaFast });
            if (!double.IsNaN(s.EmaSlow)) emaSlow.Add(new { time = Sec(c.OpenTime), value = s.EmaSlow });
        }

        // Điểm vào/ra theo chiến lược (giống chart console)
        var viewStart = f.Candles[start].OpenTime;
        var trades = Trade.FromMarkers(f.Markers);
        var markers = new List<(long Time, object M)>();
        foreach (var t in trades)
        {
            bool isLong = t.Side == Side.Long;
            if (t.Entry.Time >= viewStart)
                markers.Add((Sec(t.Entry.Time), new
                {
                    time = Sec(t.Entry.Time), position = isLong ? "belowBar" : "aboveBar",
                    shape = isLong ? "arrowUp" : "arrowDown", color = isLong ? "#22c55e" : "#ef4444",
                    text = isLong ? "LONG" : "SHORT",
                }));
            if (t.Exit is { } ex && ex.Time >= viewStart)
                markers.Add((Sec(ex.Time), new
                {
                    time = Sec(ex.Time), position = isLong ? "aboveBar" : "belowBar",
                    shape = isLong ? "arrowDown" : "arrowUp",
                    color = t.Outcome == "TP" ? "#22c55e" : t.Outcome == "SL" ? "#ef4444" : "#eab308",
                    text = t.Outcome,
                }));
        }
        var last = f.Candles[^1];
        if (f.LastIsLive)
        {
            foreach (var p in f.Previews)
            {
                bool buy = (p.Type == SignalType.Open) == (p.Side == Side.Long);
                markers.Add((Sec(last.OpenTime), new
                {
                    time = Sec(last.OpenTime), position = buy ? "belowBar" : "aboveBar",
                    shape = buy ? "arrowUp" : "arrowDown", color = "#eab308",
                    text = p.Type == SignalType.Open ? $"{(p.Side == Side.Long ? "LONG" : "SHORT")}?" : $"{p.Outcome}?",
                }));
            }
        }

        var open = trades.Count > 0 && trades[^1].Exit is null ? trades[^1] : null;
        var preview = f.Previews.FirstOrDefault();
        var snap = f.Series.At(f.Series.Count - 1);
        var acc = session?.Account;
        var now = DateTimeOffset.UtcNow;

        // Vị thế kiểu Binance: ưu tiên lệnh của phiên thử, không có thì quy đổi lệnh của chiến lược theo cỡ lệnh mặc định
        object? binance = null;
        if (acc?.Open is { } ao)
            binance = Position(ao.EntrySignal, ao.EntryTime, ao.Margin, acc.Leverage, acc.FeePct, last.Close, inSession: true);
        else if (open is not null)
            binance = Position(open.Entry.Signal, open.Entry.Time, _stake, _leverage, _feePct, last.Close, inSession: false);

        object Position(Signal e, DateTimeOffset time, double margin, double lev, double fee, double mark, bool inSession)
        {
            double qty = margin * lev / e.Price;
            int dir = e.Side == Side.Long ? 1 : -1;
            // PNL chưa chốt kiểu Binance: chưa trừ phí; ước tính thêm phí 2 chiều để biết lãi ròng nếu đóng ngay
            double pnl = Math.Max(dir * qty * (mark - e.Price), -margin);
            double fees = qty * (e.Price + mark) * fee;
            return new
            {
                side = e.Side == Side.Long ? "LONG" : "SHORT", inSession, entryTime = time.ToUnixTimeMilliseconds(),
                leverage = lev, qty, notional = qty * mark, margin, entry = e.Price, mark,
                liq = PaperAccount.LiquidationPrice(e.Side, e.Price, lev),
                tp = e.TakeProfit, sl = e.StopLoss, pnl, roe = pnl / margin, fees, net = pnl - fees,
                tpUsd = qty * Math.Abs(e.TakeProfit - e.Price), slUsd = qty * Math.Abs(e.Price - e.StopLoss),
                rr = Math.Abs(e.TakeProfit - e.Price) / Math.Abs(e.Price - e.StopLoss),
                notes = TradeNotes.Explain(e, f.Settings, margin, lev, fee),
            };
        }

        var state = new
        {
            symbol = f.Symbol, market = f.Market, interval = f.Interval,
            price = last.Close, change = last.Close / last.Open - 1,
            updated = now.ToUnixTimeMilliseconds(),
            indicators = new
            {
                emaFast = snap.EmaFast, emaSlow = snap.EmaSlow, hist = snap.Hist, rsi = snap.Rsi, atr = snap.Atr,
                adx = snap.Adx, plusDi = snap.PlusDi, minusDi = snap.MinusDi, chop = snap.Chop, vol = snap.VolumeRatio,
                bbWidth = (snap.BbUpper - snap.BbLower) / snap.Close,
            },
            settings = new
            {
                name = f.Settings.Name, fast = f.Settings.FastEma, slow = f.Settings.SlowEma,
                adxMin = f.Settings.AdxMin, chopMax = f.Settings.ChopMax,
            },
            regime = new { sideway = f.Sideway.Count > 0 && f.Sideway[^1], label = f.RegimeLabel },
            candles, emaFast, emaSlow, regimeBars = regime,
            markers = markers.OrderBy(m => m.Time).Select(m => m.M),
            position = open is null ? null : new
            {
                side = open.Side == Side.Long ? "LONG" : "SHORT",
                entry = open.EntryPrice, tp = open.TakeProfit, sl = open.StopLoss,
                entryTime = Sec(open.Entry.Time), pnl = open.ReturnAt(last.Close), rr = open.RiskReward,
            },
            binance,
            preview = preview is null ? null : new
            {
                type = preview.Type == SignalType.Open ? "open" : "close",
                side = preview.Side == Side.Long ? "LONG" : "SHORT",
                price = preview.Price, tp = preview.TakeProfit, sl = preview.StopLoss, reason = preview.Reason,
                rr = preview.Plan?.RiskReward,
            },
            longConditions = f.LongConditions.Select(c => new { name = c.Name, ok = c.Ok }),
            shortConditions = f.ShortConditions.Select(c => new { name = c.Name, ok = c.Ok }),
            note = f.Note,
            session = acc is null ? null : new
            {
                start = acc.Start.ToUnixTimeMilliseconds(), end = acc.End.ToUnixTimeMilliseconds(),
                remaining = Math.Max(0, (int)(acc.End - now).TotalSeconds),
                capital = acc.InitialCapital, stake = acc.MarginPerTrade, leverage = acc.Leverage, fee = acc.FeePct,
                equity = acc.Equity(last.Close), balance = acc.Balance, pnl = acc.TotalPnl,
                wins = acc.Wins, losses = acc.Losses,
                hasReport = _reportPath is not null,
                open = acc.Open is { } o ? new
                {
                    side = o.Side == Side.Long ? "LONG" : "SHORT", entry = o.EntryPrice,
                    liq = o.LiquidationPrice, unrealized = acc.Equity(last.Close) - acc.Balance,
                } : null,
                trades = acc.Trades.AsEnumerable().Reverse().Select(t => new
                {
                    no = t.No, side = t.Side == Side.Long ? "LONG" : "SHORT",
                    entryTime = t.EntryTime.ToUnixTimeMilliseconds(), entry = t.EntryPrice, tp = t.TakeProfit, sl = t.StopLoss,
                    liq = t.LiquidationPrice, rr = Math.Abs(t.TakeProfit - t.EntryPrice) / Math.Abs(t.EntryPrice - t.StopLoss),
                    exitTime = t.ExitTime?.ToUnixTimeMilliseconds(), exit = t.ExitPrice,
                    outcome = t.Outcome ?? "ĐANG MỞ", pnl = t.IsOpen ? (double?)null : t.Pnl, fees = t.Fees,
                    notes = TradeNotes.Explain(t.EntrySignal, f.Settings, t.Margin, acc.Leverage, acc.FeePct,
                        t.Outcome, t.ExitReason, t.ExitPrice, t.IsOpen ? null : t.Pnl),
                }),
            },
        };
        _stateJson = JsonSerializer.Serialize(state, JsonOpts);
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals,
    };

    public void Dispose()
    {
        try { _listener.Stop(); _listener.Close(); } catch { /* đã đóng */ }
    }
}
