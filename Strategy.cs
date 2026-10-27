namespace BtcSignalBot;

public sealed class StrategySettings
{
    public string Name { get; init; } = "Swing";

    // ── Xu hướng + điểm vào ──
    public int FastEma { get; init; } = 50;
    public int SlowEma { get; init; } = 200;
    public int MacdFast { get; init; } = 12;
    public int MacdSlow { get; init; } = 26;
    public int MacdSignal { get; init; } = 9;
    public int RsiPeriod { get; init; } = 14;
    public double RsiOverbought { get; init; } = 70;   // không LONG khi RSI trên mức này
    public double RsiOversold { get; init; } = 30;     // không SHORT khi RSI dưới mức này
    public int AtrPeriod { get; init; } = 14;

    // ── Lọc sideway ──
    public int AdxPeriod { get; init; } = 14;
    public double AdxMin { get; init; } = 20;          // ADX dưới mức này = không có xu hướng → không vào lệnh
    public int ChopPeriod { get; init; } = 14;
    public double ChopMax { get; init; } = 61.8;       // Choppiness trên mức này = đi ngang → không vào lệnh
    public int VolumePeriod { get; init; } = 20;
    public double MinVolumeRatio { get; init; } = 1.0; // volume nến tín hiệu phải ≥ trung bình
    public int BbPeriod { get; init; } = 20;
    public double BbK { get; init; } = 2;

    // ── TP/SL theo thị trường ──
    public int SwingLookback { get; init; } = 10;      // SL đặt sau đáy/đỉnh của N nến gần nhất
    public int RangeLookback { get; init; } = 48;      // kháng cự/hỗ trợ = đỉnh/đáy của M nến trước đó
    public double SlAtrBuffer { get; init; } = 0.2;    // thêm khoảng đệm 0.2 × ATR sau đáy/đỉnh
    public double SlMinAtr { get; init; } = 0.5;       // SL không gần hơn 0.5 × ATR (tránh bị quét bởi nhiễu)
    public double SlMaxAtr { get; init; } = 2.0;       // SL không xa hơn 2 × ATR (tránh lỗ quá lớn)
    public double TargetRMin { get; init; } = 1.3;     // TP = rủi ro × R; R tăng dần theo ADX: ADX = AdxMin → TargetRMin
    public double TargetRMax { get; init; } = 2.5;     //                                      ADX ≥ 40      → TargetRMax
    public double MinRR { get; init; } = 1.2;          // R:R thực tế thấp hơn mức này (vướng kháng cự) → bỏ lệnh
    public double MinTpFeeMultiple { get; init; } = 2; // TP phải ≥ 2 lần phí 2 chiều, không thì lãi không bù nổi phí

    public double FeePct { get; init; } = 0.001;       // 0.1% mỗi chiều (Spot); Futures taker ~0.05%

    /// <summary>
    /// false (mặc định): lệnh chỉ đóng khi chạm TP hoặc SL.
    /// true: thoát sớm thêm khi MACD cắt ngược chiều / giá đóng cửa vượt ngược EMA chậm.
    /// </summary>
    public bool EarlyExit { get; init; } = false;

    /// <summary>Lướt sóng ngắn trên nến nhỏ (1m-5m): chỉ báo nhanh hơn, TP/SL sát hơn.</summary>
    public static StrategySettings Scalp(double feePct) => new()
    {
        Name = "Scalp",
        FastEma = 21, SlowEma = 50,
        MacdFast = 8, MacdSlow = 21, MacdSignal = 5,
        RsiPeriod = 9,
        SwingLookback = 6, RangeLookback = 36,
        SlAtrBuffer = 0.15, SlMinAtr = 0.6, SlMaxAtr = 1.8,
        TargetRMin = 1.1, TargetRMax = 1.8, MinRR = 1.0,
        // Volume 5m rất lệch (vài nến đột biến kéo trung bình lên), so với trung bình gốc thì đa số nến "dưới TB":
        // chỉ loại nến volume thấp hẳn (< 0.8× TB) thay vì đòi ≥ TB.
        MinVolumeRatio = 0.8,
        FeePct = feePct,
    };

