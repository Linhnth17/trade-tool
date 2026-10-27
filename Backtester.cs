namespace BtcSignalBot;

public sealed record BacktestResult(
    DateTimeOffset From,
    DateTimeOffset To,
    int Trades,
    int Wins,
    double StrategyReturnPct,
    double BuyAndHoldPct,
    double MaxDrawdownPct);

public static class Backtester
{
    /// <summary>
    /// Chạy chiến lược trên dữ liệu lịch sử. Lệnh vào khớp ở giá MỞ CỬA của nến kế tiếp
    /// (thực tế hơn khớp ngay giá đóng cửa của nến ra tín hiệu), TP/SL khớp đúng giá TP/SL, có tính phí 2 chiều.
    /// </summary>
    public static BacktestResult Run(IReadOnlyList<Candle> candles, StrategySettings s)
    {
        var series = new IndicatorSeries(candles, s);
        var strategy = new MacdTrendStrategy(s);

        int first = -1;
        double equity = 1, peak = 1, maxDd = 0, entry = 0;
        Side? holding = null;
        int trades = 0, wins = 0;

        // Tỷ suất của lệnh (chưa phí): Long lãi khi giá tăng, Short lãi khi giá giảm
        double Gross(double exit) => holding == Side.Long ? exit / entry : 2 - exit / entry;

        void Close(double fill)
        {
            double ret = Gross(fill) * (1 - s.FeePct) * (1 - s.FeePct);
            equity *= ret;
            trades++;
            if (ret > 1) wins++;
            holding = null;
        }

        for (int i = 0; i < candles.Count - 1; i++)
        {
            var snap = series.At(i);
            if (snap.IsReady && first < 0) first = i;

            double nextOpen = candles[i + 1].Open;
            foreach (var sig in strategy.Evaluate(snap))
            {
                if (sig.Type == SignalType.Open)
                {
                    entry = nextOpen;
                    holding = sig.Side;
                }
                else if (sig.Type == SignalType.Close && holding is not null)
                {
                    // TP/SL là lệnh chờ -> khớp tại đúng giá đó; thoát sớm thì khớp giá mở nến kế tiếp
                    Close(sig.HitTarget ? sig.Price : nextOpen);
                }
            }

            // drawdown tính theo giá trị danh mục kể cả lệnh đang mở
            double mtm = holding is not null ? equity * Gross(candles[i].Close) : equity;
            peak = Math.Max(peak, mtm);
            maxDd = Math.Max(maxDd, (peak - mtm) / peak);
        }

        // Đóng vị thế còn mở ở giá cuối để thống kê
        if (holding is not null) Close(candles[^1].Close);

        if (first < 0) first = 0;
        double buyHold = candles[^1].Close / candles[first].Close - 1;

        return new BacktestResult(
            candles[first].OpenTime, candles[^1].OpenTime,
            trades, wins, (equity - 1) * 100, buyHold * 100, maxDd * 100);
    }
}
