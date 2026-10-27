using System.Runtime.InteropServices;
using System.Text;

namespace BtcSignalBot;

/// <summary>Tín hiệu đã chốt trên một nến đã đóng.</summary>
public sealed record TradeMarker(DateTimeOffset Time, Signal Signal);

/// <summary>Một lệnh: điểm mở và (nếu đã đóng) điểm đóng.</summary>
public sealed record Trade(TradeMarker Entry, TradeMarker? Exit)
{
    public Side Side => Entry.Signal.Side;
    public double EntryPrice => Entry.Signal.Price;
    public double TakeProfit => Entry.Signal.TakeProfit;
    public double StopLoss => Entry.Signal.StopLoss;

    /// <summary>"TP", "SL", "THOÁT" hoặc null nếu lệnh còn mở.</summary>
    public string? Outcome => Exit?.Signal.Outcome;

    /// <summary>R:R thực tế của lệnh (không cố định — tùy cấu trúc giá lúc vào).</summary>
    public double RiskReward => Math.Abs(TakeProfit - EntryPrice) / Math.Abs(EntryPrice - StopLoss);

    /// <summary>% lãi/lỗ (chưa phí) nếu đóng ở <paramref name="price"/>.</summary>
    public double ReturnAt(double price) =>
        Side == Side.Long ? price / EntryPrice - 1 : 1 - price / EntryPrice;

    public static List<Trade> FromMarkers(IEnumerable<TradeMarker> markers)
    {
        var trades = new List<Trade>();
        foreach (var m in markers)
        {
            if (m.Signal.Type == SignalType.Open) trades.Add(new Trade(m, null));
            else if (trades.Count > 0 && trades[^1].Exit is null) trades[^1] = trades[^1] with { Exit = m };
        }
        return trades;
    }
}

/// <summary>Dữ liệu cho một khung hình của chart.</summary>
public sealed record ChartFrame(
    string Symbol,
    string Market,
    string Interval,
    IReadOnlyList<Candle> Candles,          // nến đã đóng + (nếu LastIsLive) nến đang chạy ở cuối
    IndicatorSeries Series,                 // tính trên đúng Candles
    bool LastIsLive,
    IReadOnlyList<TradeMarker> Markers,
    IReadOnlyList<Signal> Previews,         // tín hiệu tạm của nến đang chạy
    IReadOnlyList<(string Name, bool Ok)> LongConditions,
    IReadOnlyList<(string Name, bool Ok)> ShortConditions,
    StrategySettings Settings,
    string Note,
    IReadOnlyList<bool> Sideway,            // từng nến (song song với Candles): đang đi ngang hay không
    string RegimeLabel);

/// <summary>
/// Vẽ chart nến + EMA + điểm MUA/BÁN + vùng TP/SL ngay trong console bằng mã ANSI.
/// Mỗi khung hình vẽ lại toàn màn hình (alternate screen) nên không bị trôi log.
/// </summary>
public static class ConsoleChart
{
    private const int Slot = 2;        // mỗi nến chiếm 2 cột: thân nến + khoảng trống
    private const int AxisWidth = 16;  // trục giá bên phải
    private const int SignalRows = 5;

    private const string Up = "\x1b[32m", Down = "\x1b[31m", UpLive = "\x1b[92m", DownLive = "\x1b[91m",
        FastColor = "\x1b[33m", SlowColor = "\x1b[35m", Dim = "\x1b[90m", Cyan = "\x1b[96m",
        Yellow = "\x1b[93m", Bold = "\x1b[1m", BuyColor = "\x1b[1;92m", SellColor = "\x1b[1;91m",
        TpZone = "\x1b[48;5;22m", SlZone = "\x1b[48;5;52m", SidewayBg = "\x1b[48;5;238m",
        LongBanner = "\x1b[1;97;42m", ShortBanner = "\x1b[1;97;41m", WaitBanner = "\x1b[97;100m", AlertBanner = "\x1b[1;30;103m";

    private static (int W, int H) _lastSize;