    /// <summary>Giữ lệnh dài hơn trên nến 15m trở lên.</summary>
    public static StrategySettings Swing(double feePct) => new() { FeePct = feePct };
}

public enum Side { Long, Short }

public enum SignalType { None, Open, Close }

/// <summary>
/// Kế hoạch lệnh: SL/TP tính từ cấu trúc giá + độ mạnh xu hướng, kèm lời giải thích vì sao đặt ở đó.
/// </summary>
public sealed record TradePlan(
    Side Side,
    double Entry,
    double StopLoss,
    double TakeProfit,
    double RiskReward,
    double TargetR,       // mục tiêu R chọn theo ADX (trước khi bị kháng cự cắt ngắn)
    string SlBasis,
    string TpBasis,
    double? Obstacle);    // kháng cự (Long) / hỗ trợ (Short) chặn trước mục tiêu, nếu có

/// <summary>
/// Tín hiệu. Open: Price = giá vào, kèm TakeProfit/StopLoss.
/// Close: Price = giá thoát (giá TP/SL nếu chạm — Hit = "TP"/"SL", ngược lại là giá đóng cửa).
/// </summary>
public sealed record Signal(
    SignalType Type,
    Side Side,
    string Reason,
    double Price = double.NaN,
    double TakeProfit = double.NaN,
    double StopLoss = double.NaN,
    string? Hit = null,
    IndicatorSnapshot? Context = null,   // chỉ báo tại nến ra tín hiệu (để giải thích lý do vào lệnh)
    TradePlan? Plan = null)
{
    public static readonly Signal None = new(SignalType.None, Side.Long, "");

    /// <summary>Đóng do chạm TP/SL (lệnh chờ, khớp đúng giá) chứ không phải thoát sớm.</summary>
    public bool HitTarget => Hit is not null;

    /// <summary>Kết quả của tín hiệu đóng: "TP", "SL" hoặc "THOÁT".</summary>
    public string Outcome => Hit ?? "THOÁT";
}

/// <summary>
/// Chiến lược 2 chiều, mỗi lúc tối đa 1 lệnh:
///  LONG  khi MACD histogram cắt lên 0, giá &gt; EMA chậm, EMA nhanh &gt; EMA chậm, RSI chưa quá mua.
///  SHORT đối xứng.
///  Lọc sideway: chỉ vào lệnh khi ADX ≥ AdxMin (có xu hướng), Choppiness ≤ ChopMax (không đi ngang),
///  +DI/−DI cùng chiều lệnh và volume ≥ trung bình.
///  SL đặt sau đáy/đỉnh gần nhất (+ đệm ATR), TP = rủi ro × R với R tăng theo ADX, cắt ngắn trước kháng cự/hỗ trợ.
///  R:R thực tế quá thấp hoặc TP không bù nổi phí → bỏ lệnh.
///  Lệnh chạy cho tới khi chạm SL hoặc TP — real-time kiểm tra ngay trong nến (CheckTargets).
/// Tín hiệu VÀO lệnh chỉ đánh giá trên nến ĐÃ ĐÓNG để không "vẽ lại".
/// </summary>
public sealed class MacdTrendStrategy(StrategySettings s)
{
    public StrategySettings Settings => s;
    public Side? Position { get; private set; }        // null = không có lệnh
    public bool InPosition => Position is not null;
    public double EntryPrice { get; private set; }
    public DateTimeOffset EntryTime { get; private set; }
    public double TakeProfit { get; private set; }
    public double StopLoss { get; private set; }

