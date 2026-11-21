import { jsx as _jsx, Fragment as _Fragment, jsxs as _jsxs } from "react/jsx-runtime";
import { useEffect, useState } from 'react';
import { api, go, useLoad } from '../http';
import { Badge, Empty, Loading, ago } from '../ui';
import { PriorityBadge, STATUS } from './List';
const TABS = [['open', 'Open'], ['hold', 'Waiting for you'], ['done', 'Finished'], ['all', 'All']];
const STATES = { open: 'open,progress', hold: 'hold', done: 'resolved,closed', all: '' };
/** When the fix is due, said the way a person would say it. */
export function dueWords(t, now = Date.now()) {
    if (t.status === 'resolved' || t.status === 'closed')
        return (t.status === 'closed' ? 'Closed ' : 'Resolved ') + ago(t.resolvedAt ?? t.updatedAt);
    if (t.status === 'hold')
        return 'Waiting for your reply';
    if (!t.resolveDue)
        return '';
    const h = (new Date(t.resolveDue).getTime() - now) / 3600000;
    const span = (x) => x < 1 ? 'less than an hour' : x < 48 ? Math.round(x) + (Math.round(x) === 1 ? ' hour' : ' hours') : Math.round(x / 24) + ' days';
    return h >= 0 ? 'We aim to fix it in ' + span(h) : 'Overdue by ' + span(-h);
}
/** An employee's own tickets: what is open, what is waiting for them, what is finished. */
export default function MyTickets({ me }) {
    const [tab, setTab] = useState('open');
    const [text, setText] = useState('');
    const [q, setQ] = useState('');
    const [err, setErr] = useState('');
    useEffect(() => { const h = setTimeout(() => setQ(text.trim()), 250); return () => clearTimeout(h); }, [text]);
    const sum = useLoad('/api/servicedesk/summary');
    const { data, error, loading, reload } = useLoad(`/api/servicedesk/tickets?status=${STATES[tab]}&q=${encodeURIComponent(q)}&take=100`, [tab, q]);
    const items = (data?.items ?? []);
    const s = sum.data;
    const count = (k) => k === 'open' ? Number(s?.open ?? 0) + Number(s?.progress ?? 0) : k === 'hold' ? Number(s?.hold ?? 0) : k === 'done' ? Number(s?.resolved ?? 0) + Number(s?.closed ?? 0) : null;
    const reopen = async (id) => { setErr(''); try {
        await api(`/api/servicedesk/tickets/${id}/status`, { body: { status: 'open' } });
        reload();
        sum.reload();
    }
    catch (e) {
        setErr(e.message);
    } };
    return _jsxs(_Fragment, { children: [_jsx("h1", { children: "My tickets" }), _jsxs("div", { className: "tools", children: [_jsx("nav", { className: "sub", "aria-label": "Ticket groups", children: TABS.map(([k, l]) => _jsxs("a", { href: "#/servicedesk/all", className: tab === k ? 'on' : '', onClick: (e) => { e.preventDefault(); setTab(k); }, children: [l, count(k) ? _jsxs(_Fragment, { children: [" ", _jsx(Badge, { tone: k === 'hold' ? 'warn' : undefined, children: count(k) })] }) : null] }, k)) }), _jsx("input", { placeholder: "Search number or subject", value: text, onChange: (e) => setText(e.target.value), "aria-label": "Search", style: { minWidth: 220 } }), me?.canRaise && _jsx("button", { className: "primary", onClick: () => go('servicedesk/new'), children: "New ticket" })] }), err && _jsx("p", { className: "err", role: "alert", children: err }), _jsx(Loading, { loading: loading && !data, error: error }), data && (items.length === 0 ? _jsx(Empty, { children: q ? 'No ticket matches.' : tab === 'hold' ? 'Nothing is waiting for you.' : tab === 'done' ? 'No finished tickets yet.' : 'You have no open tickets.' }) : _jsxs("table", { children: [_jsx("thead", { children: _jsxs("tr", { children: [_jsx("th", { children: "Ticket" }), _jsx("th", { children: "Subject" }), _jsx("th", { children: "Status" }), _jsx("th", { children: "When" }), _jsx("th", { children: "Updated" }), _jsx("th", {})] }) }), _jsx("tbody", { children: items.map((t) => _jsxs("tr", { onClick: () => go('servicedesk/' + t.id), style: { cursor: 'pointer' }, children: [_jsx("td", { children: _jsx("a", { href: '#/servicedesk/' + t.id, children: t.number }) }), _jsxs("td", { children: [t.subject, t.asset ? _jsx("div", { className: "muted", children: t.asset }) : null] }), _jsxs("td", { children: [t.status === 'hold' ? _jsx(Badge, { tone: "warn", children: "Waiting for you" }) : _jsx(Badge, { tone: t.status === 'resolved' || t.status === 'closed' ? 'ok' : undefined, children: STATUS[t.status] ?? t.status }), " ", _jsx(PriorityBadge, { p: t.priority })] }), _jsx("td", { className: t.sla === 'breached' ? 'err' : 'muted', children: dueWords(t) }), _jsx("td", { className: "muted", children: ago(t.updatedAt) }), _jsx("td", { children: (t.status === 'resolved' || t.status === 'closed') && me?.canRaise ? _jsx("button", { onClick: (e) => { e.stopPropagation(); void reopen(t.id); }, children: "Reopen" }) : null })] }, t.id)) })] }))] });
}
