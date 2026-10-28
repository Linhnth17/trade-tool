using System.Globalization;
using System.Text;
using BtcSignalBot;

Console.OutputEncoding = Encoding.UTF8;

// Cách chạy: dotnet run -- [SYMBOL] [INTERVAL] [tùy chọn]   ví dụ: dotnet run -- BTCUSDT.P 5m
//   --preset scalp|swing : chiến lược (mặc định: nến ≤ 5m → scalp, lớn hơn → swing)
//   --exchange okx|binance : nguồn dữ liệu (mặc định okx)
//   mặc định       : chart real-time ngay trong console, đánh dấu điểm LONG/SHORT + TP/SL
//   --log          : chỉ in log từng nến (chế độ cũ)
//   --session 6h   : chạy thử giao dịch giả lập trong 6 giờ, hết giờ tự tổng kết + xuất Excel
//   --replay       : đi kèm --session: mô phỏng ngay trên dữ liệu 6 giờ gần nhất thay vì chờ real-time
//   --capital 1000 --stake 10 --leverage 1 : vốn, tiền mỗi lệnh, đòn bẩy cho phiên thử
//   --port 5001    : mở thêm web dashboard tại http://localhost:5001
//                    (có biến môi trường PORT, ví dụ trên Render, thì nghe trên mọi địa chỉ ở cổng đó)
//   --repeat       : đi kèm --session: hết phiên thì tự tổng kết rồi mở phiên mới (chạy liên tục trên server)
//   --headless     : không vẽ chart console, chỉ in log (tự bật khi output bị chuyển hướng, vd. chạy trong Docker)
var options = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
var positional = new List<string>();
for (int i = 0; i < args.Length; i++)
{
    if (!args[i].StartsWith("--")) { positional.Add(args[i]); continue; }
    bool hasValue = i + 1 < args.Length && !args[i + 1].StartsWith("--");
    options[args[i][2..]] = hasValue ? args[++i] : null;
}
double NumOpt(string name, double fallback) =>
    options.TryGetValue(name, out var v) && double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)
        ? d : fallback;

bool logMode = options.ContainsKey("log");
bool replay = options.ContainsKey("replay");
bool repeat = options.ContainsKey("repeat");
bool headless = options.ContainsKey("headless") || Console.IsOutputRedirected;
string? envPort = Environment.GetEnvironmentVariable("PORT");
TimeSpan? sessionLength = options.TryGetValue("session", out var sl) ? ParseInterval(sl ?? "6h") : null;
double capital = NumOpt("capital", 1000), stake = NumOpt("stake", 10), leverage = NumOpt("leverage", 1);

// Hậu tố ".P" như TradingView = hợp đồng vĩnh cửu (Futures); không có thì là Spot.
string displaySymbol = positional.Count > 0 ? positional[0].ToUpperInvariant() : "BTCUSDT.P";
bool futures = displaySymbol.EndsWith(".P");
string symbol = futures ? displaySymbol[..^2] : displaySymbol;
// --exchange okx|binance (mặc định OKX: Binance chặn nhiều máy chủ cloud)
string exchange = options.TryGetValue("exchange", out var ex0) && ex0 is not null ? ex0.ToLowerInvariant() : "okx";
using IMarketData client = exchange == "binance" ? new BinanceClient(futures) : new OkxClient(futures);
string market = client.Market;
string interval = positional.Count > 1 ? positional[1] : "5m";
var period = ParseInterval(interval);
const int HistoryCandles = 5000;   // 5m ≈ 17 ngày, 15m ≈ 52 ngày: đủ làm "ấm" chỉ báo + có dữ liệu để backtest
const int MaxCandlesInMemory = 6000;

// --preset scalp|swing; mặc định: nến ≤ 5m → lướt sóng (scalp), lớn hơn → swing
double feePct = futures ? 0.0005 : 0.001; // phí taker Futures 0.05%
string preset = options.TryGetValue("preset", out var pr) && pr is not null
    ? pr.ToLowerInvariant()
    : period <= TimeSpan.FromMinutes(5) ? "scalp" : "swing";
var settings = preset == "scalp" ? StrategySettings.Scalp(feePct) : StrategySettings.Swing(feePct);

