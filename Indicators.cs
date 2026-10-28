namespace BtcSignalBot;

public static class Indicators
{
    public static double[] Sma(IReadOnlyList<double> v, int period)
    {
        var r = NaNArray(v.Count);
        double sum = 0;
        for (int i = 0; i < v.Count; i++)
        {
            sum += v[i];
            if (i >= period) sum -= v[i - period];
            if (i >= period - 1) r[i] = sum / period;
        }
        return r;
    }

    /// <summary>EMA, bỏ qua các giá trị NaN ở đầu chuỗi (seed bằng SMA).</summary>
    public static double[] Ema(IReadOnlyList<double> v, int period)
    {
        var r = NaNArray(v.Count);
        int start = 0;
        while (start < v.Count && double.IsNaN(v[start])) start++;
        if (v.Count - start < period) return r;

        double sum = 0;
        for (int i = start; i < start + period; i++) sum += v[i];
        int seed = start + period - 1;
        r[seed] = sum / period;

        double k = 2.0 / (period + 1);
        for (int i = seed + 1; i < v.Count; i++)
            r[i] = (v[i] - r[i - 1]) * k + r[i - 1];
        return r;
    }

    public static (double[] Macd, double[] Signal, double[] Hist) Macd(
        IReadOnlyList<double> close, int fast = 12, int slow = 26, int signal = 9)
    {
        var emaFast = Ema(close, fast);
        var emaSlow = Ema(close, slow);
        var macd = new double[close.Count];
        for (int i = 0; i < close.Count; i++) macd[i] = emaFast[i] - emaSlow[i]; // NaN lan truyền tự nhiên

        var sig = Ema(macd, signal);
        var hist = new double[close.Count];
        for (int i = 0; i < close.Count; i++) hist[i] = macd[i] - sig[i];
        return (macd, sig, hist);
    }

    /// <summary>RSI theo cách làm mượt của Wilder.</summary>
    public static double[] Rsi(IReadOnlyList<double> close, int period = 14)
    {
        var r = NaNArray(close.Count);
        if (close.Count <= period) return r;

        double gain = 0, loss = 0;
        for (int i = 1; i <= period; i++)
        {
            double d = close[i] - close[i - 1];
            if (d > 0) gain += d; else loss -= d;
        }
        gain /= period; loss /= period;
        r[period] = ToRsi(gain, loss);

        for (int i = period + 1; i < close.Count; i++)
        {
            double d = close[i] - close[i - 1];
            gain = (gain * (period - 1) + Math.Max(d, 0)) / period;
            loss = (loss * (period - 1) + Math.Max(-d, 0)) / period;
            r[i] = ToRsi(gain, loss);
        }
        return r;
    }

    /// <summary>ATR (Average True Range) làm mượt kiểu Wilder — đo biên độ dao động để đặt TP/SL.</summary>
    public static double[] Atr(IReadOnlyList<Candle> c, int period = 14)
    {
        var r = NaNArray(c.Count);
        if (c.Count <= period) return r;

        double TrueRange(int i) => Math.Max(c[i].High - c[i].Low,
            Math.Max(Math.Abs(c[i].High - c[i - 1].Close), Math.Abs(c[i].Low - c[i - 1].Close)));

        double atr = 0;
        for (int i = 1; i <= period; i++) atr += TrueRange(i);
        atr /= period;
        r[period] = atr;

        for (int i = period + 1; i < c.Count; i++)
        {
            atr = (atr * (period - 1) + TrueRange(i)) / period;
            r[i] = atr;
        }
        return r;
    }

    private static double TrueRange(IReadOnlyList<Candle> c, int i) => Math.Max(c[i].High - c[i].Low,
        Math.Max(Math.Abs(c[i].High - c[i - 1].Close), Math.Abs(c[i].Low - c[i - 1].Close)));

    /// <summary>
    /// ADX + DI của Wilder. ADX đo ĐỘ MẠNH xu hướng (không phân biệt tăng/giảm): &lt; 20 ≈ không có xu hướng.
    /// +DI &gt; −DI: bên mua chiếm ưu thế, ngược lại bên bán.
    /// </summary>
    public static (double[] Adx, double[] PlusDi, double[] MinusDi) Adx(IReadOnlyList<Candle> c, int period = 14)
    {
        int n = c.Count;
        var adx = NaNArray(n);
        var pdi = NaNArray(n);
        var mdi = NaNArray(n);
        if (n <= 2 * period) return (adx, pdi, mdi);

        double tr = 0, pdm = 0, mdm = 0, dxSum = 0, adxPrev = 0;
        for (int i = 1; i < n; i++)
        {
            double up = c[i].High - c[i - 1].High, down = c[i - 1].Low - c[i].Low;
            double p = up > down && up > 0 ? up : 0, m = down > up && down > 0 ? down : 0;

            if (i <= period)
            {
                tr += TrueRange(c, i); pdm += p; mdm += m;
                if (i < period) continue;
            }
            else
            {
                tr = tr - tr / period + TrueRange(c, i);
                pdm = pdm - pdm / period + p;
                mdm = mdm - mdm / period + m;
            }

            pdi[i] = tr == 0 ? 0 : 100 * pdm / tr;
            mdi[i] = tr == 0 ? 0 : 100 * mdm / tr;
            double sum = pdi[i] + mdi[i];
            double dx = sum == 0 ? 0 : 100 * Math.Abs(pdi[i] - mdi[i]) / sum;

            if (i < 2 * period - 1) dxSum += dx;
            else if (i == 2 * period - 1) adx[i] = adxPrev = (dxSum + dx) / period;
            else adx[i] = adxPrev = (adxPrev * (period - 1) + dx) / period;
        }
        return (adx, pdi, mdi);
    }

