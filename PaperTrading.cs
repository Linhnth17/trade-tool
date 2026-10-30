using ClosedXML.Excel;

namespace BtcSignalBot;

/// <summary>Một lệnh giao dịch giả lập (paper trade).</summary>
public sealed class PaperTrade
{
    public int No { get; init; }
    public Side Side { get; init; }
    public DateTimeOffset EntryTime { get; init; }
    public double EntryPrice { get; init; }
    public double TakeProfit { get; init; }
    public double StopLoss { get; init; }
    public double Margin { get; init; }      // tiền bỏ vào lệnh (vd. 10$)
    public double Notional { get; init; }    // giá trị vị thế = Margin × đòn bẩy
    public double Quantity { get; init; }    // số BTC
    public double LiquidationPrice { get; init; }
    public string EntryReason { get; init; } = "";
    public required Signal EntrySignal { get; init; }   // giữ lại để giải thích lý do vào lệnh / đặt TP-SL

    public DateTimeOffset? ExitTime { get; set; }
    public double? ExitPrice { get; set; }
    public string? Outcome { get; set; }     // TP / SL / THOÁT / THANH LÝ / HẾT GIỜ
    public string? ExitReason { get; set; }
    public double Fees { get; set; }
    public double Pnl { get; set; }          // lãi/lỗ ròng sau phí ($)
    public double EquityAfter { get; set; }

    /// <summary>Ghi chú đọc lại từ file Excel (lệnh khôi phục sau khi khởi động lại không còn số liệu chỉ báo gốc).</summary>
    public string? SavedNotes { get; init; }

    public bool IsOpen => ExitTime is null;

    /// <summary>Ghi chú của lệnh: dựng lại từ chỉ báo nếu còn, không thì dùng bản đã lưu trong Excel.</summary>
    public List<NoteSection> Notes(StrategySettings s, double leverage, double feePct) =>
        EntrySignal.Context is null && SavedNotes is not null
            ? [new NoteSection("Ghi chú (đọc lại từ file Excel)", SavedNotes.Split('\n', StringSplitOptions.RemoveEmptyEntries))]
            : TradeNotes.Explain(EntrySignal, s, Margin, leverage, feePct, Outcome, ExitReason, ExitPrice, IsOpen ? null : Pnl);

    public double GrossPnl(double price) =>
        Side == Side.Long ? Quantity * (price - EntryPrice) : Quantity * (EntryPrice - price);
}