    /// <summary>Đánh giá nến ĐÃ ĐÓNG và cập nhật trạng thái vị thế. Trả về 0-2 tín hiệu (đóng rồi mở).</summary>
    public IReadOnlyList<Signal> Evaluate(IndicatorSnapshot x) => Step(x, commit: true);

    /// <summary>
    /// "Dự báo": tín hiệu sẽ ra nếu nến đang chạy kết thúc ở giá hiện tại. Không đổi trạng thái.
    /// </summary>
    public IReadOnlyList<Signal> Preview(IndicatorSnapshot x) => Step(x, commit: false);

    /// <summary>Thị trường đang đi ngang (không nên vào lệnh) hay có xu hướng.</summary>
    public (bool Sideway, string Label) Regime(IndicatorSnapshot x)
    {
        if (double.IsNaN(x.Adx) || double.IsNaN(x.Chop)) return (false, "Đang tính chỉ báo…");
        bool sideway = x.Adx < s.AdxMin || x.Chop > s.ChopMax;
        if (sideway)
            return (true, $"SIDEWAY (ADX {x.Adx:F0}, CHOP {x.Chop:F0}): bỏ qua mua bán");
        string strength = x.Adx >= 40 ? "rất mạnh" : x.Adx >= 30 ? "mạnh" : "vừa";
        return (false, $"XU HƯỚNG {(x.PlusDi >= x.MinusDi ? "TĂNG" : "GIẢM")} {strength} (ADX {x.Adx:F0}, CHOP {x.Chop:F0})");
    }

    /// <summary>
    /// Tính SL/TP cho lệnh nếu vào tại giá đóng cửa của <paramref name="x"/>:
    ///  SL: sau đáy (Long) / đỉnh (Short) của SwingLookback nến gần nhất + đệm ATR, giới hạn trong [SlMinAtr, SlMaxAtr] × ATR.
    ///  TP: rủi ro × R, R tăng theo ADX (xu hướng càng mạnh càng để lệnh chạy xa).
    ///      Nếu có kháng cự/hỗ trợ trước mục tiêu → đặt TP ngay trước mức đó.
    /// </summary>
    public TradePlan Plan(Side side, IndicatorSnapshot x)
    {
        int dir = side == Side.Long ? 1 : -1;
        double entry = x.Close, atr = x.Atr;
        string low = side == Side.Long ? "đáy" : "đỉnh";

        // ── SL theo cấu trúc giá ──
        double swing = side == Side.Long ? x.SwingLow : x.SwingHigh;
        double risk = dir * (entry - swing) + s.SlAtrBuffer * atr;
        string slBasis;
        if (risk < s.SlMinAtr * atr)
        {
            risk = s.SlMinAtr * atr;
            slBasis = $"{low} {s.SwingLookback} nến gần nhất ({swing:N2}) quá sát giá vào, nên nới SL ra tối thiểu {s.SlMinAtr:0.##} × ATR " +
                      "để không bị nhiễu quét";
        }
        else if (risk > s.SlMaxAtr * atr)
        {
            risk = s.SlMaxAtr * atr;
            slBasis = $"{low} {s.SwingLookback} nến gần nhất ({swing:N2}) quá xa, nên giới hạn SL ở {s.SlMaxAtr:0.##} × ATR " +
                      "để lỗ không quá lớn";
        }
        else
            slBasis = $"đặt {(side == Side.Long ? "dưới" : "trên")} {low} {s.SwingLookback} nến gần nhất ({swing:N2}) " +
                      $"thêm {s.SlAtrBuffer:0.##} × ATR đệm: nếu giá phá qua {low} này thì nhịp {(side == Side.Long ? "tăng" : "giảm")} " +
                      "đã hỏng, nên cắt lỗ";
        double sl = entry - dir * risk;

        // ── TP theo độ mạnh xu hướng ──
        double strength = Math.Clamp((x.Adx - s.AdxMin) / (40 - s.AdxMin), 0, 1);
        double targetR = s.TargetRMin + (s.TargetRMax - s.TargetRMin) * strength;
        double tp = entry + dir * risk * targetR;
        string tpBasis = $"ADX {x.Adx:F0} (xu hướng {(x.Adx >= 40 ? "rất mạnh" : x.Adx >= 30 ? "mạnh" : "vừa")}) → " +
                         $"mục tiêu {targetR:0.0#} lần rủi ro";

        // ── Kháng cự / hỗ trợ chặn đường ──
        double level = side == Side.Long ? x.RangeHigh : x.RangeLow;
        double? obstacle = null;
        if (dir * (level - entry) > 0.1 * atr && dir * (tp - level) > 0)
        {
            obstacle = level;
            tp = level - dir * 0.1 * atr;
            tpBasis += $", nhưng {(side == Side.Long ? "kháng cự" : "hỗ trợ")} {level:N2} " +
                       $"({(side == Side.Long ? "đỉnh" : "đáy")} {s.RangeLookback} nến trước) nằm chắn trước, " +
                       $"nên TP đặt ngay {(side == Side.Long ? "dưới" : "trên")} mức đó vì giá dễ bị đẩy ngược tại đây";
        }
        else if (dir * (level - entry) <= 0.1 * atr)
            tpBasis += $", và giá đang vượt {(side == Side.Long ? "đỉnh" : "đáy")} {s.RangeLookback} nến nên phía trước không có " +
                       $"{(side == Side.Long ? "kháng cự" : "hỗ trợ")} gần";

        double rr = dir * (tp - entry) / risk;
        return new TradePlan(side, entry, sl, tp, rr, targetR, slBasis, tpBasis, obstacle);
    }