    public static void EnterScreen()
    {
        EnableVirtualTerminal();
        Console.Write("\x1b[?1049h\x1b[?25l\x1b[2J"); // màn hình phụ, ẩn con trỏ
    }

    public static void LeaveScreen() => Console.Write("\x1b[0m\x1b[?25h\x1b[?1049l");

    public static void Render(ChartFrame f)
    {
        var (w, h) = WindowSize();
        var cv = new Canvas(w, h);

        const int chartTop = 5;
        int chartH = Math.Max(6, h - chartTop - 3 - SignalRows); // trừ trục thời gian, vạch ngăn, footer
        int plotW = Math.Max(10, w - AxisWidth);
        int n = Math.Min(f.Candles.Count, plotW / Slot);
        int start = f.Candles.Count - n;
        var last = f.Candles[^1];
        var snap = f.Series.At(f.Series.Count - 1);
        var trades = Trade.FromMarkers(f.Markers);
        var open = trades.Count > 0 && trades[^1].Exit is null ? trades[^1] : null;
        bool sidewayNow = f.Sideway.Count > 0 && f.Sideway[^1];

        // Vị trí cột của nến theo thời gian (null nếu nằm ngoài khung nhìn)
        var colOf = new Dictionary<DateTimeOffset, int>();
        for (int j = 0; j < n; j++) colOf[f.Candles[start + j].OpenTime] = j * Slot;
        var viewStart = f.Candles[start].OpenTime;
        var visibleTrades = trades.Where(t => t.Exit is null || t.Exit.Time >= viewStart).ToList();

        // ── Thang giá: nến trong khung + TP/SL của các lệnh đang thấy ──
        double lo = double.MaxValue, hi = double.MinValue;
        for (int i = start; i < f.Candles.Count; i++)
        {
            lo = Math.Min(lo, f.Candles[i].Low);
            hi = Math.Max(hi, f.Candles[i].High);
        }
        foreach (var t in visibleTrades)
        {
            lo = Math.Min(lo, t.StopLoss);
            hi = Math.Max(hi, t.TakeProfit);
        }
        double pad = Math.Max((hi - lo) * 0.10, hi * 0.0005); // chừa chỗ cho nhãn MUA/BÁN
        lo -= pad;
        hi += pad;
        double step = (hi - lo) / chartH;
        int Row(double p) => chartTop + Math.Clamp((int)((hi - p) / step), 0, chartH - 1);
        bool InView(double p) => !double.IsNaN(p) && p >= lo && p <= hi;

        // ── Vùng TP (xanh) / SL (đỏ) của từng lệnh, như công cụ "Long/Short position" của TradingView ──
        // Long: TP phía trên, SL phía dưới; Short thì ngược lại.
        foreach (var t in visibleTrades)
        {
            int x0 = colOf.TryGetValue(t.Entry.Time, out var a) ? a : 0;
            int x1 = t.Exit is null ? plotW - 1 : colOf.TryGetValue(t.Exit.Time, out var b) ? b : plotW - 1;
            int rTp = Row(t.TakeProfit), rEntry = Row(t.EntryPrice), rSl = Row(t.StopLoss);
            for (int x = x0; x <= x1; x++)
            {
                for (int r = Math.Min(rTp, rEntry); r <= Math.Max(rTp, rEntry); r++) cv.Bg(r, x, TpZone);
                for (int r = Math.Min(rSl, rEntry); r <= Math.Max(rSl, rEntry); r++) cv.Bg(r, x, SlZone);
                cv.Put(rTp, x, '─', UpLive);
                cv.Put(rEntry, x, '─', Cyan);
                cv.Put(rSl, x, '─', DownLive);
            }
        }

        // ── EMA (vẽ trước để nến đè lên) ──
        for (int j = 0; j < n; j++)
        {
            var s = f.Series.At(start + j);
            var next = j + 1 < n ? f.Series.At(start + j + 1) : s;
            Dot(s.EmaSlow, (s.EmaSlow + next.EmaSlow) / 2, j * Slot, SlowColor);
            Dot(s.EmaFast, (s.EmaFast + next.EmaFast) / 2, j * Slot, FastColor);
        }
        void Dot(double v, double mid, int x, string color)
        {
            if (InView(v)) cv.Put(Row(v), x, '•', color);
            if (InView(mid)) cv.Put(Row(mid), x + 1, '•', color);
        }

        // ── Nến ──
        for (int j = 0; j < n; j++)
        {
            var c = f.Candles[start + j];
            bool live = f.LastIsLive && start + j == f.Candles.Count - 1;
            string color = c.Close >= c.Open ? (live ? UpLive : Up) : (live ? DownLive : Down);
            int x = j * Slot;
            int bodyTop = Row(Math.Max(c.Open, c.Close)), bodyBot = Row(Math.Min(c.Open, c.Close));
            for (int r = Row(c.High); r <= Row(c.Low); r++)
                cv.Put(r, x, r >= bodyTop && r <= bodyBot ? (live ? '▒' : '█') : '│', color);
        }

        // ── Điểm vào / ra lệnh kèm nhãn: ▲ dưới nến = lệnh mua (mở Long / đóng Short), ▼ trên nến = lệnh bán ──
        int chartBottom = chartTop + chartH - 1;
        void BuyMark(Candle c, string label, string color)
        {
            int x = colOf[c.OpenTime], r = Math.Min(Row(c.Low) + 1, chartBottom);
            cv.Put(r, x, '▲', color);
            cv.Text(Math.Min(r + 1, chartBottom), x - label.Length / 2, label, color);
        }
        void SellMark(Candle c, string label, string color)
        {
            int x = colOf[c.OpenTime], r = Math.Max(Row(c.High) - 1, chartTop);
            cv.Put(r, x, '▼', color);
            cv.Text(Math.Max(r - 1, chartTop), x - label.Length / 2, label, color);
        }
        void Mark(Candle c, bool buy, string label, string color)
        {
            if (buy) BuyMark(c, label, color);
            else SellMark(c, label, color);
        }
        static string OutcomeColor(string? o) => o == "TP" ? BuyColor : o == "SL" ? SellColor : Yellow;

        var candleAt = new Dictionary<DateTimeOffset, Candle>();
        for (int j = 0; j < n; j++) candleAt[f.Candles[start + j].OpenTime] = f.Candles[start + j];
        foreach (var t in visibleTrades)
        {
            bool isLong = t.Side == Side.Long;
            if (candleAt.TryGetValue(t.Entry.Time, out var ce))
                Mark(ce, isLong, isLong ? "LONG" : "SHORT", isLong ? BuyColor : SellColor);
            if (t.Exit is { } ex && candleAt.TryGetValue(ex.Time, out var cx))
                Mark(cx, !isLong, t.Outcome!, OutcomeColor(t.Outcome));
        }
        if (f.LastIsLive)
        {
            foreach (var p in f.Previews)
            {
                if (p.Type == SignalType.Open)
                    Mark(last, p.Side == Side.Long, p.Side == Side.Long ? "LONG?" : "SHORT?", Yellow);
                else
                    Mark(last, p.Side == Side.Short, p.Outcome + "?", Yellow);
            }
        }

        // ── Trục giá ──
        for (int r = chartTop; r < chartTop + chartH; r++)
        {
            cv.Put(r, plotW, '│', Dim);
            if ((r - chartTop) % 4 == 0) cv.Text(r, plotW + 2, $"{hi - (r - chartTop + 0.5) * step:N2}", Dim);
        }
        if (open is not null)
        {
            cv.Text(Row(open.TakeProfit), plotW + 1, $" TP {open.TakeProfit:N2}", BuyColor);
            cv.Text(Row(open.EntryPrice), plotW + 1, $" Vào {open.EntryPrice:N2}", Cyan);
            cv.Text(Row(open.StopLoss), plotW + 1, $" SL {open.StopLoss:N2}", SellColor);
        }
        cv.Text(Row(last.Close), plotW + 1, $" {last.Close:N2} ",
            last.Close >= last.Open ? "\x1b[30;42m" : "\x1b[97;41m");

        // ── Trục thời gian (nền xám = giai đoạn sideway, bot không vào lệnh) ──
        int timeRow = chartTop + chartH;
        for (int j = 0; j < n; j++)
        {
            if (start + j < f.Sideway.Count && f.Sideway[start + j])
                for (int x = j * Slot; x < (j + 1) * Slot; x++) cv.Bg(timeRow, x, SidewayBg);
        }
        for (int j = 0; j < n; j += 8)
        {
            if (j * Slot + 5 > plotW) break;
            var t = f.Candles[start + j].OpenTime.ToLocalTime();
            cv.Text(timeRow, j * Slot, t is { Hour: 0, Minute: 0 } ? $"{t:dd/MM}" : $"{t:HH:mm}", Dim);
        }

        // ── Header ──
        double chg = last.Close / last.Open - 1;
        int col = cv.Text(0, 1, $"{f.Symbol} · {f.Interval} · {f.Market}", Bold);
        col = cv.Text(0, col + 3, $"{last.Close:N2}", last.Close >= last.Open ? UpLive : DownLive);
        cv.Text(0, col + 1, $"({chg:+0.00%;-0.00%} nến này)", Dim);
        cv.Text(0, w - 10, $"{DateTime.Now:HH:mm:ss}", Dim);

        col = cv.Text(1, 1, $"EMA{f.Settings.FastEma} {snap.EmaFast:N2}", FastColor);
        col = cv.Text(1, col + 3, $"EMA{f.Settings.SlowEma} {snap.EmaSlow:N2}", SlowColor);
        col = cv.Text(1, col + 3, $"MACD hist {snap.Hist:F2}", snap.Hist >= 0 ? Up : Down);
        col = cv.Text(1, col + 3, $"RSI {snap.Rsi:F1}",
            snap.Rsi >= f.Settings.RsiOverbought ? DownLive : snap.Rsi <= 30 ? UpLive : null);
        col = cv.Text(1, col + 3, $"ATR {snap.Atr:N2}", Dim);
        cv.Text(1, col + 3, f.RegimeLabel, sidewayNow ? Yellow : Cyan);

        // ── Bảng lệnh: hàng 2 = banner, hàng 3-4 = chi tiết ──
        // Lệnh đang mở chỉ kết thúc khi chạm TP hoặc SL, không phụ thuộc thời điểm đóng nến.
        var previewOpen = f.Previews.FirstOrDefault(p => p.Type == SignalType.Open);
        if (open is not null)
        {
            double pnl = open.ReturnAt(last.Close);
            string side = open.Side == Side.Long ? "LONG" : "SHORT";
            Banner(2, $" ● ĐANG {side}   Vào {open.EntryPrice:N2} lúc {open.Entry.Time.ToLocalTime():HH:mm dd/MM}" +
                      $"   │   TP {open.TakeProfit:N2} ({open.TakeProfit / open.EntryPrice - 1:+0.00%;-0.00%})" +
                      $"   │   SL {open.StopLoss:N2} ({open.StopLoss / open.EntryPrice - 1:+0.00%;-0.00%})" +
                      $"   │   R:R 1:{open.RiskReward:0.0#}   │   Lãi/lỗ {pnl:+0.00%;-0.00%} ",
                pnl >= 0 ? LongBanner : ShortBanner);
            ProgressBar(3, open, last.Close);
            cv.Text(4, 1, "Lệnh chạy cho tới khi chạm TP hoặc SL.", Dim);
        }
        else if (previewOpen is not null)
        {
            var p = previewOpen;
            Banner(2, $" ⚡ SẮP CÓ TÍN HIỆU {(p.Side == Side.Long ? "LONG" : "SHORT")} — đang chờ xác nhận" +
                      $"   │   Vào ~{p.Price:N2}   TP {p.TakeProfit:N2} ({p.TakeProfit / p.Price - 1:+0.00%;-0.00%})" +
                      $"   SL {p.StopLoss:N2} ({p.StopLoss / p.Price - 1:+0.00%;-0.00%})" +
                      $"   R:R 1:{(p.Plan?.RiskReward ?? double.NaN):0.0#} ",
                AlertBanner);
            Checklist(3, "LONG ", f.LongConditions);
            Checklist(4, "SHORT", f.ShortConditions);
        }
        else
        {
            Banner(2, sidewayNow
                    ? $" ⏸ {f.RegimeLabel}"
                    : $" ⏳ CHƯA CÓ LỆNH: chờ đủ {f.LongConditions.Count} điều kiện LONG hoặc SHORT",
                WaitBanner);
            Checklist(3, "LONG ", f.LongConditions);
            Checklist(4, "SHORT", f.ShortConditions);
        }

        void Banner(int r, string text, string style)
        {
            for (int x = 0; x < w; x++) cv.Put(r, x, ' ', style);
            cv.Text(r, 0, text, style);
        }
        // Gọn cho console: số điều kiện đã đạt + liệt kê những điều kiện còn thiếu
        void Checklist(int r, string label, IReadOnlyList<(string Name, bool Ok)> conditions)
        {
            int ok = conditions.Count(c => c.Ok);
            int c = cv.Text(r, 1, $"{label} {ok}/{conditions.Count}", ok == conditions.Count ? UpLive : Dim);
            var missing = conditions.Where(x => !x.Ok).Select(x => x.Name).ToList();
            if (missing.Count > 0) cv.Text(r, c + 2, "thiếu: " + string.Join(" · ", missing), Dim);
        }
        // SL ━━━━━●━━━━━━━━━━ TP : giá hiện tại đang ở đâu giữa SL và TP
        void ProgressBar(int r, Trade t, double price)
        {
            const int barW = 40;
            int c = cv.Text(r, 1, "SL ", SellColor);
            int entryPos = (int)Math.Round((t.EntryPrice - t.StopLoss) / (t.TakeProfit - t.StopLoss) * (barW - 1));
            int pos = (int)Math.Round(Math.Clamp((price - t.StopLoss) / (t.TakeProfit - t.StopLoss), 0, 1) * (barW - 1));
            for (int i = 0; i < barW; i++)
                cv.Put(r, c + i, i == pos ? '●' : i == entryPos ? '┼' : '━',
                    i == pos ? Bold : i == entryPos ? Cyan : i < entryPos ? Down : Up);
            cv.Text(r, c + barW + 1, "TP", BuyColor);
        }

        // ── Lịch sử lệnh ──
        int sep = timeRow + 1;
        for (int x = 0; x < w; x++) cv.Put(sep, x, '─', Dim);
        cv.Text(sep, 2, $" Lệnh gần nhất · {f.Note} ", Dim);

        var rows = new List<(string Text, string Color)>();
        foreach (var t in Enumerable.Reverse(trades).Take(SignalRows))
        {
            bool isLong = t.Side == Side.Long;
            string head = $"{t.Entry.Time.ToLocalTime():dd/MM HH:mm} {(isLong ? "▲ LONG " : "▼ SHORT")} {t.EntryPrice,10:N2}" +
                          $" TP {t.TakeProfit,10:N2} SL {t.StopLoss,10:N2} → ";
            if (t.Exit is not { } ex)
            {
                rows.Add((head + $"ĐANG MỞ {t.ReturnAt(last.Close):+0.00%;-0.00%}", Cyan));
                continue;
            }
            double ret = t.ReturnAt(ex.Signal.Price);
            string why = t.Outcome == "THOÁT" ? $"  {ex.Signal.Reason}" : "";
            rows.Add((head + $"{ex.Time.ToLocalTime():HH:mm} {t.Outcome,-5} {ex.Signal.Price,10:N2} {ret,7:+0.00%;-0.00%}{why}",
                t.Outcome == "TP" || ret > 0 ? BuyColor : t.Outcome == "SL" || ret < 0 ? SellColor : Yellow));
        }
        if (rows.Count == 0) rows.Add(("Chưa có lệnh nào trong dữ liệu đã tải.", Dim));
        for (int i = 0; i < rows.Count; i++)
            cv.Text(sep + 1 + i, 2, rows[i].Text, rows[i].Color);

        cv.Text(h - 1, 1,
            $"▲ LONG/đóng Short  ▼ SHORT/đóng Long  ▒ nến đang chạy  • EMA{f.Settings.FastEma}  • EMA{f.Settings.SlowEma}  " +
            "nền xanh/đỏ = vùng TP/SL  trục giờ xám = sideway   Ctrl+C thoát · Không phải lời khuyên đầu tư", Dim);

        bool resized = (w, h) != _lastSize;
        _lastSize = (w, h);
        Console.Write((resized ? "\x1b[2J" : "") + cv.ToAnsi());
    }