/// <summary>
/// Tài khoản giả lập: vốn ban đầu cố định, mỗi lệnh dùng một khoản tiền cố định (không gộp lãi),
/// tính phí 2 chiều. Chỉ nhận lệnh mở trong khoảng [Start, End). End = MaxValue: chạy liên tục, không bao giờ hết phiên.
/// Có đòn bẩy (isolated margin): giá đi ngược ~(1/đòn bẩy − tỷ lệ ký quỹ duy trì) là bị THANH LÝ, mất hết margin.
/// </summary>
public sealed class PaperAccount(
    double capital, double margin, double leverage, double feePct, DateTimeOffset start, DateTimeOffset end)
{
    /// <summary>Tỷ lệ ký quỹ duy trì của Binance cho BTCUSDT bậc nhỏ nhất (0.4%).</summary>
    public const double MaintenanceMarginRate = 0.004;

    public double InitialCapital => capital;
    public double MarginPerTrade => margin;
    public double Leverage => leverage;
    public double FeePct => feePct;

    /// <summary>Giá thanh lý của lệnh isolated mở tại <paramref name="entry"/>.</summary>
    public double LiquidationPrice(Side side, double entry) => LiquidationPrice(side, entry, leverage);

    public static double LiquidationPrice(Side side, double entry, double leverage)
    {
        double move = Math.Max(1 / leverage - MaintenanceMarginRate, 0);
        return side == Side.Long ? entry * (1 - move) : entry * (1 + move);
    }
    public DateTimeOffset Start => start;
    public DateTimeOffset End => end;
    public bool Continuous => end == DateTimeOffset.MaxValue;

    public List<PaperTrade> Trades { get; } = [];
    public PaperTrade? Open => Trades.Count > 0 && Trades[^1].IsOpen ? Trades[^1] : null;

    /// <summary>Vốn đã chốt (chưa tính lệnh đang mở).</summary>
    public double Balance => capital + Trades.Where(t => !t.IsOpen).Sum(t => t.Pnl);

    /// <summary>Tài sản = vốn đã chốt + lãi/lỗ tạm tính của lệnh đang mở (trừ phí đóng dự kiến).</summary>
    public double Equity(double price) => Balance + (Open is { } o
        ? Math.Max(o.GrossPnl(price), -o.Margin) - o.Fees - o.Quantity * price * feePct // isolated: lỗ tối đa = margin
        : 0);

    /// <summary>
    /// Kiểm tra thanh lý rồi TP/SL theo giá của chính lệnh (gọi với nến đang chạy hoặc vừa đóng,
    /// TRƯỚC khi xử lý tín hiệu). Thanh lý chỉ xảy ra khi giá thanh lý gần giá vào hơn SL.
    /// Tự kiểm tra TP/SL (không chỉ chờ tín hiệu của chiến lược) để lệnh khôi phục từ Excel sau khi
    /// khởi động lại vẫn được đóng đúng, kể cả khi trạng thái chiến lược tính lại không còn giữ lệnh đó.
    /// </summary>
    public void OnCandle(Candle c, DateTimeOffset time)
    {
        if (Open is not { } o) return;
        bool isLong = o.Side == Side.Long;
        bool liqBeforeSl = isLong ? o.LiquidationPrice > o.StopLoss : o.LiquidationPrice < o.StopLoss;
        bool liqHit = isLong ? c.Low <= o.LiquidationPrice : c.High >= o.LiquidationPrice;
        if (liqBeforeSl && liqHit)
        {
            o.ExitTime = time;
            o.ExitPrice = o.LiquidationPrice;
            o.Outcome = "THANH LÝ";
            o.ExitReason = $"Giá chạm mức thanh lý (đòn bẩy {leverage:0.#}x)";
            o.Pnl = -o.Margin - o.Fees; // mất toàn bộ margin + phí mở lệnh
            o.EquityAfter = Balance;
            return;
        }

        // Nến chạm cả SL và TP: không biết cái nào trước -> giả định xấu nhất là SL (giống chiến lược)
        if (isLong ? c.Low <= o.StopLoss : c.High >= o.StopLoss) CloseOpen(time, o.StopLoss, "SL", "Chạm SL");
        else if (isLong ? c.High >= o.TakeProfit : c.Low <= o.TakeProfit) CloseOpen(time, o.TakeProfit, "TP", "Chạm TP");
    }

    /// <summary>Nhận tín hiệu từ chiến lược. <paramref name="time"/> = lúc nến tín hiệu ĐÓNG.</summary>
    public void OnSignal(Signal s, DateTimeOffset time)
    {
        if (s.Type == SignalType.Open && Open is null && time >= start && time < end) // lúc hết giờ thì không mở lệnh mới
        {
            double notional = margin * leverage;
            Trades.Add(new PaperTrade
            {
                No = Trades.Count + 1,
                Side = s.Side,
                EntryTime = time,
                EntryPrice = s.Price,
                TakeProfit = s.TakeProfit,
                StopLoss = s.StopLoss,
                Margin = margin,
                Notional = notional,
                Quantity = notional / s.Price,
                LiquidationPrice = LiquidationPrice(s.Side, s.Price),
                EntryReason = s.Reason,
                EntrySignal = s,
                Fees = notional * feePct,
            });
        }
        else if (s.Type == SignalType.Close && Open is { } o && o.Side == s.Side)
        {
            CloseOpen(time, s.Price, s.Outcome, s.Reason);
        }
    }

    /// <summary>Hết phiên: đóng lệnh còn mở ở giá thị trường.</summary>
    public void Finish(DateTimeOffset time, double price)
    {
        if (Open is not null) CloseOpen(time, price, "HẾT GIỜ", "Đóng khi hết thời gian thử");
    }

    private void CloseOpen(DateTimeOffset time, double price, string outcome, string reason)
    {
        var o = Open!;
        o.ExitTime = time;
        o.ExitPrice = price;
        o.Outcome = outcome;
        o.ExitReason = reason;
        o.Fees += o.Quantity * price * feePct;
        o.Pnl = Math.Max(o.GrossPnl(price), -o.Margin) - o.Fees;
        o.EquityAfter = Balance;
    }

    // ── Thống kê ──
    public IEnumerable<PaperTrade> Closed => Trades.Where(t => !t.IsOpen);
    public int Wins => Closed.Count(t => t.Pnl > 0);
    public int Losses => Closed.Count(t => t.Pnl <= 0);
    public double TotalPnl => Closed.Sum(t => t.Pnl);
    public double ChangePct => TotalPnl / capital * 100;
    public double WinRate => Closed.Any() ? (double)Wins / Closed.Count() : 0;
}

