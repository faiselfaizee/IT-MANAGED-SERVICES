import { jsx as _jsx, jsxs as _jsxs, Fragment as _Fragment } from "react/jsx-runtime";
import { useState } from 'react';
import { api, useInterval, useLoad } from '../http';
import { Act, Badge, Card, Dl, Empty, Loading, Meter, ago } from '../ui';
const STATUS = {
    idle: ['Ready', 'ok'], printing: ['Printing', 'ok'], warmup: ['Warming up', 'ok'], other: ['Other', undefined], unknown: ['Unknown', undefined],
};
const FLAGS = {
    jammed: 'Paper jam', door_open: 'Door or cover open', no_paper: 'Out of paper', low_paper: 'Paper low', no_toner: 'Out of toner or ink', low_toner: 'Toner or ink low',
    offline: 'Offline', service_requested: 'Service requested', output_full: 'Output tray full', output_near_full: 'Output tray nearly full',
    input_tray_missing: 'Paper tray missing', output_tray_missing: 'Output tray missing', marker_supply_missing: 'Cartridge missing', input_tray_empty: 'A paper tray is empty',
    overdue_maintenance: 'Maintenance overdue',
};
const FATAL = ['jammed', 'door_open', 'no_paper', 'no_toner', 'offline', 'service_requested', 'output_full', 'marker_supply_missing', 'input_tray_missing'];
const INK = { black: '#1f2328', cyan: '#00a6d6', magenta: '#d6247f', yellow: '#e6b800', gray: '#8a8f98', red: '#d33', green: '#2a9d4a', blue: '#2b5fd9', colour: '#7a5af8' };
const KIND = { toner: 'Toner', ink: 'Ink', drum: 'Drum', fuser: 'Fuser', waste: 'Waste container', transfer: 'Transfer unit', maintenance: 'Maintenance', other: 'Supply' };
const num = (n) => n.toLocaleString('en-IN');
function level(s) {
    if (s.pct >= 0)
        return `${Math.round(s.pct)}%${s.waste ? ' full' : ''}`;
    return s.state === 'ok' ? 'Some left (the printer does not give a level)' : 'Not reported';
}
const tone = (state) => (state === 'ok' ? 'ok' : state === 'low' ? 'warn' : state === 'empty' || state === 'full' ? 'bad' : undefined);
const words = (state, waste) => (state === 'ok' ? 'OK' : state === 'low' ? 'Low' : state === 'empty' ? 'Empty' : state === 'full' ? 'Nearly full' : waste ? 'Not reported' : 'Unknown');
function Pages({ p, daily }) {
    const show = (c) => (c ? `${num(c.pages)}${c.partial ? ' or more' : ''}` : '–');
    const max = Math.max(1, ...daily.map((d) => d.pages));
    return _jsxs(Card, { title: "Pages printed", children: [_jsx(Dl, { rows: [['Printer total', p.total != null ? num(p.total) : 'Not reported'], ['This month', show(p.thisMonth)], ['Last month', show(p.lastMonth)], ['Last 7 days', show(p.last7)], ['Last 30 days', show(p.last30)]] }), daily.length > 1 ? _jsx("div", { className: "printer-days", role: "img", "aria-label": `Pages per day, last ${daily.length} days with a reading`, style: { display: 'flex', alignItems: 'flex-end', gap: 2, height: 64, marginTop: 8 }, children: daily.map((d) => _jsx("i", { title: `${d.day}: ${num(d.pages)} pages`, style: { flex: 1, minWidth: 2, height: Math.max(2, (d.pages / max) * 64), background: 'var(--accent, #2b5fd9)', borderRadius: 1 } }, d.day)) }) : _jsx("p", { className: "muted small", children: "The daily chart fills in after a few days of readings. The printer's own counter is read every 15 minutes and kept once a day." }), _jsx("p", { className: "muted small", children: "Monthly figures come from the printer's lifetime counter. A month marked \"or more\" started before the first reading." })] });
}
/** A printer as a controller sees it over SNMP: status, toner and ink, paper, pages, and the two things a technician reaches for (clear the queue, restart). */
export function PrinterPanel({ id, canAct }) {
    const { data, error, loading, reload } = useLoad(`/api/assets/${id}/printer`, [id]);
    const [note, setNote] = useState('');
    useInterval(() => reload(), 60000);
    if (!data)
        return _jsx(Loading, { loading: loading, error: error });
    const r = data.reading;
    const flags = (r?.flags ?? []).filter((f) => FLAGS[f]);
    const st = STATUS[r?.status] ?? STATUS.unknown;
    const act = (what) => async () => { setNote(''); const out = await api(`/api/assets/${id}/printer/${what}`, { method: 'POST', body: {} }); setNote(String(out.message ?? 'Done.')); reload(); };
    return _jsxs("div", { children: [!data.controller && _jsx(Card, { children: _jsx("p", { className: "err", children: "Nothing is read from this printer yet. Open Overview, choose a controller and set Read as \"Printer or other device (SNMP)\". The controller needs an SNMP community saved under Credentials for this customer." }) }), data.controller && data.error && _jsxs("p", { className: "err", role: "alert", children: ["The last read failed: ", data.error] }), !r && data.controller && !data.error && _jsx(Empty, { children: "Waiting for the first reading. The controller reads a printer every 15 minutes, or press Read now on Overview." }), r && _jsxs(_Fragment, { children: [_jsxs(Card, { title: "Status", children: [_jsxs("p", { children: [_jsx(Badge, { tone: st[1], children: st[0] }), " ", flags.map((f) => _jsxs("span", { children: [" ", _jsx(Badge, { tone: FATAL.includes(f) ? 'bad' : 'warn', children: FLAGS[f] })] }, f))] }), r.message && _jsxs("p", { className: "muted", children: ["On the printer's display: ", r.message] }), (r.alerts ?? []).length > 0 && _jsx("ul", { children: r.alerts.map((a, i) => _jsxs("li", { children: [_jsx(Badge, { tone: a.severity === 'critical' ? 'bad' : a.severity === 'warning' ? 'warn' : undefined, children: a.severity }), " ", a.text] }, i)) }), _jsx(Dl, { rows: [['Printer name', r.name], ['Model', r.model], ['Serial number', r.serial], ['Last read', data.at ? ago(data.at) : '']] }), canAct && _jsxs("div", { className: "tools", children: [_jsx(Act, { label: "Clear the printer's queue", confirm: "Delete every job waiting in this printer's own queue? Jobs on a print server are not touched.", run: act('clear-queue') }), _jsx(Act, { label: "Restart the printer", danger: true, confirm: "Restart this printer now? A job being printed is lost.", run: act('restart') })] }), note && _jsx("p", { role: "status", children: note }), canAct && _jsx("p", { className: "muted small", children: "Both run on the site controller inside the customer's network. Clearing uses the printer's IPP service (port 631); restarting needs an SNMP community that may write. Jobs queued on a Windows print server are cleared on the server (Assets \u203A the server \u203A Commands \u203A Clear print queue and restart spooler)." })] }), _jsx(Card, { title: "Toner, ink and parts", children: (r.supplies ?? []).length === 0 ? _jsx(Empty, { children: "The printer does not report its supplies." }) :
                            _jsx("div", { className: "table-wrap", children: _jsxs("table", { children: [_jsx("thead", { children: _jsxs("tr", { children: [_jsx("th", { children: "Supply" }), _jsx("th", { children: "Level" }), _jsx("th", {})] }) }), _jsx("tbody", { children: r.supplies.map((s, i) => _jsxs("tr", { children: [_jsxs("td", { children: [s.color && INK[s.color] && _jsx("span", { "aria-hidden": "true", style: { display: 'inline-block', width: 10, height: 10, borderRadius: 5, background: INK[s.color], marginRight: 6 } }), s.name, " ", _jsx("span", { className: "muted small", children: KIND[s.kind] ?? '' })] }), _jsx("td", { style: { minWidth: 140 }, children: s.pct >= 0 ? _jsxs(_Fragment, { children: [_jsx(Meter, { pct: s.pct }), " ", level(s)] }) : level(s) }), _jsx("td", { children: _jsx(Badge, { tone: tone(s.state), children: words(s.state, !!s.waste) }) })] }, i)) })] }) }) }), (r.trays ?? []).length > 0 && _jsx(Card, { title: "Paper", children: _jsx("div", { className: "table-wrap", children: _jsxs("table", { children: [_jsx("thead", { children: _jsxs("tr", { children: [_jsx("th", { children: "Tray" }), _jsx("th", { children: "Paper" }), _jsx("th", { children: "Level" })] }) }), _jsx("tbody", { children: r.trays.map((t, i) => _jsxs("tr", { children: [_jsx("td", { children: t.name }), _jsx("td", { children: t.media || _jsx("span", { className: "muted", children: "Not reported" }) }), _jsx("td", { children: t.state === 'empty' ? _jsx(Badge, { tone: "bad", children: "Empty" }) : t.pct >= 0 ? _jsxs(_Fragment, { children: [_jsx(Meter, { pct: t.pct }), " ", Math.round(t.pct), "%"] }) : t.state === 'ok' ? _jsx(Badge, { tone: "ok", children: "Has paper" }) : _jsx("span", { className: "muted", children: "Not reported" }) })] }, i)) })] }) }) })] }), data.pages && (data.pages.total != null || r) && _jsx(Pages, { p: data.pages, daily: (data.daily ?? []) })] });
}