// --set "MinVolumeRatio=0.8,AdxMin=18": chỉnh nhanh tham số chiến lược mà không sửa code
if (options.TryGetValue("set", out var overrides) && overrides is not null)
{
    foreach (var pair in overrides.Split(',', StringSplitOptions.RemoveEmptyEntries))
    {
        var kv = pair.Split('=', 2);
        var prop = typeof(StrategySettings).GetProperty(kv[0].Trim());
        if (prop is null || kv.Length < 2) { Console.WriteLine($"Bỏ qua tham số không hợp lệ: {pair}"); continue; }
        prop.SetValue(settings, Convert.ChangeType(kv[1].Trim(), prop.PropertyType, CultureInfo.InvariantCulture));
    }
}

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
// Docker/Render dừng app bằng SIGTERM: xử lý như Ctrl+C để kịp tổng kết + lưu Excel
using var sigterm = System.Runtime.InteropServices.PosixSignalRegistration.Create(
    System.Runtime.InteropServices.PosixSignal.SIGTERM, ctx => { ctx.Cancel = true; cts.Cancel(); });

WebDashboard? dashboard = null;
if (!replay && (envPort is not null || options.ContainsKey("port")))
{
    int port = envPort is not null ? int.Parse(envPort) : (int)NumOpt("port", 5001);
    dashboard = new WebDashboard(port, stake, leverage, settings.FeePct, publicBinding: envPort is not null);
    dashboard.Start(cts.Token);
    Console.WriteLine($"Web dashboard: {dashboard.Url}");
}
using var _dashboard = dashboard;

PaperSession NewSession(DateTimeOffset start, DateTimeOffset end) => new(
    new PaperAccount(capital, stake, leverage, settings.FeePct, start, end),
    Path.Combine("reports", $"{displaySymbol}_{interval}_{start.ToLocalTime():yyyyMMdd_HHmm}" +
                            $"{(replay ? "_replay" : "")}.xlsx"),
    displaySymbol, market, interval, settings);

// Phiên chạy thử real-time: bắt đầu từ bây giờ, giữ nguyên qua các lần kết nối lại
var now0 = DateTimeOffset.UtcNow;
PaperSession? session = sessionLength is { } len && !replay ? NewSession(now0, now0 + len) : null;
bool sessionDone = false;

