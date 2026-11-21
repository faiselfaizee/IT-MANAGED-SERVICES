import { jsx as _jsx, jsxs as _jsxs, Fragment as _Fragment } from "react/jsx-runtime";
import { useState } from 'react';
import { useInterval, useLoad } from '../http';
import { ago, Empty, Loading } from '../ui';
/** Filters that come from a ring part, with the words shown for them. */
export const DEV_FILTERS = {
    status: { ok: 'OK', warning: 'Warning', critical: 'Critical', muted: 'Muted', unwatched: 'Not watched', down: 'Down' },
    disk: { ok: '20% or more disk free', low: '10 to 20% disk free', critical: 'Under 10% disk free', nodata: 'No disk data' },
    check: { ok: 'Checks passing', failing: 'A check failing', notrun: 'A check not run yet' },
    cert: { ok: 'Certificates fine', soon: 'Certificate expires within 30 days', urgent: 'Certificate expires within 7 days' },
    kind: { computer: 'Computers', server: 'Servers', network: 'Network devices', printer: 'Printers', other: 'Other devices' },
    metric: { offline: 'Down alerts', cpu: 'Processor alerts', memory: 'Memory alerts', disk: 'Disk alerts', check: 'Check alerts', cert: 'Certificate alerts', hardware: 'Hardware alerts', traffic: 'Traffic alerts', port: 'Port alerts', service: 'Service alerts', process: 'Program alerts', log: 'Message alerts' },
    port: { down: 'A watched port down', busy: 'A port above 80%' },
    service: { down: 'A service or program stopped' },
};
const TONE = { ok: 'ok', warning: 'warn', critical: 'bad', muted: 'info', unwatched: '' };
const WORD = { ok: 'OK', warning: 'Warning', critical: 'Critical', muted: 'Muted', unwatched: 'Not watched' };
export function StatusBadge({ s }) { return _jsxs("span", { className: `badge ${TONE[s] ?? ''}`, children: [_jsx("i", { className: `dot tone-${TONE[s] || 'none'}`, "aria-hidden": "true" }), " ", WORD[s] ?? s] }); }
/** A small bar for a percentage, with the number beside it (the number carries the meaning, the bar only helps the eye). */
export function Pct({ v, high = 90, invert = false }) {
    if (v == null)
        return _jsx("span", { className: "muted", children: "\u2013" });
    const bad = invert ? v < 100 - high : v >= high;
    return _jsxs("span", { className: "pct", children: [_jsx("span", { className: "pct-bar", "aria-hidden": "true", children: _jsx("i", { className: bad ? 'hot' : '', style: { width: Math.max(0, Math.min(100, v)) + '%' } }) }), v.toFixed(0), "%"] });
}
export default function Devices() {
    const qs = new URLSearchParams(location.hash.split('?')[1] ?? '');
    const company = qs.get('company') ?? '';
    const filters = Object.keys(DEV_FILTERS).map((k) => [k, qs.get(k) ?? '']).filter(([k, v]) => v && DEV_FILTERS[k][v]);
    const [search, setSearch] = useState('');
    const [skip, setSkip] = useState(0);
    const take = 50;
    const co = useLoad(company ? `/api/monitoring/dashboard?company=${encodeURIComponent(company)}` : null);
    const coName = company ? String(co.data?.customer?.company ?? (company === 'none' ? 'No customer' : '…')) : '';
    const q = `/api/monitoring/devices?search=${encodeURIComponent(search)}` + (company ? `&company=${encodeURIComponent(company)}` : '') + filters.map(([k, v]) => `&${k}=${v}`).join('') + `&skip=${skip}&take=${take}`;
    const { data, error, loading, reload } = useLoad(q);
    useInterval(reload, 60_000);
    const items = (data?.items ?? []);
    const total = Number(data?.total ?? 0);
    const without = (key) => { const x = new URLSearchParams(qs); x.delete(key); const t = x.toString(); return '#/monitoring/devices' + (t ? '?' + t : ''); };
    return _jsxs(_Fragment, { children: [_jsxs("nav", { className: "crumbs", "aria-label": "Breadcrumb", children: [_jsx("a", { href: "#/monitoring", children: "Monitoring" }), _jsx("span", { "aria-hidden": "true", children: "\u203A" }), company ? _jsxs(_Fragment, { children: [_jsx("a", { href: `#/monitoring/customer/${company}`, children: coName }), _jsx("span", { "aria-hidden": "true", children: "\u203A" })] }) : null, _jsx("span", { "aria-current": "page", children: filters.length ? filters.map(([k, v]) => DEV_FILTERS[k][v]).join(', ') : 'All devices' })] }), _jsx("h1", { children: company ? coName : 'All devices' }), (company || filters.length > 0) && _jsxs("div", { className: "filter-chips", children: [company && _jsxs("a", { href: without('company'), title: "Show every customer", children: [coName, " \u2715"] }), filters.map(([k, v]) => _jsxs("a", { href: without(k), title: "Remove this filter", children: [DEV_FILTERS[k][v], " \u2715"] }, k))] }), _jsx("div", { className: "tools", children: _jsx("input", { placeholder: "Search name, tag, address or customer", value: search, onChange: (e) => { setSearch(e.target.value); setSkip(0); }, "aria-label": "Search devices" }) }), _jsx(Loading, { loading: loading && !data, error: error }), data && (items.length === 0 ? _jsx(Empty, { children: "No devices match." }) :
                _jsx("div", { className: "table-wrap", children: _jsxs("table", { children: [_jsx("thead", { children: _jsxs("tr", { children: [_jsx("th", { children: "Status" }), _jsx("th", { children: "Device" }), _jsx("th", { children: "Customer" }), _jsx("th", { children: "Processor" }), _jsx("th", { children: "Memory" }), _jsx("th", { children: "Lowest disk free" }), _jsx("th", { children: "Reachable" }), _jsx("th", { children: "Alerts" })] }) }), _jsx("tbody", { children: items.map((d) => _jsxs("tr", { children: [_jsx("td", { children: _jsx(StatusBadge, { s: d.status }) }), _jsxs("td", { children: [_jsx("a", { href: `#/monitoring/device/${d.id}`, children: d.name }), _jsxs("div", { className: "muted small", children: [d.tag, d.ip ? ' · ' + d.ip : ''] })] }), _jsx("td", { children: d.company }), _jsx("td", { children: _jsx(Pct, { v: d.cpu }) }), _jsx("td", { children: _jsx(Pct, { v: d.mem }) }), _jsx("td", { children: d.worstDisk == null ? _jsx("span", { className: "muted", children: "\u2013" }) : _jsx(Pct, { v: d.worstDisk, high: 90, invert: true }) }), _jsx("td", { children: d.pingUp === false ? _jsxs("span", { className: "badge bad", children: ["No answer ", d.downSince ? ago(d.downSince) : ''] }) : d.pingUp ? `${d.rttMs} ms` : d.agent ? (d.agentSeen ? 'Agent ' + ago(d.agentSeen) : 'Agent never reported') : _jsx("span", { className: "muted", children: "\u2013" }) }), _jsx("td", { children: d.alerts > 0 ? _jsx("a", { href: `#/monitoring/device/${d.id}`, children: d.alertsCritical > 0 ? _jsx("span", { className: "badge bad", children: d.alerts }) : _jsx("span", { className: "badge warn", children: d.alerts }) }) : _jsx("span", { className: "muted", children: "0" }) })] }, d.id)) })] }) })), total > take && _jsxs("div", { className: "tools", children: [_jsx("button", { disabled: skip === 0, onClick: () => setSkip(Math.max(0, skip - take)), children: "Previous" }), _jsxs("span", { className: "muted", children: [skip + 1, "\u2013", Math.min(skip + take, total), " of ", total] }), _jsx("button", { disabled: skip + take >= total, onClick: () => setSkip(skip + take), children: "Next" })] })] });
}
