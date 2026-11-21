import { jsx as _jsx, jsxs as _jsxs } from "react/jsx-runtime";
import { useEffect, useRef } from 'react';
/** A record of what is happening now: one line per step, newest at the bottom, kept in view while it grows (unless the reader scrolled up). */
export function LiveLog({ items, running, empty }) {
    const box = useRef(null);
    const stick = useRef(true);
    useEffect(() => { const el = box.current; if (el && stick.current)
        el.scrollTop = el.scrollHeight; }, [items.length, running]);
    return _jsxs("div", { className: "livelog", role: "log", "aria-live": "polite", "aria-label": "Live log", ref: box, onScroll: (e) => { const el = e.currentTarget; stick.current = el.scrollHeight - el.scrollTop - el.clientHeight < 24; }, children: [items.length === 0 && _jsxs("div", { className: "ll muted", children: [_jsx("time", {}), _jsx("span", { children: empty ?? 'Nothing yet.' })] }), items.map((l) => _jsxs("div", { className: 'll ' + String(l.level ?? 'info'), children: [_jsx("time", { children: new Date(l.at).toLocaleTimeString() }), _jsx("span", { children: l.text })] }, l.seq)), running && _jsxs("div", { className: "ll wait", children: [_jsx("time", {}), _jsxs("span", { children: ["working", _jsx("i", { className: "dots", "aria-hidden": "true" })] })] })] });
}