    /// <summary>Từng điều kiện LONG và đã thỏa hay chưa (để hiển thị trên chart).</summary>
    public IReadOnlyList<(string Name, bool Ok)> LongConditions(IndicatorSnapshot x) => Conditions(Side.Long, x);

    /// <summary>Từng điều kiện SHORT, đối xứng với LONG.</summary>
    public IReadOnlyList<(string Name, bool Ok)> ShortConditions(IndicatorSnapshot x) => Conditions(Side.Short, x);

    private List<(string Name, bool Ok)> Conditions(Side side, IndicatorSnapshot x)
    {
        bool isLong = side == Side.Long;
        var plan = x.IsReady ? Plan(side, x) : null;
        double tpPct = plan is null ? 0 : Math.Abs(plan.TakeProfit / plan.Entry - 1);
        return
        [
            (isLong ? "MACD cắt lên" : "MACD cắt xuống", isLong ? x.PrevHist <= 0 && x.Hist > 0 : x.PrevHist >= 0 && x.Hist < 0),
            ($"Giá {(isLong ? ">" : "<")} EMA{s.SlowEma}", isLong ? x.Close > x.EmaSlow : x.Close < x.EmaSlow),
            ($"EMA{s.FastEma} {(isLong ? ">" : "<")} EMA{s.SlowEma}", isLong ? x.EmaFast > x.EmaSlow : x.EmaFast < x.EmaSlow),
            (isLong ? $"RSI < {s.RsiOverbought:F0}" : $"RSI > {s.RsiOversold:F0}",
                isLong ? x.Rsi < s.RsiOverbought : x.Rsi > s.RsiOversold),
            ($"Có xu hướng: ADX ≥ {s.AdxMin:F0}", x.Adx >= s.AdxMin),
            ($"Không đi ngang: CHOP ≤ {s.ChopMax:0.#}", x.Chop <= s.ChopMax),
            (isLong ? "+DI > −DI" : "−DI > +DI", isLong ? x.PlusDi > x.MinusDi : x.MinusDi > x.PlusDi),
            ($"Volume ≥ {s.MinVolumeRatio:0.#}× TB", x.VolumeRatio >= s.MinVolumeRatio),
            ($"R:R ≥ {s.MinRR:0.0#}", plan is not null && plan.RiskReward >= s.MinRR),
            ($"TP ≥ {s.MinTpFeeMultiple:0.#}× phí", tpPct >= s.MinTpFeeMultiple * 2 * s.FeePct),
        ];
    }

