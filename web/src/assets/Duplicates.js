import { jsx as _jsx, jsxs as _jsxs, Fragment as _Fragment } from "react/jsx-runtime";
import { useState } from 'react';
import { ApiError, api, useLoad } from '../http';
import { Badge, Empty, Err, Loading, Modal, fmtDate, useSubmit } from '../ui';
/** What the server said about assets that look like the one being saved: a block (same serial, MAC or host name) or a warning (same IP or name). */
export function matchesOf(e) {
    if (!(e instanceof ApiError) || (e.status !== 409 && e.status !== 403))
        return null;
    const k = e.body?.error === 'duplicate_override_forbidden' ? 'possible_duplicate' : e.body?.error;
    if (k !== 'duplicate' && k !== 'possible_duplicate')
        return null;
    return { kind: k, text: e.message, matches: (e.body?.matches ?? []) };
}
/** Shown in the add and edit forms when the asset may already exist. A warning can be overruled; a block cannot, merge instead. */
export function DuplicateNotice({ info, onAnyway, busy }) {
    const hard = info.kind === 'duplicate';
    const may = useLoad(hard ? null : '/api/assets/duplicates/override');
    return _jsxs("div", { className: hard ? 'note bad' : 'note', role: "alert", children: [_jsx("strong", { children: hard ? 'This asset already exists.' : 'Possible duplicate.' }), _jsx("ul", { children: info.matches.map((m) => _jsxs("li", { children: [_jsxs("a", { href: `#/assets/${m.id}`, target: "_blank", rel: "noreferrer", children: [m.tag, " ", m.name] }), " ", _jsxs("span", { className: "muted", children: ["same ", (m.why ?? []).join(' and ')] })] }, m.id)) }), hard ? _jsx("p", { className: "muted small", children: "A serial number, MAC address or host name that another asset of this customer already has cannot be used twice. Open the existing asset, or merge the two under All assets \u203A Duplicates." })
                : may.data?.allowed ? _jsx("div", { className: "actions", children: _jsx("button", { className: "primary", disabled: busy, onClick: onAnyway, children: "Add anyway" }) })
                    : may.data ? _jsx("p", { className: "muted small", children: "Only an administrator, or someone an administrator allows, can add it anyway. Check the asset above, or ask an administrator." }) : null] });
}
/** Assets of one customer that look like the same device, with a button to merge them into the one that has the agent. */
export default function Duplicates() {
    const { data, error, loading, reload } = useLoad('/api/assets/duplicates');
    const [pick, setPick] = useState(null);
    const me = useLoad('/api/platform/me');
    const groups = (data?.groups ?? []);
    return _jsxs(_Fragment, { children: [_jsx("h1", { children: "Duplicates" }), _jsx("p", { className: "muted", children: "Assets of one customer that share a serial number, MAC address, host name, IP address or name. Merge them to keep one record: the asset with the agent is kept, the other is retired, and its contracts, services and history move over." }), _jsx(Loading, { loading: loading && !data, error: error }), data && (groups.length === 0 ? _jsx(Empty, { children: "No duplicates found." }) : groups.map((g, n) => _jsxs("div", { className: "card", style: { marginBottom: 12 }, children: [_jsxs("h3", { children: [g.company || 'No customer', " ", _jsx("span", { className: "muted small", children: g.why.join(', ') })] }), _jsxs("table", { children: [_jsx("thead", { children: _jsxs("tr", { children: [_jsx("th", { children: "Tag" }), _jsx("th", { children: "Name" }), _jsx("th", { children: "Host" }), _jsx("th", { children: "IP" }), _jsx("th", { children: "Serial" }), _jsx("th", { children: "Agent" }), _jsx("th", { children: "Source" }), _jsx("th", { children: "Last seen" })] }) }), _jsx("tbody", { children: g.items.map((a) => _jsxs("tr", { children: [_jsx("td", { children: _jsx("a", { href: `#/assets/${a.id}`, children: a.tag }) }), _jsx("td", { children: a.name }), _jsx("td", { children: a.hostname }), _jsx("td", { className: "mono", children: a.ip }), _jsx("td", { className: "mono", children: a.serial }), _jsx("td", { children: a.agent ? _jsx(Badge, { tone: "ok", children: "agent" }) : _jsx("span", { className: "muted", children: "none" }) }), _jsx("td", { children: a.source }), _jsx("td", { children: a.lastSeen ? fmtDate(a.lastSeen) : '' })] }, a.id)) })] }), _jsx("div", { className: "actions", children: _jsx("button", { onClick: () => setPick(g), children: "Merge\u2026" }) })] }, n))), me.data?.role === 'admin' && _jsx(WhoMay, {}), pick && _jsx(Merge, { group: pick, onClose: (ch) => { setPick(null); if (ch)
                    reload(); } })] });
}
function Merge({ group, onClose }) {
    const items = group.items;
    const [keep, setKeep] = useState(items[0].id);
    const f = useSubmit(() => onClose(true));
    const others = items.filter((a) => a.id !== keep);
    return _jsx(Modal, { title: "Merge duplicates", onClose: () => onClose(false), children: _jsxs("div", { className: "form", children: [_jsxs("p", { children: ["Choose the asset to keep. ", others.length === 1 ? 'The other one is' : 'The others are', " retired, with a note that it was merged, and everything of ", others.length === 1 ? 'it' : 'them', " moves over."] }), items.map((a) => _jsxs("label", { style: { display: 'block' }, children: [_jsx("input", { type: "radio", name: "keep", checked: keep === a.id, onChange: () => setKeep(a.id) }), " ", _jsx("strong", { children: a.tag }), " ", a.name, " ", a.agent && _jsx(Badge, { tone: "ok", children: "agent" }), " ", _jsxs("span", { className: "muted", children: [a.hostname, " ", a.ip] })] }, a.id)), _jsx(Err, { text: f.err }), _jsxs("div", { className: "actions", children: [_jsx("button", { onClick: () => onClose(false), children: "Cancel" }), _jsxs("button", { className: "primary", disabled: f.busy, onClick: () => f.submit(() => api('/api/assets/merge', { body: { keepId: keep, mergeIds: others.map((a) => a.id) } })), children: ["Merge into ", items.find((a) => a.id === keep)?.tag] })] })] }) });
}
/** Administrators choose who besides them may save a possible duplicate (same IP address or name) with "Add anyway". */
export function WhoMay() {
    const { data, error, reload } = useLoad('/api/assets/settings/duplicate-override');
    const [users, setUsers] = useState(null);
    const [groups, setGroups] = useState(null);
    const f = useSubmit(() => { setUsers(null); setGroups(null); reload(); });
    if (!data)
        return error ? _jsx(Err, { text: error }) : null;
    const u = users ?? data.users;
    const g = groups ?? data.groups;
    const flip = (l, id) => (l.includes(id) ? l.filter((x) => x !== id) : [...l, id]);
    const people = (data.choices?.users ?? []);
    const teams = (data.choices?.groups ?? []);
    return _jsxs("div", { className: "card", style: { marginTop: 16 }, children: [_jsx("h3", { children: "Who may add a possible duplicate anyway" }), _jsx("p", { className: "muted small", children: "A possible duplicate is a new asset with the same IP address or name as another asset of the customer. Administrators can always save it; nobody else can unless chosen here. Same serial number, MAC address or host name is never allowed." }), _jsxs("div", { className: "grid", children: [_jsxs("div", { children: [_jsx("strong", { children: "People" }), people.length === 0 ? _jsx("p", { className: "muted small", children: "No staff members other than administrators." }) : people.map((p) => _jsxs("label", { className: "check", children: [_jsx("input", { type: "checkbox", checked: u.includes(p.id), onChange: () => setUsers(flip(u, p.id)) }), " ", p.name, " ", _jsx("span", { className: "muted small", children: p.email })] }, p.id))] }), _jsxs("div", { children: [_jsx("strong", { children: "Groups" }), teams.length === 0 ? _jsx("p", { className: "muted small", children: "No groups yet (Access \u203A Groups)." }) : teams.map((t) => _jsxs("label", { className: "check", children: [_jsx("input", { type: "checkbox", checked: g.includes(t.id), onChange: () => setGroups(flip(g, t.id)) }), " ", t.name] }, t.id))] })] }), _jsx(Err, { text: f.err }), _jsx("div", { className: "actions", children: _jsx("button", { className: "primary", disabled: f.busy || (users === null && groups === null), onClick: () => f.submit(() => api('/api/assets/settings/duplicate-override', { method: 'PUT', body: { users: u, groups: g } })), children: "Save" }) })] });
}