    /// <summary>
    /// Choppiness Index: 100 = đi ngang hoàn toàn, 0 = một chiều. Trên 61.8 thường được coi là thị trường "choppy" (sideway).
    /// </summary>
    public static double[] Choppiness(IReadOnlyList<Candle> c, int period = 14)
    {
        var r = NaNArray(c.Count);
        for (int i = period; i < c.Count; i++)
        {
            double sumTr = 0, hh = double.MinValue, ll = double.MaxValue;
            for (int j = i - period + 1; j <= i; j++)
            {
                sumTr += TrueRange(c, j);
                hh = Math.Max(hh, c[j].High);
                ll = Math.Min(ll, c[j].Low);
            }
            if (hh > ll) r[i] = 100 * Math.Log10(sumTr / (hh - ll)) / Math.Log10(period);
        }
        return r;
    }

    /// <summary>Bollinger Bands: SMA ± k độ lệch chuẩn.</summary>
    public static (double[] Upper, double[] Lower) Bollinger(IReadOnlyList<double> v, int period = 20, double k = 2)
    {
        var mid = Sma(v, period);
        var up = NaNArray(v.Count);
        var lo = NaNArray(v.Count);
        for (int i = period - 1; i < v.Count; i++)
        {
            double var = 0;
            for (int j = i - period + 1; j <= i; j++) var += (v[j] - mid[i]) * (v[j] - mid[i]);
            double sd = Math.Sqrt(var / period);
            up[i] = mid[i] + k * sd;
            lo[i] = mid[i] - k * sd;
        }
        return (up, lo);
    }

    /// <summary>Giá trị lớn nhất / nhỏ nhất trong <paramref name="lookback"/> phần tử, kết thúc tại i − <paramref name="skip"/>.</summary>
    public static double[] RollingMax(IReadOnlyList<double> v, int lookback, int skip = 0) => Rolling(v, lookback, skip, Math.Max);
    public static double[] RollingMin(IReadOnlyList<double> v, int lookback, int skip = 0) => Rolling(v, lookback, skip, Math.Min);

    private static double[] Rolling(IReadOnlyList<double> v, int lookback, int skip, Func<double, double, double> pick)
    {
        var r = NaNArray(v.Count);
        for (int i = lookback - 1 + skip; i < v.Count; i++)
        {
            double x = v[i - skip];
            for (int j = i - skip - lookback + 1; j < i - skip; j++) x = pick(x, v[j]);
            r[i] = x;
        }
        return r;
    }

    private static double ToRsi(double gain, double loss) =>
        loss == 0 ? 100 : 100 - 100 / (1 + gain / loss);

    private static double[] NaNArray(int n)
    {
        var a = new double[n];
        Array.Fill(a, double.NaN);
        return a;
    }
}

/// <summary>Giá trị các chỉ báo tại một nến.</summary>
public sealed record IndicatorSnapshot(
    DateTimeOffset Time,
    double High,
    double Low,
    double Close,
    double EmaFast,
    double EmaSlow,
    double Macd,
    double MacdSignal,
    double Hist,
    double PrevHist,
    double Rsi,
    double Atr,
    double Adx,
    double PlusDi,
    double MinusDi,
    double Chop,
    double BbUpper,
    double BbLower,
    double VolumeRatio,   // volume nến này / volume trung bình
    double SwingLow,      // đáy thấp nhất N nến gần nhất (gồm nến này) — chỗ đặt SL lệnh Long
    double SwingHigh,     // đỉnh cao nhất N nến gần nhất — chỗ đặt SL lệnh Short
    double RangeHigh,     // đỉnh cao nhất M nến TRƯỚC đó — kháng cự gần nhất cho TP lệnh Long
    double RangeLow,      // đáy thấp nhất M nến trước đó — hỗ trợ gần nhất cho TP lệnh Short
    // ── Cho điểm vào kiểu "cửa sổ" / pullback ──
    double Open,
    double PrevHigh,
    double PrevLow,
    int BarsSinceCrossUp,     // số nến từ lần MACD histogram cắt lên 0 gần nhất (0 = chính nến này)
    int BarsSinceCrossDown,
    int BarsSinceTouchLong,   // số nến từ lần giá (đáy nến) chạm/xuyên EMA nhanh gần nhất — nhịp hồi trong xu hướng tăng
    int BarsSinceTouchShort)  // số nến từ lần đỉnh nến chạm/vượt EMA nhanh — nhịp hồi trong xu hướng giảm
{
    public const int Never = 9999;

    public bool IsReady =>
        !double.IsNaN(EmaSlow) && !double.IsNaN(Hist) && !double.IsNaN(PrevHist) && !double.IsNaN(Rsi)
        && !double.IsNaN(Atr) && !double.IsNaN(Adx) && !double.IsNaN(Chop) && !double.IsNaN(RangeHigh);
}