/// <summary>Một phiên chạy thử: tài khoản giả lập + nơi lưu báo cáo Excel.</summary>
public sealed class PaperSession(
    PaperAccount account, string reportPath, string symbol, string market, string interval, StrategySettings s)
{
    public PaperAccount Account => account;
    public string ReportPath { get; private set; } = reportPath;
    public double StartPrice { get; set; } = double.NaN;
    public double LastPrice { get; set; } = double.NaN;

    /// <summary>Ghi file Excel. Nếu file đang mở trong Excel (bị khóa) thì lưu sang tên khác.</summary>
    public void Save(DateTimeOffset now)
    {
        try
        {
            ExcelReport.Save(ReportPath, account, symbol, market, interval, s, StartPrice, LastPrice, now);
        }
        catch (IOException)
        {
            ReportPath = Path.Combine(Path.GetDirectoryName(ReportPath)!,
                $"{Path.GetFileNameWithoutExtension(ReportPath)}_{DateTime.Now:HHmmss}.xlsx");
            ExcelReport.Save(ReportPath, account, symbol, market, interval, s, StartPrice, LastPrice, now);
        }
    }

    /// <summary>Một dòng trạng thái để hiện trên chart.</summary>
    public string StatusLine(DateTimeOffset now)
    {
        var left = account.End - now;
        double eq = account.Equity(LastPrice);
        string open = account.Open is { } o ? $", đang mở {(o.Side == Side.Long ? "LONG" : "SHORT")}" : "";
        string time = account.Continuous
            ? $"chạy liên tục từ {account.Start.ToLocalTime():dd/MM HH:mm} ({Uptime(now - account.Start)})"
            : left > TimeSpan.Zero ? $"còn {left:hh\\:mm\\:ss}" : "HẾT GIỜ, chờ lệnh chạm TP/SL";
        return $"PHIÊN THỬ: {time} │ " +
               $"x{account.Leverage:0.#} · Vốn {account.InitialCapital:N2}$ → {eq:N2}$ ({(eq / account.InitialCapital - 1):+0.000%;-0.000%}) │ " +
               $"{account.Closed.Count()} lệnh đã đóng{open}";
    }

    public static string Uptime(TimeSpan t) => t.TotalDays >= 1 ? $"{(int)t.TotalDays} ngày {t.Hours} giờ" : $"{t.Hours} giờ {t.Minutes} phút";

    public void PrintSummary(DateTimeOffset end)
    {
        var acc = account;
        Console.WriteLine();
        Console.WriteLine($"══ TỔNG KẾT {symbol} {interval} · {acc.Start.ToLocalTime():dd/MM HH:mm} → {end.ToLocalTime():dd/MM HH:mm} ══");
        Console.WriteLine($"  Vốn {acc.InitialCapital:N2}$, mỗi lệnh {acc.MarginPerTrade:N2}$ × {acc.Leverage:0.#}x, phí {acc.FeePct:0.00#%}/chiều");
        if (acc.Trades.Count == 0) Console.WriteLine("  Không có lệnh nào trong thời gian thử.");
        foreach (var t in acc.Trades)
        {
            var old = Console.ForegroundColor;
            Console.ForegroundColor = t.Pnl > 0 ? ConsoleColor.Green : t.Pnl < 0 ? ConsoleColor.Red : old;
            Console.WriteLine(
                $"  #{t.No} {(t.Side == Side.Long ? "LONG " : "SHORT")}  vào {t.EntryTime.ToLocalTime():dd/MM HH:mm} @ {t.EntryPrice:N2}" +
                $"  →  ra {t.ExitTime?.ToLocalTime():dd/MM HH:mm} @ {t.ExitPrice:N2}  {t.Outcome,-8} {t.Pnl,8:+0.0000;-0.0000}$");
            Console.ForegroundColor = old;
        }
        Console.WriteLine($"  Lệnh: {acc.Closed.Count()} (thắng {acc.Wins}, thua {acc.Losses}, tỷ lệ thắng {acc.WinRate:P0})");
        Console.WriteLine($"  Lãi/lỗ ròng: {acc.TotalPnl:+0.0000;-0.0000}$  →  tài sản {acc.Balance:N2}$ ({acc.TotalPnl / acc.InitialCapital:+0.000%;-0.000%})");
        Console.WriteLine($"  Giá {symbol}: {StartPrice:N2} → {LastPrice:N2} ({LastPrice / StartPrice - 1:+0.00%;-0.00%})");
        Console.WriteLine($"  Đã xuất Excel: {Path.GetFullPath(ReportPath)}");
    }
}

