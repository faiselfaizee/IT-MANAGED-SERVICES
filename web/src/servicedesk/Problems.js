import { jsx as _jsx, jsxs as _jsxs, Fragment as _Fragment } from "react/jsx-runtime";
import { useState } from 'react';
import { api, go, useLoad } from '../http';
import { Act, Badge, Card, Empty, Err, Field, Loading, fmtDate, useSubmit } from '../ui';
import { PRIORITY, PriorityBadge } from './List';
import { ProblemExtra } from './Itil';
export const PROBLEM_STATUS = { open: 'Open', investigating: 'Investigating', known_error: 'Known error', resolved: 'Resolved', closed: 'Closed' };
export const ProblemBadge = ({ s }) => _jsx(Badge, { tone: s === 'resolved' || s === 'closed' ? 'ok' : s === 'known_error' ? 'warn' : undefined, children: PROBLEM_STATUS[s] ?? s });
const idsFrom = (t) => t.split(/[\s,]+/).map((x) => Number(x.replace(/\D/g, ''))).filter((n) => n > 0);
export function ProblemList({ staff }) {
    const [status, setStatus] = useState('open');
    const [text, setText] = useState('');
    const { data, error, loading } = useLoad(`/api/servicedesk/problems?status=${status === 'all' ? '' : status}&q=${encodeURIComponent(text)}`);
    const sug = useLoad(staff ? '/api/servicedesk/problems/suggestions' : null);
    const items = (data?.items ?? []);
    const hints = (sug.data?.items ?? []);
    const raise = async (h) => { const r = await api('/api/servicedesk/problems', { body: { title: `Repeated ${h.category} tickets`, category: h.category, tickets: h.tickets } }); go('servicedesk/problems/' + r.id); };
    return _jsxs(_Fragment, { children: [_jsx("h1", { children: "Problems" }), hints.length > 0 && _jsx(Card, { title: "Worth investigating", children: hints.map((h) => _jsxs("p", { children: [h.count, " tickets in the last 30 days are about ", _jsx("strong", { children: h.category }), " and not tied to a problem. ", _jsx(Act, { label: "Raise a problem", run: () => raise(h) })] }, h.category)) }), _jsxs("div", { className: "tools", children: [_jsx("input", { placeholder: "Search number, title, category\u2026", value: text, onChange: (e) => setText(e.target.value), style: { minWidth: 240 }, "aria-label": "Search" }), _jsxs("select", { value: status, onChange: (e) => setStatus(e.target.value), "aria-label": "Status", children: [_jsx("option", { value: "open", children: "Not finished" }), _jsx("option", { value: "all", children: "Any status" }), Object.entries(PROBLEM_STATUS).map(([k, v]) => _jsx("option", { value: k, children: v }, k))] }), staff && _jsx("button", { className: "primary", onClick: () => go('servicedesk/problems/new'), children: "New problem" })] }), _jsx(Loading, { loading: loading && !data, error: error }), data && (items.length === 0 ? _jsx(Empty, { children: "No problems match." }) : _jsxs("table", { children: [_jsx("thead", { children: _jsxs("tr", { children: [_jsx("th", { children: "Problem" }), _jsx("th", { children: "Title" }), _jsx("th", { children: "Priority" }), _jsx("th", { children: "Status" }), _jsx("th", { children: "Tickets" }), _jsx("th", { children: "Updated" })] }) }), _jsx("tbody", { children: items.map(({ problem: p, tickets }) => _jsxs("tr", { onClick: () => go('servicedesk/problems/' + p.id), style: { cursor: 'pointer' }, children: [_jsx("td", { children: _jsx("a", { href: '#/servicedesk/problems/' + p.id, children: p.number }) }), _jsxs("td", { children: [p.title, _jsx("div", { className: "muted", children: [p.category, p.ownerName].filter(Boolean).join(' · ') })] }), _jsx("td", { children: _jsx(PriorityBadge, { p: p.priority }) }), _jsx("td", { children: _jsx(ProblemBadge, { s: p.status }) }), _jsx("td", { children: tickets }), _jsx("td", { className: "muted", children: fmtDate(p.updatedAt) })] }, p.id)) })] }))] });
}
export function NewProblem() {
    const settings = useLoad('/api/servicedesk/settings');
    const [v, setV] = useState({ title: '', description: '', priority: 'medium', category: '', tickets: '' });
    const set = (k, x) => setV({ ...v, [k]: x });
    const f = useSubmit(() => undefined);
    const cats = (settings.data?.categories ?? []);
    return _jsxs(_Fragment, { children: [_jsx("h1", { children: "New problem" }), _jsx(Card, { children: _jsxs("div", { className: "form", children: [_jsx(Field, { label: "Title", children: _jsx("input", { value: v.title, onChange: (e) => set('title', e.target.value), maxLength: 200, autoFocus: true }) }), _jsx(Field, { label: "What keeps going wrong?", children: _jsx("textarea", { rows: 4, value: v.description, onChange: (e) => set('description', e.target.value) }) }), _jsxs("div", { className: "row", children: [_jsx(Field, { label: "Priority", children: _jsx("select", { value: v.priority, onChange: (e) => set('priority', e.target.value), children: Object.entries(PRIORITY).map(([k, n]) => _jsx("option", { value: k, children: n }, k)) }) }), _jsx(Field, { label: "Category", children: _jsxs("select", { value: v.category, onChange: (e) => set('category', e.target.value), children: [_jsx("option", { value: "", children: "None" }), cats.map((c) => _jsx("option", { children: c }, c))] }) })] }), _jsx(Field, { label: "Related tickets (ids, separated by commas)", children: _jsx("input", { value: v.tickets, onChange: (e) => set('tickets', e.target.value) }) }), _jsx(Err, { text: f.err }), _jsxs("div", { className: "actions", children: [_jsx("button", { onClick: () => go('servicedesk/problems'), children: "Cancel" }), _jsx("button", { className: "primary", disabled: f.busy || !v.title.trim(), onClick: () => f.submit(async () => {
                                        const r = await api('/api/servicedesk/problems', { body: { title: v.title, description: v.description, priority: v.priority, category: v.category, tickets: idsFrom(v.tickets) } });
                                        go('servicedesk/problems/' + r.id);
                                    }), children: "Raise problem" })] })] }) })] });
}
export function ProblemDetail({ id, staff }) {
    const { data, error, loading, reload } = useLoad('/api/servicedesk/problems/' + id);
    const [edit, setEdit] = useState(null);
    const [note, setNote] = useState('');
    const [msg, setMsg] = useState('');
    const [links, setLinks] = useState(null);
    if (!data)
        return _jsx(Loading, { loading: loading, error: error });
    const p = data.problem;
    const notes = (data.notes ?? []);
    const tickets = (data.tickets ?? []);
    const changes = (data.changes ?? []);
    const patch = async (body) => { setMsg(''); try {
        await api('/api/servicedesk/problems/' + id, { method: 'PATCH', body });
        reload();
        return true;
    }
    catch (e) {
        setMsg(e.message);
        return false;
    } };
    const e = edit ?? { rootCause: p.rootCause, workaround: p.workaround, resolution: p.resolution };
    return _jsxs(_Fragment, { children: [_jsx("p", { children: _jsx("a", { href: "#/servicedesk/problems", children: "\u2190 All problems" }) }), _jsxs("h1", { children: [p.number, " \u00B7 ", p.title, " ", _jsx(ProblemBadge, { s: p.status }), " ", _jsx(PriorityBadge, { p: p.priority })] }), _jsx("p", { className: "muted", children: [p.category, p.ownerName && 'Owner: ' + p.ownerName].filter(Boolean).join(' · ') }), _jsx(Err, { text: msg }), p.description && _jsx(Card, { title: "What keeps going wrong", children: _jsx("p", { children: p.description }) }), _jsx(ProblemExtra, { id: id, staff: staff, status: String(p.status), open: tickets.filter((t) => !['resolved', 'closed'].includes(String(t.status))).length }), staff && _jsx("div", { className: "actions", style: { justifyContent: 'flex-start', margin: '12px 0' }, children: Object.entries(PROBLEM_STATUS).map(([k, n]) => _jsx(Act, { label: n, primary: k === p.status, disabled: k === p.status, run: async () => { if (edit)
                        await patch({ ...edit }); await patch({ status: k }); setEdit(null); } }, k)) }), _jsx(Card, { title: "Findings", children: staff ? _jsxs("div", { className: "form", children: [_jsx(Field, { label: "Root cause", children: _jsx("textarea", { rows: 3, value: e.rootCause, onChange: (x) => setEdit({ ...e, rootCause: x.target.value }) }) }), _jsx(Field, { label: "Workaround (what people can do meanwhile)", children: _jsx("textarea", { rows: 3, value: e.workaround, onChange: (x) => setEdit({ ...e, workaround: x.target.value }) }) }), _jsx(Field, { label: "Resolution", children: _jsx("textarea", { rows: 3, value: e.resolution, onChange: (x) => setEdit({ ...e, resolution: x.target.value }) }) }), _jsx("div", { className: "actions", children: _jsx("button", { className: "primary", disabled: !edit, onClick: async () => { if (await patch({ ...edit }))
                                    setEdit(null); }, children: "Save findings" }) })] }) : _jsxs(_Fragment, { children: [_jsxs("p", { children: [_jsx("strong", { children: "Root cause:" }), " ", p.rootCause || '–'] }), _jsxs("p", { children: [_jsx("strong", { children: "Workaround:" }), " ", p.workaround || '–'] }), _jsxs("p", { children: [_jsx("strong", { children: "Resolution:" }), " ", p.resolution || '–'] })] }) }), _jsxs(Card, { title: "Linked", children: [_jsxs("p", { children: ["Tickets: ", tickets.length === 0 ? _jsx("span", { className: "muted", children: "none" }) : tickets.map((t) => _jsx("a", { href: '#/servicedesk/' + t.id, style: { marginRight: 10 }, children: t.number }, t.id))] }), _jsxs("p", { children: ["Changes: ", changes.length === 0 ? _jsx("span", { className: "muted", children: "none" }) : changes.map((c) => _jsx("a", { href: '#/servicedesk/changes/' + c.id, style: { marginRight: 10 }, children: c.number }, c.id))] }), staff && (links === null ? _jsx("button", { onClick: () => setLinks(tickets.map((t) => t.id).join(', ')), children: "Change linked tickets" })
                        : _jsxs("div", { className: "row", children: [_jsx(Field, { label: "Ticket ids", children: _jsx("input", { value: links, onChange: (x) => setLinks(x.target.value) }) }), _jsx("button", { className: "primary", onClick: async () => { if (await patch({ tickets: idsFrom(links) }))
                                        setLinks(null); }, children: "Save links" })] }))] }), _jsxs(Card, { title: "Notes", children: [notes.map((n) => _jsxs("p", { children: [_jsx("strong", { children: n.author }), " ", _jsx("span", { className: "muted", children: fmtDate(n.at) }), _jsx("br", {}), n.body] }, n.id)), staff && _jsxs("div", { className: "form", children: [_jsx(Field, { label: "Add a note", children: _jsx("textarea", { rows: 3, value: note, onChange: (x) => setNote(x.target.value) }) }), _jsx("div", { className: "actions", children: _jsx(Act, { label: "Add note", primary: true, disabled: !note.trim(), run: async () => { await api(`/api/servicedesk/problems/${id}/notes`, { body: { body: note } }); setNote(''); reload(); } }) })] })] })] });
}
