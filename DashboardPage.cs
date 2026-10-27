namespace BtcSignalBot;

/// <summary>Trang HTML của web dashboard (một file, chart dùng TradingView Lightweight Charts).</summary>
public static class DashboardPage
{
    public const string Html = """
<!doctype html>
<html lang="vi">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>BTC Signal Bot</title>
<script src="https://unpkg.com/lightweight-charts@4.2.0/dist/lightweight-charts.standalone.production.js"></script>
<style>
  :root {
    --bg: #0b0e14; --panel: #121722; --panel-2: #171d2a; --border: #222a3a;
    --text: #e6e9ef; --muted: #8b93a7; --up: #22c55e; --down: #ef4444; --warn: #eab308; --accent: #38bdf8;
  }
  * { box-sizing: border-box; }
  body { margin: 0; background: var(--bg); color: var(--text);
         font: 14px/1.45 system-ui, -apple-system, "Segoe UI", Roboto, sans-serif; }
  .num { font-variant-numeric: tabular-nums; }
  header { display: flex; flex-wrap: wrap; align-items: baseline; gap: 8px 20px; padding: 14px 20px;
           border-bottom: 1px solid var(--border); }
  .sym { font-size: 18px; font-weight: 700; letter-spacing: .3px; }
  .sub { color: var(--muted); }
  .price { font-size: 26px; font-weight: 700; }
  .chip { padding: 2px 8px; border-radius: 999px; background: var(--panel-2); color: var(--muted); font-size: 12px; }
  .live-dot { display: inline-block; width: 8px; height: 8px; border-radius: 50%; background: var(--up); margin-right: 6px; }
  .live-dot.stale { background: var(--down); }
  .spacer { flex: 1; }
  main { display: grid; grid-template-columns: minmax(0, 1fr) 360px; gap: 16px; padding: 16px 20px; }
  .card { background: var(--panel); border: 1px solid var(--border); border-radius: 12px; padding: 14px 16px; }
  .card h3 { margin: 0 0 10px; font-size: 12px; font-weight: 600; letter-spacing: .6px; text-transform: uppercase; color: var(--muted); }
  #chart { height: 62vh; min-height: 360px; padding: 0; overflow: hidden; }
  aside { display: flex; flex-direction: column; gap: 16px; min-width: 0; }
  .banner { border-radius: 10px; padding: 12px 14px; font-weight: 700; font-size: 16px; }
  .banner.long { background: rgba(34,197,94,.15); color: var(--up); border: 1px solid rgba(34,197,94,.4); }
  .banner.short { background: rgba(239,68,68,.15); color: var(--down); border: 1px solid rgba(239,68,68,.4); }
  .banner.wait { background: var(--panel-2); color: var(--muted); border: 1px solid var(--border); font-weight: 600; }
  .banner.alert { background: rgba(234,179,8,.15); color: var(--warn); border: 1px solid rgba(234,179,8,.45); }
  .kv { display: grid; grid-template-columns: auto 1fr; gap: 4px 12px; margin-top: 10px; }
  .kv span:nth-child(odd) { color: var(--muted); }
  .kv span:nth-child(even) { text-align: right; }
  .cond { display: flex; flex-wrap: wrap; gap: 6px; margin: 4px 0 10px; }
  .cond b { width: 100%; font-size: 12px; color: var(--muted); font-weight: 600; }
  .pill { padding: 3px 8px; border-radius: 6px; font-size: 12px; background: var(--panel-2); color: var(--muted); }
  .pill.ok { background: rgba(34,197,94,.15); color: var(--up); }
  .equity { font-size: 28px; font-weight: 700; }
  .bar { height: 6px; background: var(--panel-2); border-radius: 3px; overflow: hidden; margin: 10px 0 4px; }
  .bar > div { height: 100%; background: var(--accent); }
  .btn { display: inline-block; margin-top: 12px; padding: 8px 12px; border-radius: 8px; background: var(--accent);
         color: #04121c; font-weight: 600; text-decoration: none; }
  .btn.disabled { background: var(--panel-2); color: var(--muted); pointer-events: none; }
  .up { color: var(--up); } .down { color: var(--down); } .warn { color: var(--warn); } .muted { color: var(--muted); }
  .trades { grid-column: 1 / -1; overflow-x: auto; }
  table { width: 100%; border-collapse: collapse; white-space: nowrap; }
  th, td { padding: 7px 10px; border-bottom: 1px solid var(--border); text-align: right; }
  th { color: var(--muted); font-size: 12px; font-weight: 600; }
  th:nth-child(-n+3), td:nth-child(-n+3) { text-align: left; }
  .warnbox { margin-top: 10px; padding: 8px 10px; border-radius: 8px; font-size: 12px;
             background: rgba(234,179,8,.08); color: var(--warn); border: 1px solid rgba(234,179,8,.25); }
  footer { padding: 0 20px 20px; color: var(--muted); font-size: 12px; }

  /* ── Vị thế kiểu Binance ── */
  .wide { grid-column: 1 / -1; }
  .tabs { display: flex; gap: 18px; border-bottom: 1px solid var(--border); margin: -4px -16px 12px; padding: 0 16px; }
  .tab { padding: 8px 0; font-weight: 600; border-bottom: 2px solid var(--warn); }
  .pos-head { display: flex; flex-wrap: wrap; align-items: center; gap: 8px; margin-bottom: 12px; }
  .pos-head .sidebar { width: 4px; height: 20px; border-radius: 2px; }
  .tag { font-size: 12px; padding: 2px 7px; border-radius: 4px; background: var(--panel-2); color: var(--muted); }
  .tag.long { background: rgba(34,197,94,.15); color: var(--up); font-weight: 700; }
  .tag.short { background: rgba(239,68,68,.15); color: var(--down); font-weight: 700; }
  .tag.accent { background: rgba(56,189,248,.12); color: var(--accent); }
  .pos-grid { display: grid; grid-template-columns: repeat(auto-fit, minmax(140px, 1fr)); gap: 12px 18px; }
  .pos-grid .lbl { font-size: 12px; color: var(--muted); margin-bottom: 2px; }
  .pos-grid .val { font-weight: 600; }
  .pos-grid .small { font-size: 12px; color: var(--muted); }
  .pnl-big { font-size: 18px; font-weight: 700; }
  .range { position: relative; height: 8px; margin: 22px 4px 26px; border-radius: 4px;
           background: linear-gradient(90deg, rgba(239,68,68,.55), rgba(239,68,68,.15) var(--e), rgba(34,197,94,.15) var(--e), rgba(34,197,94,.55)); }
  .range .mk { position: absolute; top: 50%; transform: translate(-50%, -50%); }
  .range .dot { width: 14px; height: 14px; border-radius: 50%; background: var(--text); border: 2px solid var(--bg); }
  .range .ent { width: 2px; height: 16px; background: var(--accent); }
  .range .lab { position: absolute; top: 14px; transform: translateX(-50%); font-size: 11px; color: var(--muted); white-space: nowrap; }
  .range .lab.l { transform: none; left: 0; } .range .lab.r { transform: none; right: 0; left: auto; }
  details.note { margin-top: 4px; border: 1px solid var(--border); border-radius: 10px; background: var(--panel-2); }
  details.note > summary { cursor: pointer; padding: 10px 14px; font-weight: 600; list-style: none; display: flex; gap: 8px; }
  details.note > summary::-webkit-details-marker { display: none; }
  details.note > summary::after { content: '▾'; margin-left: auto; color: var(--muted); transition: transform .15s; }
  details.note:not([open]) > summary::after { transform: rotate(-90deg); }
  .note-body { padding: 0 14px 12px; display: grid; grid-template-columns: repeat(auto-fit, minmax(260px, 1fr)); gap: 12px; }
  .note-sec h4 { margin: 6px 0; font-size: 13px; }
  .note-sec ul { margin: 0; padding-left: 18px; color: #c7ccd8; }
  .note-sec li { margin: 3px 0; }
  .btn-sm { padding: 3px 8px; border-radius: 6px; border: 1px solid var(--border); background: var(--panel-2);
            color: var(--text); cursor: pointer; font: inherit; font-size: 12px; }
  tr.note-row td { text-align: left; white-space: normal; background: var(--panel-2); }
  #toasts { position: fixed; right: 16px; bottom: 16px; display: flex; flex-direction: column; gap: 8px; z-index: 10; max-width: calc(100vw - 32px); }
  .toast { background: var(--panel); border: 1px solid var(--border); border-left: 4px solid var(--accent); border-radius: 10px;
           padding: 10px 14px; box-shadow: 0 8px 24px rgba(0,0,0,.4); width: 340px; max-width: 100%; animation: slide .2s ease-out; }
  .toast.long { border-left-color: var(--up); } .toast.short { border-left-color: var(--down); }
  .toast b { display: block; margin-bottom: 2px; }
  @keyframes slide { from { transform: translateY(10px); opacity: 0; } }
  @media (max-width: 900px) {
    main { grid-template-columns: 1fr; padding: 12px 16px; }
    header { padding: 12px 16px; }
    #chart { height: 50vh; }
  }
</style>
</head>
<body>
<header>
  <span class="sym" id="sym">—</span>
  <span class="sub" id="mkt"></span>
  <span class="price num" id="price">—</span>
  <span class="num" id="chg"></span>
  <span class="chip" id="regime"></span>
  <span class="spacer"></span>
  <span class="sub num"><span class="live-dot" id="dot"></span><span id="clock"></span></span>
</header>

<main>
  <div class="card" id="chart"></div>

  <aside>
    <div class="card">
      <h3>Lệnh hiện tại</h3>
      <div id="banner" class="banner wait">Đang tải…</div>
      <div id="posKv" class="kv num"></div>
    </div>

    <div class="card">
      <h3>Điều kiện vào lệnh</h3>
      <div id="conds"></div>
      <div class="kv num" id="ind"></div>
    </div>

    <div class="card" id="sessionCard">
      <h3>Phiên chạy thử</h3>
      <div id="session" class="muted">Không có phiên chạy thử (chạy app với --session 6h).</div>
    </div>
  </aside>

  <div class="card wide">
    <div class="tabs"><span class="tab">Vị thế (<span id="posCount">0</span>)</span></div>
    <div id="posMain" class="muted">Không có vị thế đang mở.</div>
    <details class="note" id="noteBox" hidden>
      <summary>📝 Ghi chú: vì sao vào lệnh, vì sao đặt SL / TP ở đó</summary>
      <div class="note-body" id="noteBody"></div>
    </details>
  </div>

  <div class="card trades">
    <h3>Lệnh trong phiên</h3>
    <table class="num">
      <thead><tr>
        <th>#</th><th>Chiều</th><th>Vào lúc</th><th>Giá vào</th><th>TP</th><th>SL</th><th>R:R</th><th>Thanh lý</th>
        <th>Ra lúc</th><th>Giá ra</th><th>Kết quả</th><th>Phí</th><th>Lãi/lỗ</th><th></th>
      </tr></thead>
      <tbody id="trades"><tr><td colspan="14" class="muted">Chưa có lệnh.</td></tr></tbody>
    </table>
  </div>
</main>
<footer id="note"></footer>
<div id="toasts"></div>

<script>
const $ = id => document.getElementById(id);
const fmt = (v, d = 2) => v == null || typeof v !== 'number' || !isFinite(v) ? '—'
  : v.toLocaleString('en-US', { minimumFractionDigits: d, maximumFractionDigits: d });
const pct = (v, d = 2) => typeof v !== 'number' || !isFinite(v) ? '—' : (v >= 0 ? '+' : '') + (v * 100).toFixed(d) + '%';
const usd = (v, d = 4) => typeof v !== 'number' ? '—' : (v >= 0 ? '+' : '') + v.toFixed(d) + '$';
const cls = v => v > 0 ? 'up' : v < 0 ? 'down' : 'muted';
const hhmm = ms => ms == null ? '—' : new Date(ms).toLocaleString('vi-VN', { hour: '2-digit', minute: '2-digit', day: '2-digit', month: '2-digit' });
const dur = s => { const h = Math.floor(s / 3600), m = Math.floor(s % 3600 / 60), x = s % 60;
  return [h, m, x].map(n => String(n).padStart(2, '0')).join(':'); };
// Lightweight Charts hiển thị theo UTC -> cộng lệch múi giờ để trục thời gian theo giờ máy
const TZ = -new Date().getTimezoneOffset() * 60;

const chart = LightweightCharts.createChart($('chart'), {
  autoSize: true,
  layout: { background: { color: '#121722' }, textColor: '#8b93a7' },
  grid: { vertLines: { color: '#1b2130' }, horzLines: { color: '#1b2130' } },
  rightPriceScale: { borderColor: '#222a3a' },
  timeScale: { borderColor: '#222a3a', timeVisible: true, secondsVisible: false, rightOffset: 6 },
  crosshair: { mode: LightweightCharts.CrosshairMode.Normal },
});
const candles = chart.addCandlestickSeries({
  upColor: '#22c55e', downColor: '#ef4444', borderVisible: false, wickUpColor: '#22c55e', wickDownColor: '#ef4444',
});
const emaFast = chart.addLineSeries({ color: '#eab308', lineWidth: 1, priceLineVisible: false, lastValueVisible: false });
const emaSlow = chart.addLineSeries({ color: '#a855f7', lineWidth: 1, priceLineVisible: false, lastValueVisible: false });
// Dải màu dưới đáy chart: xám = sideway (bot không vào lệnh), xanh/đỏ = có xu hướng tăng/giảm
const regimeBars = chart.addHistogramSeries({ priceScaleId: 'regime', priceLineVisible: false, lastValueVisible: false });
chart.priceScale('regime').applyOptions({ scaleMargins: { top: 0.95, bottom: 0 }, visible: false });

let firstTime = null, lastTime = null, lineKey = '', lines = [];

function priceLines(st) {
  const p = st.position, o = st.session && st.session.open;
  const key = p ? [p.side, p.entry, p.tp, p.sl, o && o.liq].join('|') : '';
  if (key === lineKey) return;
  lines.forEach(l => candles.removePriceLine(l));
  lines = [];
  lineKey = key;
  if (!p) return;
  const add = (price, color, title, style = 0) =>
    lines.push(candles.createPriceLine({ price, color, title, lineWidth: 1, lineStyle: style, axisLabelVisible: true }));
  add(p.tp, '#22c55e', 'TP');
  add(p.entry, '#38bdf8', 'Vào ' + p.side, 2);
  add(p.sl, '#ef4444', 'SL');
  if (o && isFinite(o.liq)) add(o.liq, '#f97316', 'Thanh lý', 1);
}

function render(st) {
  // ── Chart ──
  const shift = c => ({ ...c, time: c.time + TZ });
  const first = st.candles[0].time + TZ, last = st.candles[st.candles.length - 1].time + TZ;
  if (first !== firstTime || last < lastTime || last - lastTime > 86400) {
    candles.setData(st.candles.map(shift));
    emaFast.setData(st.emaFast.map(shift));
    emaSlow.setData(st.emaSlow.map(shift));
    regimeBars.setData(st.regimeBars.map(shift));
  } else {
    st.regimeBars.filter(c => c.time + TZ >= lastTime).forEach(c => regimeBars.update(shift(c)));
    // chỉ cập nhật nến cuối (và nến mới nếu vừa sang nến) để không mất vị trí zoom
    st.candles.filter(c => c.time + TZ >= lastTime).forEach(c => candles.update(shift(c)));
    st.emaFast.filter(c => c.time + TZ >= lastTime).forEach(c => emaFast.update(shift(c)));
    st.emaSlow.filter(c => c.time + TZ >= lastTime).forEach(c => emaSlow.update(shift(c)));
  }
  firstTime = first; lastTime = last;
  candles.setMarkers(st.markers.map(m => ({ ...m, time: m.time + TZ })));
  priceLines(st);

  // ── Header ──
  document.title = `${fmt(st.price)} · ${st.symbol}`;
  $('sym').textContent = st.symbol;
  $('price').textContent = fmt(st.price);
  $('price').className = 'price num ' + (st.change >= 0 ? 'up' : 'down');
  $('chg').innerHTML = `<span class="${cls(st.change)}">${pct(st.change)}</span> <span class="muted">nến này</span>`;
  $('regime').textContent = (st.regime.sideway ? '⏸ ' : '📈 ') + st.regime.label;
  $('regime').style.color = st.regime.sideway ? 'var(--warn)' : 'var(--accent)';
  $('mkt').textContent = `${st.market} · ${st.interval} · ${st.settings.name}`;

  // ── Lệnh hiện tại ──
  const p = st.position, pv = st.preview, b = $('banner');
  if (p) {
    b.className = 'banner ' + (p.side === 'LONG' ? 'long' : 'short');
    b.innerHTML = `● ĐANG ${p.side} <span style="float:right" class="${cls(p.pnl)}">${pct(p.pnl)}</span>`;
    $('posKv').innerHTML = `
      <span>Vào lúc</span><span>${hhmm((p.entryTime) * 1000)}</span>
      <span>Giá vào</span><span>${fmt(p.entry)}</span>
      <span>Take profit</span><span class="up">${fmt(p.tp)} (${pct(p.tp / p.entry - 1)})</span>
      <span>Stop loss</span><span class="down">${fmt(p.sl)} (${pct(p.sl / p.entry - 1)})</span>
      <span>R:R</span><span>1:${p.rr.toFixed(2)}</span>
      <span>Đóng lệnh khi</span><span>chạm TP hoặc SL</span>`;
  } else if (pv && pv.type === 'open') {
    b.className = 'banner alert';
    b.textContent = `⚡ SẮP CÓ TÍN HIỆU ${pv.side} — đang chờ xác nhận`;
    $('posKv').innerHTML = `
      <span>Giá vào dự kiến</span><span>${fmt(pv.price)}</span>
      <span>Take profit</span><span class="up">${fmt(pv.tp)} (${pct(pv.tp / pv.price - 1)})</span>
      <span>Stop loss</span><span class="down">${fmt(pv.sl)} (${pct(pv.sl / pv.price - 1)})</span>
      <span>R:R</span><span>1:${fmt(pv.rr)}</span>`;
  } else if (st.regime.sideway) {
    b.className = 'banner wait';
    b.textContent = '⏸ Thị trường đang SIDEWAY: tạm dừng vào lệnh';
    $('posKv').innerHTML = `<span>Lý do</span><span>${esc(st.regime.label)}</span>`;
  } else {
    b.className = 'banner wait';
    b.textContent = '⏳ Chưa có lệnh: chờ đủ điều kiện LONG hoặc SHORT';
    $('posKv').innerHTML = '';
  }

  // ── Điều kiện ──
  const row = (label, list) => `<div class="cond"><b>${label}</b>${list.map(c =>
    `<span class="pill ${c.ok ? 'ok' : ''}">${c.ok ? '✓' : '✗'} ${c.name}</span>`).join('')}</div>`;
  $('conds').innerHTML = row('LONG', st.longConditions) + row('SHORT', st.shortConditions);
  const i = st.indicators;
  $('ind').innerHTML = `
    <span style="color:#eab308">EMA${st.settings.fast}</span><span>${fmt(i.emaFast)}</span>
    <span style="color:#a855f7">EMA${st.settings.slow}</span><span>${fmt(i.emaSlow)}</span>
    <span>MACD hist</span><span class="${cls(i.hist)}">${fmt(i.hist)}</span>
    <span>RSI</span><span>${fmt(i.rsi, 1)}</span>
    <span>ATR</span><span>${fmt(i.atr)}</span>
    <span>ADX (độ mạnh xu hướng)</span><span class="${i.adx >= st.settings.adxMin ? 'up' : 'warn'}">${fmt(i.adx, 1)}</span>
    <span>+DI / −DI</span><span><span class="up">${fmt(i.plusDi, 1)}</span> / <span class="down">${fmt(i.minusDi, 1)}</span></span>
    <span>Choppiness (đi ngang)</span><span class="${i.chop <= st.settings.chopMax ? 'up' : 'warn'}">${fmt(i.chop, 1)}</span>
    <span>Volume / TB</span><span>${fmt(i.vol, 2)}×</span>
    <span>Độ rộng Bollinger</span><span>${pct(i.bbWidth).replace('+', '')}</span>`;

  // ── Phiên chạy thử ──
  const s = st.session;
  if (s) {
    const change = s.equity / s.capital - 1, total = (s.end - s.start) / 1000;
    const done = Math.min(1, 1 - s.remaining / total);
    const liqMove = Math.max(1 / s.leverage - 0.004, 0);
    $('session').innerHTML = `
      <div class="equity num ${cls(change)}">${fmt(s.equity)}$</div>
      <div class="num"><span class="${cls(change)}">${pct(change, 3)}</span>
        <span class="muted"> so với vốn ${fmt(s.capital)}$</span></div>
      <div class="bar"><div style="width:${(done * 100).toFixed(1)}%"></div></div>
      <div class="muted num">${s.remaining > 0 ? 'Còn ' + dur(s.remaining) : s.open ? 'Hết giờ — chờ lệnh đang mở chạm TP/SL' : 'Đã kết thúc'} · ${hhmm(s.start)} → ${hhmm(s.end)}</div>
      <div class="kv num">
        <span>Mỗi lệnh</span><span>${fmt(s.stake)}$ × ${s.leverage}x = ${fmt(s.stake * s.leverage)}$</span>
        <span>Phí 1 lệnh (2 chiều)</span><span>~${fmt(s.stake * s.leverage * s.fee * 2)}$</span>
        <span>Thanh lý khi giá ngược</span><span class="warn">~${(liqMove * 100).toFixed(2)}%</span>
        <span>Đã chốt</span><span class="${cls(s.pnl)}">${usd(s.pnl, 2)}</span>
        <span>Thắng / Thua</span><span>${s.wins} / ${s.losses}</span>
        ${s.open ? `<span>Đang mở</span><span class="${cls(s.open.unrealized)}">${s.open.side} ${usd(s.open.unrealized, 2)}</span>` : ''}
      </div>
      ${s.leverage >= 20 ? `<div class="warnbox">Đòn bẩy ${s.leverage}x: giá đi ngược ~${(liqMove * 100).toFixed(2)}% là mất hết ${fmt(s.stake)}$ của lệnh.</div>` : ''}
      <a class="btn ${s.hasReport ? '' : 'disabled'}" href="/report.xlsx">⬇ Tải báo cáo Excel</a>
      <a class="btn" style="background:var(--panel-2);color:var(--text)" href="/reports">Các phiên trước</a>`;

    $('trades').innerHTML = s.trades.length ? s.trades.map(t => `
      <tr>
        <td>${t.no}</td>
        <td class="${t.side === 'LONG' ? 'up' : 'down'}">${t.side === 'LONG' ? 'MUA / LONG' : 'BÁN / SHORT'}</td>
        <td>${hhmm(t.entryTime)}</td>
        <td>${fmt(t.entry)}</td><td class="up">${fmt(t.tp)}</td><td class="down">${fmt(t.sl)}</td>
        <td>1:${fmt(t.rr)}</td>
        <td class="warn">${fmt(t.liq)}</td>
        <td>${hhmm(t.exitTime)}</td><td>${fmt(t.exit)}</td>
        <td class="${t.outcome === 'TP' ? 'up' : t.outcome === 'SL' || t.outcome === 'THANH LÝ' ? 'down' : 'warn'}">${t.outcome}</td>
        <td>${fmt(t.fees, 4)}$</td>
        <td class="${cls(t.pnl)}">${t.pnl == null ? '—' : usd(t.pnl)}</td>
        <td><button class="btn-sm" onclick="toggleNote(${t.no})">${expanded.has(t.no) ? 'Ẩn' : 'Ghi chú'}</button></td>
      </tr>${expanded.has(t.no) ? `<tr class="note-row"><td colspan="14"><div class="note-body" style="padding:6px 0">${notesHtml(t.notes)}</div></td></tr>` : ''}`).join('')
      : '<tr><td colspan="14" class="muted">Chưa có lệnh nào trong phiên.</td></tr>';
    notifyTrades(s.trades);
  }

  renderPosition(st);
  $('note').textContent = st.note + ' · Giao dịch giả lập, không phải lời khuyên đầu tư.';
}

// ── Vị thế kiểu Binance ──
const esc = v => String(v).replace(/[&<>"]/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' })[c]);
const notesHtml = notes => notes.map(n =>
  `<div class="note-sec"><h4>${esc(n.Title)}</h4><ul>${n.Lines.map(l => `<li>${esc(l)}</li>`).join('')}</ul></div>`).join('');
const expanded = new Set();
function toggleNote(no) { expanded.has(no) ? expanded.delete(no) : expanded.add(no); lastState && render(lastState); }

let posKey = null, firstPoll = true, lastState = null;
const noteBox = $('noteBox');
try { noteBox.open = localStorage.getItem('noteOpen') !== '0'; } catch { noteBox.open = true; }
noteBox.addEventListener('toggle', () => { try { localStorage.setItem('noteOpen', noteBox.open ? '1' : '0'); } catch {} });

function renderPosition(st) {
  const p = st.binance;
  $('posCount').textContent = p ? 1 : 0;
  if (!p) {
    $('posMain').innerHTML = '<span class="muted">Không có vị thế đang mở. Khi có tín hiệu, lệnh sẽ hiện ở đây kèm TP/SL và ghi chú.</span>';
    noteBox.hidden = true;
    if (posKey && !firstPoll && !(st.session)) toast('Đã đóng vị thế', '', '');
    posKey = null;
    return;
  }
  const isLong = p.side === 'LONG', dir = isLong ? 1 : -1;
  const key = p.side + p.entryTime;
  // vị trí trên thanh SL → TP
  const span = p.tp - p.sl, at = v => Math.min(100, Math.max(0, (v - p.sl) / span * 100));
  const toTp = (p.tp - p.mark) * dir / p.mark, toSl = (p.mark - p.sl) * dir / p.mark;

  $('posMain').innerHTML = `
    <div class="pos-head">
      <span class="sidebar" style="background:${isLong ? 'var(--up)' : 'var(--down)'}"></span>
      <b>${esc(st.symbol)}</b><span class="sub">Vĩnh cửu</span>
      <span class="tag ${isLong ? 'long' : 'short'}">${isLong ? 'MUA / LONG' : 'BÁN / SHORT'}</span>
      <span class="tag">Isolated ${p.leverage}x</span>
      <span class="tag ${p.inSession ? 'accent' : ''}">${p.inSession ? 'Lệnh của phiên thử' : 'Ngoài phiên thử (không tính vào kết quả)'}</span>
      <span class="muted">Vào lúc ${hhmm(p.entryTime)}</span>
    </div>
    <div class="pos-grid num">
      <div><div class="lbl">Kích thước</div><div class="val ${isLong ? 'up' : 'down'}">${dir * p.qty > 0 ? '' : '-'}${p.qty.toFixed(4)} BTC</div>
        <div class="small">${fmt(p.notional)} USDT</div></div>
      <div><div class="lbl">Giá vào</div><div class="val">${fmt(p.entry)}</div></div>
      <div><div class="lbl">Giá hiện tại</div><div class="val">${fmt(p.mark)}</div>
        <div class="small ${cls(dir * (p.mark - p.entry))}">${pct(dir * (p.mark / p.entry - 1))} so với giá vào</div></div>
      <div><div class="lbl">Giá thanh lý</div><div class="val warn">${fmt(p.liq)}</div>
        <div class="small">cách ${pct(Math.abs(p.liq / p.mark - 1)).replace('+', '')}</div></div>
      <div><div class="lbl">Ký quỹ</div><div class="val">${fmt(p.margin)} USDT</div><div class="small">Isolated</div></div>
      <div><div class="lbl">PNL (ROE %)</div>
        <div class="pnl-big ${cls(p.pnl)}">${usd(p.pnl, 2)} (${pct(p.roe)})</div>
        <div class="small">Nếu đóng ngay, sau phí: ${usd(p.net, 2)}</div></div>
      <div><div class="lbl">TP / SL · R:R 1:${p.rr.toFixed(2)}</div>
        <div class="val"><span class="up">${fmt(p.tp)}</span> / <span class="down">${fmt(p.sl)}</span></div>
        <div class="small"><span class="up">${pct(dir * (p.tp / p.entry - 1))} (+${fmt(p.tpUsd)}$)</span> /
          <span class="down">${pct(dir * (p.sl / p.entry - 1))} (-${fmt(p.slUsd)}$)</span></div></div>
    </div>
    <div class="range" style="--e:${at(p.entry)}%">
      <span class="lab l down">SL ${fmt(p.sl)} · còn ${pct(toSl).replace('+', '')}</span>
      <span class="mk ent" style="left:${at(p.entry)}%" title="Giá vào"></span>
      <span class="mk dot" style="left:${at(p.mark)}%" title="Giá hiện tại ${fmt(p.mark)}"></span>
      <span class="lab r up">TP ${fmt(p.tp)} · còn ${pct(toTp).replace('+', '')}</span>
    </div>`;

  noteBox.hidden = false;
  if (key !== posKey) {
    $('noteBody').innerHTML = notesHtml(p.notes);
    if (!firstPoll)
      toast(`Đã khớp lệnh ${isLong ? 'MUA / LONG' : 'BÁN / SHORT'}`,
        `${p.qty.toFixed(4)} BTC @ ${fmt(p.entry)} · ${p.leverage}x<br>TP ${fmt(p.tp)} · SL ${fmt(p.sl)}`, isLong ? 'long' : 'short');
  }
  posKey = key;
}

// Thông báo khi một lệnh của phiên vừa đóng
let lastClosedNo = null;
function notifyTrades(trades) {
  const closed = trades.filter(t => t.exitTime != null);
  const maxNo = closed.length ? Math.max(...closed.map(t => t.no)) : 0;
  if (lastClosedNo != null && maxNo > lastClosedNo) {
    const t = closed.find(x => x.no === maxNo);
    toast(`Đã đóng lệnh #${t.no} ${t.side} — ${t.outcome}`, `@ ${fmt(t.exit)} · ${usd(t.pnl, 2)}`, t.pnl > 0 ? 'long' : 'short');
  }
  lastClosedNo = maxNo;
}

function toast(title, body, kind) {
  const el = document.createElement('div');
  el.className = 'toast ' + kind;
  el.innerHTML = `<b>${esc(title)}</b><span class="muted num">${body}</span>`;
  $('toasts').appendChild(el);
  setTimeout(() => el.remove(), 10000);
}

async function poll() {
  try {
    const r = await fetch('/api/state', { cache: 'no-store' });
    const st = await r.json();
    if (st.candles && st.candles.length) { lastState = st; render(st); firstPoll = false; }
    const stale = Date.now() - st.updated > 15000;
    $('dot').className = 'live-dot' + (stale ? ' stale' : '');
    $('clock').textContent = stale ? 'Mất kết nối dữ liệu' : new Date().toLocaleTimeString('vi-VN');
  } catch {
    $('dot').className = 'live-dot stale';
    $('clock').textContent = 'App không chạy';
  }
  setTimeout(poll, 1000);
}
poll();
</script>
</body>
</html>
""";
}