public static class ExcelReport
{
    private const string Money = "#,##0.00 $;[Red]-#,##0.00 $";
    private const string Price = "#,##0.00";
    private const string Pct = "+0.00%;[Red]-0.00%";
    private const string Time = "dd/mm/yyyy hh:mm";

    public static void Save(string path, PaperAccount acc, string symbol, string market, string interval,
        StrategySettings s, double startPrice, double endPrice, DateTimeOffset endTime)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var wb = new XLWorkbook();

        // ── Sheet Tổng kết ──
        var ws = wb.AddWorksheet("Tổng kết");
        var closed = acc.Closed.ToList();
        var longs = closed.Where(t => t.Side == Side.Long).ToList();
        var shorts = closed.Where(t => t.Side == Side.Short).ToList();
        double finalEquity = acc.Balance;

        ws.Cell(1, 1).Value = $"Tổng kết giao dịch thử {symbol} {interval}";
        ws.Cell(1, 1).Style.Font.SetBold().Font.SetFontSize(14);

        var rows = new (string Label, XLCellValue Value, string? Format)[]
        {
            ("Sàn", market, null),
            ("Bắt đầu", acc.Start.ToLocalTime().DateTime, Time),
            (acc.Continuous ? "Cập nhật lúc" : "Kết thúc", endTime.ToLocalTime().DateTime, Time),
            ("Thời lượng (giờ)", Math.Round((endTime - acc.Start).TotalHours, 2), "0.00"),
            ("", "", null),
            ("Vốn ban đầu", acc.InitialCapital, Money),
            ("Tiền mỗi lệnh (margin)", acc.MarginPerTrade, Money),
            ("Đòn bẩy", acc.Leverage, "0\"x\""),
            ("Giá trị vị thế mỗi lệnh", acc.MarginPerTrade * acc.Leverage, Money),
            ("Phí mỗi chiều", acc.FeePct, "0.00#%"),
            ("Chiến lược", $"{s.Name}: EMA{s.FastEma}/{s.SlowEma}, MACD {s.MacdFast}/{s.MacdSlow}/{s.MacdSignal}, " +
                           $"RSI {s.RsiPeriod}, lọc sideway ADX ≥ {s.AdxMin:0} & CHOP ≤ {s.ChopMax:0.#}", null),
            ("TP / SL", $"SL sau đáy/đỉnh {s.SwingLookback} nến (+{s.SlAtrBuffer} ATR, {s.SlMinAtr}–{s.SlMaxAtr} ATR); " +
                        $"TP {s.TargetRMin}–{s.TargetRMax}R theo ADX, chặn trước kháng cự; bỏ lệnh nếu R:R < {s.MinRR}", null),
            ("R:R trung bình", closed.Count > 0
                ? closed.Average(t => Math.Abs(t.TakeProfit - t.EntryPrice) / Math.Abs(t.EntryPrice - t.StopLoss)) : 0, "0.00"),
            ("", "", null),
            ("Số lệnh", closed.Count, "0"),
            ("  Long", longs.Count, "0"),
            ("  Short", shorts.Count, "0"),
            ("Thắng / Thua", $"{acc.Wins} / {acc.Losses}", null),
            ("Bị thanh lý", closed.Count(t => t.Outcome == "THANH LÝ"), "0"),
            ("Tỷ lệ thắng", acc.WinRate, "0.0%"),
            ("Tổng phí", closed.Sum(t => t.Fees), Money),
            ("Lãi/lỗ Long", longs.Sum(t => t.Pnl), Money),
            ("Lãi/lỗ Short", shorts.Sum(t => t.Pnl), Money),
            ("Lãi/lỗ ròng", acc.TotalPnl, Money),
            ("", "", null),
            ("Tài sản cuối", finalEquity, Money),
            ("Tài sản tăng/giảm", acc.TotalPnl / acc.InitialCapital, Pct),
            ("Lãi/lỗ so với tổng tiền đã vào lệnh",
                closed.Count > 0 ? acc.TotalPnl / (closed.Count * acc.MarginPerTrade) : 0, Pct),
            ("", "", null),
            ($"Giá {symbol} lúc bắt đầu", startPrice, Price),
            ($"Giá {symbol} lúc kết thúc", endPrice, Price),
            ("Giá BTC thay đổi", endPrice / startPrice - 1, Pct),
        };
        for (int i = 0; i < rows.Length; i++)
        {
            var (label, value, format) = rows[i];
            ws.Cell(i + 3, 1).Value = label;
            ws.Cell(i + 3, 2).Value = value;
            if (format is not null) ws.Cell(i + 3, 2).Style.NumberFormat.Format = format;
            if (label is "Lãi/lỗ ròng" or "Tài sản cuối" or "Tài sản tăng/giảm")
                ws.Range(i + 3, 1, i + 3, 2).Style.Font.SetBold();
        }
        ws.Cell(rows.Length + 4, 1).Value = "Giao dịch giả lập, không phải lời khuyên đầu tư. " +
                                            "Mỗi lệnh chạy đủ chu kỳ: chỉ đóng khi chạm TP hoặc SL (khớp đúng giá TP/SL).";
        ws.Cell(rows.Length + 4, 1).Style.Font.SetItalic().Font.SetFontColor(XLColor.Gray);
        ws.Column(1).Width = 36;
        ws.Column(2).Width = 52;
        ws.Column(2).Style.Alignment.SetHorizontal(XLAlignmentHorizontalValues.Left);