/// <summary>Tính toàn bộ chỉ báo cho một danh sách nến.</summary>
public sealed class IndicatorSeries
{
    private readonly IReadOnlyList<Candle> _candles;
    private readonly double[] _emaFast, _emaSlow, _macd, _signal, _hist, _rsi, _atr, _adx, _pdi, _mdi, _chop,
        _bbUp, _bbLo, _volRatio, _swingLow, _swingHigh, _rangeHigh, _rangeLow;
    private readonly int[] _sinceCrossUp, _sinceCrossDown, _sinceTouchLong, _sinceTouchShort;

    public IndicatorSeries(IReadOnlyList<Candle> candles, StrategySettings s)
    {
        _candles = candles;
        var close = candles.Select(c => c.Close).ToArray();
        var high = candles.Select(c => c.High).ToArray();
        var low = candles.Select(c => c.Low).ToArray();
        var vol = candles.Select(c => c.Volume).ToArray();
        _emaFast = Indicators.Ema(close, s.FastEma);
        _emaSlow = Indicators.Ema(close, s.SlowEma);
        (_macd, _signal, _hist) = Indicators.Macd(close, s.MacdFast, s.MacdSlow, s.MacdSignal);
        _rsi = Indicators.Rsi(close, s.RsiPeriod);
        _atr = Indicators.Atr(candles, s.AtrPeriod);
        (_adx, _pdi, _mdi) = Indicators.Adx(candles, s.AdxPeriod);
        _chop = Indicators.Choppiness(candles, s.ChopPeriod);
        (_bbUp, _bbLo) = Indicators.Bollinger(close, s.BbPeriod, s.BbK);
        var volSma = Indicators.Sma(vol, s.VolumePeriod);
        _volRatio = vol.Select((v, i) => volSma[i] > 0 ? v / volSma[i] : double.NaN).ToArray();
        _swingLow = Indicators.RollingMin(low, s.SwingLookback);
        _swingHigh = Indicators.RollingMax(high, s.SwingLookback);
        _rangeHigh = Indicators.RollingMax(high, s.RangeLookback, skip: 1);
        _rangeLow = Indicators.RollingMin(low, s.RangeLookback, skip: 1);

        // Đếm số nến kể từ sự kiện gần nhất (để điểm vào không bắt buộc rơi đúng một nến)
        int n = candles.Count;
        _sinceCrossUp = new int[n];
        _sinceCrossDown = new int[n];
        _sinceTouchLong = new int[n];
        _sinceTouchShort = new int[n];
        int up = IndicatorSnapshot.Never, down = IndicatorSnapshot.Never, tl = IndicatorSnapshot.Never, ts = IndicatorSnapshot.Never;
        for (int i = 0; i < n; i++)
        {
            up = up == IndicatorSnapshot.Never ? up : up + 1;
            down = down == IndicatorSnapshot.Never ? down : down + 1;
            tl = tl == IndicatorSnapshot.Never ? tl : tl + 1;
            ts = ts == IndicatorSnapshot.Never ? ts : ts + 1;
            if (i > 0 && _hist[i - 1] <= 0 && _hist[i] > 0) up = 0;
            if (i > 0 && _hist[i - 1] >= 0 && _hist[i] < 0) down = 0;
            if (low[i] <= _emaFast[i]) tl = 0;
            if (high[i] >= _emaFast[i]) ts = 0;
            (_sinceCrossUp[i], _sinceCrossDown[i], _sinceTouchLong[i], _sinceTouchShort[i]) = (up, down, tl, ts);
        }
    }

    public int Count => _candles.Count;

    public IndicatorSnapshot At(int i) => new(
        _candles[i].OpenTime,
        _candles[i].High,
        _candles[i].Low,
        _candles[i].Close,
        _emaFast[i],
        _emaSlow[i],
        _macd[i],
        _signal[i],
        _hist[i],
        i > 0 ? _hist[i - 1] : double.NaN,
        _rsi[i],
        _atr[i],
        _adx[i], _pdi[i], _mdi[i], _chop[i],
        _bbUp[i], _bbLo[i], _volRatio[i],
        _swingLow[i], _swingHigh[i], _rangeHigh[i], _rangeLow[i],
        _candles[i].Open,
        i > 0 ? _candles[i - 1].High : double.NaN,
        i > 0 ? _candles[i - 1].Low : double.NaN,
        _sinceCrossUp[i], _sinceCrossDown[i], _sinceTouchLong[i], _sinceTouchShort[i]);
}
