namespace BtcSignalBot;

public sealed record NoteSection(string Title, IReadOnlyList<string> Lines);

/// <summary>
/// Ghi chú giải thích một lệnh bằng lời: vì sao vào lệnh, vì sao đặt SL/TP ở mức đó, vì sao đóng lệnh.
/// Dựng từ giá trị chỉ báo tại nến ra tín hiệu (Signal.Context).
/// </summary>
public static class TradeNotes
{
    public static List<NoteSection> Explain(
        Signal open, StrategySettings s, double margin, double leverage, double feePct,
        string? exitOutcome = null, string? exitReason = null, double? exitPrice = null, double? pnl = null)
    {
        bool isLong = open.Side == Side.Long;
        double entry = open.Price, tp = open.TakeProfit, sl = open.StopLoss;
        double notional = margin * leverage, qty = notional / entry;
        double slDist = Math.Abs(entry - sl), tpDist = Math.Abs(tp - entry);
        double roundTripFee = notional * feePct * 2;
        double lossUsd = qty * slDist + roundTripFee, gainUsd = qty * tpDist - roundTripFee;
        double rr = slDist > 0 ? tpDist / slDist : double.NaN;
        var x = open.Context;
        var sections = new List<NoteSection>();

        // ── 1. Vì sao vào lệnh ──
        var why = new List<string>();
        if (x is null) why.Add(open.Reason);
        else if (isLong)
        {
            why.Add($"MACD histogram cắt lên trên 0 (nến trước {x.PrevHist:F2} → nến này {x.Hist:F2}): " +
                    "động lượng vừa chuyển từ giảm sang tăng.");
            why.Add($"Giá {x.Close:N2} nằm trên EMA{s.SlowEma} ({x.EmaSlow:N2}, cao hơn {x.Close / x.EmaSlow - 1:0.00%}): " +
                    "xu hướng lớn đang tăng.");
            why.Add($"EMA{s.FastEma} ({x.EmaFast:N2}) > EMA{s.SlowEma}: xu hướng trung hạn cùng chiều tăng " +
                    "→ chỉ mua thuận xu hướng, không bắt đáy.");
            why.Add($"RSI {x.Rsi:F1} < {s.RsiOverbought:F0}: chưa quá mua, giá còn dư địa để tăng.");
        }
        else
        {
            why.Add($"MACD histogram cắt xuống dưới 0 (nến trước {x.PrevHist:F2} → nến này {x.Hist:F2}): " +
                    "động lượng vừa chuyển từ tăng sang giảm.");
            why.Add($"Giá {x.Close:N2} nằm dưới EMA{s.SlowEma} ({x.EmaSlow:N2}, thấp hơn {1 - x.Close / x.EmaSlow:0.00%}): " +
                    "xu hướng lớn đang giảm.");
            why.Add($"EMA{s.FastEma} ({x.EmaFast:N2}) < EMA{s.SlowEma}: xu hướng trung hạn cùng chiều giảm " +
                    "→ chỉ bán thuận xu hướng, không bắt đỉnh.");
            why.Add($"RSI {x.Rsi:F1} > {s.RsiOversold:F0}: chưa quá bán, giá còn dư địa để giảm.");
        }
        if (x is not null && !double.IsNaN(x.Adx))
        {
            why.Add($"Không phải sideway: ADX {x.Adx:F0} ≥ {s.AdxMin:F0} (thị trường có xu hướng rõ) và " +
                    $"Choppiness {x.Chop:F0} ≤ {s.ChopMax:0.#} (giá đang chạy một chiều, không giằng co).");
            why.Add(isLong
                ? $"+DI {x.PlusDi:F0} > −DI {x.MinusDi:F0}: bên mua đang áp đảo bên bán."
                : $"−DI {x.MinusDi:F0} > +DI {x.PlusDi:F0}: bên bán đang áp đảo bên mua.");
            why.Add($"Volume nến tín hiệu = {x.VolumeRatio:0.0}× trung bình {s.VolumePeriod} nến: có dòng tiền xác nhận, không phải nến \"rỗng\".");
        }
        why.Add("Sau khi vào, lệnh được giữ cho tới khi chạm TP hoặc SL. Không có thoát sớm, không đóng theo nến.");
        sections.Add(new($"Vì sao {(isLong ? "MUA (LONG)" : "BÁN (SHORT)")} ở {entry:N2}", why));

        // ── 2. Vì sao đặt SL ở đó ──
        var slLines = new List<string>();
        if (x is not null)
            slLines.Add($"ATR({s.AtrPeriod}) lúc vào lệnh = {x.Atr:N2}$: biên độ dao động trung bình của một nến.");
        if (open.Plan is { } plan)
            slLines.Add($"SL {char.ToUpper(plan.SlBasis[0])}{plan.SlBasis[1..]}.");
        slLines.Add($"Khoảng cách SL = {slDist:N2}$ ({slDist / entry:0.00%}" +
                    (x is not null ? $", ≈ {slDist / x.Atr:0.0#} × ATR)." : ")."));
        slLines.Add($"Nếu chạm SL: lỗ khoảng {lossUsd:N2}$ (≈ {lossUsd / margin:0%} ký quỹ, đã gồm phí).");
        if (leverage > 1)
        {
            double liq = PaperAccount.LiquidationPrice(open.Side, entry, leverage);
            double liqDist = Math.Abs(entry - liq);
            slLines.Add(liqDist > slDist
                ? $"Giá thanh lý ở x{leverage:0.#} là {liq:N2} (cách {liqDist / entry:0.00%}), xa hơn SL → SL khớp trước, không bị cháy ký quỹ."
                : $"⚠ Giá thanh lý ở x{leverage:0.#} là {liq:N2} (cách {liqDist / entry:0.00%}), GẦN HƠN SL → " +
                  $"giá đi ngược sẽ bị thanh lý, mất hết {margin:N2}$ trước khi chạm SL.");
        }
        sections.Add(new($"Vì sao đặt SL ở {sl:N2}", slLines));

        // ── 3. Vì sao đặt TP ở đó ──
        var tpLines = new List<string>();
        if (open.Plan is { } tpPlan)
            tpLines.Add($"TP theo {tpPlan.TpBasis}.");
        tpLines.Add($"TP cách giá vào {tpDist:N2}$ ({tpDist / entry:0.00%}) → R:R thực tế 1:{rr:0.0#}. " +
                    "R:R không cố định: xu hướng càng mạnh càng để lệnh chạy xa, gặp kháng cự/hỗ trợ thì chốt sớm hơn.");
        tpLines.Add($"Nếu chạm TP: lãi khoảng {gainUsd:N2}$ (≈ {gainUsd / margin:0%} ký quỹ, đã trừ phí).");
        if (gainUsd > 0)
            tpLines.Add($"Để hòa vốn cần thắng ít nhất {lossUsd / (lossUsd + gainUsd):0%} số lệnh " +
                        $"(phí 2 chiều ~{roundTripFee:N2}$ mỗi lệnh làm điểm hòa vốn cao hơn mức {1 / (1 + rr):0%} khi không có phí).");
        else
            tpLines.Add($"⚠ Phí 2 chiều (~{roundTripFee:N2}$) lớn hơn tiền lãi khi chạm TP: lệnh này kể cả thắng vẫn lỗ.");
        sections.Add(new($"Vì sao đặt TP ở {tp:N2}", tpLines));

        // ── 4. Vì sao đóng lệnh ──
        if (exitOutcome is not null && exitPrice is { } px)
        {
            var exitLines = new List<string>
            {
                exitOutcome switch
                {
                    "TP" => $"Giá chạm TP {px:N2}: kịch bản đúng, chốt lời theo kế hoạch.",
                    "SL" => $"Giá chạm SL {px:N2}: tín hiệu sai, cắt lỗ theo kế hoạch để giữ vốn.",
                    "THANH LÝ" => $"Giá chạm mức thanh lý {px:N2} trước SL: mất toàn bộ ký quỹ của lệnh.",
                    "HẾT GIỜ" => $"Hết thời gian chạy thử, đóng ở giá thị trường {px:N2}.",
                    _ => $"{exitReason} tại {px:N2}.",
                },
            };
            if (pnl is { } p) exitLines.Add($"Kết quả: {p:+0.00;-0.00}$ ({p / margin:+0%;-0%} ký quỹ, đã gồm phí).");
            sections.Add(new($"Vì sao đóng lệnh ({exitOutcome})", exitLines));
        }

        return sections;
    }

    /// <summary>Dạng văn bản thuần (cho Excel / console).</summary>
    public static string ToText(IEnumerable<NoteSection> sections) =>
        string.Join("\n\n", sections.Select(s => s.Title + ":\n" + string.Join("\n", s.Lines.Select(l => "• " + l))));
}