        // ── Sheet Lệnh ──
        var wt = wb.AddWorksheet("Lệnh");
        string[] headers =
        [
            "#", "Chiều", "Thời gian vào", "Giá vào", "TP", "SL", "Thời gian ra", "Giá ra", "Kết quả",
            "Thời gian giữ (phút)", "Margin ($)", "Giá trị vị thế ($)", "Số lượng BTC", "Phí ($)",
            "Lãi/lỗ ($)", "Lãi/lỗ % trên margin", "Tài sản sau lệnh ($)", "Lý do vào", "Lý do ra", "Giá thanh lý", "R:R",
            "Ghi chú: vì sao vào lệnh, đặt SL/TP, đóng lệnh",
        ];
        for (int c = 0; c < headers.Length; c++) wt.Cell(1, c + 1).Value = headers[c];
        var head = wt.Range(1, 1, 1, headers.Length);
        head.Style.Font.SetBold().Font.SetFontColor(XLColor.White).Fill.SetBackgroundColor(XLColor.FromHtml("#1F4E78"));

        int r = 2;
        foreach (var t in acc.Trades)
        {
            wt.Cell(r, 1).Value = t.No;
            wt.Cell(r, 2).Value = t.Side == Side.Long ? "LONG" : "SHORT";
            wt.Cell(r, 3).Value = t.EntryTime.ToLocalTime().DateTime;
            wt.Cell(r, 4).Value = t.EntryPrice;
            wt.Cell(r, 5).Value = t.TakeProfit;
            wt.Cell(r, 6).Value = t.StopLoss;
            if (t.ExitTime is { } xt)
            {
                wt.Cell(r, 7).Value = xt.ToLocalTime().DateTime;
                wt.Cell(r, 8).Value = t.ExitPrice!.Value;
                wt.Cell(r, 9).Value = t.Outcome;
                wt.Cell(r, 10).Value = Math.Round((xt - t.EntryTime).TotalMinutes);
                wt.Cell(r, 15).Value = t.Pnl;
                wt.Cell(r, 16).Value = t.Pnl / t.Margin;
                wt.Cell(r, 17).Value = t.EquityAfter;
            }
            else wt.Cell(r, 9).Value = "ĐANG MỞ";
            wt.Cell(r, 11).Value = t.Margin;
            wt.Cell(r, 12).Value = t.Notional;
            wt.Cell(r, 13).Value = t.Quantity;
            wt.Cell(r, 14).Value = t.Fees;
            wt.Cell(r, 18).Value = t.EntryReason;
            wt.Cell(r, 19).Value = t.ExitReason;
            wt.Cell(r, 20).Value = t.LiquidationPrice;
            wt.Cell(r, 21).Value = Math.Abs(t.TakeProfit - t.EntryPrice) / Math.Abs(t.EntryPrice - t.StopLoss);
            wt.Cell(r, 21).Style.NumberFormat.Format = "\"1:\"0.00";
            wt.Cell(r, 22).Value = t.EntrySignal.Context is null && t.SavedNotes is not null
                ? t.SavedNotes
                : TradeNotes.ToText(t.Notes(s, acc.Leverage, acc.FeePct));
            wt.Cell(r, 22).Style.Alignment.SetWrapText(true).Alignment.SetVertical(XLAlignmentVerticalValues.Top);

            wt.Cell(r, 2).Style.Font.SetFontColor(t.Side == Side.Long ? XLColor.FromHtml("#107C41") : XLColor.FromHtml("#C00000"));
            if (!t.IsOpen)
                wt.Range(r, 15, r, 16).Style.Fill.SetBackgroundColor(
                    t.Pnl > 0 ? XLColor.FromHtml("#E2EFDA") : XLColor.FromHtml("#FCE4D6"));
            r++;
        }
        if (acc.Trades.Count == 0) wt.Cell(2, 1).Value = "Không có lệnh nào trong thời gian thử.";

