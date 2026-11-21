import { jsx as _jsx, Fragment as _Fragment, jsxs as _jsxs } from "react/jsx-runtime";
import { useState } from 'react';
import { go, useLoad } from '../http';
import { Badge, Empty, Loading, ago } from '../ui';
import { Ring } from '../Rings';
export const STATUS = { open: 'Open', progress: 'In progress', hold: 'Awaiting customer', resolved: 'Resolved', closed: 'Closed' };
export const KIND = { incident: 'Incident', request: 'Service request' };
export const LEVEL = { low: 'Low', medium: 'Medium', high: 'High' };
export const PRIORITY = { critical: 'Critical', high: 'High', medium: 'Medium', low: 'Low' };
const SLA = {
    ok: ['On track', 'ok'], warning: ['Due soon', 'warn'], breached: ['Overdue', 'bad'], paused: ['Paused', undefined], met: ['Met', 'ok'],
};
export const StatusBadge = ({ s }) => _jsx(Badge, { tone: s === 'resolved' || s === 'closed' ? 'ok' : s === 'hold' ? 'warn' : undefined, children: STATUS[s] ?? s });
export const PriorityBadge = ({ p }) => _jsx(Badge, { tone: p === 'critical' ? 'bad' : p === 'high' ? 'warn' : undefined, children: PRIORITY[p] ?? p });
export const KindBadge = ({ k, major }) => _jsxs(_Fragment, { children: [major && _jsx(Badge, { tone: "bad", children: "Major incident" }), _jsx(Badge, { children: KIND[k] ?? k })] });
export const SlaBadge = ({ s }) => { const [t, tone] = SLA[s] ?? [s, undefined]; return _jsx(Badge, { tone: tone, children: t }); };
/** The ticket list; kind narrows it to incidents or to service requests, major to the major incidents. */
export default function List({ me, kind = '', major = false }) {
    const staff = !!me?.staff;
    const [status, setStatus] = useState('active');
    const [priority, setPriority] = useState('');
    const [assignee, setAssignee] = useState('');
    const [text, setText] = useState('');
    const [overdue, setOverdue] = useState(false);
    const [skip, setSkip] = useState(0);
    const take = 25;
    const qs = `status=${status === 'all' ? '' : status}&priority=${priority}&assignee=${assignee}&q=${encodeURIComponent(text)}${overdue ? '&overdue=1' : ''}${kind ? '&kind=' + kind : ''}${major ? '&major=1' : ''}&skip=${skip}&take=${take}`;
    const { data, error, loading } = useLoad('/api/servicedesk/tickets?' + qs);
    const sum = useLoad('/api/servicedesk/summary' + (major ? '?major=1' : kind ? '?kind=' + kind : ''));
    const items = (data?.items ?? []);
    const total = (data?.total ?? 0);
    const s = sum.data;
    const reset = (f) => { f(); setSkip(0); };
    return _jsxs(_Fragment, { children: [_jsx("h1", { children: major ? 'Major incidents' : kind === 'incident' ? 'Incidents' : kind === 'request' ? 'Service requests' : 'Tickets' }), s && _jsxs("div", { className: "rings dash-rings", children: [_jsx(Ring, { title: "Open", unit: "open", empty: "Nothing open.", parts: [
                            { key: 'o', label: 'New', value: Number(s.open ?? 0), tone: 'info' },
                            { key: 'p', label: 'In progress', value: Number(s.progress ?? 0), tone: 'ok' },
                            { key: 'h', label: 'Awaiting customer', value: Number(s.hold ?? 0), tone: 'none' }
                        ] }), _jsx(Ring, { title: "Service level", unit: "open", empty: "Nothing open.", parts: [
                            { key: 'in', label: 'On time', value: Math.max(Number(s.open ?? 0) + Number(s.progress ?? 0) + Number(s.hold ?? 0) - Number(s.overdue ?? 0), 0), tone: 'ok' },
                            { key: 'late', label: 'Overdue', value: Number(s.overdue ?? 0), tone: 'bad' }
                        ] }), _jsx(Ring, { title: "Finished", unit: "tickets", empty: "None finished yet.", parts: [
                            { key: 'r', label: 'Resolved', value: Number(s.resolved ?? 0), tone: 'ok' },
                            { key: 'c', label: 'Closed', value: Number(s.closed ?? 0), tone: 'none' }
                        ] })] }), s && _jsxs("dl", { className: "stats", style: { marginBottom: 14 }, children: [_jsxs("div", { children: [_jsx("dt", { children: "Open" }), _jsx("dd", { children: (s.open ?? 0) + (s.progress ?? 0) })] }), _jsxs("div", { children: [_jsx("dt", { children: "Awaiting customer" }), _jsx("dd", { children: s.hold ?? 0 })] }), staff && _jsxs("div", { children: [_jsx("dt", { children: "Unassigned" }), _jsx("dd", { children: s.unassigned ?? 0 })] }), staff && _jsxs("div", { children: [_jsx("dt", { children: "Assigned to me" }), _jsx("dd", { children: s.mine ?? 0 })] }), _jsxs("div", { children: [_jsx("dt", { children: "Overdue" }), _jsx("dd", { children: s.overdue ?? 0 })] })] }), _jsxs("div", { className: "tools", children: [_jsx("input", { placeholder: "Search number, subject, person, company\u2026", value: text, onChange: (e) => reset(() => setText(e.target.value)), style: { minWidth: 260 }, "aria-label": "Search" }), _jsxs("select", { value: status, onChange: (e) => reset(() => setStatus(e.target.value)), "aria-label": "Status", children: [_jsx("option", { value: "active", children: "Not finished" }), _jsx("option", { value: "all", children: "Any status" }), Object.entries(STATUS).map(([k, v]) => _jsx("option", { value: k, children: v }, k))] }), _jsxs("select", { value: priority, onChange: (e) => reset(() => setPriority(e.target.value)), "aria-label": "Priority", children: [_jsx("option", { value: "", children: "Any priority" }), Object.entries(PRIORITY).map(([k, v]) => _jsx("option", { value: k, children: v }, k))] }), staff && _jsxs("select", { value: assignee, onChange: (e) => reset(() => setAssignee(e.target.value)), "aria-label": "Assigned to", children: [_jsx("option", { value: "", children: "Anyone" }), _jsx("option", { value: "me", children: "Me" }), _jsx("option", { value: "none", children: "Unassigned" })] }), _jsxs("label", { children: [_jsx("input", { type: "checkbox", checked: overdue, onChange: (e) => reset(() => setOverdue(e.target.checked)) }), " Overdue only"] }), me?.canRaise && _jsx("button", { className: "primary", onClick: () => go('servicedesk/new' + (kind ? '?kind=' + kind : '')), children: kind === 'request' ? 'New service request' : kind === 'incident' || major ? 'New incident' : 'New ticket' })] }), _jsx(Loading, { loading: loading && !data, error: error }), data && (items.length === 0 ? _jsx(Empty, { children: "No tickets match." }) : _jsxs("table", { children: [_jsx("thead", { children: _jsxs("tr", { children: [_jsx("th", { children: "Ticket" }), _jsx("th", { children: "Subject" }), !kind && _jsx("th", { children: "Type" }), _jsx("th", { children: "Priority" }), _jsx("th", { children: "Status" }), staff && _jsx("th", { children: "Assigned to" }), _jsx("th", { children: "Service level" }), _jsx("th", { children: "Updated" })] }) }), _jsx("tbody", { children: items.map((t) => _jsxs("tr", { onClick: () => go('servicedesk/' + t.id), style: { cursor: 'pointer' }, children: [_jsx("td", { children: _jsx("a", { href: '#/servicedesk/' + t.id, children: t.number }) }), _jsxs("td", { children: [t.subject, _jsxs("div", { className: "muted", children: [_jsx("span", { className: "for-customers", children: t.company ? t.company + ' · ' : '' }), t.requester] })] }), !kind && _jsx("td", { children: _jsx(KindBadge, { k: t.kind, major: t.major }) }), _jsxs("td", { children: [kind && t.major && _jsx(Badge, { tone: "bad", children: "Major" }), " ", _jsx(PriorityBadge, { p: t.priority })] }), _jsx("td", { children: _jsx(StatusBadge, { s: t.status }) }), staff && _jsx("td", { children: t.assignee || _jsx("span", { className: "muted", children: "Unassigned" }) }), _jsx("td", { children: _jsx(SlaBadge, { s: t.sla }) }), _jsx("td", { className: "muted", children: ago(t.updatedAt) })] }, t.id)) })] })), total > take && _jsxs("div", { className: "tools", children: [_jsx("button", { disabled: skip === 0, onClick: () => setSkip(Math.max(0, skip - take)), children: "Previous" }), _jsxs("span", { className: "muted", children: [skip + 1, "\u2013", Math.min(skip + take, total), " of ", total] }), _jsx("button", { disabled: skip + take >= total, onClick: () => setSkip(skip + take), children: "Next" })] })] });
}