while (true)
{
    while (!cts.IsCancellationRequested && !sessionDone)
    {
        try
        {
            // 1) Lịch sử
            dashboard?.SetStatus($"Đang tải {HistoryCandles} nến {displaySymbol} {interval} từ {market}…");
            var candles = await client.GetHistoryAsync(symbol, interval, HistoryCandles, cts.Token);
            if (candles.Count < 300) throw new InvalidOperationException($"{market} chỉ trả về {candles.Count} nến, không đủ để tính chỉ báo");
            Console.WriteLine($"Đã tải {candles.Count} nến {displaySymbol} ({market}) {interval} " +
                              $"({candles[0].OpenTime.ToLocalTime():dd/MM HH:mm} → {candles[^1].OpenTime.ToLocalTime():dd/MM HH:mm})");

            // 2) Backtest nhanh
            var bt = Backtester.Run(candles, settings);
            double winRate = bt.Trades > 0 ? (double)bt.Wins / bt.Trades : 0;
            Console.WriteLine($"── Backtest · chiến lược {settings.Name} ─────────────");
            Console.WriteLine($"  Số lệnh: {bt.Trades}, thắng: {bt.Wins} ({winRate:P0})");
            Console.WriteLine($"  Lợi nhuận chiến lược: {bt.StrategyReturnPct:F2}%  |  Mua & giữ: {bt.BuyAndHoldPct:F2}%");
            Console.WriteLine($"  Max drawdown: {bt.MaxDrawdownPct:F2}%  (đã tính phí {settings.FeePct:0.00#%}/chiều)");
            Console.WriteLine("─────────────────────────────────────────");

            // --diag: trong các nến có tín hiệu điểm vào, điều kiện nào chặn lệnh nhiều nhất (để chỉnh bộ lọc)
            if (options.ContainsKey("diag"))
            {
                var ds = new IndicatorSeries(candles, settings);
                var probe = new MacdTrendStrategy(settings);
                var fails = new Dictionary<string, int>();
                int crosses = 0, passed = 0, sidewayBars = 0, ready = 0;
                for (int i = 0; i < ds.Count; i++)
                {
                    var x = ds.At(i);
                    if (!x.IsReady) continue;
                    ready++;
                    if (probe.Regime(x).Sideway) sidewayBars++;
                    // Classic: điều kiện đầu là MACD cắt; các chế độ mới: điều kiện thứ 3 là điểm vào
                    int trig = settings.EntryMode == EntryModes.Classic ? 0 : 2;
                    foreach (var conds in new[] { probe.LongConditions(x), probe.ShortConditions(x) })
                    {
                        if (!conds[trig].Ok) continue; // chỉ xét nến có tín hiệu điểm vào đúng chiều
                        crosses++;
                        if (conds.All(c => c.Ok)) passed++;
                        foreach (var c in conds.Where((c, k) => k != trig && !c.Ok))
                        {
                            string key = c.Name.Replace("<", "").Replace(">", "").Trim();
                            fails[key] = fails.GetValueOrDefault(key) + 1;
                        }
                    }
                }
                Console.WriteLine($"Sideway: {sidewayBars}/{ready} nến ({(double)sidewayBars / ready:P0}). Chế độ {settings.EntryMode}: có tín hiệu điểm vào {crosses}, đủ điều kiện: {passed}");
                foreach (var (k, v) in fails.OrderByDescending(kv => kv.Value)) Console.WriteLine($"  {v,4} ({(double)v / crosses,4:P0})  {k}");
                break;
            }

            // 3) Replay lịch sử để đồng bộ trạng thái (đang giữ lệnh hay không) và lấy các điểm vào/ra cũ
            PaperSession? replaySession = null;
            if (replay && sessionLength is { } rl)
            {
                var end = candles[^1].OpenTime + period;
                replaySession = NewSession(end - rl, end);
                replaySession.StartPrice = candles.Last(c => c.OpenTime + period <= end - rl).Close;
                replaySession.LastPrice = candles[^1].Close;
            }

            var strategy = new MacdTrendStrategy(settings);
            var series = new IndicatorSeries(candles, settings);
            var markers = new List<TradeMarker>();
            for (int i = 0; i < series.Count; i++)
            {
                var s = series.At(i);
                replaySession?.Account.OnCandle(candles[i], s.Time + period);
                foreach (var sig in strategy.Evaluate(s))
                {
                    markers.Add(new TradeMarker(s.Time, sig));
                    replaySession?.Account.OnSignal(sig, s.Time + period); // tín hiệu xác nhận lúc nến đóng
                }
            }

            if (replaySession is not null)
            {
                replaySession.Account.Finish(replaySession.Account.End, replaySession.LastPrice);
                replaySession.Save(replaySession.Account.End);
                replaySession.PrintSummary(replaySession.Account.End);
                sessionDone = true;
                break;
            }

            if (session is not null && double.IsNaN(session.StartPrice)) session.StartPrice = candles[^1].Close;

            if (!logMode)
            {
                string note = $"Backtest {bt.Trades} lệnh, thắng {winRate:P0}, " +
                              $"chiến lược {bt.StrategyReturnPct:+0.00;-0.00}% vs mua&giữ {bt.BuyAndHoldPct:+0.00;-0.00}%";
                sessionDone = await RunChartAsync(client, symbol, displaySymbol, market, interval, period, candles,
                    strategy, markers, settings, note, session, dashboard, headless, cts.Token);
                if (sessionDone) break;
            }
            else
            {
                Console.WriteLine(strategy.Position is { } side
                    ? $"Trạng thái: ĐANG {(side == Side.Long ? "LONG" : "SHORT")} (giá vào {strategy.EntryPrice:N2}, " +
                      $"TP {strategy.TakeProfit:N2}, SL {strategy.StopLoss:N2})"
                    : "Trạng thái: chờ tín hiệu");
                PrintSnapshot(series.At(series.Count - 1));

                // 4) Real-time
                Console.WriteLine($"Đang nghe real-time... (Ctrl+C để thoát)");
                await foreach (var (candle, isClosed) in client.StreamKlinesAsync(symbol, interval, cts.Token))
                {
                    if (session is not null) session.LastPrice = candle.Close;
                    if (CheckTargetsNow(strategy, candle, session) is { } hit)
                    {
                        Console.WriteLine();
                        PrintSignal(hit);
                        session?.Save(DateTimeOffset.UtcNow);
                    }
                    if (session is not null && SessionFinished(session)) { sessionDone = true; break; }
                    if (!isClosed)
                    {
                        Console.Write($"\r  Giá hiện tại: {candle.Close,12:N2}   ");
                        continue;
                    }

                    AddClosed(candles, candle);
                    series = new IndicatorSeries(candles, settings); // vài nghìn nến, tính lại toàn bộ chỉ mất vài ms
                    var snap = series.At(series.Count - 1);

                    Console.WriteLine();
                    PrintSnapshot(snap);
                    session?.Account.OnCandle(candle, snap.Time + period);
                    foreach (var signal in strategy.Evaluate(snap))
                    {
                        PrintSignal(signal);
                        session?.Account.OnSignal(signal, snap.Time + period);
                    }
                    session?.Save(DateTimeOffset.UtcNow);
                    if (session is not null && SessionFinished(session)) { sessionDone = true; break; }
                }
                if (sessionDone) break;
            }

            Console.WriteLine("\nWebSocket bị đóng, kết nối lại...");
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            break;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"\nLỗi: {ex.Message}. Thử lại sau 5 giây...");
            dashboard?.SetStatus($"Lỗi kết nối {market}: {ex.Message} (thử lại sau 5 giây)", error: true);
        }

        try { await Task.Delay(TimeSpan.FromSeconds(5), cts.Token); }
        catch (OperationCanceledException) { break; }
    }

    // Kết thúc phiên: hết giờ và lệnh cuối đã chạm TP/SL, hoặc Ctrl+C giữa chừng (đóng lệnh đang mở ở giá hiện tại)
    if (session is not null && !double.IsNaN(session.LastPrice))
    {
        var acc = session.Account;
        var now = DateTimeOffset.UtcNow;
        var lastExit = acc.Trades.Select(t => t.ExitTime).LastOrDefault() ?? acc.End;
        var end = acc.Open is not null || now < acc.End ? now : (lastExit > acc.End ? lastExit : acc.End);
        acc.Finish(end, session.LastPrice);
        session.Save(end);
        session.PrintSummary(end);
    }

    // --repeat: phiên vừa hết giờ (không phải do tắt app) → mở phiên mới, chạy liên tục
    if (repeat && sessionDone && session is not null && sessionLength is { } nextLen && !cts.IsCancellationRequested)
    {
        var t = DateTimeOffset.UtcNow;
        session = NewSession(t, t + nextLen);
        sessionDone = false;
        Console.WriteLine($"Bắt đầu phiên mới: {t.ToLocalTime():dd/MM HH:mm} → {(t + nextLen).ToLocalTime():dd/MM HH:mm}");
        continue;
    }
    break;
}

