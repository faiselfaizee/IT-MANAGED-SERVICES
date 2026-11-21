import { jsx as _jsx, jsxs as _jsxs } from "react/jsx-runtime";
import { useEffect, useMemo, useRef, useState } from 'react';
const H = 220, L = 40, R = 64, T = 12, B = 26;
/** Breaks a line where readings are missing, so a computer that was off shows a gap rather than a straight line across it. */
function paths(at, vals, x, y, gapMs) {
    let d = '';
    let prev = null;
    vals.forEach((v, i) => {
        if (v == null) {
            prev = null;
            return;
        }
        const jump = prev == null || at[i] - at[prev] > gapMs;
        d += `${jump ? 'M' : 'L'}${x(at[i]).toFixed(1)},${y(v).toFixed(1)}`;
        prev = i;
    });
    return d;
}
const fmtTime = (t, long) => { const d = new Date(t); return long ? d.toLocaleDateString(undefined, { day: 'numeric', month: 'short' }) : d.toLocaleTimeString(undefined, { hour: '2-digit', minute: '2-digit' }); };
/**
 * A percentage line chart (0 to 100) over time: thin lines, a light grid, one axis, the latest value written at each line's end,
 * a crosshair with every value at the pointer, and the same numbers as a table for people who do not read charts.
 */
