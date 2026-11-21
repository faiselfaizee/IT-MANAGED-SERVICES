import { jsx as _jsx, jsxs as _jsxs, Fragment as _Fragment } from "react/jsx-runtime";
import { useEffect, useRef, useState } from 'react';
const R = 42, W = 12, C = 2 * Math.PI * R, GAP = 2;
/** True when the person asked for less motion, or the page cannot animate (tests, old browsers): then everything shows at once. */
export function stillMotion() {
    if (typeof window === 'undefined' || typeof window.matchMedia !== 'function' || typeof window.requestAnimationFrame !== 'function')
        return true;
    return window.matchMedia('(prefers-reduced-motion: reduce)').matches;
}
const ease = (t) => 1 - Math.pow(1 - t, 3);
/**
 * The values to draw right now. On first show they grow from zero, so a ring sweeps round and its numbers count up; when the values
 * change later they move from the old ones to the new ones. delay staggers several rings on one page.
 */
export function useAnimated(values, delay = 0, ms = 900) {
    const [now, setNow] = useState(() => (stillMotion() ? values : values.map(() => 0)));
    const from = useRef(now);
    const key = values.join(',');
    useEffect(() => {
        if (stillMotion()) {
            from.current = values;
            setNow(values);
            return;
        }
        const start = from.current.length === values.length ? from.current : values.map(() => 0);
        let raf = 0;
        let t0 = 0;
        let first = true;
        const step = (ts) => {
            if (first) {
                t0 = ts + (from.current.every((x) => x === 0) ? delay : 0);
                first = false;
            }
            const t = Math.min(1, Math.max(0, (ts - t0) / ms));
            const cur = values.map((v, i) => start[i] + (v - start[i]) * ease(t));
            from.current = cur;
            setNow(cur);
            if (t < 1)
                raf = requestAnimationFrame(step);
            else
                from.current = values;
        };
        raf = requestAnimationFrame(step);
        return () => cancelAnimationFrame(raf);
        // eslint-disable-next-line react-hooks/exhaustive-deps
    }, [key, delay, ms]);
    return now;
}
function arcs(parts, vals, total) {
    let at = 0;
    return parts.map((p, i) => {
        const len = total > 0 ? (vals[i] / total) * C : 0;
        const gap = len > 0 && parts.filter((_, j) => vals[j] > 0).length > 1 ? Math.min(GAP, len / 2) : 0;
        const a = { p, dash: Math.max(len - gap, 0), offset: -at };
        at += len;
        return a;
    }).filter((a) => a.dash > 0.01);
}
/**
 * A ring of a few parts with the total in the middle, and a legend that gives every count as text, so nothing is read from colour alone.
 * Hovering a part or its legend row lifts both; a legend row with an address opens the matching list.
 */
export function Ring({ title, parts, unit, empty, delay = 0 }) {
    const [hot, setHot] = useState(null);
    const total = parts.reduce((s, p) => s + p.value, 0);
    const vals = useAnimated(parts.map((p) => p.value), delay);
    const shownTotal = Math.round(vals.reduce((s, v) => s + v, 0));
    const summary = total === 0 ? `${title}: ${empty ?? 'nothing yet'}` : `${title}: ` + parts.map((p) => `${p.value} ${p.label.toLowerCase()}`).join(', ');
    return _jsxs("figure", { className: "ring", children: [_jsx("figcaption", { children: title }), _jsxs("div", { className: "ring-body", children: [_jsxs("svg", { viewBox: "0 0 110 110", width: "110", height: "110", role: "img", "aria-label": summary, children: [_jsx("circle", { cx: "55", cy: "55", r: R, fill: "none", className: "ring-track", strokeWidth: W }), arcs(parts, vals, total).map(({ p, dash, offset }) => _jsx("circle", { cx: "55", cy: "55", r: R, fill: "none", strokeWidth: hot === p.key ? W + 3 : W, className: `ring-arc tone-${p.tone}${hot && hot !== p.key ? ' dim' : ''}`, strokeDasharray: `${dash} ${C - dash}`, strokeDashoffset: offset, transform: "rotate(-90 55 55)", onMouseEnter: () => setHot(p.key), onMouseLeave: () => setHot(null), children: _jsx("title", { children: `${p.label}: ${p.value} of ${total}` }) }, p.key)), _jsx("text", { x: "55", y: "53", textAnchor: "middle", className: "ring-total", children: shownTotal }), _jsx("text", { x: "55", y: "70", textAnchor: "middle", className: "ring-unit", children: unit ?? '' })] }), _jsx("ul", { className: "ring-legend", children: parts.map((p, i) => {
                            const body = _jsxs(_Fragment, { children: [_jsx("i", { className: `dot tone-${p.tone}`, "aria-hidden": "true" }), _jsx("span", { className: "name", children: p.label }), _jsx("b", { className: "num", children: Math.round(vals[i] ?? p.value) })] });
                            return _jsx("li", { className: hot === p.key ? 'hot' : '', onMouseEnter: () => setHot(p.key), onMouseLeave: () => setHot(null), children: p.href ? _jsx("a", { href: p.href, children: body }) : _jsx("span", { children: body }) }, p.key);
                        }) })] }), total === 0 && empty && _jsx("p", { className: "muted ring-empty", children: empty })] });
}
/** A small ring without a legend, for a customer tile: the number in the middle and the parts named in its label. */
export function MiniRing({ parts, label, delay = 0 }) {
    const total = parts.reduce((s, p) => s + p.value, 0);
    const vals = useAnimated(parts.map((p) => p.value), delay, 800);
    const summary = `${label}: ` + (total === 0 ? 'none' : parts.map((p) => `${p.value} ${p.label.toLowerCase()}`).join(', '));
    return _jsxs("svg", { viewBox: "0 0 110 110", width: "76", height: "76", role: "img", "aria-label": summary, className: "mini-ring", children: [_jsx("circle", { cx: "55", cy: "55", r: R, fill: "none", className: "ring-track", strokeWidth: W + 2 }), arcs(parts, vals, total).map(({ p, dash, offset }) => _jsx("circle", { cx: "55", cy: "55", r: R, fill: "none", strokeWidth: W + 2, className: `ring-arc tone-${p.tone}`, strokeDasharray: `${dash} ${C - dash}`, strokeDashoffset: offset, transform: "rotate(-90 55 55)" }, p.key)), _jsx("text", { x: "55", y: "64", textAnchor: "middle", className: "ring-total mini", children: Math.round(vals.reduce((s, v) => s + v, 0)) })] });
}
/** A number that counts up to its value on first show and moves to a new value when it changes. */
export function CountUp({ value, delay = 0 }) {
    const [v] = useAnimated([value], delay, 800);
    return _jsx(_Fragment, { children: Math.round(v) });
}