        wt.Columns(3, 3).Style.NumberFormat.Format = Time;
        wt.Columns(7, 7).Style.NumberFormat.Format = Time;
        foreach (int c in new[] { 4, 5, 6, 8, 20 }) wt.Column(c).Style.NumberFormat.Format = Price;
        foreach (int c in new[] { 11, 12, 14, 15, 17 }) wt.Column(c).Style.NumberFormat.Format = Money;
        wt.Column(13).Style.NumberFormat.Format = "0.000000";
        wt.Column(16).Style.NumberFormat.Format = Pct;
        head.Style.NumberFormat.Format = "@";
        wt.SheetView.FreezeRows(1);
        if (acc.Trades.Count > 0) wt.Range(1, 1, r - 1, headers.Length).SetAutoFilter();
        wt.Columns(1, headers.Length - 1).AdjustToContents();
        wt.Column(headers.Length).Width = 110; // cột ghi chú dài: cố định độ rộng, xuống dòng
        if (acc.Trades.Count > 0) wt.Rows(2, r - 1).AdjustToContents();

        wb.SaveAs(path);
    }

    /// <summary>
    /// Đọc lại file do <see cref="Save"/> ghi: thời điểm bắt đầu, giá lúc bắt đầu và toàn bộ lệnh
    /// (để chạy liên tục: khởi động lại vẫn nối tiếp vào cùng một file).
    /// </summary>
    public static (DateTimeOffset Start, double StartPrice, List<PaperTrade> Trades) Load(string path)
    {
        using var wb = new XLWorkbook(path);

        var summary = wb.Worksheet("Tổng kết");
        DateTimeOffset? start = null;
        double startPrice = double.NaN;
        foreach (var row in summary.RowsUsed())
        {
            string label = row.Cell(1).GetString();
            if (label == "Bắt đầu") start = new DateTimeOffset(row.Cell(2).GetDateTime(), TimeZoneInfo.Local.GetUtcOffset(row.Cell(2).GetDateTime()));
            else if (label.StartsWith("Giá ") && label.EndsWith("lúc bắt đầu")) startPrice = row.Cell(2).GetDouble();
        }
        if (start is null) throw new InvalidDataException("Không tìm thấy dòng \"Bắt đầu\" trong sheet Tổng kết");

        var ws = wb.Worksheet("Lệnh");
        var col = ws.Row(1).CellsUsed().ToDictionary(c => c.GetString(), c => c.Address.ColumnNumber);
        int C(string name) => col.TryGetValue(name, out int n) ? n : throw new InvalidDataException($"Thiếu cột \"{name}\"");
        DateTimeOffset Local(DateTime d) => new(d, TimeZoneInfo.Local.GetUtcOffset(d));

        var trades = new List<PaperTrade>();
        foreach (var row in ws.RowsUsed().Skip(1))
        {
            if (!row.Cell(C("#")).TryGetValue(out double no)) continue; // dòng "Không có lệnh nào…"
            var side = row.Cell(C("Chiều")).GetString() == "LONG" ? Side.Long : Side.Short;
            double entry = row.Cell(C("Giá vào")).GetDouble(), tp = row.Cell(C("TP")).GetDouble(), sl = row.Cell(C("SL")).GetDouble();
            string reason = row.Cell(C("Lý do vào")).GetString();
            string outcome = row.Cell(C("Kết quả")).GetString();
            bool closed = outcome != "ĐANG MỞ";

            trades.Add(new PaperTrade
            {
                No = (int)no,
                Side = side,
                EntryTime = Local(row.Cell(C("Thời gian vào")).GetDateTime()),
                EntryPrice = entry,
                TakeProfit = tp,
                StopLoss = sl,
                Margin = row.Cell(C("Margin ($)")).GetDouble(),
                Notional = row.Cell(C("Giá trị vị thế ($)")).GetDouble(),
                Quantity = row.Cell(C("Số lượng BTC")).GetDouble(),
                LiquidationPrice = row.Cell(C("Giá thanh lý")).GetDouble(),
                EntryReason = reason,
                EntrySignal = new Signal(SignalType.Open, side, reason, entry, tp, sl),
                // lệnh còn mở sẽ có thêm phần "vì sao đóng" sau này → chỉ giữ ghi chú cũ cho lệnh đã đóng
                SavedNotes = closed ? row.Cell(C("Ghi chú: vì sao vào lệnh, đặt SL/TP, đóng lệnh")).GetString() : null,
                Fees = row.Cell(C("Phí ($)")).GetDouble(),
                ExitTime = closed ? Local(row.Cell(C("Thời gian ra")).GetDateTime()) : null,
                ExitPrice = closed ? row.Cell(C("Giá ra")).GetDouble() : null,
                Outcome = closed ? outcome : null,
                ExitReason = closed ? row.Cell(C("Lý do ra")).GetString() : null,
                Pnl = closed ? row.Cell(C("Lãi/lỗ ($)")).GetDouble() : 0,
                EquityAfter = closed ? row.Cell(C("Tài sản sau lệnh ($)")).GetDouble() : 0,
            });
        }
        return (start.Value, startPrice, trades);
    }
}