Console.WriteLine("Đã dừng.");

// Hết 6h thì không mở lệnh mới, nhưng lệnh đang mở được chạy tiếp tới khi chạm TP/SL
static bool SessionFinished(PaperSession s) => DateTimeOffset.UtcNow >= s.Account.End && s.Account.Open is null;

/// <summary>
/// Kiểm tra thanh lý + TP/SL theo giá real-time của nến đang chạy, không chờ đóng nến.
/// Trả về tín hiệu đóng lệnh nếu vừa chạm.
/// </summary>
static Signal? CheckTargetsNow(MacdTrendStrategy strategy, Candle candle, PaperSession? session)
{
    var now = DateTimeOffset.UtcNow;
    session?.Account.OnCandle(candle, now);
    var exit = strategy.CheckTargets(candle.High, candle.Low);
    if (exit is not null) session?.Account.OnSignal(exit, now);
    return exit;
}

/// <returns>true nếu phiên chạy thử đã hết giờ.</returns>
static async Task<bool> RunChartAsync(
    IMarketData client, string symbol, string displaySymbol, string market, string interval, TimeSpan period,
    List<Candle> candles, MacdTrendStrategy strategy, List<TradeMarker> markers, StrategySettings settings,
    string note, PaperSession? session, WebDashboard? dashboard, bool headless, CancellationToken ct)
{
    Candle? live = null;
    var lastDraw = DateTime.MinValue;
    // Server (headless) không có màn hình: chỉ cập nhật web, thưa hơn cho đỡ tốn CPU của gói free
    int drawEveryMs = headless ? 2000 : 500;

    void Draw()
    {
        if (headless && dashboard is null) return;
        bool hasLive = live is not null && live.OpenTime > candles[^1].OpenTime;
        var view = hasLive ? new List<Candle>(candles) { live! } : candles;
        var series = new IndicatorSeries(view, settings);
        var snap = series.At(series.Count - 1);

        var sideway = new bool[series.Count];
        for (int i = Math.Max(0, series.Count - 600); i < series.Count; i++) // chỉ cần phần đang hiển thị
            sideway[i] = strategy.Regime(series.At(i)).Sideway;

        var frame = new ChartFrame(
            displaySymbol, market, interval, view, series, hasLive, markers,
            hasLive ? strategy.Preview(snap) : [],
            strategy.LongConditions(snap),
            strategy.ShortConditions(snap),
            settings,
            session?.StatusLine(DateTimeOffset.UtcNow) ?? note,
            sideway,
            strategy.Regime(snap).Label);
        if (!headless) ConsoleChart.Render(frame);
        dashboard?.Update(frame, session);
        lastDraw = DateTime.UtcNow;
    }

    void Alert(Signal s)
    {
        if (headless) PrintSignal(s); // log server
        else Console.Beep();
    }

    if (!headless) ConsoleChart.EnterScreen();
    else Console.WriteLine($"Đang chạy real-time {displaySymbol} {interval} (headless)...");
    try
    {
        Draw();
        await foreach (var (candle, isClosed) in client.StreamKlinesAsync(symbol, interval, ct))
        {
            if (session is not null) session.LastPrice = candle.Close;

            // TP/SL khớp ngay khi giá chạm, không chờ đóng nến
            if (CheckTargetsNow(strategy, candle, session) is { } hit)
            {
                markers.Add(new TradeMarker(candle.OpenTime, hit));
                session?.Save(DateTimeOffset.UtcNow);
                Alert(hit);
                lastDraw = DateTime.MinValue; // vẽ lại ngay
            }

            if (!isClosed)
            {
                live = candle;
                if ((DateTime.UtcNow - lastDraw).TotalMilliseconds >= drawEveryMs) Draw();
            }
            else
            {
                // Nến đóng: chốt tín hiệu thật (không vẽ lại)
                AddClosed(candles, candle);
                live = null;
                var series = new IndicatorSeries(candles, settings);
                var snap = series.At(series.Count - 1);
                session?.Account.OnCandle(candle, snap.Time + period); // thanh lý (nếu có) xảy ra trước tín hiệu
                foreach (var signal in strategy.Evaluate(snap))
                {
                    markers.Add(new TradeMarker(snap.Time, signal));
                    session?.Account.OnSignal(signal, snap.Time + period);
                    Alert(signal);
                }
                session?.Save(DateTimeOffset.UtcNow); // lưu dần, lỡ tắt app giữa chừng vẫn có file
                if (headless)
                    Console.WriteLine($"[{DateTime.Now:dd/MM HH:mm}] Đóng {candle.Close:N2} · {strategy.Regime(snap).Label}" +
                                      (session is not null ? $" · {session.StatusLine(DateTimeOffset.UtcNow)}" : ""));
                Draw();
            }

            if (session is not null && SessionFinished(session)) return true;
        }
        return false;
    }
    finally
    {
        if (!headless) ConsoleChart.LeaveScreen();
    }
}