export function LineChart({ title, at, series, from, to, stepMs, unit = '%' }) {
    const [hover, setHover] = useState(null);
    const [table, setTable] = useState(false);
    const svg = useRef(null);
    // drawn at the width it is shown at, so text stays its normal size on a wide screen
    const box = useRef(null);
    const [W, setW] = useState(720);
    useEffect(() => {
        const el = box.current;
        if (!el || typeof ResizeObserver === 'undefined')
            return;
        const ro = new ResizeObserver(([e]) => setW(Math.max(360, Math.round(e.contentRect.width))));
        ro.observe(el);
        return () => ro.disconnect();
    }, [table, at.length]);
    const long = to - from > 36 * 3600_000;
    const x = (t) => L + ((t - from) / Math.max(1, to - from)) * (W - L - R);
    const y = (v) => T + (1 - Math.max(0, Math.min(100, v)) / 100) * (H - T - B);
    const ticks = useMemo(() => { const n = Math.max(3, Math.min(8, Math.floor(W / 140))); return Array.from({ length: n + 1 }, (_, i) => from + ((to - from) * i) / n); }, [from, to, W]);
    const move = (e) => {
        const box = svg.current?.getBoundingClientRect();
        if (!box || at.length === 0)
            return;
        const t = from + (((e.clientX - box.left) / box.width) * W - L) / (W - L - R) * (to - from);
        let best = 0;
        for (let i = 1; i < at.length; i++)
            if (Math.abs(at[i] - t) < Math.abs(at[best] - t))
                best = i;
        setHover(best);
    };
    const last = (s) => { for (let i = s.values.length - 1; i >= 0; i--)
        if (s.values[i] != null)
            return i; return -1; };
    const summary = `${title}: ` + series.map((s) => { const i = last(s); return i < 0 ? `${s.label} no data` : `${s.label} latest ${s.values[i].toFixed(0)}${unit}`; }).join(', ');
    return _jsxs("figure", { className: "chart", children: [_jsxs("figcaption", { children: [_jsx("span", { children: title }), series.length > 1 && _jsx("span", { className: "chart-legend", children: series.map((s) => _jsxs("span", { children: [_jsx("i", { className: `swatch ${s.cls}`, "aria-hidden": "true" }), s.label] }, s.key)) }), _jsx("button", { className: "link", onClick: () => setTable(!table), children: table ? 'Show chart' : 'Show as table' })] }), at.length === 0 ? _jsx("p", { className: "muted chart-empty", children: "No readings in this period. Agents 3.0 and later send processor, memory and disk use every minute." }) : table ?
                _jsx("div", { className: "table-wrap chart-table", children: _jsxs("table", { children: [_jsx("thead", { children: _jsxs("tr", { children: [_jsx("th", { children: "Time" }), series.map((s) => _jsx("th", { children: s.label }, s.key))] }) }), _jsx("tbody", { children: at.map((t, i) => _jsxs("tr", { children: [_jsx("td", { children: new Date(t).toLocaleString() }), series.map((s) => _jsxs("td", { children: [s.values[i] == null ? '–' : s.values[i].toFixed(1) + unit, s.peaks?.[i] != null && s.peaks[i] !== s.values[i] ? ` (peak ${s.peaks[i].toFixed(0)}${unit})` : ''] }, s.key))] }, t)) })] }) }) :
                _jsxs("div", { className: "chart-box", ref: box, children: [_jsxs("svg", { ref: svg, viewBox: `0 0 ${W} ${H}`, role: "img", "aria-label": summary, onPointerMove: move, onPointerLeave: () => setHover(null), children: [[0, 25, 50, 75, 100].map((v) => _jsxs("g", { children: [_jsx("line", { x1: L, x2: W - R, y1: y(v), y2: y(v), className: "grid" }), _jsxs("text", { x: L - 6, y: y(v) + 4, textAnchor: "end", className: "axis", children: [v, unit] })] }, v)), ticks.map((t, i) => _jsx("text", { x: x(t), y: H - 6, textAnchor: i === 0 ? 'start' : i === ticks.length - 1 ? 'end' : 'middle', className: "axis", children: fmtTime(t, long) }, i)), series.map((s) => s.peaks && _jsx("path", { d: paths(at, s.peaks, x, y, stepMs * 3), className: `peak ${s.cls}` }, s.key + 'p')), series.map((s) => _jsx("path", { d: paths(at, s.values, x, y, stepMs * 3), className: `line ${s.cls}` }, s.key)), (() => {
                                    const ends = series.map((s) => ({ s, i: last(s) })).filter((e) => e.i >= 0).map((e) => ({ ...e, ly: y(e.s.values[e.i]) + 4 })).sort((p, q) => p.ly - q.ly);
                                    for (let k = 1; k < ends.length; k++)
                                        if (ends[k].ly - ends[k - 1].ly < 13)
                                            ends[k].ly = ends[k - 1].ly + 13;
                                    return ends.map(({ s, i, ly }) => _jsxs("g", { children: [_jsx("circle", { cx: x(at[i]), cy: y(s.values[i]), r: 3.5, className: `end ${s.cls}` }), _jsxs("text", { x: x(at[i]) + 8, y: ly, className: "end-label", children: [s.values[i].toFixed(0), unit] })] }, s.key + 'l'));
                                })(), hover != null && _jsxs("g", { className: "cross", children: [_jsx("line", { x1: x(at[hover]), x2: x(at[hover]), y1: T, y2: H - B }), series.map((s) => s.values[hover] != null && _jsx("circle", { cx: x(at[hover]), cy: y(s.values[hover]), r: 4, className: `end ${s.cls}` }, s.key))] }), _jsx("rect", { x: L, y: T, width: W - L - R, height: H - T - B, fill: "transparent" })] }), hover != null && _jsxs("div", { className: "chart-tip", style: { left: `${(x(at[hover]) / W) * 100}%` }, role: "status", children: [_jsx("b", { children: new Date(at[hover]).toLocaleString(undefined, { day: 'numeric', month: 'short', hour: '2-digit', minute: '2-digit' }) }), series.map((s) => _jsxs("span", { children: [_jsx("i", { className: `swatch ${s.cls}`, "aria-hidden": "true" }), s.label, ": ", s.values[hover] == null ? '–' : s.values[hover].toFixed(1) + unit, s.peaks?.[hover] != null && s.peaks[hover] !== s.values[hover] ? `, peak ${s.peaks[hover].toFixed(0)}${unit}` : ''] }, s.key))] })] })] });
}