    private static (int W, int H) WindowSize()
    {
        try { return (Math.Max(60, Console.WindowWidth), Math.Max(24, Console.WindowHeight)); }
        catch (IOException) { return (120, 40); } // output bị redirect
    }

    // conhost cũ trên Windows cần bật VT processing thì mới hiểu mã ANSI.
    private static void EnableVirtualTerminal()
    {
        if (!OperatingSystem.IsWindows()) return;
        var handle = GetStdHandle(-11); // STD_OUTPUT_HANDLE
        if (GetConsoleMode(handle, out uint mode)) SetConsoleMode(handle, mode | 0x0004);
    }

    [DllImport("kernel32.dll")] private static extern IntPtr GetStdHandle(int nStdHandle);
    [DllImport("kernel32.dll")] private static extern bool GetConsoleMode(IntPtr h, out uint mode);
    [DllImport("kernel32.dll")] private static extern bool SetConsoleMode(IntPtr h, uint mode);

    /// <summary>Bộ đệm ký tự + màu chữ + màu nền, xuất ra một chuỗi ANSI duy nhất để tránh nháy.</summary>
    private sealed class Canvas(int w, int h)
    {
        private readonly char[,] _ch = InitChars(w, h);
        private readonly string?[,] _color = new string?[h, w];
        private readonly string?[,] _bg = new string?[h, w];

