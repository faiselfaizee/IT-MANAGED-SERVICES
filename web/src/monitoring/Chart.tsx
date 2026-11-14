import { useEffect, useMemo, useRef, useState } from 'react';

export type Series = { key: string; label: string; values: (number | null)[]; peaks?: (number | null)[]; cls: string };

const H = 220, L = 40, R = 64, T = 12, B = 26;

/** Breaks a line where readings are missing, so a computer that was off shows a gap rather than a straight line across it. */
function paths(at: number[], vals: (number | null)[], x: (t: number) => number, y: (v: number) => number, gapMs: number): string {
  let d = ''; let prev: number | null = null;
  vals.forEach((v, i) => {
    if (v == null) { prev = null; return; }
    const jump = prev == null || at[i] - at[prev] > gapMs;
    d += `${jump ? 'M' : 'L'}${x(at[i]).toFixed(1)},${y(v).toFixed(1)}`;
    prev = i;
  });
  return d;
}

const fmtTime = (t: number, long: boolean) => { const d = new Date(t); return long ? d.toLocaleDateString(undefined, { day: 'numeric', month: 'short' }) : d.toLocaleTimeString(undefined, { hour: '2-digit', minute: '2-digit' }); };

/**
 * A percentage line chart (0 to 100) over time: thin lines, a light grid, one axis, the latest value written at each line's end,
 * a crosshair with every value at the pointer, and the same numbers as a table for people who do not read charts.
 */
export function LineChart({ title, at, series, from, to, stepMs, unit = '%' }: { title: string; at: number[]; series: Series[]; from: number; to: number; stepMs: number; unit?: string }) {
  const [hover, setHover] = useState<number | null>(null);
  const [table, setTable] = useState(false);
  const svg = useRef<SVGSVGElement>(null);
  // drawn at the width it is shown at, so text stays its normal size on a wide screen
  const box = useRef<HTMLDivElement>(null); const [W, setW] = useState(720);
  useEffect(() => {
    const el = box.current; if (!el || typeof ResizeObserver === 'undefined') return;
    const ro = new ResizeObserver(([e]) => setW(Math.max(360, Math.round(e.contentRect.width))));
    ro.observe(el); return () => ro.disconnect();
  }, [table, at.length]);
  const long = to - from > 36 * 3600_000;
  const x = (t: number) => L + ((t - from) / Math.max(1, to - from)) * (W - L - R);
  const y = (v: number) => T + (1 - Math.max(0, Math.min(100, v)) / 100) * (H - T - B);
  const ticks = useMemo(() => { const n = Math.max(3, Math.min(8, Math.floor(W / 140))); return Array.from({ length: n + 1 }, (_, i) => from + ((to - from) * i) / n); }, [from, to, W]);
  const move = (e: React.PointerEvent) => {
    const box = svg.current?.getBoundingClientRect(); if (!box || at.length === 0) return;
    const t = from + (((e.clientX - box.left) / box.width) * W - L) / (W - L - R) * (to - from);
    let best = 0; for (let i = 1; i < at.length; i++) if (Math.abs(at[i] - t) < Math.abs(at[best] - t)) best = i;
    setHover(best);
  };
  const last = (s: Series) => { for (let i = s.values.length - 1; i >= 0; i--) if (s.values[i] != null) return i; return -1; };
  const summary = `${title}: ` + series.map((s) => { const i = last(s); return i < 0 ? `${s.label} no data` : `${s.label} latest ${s.values[i]!.toFixed(0)}${unit}`; }).join(', ');
  return <figure className="chart">
    <figcaption><span>{title}</span>
      {series.length > 1 && <span className="chart-legend">{series.map((s) => <span key={s.key}><i className={`swatch ${s.cls}`} aria-hidden="true" />{s.label}</span>)}</span>}
      <button className="link" onClick={() => setTable(!table)}>{table ? 'Show chart' : 'Show as table'}</button></figcaption>
    {at.length === 0 ? <p className="muted chart-empty">No readings in this period. Agents 3.0 and later send processor, memory and disk use every minute.</p> : table ?
      <div className="table-wrap chart-table"><table><thead><tr><th>Time</th>{series.map((s) => <th key={s.key}>{s.label}</th>)}</tr></thead>
        <tbody>{at.map((t, i) => <tr key={t}><td>{new Date(t).toLocaleString()}</td>{series.map((s) => <td key={s.key}>{s.values[i] == null ? '–' : s.values[i]!.toFixed(1) + unit}{s.peaks?.[i] != null && s.peaks[i] !== s.values[i] ? ` (peak ${s.peaks[i]!.toFixed(0)}${unit})` : ''}</td>)}</tr>)}</tbody></table></div> :
      <div className="chart-box" ref={box}>
        <svg ref={svg} viewBox={`0 0 ${W} ${H}`} role="img" aria-label={summary} onPointerMove={move} onPointerLeave={() => setHover(null)}>
          {[0, 25, 50, 75, 100].map((v) => <g key={v}><line x1={L} x2={W - R} y1={y(v)} y2={y(v)} className="grid" /><text x={L - 6} y={y(v) + 4} textAnchor="end" className="axis">{v}{unit}</text></g>)}
          {ticks.map((t, i) => <text key={i} x={x(t)} y={H - 6} textAnchor={i === 0 ? 'start' : i === ticks.length - 1 ? 'end' : 'middle'} className="axis">{fmtTime(t, long)}</text>)}
          {series.map((s) => s.peaks && <path key={s.key + 'p'} d={paths(at, s.peaks, x, y, stepMs * 3)} className={`peak ${s.cls}`} />)}
          {series.map((s) => <path key={s.key} d={paths(at, s.values, x, y, stepMs * 3)} className={`line ${s.cls}`} />)}
          {series.map((s) => { const i = last(s); return i < 0 ? null : <g key={s.key + 'l'}><circle cx={x(at[i])} cy={y(s.values[i]!)} r={3.5} className={`end ${s.cls}`} />
            <text x={x(at[i]) + 8} y={y(s.values[i]!) + 4} className="end-label">{s.values[i]!.toFixed(0)}{unit}</text></g>; })}
          {hover != null && <g className="cross"><line x1={x(at[hover])} x2={x(at[hover])} y1={T} y2={H - B} />
            {series.map((s) => s.values[hover] != null && <circle key={s.key} cx={x(at[hover])} cy={y(s.values[hover]!)} r={4} className={`end ${s.cls}`} />)}</g>}
          <rect x={L} y={T} width={W - L - R} height={H - T - B} fill="transparent" />
        </svg>
        {hover != null && <div className="chart-tip" style={{ left: `${(x(at[hover]) / W) * 100}%` }} role="status">
          <b>{new Date(at[hover]).toLocaleString(undefined, { day: 'numeric', month: 'short', hour: '2-digit', minute: '2-digit' })}</b>
          {series.map((s) => <span key={s.key}><i className={`swatch ${s.cls}`} aria-hidden="true" />{s.label}: {s.values[hover] == null ? '–' : s.values[hover]!.toFixed(1) + unit}{s.peaks?.[hover] != null && s.peaks[hover] !== s.values[hover] ? `, peak ${s.peaks[hover]!.toFixed(0)}${unit}` : ''}</span>)}
        </div>}
      </div>}
  </figure>;
}
