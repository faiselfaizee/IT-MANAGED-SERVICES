import { jsx as _jsx, jsxs as _jsxs, Fragment as _Fragment } from "react/jsx-runtime";
import { useState } from 'react';
import { useInterval, useLoad } from '../http';
import { CountUp, MiniRing, Ring } from '../Rings';
import { Empty, Loading } from '../ui';
const n = (v) => Number(v ?? 0);
function parts(m) {
    return [
        { key: 'o', label: 'New', value: n(m.open), tone: 'info', href: '#/servicedesk' },
        { key: 'p', label: 'In progress', value: n(m.progress), tone: 'ok', href: '#/servicedesk' },
        { key: 'h', label: 'Awaiting customer', value: n(m.hold), tone: 'none', href: '#/servicedesk' },
    ];
}
function Tile({ m, index }) {
    const delay = Math.min(index, 12) * 70;
    const all = m.companyId == null && m.company === 'All customers';
    const active = n(m.open) + n(m.progress) + n(m.hold);
    return _jsxs("a", { className: `co-tile${all ? ' all' : ''}${n(m.overdue) > 0 ? ' attn' : n(m.critical) > 0 ? ' warn' : ''}`, href: "#/servicedesk", children: [_jsxs("div", { className: "co-tile-head", children: [_jsx("b", { className: "co-name", children: m.company }), _jsxs("span", { className: "muted", children: [_jsx(CountUp, { value: active, delay: delay }), " open"] })] }), _jsxs("div", { className: "co-tile-body", children: [_jsx(MiniRing, { parts: parts(m), label: `Open tickets of ${m.company}`, delay: delay }), _jsxs("ul", { className: "co-figs", children: [_jsxs("li", { className: n(m.overdue) > 0 ? 'bad' : '', children: [_jsx("i", { className: "dot tone-bad", "aria-hidden": "true" }), _jsx("span", { children: "Overdue" }), _jsx("b", { children: _jsx(CountUp, { value: n(m.overdue), delay: delay }) })] }), _jsxs("li", { className: n(m.critical) > 0 ? 'warn' : '', children: [_jsx("i", { className: "dot tone-warn", "aria-hidden": "true" }), _jsx("span", { children: "Critical" }), _jsx("b", { children: _jsx(CountUp, { value: n(m.critical), delay: delay }) })] }), _jsxs("li", { children: [_jsx("span", { children: "Not assigned" }), _jsx("b", { children: _jsx(CountUp, { value: n(m.unassigned), delay: delay }) })] }), _jsxs("li", { children: [_jsx("i", { className: "dot tone-ok", "aria-hidden": "true" }), _jsx("span", { children: "Solved, last 30 days" }), _jsx("b", { children: _jsx(CountUp, { value: n(m.done), delay: delay }) })] })] })] })] });
}
/** The Service Desk dashboard: rings for the whole desk, then one tile of circles per customer, like Monitoring and Backup. */
export default function Dashboard() {
    const { data, error, loading, reload } = useLoad('/api/servicedesk/dashboard');
    useInterval(reload, 60_000);
    const [q, setQ] = useState('');
    if (loading && !data)
        return _jsx(Loading, { loading: true, error: error });
    if (error)
        return _jsx(Loading, { loading: false, error: error });
    const all = (data?.all ?? {});
    const tiles = (Array.isArray(data?.tiles) ? data.tiles : []);
    const active = n(all.open) + n(all.progress) + n(all.hold);
    const list = tiles.filter((t) => !q || String(t.company).toLowerCase().includes(q.toLowerCase()));
    return _jsxs(_Fragment, { children: [_jsxs("header", { className: "dash-head", children: [_jsx("h1", { children: "Service Desk" }), _jsxs("p", { className: "muted", children: [active, " open tickets \u00B7 ", n(all.overdue), " overdue \u00B7 ", n(all.unassigned), " not assigned \u00B7 ", n(all.done), " solved in the last 30 days"] }), _jsx("a", { className: "button primary", href: "#/servicedesk/all", children: "All tickets" })] }), _jsxs("div", { className: "rings dash-rings", children: [_jsx(Ring, { title: "Open tickets", unit: "open", delay: 0, empty: "No open tickets.", parts: parts(all) }), _jsx(Ring, { title: "Service level", unit: "open", delay: 120, empty: "No open tickets.", parts: [
                            { key: 'in', label: 'On time', value: Math.max(active - n(all.overdue), 0), tone: 'ok' },
                            { key: 'late', label: 'Overdue', value: n(all.overdue), tone: 'bad' }
                        ] }), _jsx(Ring, { title: "Handling", unit: "open", delay: 240, empty: "No open tickets.", parts: [
                            { key: 'a', label: 'Assigned', value: Math.max(active - n(all.unassigned), 0), tone: 'ok' },
                            { key: 'u', label: 'Not assigned', value: n(all.unassigned), tone: 'warn' }
                        ] })] }), tiles.length === 0 ? _jsx(Empty, { children: "No tickets yet." }) : _jsxs(_Fragment, { children: [tiles.length > 6 && _jsx("div", { className: "tools for-customers", children: _jsx("input", { placeholder: "Find a customer", value: q, onChange: (e) => setQ(e.target.value), "aria-label": "Find a customer" }) }), _jsx("div", { className: "co-tiles for-customers", children: list.map((t, i) => _jsx(Tile, { m: t, index: i + 1 }, String(t.companyId ?? 'none'))) })] })] });
}