    private List<Signal> Step(IndicatorSnapshot x, bool commit)
    {
        var signals = new List<Signal>(2);
        if (!x.IsReady) return signals;

        bool flat = Position is null;
        if (Position is { } side && CheckExit(x, side) is { } exit)
        {
            signals.Add(exit);
            flat = true;
            if (commit) Position = null;
        }
        if (!flat) return signals;

        Side? open =
            LongConditions(x).All(c => c.Ok) ? Side.Long :
            ShortConditions(x).All(c => c.Ok) ? Side.Short :
            null;
        if (open is not { } o) return signals;

        var plan = Plan(o, x);
        if (commit)
        {
            Position = o;
            EntryPrice = x.Close;
            EntryTime = x.Time;
            StopLoss = plan.StopLoss;
            TakeProfit = plan.TakeProfit;
        }
        string reason = o == Side.Long
            ? $"MACD cắt lên, xu hướng tăng (ADX {x.Adx:F0}), RSI {x.Rsi:F1}, R:R 1:{plan.RiskReward:0.0#}"
            : $"MACD cắt xuống, xu hướng giảm (ADX {x.Adx:F0}), RSI {x.Rsi:F1}, R:R 1:{plan.RiskReward:0.0#}";
        signals.Add(new Signal(SignalType.Open, o, reason, x.Close, plan.TakeProfit, plan.StopLoss, Context: x, Plan: plan));
        return signals;
    }

    /// <summary>
    /// Kiểm tra TP/SL ngay trong nến đang chạy (real-time, không chờ đóng nến).
    /// <paramref name="high"/>/<paramref name="low"/> = đỉnh/đáy của nến đang chạy tính đến lúc này.
    /// Chạm thì đóng lệnh luôn và trả về tín hiệu đóng.
    /// </summary>
    public Signal? CheckTargets(double high, double low)
    {
        if (Position is not { } side || TargetHit(side, high, low) is not { } exit) return null;
        Position = null;
        return exit;
    }

    private Signal? CheckExit(IndicatorSnapshot x, Side side)
    {
        if (TargetHit(side, x.High, x.Low) is { } hit) return hit;
        if (!s.EarlyExit) return null;

        (string Reason, double Price)? early = side == Side.Long
            ? x.PrevHist >= 0 && x.Hist < 0 ? ("Thoát sớm: MACD cắt xuống", x.Close) :
              x.Close < x.EmaSlow ? ($"Thoát sớm: đóng cửa dưới EMA{s.SlowEma}", x.Close) :
              null
            : x.PrevHist <= 0 && x.Hist > 0 ? ("Thoát sớm: MACD cắt lên", x.Close) :
              x.Close > x.EmaSlow ? ($"Thoát sớm: đóng cửa trên EMA{s.SlowEma}", x.Close) :
              null;
        return early is { } e ? new Signal(SignalType.Close, side, e.Reason, e.Price) : null;
    }

    private Signal? TargetHit(Side side, double high, double low)
    {
        // Nến chạm cả SL và TP: không biết cái nào trước -> giả định xấu nhất là SL.
        (string Reason, double Price, string Hit)? exit = side == Side.Long
            ? low <= StopLoss ? ("Chạm SL", StopLoss, "SL") :
              high >= TakeProfit ? ("Chạm TP", TakeProfit, "TP") :
              null
            : high >= StopLoss ? ("Chạm SL", StopLoss, "SL") :
              low <= TakeProfit ? ("Chạm TP", TakeProfit, "TP") :
              null;
        return exit is { } e ? new Signal(SignalType.Close, side, e.Reason, e.Price, Hit: e.Hit) : null;
    }
}
