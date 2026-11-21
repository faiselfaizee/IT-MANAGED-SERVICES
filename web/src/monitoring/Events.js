import { jsx as _jsx, jsxs as _jsxs, Fragment as _Fragment } from "react/jsx-runtime";
import { useState } from 'react';
import { useInterval, useLoad } from '../http';
import { ago, Empty, fmtDate, Loading } from '../ui';
export const SEVERITIES = ['Emergency', 'Alert', 'Critical', 'Error', 'Warning', 'Notice', 'Information', 'Debug'];
const KIND = { syslog: 'Syslog', trap: 'SNMP trap', eventlog: 'Windows event' };
const tone = (s) => (s <= 2 ? 'bad' : s === 3 ? 'bad' : s === 4 ? 'warn' : '');
/** A list of device messages, newest first. With asset set it shows one device's (on the device page). */
export function EventList({ asset, company, kind = '', severity = 7, search = '', take = 50, showDevice = true }) {
    const [skip, setSkip] = useState(0);
    const q = `/api/monitoring/events?kind=${kind}&severity=${severity}&search=${encodeURIComponent(search)}` + (asset ? `&asset=${asset}` : '') + (company ? `&company=${encodeURIComponent(company)}` : '') + `&skip=${skip}&take=${take}`;
    const { data, error, loading, reload } = useLoad(q);
    useInterval(reload, 60_000);
    const items = (data?.items ?? []);
    const total = Number(data?.total ?? 0);
    return _jsxs(_Fragment, { children: [_jsx(Loading, { loading: loading && !data, error: error }), data && (items.length === 0 ? _jsxs(Empty, { children: ["No messages", severity < 7 ? ' at this level or worse' : '', ". Devices send syslog and SNMP traps to the site controller (UDP 514 and 162); agents send Windows event log errors when a rule asks for them."] }) :
                _jsx("div", { className: "table-wrap", children: _jsxs("table", { children: [_jsx("thead", { children: _jsxs("tr", { children: [_jsx("th", { children: "When" }), _jsx("th", { children: "Level" }), showDevice && _jsx("th", { children: "Device" }), _jsx("th", { children: "From" }), _jsx("th", { children: "Message" })] }) }), _jsx("tbody", { children: items.map((e) => _jsxs("tr", { children: [_jsx("td", { title: fmtDate(e.at), children: ago(e.at) }), _jsx("td", { children: _jsx("span", { className: `badge ${tone(e.severity)}`, children: SEVERITIES[e.severity] ?? e.severity }) }), showDevice && _jsxs("td", { children: [_jsx("a", { href: `#/monitoring/device/${e.assetId}`, children: e.asset }), _jsx("div", { className: "muted small", children: e.company })] }), _jsxs("td", { children: [KIND[e.kind] ?? e.kind, _jsxs("div", { className: "muted small", children: [e.source, e.eventId ? ` · event ${e.eventId}` : ''] })] }), _jsx("td", { className: "msg", children: e.text })] }, e.id)) })] }) })), total > take && _jsxs("div", { className: "tools", children: [_jsx("button", { disabled: skip === 0, onClick: () => setSkip(Math.max(0, skip - take)), children: "Newer" }), _jsxs("span", { className: "muted", children: [skip + 1, "\u2013", Math.min(skip + take, total), " of ", total] }), _jsx("button", { disabled: skip + take >= total, onClick: () => setSkip(skip + take), children: "Older" })] })] });
}
/** Syslog messages, SNMP traps and Windows event log errors from every device. */
export default function Events() {
    const qs = new URLSearchParams(location.hash.split('?')[1] ?? '');
    const [kind, setKind] = useState(qs.get('kind') ?? '');
    const [severity, setSeverity] = useState(Number(qs.get('severity') ?? 4));
    const [search, setSearch] = useState('');
    return _jsxs(_Fragment, { children: [_jsx("h1", { children: "Device messages" }), _jsx("p", { className: "muted", children: "Kept 14 days. Alert rules of the kind \"Device messages\" turn the ones that matter into alerts." }), _jsxs("div", { className: "tools", children: [_jsxs("select", { value: kind, onChange: (e) => setKind(e.target.value), "aria-label": "Kind", children: [_jsx("option", { value: "", children: "Every kind" }), _jsx("option", { value: "syslog", children: "Syslog" }), _jsx("option", { value: "trap", children: "SNMP traps" }), _jsx("option", { value: "eventlog", children: "Windows events" })] }), _jsx("select", { value: severity, onChange: (e) => setSeverity(Number(e.target.value)), "aria-label": "Level", children: SEVERITIES.map((s, i) => _jsx("option", { value: i, children: i === 7 ? 'Every level' : `${s} or worse` }, s)) }), _jsx("input", { placeholder: "Search text, program or device", value: search, onChange: (e) => setSearch(e.target.value), "aria-label": "Search messages" })] }), _jsx(EventList, { kind: kind, severity: severity, search: search, company: qs.get('company') ?? undefined }, `${kind}|${severity}|${search}`)] });
}