static void AddClosed(List<Candle> candles, Candle candle)
{
    if (candles[^1].OpenTime == candle.OpenTime) candles[^1] = candle;
    else candles.Add(candle);
    if (candles.Count > MaxCandlesInMemory) candles.RemoveRange(0, candles.Count - MaxCandlesInMemory);
}

static TimeSpan ParseInterval(string s)
{
    if (s.Length < 2 || !int.TryParse(s[..^1], out int n)) return TimeSpan.FromMinutes(15);
    return s[^1] switch
    {
        'm' => TimeSpan.FromMinutes(n),
        'h' => TimeSpan.FromHours(n),
        'd' => TimeSpan.FromDays(n),
        'w' => TimeSpan.FromDays(7 * n),
        _ => TimeSpan.FromMinutes(15),
    };
}

static void PrintSnapshot(IndicatorSnapshot s) =>
    Console.WriteLine(
        $"[{s.Time.ToLocalTime():dd/MM HH:mm}] Close {s.Close:N2} | EMA50 {s.EmaFast:N2} | EMA200 {s.EmaSlow:N2} | " +
        $"MACD {s.Macd:F2}/{s.MacdSignal:F2} (hist {s.Hist:F2}) | RSI {s.Rsi:F1}");

static void PrintSignal(Signal sig)
{
    var old = Console.ForegroundColor;
    string side = sig.Side == Side.Long ? "LONG" : "SHORT";
    bool buy = (sig.Type == SignalType.Open) == (sig.Side == Side.Long); // mở Long hoặc đóng Short = lệnh mua
    Console.ForegroundColor = buy ? ConsoleColor.Green : ConsoleColor.Red;
    Console.WriteLine(sig.Type == SignalType.Open
        ? $"  >>> MỞ {side} @ {sig.Price:N2} | TP {sig.TakeProfit:N2} | SL {sig.StopLoss:N2} — {sig.Reason}"
        : $"  >>> ĐÓNG {side} @ {sig.Price:N2} ({sig.Outcome}) — {sig.Reason}");
    Console.ForegroundColor = old;
    if (!Console.IsOutputRedirected) Console.Beep();
}