        public void Put(int r, int c, char ch, string? color)
        {
            if (r < 0 || r >= h || c < 0 || c >= w) return;
            _ch[r, c] = ch;
            _color[r, c] = color;
        }

        /// <summary>Tô màu nền, giữ nguyên ký tự và màu chữ.</summary>
        public void Bg(int r, int c, string bg)
        {
            if (r < 0 || r >= h || c < 0 || c >= w) return;
            _bg[r, c] = bg;
        }

        /// <summary>Ghi chuỗi, trả về cột ngay sau chuỗi.</summary>
        public int Text(int r, int c, string s, string? color)
        {
            foreach (char ch in s) Put(r, c++, ch, color);
            return c;
        }

        public string ToAnsi()
        {
            var sb = new StringBuilder(w * h * 2);
            for (int r = 0; r < h; r++)
            {
                sb.Append($"\x1b[{r + 1};1H");
                string? cur = null;
                for (int c = 0; c < w - 1; c++) // chừa cột cuối để terminal không tự xuống dòng
                {
                    // màu chữ ghi sau nên nếu nó tự có nền (vd. nhãn giá) thì ghi đè nền vùng TP/SL
                    string style = _bg[r, c] + _color[r, c];
                    if (style != cur)
                    {
                        sb.Append("\x1b[0m").Append(style);
                        cur = style;
                    }
                    sb.Append(_ch[r, c]);
                }
                sb.Append("\x1b[0m");
            }
            return sb.ToString();
        }

        private static char[,] InitChars(int w, int h)
        {
            var a = new char[h, w];
            for (int r = 0; r < h; r++)
                for (int c = 0; c < w; c++) a[r, c] = ' ';
            return a;
        }
    }
}
